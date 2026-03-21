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

            // Try the typed poseidon_note_hash first (available in newer DLLs).
            // Fall back to the generic poseidon_hash(amount_le32 || randomness32) which
            // is equivalent: Poseidon(amount_scalar, randomness_scalar) over BLS12-381 Fr.
            try
            {
                var hashOut = new byte[PlonkNative.ScalarSize];
                int code = PlonkNative.poseidon_note_hash(amountScaled, randomness32, hashOut);
                if (code == PlonkNative.Success)
                    return hashOut;
            }
            catch (EntryPointNotFoundException)
            {
                // DLL doesn't export poseidon_note_hash — use generic path below.
            }

            // Generic fallback: encode amount as 32-byte LE scalar, concatenate with randomness
            var inputs = new byte[PlonkNative.ScalarSize * 2]; // 64 bytes
            var amountBytes = BitConverter.GetBytes(amountScaled); // 8 bytes LE
            Buffer.BlockCopy(amountBytes, 0, inputs, 0, amountBytes.Length);
            // Remaining bytes 8..31 are already 0 (zero-padded to 32-byte scalar)
            Buffer.BlockCopy(randomness32, 0, inputs, PlonkNative.ScalarSize, PlonkNative.ScalarSize);

            var result = new byte[PlonkNative.ScalarSize];
            int rc = PlonkNative.poseidon_hash(inputs, (nuint)inputs.Length, result);
            if (rc != PlonkNative.Success)
                throw new InvalidOperationException($"poseidon_hash returned {rc}");
            return result;
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