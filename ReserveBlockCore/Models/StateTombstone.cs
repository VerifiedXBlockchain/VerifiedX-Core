using ReserveBlockCore.Data;
using ReserveBlockCore.Extensions;
using ReserveBlockCore.Utilities;

namespace ReserveBlockCore.Models
{
    /// <summary>
    /// Records the deletion of a diff-tracked state record (currently only SmartContractStateTrei —
    /// AccountStateTrei records are never deleted) so StateSnapshotService can propagate the
    /// deletion into snapshot slots. A "changed since height X" query cannot see deleted records;
    /// without tombstones a snapshot would resurrect them on restore.
    /// Stored in the snapshot DB; pruned once every valid slot has passed the tombstone height.
    /// </summary>
    public class StateTombstone
    {
        public long Id { get; set; }
        /// <summary>Source collection tag, e.g. "scstate".</summary>
        public string SourceColl { get; set; }
        /// <summary>Record key within the source collection (e.g. SmartContractUID).</summary>
        public string Key { get; set; }
        /// <summary>Block height at which the deletion occurred (StateWriteContext.StampHeight).</summary>
        public long Height { get; set; }

        public const string COLL_SCSTATE = "scstate";
        public const string COLL_ASTATE = "astate";

        public static LiteDB.ILiteCollection<StateTombstone>? GetTombstones()
        {
            return DbContext.DB_Snapshot?.GetCollection<StateTombstone>(DbContext.RSRV_STATE_TOMBSTONES);
        }

        /// <summary>Writes a tombstone at the current stamp height. Failure is logged, not thrown —
        /// a missed tombstone degrades the snapshot (caught by restore validation), it must not
        /// break block processing.</summary>
        public static void Record(string sourceColl, string key)
        {
            try
            {
                var col = GetTombstones();
                if (col == null) return;
                col.InsertSafe(new StateTombstone
                {
                    SourceColl = sourceColl,
                    Key = key,
                    Height = StateWriteContext.StampHeight
                });
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Failed to record tombstone {sourceColl}/{key}: {ex.Message}", "StateTombstone.Record");
            }
        }
    }
}
