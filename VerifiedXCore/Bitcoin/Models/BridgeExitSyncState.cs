using LiteDB;
using VerifiedXCore.Data;

namespace VerifiedXCore.Bitcoin.Models
{
    /// <summary>
    /// Cursor for Base <c>ExitBurned</c> log polling (demo bridge-back).
    /// </summary>
    public class BridgeExitSyncState
    {
        [BsonId]
        public int Id { get; set; } = 1;

        /// <summary>Last Base block number fully scanned for ExitBurned logs.</summary>
        public long LastScannedBlock { get; set; }

        /// <summary>
        /// The hash of <see cref="LastScannedBlock"/> as the providers gave it when the cursor moved there; null or empty
        /// when it was not read (a row from before hashes were kept, or a cursor set by a rescan or a rewind). A block number
        /// alone does not say which block was read: the watch compares this with the chain before it reads on, and goes
        /// back when the chain holds another block at that height (BaseBridgeExitWatchService).
        /// </summary>
        public string? LastScannedBlockHash { get; set; }

        /// <summary>
        /// Earlier positions of the cursor with their hashes, oldest first, at most <see cref="MaxMarks"/>: where the watch
        /// goes back to, the newest one the chain still holds, when the block at the cursor is no longer the one read.
        /// </summary>
        public List<BridgeExitCursorMark> Marks { get; set; } = new();

        /// <summary>The most earlier positions kept.</summary>
        public const int MaxMarks = 64;

        /// <summary>Moves the cursor forward to a block that was read, with its hash; the position left becomes a mark.</summary>
        public void Advance(long block, string hash)
        {
            if (!string.IsNullOrEmpty(LastScannedBlockHash))
            {
                Marks.Add(new BridgeExitCursorMark { Block = LastScannedBlock, Hash = LastScannedBlockHash });
                if (Marks.Count > MaxMarks) Marks.RemoveRange(0, Marks.Count - MaxMarks);
            }
            LastScannedBlock = block;
            LastScannedBlockHash = string.IsNullOrEmpty(hash) ? null : hash;
        }

        /// <summary>Sets the cursor to a block whose hash was not read (a rescan, a rewind, a block passed over): reading goes on from there.</summary>
        public void MoveTo(long block)
        {
            LastScannedBlock = block;
            LastScannedBlockHash = null;
            Marks.RemoveAll(m => m.Block >= block);
        }

        /// <summary>
        /// Burns this node read on Base and handed to the casters' consensus that the chain has not yet redeemed, each with
        /// the Base block it was read in. A caster holds a burn's work only in memory; after a restart it reads Base again
        /// from the oldest of these, however far behind the cursor that is (BaseBridgeExitWatchService.ConsumeRewind).
        /// </summary>
        public List<OpenBridgeBurn> OpenBurns { get; set; } = new();

        private const string CollectionName = "rsrv_bridge_exit_sync";

        public static BridgeExitSyncState GetOrCreate()
        {
            var col = DbContext.DB_vBTC.GetCollection<BridgeExitSyncState>(CollectionName);
            var row = col.FindById(1);
            if (row != null) return row;
            row = new BridgeExitSyncState { Id = 1, LastScannedBlock = 0 };
            col.Insert(row);
            return row;
        }

        public static void Save(BridgeExitSyncState state)
        {
            var col = DbContext.DB_vBTC.GetCollection<BridgeExitSyncState>(CollectionName);
            col.Upsert(state);
        }
    }

    /// <summary>A position the cursor held, with the hash of the block there (BridgeExitSyncState.Marks).</summary>
    public class BridgeExitCursorMark
    {
        public long Block { get; set; }
        public string Hash { get; set; } = "";
    }

    /// <summary>A burn handed to the casters' consensus and not yet redeemed on the chain (BridgeExitSyncState.OpenBurns).</summary>
    public class OpenBridgeBurn
    {
        public string BurnId { get; set; } = "";
        public long BaseBlock { get; set; }
    }
}
