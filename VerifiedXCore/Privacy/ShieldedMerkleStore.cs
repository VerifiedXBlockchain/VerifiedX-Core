using System.Diagnostics.CodeAnalysis;
using LiteDB;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models.Privacy;

namespace VerifiedXCore.Privacy
{
    /// <summary>
    /// Persists shielded commitment Merkle state to <c>DB_Privacy</c>. Rebuilds stored tree levels from leaf digests (Phase 1; incremental updates can replace full rebuild later).
    ///
    /// Two tree shapes (fund-loss audit item 1, stage 2 - see <see cref="PrivacyEpoch"/>):
    /// <list type="bullet">
    /// <item>before PrivateTxProofRulesHeight: the original dynamic-depth tree (chained-Poseidon parents, duplicate-last-node
    /// padding), kept exactly as mined so history replays unchanged;</item>
    /// <item>from the height: the circuits' fixed-depth-32 tree (<see cref="FixedDepthMerkleTree"/>), with the public dummy note at leaf 0.</item>
    /// </list>
    /// The shape is chosen by the block height the caller is applying or validating at (<see cref="UsesFixedDepthAt"/>).
    /// </summary>
    public sealed class ShieldedMerkleStore
    {
        private readonly LiteDatabase _db;
        private readonly string _assetType;
        private readonly bool _fixedDepth;
        private readonly List<byte[]> _leafDigests = new();

        public ShieldedMerkleStore(string assetType, LiteDatabase? privacyDb = null)
            : this(assetType, privacyDb, UsesFixedDepthAt((Globals.LastBlock?.Height ?? 0) + 1))
        {
        }

        /// <param name="fixedDepth">True for the proof-rules epoch's tree (<see cref="UsesFixedDepthAt"/> of the height being applied or validated).</param>
        public ShieldedMerkleStore(string assetType, LiteDatabase? privacyDb, bool fixedDepth)
        {
            _assetType = assetType ?? throw new ArgumentNullException(nameof(assetType));
            _db = privacyDb ?? PrivacyDbContext.GetPrivacyDb();
            _fixedDepth = fixedDepth;
        }

        /// <summary>Whether the proof-rules epoch's fixed-depth tree is the pool's tree at <paramref name="height"/>.</summary>
        public static bool UsesFixedDepthAt(long height) => PrivacyEpoch.ProofRulesActive(height);

        public bool FixedDepth => _fixedDepth;

        public IReadOnlyList<byte[]> LeafDigests => _leafDigests;

        private ILiteCollection<CommitmentRecord> Commitments() =>
            _db.GetCollection<CommitmentRecord>(PrivacyDbContext.PRIV_COMMITMENTS);

        private ILiteCollection<MerkleTreeNodeRecord> MerkleNodes() =>
            _db.GetCollection<MerkleTreeNodeRecord>(PrivacyDbContext.PRIV_MERKLE_NODES);

        private ILiteCollection<ShieldedPoolState> PoolState() =>
            _db.GetCollection<ShieldedPoolState>(PrivacyDbContext.PRIV_POOL_STATE);

        /// <summary>
        /// Replays leaves from <see cref="CommitmentRecord"/> ordered by <see cref="CommitmentRecord.TreePosition"/>.
        /// Uses <see cref="CommitmentRecord.NoteHash"/> when available (v2); falls back to legacy G1 Poseidon digest.
        /// </summary>
        public void LoadLeavesFromCommitments()
        {
            var col = Commitments();
            var rows = col.Query().Where(x => x.AssetType == _assetType).OrderBy(x => x.TreePosition).ToList();
            _leafDigests.Clear();
            foreach (var r in rows)
            {
                if (!string.IsNullOrEmpty(r.NoteHash))
                {
                    var nh = Convert.FromBase64String(r.NoteHash);
                    _leafDigests.Add(CommitmentMerkleTree.LeafDigest(nh));
                }
                else
                {
                    // Legacy: Poseidon of G1 bytes
                    var g1 = Convert.FromBase64String(r.Commitment);
                    _leafDigests.Add(CommitmentMerkleTree.LeafDigestLegacy(g1));
                }
            }
        }

