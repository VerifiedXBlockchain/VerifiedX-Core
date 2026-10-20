using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A validator that does not cast pulls the membership record chain from the casters it knows on the caster
    /// monitor's tick (ValidatorMembershipPull, called from BlockcasterNode.MonitorCasters), adopts a verified
    /// successor and lets its caster list follow the record. Testnet, 20 October 2026: every non-caster kept the
    /// record it had at startup, refused blocks certified by the casters it did not know, and lost its rounds.
    /// </summary>
    [Collection("DbContextSequential")]
    public class ValidatorMembershipPullTests : IDisposable
    {
        private const long HeadSeq = 3;
        private const long Effective = 500;

        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly bool _priorIsBlockCaster = Globals.IsBlockCaster;
        private readonly string _priorValidatorAddress = Globals.ValidatorAddress;
        private readonly int _priorPort = Globals.ValAPIPort;
        private readonly System.Net.Http.IHttpClientFactory _priorFactory = Globals.HttpClientFactory;
        private readonly System.Collections.Concurrent.ConcurrentBag<Peers> _priorCasters = Globals.BlockCasters;
        private readonly List<(PrivateKey Key, string Pub, string Address)> _members = new();
        private readonly CasterMembershipRecord _head;

        public ValidatorMembershipPullTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"validator_pull_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            CasterMembershipStore.InvalidateCache();
            for (var i = 0; i < 3; i++)
            {
                var key = new PrivateKey("secp256k1");
                var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
                _members.Add((key, pub, AccountData.GetHumanAddress(pub)));
            }
            _head = new CasterMembershipRecord
            {
                RecordSeq = HeadSeq, EffectiveFromHeight = 400, PrevRecordHash = "prev", ChangeType = "Promotion",
                Casters = _members.Select(m => new CasterInfo { Address = m.Address, PeerIP = "10.0.0.1", PublicKey = m.Pub }).OrderBy(c => c.Address, StringComparer.Ordinal).ToList(),
            };
            _head.RecordHash = CasterMembershipStore.ComputeRecordHash(_head);
            DbContext.DB_Config!.GetCollection<CasterMembershipRecord>("rsrv_caster_membership").Insert(HeadSeq, _head);
            CasterMembershipStore.InvalidateCache();

            Globals.LastBlock = new Block { Height = Effective + 50, Timestamp = TimeUtil.GetTime() };
            Globals.ValidatorAddress = "xValidatorThatDoesNotCast";
            Globals.IsBlockCaster = false;
            Globals.HttpClientFactory = new PlainHttpClientFactory();
            ValidatorMembershipPull.ResetForTests();
        }

        public void Dispose()
        {
            Globals.BlockCasters = _priorCasters;
            Globals.HttpClientFactory = _priorFactory;
            Globals.ValAPIPort = _priorPort;
            Globals.ValidatorAddress = _priorValidatorAddress;
            Globals.IsBlockCaster = _priorIsBlockCaster;
            Globals.LastBlock = _priorLastBlock;
            ValidatorMembershipPull.ResetForTests();
            CasterMembershipStore.InvalidateCache();
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        /// <summary>A demotion of the third member at seq 4, signed by the other two (a majority of three).</summary>
        private CasterMembershipRecord Successor()
        {
            var candidate = new CasterMembershipRecord
            {
                RecordSeq = HeadSeq + 1, EffectiveFromHeight = Effective, PrevRecordHash = _head.RecordHash, ChangeType = "Demotion",
                ChangedAddress = _head.Casters[2].Address, Casters = _head.Casters.Take(2).ToList(),
            };
            candidate.RecordHash = CasterMembershipStore.ComputeRecordHash(candidate);
            var payload = CasterMembershipStore.CanonicalPayload(candidate);
            foreach (var m in _members.Where(m => candidate.Casters.Any(c => c.Address == m.Address)))
                candidate.Signatures.Add(new RecordSignature { SignerAddress = m.Address, Signature = SignatureService.CreateSignature(payload, m.Key, m.Pub) });
            return candidate;
        }

        /// <summary>A caster on loopback whose GetMembershipRecord answers with <paramref name="records"/>, and how often it was asked.</summary>
        private static (System.Net.Sockets.TcpListener Listener, System.Threading.CancellationTokenSource Stop, Func<int> Asked) Caster(params CasterMembershipRecord[] records)
        {
            var body = JsonConvert.SerializeObject(new MembershipRecordResponse { HeadSeq = records.Length == 0 ? HeadSeq : records.Max(r => r.RecordSeq), Records = records.ToList() });
            var bytes = System.Text.Encoding.UTF8.GetBytes(body);
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            var stop = new System.Threading.CancellationTokenSource();
            var asked = 0;
            _ = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    try
                    {
                        using var client = await listener.AcceptTcpClientAsync(stop.Token);
                        var stream = client.GetStream();
                        await stream.ReadAsync(new byte[4096]);
                        System.Threading.Interlocked.Increment(ref asked);
                        var head = System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(head);
                        await stream.WriteAsync(bytes);
                    }
                    catch { return; }
                }
            });
            Globals.ValAPIPort = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            return (listener, stop, () => asked);
        }

        private void KnowsOneCasterAt(string ip)
        {
            Globals.BlockCasters = new System.Collections.Concurrent.ConcurrentBag<Peers>
            {
                new Peers { PeerIP = ip, ValidatorAddress = _head.Casters[0].Address, ValidatorPublicKey = _head.Casters[0].PublicKey, IsValidator = true, IsOutgoing = true },
            };
        }

        [Fact]
        public async Task ANonCaster_PullsTheRecordOnTheMonitorTick_AdoptsTheSuccessor_AndItsCasterListFollows()
        {
            var successor = Successor();
            var (listener, stop, asked) = Caster(successor);
            try
            {
                KnowsOneCasterAt("127.0.0.1");
                Assert.Contains("127.0.0.1", ValidatorMembershipPull.PeerIps());

                Assert.True(await ValidatorMembershipPull.PullIfDueAsync());

                Assert.Equal(1, asked());
                Assert.Equal(successor.RecordHash, CasterMembershipStore.GetCurrent()!.RecordHash);
                Assert.Equal(
                    successor.Casters.Select(c => c.Address).OrderBy(a => a, StringComparer.Ordinal),
                    Globals.BlockCasters.Select(p => p.ValidatorAddress).OrderBy(a => a, StringComparer.Ordinal));

                // Due once a minute, not on every five-second tick.
                Assert.False(await ValidatorMembershipPull.PullIfDueAsync());
                Assert.Equal(1, asked());
            }
            finally
            {
                stop.Cancel();
                listener.Stop();
            }
        }

        [Fact]
        public async Task ACaster_DoesNotPullHere_ItsRoundLoopDoes()
        {
            var (listener, stop, asked) = Caster(Successor());
            try
            {
                KnowsOneCasterAt("127.0.0.1");
                Globals.IsBlockCaster = true;

                Assert.False(await ValidatorMembershipPull.PullIfDueAsync());

                Assert.Equal(0, asked());
                Assert.Equal(HeadSeq, CasterMembershipStore.GetCurrent()!.RecordSeq);
            }
            finally
            {
                stop.Cancel();
                listener.Stop();
            }
        }

        [Fact]
        public async Task AnUnchangedHead_LeavesTheCasterListAlone()
        {
            var (listener, stop, asked) = Caster();
            try
            {
                KnowsOneCasterAt("127.0.0.1");
                var before = Globals.BlockCasters.Select(p => p.PeerIP).ToList();

                Assert.True(await ValidatorMembershipPull.PullIfDueAsync());

                Assert.Equal(1, asked());
                Assert.Equal(HeadSeq, CasterMembershipStore.GetCurrent()!.RecordSeq);
                Assert.Equal(before, Globals.BlockCasters.Select(p => p.PeerIP).ToList());
            }
            finally
            {
                stop.Cancel();
                listener.Stop();
            }
        }

        /// <summary>Clients that reach the loopback caster only: every other destination (the bootstrap seeds, the record's 10.0.0.1) is refused at once, so the tests never leave the machine or wait on a timeout.</summary>
        private sealed class PlainHttpClientFactory : System.Net.Http.IHttpClientFactory
        {
            public System.Net.Http.HttpClient CreateClient(string name) => new(new LoopbackOnlyHandler());

            private sealed class LoopbackOnlyHandler : System.Net.Http.DelegatingHandler
            {
                public LoopbackOnlyHandler() : base(new System.Net.Http.HttpClientHandler()) { }

                protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, System.Threading.CancellationToken cancellationToken) =>
                    System.Net.IPAddress.TryParse(request.RequestUri?.Host, out var ip) && System.Net.IPAddress.IsLoopback(ip)
                        ? base.SendAsync(request, cancellationToken)
                        : throw new System.Net.Http.HttpRequestException($"{request.RequestUri?.Host} is not the loopback caster of this test.");
            }
        }
    }
}
