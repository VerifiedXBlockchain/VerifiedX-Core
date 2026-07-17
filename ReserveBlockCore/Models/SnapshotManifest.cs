using ReserveBlockCore.Data;

namespace ReserveBlockCore.Models
{
    /// <summary>
    /// One record per snapshot slot (0..2) in the snapshot DB. Slots hold rotating copies of all
    /// chain-derived state, spaced one snapshot cadence apart (steady state: H, H-10, H-20), so a
    /// fork of up to the max fork depth (10) always has an untainted slot at or below its target.
    ///
    /// Update-cycle ordering guarantees crash safety: a slot is marked <see cref="SnapshotSlotStatus.Updating"/>
    /// (and checkpointed) BEFORE any data is copied, and only marked <see cref="SnapshotSlotStatus.Valid"/>
    /// with its new height as the LAST step. A crash mid-cycle leaves the slot Updating (= unusable)
    /// while the other slots stay Valid.
    /// </summary>
    public class SnapshotManifest
    {
        /// <summary>Slot id, 1..SlotCount. Also the LiteDB _id — must never be 0, which LiteDB
        /// treats as "unassigned" and replaces with an auto-generated id.</summary>
        [LiteDB.BsonId]
        public int SlotId { get; set; }

        /// <summary>Block height this slot's state is consistent with (inclusive).</summary>
        public long Height { get; set; }

        /// <summary>Hash of the block at <see cref="Height"/> when the snapshot was taken. Restore
        /// verifies this against the local block store — a mismatch means the snapshot was taken
        /// on a fork branch (or the block store was rewound past it) and the slot must not be used.</summary>
        public string? BlockHash { get; set; }

        public SnapshotSlotStatus Status { get; set; } = SnapshotSlotStatus.Empty;

        public DateTime UpdatedUtc { get; set; }

        /// <summary>Bumped when the snapshot layout/collection set changes; mismatched slots are rebuilt.</summary>
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        public const int CurrentSchemaVersion = 1;
        public const int SlotCount = 3;

        public static LiteDB.ILiteCollection<SnapshotManifest>? GetManifest()
        {
            return DbContext.DB_Snapshot?.GetCollection<SnapshotManifest>(DbContext.RSRV_SNAPSHOT_MANIFEST);
        }
    }

    public enum SnapshotSlotStatus
    {
        Empty = 0,
        Updating = 1,
        Valid = 2,
        Invalid = 3
    }
}
