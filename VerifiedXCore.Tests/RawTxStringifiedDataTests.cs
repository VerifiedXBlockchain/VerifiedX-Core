using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VerifiedXCore;
using VerifiedXCore.Controllers;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// The raw-transaction endpoints read Data as a JSON value and store its JSON text. A Data sent as a string that holds
    /// JSON ("Data": "{\"Function\": ...}") was encoded a second time, as a JSON string: the hash was made over that, and
    /// VerifyRawTransaction ended in the vBTC transfer's catch-all ("Failed to validate vBTC V2 transfer transaction.")
    /// with nothing to say why (integrator report, Nov 2026). It is now refused on every raw route with the reason; a
    /// plain TX keeps any Data as before, and Data sent as the object is read as it was.
    /// </summary>
    [Collection("DbContextSequential")]
    public class RawTxStringifiedDataTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly long _priorMultiHeight;
        private readonly (PrivateKey Key, string Pub, string Address) _sender = NewKey();
        private readonly (PrivateKey Key, string Pub, string Address) _receiver = NewKey();

        private const string MultiData =
            "{\"Function\":\"TransferVBTCMultiV2()\",\"FromAddress\":\"{from}\",\"ToAddress\":\"{to}\",\"TotalAmount\":0.0003,"
            + "\"Inputs\":[{\"SCUID\":\"a12047267e7f4feb998f5a929b863048:1795182210\",\"Amount\":0.0002},{\"SCUID\":\"1589893f50c84b9db0e2fbd163e8a909:1795182530\",\"Amount\":0.0001}]}";

        public RawTxStringifiedDataTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"rawstr_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            _priorLastBlock = Globals.LastBlock;
            _priorMultiHeight = Globals.V2TransferMultiHeight;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            MempoolEvictionUtility.InvalidateAdmissionStats();
            Globals.LastBlock = new Block { Height = 100 };
            Globals.V2TransferMultiHeight = 1; // as on testnet
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _sender.Address, Balance = 100M, Nonce = 0 });
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.LastBlock = _priorLastBlock;
            Globals.CustomPath = _priorCustomPath;
            Globals.V2TransferMultiHeight = _priorMultiHeight;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static (PrivateKey, string, string) NewKey()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            return (key, pub, AccountData.GetHumanAddress(pub));
        }

        /// <summary>A signed multi-vault transfer, its Data what GetRawTransferVBTCMultiData returns.</summary>
        private Transaction SignedMultiTransfer()
        {
            var tx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(),
                FromAddress = _sender.Address,
                ToAddress = _receiver.Address,
                Amount = 0.0M, // as the raw builder writes it (the hash reads its spelling)
                Fee = 0.0002M,
                Nonce = 0,
                TransactionType = TransactionType.VBTC_V2_TRANSFER,
                Data = MultiData.Replace("{from}", _sender.Address).Replace("{to}", _receiver.Address),
            };
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, _sender.Key, _sender.Pub);
            return tx;
        }

        /// <summary>The request body with Data as the string the builder returned (stringified) or as the object.</summary>
        private static JObject Body(Transaction tx, bool stringified)
        {
            var body = JObject.Parse(JsonConvert.SerializeObject(tx));
            body["Data"] = stringified ? new JValue(tx.Data) : JToken.Parse(tx.Data!);
            return body;
        }

        [Fact]
        public async Task EveryRawRoute_RefusesStringifiedData_WithTheReason()
        {
            var tx = SignedMultiTransfer();
            var api = new TXV1Controller();
            var answers = new[]
            {
                await api.GetRawTxFee(Body(tx, stringified: true)),
                await api.GetTxHash(Body(tx, stringified: true)),
                await api.VerifyRawTransaction(Body(tx, stringified: true)),
                await api.SendRawTransaction(Body(tx, stringified: true)),
            };

            foreach (var answer in answers.Select(JObject.Parse))
            {
                Assert.Equal("Fail", (string?)answer["Result"]);
                Assert.Equal(TXV1Controller.StringifiedDataError, (string?)answer["Message"]);
            }
            Assert.Empty(TransactionData.GetPool().FindAll());
        }

        [Fact]
        public async Task DataSentAsTheObject_HashesAsBefore_AndIsJudgedByTheTransfersOwnRules()
        {
            var tx = SignedMultiTransfer();
            var api = new TXV1Controller();

            var hash = JObject.Parse(await api.GetTxHash(Body(tx, stringified: false)));
            Assert.Equal(tx.Hash, (string?)hash["Hash"]);

            // The vaults do not exist here: the transfer's own rule says so, not the catch-all.
            var verified = JObject.Parse(await api.VerifyRawTransaction(Body(tx, stringified: false)));
            Assert.Equal("Fail", (string?)verified["Result"]);
            var message = (string)verified["Message"]!;
            Assert.Contains("vBTC V2 contract not found in state trei", message);
            Assert.DoesNotContain("Failed to validate vBTC V2 transfer transaction", message);
        }

        [Fact]
        public async Task APlainTransfer_KeepsAStringDataAsBefore()
        {
            // A TX's Data is not read as a call: a string holding JSON is still encoded as its JSON text, as before.
            var memo = "{\"memo\":\"hello\"}";
            var tx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(),
                FromAddress = _sender.Address,
                ToAddress = _receiver.Address,
                Amount = 1M.ToNormalizeDecimal(),
                Fee = 0.00001M,
                Nonce = 0,
                TransactionType = TransactionType.TX,
                Data = JsonConvert.SerializeObject(memo), // what the route made of it
            };
            tx.Build();
            var body = JObject.Parse(JsonConvert.SerializeObject(tx));
            body["Data"] = new JValue(memo);

            var hash = JObject.Parse(await new TXV1Controller().GetTxHash(body));

            Assert.Equal("Success", (string?)hash["Result"]);
            Assert.Equal(tx.Hash, (string?)hash["Hash"]);
        }

        [Fact]
        public async Task AStringThatIsNotJson_IsEncodedAsBefore()
        {
            var tx = SignedMultiTransfer();
            tx.Data = JsonConvert.SerializeObject("not json");
            tx.Build();
            var body = JObject.Parse(JsonConvert.SerializeObject(tx));
            body["Data"] = new JValue("not json");

            var hash = JObject.Parse(await new TXV1Controller().GetTxHash(body));

            Assert.Equal(tx.Hash, (string?)hash["Hash"]);
        }
    }
}
