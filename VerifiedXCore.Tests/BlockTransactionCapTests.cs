using System.Collections.Generic;
using System.Linq;
using VerifiedXCore;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// From BlockTransactionCapHeight a block carries at most MaxTransactionsPerBlock transactions, its coinbase included,
    /// and a producer stops filling at the cap; below the height only the size bound applies, as before.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BlockTransactionCapTests : System.IDisposable
    {
        private const long Gate = 7_000;
        private readonly long _prior = Globals.BlockTransactionCapHeight;

        public BlockTransactionCapTests() => Globals.BlockTransactionCapHeight = Gate;

        public void Dispose() => Globals.BlockTransactionCapHeight = _prior;

        private static List<Transaction> Txs(int n) => Enumerable.Range(0, n).Select(i => new Transaction { Hash = $"t{i}" }).ToList();

        [Fact]
        public void ABlockOverTheCap_IsRefusedFromTheHeight()
        {
            Assert.False(LedgerIntegrityRules.OverTransactionCap(Globals.MaxTransactionsPerBlock, Gate));
            Assert.True(LedgerIntegrityRules.OverTransactionCap(Globals.MaxTransactionsPerBlock + 1, Gate));
            Assert.False(LedgerIntegrityRules.OverTransactionCap(Globals.MaxTransactionsPerBlock + 1, Gate - 1));
        }

        [Fact]
        public void TheProducer_StopsAtTheCap_CountingWhatTheBlockAlreadyHolds()
        {
            var pool = Txs(Globals.MaxTransactionsPerBlock + 10);
            var kept = LedgerIntegrityRules.WithinTransactionCap(pool, 1, Gate); // the coinbase is already in
            Assert.Equal(Globals.MaxTransactionsPerBlock - 1, kept.Count);
            Assert.Equal("t0", kept[0].Hash);
            Assert.False(LedgerIntegrityRules.OverTransactionCap(kept.Count + 1, Gate));

            Assert.Equal(pool.Count, LedgerIntegrityRules.WithinTransactionCap(pool, 1, Gate - 1).Count);
        }
    }
}
