namespace VerifiedXCore.Privacy
{
    /// <summary>
    /// A pool's Merkle root kept up to date leaf by leaf: one completed left node per level. A root from it costs one hash
    /// per level, where the root was rebuilt from every leaf for each private transaction applied (fifth review, rounds 32
    /// and 33: a pool can be grown about 100 leaves a block at no cost before the proof rules, so per-transaction work
    /// that grows with the pool would stop the chain in days).
    /// <para>
    /// The root is exactly the one the level-by-level computation gives. At level h the last node (index k) is built from
    /// the one below it: when k is odd its left sibling is a completed node (the latest completed even-index node of that
    /// level, kept here), and when k is even the right side is padding: the level's zero digest in the fixed-depth tree,
    /// the node itself in the legacy tree (which stops at the level with one node).
    /// </para>
    /// </summary>
    internal sealed class MerkleFrontier
    {
        private const int Levels = 64;

        public bool FixedDepth { get; }
        public long Count { get; private set; }
        private readonly byte[]?[] _branch = new byte[]?[Levels];
        private byte[]? _last;

        public MerkleFrontier(bool fixedDepth) { FixedDepth = fixedDepth; }

        public MerkleFrontier Clone()
        {
            var copy = new MerkleFrontier(FixedDepth) { Count = Count, _last = _last };
            Array.Copy(_branch, copy._branch, Levels);
            return copy;
        }

        private byte[] Parent(byte[] left, byte[] right) =>
            FixedDepth ? PoseidonV1.Hash2(left, right) : CommitmentMerkleTree.Combine(left, right);

        public void Append(byte[] leaf)
        {
            var node = leaf;
            var index = Count;
            for (var h = 0; h < Levels; h++)
            {
                if (((index >> h) & 1) == 0)
                {
                    _branch[h] = node;
                    break;
                }
                node = Parent(_branch[h]!, node);
            }
            _last = leaf;
            Count++;
        }

        /// <summary>The root: the fixed-depth tree's zero digest when empty, null for an empty legacy tree.</summary>
        public byte[]? Root()
        {
            if (Count == 0)
                return FixedDepth ? PoseidonV1.MerkleZero(FixedDepthMerkleTree.Depth) : null;
            var node = _last!;
            var k = Count - 1;
            if (FixedDepth)
            {
                for (var h = 0; h < FixedDepthMerkleTree.Depth; h++)
                {
                    node = (k & 1) == 1 ? Parent(_branch[h]!, node) : Parent(node, PoseidonV1.MerkleZero(h));
                    k >>= 1;
                }
                return node;
            }
            for (var h = 0; k > 0; h++)
            {
                node = (k & 1) == 1 ? Parent(_branch[h]!, node) : Parent(node, node);
                k >>= 1;
            }
            return node;
        }
    }
}
