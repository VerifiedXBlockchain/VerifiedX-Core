namespace ReserveBlockCore.Models
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
    /// When ResetTreis() wipes the state trie (DeleteAllSafe), this record is also wiped.
    /// A new record with IsSynced=true is only inserted at the END of a successful rebuild.
    /// If the node crashes mid-rebuild, the record stays gone → startup detects and retries.
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
    }
}
