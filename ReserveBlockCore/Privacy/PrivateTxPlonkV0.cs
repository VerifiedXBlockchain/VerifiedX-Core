using System.Diagnostics.CodeAnalysis;
using ReserveBlockCore.Models;

namespace ReserveBlockCore.Privacy
{
    /// <summary>
    /// When <see cref="PlonkProverV0.IsProveAvailable"/>, fills <c>proof_b64</c> and (for vBTC ZK) <c>fee_proof_b64</c>
    /// using <see cref="PlonkPublicInputsV1"/> + <see cref="PlonkNative.plonk_prove_v0"/> (v0 digest-binding circuit).
    /// </summary>
    public static class PrivateTxPlonkV0
    {
        /// <summary>
        /// No-op if proving is unavailable. On success with proving available, updates <paramref name="tx"/>.<c>Data</c> and re-runs <see cref="Transaction.BuildPrivate"/>.
        /// </summary>
        public static bool TryPopulateV0Proofs(Transaction tx, [NotNullWhen(false)] out string? error)
        {
            error = null;
            if (tx == null)
            {
                error = "Transaction is null.";
                return false;
            }

            if (!PrivateTransactionTypes.IsPrivateTransaction(tx.TransactionType))
                return true;

            if (!PlonkProverV0.IsProveAvailable)
                return true;

            if (!PrivateTxPayloadCodec.TryDecode(tx.Data, out var payload, out var decErr))
            {
                error = decErr;
                return false;
            }

            var primary = PlonkCircuitHelper.GetPrimaryCircuit(tx.TransactionType);
            if (!PlonkPublicInputsV1.TryBuild(tx, payload, out var pi, out var piErr))
            {
                error = piErr;
                return false;
            }

            var code = PlonkProverV0.TryProve(primary, pi, out var proof);
            if (code != PlonkNative.Success)
            {
                error = $"plonk_prove_v0 (primary) failed: code {code}";
                return false;
            }

            payload.ProofB64 = Convert.ToBase64String(proof!);

            if (PlonkCircuitHelper.UsesFeeProof(tx.TransactionType))
            {
                if (!PlonkPublicInputsV1.TryBuildFeeCircuit(tx, payload, out var piFee, out var feePiErr))
                {
                    error = feePiErr;
                    return false;
                }

                code = PlonkProverV0.TryProve(PlonkCircuitType.Fee, piFee, out var feeProof);
                if (code != PlonkNative.Success)
                {
                    error = $"plonk_prove_v0 (fee) failed: code {code}";
                    return false;
                }

                payload.FeeProofB64 = Convert.ToBase64String(feeProof!);
            }

            tx.Data = PrivateTxPayloadCodec.SerializeToJson(payload);
            tx.BuildPrivate();
            return true;
        }
    }
}
