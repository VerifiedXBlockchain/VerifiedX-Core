using System.Security.Cryptography;
using NBitcoin;
using VerifiedXCore.Bitcoin.FROST;

namespace VerifiedXCore.Bitcoin.Attestation
{
    /// <summary>
    /// Bitcoin's transaction Merkle tree, written out here so that consensus depends on these lines only. All hashes are
    /// lowercase hex in the byte order block explorers print; a branch is the list of sibling hashes from the leaf up,
    /// as Electrum and Esplora serve it.
    /// </summary>
    public static class BtcMerkle
    {
        /// <summary>No Bitcoin block needs a deeper tree (2^20 leaves is over a million transactions).</summary>
        public const int MaxDepth = 20;

        /// <summary>The root reached from <paramref name="txid"/> at <paramref name="position"/> through <paramref name="branch"/>; null when the inputs are malformed.</summary>
        public static string? RootFromBranch(string? txid, long position, IReadOnlyList<string>? branch)
        {
            if (branch == null || branch.Count > MaxDepth || position < 0 || position >= (1L << branch.Count))
                return null;
            if (!TryInternal(txid, out var node))
                return null;
            var index = position;
            foreach (var sibling in branch)
            {
                if (!TryInternal(sibling, out var other))
                    return null;
                node = (index & 1) == 1 ? HashPair(other, node) : HashPair(node, other);
                index >>= 1;
            }
            return Display(node);
        }

        /// <summary>The root of a whole block's transaction ids (the last id of an odd level is paired with itself).</summary>
        public static string? RootOf(IReadOnlyList<string> txids)
        {
            if (txids == null || txids.Count == 0) return null;
            var level = new List<byte[]>(txids.Count);
            foreach (var id in txids)
            {
                if (!TryInternal(id, out var leaf)) return null;
                level.Add(leaf);
            }
            while (level.Count > 1)
                level = NextLevel(level);
            return Display(level[0]);
        }

        /// <summary>The branch that proves the transaction at <paramref name="position"/> of a block with these ids.</summary>
        public static List<string>? BranchFor(IReadOnlyList<string> txids, int position)
        {
            if (txids == null || position < 0 || position >= txids.Count) return null;
            var level = new List<byte[]>(txids.Count);
            foreach (var id in txids)
            {
                if (!TryInternal(id, out var leaf)) return null;
                level.Add(leaf);
            }
            var branch = new List<string>();
            var index = position;
            while (level.Count > 1)
            {
                var sibling = (index ^ 1) < level.Count ? level[index ^ 1] : level[index]; // odd level: paired with itself
                branch.Add(Display(sibling));
                level = NextLevel(level);
                index >>= 1;
            }
            return branch;
        }

        private static List<byte[]> NextLevel(List<byte[]> level)
        {
            var next = new List<byte[]>((level.Count + 1) / 2);
            for (var i = 0; i < level.Count; i += 2)
                next.Add(HashPair(level[i], i + 1 < level.Count ? level[i + 1] : level[i]));
            return next;
        }

        private static byte[] HashPair(byte[] left, byte[] right)
        {
            var both = new byte[64];
            Buffer.BlockCopy(left, 0, both, 0, 32);
            Buffer.BlockCopy(right, 0, both, 32, 32);
            return SHA256.HashData(SHA256.HashData(both));
        }

        /// <summary>Explorer-order hex to the byte order Bitcoin hashes in.</summary>
        private static bool TryInternal(string? hex, out byte[] bytes)
        {
            bytes = Array.Empty<byte>();
            if (string.IsNullOrEmpty(hex) || hex.Length != 64) return false;
            try { bytes = Convert.FromHexString(hex); }
            catch { return false; }
            Array.Reverse(bytes);
            return true;
        }

        private static string Display(byte[] node)
        {
            var copy = (byte[])node.Clone();
            Array.Reverse(copy);
            return Convert.ToHexString(copy).ToLowerInvariant();
        }

        /// <summary>The id of a transaction given in its serialization WITHOUT witness data: the double SHA-256 of those bytes.</summary>
        public static string TxidOfStripped(byte[] strippedBytes) => Display(SHA256.HashData(SHA256.HashData(strippedBytes)));
    }

