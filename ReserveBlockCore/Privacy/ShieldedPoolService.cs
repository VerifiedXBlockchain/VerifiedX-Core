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

        /// <summary>Current pool Merkle root Base64, or null when the tree is empty / uninitialized.</summary>
        public static string? GetCurrentMerkleRootB64(string assetType, LiteDatabase? db = null)
        {
            var st = GetState(assetType, db);
            return string.IsNullOrWhiteSpace(st?.CurrentMerkleRoot) ? null : st!.CurrentMerkleRoot;
        }

        /// <summary>Builds an inclusion proof against commitments persisted for <paramref name="assetType"/>.</summary>
        public static bool TryGetInclusionProof(
            string assetType,
            long treePosition,
            LiteDatabase? db,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out byte[]? proof,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out byte[]? root32)
        {
            var store = new ShieldedMerkleStore(assetType, db);
            store.LoadLeavesFromCommitments();
            return store.TryGetInclusionProof(treePosition, out proof, out root32);
        }
    }
}
