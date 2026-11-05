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
    /// The raw-transaction endpoints treat "Data": null as no data, the same as a missing Data: the hash covers empty
    /// Data (what an external signer computes) and the stored transaction has no Data, not the text "null".
    /// </summary>
    [Collection("DbContextSequential")]
    public class RawTxNullDataTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly (PrivateKey Key, string Pub, string Address) _sender = NewKey();
        private readonly (PrivateKey Key, string Pub, string Address) _receiver = NewKey();

        public RawTxNullDataTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"rawnull_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            _priorLastBlock = Globals.LastBlock;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            MempoolEvictionUtility.InvalidateAdmissionStats();
            Globals.LastBlock = new Block { Height = 100 };
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _sender.Address, Balance = 100M, Nonce = 0 });
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.LastBlock = _priorLastBlock;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static (PrivateKey, string, string) NewKey()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            return (key, pub, AccountData.GetHumanAddress(pub));
        }

        /// <summary>Signed the way an external signer does it: the hash of the transaction with empty Data.</summary>
        private Transaction SignedWithoutData()
        {
            var tx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(),
                FromAddress = _sender.Address,
                ToAddress = _receiver.Address,
                Amount = 1M.ToNormalizeDecimal(),
                Fee = 0,
                Nonce = 0,
                TransactionType = TransactionType.TX,
                Data = null,
            };
            tx.Fee = 0.00001M; // above the fee floor; written without trailing zeros, which the raw parse drops
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, _sender.Key, _sender.Pub);
            return tx;
        }

        /// <summary>The request body with "Data": null, or with no Data property at all.</summary>
        private static JObject Body(Transaction tx, bool omitData)
        {
            var body = JObject.Parse(JsonConvert.SerializeObject(tx));
            if (omitData) body.Remove("Data");
            else body["Data"] = JValue.CreateNull();
            return body;
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task GetTxHash_NullOrMissingData_HashesAsEmpty(bool omitData)
        {
            var tx = SignedWithoutData();

            var result = JObject.Parse(await new TXV1Controller().GetTxHash(Body(tx, omitData)));

            Assert.Equal("Success", (string?)result["Result"]);
            Assert.Equal(tx.Hash, (string?)result["Hash"]);
        }

        [Fact]
        public async Task GetRawTxFee_NullData_SameFeeAsMissingData()
        {
            var tx = SignedWithoutData();

            var withNull = JObject.Parse(await new TXV1Controller().GetRawTxFee(Body(tx, omitData: false)));
            var missing = JObject.Parse(await new TXV1Controller().GetRawTxFee(Body(tx, omitData: true)));

            Assert.Equal("Success", (string?)withNull["Result"]);
            Assert.Equal((decimal)missing["Fee"]!, (decimal)withNull["Fee"]!);
        }

        [Fact]
        public async Task VerifyRawTransaction_NullData_AcceptsTheSignatureOverEmptyData()
        {
            var tx = SignedWithoutData();

            var result = JObject.Parse(await new TXV1Controller().VerifyRawTransaction(Body(tx, omitData: false)));

            Assert.True((string?)result["Result"] == "Success", result.ToString(Formatting.None));
            Assert.Equal(tx.Hash, (string?)result["Hash"]);
        }

        [Fact]
        public async Task SendRawTransaction_NullData_StoresNoData()
        {
            var tx = SignedWithoutData();

            var result = JObject.Parse(await new TXV1Controller().SendRawTransaction(Body(tx, omitData: false)));

            Assert.True((string?)result["Result"] == "Success", result.ToString(Formatting.None));
            var pooled = TransactionData.GetPool().FindAll().Single();
            Assert.Equal(tx.Hash, pooled.Hash);
            Assert.Null(pooled.Data);
        }

        [Fact]
        public async Task GetTxHash_JsonData_IsStillItsJsonText()
        {
            var tx = SignedWithoutData();
            tx.Data = "[\"memo\"]";
            tx.Build();
            var body = Body(tx, omitData: true);
            body["Data"] = JToken.Parse(tx.Data);

            var result = JObject.Parse(await new TXV1Controller().GetTxHash(body));

            Assert.Equal(tx.Hash, (string?)result["Hash"]);
        }
    }
}
