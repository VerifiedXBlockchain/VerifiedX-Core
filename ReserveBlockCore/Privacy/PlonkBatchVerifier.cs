namespace ReserveBlockCore.Privacy
{
    /// <summary>
    /// Sequential batch verification until native <c>plonk_batch_verify</c> is exported from <c>plonk_ffi</c>.
    /// </summary>
    public static class PlonkBatchVerifier
    {
        public static bool TryVerifyAll(IReadOnlyList<(PlonkCircuitType circuit, byte[] proof, byte[] publicInputs)> items, out int firstInvalidIndex)
        {
            firstInvalidIndex = -1;
            for (var i = 0; i < items.Count; i++)
            {
                var (c, p, pi) = items[i];
                var r = PlonkProofVerifier.VerifyRaw(c, p, pi);
                if (r == PlonkVerifyResult.Valid)
                    continue;
                if (r == PlonkVerifyResult.NotImplemented)
                    continue;
                firstInvalidIndex = i;
                return false;
            }
            return true;
        }
    }
}
