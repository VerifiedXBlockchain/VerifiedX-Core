namespace VerifiedXCore.Privacy
{
    /// <summary>
    /// The commitment tree the v1 circuits prove against (fund-loss audit item 1, stage 2): fixed depth 32, leaves are
    /// note hashes in tree-position order, every unused slot is the empty subtree of its height
    /// (<see cref="PoseidonV1.MerkleZero"/>), parents are <see cref="PoseidonV1.Hash2"/>(left, right), and bit i of a
    /// leaf's position selects its side at level i. The circuits' <c>merkle_path_verify</c> recomputes exactly this.
    /// Verified against the native <c>merkle_root_from_path_v1</c> and the circuits themselves in the stage-2 tests.
    /// </summary>
    public sealed class FixedDepthMerkleTree
    {
        public const int Depth = PlonkNative.TreeDepth;

        private readonly List<byte[]> _leaves;

        public FixedDepthMerkleTree(IEnumerable<byte[]> leavesInPositionOrder)
        {
            _leaves = new List<byte[]>();
            foreach (var leaf in leavesInPositionOrder)
            {
                if (leaf == null || leaf.Length != PlonkNative.ScalarSize)
                    throw new ArgumentException("Every leaf must be a 32-byte note hash.", nameof(leavesInPositionOrder));
                _leaves.Add((byte[])leaf.Clone());
            }
            if ((ulong)_leaves.Count > PrivacyMerklePolicy.MaxLeafCount)
                throw new ArgumentException("Too many leaves for a depth-32 tree.", nameof(leavesInPositionOrder));
        }

        public int LeafCount => _leaves.Count;

        public byte[] Leaf(long position) => (byte[])_leaves[(int)position].Clone();

        /// <summary>Root of the tree with every unused slot an empty subtree. An empty tree has the depth-32 zero digest.</summary>
        public byte[] Root()
        {
            if (_leaves.Count == 0)
                return PoseidonV1.MerkleZero(Depth);
            var level = new List<byte[]>(_leaves);
            for (var l = 0; l < Depth; l++)
                level = NextLevel(level, l);
            return level[0];
        }

        /// <summary>Sibling path (bottom-up, <see cref="Depth"/> digests) for the leaf at <paramref name="position"/>.</summary>
        public byte[][] Path(long position)
        {
            if (position < 0 || position >= _leaves.Count)
                throw new ArgumentOutOfRangeException(nameof(position));
            var path = new byte[Depth][];
            var level = new List<byte[]>(_leaves);
            var cur = (int)position;
            for (var l = 0; l < Depth; l++)
            {
                var sib = cur ^ 1;
                path[l] = sib < level.Count ? (byte[])level[sib].Clone() : PoseidonV1.MerkleZero(l);
                level = NextLevel(level, l);
                cur >>= 1;
            }
            return path;
        }

        /// <summary>The path as the flat 1024-byte buffer the witness format and <see cref="PoseidonV1.RootFromPath"/> take.</summary>
        public byte[] PathFlat(long position) => Flatten(Path(position));

        public static byte[] Flatten(byte[][] path)
        {
            var flat = new byte[PlonkNative.ScalarSize * Depth];
            for (var l = 0; l < Depth; l++)
                Buffer.BlockCopy(path[l], 0, flat, l * PlonkNative.ScalarSize, PlonkNative.ScalarSize);
            return flat;
        }

        /// <summary>The witness' direction bits: <see cref="Depth"/> little-endian field elements, bit i of the position at level i.</summary>
        public static byte[] IndicesFlat(long position)
        {
            var flat = new byte[PlonkNative.ScalarSize * Depth];
            for (var l = 0; l < Depth; l++)
                flat[l * PlonkNative.ScalarSize] = (byte)((position >> l) & 1);
            return flat;
        }

        /// <summary>Recomputes the root from a leaf and its path in managed code (the native equivalent is <see cref="PoseidonV1.RootFromPath"/>).</summary>
        public static byte[] RootFromPath(byte[] leaf32, long position, byte[][] path)
        {
            if (path.Length != Depth) throw new ArgumentException("Path must have 32 levels.", nameof(path));
            var cur = leaf32;
            for (var l = 0; l < Depth; l++)
                cur = ((position >> l) & 1) == 0 ? PoseidonV1.Hash2(cur, path[l]) : PoseidonV1.Hash2(path[l], cur);
            return cur;
        }

        private static List<byte[]> NextLevel(List<byte[]> level, int l)
        {
            var next = new List<byte[]>((level.Count + 1) / 2);
            for (var i = 0; i < level.Count; i += 2)
            {
                var left = level[i];
                var right = i + 1 < level.Count ? level[i + 1] : PoseidonV1.MerkleZero(l);
                next.Add(PoseidonV1.Hash2(left, right));
            }
            return next;
        }
    }
}
