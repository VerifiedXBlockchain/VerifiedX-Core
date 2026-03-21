using LiteDB;
using ReserveBlockCore.Data;
using ReserveBlockCore.Extensions;
using ReserveBlockCore.Models.Privacy;

namespace ReserveBlockCore.Privacy
{
    public static class PrivacyDbContext
    {
        public const string PRIV_COMMITMENTS = "ShieldedCommitments";
        public const string PRIV_NULLIFIERS = "ShieldedNullifiers";
        public const string PRIV_POOL_STATE = "ShieldedPoolState";
        public const string PRIV_WALLETS = "ShieldedWallets";
        public const string PRIV_MERKLE_NODES = "MerkleTreeNodes";

        public static LiteDatabase GetPrivacyDb() => DbContext.DB_Privacy;

        public static ILiteCollection<CommitmentRecord> Commitments() =>
            GetPrivacyDb().GetCollection<CommitmentRecord>(PRIV_COMMITMENTS);

        public static ILiteCollection<NullifierRecord> Nullifiers() =>
            GetPrivacyDb().GetCollection<NullifierRecord>(PRIV_NULLIFIERS);

        public static ILiteCollection<ShieldedPoolState> PoolState() =>
            GetPrivacyDb().GetCollection<ShieldedPoolState>(PRIV_POOL_STATE);

        public static ILiteCollection<ShieldedWallet> Wallets() =>
            GetPrivacyDb().GetCollection<ShieldedWallet>(PRIV_WALLETS);

        public static ILiteCollection<MerkleTreeNodeRecord> MerkleNodes() =>
            GetPrivacyDb().GetCollection<MerkleTreeNodeRecord>(PRIV_MERKLE_NODES);

        /// <summary>
        /// Ensures indexes on the privacy database. Safe to call at startup and on temp DBs in tests.
        /// </summary>
        public static void EnsurePrivacyIndexes(LiteDatabase db)
        {
            var commitments = db.GetCollection<CommitmentRecord>(PRIV_COMMITMENTS);
            commitments.EnsureIndexSafe(x => x.Commitment, false);
            commitments.EnsureIndexSafe(x => x.NoteHash, false);
            commitments.EnsureIndexSafe(x => x.AssetType, false);
            commitments.EnsureIndexSafe(x => x.TreePosition, false);

            var nullifiers = db.GetCollection<NullifierRecord>(PRIV_NULLIFIERS);
            nullifiers.EnsureIndexSafe(x => x.Nullifier, true);
            nullifiers.EnsureIndexSafe(x => x.AssetType, false);

            var poolState = db.GetCollection<ShieldedPoolState>(PRIV_POOL_STATE);
            poolState.EnsureIndexSafe(x => x.AssetType, true);

            var wallets = db.GetCollection<ShieldedWallet>(PRIV_WALLETS);
            wallets.EnsureIndexSafe(x => x.ShieldedAddress, true);

            var merkle = db.GetCollection<MerkleTreeNodeRecord>(PRIV_MERKLE_NODES);
            merkle.EnsureIndexSafe(x => x.AssetType, false);
            merkle.EnsureIndexSafe(x => x.Level, false);
            merkle.EnsureIndexSafe(x => x.Index, false);
        }
    }
}
