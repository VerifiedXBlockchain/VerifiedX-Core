using System;
using System.Collections.Generic;
using System.Linq;
using VerifiedXCore;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// From TxHashGuardByTimeHeight the replay guard holds a block's transaction hashes while the block is within the
    /// transaction time window of the newest block, whatever the block cadence, and never fewer than 400 blocks. Below the
    /// height it holds the last 400 blocks.
    /// </summary>
    [Collection("DbContextSequential")]
    public class TxHashGuardByTimeTests : IDisposable
    {
        private const long Start = 10_000;
        private const long Cadence = 5; // seconds per block, faster than today
        private readonly long _priorHeight = Globals.TxHashGuardByTimeHeight;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _priorMemBlocks = Globals.MemBlocks;
        private readonly Block _priorLastBlock = Globals.LastBlock;

        public TxHashGuardByTimeTests()
        {
            Globals.MemBlocks = new();
            TxHashGuard.BlockTimes.Clear();
        }

        public void Dispose()
        {
            Globals.TxHashGuardByTimeHeight = _priorHeight;
            Globals.MemBlocks = _priorMemBlocks;
            Globals.LastBlock = _priorLastBlock;
            TxHashGuard.BlockTimes.Clear();
        }

        private static Block BlockAt(long height) => new()
        {
            Height = height,
            Timestamp = 1_700_000_000 + (height - Start) * Cadence,
            Transactions = new List<Transaction> { new() { Hash = $"tx-{height}" } },
        };

        /// <summary>Commits blocks Start..Start+count-1 as the node does (tip first, then the guard).</summary>
        private static void Commit(int count)
        {
            for (var h = Start; h < Start + count; h++)
            {
                var block = BlockAt(h);
                Globals.LastBlock = block;
                BlockValidatorService.UpdateMemBlocks(block);
            }
        }

        [Fact]
        public void TheWindow_CoversTheTransactionWindowAndTheFutureAllowance()
        {
            Assert.Equal(4_380, TxHashGuard.WindowSeconds);
        }

        [Fact]
        public void FromTheHeight_AFastChainStillRefusesARemineInsideTheWindow()
        {
            Globals.TxHashGuardByTimeHeight = Start;
            Commit(1_000); // 5,000 s of blocks at 5 s each
            var tip = Start + 999;
            // A block 600 blocks back (3,000 s) is inside the 4,380 s window: its transaction is still guarded.
            Assert.True(Globals.MemBlocks.ContainsKey($"tx-{tip - 600}"));
            // One just inside the window, and one just outside it.
            Assert.True(Globals.MemBlocks.ContainsKey($"tx-{tip - TxHashGuard.WindowSeconds / Cadence}"));
            Assert.False(Globals.MemBlocks.ContainsKey($"tx-{tip - TxHashGuard.WindowSeconds / Cadence - 1}"));
        }

        [Fact]
        public void FromTheHeight_ASlowChainKeepsAtLeast400Blocks()
        {
            Globals.TxHashGuardByTimeHeight = Start;
            Assert.True(TxHashGuard.Keeps(Start, 0, Start + 399, 1_000_000));
            Assert.False(TxHashGuard.Keeps(Start, 0, Start + 400, 1_000_000));
        }

        [Fact]
        public void BelowTheHeight_TheGuardHoldsTheLast400Blocks()
        {
            Commit(1_000);
            var tip = Start + 999;
            Assert.True(Globals.MemBlocks.ContainsKey($"tx-{tip - 399}"));
            Assert.False(Globals.MemBlocks.ContainsKey($"tx-{tip - 400}"));
            Assert.Equal(400, Globals.MemBlocks.Count);
        }

        [Fact]
        public void AtStartup_TheGuardLoadsTheSameBlocksAsLiveCommits()
        {
            Globals.TxHashGuardByTimeHeight = Start;
            Commit(1_200);
            var live = Globals.MemBlocks.Keys.OrderBy(k => k).ToList();

            TxHashGuard.Load(Enumerable.Range(0, 1_200).Select(i => BlockAt(Start + 1_199 - i)));
            Assert.Equal(live, Globals.MemBlocks.Keys.OrderBy(k => k).ToList());
        }

        [Fact]
        public void ARollback_ForgetsTheBlockTimesAboveTheTarget()
        {
            Globals.TxHashGuardByTimeHeight = Start;
            Commit(10);
            VerifiedXCore.Utilities.BlockRollbackUtility.PruneMemBlocksAbove(Start + 4);
            Assert.False(TxHashGuard.BlockTimes.ContainsKey(Start + 5));
            Assert.False(Globals.MemBlocks.ContainsKey($"tx-{Start + 5}"));
            Assert.True(Globals.MemBlocks.ContainsKey($"tx-{Start + 4}"));
        }
    }
}
