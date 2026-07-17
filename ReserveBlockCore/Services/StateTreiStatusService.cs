using ReserveBlockCore.Data;
using ReserveBlockCore.Models;
using ReserveBlockCore.Utilities;

namespace ReserveBlockCore.Services
{
    /// <summary>
    /// Reads and writes the StateTreiStatus flag in DB_AccountStateTrei.
    /// 
    /// This flag tracks whether the state trie is fully built and consistent.
    /// It lives in the same DB as the state trie itself, so it:
    /// - Travels with snapshots (restored snapshot = IsSynced=true)
    /// - Gets wiped when ResetTreis wipes the state trie (missing = needs rebuild)
    /// - Is set to true ONLY after a successful full rebuild
    /// </summary>
    public static class StateTreiStatusService
    {
        private const string COLLECTION_NAME = "state_trei_status";

        /// <summary>
        /// Gets the current StateTreiStatus. Returns null if no record exists
        /// (meaning state trie was wiped or never built).
        /// </summary>
        public static StateTreiStatus? GetStatus()
        {
            try
            {
                var db = DbContext.DB_AccountStateTrei;
                if (db == null) return null;

                var col = db.GetCollection<StateTreiStatus>(COLLECTION_NAME);
                return col.FindOne(x => true);
            }
            catch (Exception ex)
            {
                LogUtility.Log($"[StateTreiStatusService] Error reading status: {ex.Message}", "StateTreiStatusService.GetStatus");
                return null;
            }
        }

        /// <summary>
        /// Returns true if the state trie is verified as synced.
        /// Returns false if the record is missing, null, or IsSynced=false.
        /// </summary>
        public static bool IsSynced()
        {
            var status = GetStatus();
            return status?.IsSynced == true;
        }

        /// <summary>
        /// Marks the state trie as fully synced after a successful rebuild.
        /// Called at the END of ResetTreis() after all blocks have been replayed.
        /// </summary>
        public static void SetSynced(long height)
        {
            try
            {
                var db = DbContext.DB_AccountStateTrei;
                if (db == null) return;

                var col = db.GetCollection<StateTreiStatus>(COLLECTION_NAME);
                
                // Delete any existing record first
                col.DeleteAll();

                var status = new StateTreiStatus
                {
                    Id = 1,
                    IsSynced = true,
                    LastSyncedHeight = height,
                    LastSyncTime = DateTime.UtcNow,
                    LastFailureReason = null
                };

                col.Insert(status);
                db.Checkpoint();

                LogUtility.Log(
                    $"[StateTreiStatusService] State trie marked as SYNCED at height {height}.",
                    "StateTreiStatusService.SetSynced");
            }
            catch (Exception ex)
            {
                LogUtility.Log($"[StateTreiStatusService] Error setting synced status: {ex.Message}", "StateTreiStatusService.SetSynced");
            }
        }

        /// <summary>
        /// Marks the state trie as NOT synced due to a failure.
        /// This is called when ResetTreis fails, so the next startup knows to retry.
        /// Note: Usually the record is already gone because ResetTreis wipes the DB.
        /// This method handles the edge case where the DB wasn't fully wiped.
        /// </summary>
        public static void SetFailed(string reason)
        {
            try
            {
                var db = DbContext.DB_AccountStateTrei;
                if (db == null) return;

                var col = db.GetCollection<StateTreiStatus>(COLLECTION_NAME);
                
                col.DeleteAll();

                var status = new StateTreiStatus
                {
                    Id = 1,
                    IsSynced = false,
                    LastSyncedHeight = Globals.LastBlock?.Height ?? 0,
                    LastSyncTime = DateTime.UtcNow,
                    LastFailureReason = reason
                };

                col.Insert(status);
                db.Checkpoint();

                LogUtility.Log(
                    $"[StateTreiStatusService] State trie marked as FAILED: {reason}",
                    "StateTreiStatusService.SetFailed");
            }
            catch (Exception ex)
            {
                // If we can't even write the failure status, just log it.
                // The missing record will trigger a rebuild on next startup anyway.
                LogUtility.Log($"[StateTreiStatusService] Error setting failed status: {ex.Message}", "StateTreiStatusService.SetFailed");
            }
        }
    }
}
