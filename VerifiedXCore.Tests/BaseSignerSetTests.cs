using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Who sits on the vBTCb contract is decided by VFX alone (BaseSignerSetPolicy), and a validator signs an addition or
    /// removal only when it is exactly the change it computes itself for the current epoch (SignValidatorUpdate, through
    /// the validator API as the node serves it).
    /// </summary>
    [Collection("DbContextSequential")]
    public class BaseSignerSetTests : IDisposable
    {
        private const string Proxy = "0x09096c6eBf35F0aDAEeC02D06a0dE77E790F132D";
        private const long Epoch = 2 * BaseSignerSetPolicy.EpochBlocks;
        private readonly string EpochHash = $"epoch-{Guid.NewGuid():N}";   // the registry caches a scan by (height, block hash)

        private sealed record V(PrivateKey Key, string Pub, string Address, string Base);

        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly string _priorValidator = Globals.ValidatorAddress;
        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly bool _priorSynced = Globals.IsChainSynced;
        private readonly bool _priorResyncing = Globals.IsResyncing;
        private readonly bool _priorStopTimers = Globals.StopAllTimers;
        private readonly long _priorHeight = Globals.BaseBridgeV4Height;
        private readonly string _priorContract = BaseBridgeService.ContractAddress;
        private readonly int _priorChainId = BaseBridgeService.BaseChainId;

        // On the contract: this node, two others, and one that left VFX. Registered newcomers: two funded, one not.
        private readonly V _me = NewKey(), _seed2 = NewKey(), _seed3 = NewKey(), _gone = NewKey();
        private readonly V _new1 = NewKey(), _new2 = NewKey(), _unfunded = NewKey();

        public BaseSignerSetTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"basesigners_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();

            var registered = new[] { _me, _seed2, _seed3, _new1, _new2, _unfunded };
            foreach (var v in registered.Append(_gone))
                AccountData.GetAccounts().InsertSafe(new Account { Address = v.Address, PublicKey = v.Pub, PrivateKey = "0" + v.Key.secret.ToString("x") });
            foreach (var v in registered.Where(v => v != _unfunded))
                StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = v.Address, Balance = 5_000M, Nonce = 0 });
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _unfunded.Address, Balance = 4_999M, Nonce = 0 });
            BlockchainData.GetBlocks().InsertSafe(new Block
            {
                Height = Epoch - 100, Hash = "registrations",
                Transactions = registered.Select((v, i) => new Transaction
                {
                    TransactionType = TransactionType.VBTC_V2_VALIDATOR_REGISTER, FromAddress = v.Address, ToAddress = v.Address, Hash = "r" + i,
                    Data = JsonConvert.SerializeObject(new { ValidatorAddress = v.Address, IPAddress = $"10.0.0.{i + 1}", FrostPublicKey = v.Pub, IsS3C = false }),
                }).ToList(),
            });
            BlockchainData.GetBlocks().InsertSafe(new Block { Height = Epoch, Hash = EpochHash, Transactions = new List<Transaction>() });

            Globals.LastBlock = new Block { Height = Epoch + BaseSignerSetPolicy.SettleBlocks, Timestamp = TimeUtil.GetTime() };
            Globals.IsChainSynced = true;
            Globals.IsResyncing = false;
            Globals.StopAllTimers = false;
            Globals.BaseBridgeV4Height = Globals.BaseBridgeV4TestnetHeight;
            Globals.ValidatorAddress = _me.Address;
            BaseBridgeService.ContractAddress = Proxy;
            BaseBridgeService.BaseChainId = 8453;
            VBTCValidatorRegistry.InvalidateCache();
        }

        public void Dispose()
        {
            BaseBridgeService.ReadSignerViewForTests = null;
            BaseBridgeService.ContractAddress = _priorContract;
            BaseBridgeService.BaseChainId = _priorChainId;
            Globals.BaseBridgeV4Height = _priorHeight;
            Globals.ValidatorAddress = _priorValidator;
            Globals.LastBlock = _priorLastBlock;
            Globals.IsChainSynced = _priorSynced;
            Globals.IsResyncing = _priorResyncing;
            Globals.StopAllTimers = _priorStopTimers;
            VBTCValidatorRegistry.InvalidateCache();
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static V NewKey()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            return new V(key, pub, AccountData.GetHumanAddress(pub), ValidatorEthKeyService.DeriveBaseAddressFromVfxPublicKey(pub).ToLowerInvariant());
        }

        private static IEnumerable<string> Addresses(int n, char c = 'a') =>
            Enumerable.Range(1, n).Select(i => "0x" + c + i.ToString("x").PadLeft(39, '0'));

        /// <summary>The contract with these validators, and the same keys as its casters (as the seeds are on mainnet).</summary>
        private static void Contract(int nonce, params V[] on) => Contract(nonce, on, on);

        private static void Contract(int nonce, V[] validators, V[] casters)
        {
            BaseValidatorSyncService.SignerViewCache = null;                        // the contract changed: read it again
            BaseBridgeService.ReadSignerViewForTests = () => Task.FromResult<BaseBridgeService.SignerView?>(new BaseBridgeService.SignerView(
                validators.Select(v => v.Base).ToList(), 4, Math.Max(2, (validators.Length * 2 + 2) / 3), nonce,
                casters.Select(v => v.Base).ToList(), casters.Length == 0 ? 0 : casters.Length / 2 + 1));
        }

        private static async Task<(HttpStatusCode Code, JObject Body)> Ask(string action, IEnumerable<string> targets, long epoch, int nonce)
        {
            using var server = new TestServer(new WebHostBuilder().UseStartup<StartupP2PCaster>());
            var ctx = await server.SendAsync(c =>
            {
                c.Request.Method = "POST";
                c.Request.Path = "/valapi/validator/SignValidatorUpdate";
                c.Request.ContentType = "application/json";
                c.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new
                {
                    Action = action, TargetAddresses = targets.ToArray(), VfxBlockHeight = epoch, AdminNonce = nonce.ToString(),
                })));
                c.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");
            });
            using var reader = new StreamReader(ctx.Response.Body);
            var text = await reader.ReadToEndAsync();
            var json = text.Trim().StartsWith("\"") ? JsonConvert.DeserializeObject<string>(text)! : text;
            return ((HttpStatusCode)ctx.Response.StatusCode, JObject.Parse(json));
        }

        private static string Refusal((HttpStatusCode Code, JObject Body) answer)
        {
            Assert.Equal(HttpStatusCode.Forbidden, answer.Code);
            Assert.Null(answer.Body["Signature"]?.Value<string>());
            return answer.Body.Value<string>("Message")!;
        }

        private static void SignedBy(V signer, (HttpStatusCode Code, JObject Body) answer, byte[] digest)
        {
            Assert.True(answer.Code == HttpStatusCode.OK, answer.Body.ToString());
            Assert.Equal(signer.Base, ValidatorEthKeyService.RecoverSigner(digest, answer.Body.Value<string>("Signature"), personalMessage: false), StringComparer.OrdinalIgnoreCase);
        }

        // ── The rule ────────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void IncumbentsStay_TheDepartedGo_NeverBelowThree()
        {
            var contract = Addresses(5).ToList();
            var plan = BaseSignerSetPolicy.Compute(Epoch, EpochHash, contract.Take(3), contract);
            Assert.Equal(contract.Skip(3).OrderBy(a => BaseSignerSetPolicy.RankOf(EpochHash, a), StringComparer.Ordinal).Take(1), plan.Removes); // under a third of 5 an epoch
            Assert.Empty(plan.Adds);                                               // removals first

            var two = BaseSignerSetPolicy.Compute(Epoch, EpochHash, contract.Take(1), contract.Take(3));
            Assert.Empty(two.Removes);                                             // would leave fewer than 3
            var four = BaseSignerSetPolicy.Compute(Epoch, EpochHash, contract.Take(1), contract.Take(4));
            Assert.Single(four.Removes);                                           // down to 3, no further
        }

        [Fact]
        public void FewerThanAThirdLeaveInOneEpoch_AndAValidatorStillSeenStays()
        {
            var contract = Addresses(10).ToList();
            Assert.Equal(3, BaseSignerSetPolicy.Compute(Epoch, EpochHash, Array.Empty<string>(), contract).Removes.Count); // not all 7 above the floor
            Assert.Empty(BaseSignerSetPolicy.Compute(Epoch, EpochHash, Array.Empty<string>(), contract, contract).Removes);
            var left = contract.ToList();
            for (var epoch = 1; left.Count > BaseSignerSetPolicy.MinValidators; epoch++)
            {
                var removes = BaseSignerSetPolicy.Compute(epoch * BaseSignerSetPolicy.EpochBlocks, $"h{epoch}", Array.Empty<string>(), left).Removes;
                Assert.True(3 * removes.Count < left.Count);
                left = left.Except(removes).ToList();
            }
        }

        /// <summary>
        /// A validator on the contract keeps its seat while it was seen (REGISTER or HEARTBEAT) in the two days before the
        /// boundary, even when it is not active at the boundary itself; it loses it when not seen for two days or once it
        /// exits.
        /// </summary>
        [Fact]
        public void AValidatorOnTheContract_KeepsItsSeatForTwoDaysWithoutAHeartbeat_NotLongerOrAfterItExits()
        {
            const long boundary = 3 * BaseSignerSetPolicy.EpochBlocks;
            var (quiet, old, exited) = (NewKey(), NewKey(), NewKey());
            foreach (var v in new[] { quiet, old, exited })
                StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = v.Address, Balance = 5_000M, Nonce = 0 });
            Transaction Register(V v) => new()
            {
                TransactionType = TransactionType.VBTC_V2_VALIDATOR_REGISTER, FromAddress = v.Address, ToAddress = v.Address, Hash = "r" + v.Address,
                Data = JsonConvert.SerializeObject(new { ValidatorAddress = v.Address, IPAddress = "10.0.1.1", FrostPublicKey = v.Pub, IsS3C = false }),
            };
            void At(long height, params Transaction[] txs) => BlockchainData.GetBlocks().InsertSafe(new Block { Height = height, Hash = $"b{height}-{Guid.NewGuid():N}", Transactions = txs.ToList() });
            At(boundary - BaseSignerSetPolicy.KeepSeenBlocks - 1, Register(old));                // seen just over two days before
            At(boundary - 5_000, Register(quiet));                                                 // seen 5,000 blocks before: not active, still seen
            At(boundary - 200, Register(exited));
            At(boundary - 100, new Transaction
            {
                TransactionType = TransactionType.VBTC_V2_VALIDATOR_EXIT, FromAddress = exited.Address, ToAddress = exited.Address, Hash = "x" + exited.Address,
                Data = JsonConvert.SerializeObject(new { ValidatorAddress = exited.Address }),
            });
            At(boundary);

            var contract = new[] { _me, _seed2, _seed3, _new1, _new2, quiet, old, exited }.Select(v => v.Base).ToList();
            var plan = BaseSignerSetPolicy.ForEpoch(boundary, contract)!;
            Assert.Equal(new[] { old.Base, exited.Base }.OrderBy(a => a), plan.Removes.OrderBy(a => a));
            Assert.DoesNotContain(quiet.Base, VBTCValidatorRegistry.GetActiveValidatorsAt(boundary).Select(v => BaseSignerSetPolicy.BaseAddressOf(v.FrostPublicKey).ToLowerInvariant()));
        }

        [Fact]
        public void TheCasters_FollowTheCommitteeTwoDaysBehind_OneInAndOneOutAnEpoch()
        {
            var c = Addresses(12, 'c').ToList();
            BaseSignerSetPolicy.CasterPlan Plan(IEnumerable<string> now, IEnumerable<string> day, IEnumerable<string> two, IEnumerable<string> contract) =>
                BaseSignerSetPolicy.ComputeCasters(Epoch, EpochHash, now, day, two, contract);
            var five = c.Take(5).ToList();

            // Out at the boundary and both before: out. Out at the boundary only (or back since): stays.
            Assert.Equal(new[] { c[4] }, Plan(c.Take(4), c.Take(4), c.Take(4), five).Remove);
            Assert.Empty(Plan(c.Take(4), c.Take(4), five, five).Remove);
            Assert.Empty(Plan(c.Take(4), five, c.Take(4), five).Remove);

            // In at all three: in. In for less: waits. One of each an epoch.
            var swap = Plan(c.Take(4).Append(c[5]), c.Take(4).Append(c[5]), c.Take(4).Append(c[5]), five);
            Assert.Equal(new[] { c[5] }, swap.Add);
            Assert.Equal(new[] { c[4] }, swap.Remove);
            Assert.Empty(Plan(five.Append(c[5]), five.Append(c[5]), five, five).Add);
            var many = c.Take(3).ToList();
            Assert.Single(Plan(c.Take(8), c.Take(8), c.Take(8), many).Add);
            Assert.Single(Plan(c.Take(3), c.Take(3), c.Take(3), c.Skip(5).Take(5)).Remove);

            // Never below 3 or above 10; no record, no change; a contract without casters gets every member in for two days.
            Assert.Empty(Plan(c.Take(2), c.Take(2), c.Take(2), c.Take(3)).Remove);
            Assert.Empty(Plan(c.Take(11), c.Take(11), c.Take(11), c.Take(10)).Add);
            Assert.True(Plan(null, five, five, five).Empty);
            Assert.Equal(five.OrderBy(a => a), Plan(c.Take(6), five, five, Array.Empty<string>()).Add.OrderBy(a => a));
            Assert.True(Plan(c.Take(2), c.Take(2), c.Take(2), Array.Empty<string>()).Empty);
        }

        [Fact]
        public void TheSetGrowsByLessThanAThirdAnEpoch_UpToFifty()
        {
            var newcomers = Addresses(100, 'b').ToList();
            var sizes = new List<int>();
            var contract = Addresses(3).ToList();
            for (var epoch = 1; epoch <= 12; epoch++)
            {
                var plan = BaseSignerSetPolicy.Compute(epoch * BaseSignerSetPolicy.EpochBlocks, $"h{epoch}", contract.Concat(newcomers), contract);
                Assert.Empty(plan.Removes);
                Assert.True(plan.Adds.Count == 0 || 3 * plan.Adds.Count < contract.Count + plan.Adds.Count);   // newcomers < 1/3 of the new set
                contract.AddRange(plan.Adds);
                sizes.Add(contract.Count);
            }
            Assert.Equal(new[] { 4, 5, 7, 10, 14, 20, 29, 43, 50, 50, 50, 50 }, sizes);
        }

        [Fact]
        public void ANewcomersPlace_FollowsTheBoundaryBlocksHash_NotItsAddress()
        {
            var contract = Addresses(5).ToList();                                 // room for two
            var newcomers = Addresses(20, 'b').ToList();
            string[] Chosen(string hash) => BaseSignerSetPolicy.Compute(Epoch, hash, contract.Concat(newcomers), contract).Adds.ToArray();
            var expected = newcomers.OrderBy(a => BaseSignerSetPolicy.RankOf("h1", a), StringComparer.Ordinal).Take(2);
            Assert.Equal(expected, Chosen("h1"));
            Assert.NotEqual(newcomers.Take(2), Chosen("h1"));                       // not the lowest addresses
            Assert.NotEqual(Chosen("h1"), Chosen("h2"));                            // another block, another order
        }

        // ── What a validator signs ──────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task AValidatorSignsTheRemovalItComputes_ThenOneAdditionAnEpoch()
        {
            // The departed validator goes first; nothing is added while it is still on.
            Contract(7, _me, _seed2, _seed3, _gone);
            var removal = await Ask("REMOVE", new[] { _gone.Base }, Epoch, 7);
            SignedBy(_me, removal, VbtcbTypedData.RemoveValidatorsDigest(new[] { _gone.Base }, Epoch, 7, 8453, Proxy));
            Assert.Contains("removals come first", Refusal(await Ask("ADD", new[] { _new1.Base }, Epoch, 7)));
            Assert.Contains("Not the removal", Refusal(await Ask("REMOVE", new[] { _seed2.Base }, Epoch, 7)));

            // Removed: one seat for a newcomer, the first of the funded ones by the boundary block's hash.
            Contract(8, _me, _seed2, _seed3);
            var (first, second) = BaseSignerSetPolicy.RankOf(EpochHash, _new1.Base).CompareTo(BaseSignerSetPolicy.RankOf(EpochHash, _new2.Base)) < 0
                ? (_new1, _new2) : (_new2, _new1);
            Assert.Contains("Not an addition", Refusal(await Ask("ADD", new[] { second.Base }, Epoch, 8)));
            Assert.Contains("Not an addition", Refusal(await Ask("ADD", new[] { _unfunded.Base }, Epoch, 8)));
            Assert.Contains("current epoch", Refusal(await Ask("ADD", new[] { first.Base }, Epoch - BaseSignerSetPolicy.EpochBlocks, 8)));
            Assert.Contains("Admin nonce", Refusal(await Ask("ADD", new[] { first.Base }, Epoch, 7)));
            SignedBy(_me, await Ask("ADD", new[] { first.Base }, Epoch, 8), VbtcbTypedData.AddValidatorsDigest(new[] { first.Base }, Epoch, 8, 8453, Proxy));
            SignedBy(_me, await Ask("ADD", new[] { first.Base }, Epoch, 8), VbtcbTypedData.AddValidatorsDigest(new[] { first.Base }, Epoch, 8, 8453, Proxy)); // same nonce: again

            // It went through: the plan now has room for the next, but this node signs no second addition this epoch.
            Contract(9, _me, _seed2, _seed3, first);
            Assert.Contains("already signed an addition", Refusal(await Ask("ADD", new[] { second.Base }, Epoch, 9)));
        }

        [Fact]
        public async Task ANewcomerSignsOnlyItsOwnAddition_AndNothingIsSignedBeforeTheBoundaryIsSettledOrTheHeight()
        {
            Contract(8, _me, _seed2, _seed3);
            var first = BaseSignerSetPolicy.ForEpoch(Epoch, new[] { _me.Base, _seed2.Base, _seed3.Base })!.Adds.Single();
            var newcomer = first == _new1.Base ? _new1 : _new2;
            var other = newcomer == _new1 ? _new2 : _new1;

            Globals.ValidatorAddress = newcomer.Address;
            SignedBy(newcomer, await Ask("ADD", new[] { first }, Epoch, 8), VbtcbTypedData.AddValidatorsDigest(new[] { first }, Epoch, 8, 8453, Proxy));
            Globals.ValidatorAddress = other.Address;                               // neither on the contract nor being added
            Assert.Contains("Only a validator or caster on the contract, or one being added", Refusal(await Ask("ADD", new[] { first }, Epoch, 8)));
            Assert.Contains("Only a validator or caster on the contract signs a removal", Refusal(await Ask("REMOVE", new[] { _seed3.Base }, Epoch, 8)));

            Globals.ValidatorAddress = _me.Address;
            Globals.LastBlock = new Block { Height = Epoch + BaseSignerSetPolicy.SettleBlocks - 1, Timestamp = TimeUtil.GetTime() };
            Assert.Contains("not settled", Refusal(await Ask("ADD", new[] { first }, Epoch, 8)));
            Globals.LastBlock = new Block { Height = Epoch + BaseSignerSetPolicy.SettleBlocks, Timestamp = TimeUtil.GetTime() };
            Globals.BaseBridgeV4Height = Epoch + 1;
            Assert.Equal(BaseValidatorSyncService.AdminChangesNotSignedReason, Refusal(await Ask("ADD", new[] { first }, Epoch, 8)));
            Globals.BaseBridgeV4Height = long.MaxValue;                             // mainnet as released
            Assert.Equal(BaseValidatorSyncService.AdminChangesNotSignedReason, Refusal(await Ask("ADD", new[] { first }, Epoch, 8)));
        }

        /// <summary>
        /// SignValidatorUpdate is open to anyone. A request this node refuses on what it knows itself (below the height,
        /// another epoch, an unsettled boundary, a malformed target) makes no Base call, and requests that do need the
        /// contract share one read for a few seconds.
        /// </summary>
        [Fact]
        public async Task ARequestRefusedOnWhatTheNodeKnows_ReadsNothingFromBase_AndOthersShareOneRead()
        {
            var reads = 0;
            BaseValidatorSyncService.SignerViewCache = null;
            BaseBridgeService.ReadSignerViewForTests = () =>
            {
                Interlocked.Increment(ref reads);
                return Task.FromResult<BaseBridgeService.SignerView?>(new BaseBridgeService.SignerView(new[] { _me.Base, _seed2.Base, _seed3.Base }, 4, 2, 8,
                    new[] { _me.Base, _seed2.Base, _seed3.Base }, 2));
            };
            var first = BaseSignerSetPolicy.ForEpoch(Epoch, new[] { _me.Base, _seed2.Base, _seed3.Base })!.Adds.Single();

            Globals.BaseBridgeV4Height = long.MaxValue;                             // mainnet as released
            Assert.Equal(BaseValidatorSyncService.AdminChangesNotSignedReason, Refusal(await Ask("ADD", new[] { first }, Epoch, 8)));
            Globals.BaseBridgeV4Height = Globals.BaseBridgeV4TestnetHeight;
            Assert.Contains("current epoch", Refusal(await Ask("ADD", new[] { first }, Epoch - BaseSignerSetPolicy.EpochBlocks, 8)));
            Assert.Contains("Invalid target", Refusal(await Ask("ADD", new[] { "0x12" }, Epoch, 8)));
            Globals.LastBlock = new Block { Height = Epoch + BaseSignerSetPolicy.SettleBlocks - 1, Timestamp = TimeUtil.GetTime() };
            Assert.Contains("not settled", Refusal(await Ask("ADD", new[] { first }, Epoch, 8)));
            Assert.Equal(0, reads);

            Globals.LastBlock = new Block { Height = Epoch + BaseSignerSetPolicy.SettleBlocks, Timestamp = TimeUtil.GetTime() };
            for (var i = 0; i < 5; i++)
                SignedBy(_me, await Ask("ADD", new[] { first }, Epoch, 8), VbtcbTypedData.AddValidatorsDigest(new[] { first }, Epoch, 8, 8453, Proxy));
            Assert.Contains("Admin nonce", Refusal(await Ask("ADD", new[] { first }, Epoch, 7)));
            Assert.Equal(1, reads);
        }

        // ── Who sends it ────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Routes each request to the validator API as the node at that IP answers it; unreachable IPs fail.</summary>
        private sealed class ValidatorNetwork : System.Net.Http.IHttpClientFactory
        {
            private readonly TestServer _server = new(new WebHostBuilder().UseStartup<StartupP2PCaster>());
            private readonly Dictionary<string, V> _at;
            public readonly HashSet<string> Down = new();
            public ValidatorNetwork(Dictionary<string, V> at) => _at = at;
            public System.Net.Http.HttpClient CreateClient(string name) => new(new Router(this));
            private sealed class Router : System.Net.Http.HttpMessageHandler
            {
                private readonly ValidatorNetwork _net;
                public Router(ValidatorNetwork net) => _net = net;
                protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, System.Threading.CancellationToken ct)
                {
                    var ip = request.RequestUri!.Host;
                    if (_net.Down.Contains(ip) || !_net._at.TryGetValue(ip, out var v)) throw new System.Net.Http.HttpRequestException("unreachable");
                    var body = await request.Content!.ReadAsStringAsync(ct);
                    var caller = Globals.ValidatorAddress;
                    Globals.ValidatorAddress = v.Address;                         // the node at that IP answers
                    try
                    {
                        var ctx = await _net._server.SendAsync(c =>
                        {
                            c.Request.Method = "POST";
                            c.Request.Path = request.RequestUri.AbsolutePath;
                            c.Request.ContentType = "application/json";
                            c.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
                            c.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");
                        });
                        using var reader = new StreamReader(ctx.Response.Body);
                        return new System.Net.Http.HttpResponseMessage((HttpStatusCode)ctx.Response.StatusCode) { Content = new System.Net.Http.StringContent(await reader.ReadToEndAsync()) };
                    }
                    finally { Globals.ValidatorAddress = caller; }
                }
            }
        }

        [Fact]
        public async Task TheValidatorInTurn_SendsTheRemovalThenTheAddition_WithEachValidatorsOwnSignature()
        {
            var at = new[] { _me, _seed2, _seed3, _new1, _new2, _unfunded }.Select((v, i) => (v, i)).ToDictionary(x => $"10.0.0.{x.i + 1}", x => x.v);
            var network = new ValidatorNetwork(at);
            var sent = new List<(string Function, object[] Args)>();
            var priorFactory = Globals.HttpClientFactory;
            Globals.HttpClientFactory = network;
            BaseValidatorSyncService.SubmitForTests = (f, a) => { sent.Add((f, a)); return Task.FromResult<string?>("0xtx"); };
            Globals.LastBlock = new Block { Height = Epoch + BaseSignerSetPolicy.SettleBlocks + 3 * BaseValidatorSyncService.SubmitTurnBlocks, Timestamp = TimeUtil.GetTime() };
            try
            {
                static List<string> Sigs(object arg) => ((List<byte[]>)arg).Select(b => "0x" + Convert.ToHexString(b)).ToList();
                static void SignedByDistinct(IEnumerable<string> sigs, byte[] digest, IEnumerable<V> allowed, int atLeast)
                {
                    var signers = sigs.Select(s => ValidatorEthKeyService.RecoverSigner(digest, s, personalMessage: false)!.ToLowerInvariant()).ToList();
                    Assert.True(signers.Count >= atLeast, $"{signers.Count} < {atLeast}");
                    Assert.Equal(signers.Count, signers.Distinct().Count());
                    Assert.All(signers, s => Assert.Contains(s, allowed.Select(v => v.Base)));
                }

                // The departed validator is removed with 2/3 of the contract's validators signing.
                Contract(7, _me, _seed2, _seed3, _gone);
                await BaseValidatorSyncService.SyncOnceAsync();
                var (removeFn, removeArgs) = Assert.Single(sent);
                Assert.Equal("removeValidatorBatch", removeFn);
                Assert.Equal(new[] { _gone.Base }, (List<string>)removeArgs[0]);
                Assert.Equal(Epoch, (long)(System.Numerics.BigInteger)removeArgs[1]);
                SignedByDistinct(Sigs(removeArgs[2]), VbtcbTypedData.RemoveValidatorsDigest(new[] { _gone.Base }, Epoch, 7, 8453, Proxy), new[] { _me, _seed2, _seed3 }, 3);

                // Then the first newcomer, which signs its own seat, while its turn has not come for a later validator.
                sent.Clear();
                Contract(8, _me, _seed2, _seed3);
                var first = BaseSignerSetPolicy.ForEpoch(Epoch, new[] { _me.Base, _seed2.Base, _seed3.Base })!.Adds.Single();
                var newcomer = first == _new1.Base ? _new1 : _new2;
                var order = new[] { _me, _seed2, _seed3 }.OrderBy(v => BaseSignerSetPolicy.RankOf(EpochHash, v.Base), StringComparer.Ordinal).ToList();
                Globals.ValidatorAddress = order[2].Address;
                Globals.LastBlock = new Block { Height = Epoch + BaseSignerSetPolicy.SettleBlocks + 2 * BaseValidatorSyncService.SubmitTurnBlocks - 1, Timestamp = TimeUtil.GetTime() };
                await BaseValidatorSyncService.SyncOnceAsync();
                Assert.Empty(sent);                                                // third in order: not yet

                network.Down.Add(at.Single(x => x.Value == newcomer).Key);        // the newcomer is down: no seat this time
                Globals.LastBlock = new Block { Height = Epoch + BaseSignerSetPolicy.SettleBlocks + 2 * BaseValidatorSyncService.SubmitTurnBlocks, Timestamp = TimeUtil.GetTime() };
                await BaseValidatorSyncService.SyncOnceAsync();
                Assert.Empty(sent);

                network.Down.Clear();
                await BaseValidatorSyncService.SyncOnceAsync();
                var (addFn, addArgs) = Assert.Single(sent);
                Assert.Equal("addValidatorBatch", addFn);
                Assert.Equal(new[] { first }, (List<string>)addArgs[0]);
                var addDigest = VbtcbTypedData.AddValidatorsDigest(new[] { first }, Epoch, 8, 8453, Proxy);
                SignedByDistinct(Sigs(addArgs[2]), addDigest, new[] { _me, _seed2, _seed3 }, 2);
                SignedByDistinct(Sigs(addArgs[3]), addDigest, new[] { newcomer }, 1);
            }
            finally
            {
                BaseValidatorSyncService.SubmitForTests = null;
                Globals.HttpClientFactory = priorFactory;
            }
        }

        /// <summary>
        /// On a contract with its own casters, a validator change needs both groups: a caster signs the change it computes
        /// itself, the validator in turn gathers 2/3 of the validators and a majority of the casters (at the address the
        /// caster record gives) before it sends, and while the contract has no casters nothing is signed or sent.
        /// </summary>
        [Fact]
        public async Task AValidatorChange_IsSignedByTheCastersToo_AndNothingChangesWhileTheContractHasNone()
        {
            var casters = new[] { NewKey(), NewKey(), NewKey() };
            foreach (var c in casters)
                AccountData.GetAccounts().InsertSafe(new Account { Address = c.Address, PublicKey = c.Pub, PrivateKey = "0" + c.Key.secret.ToString("x") });
            var record = new CasterMembershipRecord
            {
                RecordSeq = 1, EffectiveFromHeight = 0,
                Casters = casters.Select((c, i) => new CasterInfo { Address = c.Address, PeerIP = $"10.0.5.{i + 1}", PublicKey = c.Pub }).ToList(),
            };
            DbContext.DB_Config!.GetCollection<CasterMembershipRecord>("rsrv_caster_membership").Upsert(record.RecordSeq, record);
            VerifiedXCore.Services.CasterMembershipStore.InvalidateCache();

            var at = new[] { _me, _seed2, _seed3, _new1, _new2, _unfunded }.Select((v, i) => (v, ip: $"10.0.0.{i + 1}"))
                .Concat(casters.Select((c, i) => (v: c, ip: $"10.0.5.{i + 1}"))).ToDictionary(x => x.ip, x => x.v);
            var network = new ValidatorNetwork(at);
            var sent = new List<(string Function, object[] Args)>();
            var priorFactory = Globals.HttpClientFactory;
            Globals.HttpClientFactory = network;
            BaseValidatorSyncService.SubmitForTests = (f, a) => { sent.Add((f, a)); return Task.FromResult<string?>("0xtx"); };
            var validators = new[] { _me, _seed2, _seed3, _gone };
            var digest = VbtcbTypedData.RemoveValidatorsDigest(new[] { _gone.Base }, Epoch, 7, 8453, Proxy);
            try
            {
                // While the contract has no casters (a proxy just upgraded from V3), no validator change is signed; what the
                // validator in turn sends is the first caster list: the committee's members, each signing its own seat.
                Contract(7, validators, Array.Empty<V>());
                Assert.Equal(BaseValidatorSyncService.NoCastersYetReason, Refusal(await Ask("REMOVE", new[] { _gone.Base }, Epoch, 7)));
                Globals.LastBlock = new Block { Height = Epoch + BaseSignerSetPolicy.SettleBlocks + 8 * BaseValidatorSyncService.SubmitTurnBlocks, Timestamp = TimeUtil.GetTime() };
                await BaseValidatorSyncService.SyncOnceAsync();
                var (initFn, initArgs) = Assert.Single(sent);
                Assert.Equal("updateCasters", initFn);
                Assert.Equal(casters.Select(c => c.Base).OrderBy(a => a), ((List<string>)initArgs[0]).OrderBy(a => a));
                Assert.Empty((List<string>)initArgs[1]);
                var initDigest = VbtcbTypedData.UpdateCastersDigest((List<string>)initArgs[0], Array.Empty<string>(), Epoch, 7, 8453, Proxy);
                Assert.Equal((List<string>)initArgs[0], ((List<byte[]>)initArgs[4]).Select(b => ValidatorEthKeyService.RecoverSigner(initDigest, "0x" + Convert.ToHexString(b), personalMessage: false)!.ToLowerInvariant()).ToList());
                sent.Clear();

                Contract(7, validators, casters);
                Globals.ValidatorAddress = casters[0].Address;                     // a caster signs the removal it computes
                SignedBy(casters[0], await Ask("REMOVE", new[] { _gone.Base }, Epoch, 7), digest);
                Globals.ValidatorAddress = _new1.Address;                          // on neither list
                Assert.Contains("Only a validator or caster on the contract", Refusal(await Ask("REMOVE", new[] { _gone.Base }, Epoch, 7)));

                Globals.ValidatorAddress = _me.Address;
                await BaseValidatorSyncService.SyncOnceAsync();
                var (fn, args) = Assert.Single(sent);
                Assert.Equal("removeValidatorBatch", fn);
                var signers = ((List<byte[]>)args[2]).Select(b => ValidatorEthKeyService.RecoverSigner(digest, "0x" + Convert.ToHexString(b), personalMessage: false)!.ToLowerInvariant()).ToList();
                Assert.True(signers.Count(s => new[] { _me, _seed2, _seed3 }.Any(v => v.Base == s)) >= 3);
                Assert.True(signers.Count(s => casters.Any(c => c.Base == s)) >= 2);
            }
            finally
            {
                DbContext.DB_Config!.GetCollection<CasterMembershipRecord>("rsrv_caster_membership").Delete(record.RecordSeq);
                VerifiedXCore.Services.CasterMembershipStore.InvalidateCache();
                BaseValidatorSyncService.SubmitForTests = null;
                Globals.HttpClientFactory = priorFactory;
            }
        }

        /// <summary>
        /// A caster out of the committee for two days leaves the contract and one in for two days joins, in one updateCasters
        /// the validator in turn sends: signed by 2/3 of the validators and a majority of the casters, each computing the
        /// same change from its own caster records, and by the new caster for its own seat.
        /// </summary>
        [Fact]
        public async Task ACasterOutOfTheCommitteeForTwoDays_IsSwappedForOneInForTwoDays()
        {
            const long boundary = 4 * BaseSignerSetPolicy.EpochBlocks;
            var c = Enumerable.Range(0, 5).Select(_ => NewKey()).ToArray();               // c[3] leaves the committee, c[4] joins
            foreach (var k in c)
                AccountData.GetAccounts().InsertSafe(new Account { Address = k.Address, PublicKey = k.Pub, PrivateKey = "0" + k.Key.secret.ToString("x") });
            CasterMembershipRecord Record(long seq, long from, params int[] members) => new()
            {
                RecordSeq = seq, EffectiveFromHeight = from,
                Casters = members.Select(i => new CasterInfo { Address = c[i].Address, PeerIP = $"10.0.4.{i + 1}", PublicKey = c[i].Pub }).ToList(),
            };
            var records = new[] { Record(1, 0, 0, 1, 2, 3), Record(2, boundary - 2 * BaseSignerSetPolicy.EpochBlocks - 10, 0, 1, 2, 4) };
            foreach (var r in records) DbContext.DB_Config!.GetCollection<CasterMembershipRecord>("rsrv_caster_membership").Upsert(r.RecordSeq, r);
            VerifiedXCore.Services.CasterMembershipStore.InvalidateCache();
            BlockchainData.GetBlocks().InsertSafe(new Block { Height = boundary, Hash = $"b{boundary}-{Guid.NewGuid():N}", Transactions = new List<Transaction>() });

            var validators = new[] { _me, _seed2, _seed3 };
            BlockchainData.GetBlocks().InsertSafe(new Block
            {
                Height = boundary - 100, Hash = $"r{boundary}-{Guid.NewGuid():N}",
                Transactions = validators.Select((v, i) => new Transaction
                {
                    TransactionType = TransactionType.VBTC_V2_VALIDATOR_REGISTER, FromAddress = v.Address, ToAddress = v.Address, Hash = $"rr{i}",
                    Data = JsonConvert.SerializeObject(new { ValidatorAddress = v.Address, IPAddress = $"10.0.0.{i + 1}", FrostPublicKey = v.Pub, IsS3C = false }),
                }).ToList(),
            });
            var at = validators.Select((v, i) => (v, ip: $"10.0.0.{i + 1}")).Concat(c.Select((k, i) => (v: k, ip: $"10.0.4.{i + 1}"))).ToDictionary(x => x.ip, x => x.v);
            var priorFactory = Globals.HttpClientFactory;
            Globals.HttpClientFactory = new ValidatorNetwork(at);
            var sent = new List<(string Function, object[] Args)>();
            BaseValidatorSyncService.SubmitForTests = (f, a) => { sent.Add((f, a)); return Task.FromResult<string?>("0xtx"); };
            Globals.LastBlock = new Block { Height = boundary + BaseSignerSetPolicy.SettleBlocks + 8 * BaseValidatorSyncService.SubmitTurnBlocks, Timestamp = TimeUtil.GetTime() };
            try
            {
                Contract(9, validators, c.Take(4).ToArray());
                var digest = VbtcbTypedData.UpdateCastersDigest(new[] { c[4].Base }, new[] { c[3].Base }, boundary, 9, 8453, Proxy);
                Assert.Contains("Not the caster change", Refusal(await Ask("CASTERS", new[] { c[4].Base }, boundary, 9)));      // the removal left out
                Globals.ValidatorAddress = c[0].Address;
                SignedBy(c[0], await AskCasters(new[] { c[4].Base }, new[] { c[3].Base }, boundary, 9), digest);

                Globals.ValidatorAddress = _me.Address;
                await BaseValidatorSyncService.SyncOnceAsync();
                var (fn, args) = Assert.Single(sent);
                Assert.Equal("updateCasters", fn);
                Assert.Equal(new[] { c[4].Base }, (List<string>)args[0]);
                Assert.Equal(new[] { c[3].Base }, (List<string>)args[1]);
                var signers = ((List<byte[]>)args[3]).Select(b => ValidatorEthKeyService.RecoverSigner(digest, "0x" + Convert.ToHexString(b), personalMessage: false)!.ToLowerInvariant()).ToList();
                Assert.True(signers.Count(s => validators.Any(v => v.Base == s)) >= 2);
                Assert.True(signers.Count(s => c.Take(4).Any(k => k.Base == s)) >= 3);
                Assert.Equal(c[4].Base, ValidatorEthKeyService.RecoverSigner(digest, "0x" + Convert.ToHexString(((List<byte[]>)args[4]).Single()), personalMessage: false)!.ToLowerInvariant());
            }
            finally
            {
                foreach (var r in records) DbContext.DB_Config!.GetCollection<CasterMembershipRecord>("rsrv_caster_membership").Delete(r.RecordSeq);
                VerifiedXCore.Services.CasterMembershipStore.InvalidateCache();
                BaseValidatorSyncService.SubmitForTests = null;
                Globals.HttpClientFactory = priorFactory;
            }
        }

        private static async Task<(HttpStatusCode Code, JObject Body)> AskCasters(IEnumerable<string> add, IEnumerable<string> remove, long epoch, int nonce)
        {
            using var server = new TestServer(new WebHostBuilder().UseStartup<StartupP2PCaster>());
            var ctx = await server.SendAsync(x =>
            {
                x.Request.Method = "POST";
                x.Request.Path = "/valapi/validator/SignValidatorUpdate";
                x.Request.ContentType = "application/json";
                x.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new
                {
                    Action = "CASTERS", TargetAddresses = add.ToArray(), RemoveAddresses = remove.ToArray(), VfxBlockHeight = epoch, AdminNonce = nonce.ToString(),
                })));
                x.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");
            });
            using var reader = new StreamReader(ctx.Response.Body);
            var text = await reader.ReadToEndAsync();
            var json = text.Trim().StartsWith("\"") ? JsonConvert.DeserializeObject<string>(text)! : text;
            return ((HttpStatusCode)ctx.Response.StatusCode, JObject.Parse(json));
        }

        /// <summary>
        /// Fewer than a third of the validators leave in one epoch through the loop as it runs, not only in one batch: once
        /// a removal went through, the loop computes the next batch against the smaller set but nobody signs it until the
        /// next boundary.
        /// </summary>
        [Fact]
        public async Task OneRemovalAnEpoch_TheLoopDoesNotRemoveAgainAgainstTheSmallerSet()
        {
            var (s1, s2) = (NewKey(), NewKey());                                // on the contract, never seen on VFX
            var validators = new[] { _me, _seed2, _seed3, _new1, _new2, _unfunded, s1, s2 };   // 8: two may leave an epoch
            var at = new[] { _me, _seed2, _seed3, _new1, _new2, _unfunded }.Select((v, i) => (v, ip: $"10.0.0.{i + 1}")).ToDictionary(x => x.ip, x => x.v);
            var priorFactory = Globals.HttpClientFactory;
            Globals.HttpClientFactory = new ValidatorNetwork(at);
            var sent = new List<(string Function, object[] Args)>();
            BaseValidatorSyncService.SubmitForTests = (f, a) => { sent.Add((f, a)); return Task.FromResult<string?>("0xtx"); };
            Globals.LastBlock = new Block { Height = Epoch + BaseSignerSetPolicy.SettleBlocks + 8 * BaseValidatorSyncService.SubmitTurnBlocks, Timestamp = TimeUtil.GetTime() };
            var casters = new[] { _me, _seed2, _seed3 };
            try
            {
                Contract(11, validators, casters);
                await BaseValidatorSyncService.SyncOnceAsync();
                var (fn, args) = Assert.Single(sent);
                Assert.Equal("removeValidatorBatch", fn);
                var removed = (List<string>)args[0];
                Assert.Equal(2, removed.Count);                                   // of the three that should go

                // It went through. Against the six left the rule would remove the third now; it waits for the next epoch.
                sent.Clear();
                var left = validators.Where(v => !removed.Contains(v.Base)).ToArray();
                var third = BaseSignerSetPolicy.ForEpoch(Epoch, left.Select(v => v.Base))!.Removes;
                Assert.Single(third);
                Contract(12, left, casters);
                await BaseValidatorSyncService.SyncOnceAsync();
                Assert.Empty(sent);
                Globals.ValidatorAddress = _seed2.Address;
                Assert.Contains("already signed a removal", Refusal(await Ask("REMOVE", third, Epoch, 12)));
            }
            finally
            {
                BaseValidatorSyncService.SubmitForTests = null;
                Globals.HttpClientFactory = priorFactory;
            }
        }
    }
}
