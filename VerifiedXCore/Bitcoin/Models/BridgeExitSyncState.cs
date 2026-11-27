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

    /// <summary>A burn handed to the casters' consensus and not yet redeemed on the chain (BridgeExitSyncState.OpenBurns).</summary>
    public class OpenBridgeBurn
    {
        public string BurnId { get; set; } = "";
        public long BaseBlock { get; set; }
    }
}
