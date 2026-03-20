namespace ReserveBlockCore.Privacy
{
    /// <summary>
    /// v0 PLONK proving via <see cref="PlonkNative.plonk_prove_v0"/> (requires <b>VXPLNK02</b> params with prover key).
    /// Proves SHA-256 digest binding to the full VFXPI1 blob — not full wallet privacy yet.
    /// </summary>
    public static class PlonkProverV0
    {
        /// <summary>Native <c>plonk_prove_v0</c> is available (<see cref="PlonkNative.CapProveV1"/>).</summary>
        public static bool IsProveAvailable =>
            (PlonkNative.plonk_capabilities() & PlonkNative.CapProveV1) != 0;

        /// <summary>
        /// Generates a PLONK proof byte vector for <paramref name="publicInputsVfxpi1"/> (exact bytes nodes verify with <see cref="PlonkProofVerifier"/>).
        /// </summary>
        /// <returns><c>Success</c> code from <see cref="PlonkNative"/>; proof in <paramref name="proof"/> when zero.</returns>
        public static int TryProve(PlonkCircuitType circuitType, byte[] publicInputsVfxpi1, out byte[]? proof)
        {
            proof = null;
            if (publicInputsVfxpi1 == null || publicInputsVfxpi1.Length == 0)
                return PlonkNative.ErrParam;
            if (!IsProveAvailable)
                return PlonkNative.ErrNotImplemented;

            const int maxFirst = 512 * 1024;
            var buf = new byte[maxFirst];
            nuint len = maxFirst;
            var code = PlonkNative.plonk_prove_v0(
                (byte)circuitType,
                publicInputsVfxpi1,
                (nuint)publicInputsVfxpi1.Length,
                buf,
                ref len);

            if (code == PlonkNative.ErrParam && len > maxFirst)
            {
                buf = new byte[(int)len];
                len = (nuint)buf.Length;
                code = PlonkNative.plonk_prove_v0(
                    (byte)circuitType,
                    publicInputsVfxpi1,
                    (nuint)publicInputsVfxpi1.Length,
                    buf,
                    ref len);
            }

            if (code != PlonkNative.Success)
                return code;

            proof = new byte[(int)len];
            Buffer.BlockCopy(buf, 0, proof, 0, (int)len);
            return PlonkNative.Success;
        }
    }
}
