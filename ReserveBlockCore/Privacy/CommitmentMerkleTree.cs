using System.Diagnostics.CodeAnalysis;

namespace ReserveBlockCore.Privacy
{
    /// <summary>
    /// Poseidon-based Merkle helpers (native). Leaf = Poseidon note hash (amount, randomness); parent = Poseidon(left||right).
    /// Proof format matches <c>plonk_ffi</c> <see cref="PlonkNative.merkle_tree_prove"/> (sibling digests bottom-up).
    /// <para>
    /// <b>v2 (note-hash leaves):</b> The Merkle leaf is <c>Poseidon(amount_scaled, randomness)</c> computed by
    /// <see cref="NoteHashService"/>. This binds amounts to commitments in-circuit and prevents inflation attacks.
    /// The legacy <see cref="LeafDigestLegacy"/> (Poseidon of G1 bytes) is retained for backward compatibility
    /// during migration but should not be used for new commitments.
    /// </para>
    /// </summary>
    public static class CommitmentMerkleTree
    {
        /// <summary>
        /// Preferred leaf digest: the Poseidon note hash itself (already 32 bytes).
        /// <para>Use <see cref="NoteHashService.Compute"/> to produce the note hash, then pass it here.</para>
        /// </summary>
        public static byte[] LeafDigest(ReadOnlySpan<byte> noteHash32)
        {
            if (noteHash32.Length != PlonkNative.ScalarSize)
                throw new ArgumentException("Note hash must be 32 bytes.", nameof(noteHash32));
            return noteHash32.ToArray();
        }

        /// <summary>
        /// Legacy leaf digest: <c>Poseidon(G1 commitment bytes)</c>. Retained for backward compatibility only.
        /// </summary>
        public static byte[] LeafDigestLegacy(ReadOnlySpan<byte> g1CommitmentCompressed)
        {
            var buf = g1CommitmentCompressed.ToArray();
            var out32 = new byte[PlonkNative.ScalarSize];
            int code = PlonkNative.poseidon_hash(buf, (nuint)buf.Length, out32);
            if (code != PlonkNative.Success)
                throw new InvalidOperationException($"poseidon_hash failed: {code}");
            return out32;
        }

        public static byte[] Combine(ReadOnlySpan<byte> left32, ReadOnlySpan<byte> right32)
        {
            if (left32.Length != PlonkNative.ScalarSize || right32.Length != PlonkNative.ScalarSize)
                throw new ArgumentException("Expected 32-byte child digests.");
            var combined = new byte[64];
            left32.CopyTo(combined);
            right32.CopyTo(combined.AsSpan(32));
            var out32 = new byte[PlonkNative.ScalarSize];
            int code = PlonkNative.poseidon_hash(combined, (nuint)combined.Length, out32);
            if (code != PlonkNative.Success)
                throw new InvalidOperationException($"poseidon_hash failed: {code}");
            return out32;
        }

        /// <summary>
        /// Builds a Merkle inclusion proof for <paramref name="leafIndex"/> over the given leaf digests (32 bytes each).
        /// </summary>
        public static bool TryBuildProof(IReadOnlyList<byte[]> leafDigests32, long leafIndex, [NotNullWhen(true)] out byte[]? proofBytes)
        {
            proofBytes = null;
            var n = leafDigests32.Count;
            if (n == 0 || leafIndex < 0 || leafIndex >= n)
                return false;
            if ((ulong)n > PrivacyMerklePolicy.MaxLeafCount)
                return false;

            var proof = new List<byte>(PrivacyMerklePolicy.GetExpectedProofSizeBytes((ulong)n));
            int idx = (int)leafIndex;
            var level = new List<byte[]>(n);
            foreach (var d in leafDigests32)
            {
                if (d.Length != PrivacyMerklePolicy.DigestSizeBytes)
                    throw new ArgumentException("Each leaf digest must be 32 bytes.", nameof(leafDigests32));
                level.Add((byte[])d.Clone());
            }

            while (level.Count > 1)
            {
                int siblingIdx = (idx % 2 == 0) ? idx + 1 : idx - 1;
                var sibling = siblingIdx < level.Count ? level[siblingIdx] : level[idx];
                proof.AddRange(sibling);
                var next = new List<byte[]>();
                for (int i = 0; i < level.Count; i += 2)
                {
                    var left = level[i];
                    var right = (i + 1 < level.Count) ? level[i + 1] : left;
                    next.Add(Combine(left, right));
                }
                level = next;
                idx /= 2;
            }

            proofBytes = proof.ToArray();
            return true;
        }

        /// <summary>
        /// Recomputes the Merkle root from a leaf digest and sibling path (same semantics as proof builder).
        /// </summary>
        public static bool TryComputeRootFromProof(
            ReadOnlySpan<byte> leafDigest32,
            long leafIndex,
            long leafCount,
            ReadOnlySpan<byte> proof,
            [NotNullWhen(true)] out byte[]? rootOut)
        {
            rootOut = null;
            if (leafDigest32.Length != PrivacyMerklePolicy.DigestSizeBytes)
                return false;
            if (leafCount <= 0 || leafIndex < 0 || leafIndex >= leafCount)
                return false;
            if ((ulong)leafCount > PrivacyMerklePolicy.MaxLeafCount)
                return false;

            var expectedLen = PrivacyMerklePolicy.GetExpectedProofSizeBytes((ulong)leafCount);
            if (proof.Length != expectedLen)
                return false;

            var cur = leafDigest32.ToArray();
            long idx = leafIndex;
            long count = leafCount;
            int offset = 0;
            while (count > 1)
            {
                var sib = proof.Slice(offset, PrivacyMerklePolicy.DigestSizeBytes);
                offset += PrivacyMerklePolicy.DigestSizeBytes;
                if ((idx & 1) == 0)
                    cur = Combine(cur, sib);
                else
                    cur = Combine(sib, cur);
                idx /= 2;
                count = (count + 1) / 2;
            }

            rootOut = cur;
            return true;
        }

        /// <summary>
        /// Returns true if <paramref name="proof"/> proves <paramref name="leafDigest32"/> is under <paramref name="expectedRoot32"/>.
        /// </summary>
        public static bool VerifyInclusionProof(
            ReadOnlySpan<byte> leafDigest32,
            long leafIndex,
            long leafCount,
            ReadOnlySpan<byte> proof,
            ReadOnlySpan<byte> expectedRoot32)
        {
            if (!TryComputeRootFromProof(leafDigest32, leafIndex, leafCount, proof, out var root) || root == null)
                return false;
            return expectedRoot32.SequenceEqual(root);
        }
    }
}
