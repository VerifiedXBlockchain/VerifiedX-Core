using LiteDB;
using ReserveBlockCore.Extensions;
using ReserveBlockCore.Models.Privacy;

namespace ReserveBlockCore.Privacy
{
    /// <summary>
    /// Persists shielded commitment Merkle state to <c>DB_Privacy</c>. Rebuilds stored tree levels from leaf digests (Phase 1; incremental updates can replace full rebuild later).
    /// </summary>
    public sealed class ShieldedMerkleStore
    {
        private readonly LiteDatabase _db;
        private readonly string _assetType;
        private readonly List<byte[]> _leafDigests = new();

        public ShieldedMerkleStore(string assetType, LiteDatabase? privacyDb = null)
        {
            _assetType = assetType ?? throw new ArgumentNullException(nameof(assetType));
            _db = privacyDb ?? PrivacyDbContext.GetPrivacyDb();
        }

        public IReadOnlyList<byte[]> LeafDigests => _leafDigests;

        private ILiteCollection<CommitmentRecord> Commitments() =>
            _db.GetCollection<CommitmentRecord>(PrivacyDbContext.PRIV_COMMITMENTS);

        private ILiteCollection<MerkleTreeNodeRecord> MerkleNodes() =>
            _db.GetCollection<MerkleTreeNodeRecord>(PrivacyDbContext.PRIV_MERKLE_NODES);

        private ILiteCollection<ShieldedPoolState> PoolState() =>
            _db.GetCollection<ShieldedPoolState>(PrivacyDbContext.PRIV_POOL_STATE);

        /// <summary>Replays leaves from <see cref="CommitmentRecord"/> ordered by <see cref="CommitmentRecord.TreePosition"/>.</summary>
        public void LoadLeavesFromCommitments()
        {
            var col = Commitments();
            var rows = col.Query().Where(x => x.AssetType == _assetType).OrderBy(x => x.TreePosition).ToList();
            _leafDigests.Clear();
            foreach (var r in rows)
            {
                var g1 = Convert.FromBase64String(r.Commitment);
                _leafDigests.Add(CommitmentMerkleTree.LeafDigest(g1));
            }
        }

        public long AppendG1Commitment(byte[] g1Compressed, long blockHeight, long timestamp)
        {
            if (g1Compressed == null || g1Compressed.Length != PlonkNative.G1CompressedSize)
                throw new ArgumentException($"G1 commitment must be {PlonkNative.G1CompressedSize} bytes.", nameof(g1Compressed));

            var pos = (long)_leafDigests.Count;
            var rec = new CommitmentRecord
            {
                Commitment = Convert.ToBase64String(g1Compressed),
                AssetType = _assetType,
                TreePosition = pos,
                BlockHeight = blockHeight,
                Timestamp = timestamp,
                IsSpent = false
            };
            Commitments().InsertSafe(rec);
            _leafDigests.Add(CommitmentMerkleTree.LeafDigest(g1Compressed));
            RebuildAndPersistMerkleNodes();
            return pos;
        }

        public byte[]? GetRootBytes()
        {
            if (_leafDigests.Count == 0)
                return null;
            var level = new List<byte[]>(_leafDigests);
            while (level.Count > 1)
            {
                var next = new List<byte[]>();
                for (var i = 0; i < level.Count; i += 2)
                {
                    var left = level[i];
                    var right = i + 1 < level.Count ? level[i + 1] : left;
                    next.Add(CommitmentMerkleTree.Combine(left, right));
                }
                level = next;
            }
            return level[0];
        }

        public void RebuildAndPersistMerkleNodes()
        {
            var merkle = MerkleNodes();
            merkle.DeleteManySafe(x => x.AssetType == _assetType);
            if (_leafDigests.Count == 0)
                return;

            var level = new List<byte[]>(_leafDigests);
            var lvl = 0;
            while (true)
            {
                for (var i = 0; i < level.Count; i++)
                {
                    var node = new MerkleTreeNodeRecord
                    {
                        AssetType = _assetType,
                        Level = lvl,
                        Index = i,
                        NodeHash = Convert.ToBase64String(level[i])
                    };
                    merkle.InsertSafe(node);
                }
                if (level.Count <= 1)
                    break;
                var next = new List<byte[]>();
                for (var i = 0; i < level.Count; i += 2)
                {
                    var left = level[i];
                    var right = i + 1 < level.Count ? level[i + 1] : left;
                    next.Add(CommitmentMerkleTree.Combine(left, right));
                }
                level = next;
                lvl++;
            }
        }

        public void UpdatePoolStateRoot(long blockHeight, decimal totalShieldedSupply, long totalCommitments)
        {
            var root = GetRootBytes();
            var col = PoolState();
            var existing = col.FindOne(x => x.AssetType == _assetType);
            var state = existing ?? new ShieldedPoolState { AssetType = _assetType };
            state.CurrentMerkleRoot = root != null ? Convert.ToBase64String(root) : "";
            state.TotalCommitments = totalCommitments;
            state.TotalShieldedSupply = totalShieldedSupply;
            state.LastUpdateHeight = blockHeight;
            col.UpsertSafe(state);
        }
    }
}
