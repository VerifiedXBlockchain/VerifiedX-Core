namespace ReserveBlockCore.Privacy
{
    /// <summary>
    /// Computes Poseidon note hashes: <c>note_hash = Poseidon(amount_scaled, randomness)</c>.
    /// <para>
    /// The note hash is the Merkle leaf in the shielded pool and provides in-circuit
    /// binding between the claimed amount and the on-chain commitment. This prevents
    /// inflation attacks where a prover could lie about input amounts during Z→Z transfers.
    /// </para>
    /// <para>
    /// G1 Pedersen commitments remain for external verification (homomorphic supply auditing,
    /// <c>pedersen_verify</c>, <c>pedersen_commitment_add</c>). Both the note hash and
    /// the Pedersen commitment are computed from the same <c>(amount, randomness)</c> pair.
    /// </para>
    /// </summary>
    public static class NoteHashService
    {
        /// <summary>
        /// Computes a 32-byte Poseidon note hash from the scaled amount and 32-byte randomness.
        /// </summary>
        public static byte[] Compute(ulong amountScaled, byte[] randomness32)
        {
            if (randomness32.Length != PlonkNative.ScalarSize)
                throw new ArgumentException("Randomness must be 32 bytes.", nameof(randomness32));

            var hashOut = new byte[PlonkNative.ScalarSize];
            int code = PlonkNative.poseidon_note_hash(amountScaled, randomness32, hashOut);
            if (code != PlonkNative.Success)
                throw new InvalidOperationException($"poseidon_note_hash returned {code}");
            return hashOut;
        }

        /// <summary>
        /// Computes the note hash and returns it as a Base64 string.
        /// </summary>
        public static string ComputeBase64(ulong amountScaled, byte[] randomness32)
            => Convert.ToBase64String(Compute(amountScaled, randomness32));

        /// <summary>
        /// Computes note hash from a C# decimal amount (applies 10^18 scaling) and 32-byte randomness.
        /// </summary>
        public static byte[] ComputeFromDecimal(decimal amount, byte[] randomness32)
        {
        if (!PrivacyPedersenAmount.TryToScaledU64(amount, out var scaled, out var err))
            throw new ArgumentException(err ?? "Amount out of range.");
            return Compute(scaled, randomness32);
        }

        /// <summary>
        /// Computes note hash from a C# decimal amount and returns Base64.
        /// </summary>
        public static string ComputeFromDecimalBase64(decimal amount, byte[] randomness32)
            => Convert.ToBase64String(ComputeFromDecimal(amount, randomness32));
    }
}