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
    /// The shape is chosen by the asset and the block height the caller is applying or validating at (<see cref="UsesFixedDepth"/>).
    /// </summary>
    public sealed class ShieldedMerkleStore
    {
        private readonly LiteDatabase _db;
        private readonly string _assetType;
        private readonly bool _fixedDepth;
        private readonly List<byte[]> _leafDigests = new();

        /// <summary>The tree as the NEXT block will see it (tip + 1). Wallet and API reads only: block application and replay pass the height of the block they apply.</summary>
        public ShieldedMerkleStore(string assetType, LiteDatabase? privacyDb = null)
            : this(assetType, privacyDb, UsesFixedDepth(assetType, (Globals.LastBlock?.Height ?? 0) + 1))
        {
        }

        /// <param name="fixedDepth">True for the proof-rules epoch's tree (<see cref="UsesFixedDepth"/> of the asset and the height being applied or validated).</param>
        public ShieldedMerkleStore(string assetType, LiteDatabase? privacyDb, bool fixedDepth)
        {
            _assetType = assetType ?? throw new ArgumentNullException(nameof(assetType));
            _db = privacyDb ?? PrivacyDbContext.GetPrivacyDb();
            _fixedDepth = fixedDepth;
        }

        /// <summary>
        /// Whether <paramref name="assetType"/>'s pool is the proof-rules epoch's fixed-depth tree at <paramref name="height"/>.
        /// Only the pools the epoch resets (VFX) ever are; a vBTC pool keeps its legacy tree for good - its leaves are legacy
        /// note hashes, which are not field elements and cannot go into the circuits' tree at all. The height is that of the
        /// block being applied or validated, never the live tip: a replay applies pre-epoch blocks while the tip is past it.
        /// </summary>
        public static bool UsesFixedDepth(string? assetType, long height) =>
            PrivacyEpoch.ProofRulesActive(height) && PrivacyEpochService.IsEpochAsset(assetType);

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
        /// <para>
        /// Every private transaction applied loaded its pool's every leaf (about 0.12 s a transaction at 10,000 leaves,
        /// on every node; fifth review, round 32). The leaves read last are kept per database and pool and reused when
        /// the row at their last position is still the same row (rows leave a pool only from its end, by rollback, or
        /// all at once, by a reset or rebuild, and a row put back is a new row); only the rows after it are read.
        /// Anything else is read in full, as before.
        /// </para>
        /// </summary>
        public void LoadLeavesFromCommitments()
        {
            var col = Commitments();
            var cache = _leafCaches.GetOrCreateValue(_db).GetOrAdd(_assetType, _ => new LeafCache());
            lock (cache)
            {
                if (!TryExtend(col, cache))
                {
                    _fullLeafLoadsOnThisThread++;
                    var rows = col.Query().Where(x => x.AssetType == _assetType).OrderBy(x => x.TreePosition).ToList();
                    cache.Digests = rows.Select(DigestOf).ToList();
                    cache.LastId = rows.Count > 0 ? rows[^1].Id : null;
                    cache.LastPosition = rows.Count > 0 ? rows[^1].TreePosition : -1;
                    cache.Valid = rows.Select((r, i) => r.TreePosition == i).All(x => x);
                }
                _leafDigests.Clear();
                _leafDigests.AddRange(cache.Digests);
            }
        }

        private static byte[] DigestOf(CommitmentRecord r) =>
            !string.IsNullOrEmpty(r.NoteHash)
                ? CommitmentMerkleTree.LeafDigest(Convert.FromBase64String(r.NoteHash))
                : CommitmentMerkleTree.LeafDigestLegacy(Convert.FromBase64String(r.Commitment)); // legacy: Poseidon of G1 bytes

        /// <summary>The cached leaves brought up to date by reading only the rows after them; false when they cannot be.</summary>
        private bool TryExtend(ILiteCollection<CommitmentRecord> col, LeafCache cache)
        {
            if (!cache.Valid || cache.LastId == null || cache.Digests.Count == 0 || cache.LastPosition != cache.Digests.Count - 1)
                return false;
            // The asset compared as the full load's query compares it (the database's collation, which ignores case): an
            // exact comparison here dropped a "vfx" row the full load counts, and nodes rooted the pool differently
            // (fifth review, round 33).
            var last = col.Find(x => x.TreePosition == cache.LastPosition).Where(SameAsset).ToList();
            if (last.Count != 1 || !last[0].Id.Equals(cache.LastId))
                return false;
            var newer = col.Find(x => x.TreePosition > cache.LastPosition).Where(SameAsset)
                .OrderBy(x => x.TreePosition).ToList();
            for (var i = 0; i < newer.Count; i++)
                if (newer[i].TreePosition != cache.LastPosition + 1 + i)
                    return false;
            foreach (var r in newer)
                cache.Digests.Add(DigestOf(r));
            if (newer.Count > 0)
            {
                cache.LastId = newer[^1].Id;
                cache.LastPosition = newer[^1].TreePosition;
            }
            return true;
        }

        private bool SameAsset(CommitmentRecord r) => _db.Collation.Compare(r.AssetType ?? "", _assetType) == 0;

        private sealed class LeafCache
        {
            public List<byte[]> Digests = new();
            public ObjectId? LastId;
            public long LastPosition = -1;
            public bool Valid;
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<LiteDatabase, System.Collections.Concurrent.ConcurrentDictionary<string, LeafCache>> _leafCaches = new();

        [ThreadStatic] private static long _fullLeafLoadsOnThisThread;

        /// <summary>Leaf loads this thread read in full (tests).</summary>
        internal static long FullLeafLoadsOnThisThread => _fullLeafLoadsOnThisThread;

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

        /// <summary>
        /// Clears this asset's stored tree-node rows. Nothing reads them (roots and paths come from the leaves), and they
        /// were written again whole on every appended leaf, one row at a time: each output cost the whole tree, on every
        /// node applying the block (about 0.15 s at 250 leaves and growing with the pool; fifth review, round 32). They
        /// are no longer written; rows an earlier build left are removed here.
        /// </summary>
        public void RebuildAndPersistMerkleNodes() =>
            MerkleNodes().DeleteManySafe(x => x.AssetType == _assetType);

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
