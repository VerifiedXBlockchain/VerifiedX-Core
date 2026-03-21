using System.Runtime.InteropServices;

namespace ReserveBlockCore.Privacy
{
    /// <summary>
    /// P/Invoke for <c>plonk_ffi</c> native library (Pedersen, Poseidon, Merkle, nullifiers).
    /// v0 PLONK verify/prove when <c>VXPLNK02</c> params are loaded (sibling <c>plonk</c> repo); otherwise verify may return <see cref="ErrNotImplemented"/> after layout checks.
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

        /// <summary>Bit 2: v0 <see cref="plonk_prove_v0"/> available (<b>VXPLNK02</b> params include prover key).</summary>
        public const uint CapProveV1 = 4;

        /// <summary>Bit 3: v1 real circuits loaded (<b>VXPLNK03</b> params — shield/transfer/unshield/fee VKs).</summary>
        public const uint CapV1Circuits = 8;

        /// <summary>Bit 4: v1 prover keys available (VXPLNK03 with prover keys — full proving capability).</summary>
        public const uint CapV1Prove = 16;

        /// <summary>Tree depth used by the v1 circuits (Merkle path = 32 sibling hashes).</summary>
        public const int TreeDepth = 32;

        /// <summary>Size of one transfer input in the flat witness wire format (bytes).</summary>
        public const int TransferInputWireSize = 8 + ScalarSize + ScalarSize + 8 + ScalarSize * TreeDepth + ScalarSize * TreeDepth; // 2128

        /// <summary>Size of one output in the flat witness wire format (bytes).</summary>
        public const int OutputWireSize = 8 + ScalarSize; // 40

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

        /// <summary>
        /// Compute Poseidon note hash: <c>note_hash = Poseidon(amount_scaled, randomness_fr)</c>.
        /// This 32-byte digest is used as the Merkle leaf and for in-circuit amount binding.
        /// </summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int poseidon_note_hash(ulong amountScaled, byte[] randomness, byte[] hashOut);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int plonk_verify(byte circuitType, byte[] proof, nuint proofLen, byte[] publicInputs, nuint publicInputsLen);

        /// <summary>v0 prove: <paramref name="proofOut"/> must hold at least <paramref name="proofOutLen"/> bytes (in/out; grows if <see cref="ErrParam"/>).</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int plonk_prove_v0(byte circuitType, byte[] publicInputs, nuint publicInputsLen, byte[] proofOut, ref nuint proofOutLen);

        // ─── v1 circuit FFI (VXPLNK03) ────────────────────────────────────

        /// <summary>
        /// Derive nullifier using note_hash (v1, circuit-compatible): <c>Poseidon(viewingKey, noteHash, position)</c>.
        /// </summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int nullifier_derive_v1(byte[] viewingKey, byte[] noteHash, ulong treePosition, byte[] nullifierOut);

        /// <summary>
        /// Generate a Shield circuit proof (v1). Requires VXPLNK03 with prover keys.
        /// Returns proof bytes + public input bytes in separate output buffers.
        /// </summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int plonk_prove_shield(
            ulong amountScaled,
            byte[] randomness,
            byte[] proofOut, ref nuint proofOutLen,
            byte[] piOut, ref nuint piOutLen);

        /// <summary>
        /// Generate a Transfer circuit proof (v1, 2-in/2-out).
        /// <paramref name="witnessData"/> is the flat witness blob (4384 bytes).
        /// </summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int plonk_prove_transfer(
            byte[] witnessData, nuint witnessDataLen,
            byte[] proofOut, ref nuint proofOutLen,
            byte[] piOut, ref nuint piOutLen);

        /// <summary>
        /// Generate an Unshield circuit proof (v1).
        /// <paramref name="witnessData"/> is the flat witness blob (4344 bytes).
        /// </summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int plonk_prove_unshield(
            byte[] witnessData, nuint witnessDataLen,
            byte[] proofOut, ref nuint proofOutLen,
            byte[] piOut, ref nuint piOutLen);

        /// <summary>
        /// Generate a Fee circuit proof (v1, 1-in/1-out).
        /// <paramref name="witnessData"/> is the flat witness blob (2208 bytes).
        /// </summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int plonk_prove_fee(
            byte[] witnessData, nuint witnessDataLen,
            byte[] proofOut, ref nuint proofOutLen,
            byte[] piOut, ref nuint piOutLen);
    }
}
