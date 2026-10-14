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
            var levels = Levels(txids);
            return levels == null ? null : BranchFrom(levels, position);
        }

        /// <summary>
        /// Every level of a block's tree, leaves first; null when an id does not read. Built once, every branch of the
        /// block is read from it (<see cref="BranchFrom"/>): the tree was built again for each event, and a Bitcoin block
        /// with a few thousand small payments to a vault took longer to plan than a VFX block lasts (fourth review, round 9).
        /// </summary>
        public static List<List<byte[]>>? Levels(IReadOnlyList<string> txids)
        {
            if (txids == null || txids.Count == 0) return null;
            var level = new List<byte[]>(txids.Count);
            foreach (var id in txids)
            {
                if (!TryInternal(id, out var leaf)) return null;
                level.Add(leaf);
            }
            var levels = new List<List<byte[]>> { level };
            while (level.Count > 1)
            {
                level = NextLevel(level);
                levels.Add(level);
            }
            return levels;
        }

        /// <summary>The branch of the transaction at <paramref name="position"/>, read from a block's levels; the same as <see cref="BranchFor"/>.</summary>
        public static List<string>? BranchFrom(List<List<byte[]>> levels, int position)
        {
            if (levels == null || levels.Count == 0 || position < 0 || position >= levels[0].Count) return null;
            var branch = new List<string>();
            var index = position;
            for (var l = 0; l < levels.Count - 1; l++)
            {
                var level = levels[l];
                var sibling = (index ^ 1) < level.Count ? level[index ^ 1] : level[index]; // odd level: paired with itself
                branch.Add(Display(sibling));
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

        /// <summary>The id of a transaction given the first of its two SHA-256 hashes.</summary>
        public static string TxidOfFirstHash(byte[] firstHash) => Display(SHA256.HashData(firstHash));
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
    /// level up (the 64-byte transaction weakness). The coinbase is carried whole, or, when a miner made it too large
    /// for that, shown to be a transaction by its length (<see cref="VerifyCoinbaseTail"/>).</item>
    /// <item>A transaction of 64 bytes or fewer is never accepted, for the same reason.</item>
    /// </list>
    /// </summary>
    public static class BtcTxInclusion
    {
        /// <summary>
        /// Largest transaction a report carries, in bytes without witness data: the largest a Bitcoin node relays
        /// (400,000 weight units is 100,000 such bytes), so every transaction anyone can send in the ordinary way can
        /// be proven. A larger one has to be handed to a miner directly; it is never an event, and what it pays to a
        /// vault is not credited (plan 3.2). A vault's own withdrawals are a few hundred bytes.
        /// </summary>
        public const int MaxTxBytes = 100_000;
        /// <summary>A coinbase proof in its constant-size form carries at most one hash block of the coinbase.</summary>
        public const int MaxCoinbaseTailBytes = 64;
        /// <summary>No Bitcoin transaction is larger than a block.</summary>
        public const long MaxAnyTxBytes = 4_000_000;

        /// <summary>The network the chain follows, from the chain's own setting: never from local configuration.</summary>
        private static Network Net => FrostDkgAttestation.ConsensusNetwork;

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
            coinbase = null;
            txid = null;
            if (coinbaseStrippedHex != null && coinbaseStrippedHex.Length / 2 > BtcAttestRules.MaxCoinbaseProofBytes)
                return $"a coinbase larger than {BtcAttestRules.MaxCoinbaseProofBytes} bytes is proven by its last block and its length, not carried";
            var reason = Check(header, coinbaseStrippedHex, 0, branch, branch?.Count ?? -1, out coinbase, out txid, asCoinbase: true);
            if (reason != null) return reason;
            if (!coinbase!.IsCoinBase) return "the transaction given as the block's coinbase is not a coinbase transaction";
            treeDepth = branch!.Count;
            return null;
        }

        /// <summary>
        /// The depth of the block's tree from a coinbase too large to carry. The first leaf of the tree is shown to be
        /// the hash of <paramref name="totalBytes"/> bytes, more than 64, by finishing its SHA-256 from the state before
        /// its last block (<see cref="BtcSha256"/>): an inner node of the tree is the hash of exactly 64 bytes, so this
        /// node is a transaction, and its branch is as long as the tree is deep. Nothing else is learnt about the
        /// coinbase and nothing else is needed: what it pays is never read from this form. Null reason on success.
        /// </summary>
        public static string? VerifyCoinbaseTail(BtcHeader header, string? stateHex, string? tailHex, long? totalBytes, IReadOnlyList<string>? branch, out int treeDepth)
        {
            treeDepth = 0;
            if (header == null) return "no header";
            if (branch == null) return "no Merkle branch";
            if (branch.Count > BtcMerkle.MaxDepth) return "the tree depth is out of range";
            if (stateHex == null || tailHex == null || totalBytes == null) return "no coinbase proof";
            if (stateHex.Length != BtcSha256.StateBytes * 2 || tailHex.Length % 2 != 0 || tailHex.Length == 0 || tailHex.Length / 2 > MaxCoinbaseTailBytes)
                return "the coinbase proof is malformed";
            if (stateHex != stateHex.ToLowerInvariant() || tailHex != tailHex.ToLowerInvariant()) return "the coinbase proof is not lowercase hex";
            var total = totalBytes.Value;
            if (total <= BtcAttestRules.MaxCoinbaseProofBytes) return $"a coinbase of up to {BtcAttestRules.MaxCoinbaseProofBytes} bytes is carried whole";
            if (total > MaxAnyTxBytes) return "the coinbase length is out of range";

            byte[] state, tail;
            try { state = Convert.FromHexString(stateHex); tail = Convert.FromHexString(tailHex); }
            catch { return "the coinbase proof is not valid hex"; }
            // One form per coinbase: the tail is what is left after the whole blocks, or the last whole block.
            if (tail.Length != (total % 64 == 0 ? 64 : total % 64)) return "the coinbase proof carries exactly the coinbase's last block";
            var first = BtcSha256.Finish(state, tail, total);
            if (first == null) return "the coinbase proof is malformed";

            var id = BtcMerkle.TxidOfFirstHash(first);
            var root = BtcMerkle.RootFromBranch(id, 0, branch);
            if (root == null) return "the Merkle branch is malformed";
            if (!string.Equals(root, header.MerkleRoot, StringComparison.Ordinal))
                return $"the coinbase proof does not lead to block {header.Hash}";
            treeDepth = branch.Count;
            return null;
        }

        /// <summary>
        /// Checks that the transaction is at <paramref name="position"/> of the block with <paramref name="header"/>, in
        /// a tree of <paramref name="treeDepth"/> levels (from <see cref="VerifyCoinbase"/>). Null reason on success.
        /// </summary>
        public static string? Verify(BtcHeader header, string? strippedHex, long position, IReadOnlyList<string>? branch, int treeDepth, out Transaction? tx, out string? txid) =>
            Check(header, strippedHex, position, branch, treeDepth, out tx, out txid);

        /// <summary>The size of an inner node of the tree: two hashes.</summary>
        public const int InnerNodeBytes = 64;

        private static string? Check(BtcHeader header, string? strippedHex, long position, IReadOnlyList<string>? branch, int treeDepth, out Transaction? tx, out string? txid, bool asCoinbase = false)
        {
            tx = null;
            txid = null;
            if (header == null) return "no header";
            if (branch == null) return "no Merkle branch";
            if (treeDepth < 0 || treeDepth > BtcMerkle.MaxDepth) return "the tree depth is out of range";
            if (branch.Count != treeDepth)
                return $"the proof has {branch.Count} levels; this block's tree has {treeDepth} (every transaction of a block sits at the same depth)";
            if (string.IsNullOrEmpty(strippedHex) || strippedHex.Length % 2 != 0) return "the transaction is not valid hex";
            if (!string.Equals(strippedHex, strippedHex.ToLowerInvariant(), StringComparison.Ordinal)) return "the transaction is not lowercase hex";
            if (strippedHex.Length / 2 > MaxTxBytes) return $"the transaction is larger than {MaxTxBytes} bytes";

            byte[] bytes;
            try { bytes = Convert.FromHexString(strippedHex); }
            catch { return "the transaction is not valid hex"; }
            // No transaction is refused for its size being that of an inner node (64 bytes) or less. What stops an
            // inner node being passed off as a transaction, or a made-up transaction being hung below a real 64-byte
            // one, is the depth: every proof of a block has exactly as many levels as its coinbase's, and bytes that
            // are an inner node do not parse as a coinbase. Refusing small transactions on top of that left a block
            // with a small coinbase without any provable event, and a small spend of a vault coin either stopped all
            // reporting (when the scanner reported it) or went unrecorded (when it did not), the coin then counted as
            // held for good (second path audit, 14 October 2026).

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