    /// <summary>
    /// Proof that a Bitcoin transaction is in a block whose header is known (docs/PLAN-vBTC-Bitcoin-Attestation.md, 3.2).
    ///
    /// Three things beyond the hashing are enforced, each closing a known way to fool a proof checker:
    /// <list type="bullet">
    /// <item>The transaction is carried in its serialization without witness data, and its id is the hash of exactly
    /// those bytes. What is parsed and what is hashed cannot differ, and a re-serialization must give the same bytes.</item>
    /// <item>Every proof in a block has the same length as the proof of that block's coinbase transaction. All leaves of
    /// Bitcoin's tree sit at one depth; without this, an inner node of the tree can be passed off as a transaction one
    /// level up (the 64-byte transaction weakness).</item>
    /// <item>A transaction of 64 bytes or fewer is never accepted, for the same reason.</item>
    /// </list>
    /// </summary>
    public static class BtcTxInclusion
    {
        /// <summary>Largest transaction a report carries with its bytes (plan 3.2: larger ones use the signed-only form).</summary>
        public const int MaxTxBytes = 100_000;

        private static Network Net => Globals.BTCNetwork ?? FrostDkgAttestation.ConsensusNetwork;

        /// <summary>The serialization without witness data, lowercase hex: the form a report carries.</summary>
        public static string ToStrippedHex(Transaction tx) =>
            Convert.ToHexString(tx.WithOptions(TransactionOptions.None).ToBytes()).ToLowerInvariant();

        /// <summary>
        /// Checks the block's coinbase transaction and returns the depth of the block's tree, which every other proof in
        /// the block must match. Null reason on success.
        /// </summary>
        public static string? VerifyCoinbase(BtcHeader header, string? coinbaseStrippedHex, IReadOnlyList<string>? branch, out int treeDepth, out Transaction? coinbase, out string? txid)
        {
            treeDepth = 0;
            var reason = Check(header, coinbaseStrippedHex, 0, branch, branch?.Count ?? -1, out coinbase, out txid);
            if (reason != null) return reason;
            if (!coinbase!.IsCoinBase) return "the transaction given as the block's coinbase is not a coinbase transaction";
            treeDepth = branch!.Count;
            return null;
        }

        /// <summary>
        /// Checks that the transaction is at <paramref name="position"/> of the block with <paramref name="header"/>, in
        /// a tree of <paramref name="treeDepth"/> levels (from <see cref="VerifyCoinbase"/>). Null reason on success.
        /// </summary>
        public static string? Verify(BtcHeader header, string? strippedHex, long position, IReadOnlyList<string>? branch, int treeDepth, out Transaction? tx, out string? txid) =>
            Check(header, strippedHex, position, branch, treeDepth, out tx, out txid);

        private static string? Check(BtcHeader header, string? strippedHex, long position, IReadOnlyList<string>? branch, int treeDepth, out Transaction? tx, out string? txid)
        {
            tx = null;
            txid = null;
            if (header == null) return "no header";
            if (branch == null) return "no Merkle branch";
            if (treeDepth < 0 || treeDepth > BtcMerkle.MaxDepth) return "the tree depth is out of range";
            if (branch.Count != treeDepth)
                return $"the proof has {branch.Count} levels; this block's tree has {treeDepth} (every transaction of a block sits at the same depth)";
            if (string.IsNullOrEmpty(strippedHex) || strippedHex.Length % 2 != 0) return "the transaction is not valid hex";
            if (strippedHex.Length / 2 > MaxTxBytes) return $"the transaction is larger than {MaxTxBytes} bytes";

            byte[] bytes;
            try { bytes = Convert.FromHexString(strippedHex); }
            catch { return "the transaction is not valid hex"; }
            if (bytes.Length <= 64) return "a transaction of 64 bytes or fewer is not accepted";

            Transaction parsed;
            try { parsed = Transaction.Load(bytes, Net); }
            catch (Exception ex) { return $"the transaction does not parse: {ex.Message}"; }
            byte[] again;
            try { again = parsed.WithOptions(TransactionOptions.None).ToBytes(); }
            catch (Exception ex) { return $"the transaction does not serialize: {ex.Message}"; }
            if (!again.AsSpan().SequenceEqual(bytes))
                return "the transaction is not in its canonical serialization without witness data";
            if (parsed.Inputs.Count == 0 || parsed.Outputs.Count == 0) return "the transaction has no inputs or no outputs";

            var id = BtcMerkle.TxidOfStripped(bytes);
            var root = BtcMerkle.RootFromBranch(id, position, branch);
            if (root == null) return "the Merkle branch or the position is malformed";
            if (!string.Equals(root, header.MerkleRoot, StringComparison.Ordinal))
                return $"transaction {id} is not in block {header.Hash} at position {position}";

            tx = parsed;
            txid = id;
            return null;
        }
    }
}
