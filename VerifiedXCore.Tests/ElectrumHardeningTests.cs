using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using NBitcoin;
using VerifiedXCore.Bitcoin.ElectrumX;
using VerifiedXCore.Bitcoin.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Tester report MTI#11: an Electrum server that never answers hung every on-demand Bitcoin call routed to it (no
    /// connect timeout, so its FailCount never rose and it kept being picked), and a failed balance lookup read as a
    /// zero balance. Covers the client's bounds and "answered" flags, the server pool, the verified deposit lookup,
    /// and the owner-deposit check's behavior per validation mode.
    /// </summary>
    [Collection("DbContextSequential")]
    public class ElectrumHardeningTests : IDisposable
    {
        private readonly List<ClientSettings>? _priorServers = Globals.ClientSettings;
        private readonly string _priorValidatorAddress = Globals.ValidatorAddress;
        private readonly bool _priorIsBlockCaster = Globals.IsBlockCaster;

        public void Dispose()
        {
            Globals.ClientSettings = _priorServers;
            Globals.ValidatorAddress = _priorValidatorAddress;
            Globals.IsBlockCaster = _priorIsBlockCaster;
            DepositBalanceLookup.ClearCache();
        }

        private static ClientSettings Server(string host) => new() { Host = host, Port = 50002, UseSsl = true };

        private static IDestination SomeAddress() => new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.Main);

        // ---------------------------------------------------------------- client

        [Fact]
        public async Task Client_TlsHandshakeThatNeverCompletes_GivesUpAtTheConnectBound()
        {
            // The kernel completes the TCP connect from the listen backlog; nothing ever answers the TLS hello.
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var client = new Client("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, true) { ConnectTimeout = TimeSpan.FromSeconds(1) };
                var sw = Stopwatch.StartNew();
                var balance = await client.GetBalance(SomeAddress());

                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
                Assert.False(client.LastCallAnswered);
                Assert.False(client.LastCallSucceeded);
                Assert.Equal(0M, balance.Confirmed); // callers must check LastCallSucceeded, not the zero
            }
            finally { listener.Stop(); }
        }

        [Fact]
        public async Task Client_ServerThatAcceptsButNeverAnswers_GivesUpAtTheRequestBound()
        {
            var (listener, port, served) = Serve(reply: null);
            try
            {
                var client = new Client("127.0.0.1", port, false) { RequestTimeout = TimeSpan.FromSeconds(1) };
                var sw = Stopwatch.StartNew();
                await client.GetBalance(SomeAddress());

                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
                Assert.False(client.LastCallAnswered);
                Assert.Equal("timed out", client.LastFailure);
            }
            finally { listener.Stop(); }
            await served;
        }

        [Fact]
        public async Task Client_Answer_Succeeds()
        {
            var (listener, port, served) = Serve("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"confirmed\":150000000,\"unconfirmed\":0}}\n");
            try
            {
                var client = new Client("127.0.0.1", port, false);
                var balance = await client.GetBalance(SomeAddress());

                Assert.True(client.LastCallSucceeded);
                Assert.Equal(150000000M, balance.Confirmed);
            }
            finally { listener.Stop(); }
            await served;
        }

        [Fact]
        public async Task Client_JsonRpcError_IsAnsweredButNotSucceeded()
        {
            // A server saying "unknown transaction" is up: health must not treat it like a dead one.
            var (listener, port, served) = Serve("{\"jsonrpc\":\"2.0\",\"id\":1,\"error\":{\"code\":2,\"message\":\"no such transaction\"}}\n");
            try
            {
                var client = new Client("127.0.0.1", port, false);
                var confirms = await client.GetConfirms(new string('a', 64));

                Assert.Equal(-1, confirms);
                Assert.True(client.LastCallAnswered);
                Assert.False(client.LastCallSucceeded);
            }
            finally { listener.Stop(); }
            await served;
        }

        /// <summary>A one-connection plain-TCP server: reads one request line, then sends <paramref name="reply"/> (or nothing).</summary>
        private static (TcpListener Listener, int Port, Task Served) Serve(string? reply)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var served = Task.Run(async () =>
            {
                try
                {
                    using var socket = await listener.AcceptTcpClientAsync();
                    var stream = socket.GetStream();
                    var reader = new StreamReader(stream, Encoding.UTF8);
                    await reader.ReadLineAsync();
                    if (reply != null)
                    {
                        var bytes = Encoding.UTF8.GetBytes(reply);
                        await stream.WriteAsync(bytes, 0, bytes.Length);
                    }
                    await Task.Delay(2000);
                }
                catch { /* listener stopped */ }
            });
            return (listener, ((IPEndPoint)listener.LocalEndpoint).Port, served);
        }

        // ---------------------------------------------------------------- pool

        [Fact]
        public void Pool_PrefersTheFastestHealthyServer_AndKeepsConfiguredOrderUntilMeasured()
        {
            var a = Server("a"); var b = Server("b"); var c = Server("c");
            Globals.ClientSettings = new List<ClientSettings> { a, b, c };

            Assert.Equal(new[] { "a", "b", "c" }, ElectrumServerPool.GetCandidates().Select(s => s.Host));

            ElectrumServerPool.ReportSuccess(c, 40);
            ElectrumServerPool.ReportSuccess(b, 900);
            Assert.Equal(new[] { "c", "b", "a" }, ElectrumServerPool.GetCandidates().Select(s => s.Host));
        }

        [Fact]
        public void Pool_FailureCoolsTheServerDown_AndTheNextSuccessRestoresIt()
        {
            var a = Server("a"); var b = Server("b");
            Globals.ClientSettings = new List<ClientSettings> { a, b };

            ElectrumServerPool.ReportFailure(a, "test");
            Assert.Equal(new[] { "b" }, ElectrumServerPool.GetCandidates().Select(s => s.Host));

            // Cooldown over: back in the list, but behind the server that has not failed.
            var later = DateTime.UtcNow + ElectrumServerPool.BaseCooldown + TimeSpan.FromSeconds(1);
            Assert.Equal(new[] { "b", "a" }, ElectrumServerPool.GetCandidates(later).Select(s => s.Host));

            ElectrumServerPool.ReportSuccess(a, 10);
            Assert.Equal(0UL, a.FailCount);
            Assert.Contains(a, ElectrumServerPool.GetCandidates());
        }

        [Fact]
        public void Pool_CooldownDoublesWithConsecutiveFailures_UpToTheCap()
        {
            Assert.Equal(TimeSpan.FromSeconds(15), ElectrumServerPool.CooldownFor(1));
            Assert.Equal(TimeSpan.FromSeconds(30), ElectrumServerPool.CooldownFor(2));
            Assert.Equal(TimeSpan.FromSeconds(60), ElectrumServerPool.CooldownFor(3));
            Assert.Equal(ElectrumServerPool.MaxCooldown, ElectrumServerPool.CooldownFor(12));
            Assert.Equal(ElectrumServerPool.MaxCooldown, ElectrumServerPool.CooldownFor(ulong.MaxValue));
        }

        [Fact]
        public void Pool_EveryServerCoolingDown_HasNoCandidates()
        {
            var a = Server("a"); var b = Server("b");
            Globals.ClientSettings = new List<ClientSettings> { a, b };
            ElectrumServerPool.ReportFailure(a, "test");
            ElectrumServerPool.ReportFailure(b, "test");

            Assert.Empty(ElectrumServerPool.GetCandidates());
        }

        [Fact]
        public void Pool_LaggingServerIsUsedOnlyWhenNothingElseIs()
        {
            var a = Server("a"); var b = Server("b");
            Globals.ClientSettings = new List<ClientSettings> { a, b };
            a.IsLagging = true;

            Assert.Equal(new[] { "b" }, ElectrumServerPool.GetCandidates().Select(s => s.Host));

            ElectrumServerPool.ReportFailure(b, "test");
            Assert.Equal(new[] { "a" }, ElectrumServerPool.GetCandidates().Select(s => s.Host));
        }

        [Fact]
        public void Pool_TipLag_MarksServersBehind_ButOneServerClaimingAHighTipCannotSidelineTheRest()
        {
            var a = Server("a"); var b = Server("b"); var c = Server("c"); var liar = Server("liar");
            a.TipHeight = 900_000; b.TipHeight = 900_000; c.TipHeight = 899_990; liar.TipHeight = 5_000_000;

            ElectrumServerPool.ApplyTipLag(new List<ClientSettings> { a, b, c, liar });

            Assert.False(a.IsLagging);
            Assert.False(b.IsLagging);
            Assert.True(c.IsLagging);      // 10 behind the reference (the second-highest tip)
            Assert.False(liar.IsLagging);
        }

        // ---------------------------------------------------------------- deposit lookup

        [Fact]
        public async Task Lookup_BestServerSilent_TheOthersAreAskedAndTheFirstAnswerWins()
        {
            using var fake = new FakeElectrum(("a", false, 0M), ("b", true, 1.5M), ("c", true, 1.5M));

            var answer = await DepositBalanceLookup.GetConfirmedBalanceAsync("addr1");

            Assert.True(answer.Answered);
            Assert.Equal(1.5M, answer.ConfirmedBtc);
            Assert.Equal("a", fake.Asked.First());
        }

        [Fact]
        public async Task Lookup_AnswerIsRemembered_AndBypassWithExcludeAsksAnotherServer()
        {
            using var fake = new FakeElectrum(("a", true, 1.0M), ("b", true, 2.0M));

            var first = await DepositBalanceLookup.GetConfirmedBalanceAsync("addr1");
            var cached = await DepositBalanceLookup.GetConfirmedBalanceAsync("addr1");
            Assert.Equal(1.0M, cached.ConfirmedBtc);
            Assert.Single(fake.Asked);

            var second = await DepositBalanceLookup.GetConfirmedBalanceAsync("addr1", bypassCache: true, excludeServer: first.Server);
            Assert.Equal(2.0M, second.ConfirmedBtc);
            Assert.Equal(new[] { "a", "b" }, fake.Asked);
        }

        [Fact]
        public async Task Lookup_NoServerAnswers_IsNoAnswer_NotZero()
        {
            using var fake = new FakeElectrum(("a", false, 0M), ("b", false, 0M));

            var answer = await DepositBalanceLookup.GetConfirmedBalanceAsync("addr1");

            Assert.False(answer.Answered);
            Assert.False(answer.NoOtherServer);
            Assert.Null(DepositBalanceLookup.LastKnownBalance("addr1"));

            // Both are cooling down now: the next lookup gives up at once instead of waiting per server.
            fake.Asked.Clear();
            Assert.False((await DepositBalanceLookup.GetConfirmedBalanceAsync("addr2")).Answered);
            Assert.Empty(fake.Asked);
        }

        // ---------------------------------------------------------------- owner deposit check

        private static Task<OwnerDepositCheck> Check(decimal needed, bool blockDownloads = false, bool blockVerify = false)
            => VbtcOwnerDeposit.CheckAsync(() => "addr1", needed, blockDownloads, blockVerify);

        private static void RunAsPlainNode() { Globals.ValidatorAddress = ""; Globals.IsBlockCaster = false; }
        private static void RunAsValidator() { Globals.ValidatorAddress = "xValidator"; Globals.IsBlockCaster = false; }

        [Fact]
        public async Task OwnerCheck_BlockValidation_NeverAsksElectrum()
        {
            using var fake = new FakeElectrum(("a", true, 5M));

            Assert.Equal(OwnerDepositStatus.NotQueried, (await Check(1M, blockVerify: true)).Status);
            Assert.Equal(OwnerDepositStatus.NotQueried, (await Check(1M, blockDownloads: true)).Status);
            Assert.Empty(fake.Asked);
        }

        [Fact]
        public async Task OwnerCheck_LedgerAloneCovers_NoQuery()
        {
            using var fake = new FakeElectrum(("a", true, 5M));
            var resolved = false;

            var result = await VbtcOwnerDeposit.CheckAsync(() => { resolved = true; return "addr1"; }, 0M, false, false);

            Assert.Equal(OwnerDepositStatus.Checked, result.Status);
            Assert.False(resolved); // not even the contract decompile for the address
            Assert.Empty(fake.Asked);
        }

        [Fact]
        public async Task OwnerCheck_NoAnswer_LocalSubmitAndProposalAreUnverifiable_AdmissionOnAValidatorTrusts()
        {
            using var fake = new FakeElectrum(("a", false, 0M));
            RunAsValidator();

            Assert.Equal(OwnerDepositStatus.Unverifiable, (await Check(1M)).Status); // LocalSubmit by default

            using (ElectrumCheckScope.Enter(ElectrumCheckMode.BlockProposal))
                Assert.Equal(OwnerDepositStatus.Unverifiable, (await Check(1M)).Status);

            using (ElectrumCheckScope.Enter(ElectrumCheckMode.PeerAdmission))
                Assert.Equal(OwnerDepositStatus.Trusted, (await Check(1M)).Status);

            Assert.Equal(ElectrumCheckMode.LocalSubmit, ElectrumCheckScope.Current); // scopes restore
        }

        [Fact]
        public async Task OwnerCheck_PlainNodeRelaying_DoesNotAskElectrum()
        {
            using var fake = new FakeElectrum(("a", true, 0M));
            RunAsPlainNode();

            using (ElectrumCheckScope.Enter(ElectrumCheckMode.PeerAdmission))
                Assert.Equal(OwnerDepositStatus.Trusted, (await Check(1M)).Status);
            Assert.Empty(fake.Asked);
        }

        [Fact]
        public async Task OwnerCheck_Shortfall_IsDoubleCheckedOnASecondServer_TheHigherAnswerWins()
        {
            // "a" is behind and under-reports; "b" sees the deposit.
            using var fake = new FakeElectrum(("a", true, 0.1M), ("b", true, 1.0M));

            var result = await Check(0.5M);

            Assert.Equal(OwnerDepositStatus.Checked, result.Status);
            Assert.Equal(1.0M, result.DepositBalance);
            Assert.Equal(new[] { "a", "b" }, fake.Asked);
        }

        [Fact]
        public async Task OwnerCheck_ShortfallBothServersAgree_IsAShortfall()
        {
            using var fake = new FakeElectrum(("a", true, 0.1M), ("b", true, 0.1M));

            var result = await Check(0.5M);

            Assert.Equal(OwnerDepositStatus.Checked, result.Status);
            Assert.Equal(0.1M, result.DepositBalance);
        }

        [Fact]
        public async Task OwnerCheck_ShortfallWithOnlyOneServerConfigured_TheOneAnswerStands()
        {
            using var fake = new FakeElectrum(("a", true, 0.1M));

            var result = await Check(0.5M);

            Assert.Equal(OwnerDepositStatus.Checked, result.Status);
            Assert.Equal(0.1M, result.DepositBalance);
        }

        [Fact]
        public async Task OwnerCheck_ShortfallAndTheSecondServerIsSilent_IsUnverifiable()
        {
            using var fake = new FakeElectrum(("a", true, 0.1M), ("b", false, 0M));

            Assert.Equal(OwnerDepositStatus.Unverifiable, (await Check(0.5M)).Status);
        }
    }
}
