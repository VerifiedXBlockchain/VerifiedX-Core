using System.Collections.Concurrent;
using VerifiedXCore.Models;

namespace VerifiedXCore.Services
{
    /// <summary>
    /// How long the recent-transaction-hash replay guard (Globals.MemBlocks) holds a block's hashes. A transaction is valid
    /// in a block from 420 s before to 3,900 s after its timestamp (LedgerIntegrityRules.TimestampOutsideItsBlock), so two
    /// blocks carrying it can be up to 4,320 s apart, plus the 60 s a block's timestamp may run ahead. The guard held the
    /// last 400 blocks, which outlasts that window only at today's block cadence. From Globals.TxHashGuardByTimeHeight it
    /// also holds every block whose timestamp is within that window of the newest block, keyed on block timestamps (never
    /// the clock) so a replay holds the same hashes.
    /// </summary>
    public static class TxHashGuard
    {
        public const int MinBlocks = 400;

        public const long WindowSeconds = Globals.MaxTxAgeSeconds + LedgerIntegrityRules.BlockTimeMarginSeconds
            + Globals.MaxFutureSkewSeconds + LedgerIntegrityRules.BlockTimeMarginSeconds + BlockValidatorService.FutureBlockAllowanceSeconds;

        /// <summary>Timestamps of the blocks whose hashes the guard holds (height → timestamp).</summary>
        internal static readonly ConcurrentDictionary<long, long> BlockTimes = new();

        public static bool ByTime(long nextHeight) => nextHeight >= Globals.TxHashGuardByTimeHeight;

        /// <summary>Whether the guard still holds the block at <paramref name="entryHeight"/> once the newest block is the one given.</summary>
        public static bool Keeps(long entryHeight, long? entryTimestamp, long newestHeight, long newestTimestamp) =>
            newestHeight - entryHeight < MinBlocks
            || (ByTime(newestHeight + 1) && entryTimestamp is { } ts && ts >= newestTimestamp - WindowSeconds);

        /// <summary>
        /// Records a committed block's hashes and drops the blocks the guard no longer holds: the block count is taken from
        /// the node's tip as before, the window from the newer of the tip and the block.
        /// </summary>
        public static void Add(Block block, Block? tip)
        {
            foreach (var trans in block.Transactions)
                Globals.MemBlocks[trans.Hash] = block.Height;
            BlockTimes[block.Height] = block.Timestamp;

            var tipHeight = tip?.Height ?? block.Height;
            var newestTimestamp = tip != null && tip.Height > block.Height ? tip.Timestamp : block.Timestamp;
            foreach (var pair in Globals.MemBlocks)
                if (!Keeps(pair.Value, Time(pair.Value), tipHeight, newestTimestamp))
                    Globals.MemBlocks.TryRemove(pair.Key, out _);
            foreach (var height in BlockTimes.Keys)
                if (!Keeps(height, Time(height), tipHeight, newestTimestamp))
                    BlockTimes.TryRemove(height, out _);
        }

        /// <summary>The blocks the guard holds at startup, newest first from <paramref name="blocksNewestFirst"/>.</summary>
        public static void Load(IEnumerable<Block> blocksNewestFirst)
        {
            var memBlocks = new ConcurrentDictionary<string, long>();
            BlockTimes.Clear();
            Block? newest = null;
            var count = 0;
            foreach (var b in blocksNewestFirst)
            {
                newest ??= b;
                if (!Keeps(b.Height, b.Timestamp, newest.Height, newest.Timestamp) && count >= MinBlocks)
                    break;
                foreach (var tx in b.Transactions)
                    memBlocks.TryAdd(tx.Hash, b.Height);
                BlockTimes[b.Height] = b.Timestamp;
                count++;
            }
            Globals.MemBlocks = memBlocks;
        }

        /// <summary>Forgets the blocks above <paramref name="targetHeight"/> (a rollback).</summary>
        public static void ForgetAbove(long targetHeight)
        {
            foreach (var height in BlockTimes.Keys.Where(h => h > targetHeight).ToList())
                BlockTimes.TryRemove(height, out _);
        }

        private static long? Time(long height) => BlockTimes.TryGetValue(height, out var ts) ? ts : null;
    }
}
