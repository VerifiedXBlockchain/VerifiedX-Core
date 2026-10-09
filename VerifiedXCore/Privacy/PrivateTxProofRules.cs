using VerifiedXCore.Models;
using VerifiedXCore.Models.Privacy;

namespace VerifiedXCore.Privacy
{
    /// <summary>
    /// Fund-loss audit item 1, stage 2 (Globals.PrivateTxProofRulesHeight): the shape every private transaction must
    /// have once proofs are required, checked before the proof itself (<see cref="PlonkProofVerifier"/>):
    /// <list type="bullet">
    /// <item>no private transaction at all in the reset block (the pools restart in it);</item>
    /// <item>every output carries a canonical note hash (the circuits bind note hashes, not Pedersen commitments);</item>
    /// <item>a shield has one output; an unshield spends exactly two notes and has one change output (amount may be
    /// zero); a private transfer spends exactly two notes and has two outputs;</item>
    /// <item>the public dummy note's nullifier appears at most once, and a real nullifier is never the dummy's.</item>
    /// </list>
    /// Null when the rules do not apply (before the height) or pass.
    /// </summary>
    public static class PrivateTxProofRules
    {
        public const string ResetBlockReason = "No private transaction is valid in the block that starts the proof-rules epoch.";

        public static string? Check(Transaction tx, PrivateTxPayload payload, long height)
        {
            if (tx == null || payload == null || !PrivateTransactionTypes.IsPrivateTransaction(tx.TransactionType))
                return null;
            if (PrivacyEpoch.IsResetBlock(height))
                return ResetBlockReason;
            if (!PrivacyEpoch.ProofRulesActive(height))
                return null;

            foreach (var o in payload.Outs)
            {
                if (o == null) return "Private outputs cannot be null.";
                var nh = o.NoteHashB64;
                if (string.IsNullOrWhiteSpace(nh)) return "Every private output must carry its note_hash (the circuits bind note hashes).";
                byte[] bytes;
                try { bytes = Convert.FromBase64String(nh); } catch { return "An output note_hash is not valid Base64."; }
                if (!PrivacyField.IsCanonicalLe(bytes)) return "An output note_hash must be a canonical 32-byte field element.";
            }

            switch (tx.TransactionType)
            {
                case TransactionType.VFX_SHIELD:
                case TransactionType.VBTC_V2_SHIELD:
                    if (payload.Outs.Count != 1) return "A shield carries exactly one output.";
                    break;
                case TransactionType.VFX_UNSHIELD:
                case TransactionType.VBTC_V2_UNSHIELD:
                    if (payload.NullsB64.Count != 2) return "An unshield spends exactly two notes (a single-note spend carries the public dummy note as its second input).";
                    if (payload.Outs.Count != 1) return "An unshield carries exactly one change output (its amount may be zero).";
                    break;
                case TransactionType.VFX_PRIVATE_TRANSFER:
                case TransactionType.VBTC_V2_PRIVATE_TRANSFER:
                    if (payload.NullsB64.Count != 2) return "A private transfer spends exactly two notes (a single-note spend carries the public dummy note as its second input).";
                    if (payload.Outs.Count != 2) return "A private transfer carries exactly two outputs (payment and change; the change may be zero).";
                    break;
            }

            var dummies = payload.NullsB64.Count(PrivacyEpoch.IsDummyNullifier);
            if (dummies > 1) return "The public dummy note can be spent only once per transaction.";
            if (!string.IsNullOrWhiteSpace(payload.FeeInputNullifierB64) && PrivacyEpoch.IsDummyNullifier(payload.FeeInputNullifierB64))
                return "The VFX fee leg must spend a real note.";
            return null;
        }
    }
}
