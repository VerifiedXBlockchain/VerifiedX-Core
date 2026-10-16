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
        public void WithinOneRating_TheLatestArrivalIsEvictedFirst_WhateverItsTimestamp()
        {
            Pool("waiting", TransactionRating.C, timestamp: 2000);
            Pool("backdated", TransactionRating.C, timestamp: 1); // claims to be older than everything
            MempoolEvictionUtility.RecordArrivalForTests("waiting", DateTime.UtcNow.AddMinutes(-5));
            MempoolEvictionUtility.RecordArrivalForTests("backdated", DateTime.UtcNow);

            Assert.Equal(new[] { "backdated" }, MempoolEvictionUtility.EvictLowestPriority(targetCount: 1));
            Assert.Equal(new[] { "waiting" }, Pooled());
        }

        [Fact]
        public void AnAdmittedTransaction_IsInsertedAndCountedTogether_ARefusedOneNeither()
        {
            var before = MempoolEvictionUtility.GetAdmissionStats().sizeBytes;
            var tx = Send(1M);
            Assert.True(MempoolEvictionUtility.TryAdmitAndInsert(tx, TransactionData.GetPool()));
            // Still inside the cache window: the inserted bytes are already counted.
            var afterAdmit = MempoolEvictionUtility.GetAdmissionStats();
            Assert.Equal(before + MempoolEvictionUtility.CalculateTransactionSize(tx), afterAdmit.sizeBytes);
            Assert.Equal(1, PoolCount());

            Assert.False(MempoolEvictionUtility.TryAdmitAndInsert(UnderFloor(), TransactionData.GetPool()));
            Assert.Equal(afterAdmit.sizeBytes, MempoolEvictionUtility.GetAdmissionStats().sizeBytes);
            Assert.Equal(1, PoolCount());
        }

        [Fact]
        public void ASecondDeliveryOfAPooledTransaction_IsAlreadyAdmitted_AndTheFirstCopyStays()
        {
            var tx = Send(1M);
            Assert.True(MempoolEvictionUtility.TryAdmitAndInsert(tx, TransactionData.GetPool()));
            var counted = MempoolEvictionUtility.GetAdmissionStats().sizeBytes;

            // Two handlers can both pass their "not pooled" check; the later one reaches admission with the same transaction.
            var again = JsonConvert.DeserializeObject<Transaction>(JsonConvert.SerializeObject(tx))!;
            Assert.True(MempoolEvictionUtility.TryAdmitAndInsert(again, TransactionData.GetPool(), out var reason));
            Assert.Equal("", reason);

            Assert.Equal(1, PoolCount());
            Assert.Equal(tx.Hash, TransactionData.GetPool().FindAll().Single().Hash);
            Assert.Equal(counted, MempoolEvictionUtility.GetAdmissionStats().sizeBytes); // not counted twice
        }

        [Fact]
        public void ACopyWithTheSameHashButAnotherSignature_IsRefused_AndThePooledOneStays()
        {
            var tx = Send(1M);
            Assert.True(MempoolEvictionUtility.TryAdmitAndInsert(tx, TransactionData.GetPool()));

            var forged = JsonConvert.DeserializeObject<Transaction>(JsonConvert.SerializeObject(tx))!;
            forged.Signature = "not-the-signature";
            Assert.False(MempoolEvictionUtility.TryAdmitAndInsert(forged, TransactionData.GetPool()));

            var recased = JsonConvert.DeserializeObject<Transaction>(JsonConvert.SerializeObject(tx))!;
            recased.Hash = Flip(tx.Hash);
            Assert.False(MempoolEvictionUtility.TryAdmitAndInsert(recased, TransactionData.GetPool()));

            Assert.Equal(tx.Signature, TransactionData.GetPool().FindAll().Single().Signature);
        }

        [Fact]
        public void ARefusedCopyThatDiffersFromThePooledTransaction_RemovesNothing()
        {
            var tx = Send(1M);
            Assert.True(MempoolEvictionUtility.TryAdmitAndInsert(tx, TransactionData.GetPool()));

            // Same hash and signature, other content: it fails validation, and its refusal is not the pooled transaction's.
            var altered = JsonConvert.DeserializeObject<Transaction>(JsonConvert.SerializeObject(tx))!;
            altered.Amount = 2M;
            Assert.Equal(0, TransactionData.RemoveRefusedFromPool(TransactionData.GetPool(), altered));

            Assert.Equal(tx.Hash, TransactionData.GetPool().FindAll().Single().Hash);
        }

        [Fact]
        public void ARefusedCopyWithTheSameConcatenatedFields_SplitDifferently_RemovesNothing()
        {
            var tx = Send(1M, data: null!);
            tx.UnlockTime = 1792235000;
            Assert.True(MempoolEvictionUtility.TryAdmitAndInsert(tx, TransactionData.GetPool()));

            var resplit = JsonConvert.DeserializeObject<Transaction>(JsonConvert.SerializeObject(tx))!;
            resplit.Data = "1";
            resplit.UnlockTime = 792235000;
            Assert.Equal(tx.GetHashPreimage(), resplit.GetHashPreimage());
            Assert.Equal(0, TransactionData.RemoveRefusedFromPool(TransactionData.GetPool(), resplit));

            Assert.Equal(1, PoolCount());
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ARefusedCopyWhoseSignatureOrHashDiffersOnlyInCase_RemovesNothing(bool signature)
        {
            var tx = Send(1M);
            Assert.True(MempoolEvictionUtility.TryAdmitAndInsert(tx, TransactionData.GetPool()));

            var recased = JsonConvert.DeserializeObject<Transaction>(JsonConvert.SerializeObject(tx))!;
            if (signature) recased.Signature = Flip(tx.Signature);
            else recased.Hash = Flip(tx.Hash);
            Assert.Equal(0, TransactionData.RemoveRefusedFromPool(TransactionData.GetPool(), recased));

            Assert.Equal(1, PoolCount());
        }

        private static string Flip(string s) => new string(s.Select(c => char.IsUpper(c) ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c)).ToArray());

        [Fact]
        public void ARefusedCopyIdenticalToThePooledTransaction_RemovesIt()
        {
            var tx = Send(1M);
            Assert.True(MempoolEvictionUtility.TryAdmitAndInsert(tx, TransactionData.GetPool()));

            var same = JsonConvert.DeserializeObject<Transaction>(JsonConvert.SerializeObject(tx))!;
            Assert.Equal(1, TransactionData.RemoveRefusedFromPool(TransactionData.GetPool(), same));
            Assert.Equal(0, PoolCount());
        }

        [Fact]
        public void EvictionThatCannotMakeRoom_EvictsNothing()
        {
            Pool("c-old", TransactionRating.C, 1);
            Pool("c-new", TransactionRating.C, 2);

            var evicted = MempoolEvictionUtility.EvictLowestPriority(1, -1, TransactionRating.A, null, out var freed,
                mustFitBytes: Globals.MaxMempoolSizeBytes + 1);
            Assert.Empty(evicted);
            Assert.Equal(0, freed);
            Assert.Equal(2, PoolCount());
        }

        [Fact]
        public void AFullPoolWithNothingRatedBelow_RefusesAndStopsScanningForAWhile()
        {
            var pool = TransactionData.GetPool();
            pool.InsertBulk(Enumerable.Range(0, Globals.MaxMempoolEntries).Select(i => new Transaction
            {
                Hash = $"a{i}", FromAddress = "x", ToAddress = "y", Amount = 1M, Fee = 1M, Timestamp = i,
                TransactionType = TransactionType.TX, TransactionRating = TransactionRating.A,
            }));
            MempoolEvictionUtility.InvalidateAdmissionStats();
            var tx = Send(1M);
            tx.TransactionRating = TransactionRating.C;

            Assert.False(MempoolEvictionUtility.CheckAdmissionBounds(tx).admit);
            Assert.Equal(Globals.MaxMempoolEntries, PoolCount()); // nothing rated as well or better was evicted
            Assert.True(MempoolEvictionUtility.RefusingWithoutScanFor(TransactionRating.C));
            Assert.True(MempoolEvictionUtility.RefusingWithoutScanFor(TransactionRating.D));
            Assert.False(MempoolEvictionUtility.RefusingWithoutScanFor(TransactionRating.B)); // a better rating still gets a look
            Assert.False(MempoolEvictionUtility.CheckAdmissionBounds(tx).admit);
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

        private static Transaction OfType(TransactionType type, decimal fee) => new Transaction
        {
            Hash = Guid.NewGuid().ToString("N"), FromAddress = "x", ToAddress = "y", Amount = 0M, Fee = fee, Timestamp = 1,
            TransactionType = type, Data = JsonConvert.SerializeObject(new[] { new string('d', 4096) }),
        };

        [Theory]
        [InlineData(TransactionType.FTKN_TX)]
        [InlineData(TransactionType.NFT_TX)]
        [InlineData(TransactionType.SC_MINT)]
        [InlineData(TransactionType.VBTC_V2_TRANSFER)]
        public void TheFeeFloor_AppliesToTypesWithoutAFixedFeeOfTheirOwn(TransactionType type)
        {
            Assert.False(MempoolEvictionUtility.ValidateMinimumFee(OfType(type, 0.000003M)).isValid);
            var paid = OfType(type, 0M);
            paid.Fee = FeeCalcService.CalculateTXFee(paid);
            Assert.True(MempoolEvictionUtility.ValidateMinimumFee(paid).isValid);
        }

        [Theory]
        [InlineData(TransactionType.VBTC_V2_VALIDATOR_HEARTBEAT)]
        [InlineData(TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC)]
        [InlineData(TransactionType.TKNZ_WD_ARB)]
        [InlineData(TransactionType.ADNR)]
        [InlineData(TransactionType.VOTE_TOPIC)]
        [InlineData(TransactionType.DSTR)]
        [InlineData(TransactionType.VFX_PRIVATE_TRANSFER)]
        [InlineData(TransactionType.VBTC_V2_BTC_REPORT)]
        public void TheFeeFloor_LeavesFeeFreeAndFixedFeeTypesAlone(TransactionType type)
        {
            Assert.True(MempoolEvictionUtility.ValidateMinimumFee(OfType(type, 0M)).isValid);
        }

        [Fact]
        public void Eviction_RemovesUntilBothTargetsAreMet_AsPlanned()
        {
            // Three entries; the count target alone is met after one eviction, the size target only after all three.
            Pool("c1", TransactionRating.C, 1);
            Pool("c2", TransactionRating.C, 2);
            Pool("c3", TransactionRating.C, 3);
            var total = TransactionData.GetPool().FindAll().Sum(MempoolEvictionUtility.CalculateTransactionSize);

            var evicted = MempoolEvictionUtility.EvictLowestPriority(targetCount: 2, targetSize: 1);
            Assert.Equal(3, evicted.Count);
            Assert.Equal(0, PoolCount());
            Assert.True(total > 1);
        }

        [Fact]
        public void TransferRating_ReadsTheSendersRowsFromThePool_AndStillFailsAFlood()
        {
            for (var i = 0; i < 60; i++)
                TransactionData.GetPool().Insert(new Transaction { Hash = $"f{i}", FromAddress = _sender.Address, ToAddress = "y", Amount = 1M, Fee = 1M, Timestamp = i, TransactionType = TransactionType.TX, TransactionRating = TransactionRating.A });
            var rating = TransactionRatingService.GetTransactionRating(Send(1M)).GetAwaiter().GetResult();
            Assert.Equal(TransactionRating.F, rating);
            Assert.All(TransactionData.GetPool().Find(x => x.FromAddress == _sender.Address), x => Assert.Equal(TransactionRating.E, x.TransactionRating));
        }

        [Fact]
        public void AnInsertThatFails_EvictsNothing()
        {
            var pool = TransactionData.GetPool();
            pool.InsertBulk(Enumerable.Range(0, Globals.MaxMempoolEntries).Select(i => new Transaction
            {
                Hash = $"d{i}", FromAddress = "x", ToAddress = "y", Amount = 1M, Fee = 1M, Timestamp = i,
                TransactionType = TransactionType.TX, TransactionRating = TransactionRating.D,
            }));
            MempoolEvictionUtility.InvalidateAdmissionStats();

            var tx = Send(1M);
            tx.TransactionRating = TransactionRating.A;     // outranks every pooled entry, so room would be made for it
            tx.Id = pool.FindAll().First().Id;              // an id already in the pool: the insert fails

            Assert.False(MempoolEvictionUtility.TryAdmitAndInsert(tx, pool, out var reason));
            Assert.Contains("insert failed", reason);
            Assert.Equal(Globals.MaxMempoolEntries, PoolCount());

            tx.Id = null;                                   // the same transaction with a fresh id is admitted, making room
            Assert.True(MempoolEvictionUtility.TryAdmitAndInsert(tx, pool, out var e2), e2);
            Assert.True(PoolCount() < Globals.MaxMempoolEntries);
            Assert.NotNull(pool.FindOne(x => x.Hash == tx.Hash));
        }
    }
}
