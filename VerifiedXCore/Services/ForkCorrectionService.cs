using Newtonsoft.Json;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Services
{
    /// <summary>
    /// PHASE 2: Handles fork correction messages from casters.
    /// When a caster detects a hash split among peer casters and votes on the canonical
    /// block (3/5 majority), it broadcasts the correction to all connected validators.
    /// This service processes those corrections on the validator side.
    /// </summary>
    public static class ForkCorrectionService
    {
        /// <summary>Replaceable for tests: whether the block carries a valid committee certificate (strict).</summary>
        internal static Func<Block, bool> HasValidCertificate = ConsensusCertificateVerifier.HasValidCertificate;

        /// <summary>
        /// Replaceable for tests: the bounded reorg onto the corrected block. It shares the recovery interlock, opens the
        /// download phase so the block is accepted, validates it fully and checks the result; the earlier in-house
        /// rollback left the node resyncing, so the replacement block was always refused and never applied.
        /// </summary>
        internal static Func<long, string, string, string, Block?, Task<bool>> Reorg = Utilities.ForkRecoveryUtility.ReorgToBranchAsync;

        /// <summary>
        /// Handles an incoming fork correction from a caster.
        /// If our block at the given height has a different hash, we rollback and apply
        /// the correct block. If hashes match, this is a no-op.
        /// </summary>
        /// <param name="height">The block height being corrected</param>
        /// <param name="blockJson">JSON-serialized correct block from caster majority vote</param>
        /// <param name="casterIP">IP of the caster that sent this correction</param>
        public static async Task HandleForkCorrectionAsync(long height, string blockJson, string casterIP)
        {
            try
            {
                // Wave 5 (interlock unification): exactly one rollback writer at any instant —
                // never interleave a pushed correction with an active RecoverAsync/ReorgToBranch.
                if (Utilities.ForkRecoveryUtility.IsRecoveryInProgress)
                {
                    LogUtility.Log($"[ForkCorrection] Skipping correction from {casterIP} — recovery/reorg already in progress.", "ForkCorrectionService");
                    return;
                }

                if (string.IsNullOrEmpty(blockJson))
                {
                    LogUtility.Log($"[ForkCorrection] Empty blockJson received from {casterIP}.", "ForkCorrectionService");
                    return;
                }

                var correctBlock = JsonConvert.DeserializeObject<Block>(blockJson);
                if (correctBlock == null)
                {
                    LogUtility.Log($"[ForkCorrection] Failed to deserialize block from {casterIP}.", "ForkCorrectionService");
                    return;
                }

                // Validate chain reference
                if (correctBlock.ChainRefId != BlockchainData.ChainRef)
                {
                    LogUtility.Log($"[ForkCorrection] Rejecting correction from {casterIP}: wrong ChainRefId.", "ForkCorrectionService");
                    return;
                }

                // Validate block signature before trusting it
                if (correctBlock.Height > 0)
                {
                    var sigValid = SignatureService.VerifySignature(
                        correctBlock.Validator, correctBlock.Hash, correctBlock.ValidatorSignature);
                    if (!sigValid)
                    {
                        LogUtility.Log(
                            $"[ForkCorrection] Rejecting correction from {casterIP}: invalid block signature for height {height}.",
                            "ForkCorrectionService");
                        return;
                    }
                }

                // The block must be the one the message names and must hash to its own hash: the rollback depth comes
                // from the height, and a signature over an arbitrary hash proves nothing about the block's content.
                if (correctBlock.Height != height || correctBlock.GetBlockHash() != correctBlock.Hash)
                {
                    LogUtility.Log($"[ForkCorrection] Rejecting correction from {casterIP}: block height or hash does not match (message height {height}).", "ForkCorrectionService");
                    return;
                }

                // The hash (and so the certificate) covers the merkle root, not the transactions: a certified header with
                // other transactions would roll the node back and only then fail validation.
                if (correctBlock.Transactions == null || correctBlock.NumOfTx != correctBlock.Transactions.Count
                    || correctBlock.MerkleRootOf() != correctBlock.MerkleRoot)
                {
                    LogUtility.Log($"[ForkCorrection] Rejecting correction from {casterIP}: the block's transactions do not match its header (height {height}).", "ForkCorrectionService");
                    return;
                }

                // The merkle root covers each transaction's carried hash, not its body: every body must hash to it, and every
                // signature (outside the hash) must verify, before anything is rolled back.
                var txError = TransactionIntegrityError(correctBlock);
                if (txError != null)
                {
                    LogUtility.Log($"[ForkCorrection] Rejecting correction from {casterIP} at height {height}: {txError}", "ForkCorrectionService");
                    return;
                }

                var myHeight = Globals.LastBlock.Height;

                // Case 1: The correction is for a block we already have — check if hash matches
                if (height == myHeight)
                {
                    if (Globals.LastBlock.Hash == correctBlock.Hash)
                    {
                        // We already have the correct block — no action needed
                        return;
                    }

                    // Our block hash differs from the caster majority — need to correct
                    LogUtility.Log(
                        $"[ForkCorrection] Hash divergence detected at height {height}. " +
                        $"Our hash={Globals.LastBlock.Hash?[..Math.Min(16, Globals.LastBlock.Hash?.Length ?? 0)]} " +
                        $"Correct hash={correctBlock.Hash?[..Math.Min(16, correctBlock.Hash?.Length ?? 0)]} " +
                        $"from caster {casterIP}. Initiating correction.",
                        "ForkCorrectionService");

                    await ReorgOntoAsync(correctBlock, casterIP);
                }
                // Case 2: The correction is for a height we haven't reached yet — just queue it
                else if (height == myHeight + 1)
                {
                    // This is the next expected block — add to download dict for normal processing. A certified one is the
                    // committee's approval for its height, as for a rollback below.
                    if (HasValidCertificate(correctBlock))
                        ApproveCertified(correctBlock);
                    BlockStaging.Stage(correctBlock, casterIP); // VX-19: de-duplicated staging

                    await BlockValidatorService.ValidateBlocks();
                }
                // Case 3: The correction is for a height below ours — we may need to rewind
                else if (height < myHeight && height > 0)
                {
                    // Check if our block at that height has the same hash
                    var ourBlock = BlockchainData.GetBlockByHeight(height);
                    if (ourBlock != null && ourBlock.Hash == correctBlock.Hash)
                    {
                        // We already have the correct block at this height
                        return;
                    }

                    LogUtility.Log(
                        $"[ForkCorrection] Correction for past height {height} (we're at {myHeight}). " +
                        $"Our hash at {height}={ourBlock?.Hash?[..Math.Min(16, ourBlock?.Hash?.Length ?? 0)]} " +
                        $"Correct hash={correctBlock.Hash?[..Math.Min(16, correctBlock.Hash?.Length ?? 0)]}. " +
                        $"Correcting {myHeight - height + 1} block(s).",
                        "ForkCorrectionService");

                    await ReorgOntoAsync(correctBlock, casterIP);
                }
                else
                {
                    // Correction is for a height too far ahead — ignore
                    LogUtility.Log(
                        $"[ForkCorrection] Ignoring correction from {casterIP} for height {height} (we're at {myHeight}).",
                        "ForkCorrectionService");
                }
            }
            catch (Exception ex)
            {
                LogUtility.Log($"[ForkCorrection] Error handling correction from {casterIP}: {ex.Message}", "ForkCorrectionService");
            }
        }

        /// <summary>
        /// Whether each transaction in <paramref name="block"/> carries the hash its content gives and the signature that hash
        /// needs, checked the way block validation checks it for the transaction's kind; null when they all do.
        /// </summary>
        internal static string? TransactionIntegrityError(Block block)
        {
            // Validation measures and applies each transaction at its block's height (tx.Height is in no hash); so does this.
            LedgerIntegrityRules.NormalizeTransactionHeights(block);
            foreach (var tx in block.Transactions)
            {
                // Validation skips an honored whitelist entry; so does this.
                if (LedgerIntegrityRules.IsHonoredWhitelistEntry(tx, block.Height))
                    continue;

                // VerifyTX reads Data and UnlockTime one way only (they are joined without a separator in the hash).
                if (tx.FromAddress != "Coinbase_BlkRwd" && tx.FromAddress != "Coinbase_TrxFees"
                    && LedgerIntegrityRules.CanonicalPreimage(tx) is { } preimageError)
                    return $"transaction {tx.Hash}: {preimageError}";

                // Sender-less, and no signature covers a reward's amount: the hash only, with its own values.
                if (tx.FromAddress == "Coinbase_BlkRwd" || tx.FromAddress == "Coinbase_TrxFees")
                {
                    if (!RewardHashMatches(tx))
                        return $"transaction {tx.Hash} does not hash to its carried hash.";
                    // The recipient and amount are joined in the hash: digits could move between them unless the recipient
                    // is a whole address, and between amount and fee unless the fee is written as validation requires.
                    if (!AddressValidateUtility.ValidateAddress(tx.ToAddress) || LedgerIntegrityRules.CanonicalPreimage(tx) != null)
                        return $"reward {tx.Hash} does not read one way.";
                    continue;
                }
                if (VerifiedXCore.Bitcoin.Attestation.BtcAttestTx.IsAttestationType(tx.TransactionType))
                {
                    if (!string.Equals(tx.Hash, tx.GetHash(), StringComparison.Ordinal))
                        return $"transaction {tx.Hash} does not hash to its carried hash.";
                    var envelopeError = VerifiedXCore.Bitcoin.Attestation.BtcAttestTxValidator.EnvelopeError(tx);
                    if (envelopeError != null)
                        return $"transaction {tx.Hash}: {envelopeError}";
                    if (!TransactionValidatorService.VerifyTXSize(tx, VerifiedXCore.Bitcoin.Attestation.BtcAttestRules.MaxReportTxBytes))
                        return $"transaction {tx.Hash} is larger than validation allows.";
                    continue;
                }

                if (VerifiedXCore.Privacy.PrivateTransactionTypes.IsPrivateTransaction(tx.TransactionType))
                {
                    if (!TransactionValidatorService.VerifyTXSize(tx, LedgerIntegrityRules.DefaultMaxTxSizeBytes))
                        return $"private transaction {tx.Hash} is larger than validation allows.";

                    if (!VerifiedXCore.Privacy.PrivateTransactionValidatorService.VerifyPrivateHash(tx, block.Height))
                        return $"private transaction {tx.Hash} does not hash to its carried hash.";
                    var signatureOk = VerifiedXCore.Privacy.PrivateTransactionTypes.IsTransparentShield(tx.TransactionType)
                        ? !string.IsNullOrEmpty(tx.Signature) && SignatureService.VerifySignature(tx.FromAddress, tx.Hash, tx.Signature)
                        : tx.Signature == VerifiedXCore.Privacy.PrivacyConstants.PlonkSignatureSentinel;
                    if (!signatureOk)
                        return $"private transaction {tx.Hash} has no valid signature.";
                    // The private hash leaves out the recipient (and, past a shield, the sender, amount, nonce and fee):
                    // those are held to the values the validator requires.
                    if ((tx.FromAddress ?? "").StartsWith("xRBX") || (tx.ToAddress ?? "").StartsWith("xRBX"))
                        return $"private transaction {tx.Hash} involves a reserve (xRBX) address.";
                    if (VerifiedXCore.Privacy.PrivateTransactionTypes.IsTransparentShield(tx.TransactionType))
                    {
                        if (tx.ToAddress != VerifiedXCore.Privacy.PrivacyConstants.ShieldedPoolAddress)
                            return $"private transaction {tx.Hash} is a shield not sent to the shielded pool.";
                        // Below ShieldFeeSignedHeight the fee is outside the hash: held to the shield validator's rule.
                        if (tx.Fee <= 0 || (Globals.LastBlock.Height > Globals.TXHeightRule2 && tx.Fee < Globals.MinFeePerKB))
                            return $"private transaction {tx.Hash} is a shield without the fee validation requires.";
                    }
                    else
                    {
                        if (!VerifiedXCore.Privacy.PrivateTxPayloadCodec.TryDecode(tx.Data, out var payload, out _) || payload == null)
                            return $"private transaction {tx.Hash} carries no readable payload.";
                        var (envelopeOk, envelopeMessage) = VerifiedXCore.Privacy.PrivateTransactionValidatorService.ValidateZkPrivate(tx, payload);
                        if (!envelopeOk)
                            return $"private transaction {tx.Hash}: {envelopeMessage}";
                        // An unshield's outer recipient and amount are its payload's, on the heights validation holds them to it.
                        if ((block.Height >= Globals.PrivateTxSupplyRulesHeight || block.Height <= VerifiedXCore.Privacy.PrivateTxSupplyRules.UnshieldBindingScannedThrough)
                            && VerifiedXCore.Privacy.PrivateTxSupplyRules.OuterFieldsError(tx, payload) is { } outerError)
                            return $"private transaction {tx.Hash}: {outerError}";
                    }
                    continue;
                }

                if (!HashMatchesStrictly(tx))
                    return $"transaction {tx.Hash} does not hash to its carried hash.";
                // The recipient and amount are joined in the hash: a whole recipient keeps digits from moving between them.
                // A name resolves against chain state, and the state in hand is the branch being replaced, so a name is
                // left to full validation (from PathAuditRulesHeight a name is refused outright, without state).
                if (LedgerIntegrityRules.RecipientIsNotAName(tx, block.Height) is { } nameError)
                    return $"transaction {tx.Hash}: {nameError}";
                if (!IsNameRecipient(tx.ToAddress) && TransactionValidatorService.RecipientError(tx) is { } recipientError)
                    return $"transaction {tx.Hash}: {recipientError}";
                if (!TransactionValidatorService.VerifyTXSize(tx, LargestSizeCap(tx, block.Height)))
                    return $"transaction {tx.Hash} is larger than validation allows."; // whitespace inside the signature's Base64 still verifies
                // A delayed reserve send's settlement time: with none, its digits could have moved into Data (the preimage
                // joins them). Reserve control transactions (Register, CallBack, Recover) carry none.
                if ((tx.FromAddress ?? "").StartsWith("xRBX") && tx.TransactionType != TransactionType.RESERVE && tx.UnlockTime == null)
                    return $"reserve transaction {tx.Hash} has no unlock time.";
                var signatureError = TransactionValidatorService.SignatureError(tx);
                if (signatureError != null)
                    return $"transaction {tx.Hash}: {signatureError}";
            }
            return null;
        }

        /// <summary>
        /// The standard hash check without its zero-amount fallback for a non-whole amount: VerifyTX re-hashes such an
        /// amount as 0, so a carried fraction passes it against a signature over 0 and is refused only by later rules. Here,
        /// before a rollback, the amount must be the one that was hashed (or its "N.0" spelling, or a zero amount).
        /// </summary>
        private static bool HashMatchesStrictly(Transaction tx)
        {
            if (string.Equals(tx.Hash, tx.GetHash(), StringComparison.Ordinal))
                return true;
            if (tx.Amount % 1 != 0)
                return false;
            return TransactionValidatorService.StandardHashMatches(tx);
        }

        /// <summary>Whether AddressValidateUtility.ValidateAddress would look this recipient up as a registered name.</summary>
        private static bool IsNameRecipient(string? to) =>
            !string.IsNullOrEmpty(to)
            && !to.StartsWith(VerifiedXCore.Privacy.ShieldedAddressConstants.Prefix, StringComparison.Ordinal)
            && (to.ToLower().Contains(".rbx") || to.ToLower().Contains(".vfx"));

        /// <summary>
        /// Whether a block reward hashes to its carried hash with the values it carries. A whole number built with no decimal
        /// places (32, 0) reads 32.0 and 0.0 once the block has crossed the wire, so each of Amount and Fee is also tried
        /// without trailing zeros: the same values, never another one.
        /// </summary>
        /// <summary>
        /// The largest size validation can allow this transaction, without reading state: the state in hand before a
        /// rollback is the branch being replaced, and MaxTxSizeBytes reads the vault a transfer moves. From
        /// VbtcVaultTxSizeHeight a vault creation, or a TKNZ/NFT Transfer() that carries a contract's code (the shape
        /// MaxTxSizeBytes then compares with the stored vault), may be as large as a vault; anything else is 30 KB.
        /// </summary>
        private static int LargestSizeCap(Transaction tx, long height)
        {
            if (height < Globals.VbtcVaultTxSizeHeight)
                return LedgerIntegrityRules.DefaultMaxTxSizeBytes;
            if (tx.TransactionType == TransactionType.VBTC_V2_CONTRACT_CREATE)
                return Globals.MaxVbtcVaultTxSizeBytes;
            if ((tx.TransactionType != TransactionType.TKNZ_TX && tx.TransactionType != TransactionType.NFT_TX) || tx.Data == null)
                return LedgerIntegrityRules.DefaultMaxTxSizeBytes;
            try
            {
                var payload = SmartContractDeployBinding.ReadPayload(tx.Data);
                return payload.Function == "Transfer()" && !string.IsNullOrEmpty(payload.ContractUID) && !string.IsNullOrEmpty(payload.Data)
                    ? Globals.MaxVbtcVaultTxSizeBytes
                    : LedgerIntegrityRules.DefaultMaxTxSizeBytes;
            }
            catch { return LedgerIntegrityRules.DefaultMaxTxSizeBytes; }
        }

        private static bool RewardHashMatches(Transaction tx)
        {
            foreach (var amount in new[] { tx.Amount, tx.Amount / 1.000000000000000000000000000000000m })
                foreach (var fee in new[] { tx.Fee, tx.Fee / 1.000000000000000000000000000000000m })
                {
                    var probe = new Transaction
                    {
                        Timestamp = tx.Timestamp, FromAddress = tx.FromAddress, ToAddress = tx.ToAddress, Amount = amount, Fee = fee,
                        Nonce = tx.Nonce, TransactionType = tx.TransactionType, Data = tx.Data, UnlockTime = tx.UnlockTime,
                    };
                    if (string.Equals(tx.Hash, probe.GetHash(), StringComparison.Ordinal))
                        return true;
                }
            return false;
        }

        /// <summary>
        /// The certificate is the committee's approval of the block: a hash a caster approved earlier for its height (or
        /// for the heights above it) would otherwise make validation refuse it.
        /// </summary>
        internal static void ApproveCertified(Block certified)
        {
            Globals.CasterApprovedBlockHashDict[certified.Height] = certified.Hash;
            foreach (var stale in Globals.CasterApprovedBlockHashDict.Keys.Where(h => h > certified.Height).ToList())
                Globals.CasterApprovedBlockHashDict.TryRemove(stale, out _);
        }

        /// <summary>
        /// Rolls back to and adopts the corrected block, only when it carries a valid committee certificate: a block a
        /// single key signed made every recipient roll back. Without one, the fork is left to ForkChoice and recovery.
        /// </summary>
        private static async Task ReorgOntoAsync(Block correctBlock, string casterIP)
        {
            if (!HasValidCertificate(correctBlock))
            {
                LogUtility.Log($"[ForkCorrection] Not rolling back for the correction from {casterIP} at height {correctBlock.Height}: it carries no valid committee certificate.", "ForkCorrectionService");
                return;
            }
            ApproveCertified(correctBlock);

            var adopted = await Reorg(correctBlock.Height, correctBlock.Hash, casterIP, "ForkCorrection", correctBlock);
            LogUtility.Log($"[ForkCorrection] Correction at height {correctBlock.Height} from {casterIP}: {(adopted ? "adopted" : "not adopted")}.", "ForkCorrectionService");
        }
    }
}