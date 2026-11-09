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

        private static void Contract(int nonce, params V[] on)
        {
            BaseValidatorSyncService.SignerViewCache = null;                        // the contract changed: read it again
            BaseBridgeService.ReadSignerViewForTests = () => Task.FromResult<BaseBridgeService.SignerView?>(
                new BaseBridgeService.SignerView(on.Select(v => v.Base).ToList(), 4, Math.Max(2, (on.Length * 2 + 2) / 3), nonce));
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
            Assert.Equal(contract.Skip(3).OrderBy(a => BaseSignerSetPolicy.RankOf(EpochHash, a), StringComparer.Ordinal), plan.Removes);
            Assert.Empty(plan.Adds);                                               // 3 left: room for one, nobody new

            var two = BaseSignerSetPolicy.Compute(Epoch, EpochHash, contract.Take(1), contract.Take(3));
            Assert.Empty(two.Removes);                                             // would leave fewer than 3
            var four = BaseSignerSetPolicy.Compute(Epoch, EpochHash, contract.Take(1), contract.Take(4));
            Assert.Single(four.Removes);                                           // down to 3, no further
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
            Assert.Contains("Only a validator on the contract, or one being added", Refusal(await Ask("ADD", new[] { first }, Epoch, 8)));
            Assert.Contains("Only a validator on the contract signs a removal", Refusal(await Ask("REMOVE", new[] { _seed3.Base }, Epoch, 8)));

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
                return Task.FromResult<BaseBridgeService.SignerView?>(new BaseBridgeService.SignerView(new[] { _me.Base, _seed2.Base, _seed3.Base }, 4, 2, 8));
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
    }
}
