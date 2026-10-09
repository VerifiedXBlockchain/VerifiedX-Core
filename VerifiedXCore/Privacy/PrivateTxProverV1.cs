using VerifiedXCore.Models;
using VerifiedXCore.Models.Privacy;

namespace VerifiedXCore.Privacy
{
    /// <summary>
    /// Builds the v1 circuit witnesses from a wallet's notes and attaches the proof to a private transaction
    /// (fund-loss audit item 1, stage 2). The witness is: for each of two spent notes, (amount, randomness, viewing key,
    /// position, 32-level Merkle path, direction bits); for the outputs, (amount, randomness); the fee; the root. A
    /// single-note spend fills the second input with the public dummy note (<see cref="PrivacyEpoch"/>). Every proof is
    /// verified locally against the VFXPI1 version-2 blob consensus will rebuild before the transaction leaves the wallet.
    /// </summary>
    public static class PrivateTxProverV1
    {
        /// <summary>A spent note as the circuits see it.</summary>
        public sealed record SpentNote(ulong AmountScaled, byte[] Randomness32, long TreePosition);

        /// <summary>An output note as the circuits see it.</summary>
        public sealed record OutputNote(ulong AmountScaled, byte[] Randomness32);

        /// <summary>The dummy note as a spent input.</summary>
        public static SpentNote Dummy => new(0, PrivacyField.Zero32, 0);

        /// <summary>Whether this node can produce v1 proofs (VXPLNK03 with prover keys loaded).</summary>
        public static bool CanProve
        {
            get { PLONKSetup.RefreshVerificationCapability(); return PLONKSetup.IsV1ProvingAvailable; }
        }

        public const string CannotProveReason = "This node cannot produce PLONK proofs: the VXPLNK03 params with prover keys are not loaded (they download on first start; check the PLONK status route).";

        /// <summary>
        /// The witness for one input: the note, the spender's canonical viewing key and the note's path in the asset's
        /// fixed-depth tree (loaded from the privacy database as it stands now).
        /// </summary>
        public static bool TryBuildInput(string asset, SpentNote note, byte[] viewingKeyCanonical, LiteDB.LiteDatabase? db,
            out PlonkProverV1.TransferInputWitness? witness, out byte[]? root, out string? error)
        {
            witness = null;
            root = null;
            error = null;
            var store = new ShieldedMerkleStore(asset, db, fixedDepth: true);
            store.LoadLeavesFromCommitments();
            if (!store.TryGetInclusionProof(note.TreePosition, out var path, out var r))
            {
                error = $"Note at tree position {note.TreePosition} is not in the {asset} pool's tree (did the pool restart at the proof-rules height?).";
                return false;
            }
            var leaf = PoseidonV1.NoteHash(note.AmountScaled, note.Randomness32);
            if (!PoseidonV1.RootFromPath(leaf, (ulong)note.TreePosition, path).AsSpan().SequenceEqual(r))
            {
                error = $"Note at tree position {note.TreePosition} does not match the leaf stored there (amount or randomness differ).";
                return false;
            }
            witness = new PlonkProverV1.TransferInputWitness
            {
                AmountScaled = note.AmountScaled,
                Randomness32 = note.Randomness32,
                ViewingKey32 = viewingKeyCanonical,
                TreePosition = (ulong)note.TreePosition,
                MerklePath = path,
                MerkleIndices = FixedDepthMerkleTree.IndicesFlat(note.TreePosition),
            };
            root = r;
            return true;
        }

        /// <summary>The nullifier the circuits will expose for a spent note (the dummy's uses viewing key 0).</summary>
        public static byte[] NullifierFor(SpentNote note, byte[] viewingKeyCanonical) =>
            note.AmountScaled == 0 && note.TreePosition == 0 && note.Randomness32.AsSpan().SequenceEqual(PrivacyField.Zero32)
                ? PrivacyEpoch.DummyNullifier
                : PoseidonV1.Nullifier(viewingKeyCanonical, PoseidonV1.NoteHash(note.AmountScaled, note.Randomness32), (ulong)note.TreePosition);

        /// <summary>Proves a shield (the output's note hash binds the amount).</summary>
        public static bool TryProveShield(ulong amountScaled, byte[] randomness32, out byte[]? proof, out string? error)
        {
            proof = null;
            error = null;
            if (!CanProve) { error = CannotProveReason; return false; }
            var code = PlonkProverV1.TryProveShield(amountScaled, randomness32, out proof, out _);
            if (code != PlonkNative.Success || proof == null) { error = $"plonk_prove_shield failed: code {code}"; return false; }
            return true;
        }

        /// <summary>Proves an unshield of two notes (the second may be the dummy) with one change output.</summary>
        public static bool TryProveUnshield(PlonkProverV1.TransferInputWitness[] inputs, ulong transparentScaled, OutputNote change, ulong feeScaled, byte[] root,
            out byte[]? proof, out string? error)
        {
            proof = null;
            error = null;
            if (!CanProve) { error = CannotProveReason; return false; }
            var code = PlonkProverV1.TryProveUnshield(inputs, transparentScaled, change.AmountScaled, change.Randomness32, feeScaled, root, out proof, out _);
            if (code != PlonkNative.Success || proof == null) { error = $"plonk_prove_unshield failed: code {code}"; return false; }
            return true;
        }

        /// <summary>Proves a private transfer of two notes into two outputs.</summary>
        public static bool TryProveTransfer(PlonkProverV1.TransferInputWitness[] inputs, OutputNote[] outputs, ulong feeScaled, byte[] root,
            out byte[]? proof, out string? error)
        {
            proof = null;
            error = null;
            if (!CanProve) { error = CannotProveReason; return false; }
            var ow = outputs.Select(o => new PlonkProverV1.TransferOutputWitness { AmountScaled = o.AmountScaled, Randomness32 = o.Randomness32 }).ToArray();
            var code = PlonkProverV1.TryProveTransfer(inputs, ow, feeScaled, root, out proof, out _);
            if (code != PlonkNative.Success || proof == null) { error = $"plonk_prove_transfer failed: code {code}"; return false; }
            return true;
        }

        /// <summary>
        /// Writes the proof into the payload, re-serialises the transaction and checks it the way consensus will:
        /// version-2 public inputs rebuilt from the transaction, verified natively. Returns false when that check fails,
        /// so a wallet never broadcasts a transaction validators would refuse.
        /// </summary>
        public static bool TryAttachAndVerify(Transaction tx, PrivateTxPayload payload, byte[] proof, out string? error)
        {
            error = null;
            payload.ProofB64 = Convert.ToBase64String(proof);
            tx.Data = PrivateTxPayloadCodec.SerializeToJson(payload);
            tx.BuildPrivate();
            if (!PlonkPublicInputsV2.TryBuild(tx, payload, out var pi, out var piErr))
            {
                error = piErr;
                return false;
            }
            var circuit = PlonkCircuitHelper.GetPrimaryCircuit(tx.TransactionType);
            var result = PlonkProofVerifier.VerifyRaw(circuit, proof, pi);
            if (result != PlonkVerifyResult.Valid)
            {
                error = $"The proof this wallet produced does not verify against the transaction's public inputs ({result}); not broadcasting.";
                return false;
            }
            return true;
        }
    }
}
