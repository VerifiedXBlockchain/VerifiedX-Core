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

        /// <summary>Bit 0: full PLONK verify (circuits + SRS) wired in native — see <see cref="PLONKSetup.IsProofVerificationImplemented"/>.</summary>
        public const uint CapVerifyV1 = 1;

        /// <summary>Bit 1: <c>public_inputs</c> v1 (VFXPI1) layout validation in native.</summary>
        public const uint CapParsePublicInputsV1 = 2;

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint plonk_capabilities();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int plonk_load_params(string? paramsPath);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int pedersen_commit(ulong amountScaled, byte[] randomness, byte[] commitmentOut);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int pedersen_verify(byte[] commitment, ulong amountScaled, byte[] randomness);

        /// <summary>G1 point addition on compressed Pedersen commitments (homomorphic: C(a,r)+C(b,s)=C(a+b,r+s) in Fr).</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int pedersen_commitment_add(byte[] commitmentA, byte[] commitmentB, byte[] commitmentOut);

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
