namespace ReserveBlockCore.Privacy
{
    /// <summary>
    /// Policy for the shielded commitment Merkle tree. Must stay aligned with in-circuit Merkle gadgets (Phase 4).
    /// </summary>
    public static class PrivacyMerklePolicy
    {
        /// <summary>Maximum tree height (root to leaf) used for consensus / circuit design: 2^32 leaves.</summary>
        public const int MaxTreeDepth = 32;

        /// <summary>Maximum leaf count (inclusive of design bound). In-memory C# paths use smaller collections in practice.</summary>
        public const ulong MaxLeafCount = 1UL << MaxTreeDepth;

        /// <summary>Digest size for Poseidon nodes (BLS12-381 scalar field bytes).</summary>
        public const int DigestSizeBytes = PlonkNative.ScalarSize;

        /// <summary>
        /// Number of 32-byte sibling digests in an inclusion proof for <paramref name="leafCount"/> leaves
        /// (same pairing rule as native <c>plonk_ffi</c> and <see cref="ShieldedMerkleStore"/>).
        /// </summary>
        public static int GetExpectedProofSizeBytes(ulong leafCount)
        {
            if (leafCount <= 1)
                return 0;
            int siblings = 0;
            ulong n = leafCount;
            while (n > 1)
            {
                siblings++;
                n = (n + 1UL) / 2UL;
            }
            return siblings * DigestSizeBytes;
        }

        /// <summary>Whether <paramref name="leafCount"/> is within the protocol design bound.</summary>
        public static bool IsLeafCountAllowed(ulong leafCount) => leafCount <= MaxLeafCount;
    }
}
