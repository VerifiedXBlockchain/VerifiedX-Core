using System.Runtime.InteropServices;

namespace VerifiedXCore.Privacy
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
        /// <summary>
        /// Bit 5: the library verifies v1 proofs against VFXPI1 <b>version 2</b> public inputs (rebuilt at the circuits' own
        /// positions from on-chain fields) and exports the circuit-compatible Poseidon (<see cref="poseidon_hash_2"/>,
        /// <see cref="poseidon_hash_3"/>, <see cref="poseidon_note_hash"/>, <see cref="poseidon_merkle_zero"/>,
        /// <see cref="merkle_root_from_path_v1"/>). Fund-loss audit item 1, stage 2.
        /// </summary>
        public const uint CapVfxPi2Verify = 32;
        /// <summary>
        /// Bit 6: the v2 owner-bound circuits are loaded (<b>VXPLNK04</b>): notes carry an owner key, spends prove the owner's
        /// nullifier key, the Unshield binds its recipient, <see cref="plonk_verify"/> takes VFXPI1 version-3 blobs, and the
        /// witness formats below are the v2 ones. Fund-loss re-audit (stage 3), Oct 2026.
        /// </summary>
        public const uint CapV2Circuits = 64;

        /// <summary>Tree depth used by the v1 circuits (Merkle path = 32 sibling hashes).</summary>
        public const int TreeDepth = 32;

        /// <summary>Size of one transfer input in the flat witness wire format (bytes).</summary>
        public const int TransferInputWireSize = 8 + ScalarSize + ScalarSize + 8 + ScalarSize * TreeDepth + ScalarSize * TreeDepth; // 2128

        /// <summary>Size of one output in the flat witness wire format (bytes).</summary>
        public const int OutputWireSize = 8 + ScalarSize + ScalarSize; // 72 (v2: amount, randomness, owner_pk)

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

        // ─── Circuit-compatible Poseidon (fund-loss audit item 1, stage 2; CapVfxPi2Verify) ─────────────
        // Every field element crosses as 32 little-endian bytes in arkworks canonical form (exactly the encoding the
        // plonk_prove_* witness blobs use); a value at or above the BLS12-381 scalar modulus returns ErrCrypto.
        // These are the hashes the v1 circuits compute; the older poseidon_hash is a chained big-endian sponge and is
        // NOT what the circuits compute for two inputs.

        /// <summary>
        /// Compute Poseidon note hash: <c>note_hash = Poseidon(amount_scaled, randomness_fr)</c>.
        /// This 32-byte digest is used as the Merkle leaf and for in-circuit amount binding.
        /// </summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int poseidon_note_hash(ulong amountScaled, byte[] randomness, byte[] hashOut);
        /// <summary><c>owner_pk = Poseidon(OWNER_PK_DOMAIN, nullifier_key)</c> (v2).</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int poseidon_owner_pk(byte[] nullifierKey, byte[] hashOut);
        /// <summary><c>note_hash = Poseidon(amount_scaled, randomness, owner_pk)</c>: the v2 owner-bound leaf.</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int poseidon_note_hash_v2(ulong amountScaled, byte[] randomness, byte[] ownerPk, byte[] hashOut);
        /// <summary><c>out = Poseidon(a, b)</c> exactly as the circuits' Merkle parent hash.</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int poseidon_hash_2(byte[] a, byte[] b, byte[] hashOut);
        /// <summary><c>out = Poseidon(a, b, c)</c> (chained from zero) exactly as the circuits' nullifier hash.</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int poseidon_hash_3(byte[] a, byte[] b, byte[] c, byte[] hashOut);
        /// <summary>The digest of an empty subtree of height <paramref name="level"/> (0 = the zero field element).</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int poseidon_merkle_zero(uint level, byte[] hashOut);
        /// <summary>Root from a leaf, its position and a <see cref="TreeDepth"/> x 32-byte sibling path (bottom-up), as the circuits compute it.</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int merkle_root_from_path_v1(byte[] leaf, ulong position, byte[] path, nuint pathLen, byte[] rootOut);

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
        /// Generate a Shield circuit proof (v2: the output names its owner). Requires VXPLNK04 with prover keys.
        /// Returns proof bytes + public input bytes in separate output buffers.
        /// </summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int plonk_prove_shield(
            ulong amountScaled,
            byte[] randomness,
            byte[] ownerPk,
            byte[] proofOut, ref nuint proofOutLen,
            byte[] piOut, ref nuint piOutLen);

        /// <summary>
        /// Generate a Transfer circuit proof (v1, 2-in/2-out).
        /// <paramref name="witnessData"/> is the flat witness blob (4448 bytes, v2).
        /// </summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int plonk_prove_transfer(
            byte[] witnessData, nuint witnessDataLen,
            byte[] proofOut, ref nuint proofOutLen,
            byte[] piOut, ref nuint piOutLen);

        /// <summary>
        /// Generate an Unshield circuit proof (v1).
        /// <paramref name="witnessData"/> is the flat witness blob (4408 bytes, v2: + change owner key + recipient tag).
        /// </summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int plonk_prove_unshield(
            byte[] witnessData, nuint witnessDataLen,
            byte[] proofOut, ref nuint proofOutLen,
            byte[] piOut, ref nuint piOutLen);

        /// <summary>
        /// Generate a Fee circuit proof (v1, 1-in/1-out).
        /// <paramref name="witnessData"/> is the flat witness blob (2240 bytes, v2: + change owner key).
        /// </summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int plonk_prove_fee(
            byte[] witnessData, nuint witnessDataLen,
            byte[] proofOut, ref nuint proofOutLen,
            byte[] piOut, ref nuint piOutLen);
    }
}
