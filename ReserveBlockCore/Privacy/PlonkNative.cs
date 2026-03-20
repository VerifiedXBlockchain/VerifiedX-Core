using System.Runtime.InteropServices;

namespace ReserveBlockCore.Privacy
{
    /// <summary>
    /// P/Invoke for <c>plonk_ffi</c> native library (Pedersen, Poseidon, Merkle, nullifiers).
    /// PLONK prove/verify return <see cref="ErrNotImplemented"/> until Phase 4 circuits ship.
    /// </summary>
    public static class PlonkNative
    {
        private const string DllName = "plonk_ffi";

        public const int G1CompressedSize = 48;
        public const int ScalarSize = 32;

        public const int Success = 0;
        public const int ErrNull = -1;
        public const int ErrUtf8 = -2;
        public const int ErrCrypto = -4;
        public const int ErrParam = -5;
        public const int ErrNotImplemented = -6;

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int plonk_load_params(string? paramsPath);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int pedersen_commit(ulong amountScaled, byte[] randomness, byte[] commitmentOut);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int pedersen_verify(byte[] commitment, ulong amountScaled, byte[] randomness);

        /// <summary>Variable-length input; hashes as sequence of 32-byte big-endian field elements.</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int poseidon_hash(byte[] inputs, nuint inputsLen, byte[] hashOut);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int merkle_tree_add(string treeId, byte[] commitment, out ulong positionOut);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int merkle_tree_prove(string treeId, ulong position, byte[] proofOut, ref nuint proofOutLen, byte[] rootOut);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int nullifier_derive(byte[] viewingKey, byte[] commitment, ulong treePosition, byte[] nullifierOut);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int plonk_verify(byte circuitType, byte[] proof, nuint proofLen, byte[] publicInputs, nuint publicInputsLen);
    }
}
