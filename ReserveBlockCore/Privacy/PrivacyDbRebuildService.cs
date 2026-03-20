using LiteDB;
using ReserveBlockCore.Models.Privacy;

namespace ReserveBlockCore.Privacy
{
    /// <summary>
    /// Replays shielded state from chain data. Full block replay requires private transaction types
    /// and <c>PrivateTxPayload</c> (later phases). Merkle reconstruction from committed rows is supported today.
    /// </summary>
    public static class PrivacyDbRebuildService
    {
        /// <summary>
        /// Placeholder: returns false until private TX parsing is implemented.
        /// </summary>
        public static Task<(bool Success, string Message)> TryRebuildFromBlocksAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult((false, "Privacy rebuild from blocks is not yet implemented (requires private TX types)."));
        }

        /// <summary>
        /// Reloads leaf digests from <see cref="CommitmentRecord"/> (ordered by <see cref="CommitmentRecord.TreePosition"/>),
        /// persists Merkle node rows, and refreshes pool root / commitment count. Does not recompute shielded supply.
        /// </summary>
        public static Task<(bool Success, string Message)> TryRebuildMerkleStateFromDbAsync(
            string assetType,
            LiteDatabase privacyDb,
            CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            if (string.IsNullOrWhiteSpace(assetType))
                return Task.FromResult((false, "assetType is required."));
            if (privacyDb == null)
                return Task.FromResult((false, "privacyDb is required."));

            try
            {
                var commitments = privacyDb.GetCollection<CommitmentRecord>(PrivacyDbContext.PRIV_COMMITMENTS);
                var count = commitments.Count(x => x.AssetType == assetType);

                var store = new ShieldedMerkleStore(assetType, privacyDb);
                store.LoadLeavesFromCommitments();
                store.RebuildAndPersistMerkleNodes();

                var poolCol = privacyDb.GetCollection<ShieldedPoolState>(PrivacyDbContext.PRIV_POOL_STATE);
                var existing = poolCol.FindOne(x => x.AssetType == assetType);
                var height = existing?.LastUpdateHeight ?? 0L;
                var supply = existing?.TotalShieldedSupply ?? 0m;
                store.UpdatePoolStateRoot(height, supply, count);

                var root = store.GetRootBytes();
                var rootB64 = root != null ? Convert.ToBase64String(root) : "";
                return Task.FromResult((true, $"Rebuilt Merkle for '{assetType}': commitments={count}, root={rootB64}"));
            }
            catch (Exception ex)
            {
                return Task.FromResult((false, ex.Message));
            }
        }
    }
}
