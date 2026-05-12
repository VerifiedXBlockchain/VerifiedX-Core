using ReserveBlockCore.Models;
using ReserveBlockCore.Models.Privacy;

using ReserveBlockCore.Models;

namespace ReserveBlockCore.Privacy
{
    /// <summary>
    /// Calls <see cref="PlonkNative.plonk_verify"/> with v1 public inputs. When the native library is still a stub,
    /// verification returns <see cref="PlonkVerifyResult.NotImplemented"/> and consensus may accept txs (see <see cref="Globals.EnforcePlonkProofsForZk"/>).
    /// Block validation at tip runs <see cref="TryValidatePrivateProofsInBlock"/> once per block, then per-tx validation skips duplicate PLONK work via <see cref="PrivateTransactionValidatorService.VerifyPrivateTX"/>.
    /// </summary>
    public static class PlonkProofVerifier
    {
        /// <summary>
        /// Verifies PLONK payloads for all private txs in a block (single pass at chain tip). Historical sync passes <paramref name="blockDownloads"/> true so proofs are skipped.
        /// </summary>
        public static (bool ok, string message) TryValidatePrivateProofsInBlock(Block block, bool blockDownloads)
        {
            if (block.Transactions == null)
                return (true, "");

            foreach (var tx in block.Transactions)
            {
                if (tx.FromAddress == "Coinbase_TrxFees" || tx.FromAddress == "Coinbase_BlkRwd")
                    continue;
                if (!PrivateTransactionTypes.IsPrivateTransaction(tx.TransactionType))
                    continue;
                if (!PrivateTxPayloadCodec.TryDecode(tx.Data, out var payload, out var decErr))
                    return (false, decErr ?? "Invalid private payload.");
                if (payload == null)
                    return (false, "Invalid private payload.");

                var pl = TryValidatePrivateProofs(tx, payload, blockDownloads);
                if (!pl.ok)
                    return pl;
            }

            return (true, "");
        }

        public static PlonkVerifyResult VerifyRaw(PlonkCircuitType circuitType, ReadOnlySpan<byte> proof, ReadOnlySpan<byte> publicInputs)
        {
            var p = proof.ToArray();
            var pi = publicInputs.ToArray();
            int code = PlonkNative.plonk_verify((byte)circuitType, p, (nuint)p.Length, pi, (nuint)pi.Length);
            if (code == PlonkNative.ErrNotImplemented)
                return PlonkVerifyResult.NotImplemented;
            if (code == 1)
                return PlonkVerifyResult.Valid;
            if (code == 0)
                return PlonkVerifyResult.Invalid;
            return PlonkVerifyResult.NativeError;
        }

        /// <summary>Validates optional <c>proof_b64</c> / <c>fee_proof_b64</c> for ZK private txs when not in historical sync mode.</summary>
        public static (bool ok, string message) TryValidatePrivateProofs(
            Transaction tx,
            PrivateTxPayload payload,
            bool blockDownloads)
        {
            if (blockDownloads)
                return (true, "");

            if (!PrivateTransactionTypes.IsZkAuthorizedPrivate(tx.TransactionType)
                && tx.TransactionType != TransactionType.VFX_SHIELD
                && tx.TransactionType != TransactionType.VBTC_V2_SHIELD)
                return (true, "");

            var hasMain = !string.IsNullOrWhiteSpace(payload.ProofB64);
            var hasFee = !string.IsNullOrWhiteSpace(payload.FeeProofB64);

            if (Globals.EnforcePlonkProofsForZk
                && PrivateTransactionTypes.IsZkAuthorizedPrivate(tx.TransactionType)
                && !hasMain)
                return (false, "PLONK proof_b64 is required when EnforcePlonkProofsForZk is enabled.");

            if (!hasMain && !hasFee)
                return (true, "");

            if (!PLONKSetup.IsProofVerificationImplemented)
            {
                if (hasMain || hasFee)
                {
                    // Proofs present but native verifier not shipped — allow until plonk_ffi is upgraded.
                }
                return (true, "");
            }

            if (hasMain)
            {
                byte[] proofBytes;
                try
                {
                    proofBytes = Convert.FromBase64String(payload.ProofB64!);
                }
                catch
                {
                    return (false, "proof_b64 is not valid Base64.");
                }
                if (!PlonkPublicInputsV1.TryBuild(tx, payload, out var pubIn, out var err))
                    return (false, err ?? "Could not build PLONK public inputs.");
                var circuit = PlonkCircuitHelper.GetPrimaryCircuit(tx.TransactionType);
                var vr = VerifyRaw(circuit, proofBytes, pubIn);
                if (vr != PlonkVerifyResult.Valid)
                {
                    return vr switch
                    {
                        PlonkVerifyResult.Invalid => (false, "PLONK proof verification failed (primary circuit)."),
                        PlonkVerifyResult.NotImplemented => (false, "PLONK verifier is not available (unexpected stub)."),
                        _ => (false, "PLONK verifier returned an error (primary circuit).")
                    };
                }
            }

            if (hasFee && PlonkCircuitHelper.UsesFeeProof(tx.TransactionType))
            {
                byte[] feeProofBytes;
                try
                {
                    feeProofBytes = Convert.FromBase64String(payload.FeeProofB64!);
                }
                catch
                {
                    return (false, "fee_proof_b64 is not valid Base64.");
                }
                if (!PlonkPublicInputsV1.TryBuildFeeCircuit(tx, payload, out var feePub, out var err2))
                    return (false, err2 ?? "Could not build fee circuit public inputs.");
                var vr = VerifyRaw(PlonkCircuitType.Fee, feeProofBytes, feePub);
                if (vr != PlonkVerifyResult.Valid)
                {
                    return vr switch
                    {
                        PlonkVerifyResult.Invalid => (false, "PLONK fee proof verification failed."),
                        PlonkVerifyResult.NotImplemented => (false, "PLONK fee verifier is not available (unexpected stub)."),
                        _ => (false, "PLONK fee verifier returned an error.")
                    };
                }
            }

            return (true, "");
        }
    }
}
