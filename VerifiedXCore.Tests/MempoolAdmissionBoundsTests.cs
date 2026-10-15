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
    /// Every path that puts a transaction into the mempool applies the same admission bounds (fee floor, entry and size
    /// caps), and the node rates a submitted transaction itself.
    /// </summary>
    [Collection("DbContextSequential")]
    public class MempoolAdmissionBoundsTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
                private readonly (PrivateKey Key, string Pub, string Address) _sender = NewKey();
        private readonly (PrivateKey Key, string Pub, string Address) _receiver = NewKey();

        public MempoolAdmissionBoundsTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"mab_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
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

        private Transaction Send(decimal amount, TransactionRating? claimedRating = null, string data = "[\"memo\"]", decimal? fee = null)
        {
            var tx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(),
                FromAddress = _sender.Address,
                ToAddress = _receiver.Address,
                Amount = amount.ToNormalizeDecimal(),
                Fee = 0,
                Nonce = 0,
                TransactionType = TransactionType.TX,
                Data = data,
            };
            tx.Fee = fee ?? FeeCalcService.CalculateTXFee(tx);
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, _sender.Key, _sender.Pub);
            tx.TransactionRating = claimedRating;
            return tx;
        }

        /// <summary>Submits the way a wallet does: Data as a JSON value, which the controller re-serialises.</summary>
        private static async Task<JObject> Submit(Transaction tx)
        {
            var body = JObject.Parse(JsonConvert.SerializeObject(tx));
            body["Data"] = JToken.Parse(tx.Data!);
            return JObject.Parse(await new TXV1Controller().SendRawTransaction(body));
        }

        private static int PoolCount() => TransactionData.GetPool().Count();

        /// <summary>A 2.6 KB transfer whose fee clears VerifyTX's flat minimum but not the mempool's per-KB floor.</summary>
        private Transaction UnderFloor() => Send(1M, data: JsonConvert.SerializeObject(new[] { new string('a', 2048) }), fee: 0.0000031M);

        [Fact]
        public async Task SendRawTransaction_RefusesAFeeBelowTheMempoolFloor()
        {
            var tx = UnderFloor();
            Assert.True((await TransactionValidatorService.VerifyTX(tx)).Item1);

            var result = await Submit(tx);
            Assert.Equal("Fail", (string?)result["Result"]);
            Assert.Equal(0, PoolCount());
        }

        [Fact]
        public async Task SendRawTransaction_IgnoresARatingSuppliedByTheCaller()
        {
            var result = await Submit(Send(1M, claimedRating: TransactionRating.A));
            Assert.True((string?)result["Result"] == "Success", result.ToString(Formatting.None));
            var stored = TransactionData.GetPool().FindAll().Single();
            Assert.Equal(await TransactionRatingService.GetTransactionRating(stored), stored.TransactionRating);
        }

        [Fact]
        public async Task SendRawTransaction_AdmitsANormalTransaction()
        {
            var result = await Submit(Send(1M));
            Assert.True((string?)result["Result"] == "Success", result.ToString(Formatting.None));
            Assert.Equal(1, PoolCount());
        }

        [Fact]
        public void CheckAdmissionBounds_RefusesAFeeBelowTheFloor_AndAdmitsANormalFee()
        {
            Assert.False(MempoolEvictionUtility.CheckAdmissionBounds(UnderFloor()).admit);
            Assert.True(MempoolEvictionUtility.CheckAdmissionBounds(Send(1M)).admit);
        }

        private static void Pool(string hash, TransactionRating rating, long timestamp) =>
            TransactionData.GetPool().Insert(new Transaction { Hash = hash, FromAddress = "x", ToAddress = "y", Amount = 1M, Fee = 1M, Timestamp = timestamp, TransactionType = TransactionType.TX, TransactionRating = rating });

        private static string[] Pooled() => TransactionData.GetPool().FindAll().Select(x => x.Hash).OrderBy(x => x).ToArray();

        [Fact]
        public void EvictingForAnIncomingTx_RemovesOnlyEntriesRatedBelowIt()
        {
            Pool("a-old", TransactionRating.A, 1);
            Pool("c-old", TransactionRating.C, 1);
            Pool("c-new", TransactionRating.C, 2);

            var evicted = MempoolEvictionUtility.EvictLowestPriority(targetCount: 1, onlyRatedBelow: TransactionRating.B);
            Assert.Equal(new[] { "c-new", "c-old" }, evicted.OrderBy(x => x).ToArray());
            Assert.Equal(new[] { "a-old" }, Pooled());
        }

        [Fact]
        public void EvictingForAnIncomingTx_NeverRemovesAnEntryRatedAsWell()
        {
            Pool("c-old", TransactionRating.C, 1);
            Pool("c-new", TransactionRating.C, 2);

            Assert.Empty(MempoolEvictionUtility.EvictLowestPriority(targetCount: 1, onlyRatedBelow: TransactionRating.C));
            Assert.Equal(2, PoolCount());
        }

        [Fact]
        public void WithinOneRating_TheNewestEntryIsEvictedFirst()
        {
            Pool("c-old", TransactionRating.C, 1);
            Pool("c-new", TransactionRating.C, 2);

            Assert.Equal(new[] { "c-new" }, MempoolEvictionUtility.EvictLowestPriority(targetCount: 1));
            Assert.Equal(new[] { "c-old" }, Pooled());
        }

        [Fact]
        public void AdmissionStats_CountIsExact_EvenWhileTheSizeIsCached()
        {
            var before = MempoolEvictionUtility.GetAdmissionStats();
            Pool("one", TransactionRating.C, 1);
            Pool("two", TransactionRating.C, 2);
            var after = MempoolEvictionUtility.GetAdmissionStats();
            Assert.Equal(before.count + 2, after.count);

            MempoolEvictionUtility.InvalidateAdmissionStats();
            Assert.Equal(MempoolEvictionUtility.GetMempoolStats(), MempoolEvictionUtility.GetAdmissionStats());
        }
    }
}
