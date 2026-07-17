namespace ReserveBlockCore.Privacy
{
    /// <summary>
    /// v1 PLONK proving via real circuit FFI exports (<b>VXPLNK03</b> params with prover keys).
    /// Each method serializes the circuit-specific witness into the flat byte layout expected by
    /// <c>plonk-ffi</c>, calls the native prove function, and returns proof + public-input bytes.
    /// </summary>
    public static class PlonkProverV1
    {
        /// <summary>Whether v1 real-circuit proving is available.</summary>
        public static bool IsAvailable => PLONKSetup.IsV1ProvingAvailable;

        // ─── Shield (T→Z) ─────────────────────────────────────────────

        /// <summary>
        /// Proves a Shield circuit: the output commitment matches the stated transparent amount.
        /// </summary>
        public static int TryProveShield(
            ulong amountScaled,
            byte[] randomness32,
            out byte[]? proof,
            out byte[]? publicInputs)
        {
            proof = null;
            publicInputs = null;
            if (randomness32?.Length != PlonkNative.ScalarSize)
                return PlonkNative.ErrParam;
            if (!IsAvailable)
                return PlonkNative.ErrNotImplemented;

            return CallProveWithRetry(
                (byte[] pBuf, ref nuint pLen, byte[] piBuf, ref nuint piLen) =>
                    PlonkNative.plonk_prove_shield(amountScaled, randomness32, pBuf, ref pLen, piBuf, ref piLen),
                out proof, out publicInputs);
        }

        // ─── Transfer (Z→Z, 2-in / 2-out) ─────────────────────────────

        /// <summary>
        /// Proves a Transfer circuit (2-in/2-out).
        /// Wire format: 2×TransferInput + 2×Output(amount+rand) + fee(8) + merkleRoot(32) = 4384 bytes.
        /// </summary>
        public static int TryProveTransfer(
            TransferInputWitness[] inputs,
            TransferOutputWitness[] outputs,
            ulong feeScaled,
            byte[] merkleRoot32,
            out byte[]? proof,
            out byte[]? publicInputs)
        {
            proof = null;
            publicInputs = null;
            if (inputs?.Length != 2 || outputs?.Length != 2)
                return PlonkNative.ErrParam;
            if (merkleRoot32?.Length != PlonkNative.ScalarSize)
                return PlonkNative.ErrParam;
            if (!IsAvailable)
                return PlonkNative.ErrNotImplemented;

            var expectedLen = 2 * PlonkNative.TransferInputWireSize + 2 * PlonkNative.OutputWireSize + 8 + PlonkNative.ScalarSize;
            var witness = new byte[expectedLen];
            var offset = 0;

            for (int i = 0; i < 2; i++)
                offset = WriteTransferInput(witness, offset, inputs[i]);
            for (int i = 0; i < 2; i++)
                offset = WriteOutput(witness, offset, outputs[i].AmountScaled, outputs[i].Randomness32);
            WriteU64LE(witness, offset, feeScaled); offset += 8;
            Buffer.BlockCopy(merkleRoot32, 0, witness, offset, PlonkNative.ScalarSize);

            return CallProveWithRetry(
                (byte[] pBuf, ref nuint pLen, byte[] piBuf, ref nuint piLen) =>
                    PlonkNative.plonk_prove_transfer(witness, (nuint)witness.Length, pBuf, ref pLen, piBuf, ref piLen),
                out proof, out publicInputs);
        }

        // ─── Unshield (Z→T) ───────────────────────────────────────────

        /// <summary>
        /// Proves an Unshield circuit.
        /// Wire format: 2×TransferInput + transparentAmt(8) + changeAmt(8) + changeRand(32) + fee(8) + merkleRoot(32) = 4344 bytes.
        /// </summary>
        public static int TryProveUnshield(
            TransferInputWitness[] inputs,
            ulong transparentAmountScaled,
            ulong changeAmountScaled,
            byte[] changeRandomness32,
            ulong feeScaled,
            byte[] merkleRoot32,
            out byte[]? proof,
            out byte[]? publicInputs)
        {
            proof = null;
            publicInputs = null;
            if (inputs?.Length != 2)
                return PlonkNative.ErrParam;
            if (changeRandomness32?.Length != PlonkNative.ScalarSize)
                return PlonkNative.ErrParam;
            if (merkleRoot32?.Length != PlonkNative.ScalarSize)
                return PlonkNative.ErrParam;
            if (!IsAvailable)
                return PlonkNative.ErrNotImplemented;

            var expectedLen = 2 * PlonkNative.TransferInputWireSize + 8 + 8 + PlonkNative.ScalarSize + 8 + PlonkNative.ScalarSize;
            var witness = new byte[expectedLen];
            var offset = 0;

            for (int i = 0; i < 2; i++)
                offset = WriteTransferInput(witness, offset, inputs[i]);
            WriteU64LE(witness, offset, transparentAmountScaled); offset += 8;
            WriteU64LE(witness, offset, changeAmountScaled); offset += 8;
            Buffer.BlockCopy(changeRandomness32, 0, witness, offset, PlonkNative.ScalarSize); offset += PlonkNative.ScalarSize;
            WriteU64LE(witness, offset, feeScaled); offset += 8;
            Buffer.BlockCopy(merkleRoot32, 0, witness, offset, PlonkNative.ScalarSize);

            return CallProveWithRetry(
                (byte[] pBuf, ref nuint pLen, byte[] piBuf, ref nuint piLen) =>
                    PlonkNative.plonk_prove_unshield(witness, (nuint)witness.Length, pBuf, ref pLen, piBuf, ref piLen),
                out proof, out publicInputs);
        }

        // ─── Fee (1-in / 1-out, vBTC fee leg) ─────────────────────────

        /// <summary>
        /// Proves a Fee circuit (1-in/1-out VFX fee).
        /// Wire format: 1×TransferInput + changeAmt(8) + changeRand(32) + fee(8) + merkleRoot(32) = 2208 bytes.
        /// </summary>
        public static int TryProveFee(
            TransferInputWitness input,
            ulong changeAmountScaled,
            byte[] changeRandomness32,
            ulong feeScaled,
            byte[] merkleRoot32,
            out byte[]? proof,
            out byte[]? publicInputs)
        {
            proof = null;
            publicInputs = null;
            if (input == null)
                return PlonkNative.ErrParam;
            if (changeRandomness32?.Length != PlonkNative.ScalarSize)
                return PlonkNative.ErrParam;
            if (merkleRoot32?.Length != PlonkNative.ScalarSize)
                return PlonkNative.ErrParam;
            if (!IsAvailable)
                return PlonkNative.ErrNotImplemented;

            var expectedLen = PlonkNative.TransferInputWireSize + 8 + PlonkNative.ScalarSize + 8 + PlonkNative.ScalarSize;
            var witness = new byte[expectedLen];
            var offset = 0;

            offset = WriteTransferInput(witness, offset, input);
            WriteU64LE(witness, offset, changeAmountScaled); offset += 8;
            Buffer.BlockCopy(changeRandomness32, 0, witness, offset, PlonkNative.ScalarSize); offset += PlonkNative.ScalarSize;
            WriteU64LE(witness, offset, feeScaled); offset += 8;
            Buffer.BlockCopy(merkleRoot32, 0, witness, offset, PlonkNative.ScalarSize);

            return CallProveWithRetry(
                (byte[] pBuf, ref nuint pLen, byte[] piBuf, ref nuint piLen) =>
                    PlonkNative.plonk_prove_fee(witness, (nuint)witness.Length, pBuf, ref pLen, piBuf, ref piLen),
                out proof, out publicInputs);
        }

        // ─── Witness data types ────────────────────────────────────────

        /// <summary>
        /// Represents a shielded input being spent in a transfer/unshield/fee circuit.
        /// Per-input wire format (2128 bytes): amount(8) + randomness(32) + viewing_key(32) + position(8) + merkle_path(1024) + merkle_indices(1024).
        /// </summary>
        public sealed class TransferInputWitness
        {
            public ulong AmountScaled { get; set; }
            public byte[] Randomness32 { get; set; } = Array.Empty<byte>();
            public byte[] ViewingKey32 { get; set; } = Array.Empty<byte>();
            public ulong TreePosition { get; set; }
            /// <summary>Merkle path: <see cref="PlonkNative.TreeDepth"/> × 32-byte sibling hashes (bottom-up).</summary>
            public byte[] MerklePath { get; set; } = Array.Empty<byte>();
            /// <summary>Merkle indices: <see cref="PlonkNative.TreeDepth"/> × 32-byte field elements (0 or 1 per level, LE).</summary>
            public byte[] MerkleIndices { get; set; } = Array.Empty<byte>();
        }

        /// <summary>A transfer output (amount + randomness).</summary>
        public sealed class TransferOutputWitness
        {
            public ulong AmountScaled { get; set; }
            public byte[] Randomness32 { get; set; } = Array.Empty<byte>();
        }

        // ─── Serialization helpers ─────────────────────────────────────

        private static int WriteTransferInput(byte[] buf, int offset, TransferInputWitness inp)
        {
            WriteU64LE(buf, offset, inp.AmountScaled); offset += 8;
            Buffer.BlockCopy(inp.Randomness32, 0, buf, offset, PlonkNative.ScalarSize); offset += PlonkNative.ScalarSize;
            Buffer.BlockCopy(inp.ViewingKey32, 0, buf, offset, PlonkNative.ScalarSize); offset += PlonkNative.ScalarSize;
            WriteU64LE(buf, offset, inp.TreePosition); offset += 8;
            Buffer.BlockCopy(inp.MerklePath, 0, buf, offset, PlonkNative.ScalarSize * PlonkNative.TreeDepth);
            offset += PlonkNative.ScalarSize * PlonkNative.TreeDepth;
            Buffer.BlockCopy(inp.MerkleIndices, 0, buf, offset, PlonkNative.ScalarSize * PlonkNative.TreeDepth);
            offset += PlonkNative.ScalarSize * PlonkNative.TreeDepth;
            return offset;
        }

        private static int WriteOutput(byte[] buf, int offset, ulong amountScaled, byte[] randomness32)
        {
            WriteU64LE(buf, offset, amountScaled); offset += 8;
            Buffer.BlockCopy(randomness32, 0, buf, offset, PlonkNative.ScalarSize); offset += PlonkNative.ScalarSize;
            return offset;
        }

        private static void WriteU64LE(byte[] buf, int offset, ulong value)
        {
            var le = BitConverter.GetBytes(value);
            Buffer.BlockCopy(le, 0, buf, offset, 8);
        }

        // ─── Call helper with buffer retry ─────────────────────────────

        private delegate int ProveDelegate(byte[] proofBuf, ref nuint proofLen, byte[] piBuf, ref nuint piLen);

        private static int CallProveWithRetry(ProveDelegate proveCall, out byte[]? proof, out byte[]? publicInputs)
        {
            proof = null;
            publicInputs = null;

            const int initialBuf = 512 * 1024;
            var proofBuf = new byte[initialBuf];
            var piBuf = new byte[initialBuf];
            nuint proofLen = (nuint)initialBuf;
            nuint piLen = (nuint)initialBuf;

            var code = proveCall(proofBuf, ref proofLen, piBuf, ref piLen);

            // Retry with larger buffer if needed
            if (code == PlonkNative.ErrParam && ((int)proofLen > initialBuf || (int)piLen > initialBuf))
            {
                proofBuf = new byte[(int)proofLen];
                piBuf = new byte[(int)piLen];
                code = proveCall(proofBuf, ref proofLen, piBuf, ref piLen);
            }

            if (code != PlonkNative.Success)
                return code;

            proof = new byte[(int)proofLen];
            Buffer.BlockCopy(proofBuf, 0, proof, 0, (int)proofLen);
            publicInputs = new byte[(int)piLen];
            Buffer.BlockCopy(piBuf, 0, publicInputs, 0, (int)piLen);
            return PlonkNative.Success;
        }
    }
}