namespace VerifiedXCore.Models
{
    /// <summary>
    /// Persistent flag stored in DB_AccountStateTrei that tracks whether the state trie
    /// has been fully built from the block database. This travels with snapshots.
    /// 
    /// Semantics:
    /// - Record missing (null) → state trie was wiped / never built → needs ResetTreis
    /// - IsSynced = false → ResetTreis was started but didn't finish → needs retry
    /// - IsSynced = true → state trie is consistent with the block database
    /// 
    /// ResetTreis() keeps this record when it wipes the state trie: it first writes IsSynced=false with
    /// RebuildInProgress set, and only a successful rebuild writes IsSynced=true at its END.
    /// If the node crashes mid-rebuild, RebuildInProgress is still set at startup and the node stays stopped.
    /// </summary>
    public class StateTreiStatus
    {
        public long Id { get; set; }

        /// <summary>True if the state trie is fully consistent with the block database.</summary>
        public bool IsSynced { get; set; }

        /// <summary>The block height at which the state trie was last verified/rebuilt.</summary>
        public long LastSyncedHeight { get; set; }

        /// <summary>UTC timestamp of the last successful sync.</summary>
        public DateTime LastSyncTime { get; set; }

        /// <summary>If IsSynced is false, the reason why the last rebuild failed.</summary>
        public string? LastFailureReason { get; set; }

        /// <summary>
        /// Set just before a snapshot restore wipes the state, cleared only when the state is next marked synced (a restore
        /// that completes, or a rebuild). While set, this node does not validate or produce blocks. A separate field, so a
        /// crash mid-restore or a later failure reason cannot drop it.
        /// </summary>
        public bool? RestoreDestroyed { get; set; }

        /// <summary>
        /// Set before a rebuild (ResetTreis, or a revert ahead of one) destroys anything, and gone once that rebuild ends:
        /// a rebuild that completes marks the state synced, one that ends with failures records them. Still set at startup,
        /// the rebuild was interrupted and the state is partial, so it stops this node as <see cref="RestoreDestroyed"/> does.
        /// </summary>
        public bool? RebuildInProgress { get; set; }

        /// <summary>
        /// Set when a block was committed with some of its state writes not applied. Those writes are missing until the
        /// state is rebuilt or restored from a snapshot, so blocks that apply cleanly afterwards do not clear it: only a
        /// rebuild (which starts a new record) or a state marked synced (a completed rebuild or restore) does.
        /// </summary>
        public bool? IncompleteApply { get; set; }
    }
}