        /// <summary>
        /// Appends a commitment with its Poseidon note hash as the Merkle leaf.
        /// This is the preferred v2 path — the note hash binds amounts in-circuit.
        /// </summary>
        public long AppendCommitment(byte[] g1Compressed, byte[] noteHash32, long blockHeight, long timestamp)
        {
            if (g1Compressed == null || g1Compressed.Length != PlonkNative.G1CompressedSize)
                throw new ArgumentException($"G1 commitment must be {PlonkNative.G1CompressedSize} bytes.", nameof(g1Compressed));
            if (noteHash32 == null || noteHash32.Length != PlonkNative.ScalarSize)
                throw new ArgumentException("Note hash must be 32 bytes.", nameof(noteHash32));

            var pos = (long)_leafDigests.Count;
            var rec = new CommitmentRecord
            {
                Commitment = Convert.ToBase64String(g1Compressed),
                NoteHash = Convert.ToBase64String(noteHash32),
                AssetType = _assetType,
                TreePosition = pos,
                BlockHeight = blockHeight,
                Timestamp = timestamp,
                IsSpent = false
            };
            Commitments().InsertSafe(rec);
            _leafDigests.Add(CommitmentMerkleTree.LeafDigest(noteHash32));
            RebuildAndPersistMerkleNodes();
            return pos;
        }

        /// <summary>
        /// Legacy: appends using G1 commitment bytes as leaf (Poseidon of G1).
        /// Retained for backward compatibility during migration.
        /// </summary>
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
            _leafDigests.Add(CommitmentMerkleTree.LeafDigestLegacy(g1Compressed));
            RebuildAndPersistMerkleNodes();
            return pos;
        }

        /// <summary>
        /// Builds an inclusion proof for the leaf at <paramref name="treePosition"/> against the current in-memory leaf list.
        /// Fixed-depth trees return the flat 32 x 32-byte sibling path the circuits and <see cref="PoseidonV1.RootFromPath"/> take.
        /// </summary>
        public bool TryGetInclusionProof(long treePosition, [NotNullWhen(true)] out byte[]? proof, [NotNullWhen(true)] out byte[]? root32)
        {
            proof = null;
            root32 = null;
            if (treePosition < 0 || treePosition >= _leafDigests.Count)
                return false;
            if (_fixedDepth)
            {
                var tree = new FixedDepthMerkleTree(_leafDigests);
                proof = tree.PathFlat(treePosition);
                root32 = tree.Root();
                return true;
            }
            if (!CommitmentMerkleTree.TryBuildProof(_leafDigests, treePosition, out proof))
                return false;
            var root = GetRootBytes();
            if (root == null)
                return false;
            root32 = root;
            return true;
        }

        public byte[]? GetRootBytes()
        {
            if (_fixedDepth)
                return new FixedDepthMerkleTree(_leafDigests).Root(); // an empty epoch tree still has a root (the depth-32 zero digest)
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
                if (_fixedDepth ? lvl >= FixedDepthMerkleTree.Depth : level.Count <= 1)
                    break;
                var next = new List<byte[]>();
                for (var i = 0; i < level.Count; i += 2)
                {
                    var left = level[i];
                    var right = i + 1 < level.Count ? level[i + 1] : (_fixedDepth ? PoseidonV1.MerkleZero(lvl) : left);
                    next.Add(_fixedDepth ? PoseidonV1.Hash2(left, right) : CommitmentMerkleTree.Combine(left, right));
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

        /// <summary>
        /// Starts the proof-rules epoch for this asset: every commitment, tree node and pool row of the asset goes
        /// (notes from before the height can never be proven), the fixed-depth tree starts with the public dummy note
        /// at leaf 0, and the supply counter is 0. Nullifiers are kept: they can never be spent again either way.
        /// Only meaningful on a store created with fixedDepth = true.
        /// </summary>
        public void ResetForEpoch(long blockHeight, long timestamp)
        {
            if (!_fixedDepth)
                throw new InvalidOperationException("ResetForEpoch is for the proof-rules epoch's fixed-depth tree.");
            Commitments().DeleteManySafe(x => x.AssetType == _assetType);
            MerkleNodes().DeleteManySafe(x => x.AssetType == _assetType);
            PoolState().DeleteManySafe(x => x.AssetType == _assetType);
            _leafDigests.Clear();
            AppendCommitment(PrivacyEpoch.DummyCommitment, PrivacyEpoch.DummyNoteHash, blockHeight, timestamp);
            UpdatePoolStateRoot(blockHeight, 0M, 1);
        }
    }
}
