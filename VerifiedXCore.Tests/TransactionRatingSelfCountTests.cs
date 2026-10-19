using System;
using System.IO;
using System.Threading.Tasks;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A transaction is rated against the sender's other pooled transactions, not its own pooled copy: two peers delivering
    /// one genuine ADNR or DSTR transaction at once made the second handler rate it F and delete it from the mempool.
    /// </summary>
    [Collection("DbContextSequential")]
    public class TransactionRatingSelfCountTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;

        public TransactionRatingSelfCountTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"rating_self_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static Transaction Pooled(string hash, TransactionType type)
        {
            var tx = new Transaction { Hash = hash, FromAddress = "xSender", ToAddress = "Adnr_Base", Amount = 5M, Fee = 0.1M, TransactionType = type, Timestamp = 1_700_000_000 };
            TransactionData.GetPool().Insert(tx);
            return tx;
        }

        [Theory]
        [InlineData(TransactionType.ADNR)]
        [InlineData(TransactionType.DSTR)]
        public async Task ATransactionAlreadyPooled_IsNotRatedAgainstItself(TransactionType type)
        {
            var pooled = Pooled("tx-x", type);

            Assert.NotEqual(TransactionRating.F, await TransactionRatingService.GetTransactionRating(pooled));
        }

        [Theory]
        [InlineData(TransactionType.ADNR)]
        [InlineData(TransactionType.DSTR)]
        public async Task ASecondTransactionFromTheSameSender_IsStillRatedF(TransactionType type)
        {
            Pooled("tx-x", type);
            var other = new Transaction { Hash = "tx-y", FromAddress = "xSender", ToAddress = "Adnr_Base", Amount = 5M, Fee = 0.1M, TransactionType = type, Timestamp = 1_700_000_001 };

            Assert.Equal(TransactionRating.F, await TransactionRatingService.GetTransactionRating(other));
        }
    }
}
