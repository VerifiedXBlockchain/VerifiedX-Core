using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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
    /// A validator's signature on a change to the vBTCb contract's signers, and on the cancellation of a pending removal,
    /// rests on what the contract holds: its validators, its admin nonce, its pending removal. Those are taken only as
    /// two Base providers give them alike. Every provider here serves this network's Base chain; one does not tell the
    /// truth about the contract. Asked through SignValidatorUpdate as the node serves it, with the providers on loopback.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BaseSignerAgreementTests : BaseProviderTestBase
    {
        private const long Epoch = 2 * BaseSignerSetPolicy.EpochBlocks;
        private readonly string EpochHash = $"epoch-{Guid.NewGuid():N}";   // the registry caches a scan by (height, block hash)

        private sealed record V(PrivateKey Key, string Pub, string Address, string Base);

        private readonly bool _priorSynced = Globals.IsChainSynced;
        private readonly bool _priorResyncing = Globals.IsResyncing;
        private readonly bool _priorStopTimers = Globals.StopAllTimers;
        private readonly long _priorHeight = Globals.BaseBridgeV4Height;

        // Registered on VFX and seen: this node and two others. One more left VFX.
        private readonly V _me = NewKey(), _seed2 = NewKey(), _seed3 = NewKey(), _gone = NewKey();

        public BaseSignerAgreementTests()
        {
            var registered = new[] { _me, _seed2, _seed3 };
            foreach (var v in registered.Append(_gone))
                AccountData.GetAccounts().InsertSafe(new Account { Address = v.Address, PublicKey = v.Pub, PrivateKey = "0" + v.Key.secret.ToString("x") });
            foreach (var v in registered)
                StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = v.Address, Balance = 5_000M, Nonce = 0 });
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
            VBTCValidatorRegistry.InvalidateCache();
            ForgetWhatWasRead();
        }

        public override void Dispose()
        {
            ForgetWhatWasRead();
            Globals.BaseBridgeV4Height = _priorHeight;
            Globals.IsChainSynced = _priorSynced;
            Globals.IsResyncing = _priorResyncing;
            Globals.StopAllTimers = _priorStopTimers;
            VBTCValidatorRegistry.InvalidateCache();
            base.Dispose();
        }

        /// <summary>The signing route keeps what it read of the contract for a few seconds: forgotten, so the next request reads it again.</summary>
        private static void ForgetWhatWasRead()
        {
            BaseValidatorSyncService.SignerViewCache = null;
            BaseValidatorSyncService.PendingCache = null;
        }

        private static V NewKey()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            return new V(key, pub, AccountData.GetHumanAddress(pub), ValidatorEthKeyService.DeriveBaseAddressFromVfxPublicKey(pub).ToLowerInvariant());
        }

        /// <summary>
        /// A provider serving a version 4 contract with these validators (the same keys as its casters), this admin nonce,
        /// and a pending removal of <paramref name="pendingRemoval"/> when given.
        /// </summary>
        private FakeBaseRpc Serving(int nonce, V[] on, V[]? pendingRemoval = null)
        {
            var p = Provider();
            var members = on.Select(v => v.Base).ToList();
            var removing = (pendingRemoval ?? Array.Empty<V>()).Select(v => v.Base).ToList();
            var views = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [Selector("getValidators()")] = Encoded("address[]", members),
                [Selector("requiredMintSignatures()")] = Encoded("uint256", new BigInteger(Math.Max(2, (members.Count * 2 + 2) / 3))),
                [Selector("adminNonce()")] = Encoded("uint256", new BigInteger(nonce)),
                [Selector("MESSAGE_VERSION()")] = Encoded("uint256", new BigInteger(4)),
                [Selector("getCasters()")] = Encoded("address[]", members),
                [Selector("requiredCasterSignatures()")] = Encoded("uint256", new BigInteger(members.Count / 2 + 1)),
                [Selector("pendingRemovalEta()")] = Encoded("uint256", new BigInteger(removing.Count == 0 ? 0 : 1_900_000_000)),
                [Selector("getPendingRemovals()")] = Encoded("address[]", removing),
                [Selector("getPendingCasterRemovals()")] = Encoded("address[]", new List<string>()),
            };
            p.On = (method, args) =>
            {
                if (method != "eth_call") return null;
                var call = (JObject)args[0]!;
                var data = (call["data"] ?? call["input"])!.ToString()[..10];
                return views.TryGetValue(data, out var answer) ? answer : null;
            };
            return p;
        }

        private static async Task<(HttpStatusCode Code, JObject Body)> Ask(string action, IEnumerable<string> targets, int nonce)
        {
            ForgetWhatWasRead();
            using var server = new TestServer(new WebHostBuilder().UseStartup<StartupP2PCaster>());
            var ctx = await server.SendAsync(c =>
            {
                c.Request.Method = "POST";
                c.Request.Path = "/valapi/validator/SignValidatorUpdate";
                c.Request.ContentType = "application/json";
                c.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new
                {
                    Action = action, TargetAddresses = targets.ToArray(), VfxBlockHeight = Epoch, AdminNonce = nonce.ToString(),
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
            Assert.True(answer.Code == HttpStatusCode.Forbidden, answer.Body.ToString());
            Assert.Null(answer.Body["Signature"]?.Value<string>());
            return answer.Body.Value<string>("Message")!;
        }

        private static void SignedBy(V signer, (HttpStatusCode Code, JObject Body) answer, byte[] digest)
        {
            Assert.True(answer.Code == HttpStatusCode.OK, answer.Body.ToString());
            Assert.Equal(signer.Base, ValidatorEthKeyService.RecoverSigner(digest, answer.Body.Value<string>("Signature"), personalMessage: false), StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// One provider says a validator that left VFX is on the contract, which would have this node sign its removal;
        /// the contract does not hold it. No removal is signed on that provider's word.
        /// </summary>
        [Fact]
        public async Task OneProviderMisstatingTheContractsValidators_GetsNoSignatureOnWhatItSays()
        {
            var contract = new[] { _me, _seed2, _seed3 };
            var misstating = Serving(7, contract.Append(_gone).ToArray());
            var first = Serving(7, contract);
            var second = Serving(7, contract);

            // Beside one other provider the contract is not read at all, whichever of the two is asked first.
            foreach (var order in new[] { new[] { misstating, first }, new[] { first, misstating } })
            {
                Configure(order);
                Assert.Equal("Could not read the Base contract", Refusal(await Ask("REMOVE", new[] { _gone.Base }, 7)));
            }

            // Beside two that agree, the contract is what those two give: there is no such removal to sign.
            Configure(misstating, first, second);
            Assert.Contains("Not the removal", Refusal(await Ask("REMOVE", new[] { _gone.Base }, 7)));
        }

        /// <summary>The other way round: a provider that leaves a departed validator out does not keep its removal from being signed.</summary>
        [Fact]
        public async Task OneProviderLeavingAValidatorOut_DoesNotStopTheRemovalTwoOthersShow()
        {
            var contract = new[] { _me, _seed2, _seed3, _gone };
            Configure(Serving(7, contract.Take(3).ToArray()), Serving(7, contract), Serving(7, contract));

            SignedBy(_me, await Ask("REMOVE", new[] { _gone.Base }, 7),
                VbtcbTypedData.RemoveValidatorsDigest(new[] { _gone.Base }, Epoch, 7, BaseBridgeService.BaseChainId, Contract));
        }

        /// <summary>
        /// One provider says the contract has a pending removal that names a validator still on VFX, which a member
        /// answers with its signature on cancelling what is pending; the contract has none. No cancellation is signed on
        /// that provider's word.
        /// </summary>
        [Fact]
        public async Task OneProviderInventingAPendingRemoval_GetsNoCancellationSigned()
        {
            var contract = new[] { _me, _seed2, _seed3 };
            var inventing = Serving(14, contract, pendingRemoval: new[] { _seed2 });
            var first = Serving(14, contract);
            var second = Serving(14, contract);

            foreach (var order in new[] { new[] { inventing, first }, new[] { first, inventing } })
            {
                Configure(order);
                Assert.Equal("Could not read the Base contract", Refusal(await Ask("CANCEL", Array.Empty<string>(), 14)));
            }

            Configure(inventing, first, second);
            Assert.Contains("No pending removal names", Refusal(await Ask("CANCEL", Array.Empty<string>(), 14)));
        }

        /// <summary>The other way round: a provider that hides a pending removal does not keep its cancellation from being signed.</summary>
        [Fact]
        public async Task OneProviderHidingAPendingRemoval_DoesNotStopItsCancellation()
        {
            var contract = new[] { _me, _seed2, _seed3 };
            Configure(Serving(14, contract), Serving(14, contract, pendingRemoval: new[] { _seed2 }), Serving(14, contract, pendingRemoval: new[] { _seed2 }));

            SignedBy(_me, await Ask("CANCEL", Array.Empty<string>(), 14),
                VbtcbTypedData.CancelPendingDigest(14, BaseBridgeService.BaseChainId, Contract));
        }
    }
}
