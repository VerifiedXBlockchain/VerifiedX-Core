namespace ReserveBlockCore.Privacy
{
    /// <summary>
    /// Poseidon-based Merkle helpers (native). Leaf = Poseidon(G1 commitment bytes); parent = Poseidon(left||right).
    /// </summary>
    public static class CommitmentMerkleTree
    {
        public static byte[] LeafDigest(ReadOnlySpan<byte> g1CommitmentCompressed)
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
    }
}
