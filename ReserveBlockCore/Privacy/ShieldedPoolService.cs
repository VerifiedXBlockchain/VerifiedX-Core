using LiteDB;
using ReserveBlockCore.Extensions;
using ReserveBlockCore.Models.Privacy;

namespace ReserveBlockCore.Privacy
{
    /// <summary>
    /// Read/update <see cref="ShieldedPoolState"/> in <c>DB_Privacy</c>. Merkle root + counts are maintained by <see cref="ShieldedMerkleStore.UpdatePoolStateRoot"/>
    /// via <see cref="PrivateTxLedgerService"/> / <see cref="PrivacyDbRebuildService"/>.
    /// </summary>
    public static class ShieldedPoolService
    {
        public static ShieldedPoolState? GetState(string assetType, LiteDatabase? db = null)
        {
            var d = db ?? PrivacyDbContext.GetPrivacyDb();
            return d.GetCollection<ShieldedPoolState>(PrivacyDbContext.PRIV_POOL_STATE)
                .FindOne(x => x.AssetType == assetType);
        }

        /// <summary>Ensures a row exists for <paramref name="assetType"/> (empty tree / zero supply).</summary>
        public static ShieldedPoolState GetOrCreateState(string assetType, LiteDatabase? db = null)
        {
            var d = db ?? PrivacyDbContext.GetPrivacyDb();
            var col = d.GetCollection<ShieldedPoolState>(PrivacyDbContext.PRIV_POOL_STATE);
            col.EnsureIndex(x => x.AssetType, true);
            var existing = col.FindOne(x => x.AssetType == assetType);
            if (existing != null)
                return existing;
            var created = new ShieldedPoolState { AssetType = assetType };
            col.InsertSafe(created);
            return created;
        }
    }
}
