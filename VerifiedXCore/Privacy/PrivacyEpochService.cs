using LiteDB;
using VerifiedXCore.Models;
using VerifiedXCore.Models.Privacy;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Privacy
{
    /// <summary>
    /// Applies the start of the proof-rules epoch (fund-loss audit item 1, stage 2): at exactly
    /// Globals.PrivateTxProofRulesHeight, before that block's transactions, every shielded pool is reset to the
    /// circuits' tree with the public dummy note at leaf 0 (<see cref="ShieldedMerkleStore.ResetForEpoch"/>).
    /// Called by block application and by the privacy-store rebuild, so a replay reproduces the same state.
    /// </summary>
    public static class PrivacyEpochService
    {
        /// <summary>The assets whose pools exist (pool rows and commitment rows), always including VFX.</summary>
        public static List<string> PoolAssets(LiteDatabase db)
        {
            var assets = new HashSet<string>(StringComparer.Ordinal) { "VFX" };
            foreach (var row in db.GetCollection<ShieldedPoolState>(PrivacyDbContext.PRIV_POOL_STATE).FindAll())
                if (!string.IsNullOrEmpty(row.AssetType)) assets.Add(row.AssetType);
            foreach (var row in db.GetCollection<CommitmentRecord>(PrivacyDbContext.PRIV_COMMITMENTS).FindAll())
                if (!string.IsNullOrEmpty(row.AssetType)) assets.Add(row.AssetType);
            return assets.OrderBy(a => a, StringComparer.Ordinal).ToList();
        }

        /// <summary>Resets every pool for the epoch at <paramref name="blockHeight"/>.</summary>
        public static void ResetPools(LiteDatabase db, long blockHeight, long timestamp)
        {
            foreach (var asset in PoolAssets(db))
            {
                var store = new ShieldedMerkleStore(asset, db, fixedDepth: true);
                store.ResetForEpoch(blockHeight, timestamp);
            }
            LogUtility.Log($"Shielded pools reset for the proof-rules epoch at block {blockHeight}.", "PrivacyEpochService.ResetPools()");
        }

        /// <summary>Block application: runs the reset when <paramref name="block"/> is the epoch's first block.</summary>
        public static void ApplyIfResetBlock(Block block, LiteDatabase? db = null)
        {
            if (block == null || !PrivacyEpoch.IsResetBlock(block.Height))
                return;
            ResetPools(db ?? PrivacyDbContext.GetPrivacyDb(), block.Height, block.Timestamp);
        }
    }
}
