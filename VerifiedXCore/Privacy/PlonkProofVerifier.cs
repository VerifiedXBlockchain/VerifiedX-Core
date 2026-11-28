using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
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
    /// library cannot verify (no VXPLNK06 params loaded, or a library without the v2 circuits) refuses the transaction
    /// rather than letting it through. Historical sync (<c>blockDownloads</c>) still skips proofs: those blocks were
    /// accepted by the validators of their time.
    /// </summary>
    public static class PlonkProofVerifier
    {
        public const string MissingProofReason = "A private transaction must carry a PLONK proof (proof_b64).";
        public const string FailedProofPrefix = "PLONK proof verification failed";
        public const string VerifierUnavailableReason = "This node cannot verify PLONK proofs (VXPLNK06 params not loaded, or the native library lacks the v2 owner-bound circuits or is a circuit generation this build does not pin); private transactions are refused until it can.";

        /// <summary>Replaceable for tests: whether this node can verify v1 proofs against version-2 public inputs.</summary>
        internal static Func<bool> VerifierAvailable = () =>
        {
            PLONKSetup.RefreshVerificationCapability();
            return PLONKSetup.IsCircuitVersionPinned && PLONKSetup.IsV1CircuitsLoaded && PLONKSetup.IsVfxPi2VerifyAvailable && PLONKSetup.IsV2CircuitsAvailable;
        };

        /// <summary>Whether proofs mined at <paramref name="height"/> are judged under the library's current rules (Globals.PlonkCurrentRulesHeight).</summary>
        public static bool UsesCurrentRules(long height) => height >= Globals.PlonkCurrentRulesHeight;

        /// <summary>Replaceable for tests: whether this node's library judges proofs under its current rules.</summary>
        internal static Func<bool> CurrentRulesAvailable = () =>
        {
            PLONKSetup.RefreshVerificationCapability();
            return PLONKSetup.IsVerify2Available && PLONKSetup.IsBoundHashCapacityAvailable;
        };

        /// <summary>
        /// The rules a block at <paramref name="height"/> judges its private proofs under: key-bound from
        /// Globals.PlonkKeyBoundRulesHeight, current from Globals.PlonkCurrentRulesHeight, original below.
        /// </summary>
        public static PlonkRules RulesAt(long height) =>
            height >= Globals.PlonkKeyBoundRulesHeight ? PlonkRules.KeyBound
            : height >= Globals.PlonkCurrentRulesHeight ? PlonkRules.Current
            : PlonkRules.Original;

        /// <summary>Replaceable for tests: whether this node's library judges proofs under the key-bound rules (which build on the current ones).</summary>
        internal static Func<bool> KeyBoundRulesAvailable = () =>
        {
            PLONKSetup.RefreshVerificationCapability();
            return PLONKSetup.IsVerify3Available && PLONKSetup.IsVerify2Available && PLONKSetup.IsBoundHashCapacityAvailable;
        };

        /// <summary>Whether this node's library can judge proofs under <paramref name="rules"/>.</summary>
        public static bool RulesAvailable(PlonkRules rules) => rules switch
        {
            PlonkRules.KeyBound => KeyBoundRulesAvailable(),
            PlonkRules.Current => CurrentRulesAvailable(),
            _ => true,
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

        /// <summary>Replaceable for tests: the native verifier call under <see cref="PlonkRules"/>.</summary>
        internal static Func<byte, byte[], byte[], PlonkRules, int> NativeVerify = (circuit, p, pi, rules) => rules switch
        {
            PlonkRules.KeyBound => PlonkNative.plonk_verify3(circuit, p, (nuint)p.Length, pi, (nuint)pi.Length),
            PlonkRules.Current => PlonkNative.plonk_verify2(circuit, p, (nuint)p.Length, pi, (nuint)pi.Length),
            _ => PlonkNative.plonk_verify(circuit, p, (nuint)p.Length, pi, (nuint)pi.Length),
        };

        /// <summary>
        /// Keys of (rules, circuit, proof, public inputs) the native verifier has accepted. Verification is a pure function
        /// of that triple, so a hit returns exactly what the verifier would; it spares re-verifying the same proof at every
        /// relay hop, every proposal pass over the mempool and block validation. Only Valid results are cached.
        /// </summary>
        private static readonly ConcurrentDictionary<string, byte> VerifiedProofs = new(StringComparer.Ordinal);
        internal const int VerifiedProofsMax = 8192;

        internal static void ClearVerifiedProofCache() => VerifiedProofs.Clear();

        private static string VerifiedProofKey(byte circuit, byte[] proof, byte[] publicInputs, PlonkRules rules)
        {
            using var sha = SHA256.Create();
            var header = new byte[10];
            header[0] = circuit;
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1), proof.Length);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(5), publicInputs.Length);
            header[9] = (byte)rules; // a proof accepted under one set of rules says nothing of another
            sha.TransformBlock(header, 0, header.Length, null, 0);
            sha.TransformBlock(proof, 0, proof.Length, null, 0);
            sha.TransformFinalBlock(publicInputs, 0, publicInputs.Length);
            return Convert.ToHexString(sha.Hash!);
        }

        /// <summary>Verifies a proof natively under <paramref name="rules"/> (<see cref="RulesAt"/> of the block that carries it).</summary>
        public static PlonkVerifyResult VerifyRaw(PlonkCircuitType circuitType, ReadOnlySpan<byte> proof, ReadOnlySpan<byte> publicInputs, PlonkRules rules)
        {
            var p = proof.ToArray();
            var pi = publicInputs.ToArray();
            var key = VerifiedProofKey((byte)circuitType, p, pi, rules);
            if (VerifiedProofs.ContainsKey(key))
                return PlonkVerifyResult.Valid;

            int code = NativeVerify((byte)circuitType, p, pi, rules);
            if (code == PlonkNative.ErrNotImplemented)
                return PlonkVerifyResult.NotImplemented;
            if (code == 1)
            {
                if (VerifiedProofs.Count >= VerifiedProofsMax)
                    VerifiedProofs.Clear();
                VerifiedProofs[key] = 0;
                return PlonkVerifyResult.Valid;
            }
            // A proof the library refuses - it panicked on it (ErrPanic, where it used to abort the node) or could not
            // read it (ErrCrypto) - is an invalid proof, as one that fails verification is: it says nothing about this
            // node's state, so it is not taken for a sign of local corruption. Public inputs the library rejects are the
            // node's own, and stay a native error.
            if (code == 0 || code == PlonkNative.ErrPanic || code == PlonkNative.ErrCrypto)
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
                return RequireAndVerifyV2(tx, payload, RulesAt(h));

            return LegacyOptionalProofs(tx, payload);
        }

        // ── Proof-rules epoch ─────────────────────────────────────────────────────────────────────────────────

        private static (bool ok, string message) RequireAndVerifyV2(Transaction tx, PrivateTxPayload payload, PlonkRules rules)
        {
            var isZk = PrivateTransactionTypes.IsZkAuthorizedPrivate(tx.TransactionType);
            var isShield = tx.TransactionType == TransactionType.VFX_SHIELD || tx.TransactionType == TransactionType.VBTC_V2_SHIELD;
            if (!isZk && !isShield)
                return (true, "");

            if (string.IsNullOrWhiteSpace(payload.ProofB64))
                return (false, MissingProofReason);
            if (PlonkCircuitHelper.UsesFeeProof(tx.TransactionType) && string.IsNullOrWhiteSpace(payload.FeeProofB64))
                return (false, "A vBTC private transaction must carry its VFX fee-leg proof (fee_proof_b64).");

            if (!VerifierAvailable() || !RulesAvailable(rules))
                return (false, VerifierUnavailableReason);

            if (!TryDecodeProof(payload.ProofB64!, out var proofBytes))
                return (false, "proof_b64 is not valid Base64.");
            if (!PlonkPublicInputsV2.TryBuild(tx, payload, out var publicInputs, out var piErr))
                return (false, piErr ?? "Could not build the public inputs.");
            var circuit = PlonkCircuitHelper.GetPrimaryCircuit(tx.TransactionType);
            var result = VerifyRaw(circuit, proofBytes, publicInputs, rules);
            if (result != PlonkVerifyResult.Valid)
                return (false, Describe(result, "primary circuit"));

            if (PlonkCircuitHelper.UsesFeeProof(tx.TransactionType))
            {
                if (!TryDecodeProof(payload.FeeProofB64!, out var feeProof))
                    return (false, "fee_proof_b64 is not valid Base64.");
                if (!PlonkPublicInputsV2.TryBuildFeeCircuit(tx, payload, out var feePi, out var feeErr))
                    return (false, feeErr ?? "Could not build the fee circuit public inputs.");
                var feeResult = VerifyRaw(PlonkCircuitType.Fee, feeProof, feePi, rules);
                if (feeResult != PlonkVerifyResult.Valid)
                    return (false, Describe(feeResult, "fee circuit"));
            }
            return (true, "");
        }

        private static string Describe(PlonkVerifyResult r, string which) => r switch
        {
            PlonkVerifyResult.Invalid => $"{FailedProofPrefix} ({which}).",
            PlonkVerifyResult.NotImplemented => VerifierUnavailableReason,
            _ => $"PLONK verifier returned an error ({which}).",
        };

        private static bool TryDecodeProof(string b64, out byte[] bytes)
        {
            try { bytes = Convert.FromBase64String(b64); return bytes.Length > 0; }
            catch { bytes = Array.Empty<byte>(); return false; }
        }

        // ── Before the height: the original optional-proof behaviour, unchanged ──────────────────────────────

        /// <summary>
        /// Before the proof-rules height proofs are optional and are not checked: whether a node checked one depended on
        /// whether it had loaded its proving parameters (a node just started, or without the download, took any proof)
        /// and on which parameters (9.x's refuse proofs made under 8.x's): nodes judged one block differently (fifth
        /// review, round 32). The pre-epoch rules do not rely on these proofs.
        /// </summary>
        private static (bool ok, string message) LegacyOptionalProofs(Transaction tx, PrivateTxPayload payload) => (true, "");
    }
}
