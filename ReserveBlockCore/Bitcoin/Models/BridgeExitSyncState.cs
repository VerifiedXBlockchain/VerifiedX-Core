using LiteDB;
using ReserveBlockCore.Data;

namespace ReserveBlockCore.Bitcoin.Models
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
}
