using VerifiedXCore.Models;
using VerifiedXCore.Models.Privacy;

namespace VerifiedXCore.Privacy
{
    /// <summary>
    /// Consensus-side PLONK verification of private transactions.
    ///
    /// Before Globals.PrivateTxProofRulesHeight proofs were optional and the enforce flag was set nowhere (which is how the
    /// forged mainnet unshields passed); that behaviour is kept for history. From the height (fund-loss audit item 1,
    /// stage 2) a ZK-authorized private transaction - and a shield - must carry a proof that verifies against the VFXPI1
    /// version-2 public inputs rebuilt from its own fields (<see cref="PlonkPublicInputsV2"/>), and a node whose native
    /// library cannot verify (no VXPLNK04 params loaded, or a library without the v2 circuits) refuses the transaction
    /// rather than letting it through. Historical sync (<c>blockDownloads</c>) still skips proofs: those blocks were
    /// accepted by the validators of their time.
    /// </summary>
    public static class PlonkProofVerifier
    {
        public const string VerifierUnavailableReason = "This node cannot verify PLONK proofs (VXPLNK04 params not loaded or the native library lacks the v2 owner-bound circuits); private transactions are refused until it can.";

        /// <summary>Replaceable for tests: whether this node can verify v1 proofs against version-2 public inputs.</summary>
        internal static Func<bool> VerifierAvailable = () =>
        {
            PLONKSetup.RefreshVerificationCapability();
            return PLONKSetup.IsV1CircuitsLoaded && PLONKSetup.IsVfxPi2VerifyAvailable && PLONKSetup.IsV2CircuitsAvailable;
        };

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

                var pl = TryValidatePrivateProofs(tx, payload, blockDownloads, block.Height);
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

        /// <summary>
        /// Validates <c>proof_b64</c> / <c>fee_proof_b64</c> for a private transaction at <paramref name="height"/> (the block's
        /// height in validation, tip + 1 at admission). Before the proof-rules height the proofs are optional (legacy
        /// behaviour); from it they are required and verified against version-2 public inputs.
        /// </summary>
        public static (bool ok, string message) TryValidatePrivateProofs(
            Transaction tx,
            PrivateTxPayload payload,
            bool blockDownloads,
            long? height = null)
        {
            if (blockDownloads)
                return (true, "");

            var h = height ?? ((Globals.LastBlock?.Height ?? 0) + 1);
            if (PrivacyEpoch.ProofRulesActive(h))
                return RequireAndVerifyV2(tx, payload);

            return LegacyOptionalProofs(tx, payload);
        }

        // ── Proof-rules epoch ─────────────────────────────────────────────────────────────────────────────────

        private static (bool ok, string message) RequireAndVerifyV2(Transaction tx, PrivateTxPayload payload)
        {
            var isZk = PrivateTransactionTypes.IsZkAuthorizedPrivate(tx.TransactionType);
            var isShield = tx.TransactionType == TransactionType.VFX_SHIELD || tx.TransactionType == TransactionType.VBTC_V2_SHIELD;
            if (!isZk && !isShield)
                return (true, "");

            if (string.IsNullOrWhiteSpace(payload.ProofB64))
                return (false, "A private transaction must carry a PLONK proof (proof_b64).");
            if (PlonkCircuitHelper.UsesFeeProof(tx.TransactionType) && string.IsNullOrWhiteSpace(payload.FeeProofB64))
                return (false, "A vBTC private transaction must carry its VFX fee-leg proof (fee_proof_b64).");

            if (!VerifierAvailable())
                return (false, VerifierUnavailableReason);

            if (!TryDecodeProof(payload.ProofB64!, out var proofBytes))
                return (false, "proof_b64 is not valid Base64.");
            if (!PlonkPublicInputsV2.TryBuild(tx, payload, out var publicInputs, out var piErr))
                return (false, piErr ?? "Could not build the public inputs.");
            var circuit = PlonkCircuitHelper.GetPrimaryCircuit(tx.TransactionType);
            var result = VerifyRaw(circuit, proofBytes, publicInputs);
            if (result != PlonkVerifyResult.Valid)
                return (false, Describe(result, "primary circuit"));

            if (PlonkCircuitHelper.UsesFeeProof(tx.TransactionType))
            {
                if (!TryDecodeProof(payload.FeeProofB64!, out var feeProof))
                    return (false, "fee_proof_b64 is not valid Base64.");
                if (!PlonkPublicInputsV2.TryBuildFeeCircuit(tx, payload, out var feePi, out var feeErr))
                    return (false, feeErr ?? "Could not build the fee circuit public inputs.");
                var feeResult = VerifyRaw(PlonkCircuitType.Fee, feeProof, feePi);
                if (feeResult != PlonkVerifyResult.Valid)
                    return (false, Describe(feeResult, "fee circuit"));
            }
            return (true, "");
        }

        private static string Describe(PlonkVerifyResult r, string which) => r switch
        {
            PlonkVerifyResult.Invalid => $"PLONK proof verification failed ({which}).",
            PlonkVerifyResult.NotImplemented => VerifierUnavailableReason,
            _ => $"PLONK verifier returned an error ({which}).",
        };

        private static bool TryDecodeProof(string b64, out byte[] bytes)
        {
            try { bytes = Convert.FromBase64String(b64); return bytes.Length > 0; }
            catch { bytes = Array.Empty<byte>(); return false; }
        }

        // ── Before the height: the original optional-proof behaviour, unchanged ──────────────────────────────

        private static (bool ok, string message) LegacyOptionalProofs(Transaction tx, PrivateTxPayload payload)
        {
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
                // Proofs present but native verifier not shipped — allow until plonk_ffi is upgraded.
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
