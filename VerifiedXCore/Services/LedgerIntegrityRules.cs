using Newtonsoft.Json.Linq;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Models;
using VerifiedXCore.Models.SmartContracts;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Services
{
    /// <summary>
    /// Security audit Sep 2026 — independent-review follow-ups (same class as VX-01/VX-02: ledger rows written for an
    /// address or amount the signer does not control). Stateless consensus predicates, shared by
    /// TransactionValidatorService and the AuditReplayScanService so the scan certifies exactly the enforced rules.
    /// Each returns null when the transaction satisfies the rule, else the rejection reason.
    /// </summary>
    public static class LedgerIntegrityRules
    {
        // ── NEW-04: fungible-token functions debit the address named in the data, so it must be the signer ──────

        public const string LegacyTokenTransferToAddress = "Token_Base";

        public static string? TokenTransfer(string txFrom, string txTo, string? dataFrom, string? dataTo, decimal? amount)
        {
            if (dataFrom != txFrom) return "Token transfer FromAddress must be the transaction signer.";
            // Older wallets addressed token transfers to "Token_Base" with the recipient only in the data; that shape is in
            // the chain history (found by the replay scan: 193 testnet transactions) and must keep replaying. The holder
            // signs the data, so the data recipient is authorised either way; the binding is a consistency rule.
            if (dataTo != txTo && txTo != LegacyTokenTransferToAddress) return "Token transfer ToAddress must be the transaction's ToAddress.";
            if (amount == null || amount.Value <= 0M) return "Token transfer amount must be greater than zero.";
            // NEW-09: the apply loads the sender and recipient accounts as two copies and saves the recipient's (stale,
            // pre-debit) copy last, so a transfer to oneself added the amount without the debit - a mint per transaction.
            if (dataTo == dataFrom) return "Token transfer to the sender's own address is refused.";
            return null;
        }

        /// <summary>
        /// NEW-09 (follow-up): the apply resolves both addresses through LiteDB's collation (case and invisible characters
        /// ignored), so a recipient that merely LOOKS different ("XABC…", "xa­bc…") still loads the sender's own account
        /// twice. Refused when the data recipient resolves to the sender's account.
        /// </summary>
        public static string? TokenTransferResolvesToSender(string? dataFrom, string? dataTo)
        {
            if (string.IsNullOrEmpty(dataFrom) || string.IsNullOrEmpty(dataTo)) return null;
            var toAccount = VerifiedXCore.Data.StateData.GetSpecificAccountStateTrei(dataTo);
            if (toAccount == null) return null;
            // Compare the two RESOLVED records (fifth review): a sender whose record was itself created under an alias
            // key (e.g. "xa­bc..." via an unvalidated payment recipient) resolves to a record whose Key differs from
            // its address text, yet sender and recipient still load that one record twice.
            var fromAccount = VerifiedXCore.Data.StateData.GetSpecificAccountStateTrei(dataFrom);
            var same = string.Equals(toAccount.Key, dataFrom, StringComparison.Ordinal)
                || (fromAccount != null && string.Equals(fromAccount.Key, toAccount.Key, StringComparison.Ordinal));
            return same ? "Token transfer recipient resolves to the sender's own account." : null;
        }

        public static string? TokenBurn(string txFrom, string? dataFrom, decimal? amount)
        {
            if (dataFrom != txFrom) return "Token burn FromAddress must be the transaction signer.";
            if (amount == null || amount.Value <= 0M) return "Token burn amount must be greater than zero.";
            return null;
        }

        public static string? TokenVoteCast(string txFrom, string? dataFrom) =>
            dataFrom != txFrom ? "Token vote FromAddress must be the transaction signer." : null;

        // ── NEW-05: legacy V1 vBTC (tokenization) transfers ────────────────────────────────────────────────────

        public static string? V1TransferAmount(decimal? amount) =>
            VBTCService.GetVbtcAmountError(amount, "legacy vBTC (V1) transfer");

        /// <summary>
        /// Each co-signed input must carry its holder's valid signature over SignatureInput + tx.ToAddress +
        /// tx.FromAddress (it was computed and ignored, so any holder's balance could be spent) and a positive amount.
        /// </summary>
        public static string? V1TransferMultiInput(VBTCTransferInput input, string signatureInput, string txTo, string txFrom)
        {
            if (input?.FromAddress == null || input.Signature == null) return "Missing signature/from address in inputs.";
            var amountError = VBTCService.GetVbtcAmountError(input.Amount, "legacy vBTC (V1) transfer input");
            if (amountError != null) return amountError;
            bool ok;
            try { ok = SignatureService.VerifySignature(input.FromAddress, signatureInput + txTo + txFrom, input.Signature); }
            catch { ok = false; }
            return ok ? null : $"Input signature for {input.FromAddress} is not valid.";
        }

        // ── NEW-06: one contract creation per ContractUID per block ─────────────────────────────────────────────

        /// <summary>
        /// The ContractUID a transaction creates (Mint(), TokenDeploy(), vBTC V2 contract create), or null. The
        /// "already deployed" checks read committed state only, so two creations of one UID in the same block both
        /// passed and the second credited its own supply under the first's contract.
        /// </summary>
        public static string? CreatedContractUid(Transaction tx)
        {
            if (tx?.Data == null) return null;
            try
            {
                var (_, _, uid, function, _) = TransactionUtility.GetSCTXFunctionAndUID(tx);
                if (tx.TransactionType == TransactionType.VBTC_V2_CONTRACT_CREATE || function == "Mint()" || function == "TokenDeploy()")
                    return string.IsNullOrEmpty(uid) ? null : uid;
            }
            catch { }
            return null;
        }

        // ── The rules read the contract call the dispatcher runs ────────────────────────────────────────────────

        /// <summary>The transaction types whose Data is a contract call that validation and state application dispatch on.</summary>
        public static readonly TransactionType[] ContractCallTypes =
        {
            TransactionType.NFT_TX, TransactionType.NFT_MINT, TransactionType.NFT_BURN, TransactionType.NFT_SALE,
            TransactionType.FTKN_MINT, TransactionType.FTKN_TX, TransactionType.FTKN_BURN,
            TransactionType.TKNZ_MINT, TransactionType.TKNZ_TX, TransactionType.TKNZ_BURN,
            TransactionType.SC_MINT, TransactionType.SC_TX, TransactionType.SC_BURN,
            TransactionType.VBTC_V2_CONTRACT_CREATE, TransactionType.TKNZ_WD_ARB, TransactionType.TKNZ_WD_OWNER,
        };

        /// <summary>The function and contract a contract call names, read exactly as the dispatchers read them (VerifyTX and StateData.UpdateTreis).</summary>
        public static (string? Function, string? ContractUID) DispatchedCall(string? txData)
        {
            if (string.IsNullOrEmpty(txData)) return (null, null);
            // Step for step as the dispatchers do it: the function is kept even when reading the contract's UID then
            // fails (the dispatcher runs it with no UID), and the single-object shape is tried only when the array
            // shape gave nothing at all.
            string? function = null, uid = null;
            var read = false;
            try
            {
                var first = Newtonsoft.Json.JsonConvert.DeserializeObject<JArray>(txData)![0];
                function = (string?)first["Function"];
                uid = (string?)first["ContractUID"];
                read = true;
            }
            catch { }
            try
            {
                if (!read)
                {
                    var obj = JObject.Parse(txData);
                    uid = obj["ContractUID"]?.ToObject<string?>();
                    function = obj["Function"]?.ToObject<string?>();
                }
            }
            catch { }
            return (function, uid);
        }

        /// <summary>The first element of a contract call (array shape) or the call itself (object shape); null when it is neither.</summary>
        private static JObject? CallObject(string txData)
        {
            try
            {
                var root = JToken.Parse(txData);
                return root is JArray arr ? (arr.Count > 0 ? arr[0] as JObject : null) : root as JObject;
            }
            catch { return null; }
        }

        /// <summary>
        /// The rules below decide from <see cref="SmartContractDeployBinding.ReadPayload"/>; the dispatchers that run a
        /// contract call read only its Function and ContractUID. The reader takes Function, ContractUID, Data and MD5List
        /// in one step, so a call whose MD5List (or Data) is not text, for example <c>"MD5List":{}</c>, made it give up
        /// and report no function at all, while the dispatcher still ran the function: every rule that asks "is this a
        /// Burn(), a rewrite, a V1 function" passed, and the vault was burned, rewritten, or written to with the retired
        /// V1 ledger function (second path audit, 14 October 2026). From Globals.PathAuditRulesHeight a contract call
        /// that the dispatcher would run and the reader cannot read the same way is refused. Null when the rule passes.
        /// </summary>
        public static string? ContractCallIsReadable(Transaction tx, long height)
        {
            if (tx?.Data == null || height < Globals.PathAuditRulesHeight) return null;
            if (!ContractCallTypes.Contains(tx.TransactionType)) return null;
            var call = DispatchedCall(tx.Data);
            if (string.IsNullOrWhiteSpace(call.Function)) return null; // nothing is dispatched
            var read = SmartContractDeployBinding.ReadPayload(tx.Data);
            if (!string.Equals(read.Function, call.Function, StringComparison.Ordinal) || !string.Equals(read.ContractUID, call.ContractUID, StringComparison.Ordinal))
                return "This transaction's contract call cannot be read: its Function, ContractUID, Data and MD5List must each be text.";
            // The two other fields the apply reads from the call. One that is not a plain value passed validation
            // (nothing here reads it) and made the apply throw on every node: a transaction mined and never applied.
            var fields = CallObject(tx.Data);
            if (fields != null && ((fields["Locators"] is { } locators && locators is not JValue) || (fields["Amount"] is { } amount && amount is not JValue)))
                return "This transaction's contract call cannot be read: its Locators and Amount must each be a plain value.";
            return null;
        }

        // ── A vault that is changing hands; a recipient that can hold vBTC ───────────────────────────────────────

        /// <summary>
        /// While a vault the chain watches is marked for a new owner (a sale in progress, a reserve account's delayed
        /// transfer), its owner moves no vBTC out of it: no transfer, withdrawal or bridge lock. The owner's balance is
        /// the vault's, and goes with it; a seller could put a transfer of all of it in the block that completes the
        /// sale, and the buyer paid for an empty vault (second path audit, 14 October 2026). Holders are not touched.
        /// Null when the rule passes.
        /// </summary>
        public static string? OwnerKeepsAVaultWholeWhileItChangesHands(Transaction tx)
        {
            if (tx == null || string.IsNullOrEmpty(tx.FromAddress)) return null;
            foreach (var (key, _) in SameBlockDebitGuard.GetDebits(tx))
            {
                if (key.Kind != SameBlockDebitGuard.LedgerKind.VbtcV2 || key.Holder != tx.FromAddress) continue;
                var contract = SmartContractStateTrei.GetSmartContractState(key.ContractUid);
                if (contract == null || contract.OwnerAddress != tx.FromAddress || (contract.NextOwner == null && !contract.IsLocked)) continue;
                if (!VerifiedXCore.Bitcoin.Attestation.BtcAttestBalance.IsAttestedVault(contract.SmartContractUID)) continue;
                return $"Vault {contract.SmartContractUID} is being transferred or sold. Its owner moves no vBTC out of it until that is completed or called back.";
            }
            return null;
        }

        /// <summary>
        /// From Globals.PathAuditRulesHeight vBTC is transferred to an address somebody can sign for: a VFX address (a
        /// reserve account's included), not a shielded address, a name, or one of the chain's placeholder addresses.
        /// The ledger credits the transaction's ToAddress as it stands, and a balance under a key nobody holds is lost:
        /// its owner cannot take it back. Null when the rule passes.
        /// </summary>
        public static string? VbtcRecipientCanHoldIt(Transaction tx, long height)
        {
            if (tx == null || height < Globals.PathAuditRulesHeight) return null;
            var function = tx.TransactionType == TransactionType.VBTC_V2_TRANSFER ? "TransferVBTCV2()" : DispatchedCall(tx.Data).Function;
            if (tx.TransactionType != TransactionType.VBTC_V2_TRANSFER && function != "TransferVBTCV2()" && function != "TransferVBTCMultiV2()") return null;
            var to = tx.ToAddress ?? "";
            return !to.StartsWith(VerifiedXCore.Privacy.ShieldedAddressConstants.Prefix, StringComparison.Ordinal) && AddressValidateUtility.ValidateRBXAddress(to)
                ? null
                : "vBTC can only be transferred to a VFX address (not a shielded address, a name or a placeholder): nobody could spend it there.";
        }

        // ── NEW-26 (follow-up): a vBTC V2 vault's code never changes after creation ─────────────────────────────

        /// <summary>The contract functions whose apply overwrites the stored ContractData with the carried body.</summary>
        public static readonly string[] ContractRewritingFunctions = { "Update()", "Transfer()", "Evolve()", "Devolve()", "ChangeEvolveStateSpecific()" };

        /// <summary>
        /// Update(), Transfer() and the Evolve functions write the body they carry over the stored ContractData, so the owner
        /// of a vBTC V2 vault could replace its code after creation - including the DepositAddress and the DKG data every
        /// vault reader and the NEW-26 rule rely on (review round 7). On a vault, the carried body must equal the stored code
        /// exactly: honest transfers resend it unchanged (all 8 mainnet and 1 testnet vault transfers did; no vault was ever
        /// updated or evolved), and an empty body (which would wipe the code) is refused. Reads the payload exactly as the
        /// StateData dispatcher does (case-sensitive "Data"/"ContractUID"). Null when the rule passes.
        /// </summary>
        public static string? VaultCodeUnchanged(Transaction tx)
        {
            if (tx?.Data == null) return null;
            var payload = SmartContractDeployBinding.ReadPayload(tx.Data);
            if (payload.Function == null || !ContractRewritingFunctions.Contains(payload.Function) || string.IsNullOrEmpty(payload.ContractUID))
                return null;
            var stored = SmartContractStateTrei.GetSmartContractState(payload.ContractUID);
            if (stored == null || !VBTCService.IsVbtcV2Contract(stored))
                return null;
            return string.Equals(payload.Data, stored.ContractData, StringComparison.Ordinal)
                ? null
                : "A vBTC V2 vault's contract code cannot be changed after creation.";
        }

        // ── A vBTC V2 vault is never burned, and its UID is never created twice ─────────────────────────────────

        /// <summary>How long after its UID was made a vault may still be created (the UID ends in the time it was made).</summary>
        public const long VaultUidFreshSeconds = 7 * 24 * 60 * 60;
        /// <summary>The same for a vault the chain watches, from Globals.PathAuditRulesHeight: inside the look-back its Bitcoin is watched from.</summary>
        public const long WatchedVaultUidFreshSeconds = 12 * 60 * 60;

        /// <summary>
        /// Burn() deletes a contract's state record. A vault's ledger is on that record, so burning a vault destroys
        /// every holder's vBTC, strands the Bitcoin, and (with <see cref="VaultUidIsNew"/> missing) let the owner start
        /// the same vault again with an empty ledger. Refused for a vault the chain watches at any height, and for
        /// every vBTC V2 vault from Globals.VaultBurnRuleHeight. Reads the payload exactly as the StateData dispatcher
        /// does. Null when the rule passes.
        /// </summary>
        public static string? VaultNotBurned(Transaction tx, long height)
        {
            if (tx?.Data == null) return null;
            var payload = SmartContractDeployBinding.ReadPayload(tx.Data);
            if (payload.Function != "Burn()" || string.IsNullOrEmpty(payload.ContractUID)) return null;
            var stored = SmartContractStateTrei.GetSmartContractState(payload.ContractUID);
            if (stored == null || !VBTCService.IsVbtcV2Contract(stored)) return null;
            if (height < Globals.VaultBurnRuleHeight && !VerifiedXCore.Bitcoin.Attestation.BtcAttestBalance.IsAttestedVault(stored.SmartContractUID))
                return null;
            return "A vBTC V2 vault cannot be burned: its ledger, every holder's vBTC and the record of its Bitcoin live on the contract.";
        }

        private const string VaultCreatedBefore = "This vBTC V2 vault was created before; a vault's UID is never created twice.";

        /// <summary>
        /// No contract is created under the UID of a vault the chain watches or ever watched: its deposit script, its
        /// coins and its withdrawals are kept under that UID and are never deleted. Any creation, at any height (only
        /// these rules write a watched script). Null when the rule passes.
        /// </summary>
        public static string? CreatedUidWasNeverWatched(Transaction tx)
        {
            var uid = CreatedContractUid(tx);
            if (uid == null) return null;
            if (VerifiedXCore.Bitcoin.Attestation.BtcAttestStore.ScriptRowForVault(uid) != null) return VaultCreatedBefore;
            // Nor under the UID the chain fixed for a due key's successor (plan 8.8): an S3C vault made in seconds by a
            // few validators under it blocked the re-key, attempt after attempt (fourth review, round 3).
            if (VerifiedXCore.Bitcoin.Attestation.BtcAttestStore.Db != null && VerifiedXCore.Bitcoin.Attestation.BtcVaultKeys.DueKeyClaiming(uid) is { } dueVault)
                return $"This UID is the one fixed for vault {dueVault}'s next key; no contract is created under it.";
            // Nor under the UID of a vault's later key (plan 8.8): its holders file that key's shares under it.
            return VerifiedXCore.Bitcoin.Attestation.BtcAttestStore.Db?.GetCollection<VerifiedXCore.Bitcoin.Attestation.VaultDepositScriptRow>(VerifiedXCore.Bitcoin.Attestation.BtcAttestStore.Scripts)
                       .Exists(x => x.KeyUID == uid) == true
                ? "This UID is a vault key's; no contract is created under it."
                : null;
        }

        /// <summary>
        /// A vault is created once. What is kept outside a vault's state record (its withdrawals, its bridge locks) is
        /// kept under its UID and outlives the record, so a second creation under the same UID would inherit all of it
        /// with an empty ledger. From Globals.VaultBurnRuleHeight a vault's creation is refused when its UID has mined
        /// withdrawals or bridge locks, or when the UID was not made recently: a UID ends in the time it was made, and a
        /// vault's creation follows its key ceremony within minutes, so an old creation cannot be sent again later.
        /// Called for a creation that carries a TokenizationV2 feature. Null when the rule passes.
        /// </summary>
        public static string? VaultUidIsNew(Transaction tx, string uid, long height)
        {
            const string again = VaultCreatedBefore;
            if (height < Globals.VaultBurnRuleHeight) return null;

            if (VBTCWithdrawalRequest.GetVBTCWithdrawalRequestDb()?.Exists(x => x.SmartContractUID == uid && x.TransactionHash != null && x.TransactionHash != "") == true)
                return again;
            if (VBTCBridgeLockState.GetCollection().Exists(x => x.SmartContractUID == uid))
                return again;

            var colon = uid.LastIndexOf(':');
            if (colon < 0 || !long.TryParse(uid.AsSpan(colon + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var made))
                return "A vBTC V2 vault's UID ends in the time it was made (the UID the key ceremony ran under).";
            if (made > tx.Timestamp + 3600 || made < tx.Timestamp - VaultUidFreshSeconds)
                return $"This vBTC V2 vault's UID was not made within the last {VaultUidFreshSeconds / 86400} days. Run the key ceremony again for a new vault.";
            if (height >= Globals.PathAuditRulesHeight)
            {
                // A vault the chain watches is watched from about a day of Bitcoin blocks before its creation. Its
                // deposit address exists from its key ceremony, so a vault created days after the ceremony could have
                // Bitcoin at its address from before that window, which no report can then credit and no withdrawal
                // can spend. Twelve hours keeps the ceremony inside the window.
                if (made < tx.Timestamp - WatchedVaultUidFreshSeconds && VerifiedXCore.Bitcoin.Attestation.BtcAttestRules.Recording(height))
                    return $"This vBTC V2 vault's UID was not made within the last {WatchedVaultUidFreshSeconds / 3600} hours. Run the key ceremony again for a new vault.";
                // And a vault burned just before burning stopped cannot come back: no vault is created under a UID
                // from before these rules (a UID may be dated up to an hour ahead of its transaction).
                var before = VerifiedXCore.Data.BlockchainData.GetBlockByHeight(Globals.PathAuditRulesHeight - 1);
                if (before != null && made < before.Timestamp + 3600 + Globals.MaxFutureSkewSeconds)
                    return "This vBTC V2 vault's UID was made before the present rules started. Run the key ceremony again for a new vault.";
            }
            return null;
        }

        // ── Fund-loss audit item 4: a contract that is not a vault cannot become one by rewrite ──────────────────

        /// <summary>
        /// From Globals.VaultRewriteRulesHeight, the body carried by a rewriting function for a stored contract that is
        /// NOT a vBTC V2 vault must not decompile to a TokenizationV2 feature. The DKG attestation (NEW-26) runs on
        /// creations only and VaultCodeUnchanged only protects contracts already recognised as vaults, so an owner could
        /// Update() a plain NFT into a vault naming any deposit address and FROST group key. A body identical to the
        /// stored one is never decompiled (honest transfers resend it unchanged). Null when the rule passes.
        /// </summary>
        public static string? VaultNotCreatedByRewrite(Transaction tx, long height)
        {
            if (tx?.Data == null || height < Globals.VaultRewriteRulesHeight) return null;
            var payload = SmartContractDeployBinding.ReadPayload(tx.Data);
            if (payload.Function == null || !ContractRewritingFunctions.Contains(payload.Function)
                || string.IsNullOrEmpty(payload.ContractUID) || string.IsNullOrEmpty(payload.Data))
                return null;
            var stored = SmartContractStateTrei.GetSmartContractState(payload.ContractUID);
            if (stored == null || VBTCService.IsVbtcV2Contract(stored))
                return null; // missing: the function's own rule; a vault: VaultCodeUnchanged
            if (string.Equals(payload.Data, stored.ContractData, StringComparison.Ordinal))
                return null;
            // (From Globals.PathAuditRulesHeight a body whose code is not timeless is never a vault to any reader, so it
            // is not run to ask: its answer could differ on a node that validates this block later.)
            if (height >= Globals.PathAuditRulesHeight && !ContractCodeIsTimeless(payload.Data)) return null;
            return CarriesTokenizationV2(payload.Data)
                ? "A contract that is not a vBTC V2 vault cannot be given a TokenizationV2 feature; vaults are created only with a validator-attested key ceremony."
                : null;
        }

        /// <summary>Whether a carried contract body decompiles to a contract with a TokenizationV2 feature. Never throws.</summary>
        public static bool CarriesTokenizationV2(string? body)
        {
            if (string.IsNullOrEmpty(body)) return false;
            try
            {
                var scMain = SmartContractMain.GenerateSmartContractInMemory(body);
                return scMain?.Features?.Any(f => f != null && f.FeatureName == FeatureName.TokenizationV2) == true;
            }
            catch { return false; }
        }

        // ── Fund-loss audit item 9: a contract's Royalty feature never changes after creation ────────────────────

        /// <summary>
        /// From Globals.RoyaltyRewriteRulesHeight, the body carried by a rewriting function must keep the stored body's
        /// Royalty feature exactly (present or not, amount, pay-to address). Evolve()/Devolve() check only not-locked,
        /// sender == minter and recipient == owner, then overwrite the body, and a sale reads the royalty from the current
        /// body: the original minter could rewrite a sold NFT to a 99.99% royalty to itself whenever it was unlocked. A
        /// body identical to the stored one is never decompiled. Null when the rule passes.
        /// </summary>
        public static string? RoyaltyUnchangedByRewrite(Transaction tx, long height)
        {
            if (tx?.Data == null || height < Globals.RoyaltyRewriteRulesHeight) return null;
            var payload = SmartContractDeployBinding.ReadPayload(tx.Data);
            if (payload.Function == null || !ContractRewritingFunctions.Contains(payload.Function)
                || string.IsNullOrEmpty(payload.ContractUID) || string.IsNullOrEmpty(payload.Data))
                return null;
            var stored = SmartContractStateTrei.GetSmartContractState(payload.ContractUID);
            if (stored == null || string.Equals(payload.Data, stored.ContractData, StringComparison.Ordinal))
                return null;
            var before = RoyaltyOf(stored.ContractData);
            var after = RoyaltyOf(payload.Data);
            if (before == null && after == null) return null;
            if (before != null && after != null
                && before.Value.Amount == after.Value.Amount
                && string.Equals(before.Value.PayTo, after.Value.PayTo, StringComparison.Ordinal))
                return null;
            return $"A contract's Royalty feature cannot be changed after creation (stored: {Describe(before)}; carried: {Describe(after)}).";
        }

        private static string Describe((decimal Amount, string PayTo)? r) => r == null ? "none" : $"{r.Value.Amount} to {r.Value.PayTo}";

        /// <summary>The Royalty feature (amount, pay-to) a contract body decompiles to, or null when it has none. Never throws.</summary>
        public static (decimal Amount, string PayTo)? RoyaltyOf(string? body)
        {
            if (string.IsNullOrEmpty(body)) return null;
            try
            {
                var scMain = SmartContractMain.GenerateSmartContractInMemory(body);
                var feat = scMain?.Features?.FirstOrDefault(f => f != null && f.FeatureName == FeatureName.Royalty);
                if (feat == null) return null;
                var rf = feat.FeatureFeatures as RoyaltyFeature;
                if (rf == null)
                {
                    try { rf = Newtonsoft.Json.JsonConvert.DeserializeObject<RoyaltyFeature>(feat.FeatureFeatures?.ToString() ?? ""); } catch { }
                }
                return rf == null ? null : (rf.RoyaltyAmount, rf.RoyaltyPayToAddress ?? "");
            }
            catch { return null; }
        }

        // ── vBTC V2 vault transaction size ─────────────────────────────────────────────────────────────────────

        public const int DefaultMaxTxSizeBytes = 30 * 1024;

        /// <summary>
        /// The size cap for a transaction mined at <paramref name="height"/>: Globals.MaxVbtcVaultTxSizeBytes from
        /// Globals.VbtcVaultTxSizeHeight for a vault creation, or for a Transfer() of an on-chain vault that carries the
        /// vault's stored code exactly (the only body VaultCodeUnchanged admits, so no transfer outgrows its vault's
        /// creation); 30 KB for everything else. A creation's body is judged by the creation rules that follow the size
        /// check (NEW-26 attestation, decompile), so a large creation that is not a valid vault is still refused.
        /// </summary>
        public static int MaxTxSizeBytes(Transaction tx, long height)
        {
            if (tx == null || height < Globals.VbtcVaultTxSizeHeight)
                return DefaultMaxTxSizeBytes;
            if (tx.TransactionType == TransactionType.VBTC_V2_CONTRACT_CREATE)
                return Globals.MaxVbtcVaultTxSizeBytes;
            // Vault transfers go out as TKNZ_TX (wallet and raw routes); the generic NFT transfer path (NFT_TX) can carry one too.
            if ((tx.TransactionType != TransactionType.TKNZ_TX && tx.TransactionType != TransactionType.NFT_TX) || tx.Data == null)
                return DefaultMaxTxSizeBytes;
            try
            {
                var payload = SmartContractDeployBinding.ReadPayload(tx.Data);
                if (payload.Function != "Transfer()" || string.IsNullOrEmpty(payload.ContractUID) || string.IsNullOrEmpty(payload.Data))
                    return DefaultMaxTxSizeBytes;
                var stored = SmartContractStateTrei.GetSmartContractState(payload.ContractUID);
                if (stored == null || !string.Equals(payload.Data, stored.ContractData, StringComparison.Ordinal) || !VBTCService.IsVbtcV2Contract(stored))
                    return DefaultMaxTxSizeBytes;
                return Globals.MaxVbtcVaultTxSizeBytes;
            }
            catch { return DefaultMaxTxSizeBytes; }
        }

        // ── NEW-27: whitelisted transactions ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// Whether a transaction on the bad-transaction whitelist (Globals.BadTxList / BadNFTTxList) skips validation here.
        /// VerifyTX and the per-block debit guard used to skip every check for ANY transaction whose carried Hash was listed.
        /// Nothing binds a carried hash to the content (the merkle root is built from the stored hashes), so any content
        /// carrying a listed hash verified, at admission and in blocks - e.g. a victim's balance sent to an attacker, unsigned.
        /// An entry (added by an operator on this node) is now honoured only in block validation, never at admission or
        /// proposal, and only for the content its hash commits to.
        /// </summary>
        public static bool IsHonoredWhitelistEntry(Transaction? tx, long? blockHeight)
        {
            if (tx == null || string.IsNullOrEmpty(tx.Hash) || blockHeight == null) return false;
            // Historical mainnet transactions (owner decision): the fixed list, each bound to its own block height and content.
            if (HistoricalTransactionExceptions.IsAccepted(tx, blockHeight)) return true;
            bool listed;
            try { listed = Globals.BadTxList.Contains(tx.Hash) || Globals.BadNFTTxList.Contains(tx.Hash); }
            catch { listed = false; } // the lists are edited from the console
            if (!listed) return false;
            try { return tx.GetHash() == tx.Hash; } catch { return false; }
        }

        // ── NEW-26: a vBTC V2 deposit address is the validators' FROST key ──────────────────────────────────────

        /// <summary>
        /// From Globals.VbtcV2DkgAttestationHeight, a contract creation whose body carries a TokenizationV2 feature must
        /// bind its DepositAddress to its FROST group key and carry validator attestations of the DKG
        /// (FrostDkgAttestation.Validate). The eligible validators are derived from committed blocks up to height - 1.
        /// </summary>
        public static string? VbtcV2DkgAttestation(Transaction tx, long height)
        {
            if (height < Globals.VbtcV2DkgAttestationHeight) return null;
            var uid = CreatedContractUid(tx);
            if (uid == null) return null;
            var payload = SmartContractDeployBinding.ReadPayload(tx.Data);
            // Whether this creation makes a vault is asked of the body's code. Code that is not timeless can answer no
            // today and yes to a node that validates this block later (which would then refuse a block the network
            // accepted and never sync past it), so from Globals.PathAuditRulesHeight it is not asked at all: such a
            // body is never a vault to any reader (VBTCService.IsVbtcV2Contract), and the creation type that must
            // make one is refused.
            if (height >= Globals.PathAuditRulesHeight && NotTimelessBecause(payload.Data) is { } notTimeless)
                return tx.TransactionType == TransactionType.VBTC_V2_CONTRACT_CREATE
                    ? $"A vBTC V2 vault's contract code must read the same on every node at every time: {notTimeless}."
                    : null;
            var bindingError = SmartContractDeployBinding.Validate(payload.Data, uid, tx.FromAddress, isTokenDeploy: payload.Function == "TokenDeploy()", out var decompiled);
            if (bindingError != null) return bindingError;
            // Every reader of a vault's TokenizationV2 data decompiles the body, so a body that does not decompile is never a
            // vault; after activation the vBTC V2 creation type must still carry one that does.
            if (decompiled == null)
                return tx.TransactionType == TransactionType.VBTC_V2_CONTRACT_CREATE ? "vBTC V2 contract body could not be decompiled." : null;
            var v2 = decompiled.Features?.Where(f => f != null && f.FeatureName == FeatureName.TokenizationV2).ToList();
            if (v2 == null || v2.Count == 0) return null;
            if (v2.Count > 1) return "A contract may carry only one TokenizationV2 feature.";
            // A vault's contract is its TokenizationV2 data and nothing that could make it read differently later
            // or on another node (see VaultCodeIsPlain).
            if (height >= Globals.PathAuditRulesHeight)
            {
                if (decompiled.Features!.Count(f => f != null) != 1)
                    return "A vBTC V2 vault's contract carries the TokenizationV2 feature and no other.";
            }
            var reuseError = VaultUidIsNew(tx, uid, height);
            if (reuseError != null) return reuseError;
            // From the recording height a vault is a contract made with Mint(), whose deposit script the chain then
            // watches. One made as a token would be a vault the chain does not watch, left to the earlier rules.
            if (payload.Function == "TokenDeploy()" && VerifiedXCore.Bitcoin.Attestation.BtcAttestRules.Recording(height))
                return "A vBTC V2 vault is created with Mint(), not deployed as a token.";
            VerifiedXCore.Models.SmartContracts.TokenizationV2Feature? feature;
            try
            {
                feature = v2[0].FeatureFeatures as VerifiedXCore.Models.SmartContracts.TokenizationV2Feature
                    ?? Newtonsoft.Json.JsonConvert.DeserializeObject<VerifiedXCore.Models.SmartContracts.TokenizationV2Feature>(v2[0].FeatureFeatures?.ToString() ?? "");
            }
            catch { feature = null; }
            // The height the key ceremony's proof names is read by the vault's signers; nothing checked it, and one in
            // the future made every signer of the vault fail on its withdrawals (second path audit, 14 October 2026).
            if (height >= Globals.PathAuditRulesHeight && feature != null && (feature.ProofBlockHeight < 0 || feature.ProofBlockHeight > height))
                return "A vBTC V2 vault's ProofBlockHeight is a block that exists: not after the block that creates the vault.";
            // A deposit script belongs to one vault. A second vault carrying a watched vault's address would get no
            // coins of its own and would not be watched, so it would fall to the earlier rules, which read its
            // deposit from that address: the other vault's Bitcoin. (The key ceremony makes such an address
            // unreachable for an honest set of validators; this does not depend on that.)
            if (VerifiedXCore.Bitcoin.Attestation.BtcCanonicalWithdrawal.TryDestinationScript(feature?.DepositAddress, out var depositScript)
                && VerifiedXCore.Bitcoin.Attestation.BtcAttestStore.ScriptRow(depositScript) is { } taken && taken.SmartContractUID != uid)
                return $"This deposit address already belongs to vault {taken.SmartContractUID}.";
            var ceremonyError = VerifiedXCore.Bitcoin.FROST.FrostDkgAttestation.Validate(feature, uid, tx.FromAddress,
                () => ValidatorBond.CeremonyEligible(VBTCValidatorRegistry.GetActiveValidatorsAt(height - 1), height), // active, funded now, and (from the bond height) a stake locked for two days
                requireKeyPackageHash: height >= Globals.PathAuditRulesHeight); // every participant finished with the same key package
            if (ceremonyError != null) return ceremonyError;
            // A public vault's key never needs (nearly) all of its holders: at all of them any one could freeze its
            // Bitcoin for good, and the new key that would replace it must be swept by that same key. The creator chose
            // the threshold and could sell the vault's vBTC before anyone noticed (fourth review). The bound is a
            // re-key's: 67%, never all.
            if (height >= Globals.PathAuditRulesHeight && feature != null && !feature.IsS3C
                && VerifiedXCore.Bitcoin.FROST.FrostDkgAttestation.ParseProof(feature.DKGProof) is { } proof
                && proof.Threshold > VerifiedXCore.Bitcoin.FROST.FrostDkgAttestation.RekeyThreshold(proof.Participants?.Count ?? 0))
                return $"A public vault's key signs with at most {VerifiedXCore.Bitcoin.FROST.FrostDkgAttestation.RekeyThreshold(proof.Participants?.Count ?? 0)} of its {proof.Participants?.Count ?? 0} holders; this one needs {proof.Threshold}.";
            return null;
        }

        // ── A vault's contract reads the same on every node, at every time ───────────────────────────────────────

        /// <summary>The language built-in that reads the node's clock. Evolving NFTs use it; a vault may not.</summary>
        public const string ClockBuiltin = "dateProc";

        /// <summary>
        /// The language built-ins whose result depends on nothing but their arguments. Every other built-in reads or
        /// keeps something outside the code: the clock (dateProc), a random number (rand, and createSig's nonce), the
        /// console (input), or a table that lives as long as the process does (send, whose second call with the same
        /// key throws, so a body using it is read once per process and never again).
        /// </summary>
        public static readonly string[] PureBuiltins = { "print", "getMethods", "getProperties", "getTokenDetails", "getVotingRules", "validateSig" };

        /// <summary>
        /// Whether a contract body's code gives the same result whenever and wherever it runs: it parses, it has no
        /// do-while loop, and every function it calls is one it declares itself or one of
        /// <see cref="PureBuiltins"/>. An allow-list: a built-in this node does not know to be pure is not allowed.
        /// Decided from the text alone, so it is the same on every node at every time. Never throws.
        /// </summary>
        public static bool ContractCodeIsTimeless(string? body) => NotTimelessBecause(body) == null;

        /// <summary>Why a body's code is not timeless; null when it is.</summary>
        public static string? NotTimelessBecause(string? body)
        {
            if (string.IsNullOrEmpty(body)) return "it is empty";
            try
            {
                var text = System.Text.Encoding.Unicode.GetString(SmartContractUtility.Decompress(Convert.FromBase64String(body)));
                if (text.TrimStart().StartsWith("#", StringComparison.Ordinal)) return "it is a console command, not code";
                var tree = global::Trillium.Syntax.SyntaxTree.Parse(text, preventLoopsAndRecursion: true); // as the node parses it
                if (tree.Diagnostics.Length > 0) return "it does not parse";
                var declared = new HashSet<string>(StringComparer.Ordinal);
                var calls = new List<string>();
                var pending = new Stack<global::Trillium.Syntax.SyntaxNode>();
                pending.Push(tree.Root);
                while (pending.Count > 0)
                {
                    var node = pending.Pop();
                    if (node is global::Trillium.Syntax.DoWhileStatementSyntax) return "it has a do-while loop";
                    if (node is global::Trillium.Syntax.FunctionDeclarationSyntax function) declared.Add(function.Identifier.Text);
                    if (node is global::Trillium.Syntax.CallExpressionSyntax call) calls.Add(call.Identifier.Text);
                    foreach (var child in node.GetChildren())
                        pending.Push(child);
                }
                var outside = calls.FirstOrDefault(name => !declared.Contains(name) && !PureBuiltins.Contains(name));
                return outside == null ? null : $"it calls {outside}()";
            }
            catch { return "it cannot be read"; }
        }

        /// <summary>
        /// Whether a decompiled contract is a vault as every node will read it at every time: it carries the
        /// TokenizationV2 feature and no other, and its code is timeless.
        ///
        /// "Is this contract a vault" was answered by running the contract's code and looking for the feature. Contract
        /// code can read the clock (dateProc), and the decompiler hands an evolving contract the node's tip height, so
        /// the same stored code could be a vault on one node and not on another, or become one later: an NFT that
        /// turns into a "vault" naming any funded Bitcoin address after it was minted (no key ceremony was ever
        /// checked), or an S3C vault that stops saying it is one and so may use the bridge (second path audit,
        /// 14 October 2026). With one feature and no outside input the code has nothing to depend on.
        /// </summary>
        public static bool VaultCodeIsPlain(SmartContractMain? decompiled, string? body) =>
            decompiled?.Features != null
            && decompiled.Features.Count(f => f != null) == 1
            && decompiled.Features.Any(f => f != null && f.FeatureName == FeatureName.TokenizationV2)
            && ContractCodeIsTimeless(body);

        /// <summary>The deposit script a vault creation would register, as hex; null when the transaction creates no vault.</summary>
        public static string? CreatedVaultDepositScript(Transaction tx)
        {
            try
            {
                if (CreatedContractUid(tx) == null) return null;
                var payload = SmartContractDeployBinding.ReadPayload(tx.Data);
                if (string.IsNullOrEmpty(payload.Data)) return null;
                if (!ContractCodeIsTimeless(payload.Data)) return null;
                var decompiled = SmartContractMain.GenerateSmartContractInMemory(payload.Data);
                var v2 = decompiled?.Features?.FirstOrDefault(f => f != null && f.FeatureName == FeatureName.TokenizationV2);
                if (v2 == null) return null;
                var feature = v2.FeatureFeatures as TokenizationV2Feature
                    ?? Newtonsoft.Json.JsonConvert.DeserializeObject<TokenizationV2Feature>(v2.FeatureFeatures?.ToString() ?? "");
                return VerifiedXCore.Bitcoin.Attestation.BtcCanonicalWithdrawal.TryDestinationScript(feature?.DepositAddress, out var script)
                    ? script : null;
            }
            catch { return null; }
        }

        // ── NEW-29: contract code must be deterministic and must terminate ──────────────────────────────────────

        /// <summary>The language built-ins whose result differs between nodes (unseeded random; the node's console).</summary>
        public static readonly string[] NondeterministicBuiltins = { "rand", "input" };

        /// <summary>
        /// Two more, refused from Globals.PathAuditRulesHeight: `send` writes a table that lives as long as the node's
        /// process and throws on a second call with the same key (a body using it is read once per process and never
        /// again), and `createSig` draws a random number. Neither is used by any contract the node writes. The
        /// remaining input from outside the code, the clock (dateProc), is answered from the block's timestamp
        /// (<see cref="ContractClock"/>).
        /// </summary>
        public static readonly string[] ProcessBoundBuiltins = { "send", "createSig" };

        /// <summary>
        /// Every node runs contract code to read a contract (VX-01, VX-02, NEW-26, NEW-28, token-deploy apply), so the code
        /// must give every node the same answer and must finish. Trillium's rand() is unseeded and input() reads the node's
        /// console: a body whose minter or features depend on them is accepted by some nodes and refused by others. A
        /// do-while loop is the one unbounded construct the parse-time loop/recursion ban does not catch. A transaction that
        /// carries a contract body using any of these is refused, before anything runs it. Only bodies that parse are
        /// examined, on their syntax tree: a body with a parse error never runs (Compilation.Evaluate stops first), and
        /// words inside a broken multi-line string (e.g. "do" in a description) must not refuse it. No mainnet or testnet
        /// body uses these, so there is no activation height. Null when the rule passes.
        /// </summary>
        public static string? ContractBodyAllowed(Transaction tx, long height = 0)
        {
            var alsoRefused = height >= Globals.PathAuditRulesHeight ? ProcessBoundBuiltins : Array.Empty<string>();
            foreach (var body in CarriedContractBodies(tx?.Data))
            {
                string text;
                try { text = System.Text.Encoding.Unicode.GetString(SmartContractUtility.Decompress(Convert.FromBase64String(body))); }
                catch { continue; } // not a contract body: nothing ever runs it
                var tree = global::Trillium.Syntax.SyntaxTree.Parse(text, preventLoopsAndRecursion: true); // as the node parses it
                if (tree.Diagnostics.Length > 0) continue;
                var pending = new Stack<global::Trillium.Syntax.SyntaxNode>();
                pending.Push(tree.Root);
                while (pending.Count > 0)
                {
                    var node = pending.Pop();
                    if (node is global::Trillium.Syntax.DoWhileStatementSyntax)
                        return "Contract code may not use a do-while loop.";
                    if (node is global::Trillium.Syntax.CallExpressionSyntax call
                        && (NondeterministicBuiltins.Contains(call.Identifier.Text) || alsoRefused.Contains(call.Identifier.Text)))
                        return $"Contract code may not call {call.Identifier.Text}().";
                    foreach (var child in node.GetChildren())
                        pending.Push(child);
                }
            }
            return null;
        }

        /// <summary>Every string "Data" value in the payload (array or object shape) - the contract bodies a transaction carries.</summary>
        private static IEnumerable<string> CarriedContractBodies(string? txData)
        {
            if (string.IsNullOrEmpty(txData) || !txData.Contains("\"Data\"")) yield break;
            JToken root;
            try { root = JToken.Parse(txData); } catch { yield break; }
            var items = root is JArray arr ? arr.ToList() : new List<JToken> { root };
            foreach (var item in items)
                if (item is JObject obj && obj["Data"] is JValue v && v.Type == JTokenType.String && !string.IsNullOrEmpty((string?)v))
                    yield return (string)v!;
        }

        // ── NEW-28: legacy V1 vBTC (arbiter tokenization) is retired ────────────────────────────────────────────

        public const string VbtcV1RetiredMessage = "Legacy V1 vBTC is retired: V1 contracts cannot be created, transferred or withdrawn.";

        /// <summary>The contract functions that exist only for V1 vBTC (its ledger transfers and arbiter withdrawals).</summary>
        public static readonly string[] VbtcV1OnlyFunctions = { "TransferCoin()", "TransferCoinMulti()", "TokenizedWithdrawalRequest()", "TokenizedWithdrawalComplete()" };

        /// <summary>
        /// From Globals.VbtcV1RetirementHeight: no V1 withdrawal transaction, no V1-only function, and nothing that names an
        /// existing V1 contract (ownership transfer, sale, update, evolve, burn, ...). V1 balances stay frozen as recorded.
        /// Keys on the contract, not the transaction type: vBTC V2 uses TKNZ_TX and TKNZ_MINT too. Contract creations are
        /// checked separately (<see cref="VbtcV1Creation"/>) because that decompiles the submitted body.
        /// </summary>
        public static string? VbtcV1Frozen(Transaction tx, long height)
        {
            if (tx == null || height < Globals.VbtcV1RetirementHeight) return null;
            if (tx.TransactionType == TransactionType.TKNZ_WD_ARB || tx.TransactionType == TransactionType.TKNZ_WD_OWNER)
                return VbtcV1RetiredMessage;
            var payload = SmartContractDeployBinding.ReadPayload(tx.Data);
            if (payload.Function != null && VbtcV1OnlyFunctions.Contains(payload.Function))
                return VbtcV1RetiredMessage;
            if (!string.IsNullOrEmpty(payload.ContractUID) && VBTCService.IsVbtcV1Contract(SmartContractStateTrei.GetSmartContractState(payload.ContractUID)))
                return VbtcV1RetiredMessage;
            return null;
        }

        /// <summary>
        /// From Globals.VbtcV1RetirementHeight a Mint() whose body declares the V1 Tokenization feature is refused. Decompiles
        /// the submitted body, so it runs after the signature check (VX-02 follow-up). A body that does not decompile is no V1
        /// contract to any reader, and the V1 functions are refused by name anyway.
        /// </summary>
        public static string? VbtcV1Creation(Transaction tx, long height)
        {
            if (tx == null || height < Globals.VbtcV1RetirementHeight) return null;
            var payload = SmartContractDeployBinding.ReadPayload(tx.Data);
            if (payload.Function != "Mint()" || string.IsNullOrEmpty(payload.Data)) return null;
            // As for a vault: code that is not timeless is never a V1 contract to any reader, and is not run to ask.
            if (height >= Globals.PathAuditRulesHeight && !ContractCodeIsTimeless(payload.Data)) return null;
            try { return VBTCService.DeclaresVbtcV1(VerifiedXCore.Models.SmartContracts.SmartContractMain.GenerateSmartContractInMemory(payload.Data)) ? VbtcV1RetiredMessage : null; }
            catch { return null; }
        }

        // ── NEW-13: a transaction's Height is its block's height ────────────────────────────────────────────────

        // ── NEW-18: an unambiguous hash preimage ────────────────────────────────────────────────────────────────

        /// <summary>
        /// The hash preimage appends UnlockTime directly after Data with no separator (Transaction.GetHashPreimage), so
        /// digits can move between the two with the hash - and the signature - unchanged: Data null + UnlockTime 1790390189
        /// hashes like Data "1" + UnlockTime 790390189. UnlockTime drives a reserve transfer's settlement (and the 24h
        /// rule is not checked during block download), so a relaying peer could change it on syncing nodes. Honest
        /// transactions with an UnlockTime carry no Data or JSON Data; one whose Data ends in a digit is refused, which
        /// leaves exactly one reading of every preimage that has an UnlockTime.
        /// </summary>
        public static string? CanonicalPreimage(Transaction tx)
        {
            if (tx == null) return null;
            // Data | UnlockTime (a trailing '-' could also move and make the UnlockTime negative).
            if (tx.UnlockTime != null && !string.IsNullOrEmpty(tx.Data) && (char.IsDigit(tx.Data[^1]) || tx.Data[^1] == '-'))
                return "A transaction with an UnlockTime may not have Data ending in a digit or '-' (ambiguous hash).";
            // NEW-23: Amount | Fee. A whole-number Amount is written without a decimal point ("10"), so fee digits can move
            // into it: "10" + "0.00000602" re-splits as Amount "100.0000060" + Fee "2" - same hash and signature, ten times
            // the amount. Every such variant leaves a whole-number Fee; honest fees carry decimal places (8 by the fee
            // calculator), so a non-zero Fee without decimal places is refused.
            if (tx.Fee != 0M && ((decimal.GetBits(tx.Fee)[3] >> 16) & 0xFF) == 0)
                return "A non-zero fee must be written with decimal places (ambiguous hash).";
            return null;
        }

        // ── NEW-17: NFT sale amounts ─────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A sale's price (SoldFor at Sale_Start/M_Sale_Start) and every payment its completion applies must be positive:
        /// the completion checks were lower-bound or upper-bound only (or absent), and a negative payment debited the
        /// buyer "negatively" and credited its recipient negatively - minting VFX or draining an arbitrary account.
        /// </summary>
        public static string? SaleAmounts(Transaction tx)
        {
            if (tx?.TransactionType != TransactionType.NFT_SALE || string.IsNullOrEmpty(tx.Data)) return null;
            try
            {
                var jobj = JObject.Parse(tx.Data);
                var function = jobj["Function"]?.ToObject<string?>();
                if (function == "Sale_Start()" || function == "M_Sale_Start()")
                {
                    var soldFor = jobj["SoldFor"]?.ToObject<decimal?>();
                    return soldFor is > 0M ? null : "Sale price must be greater than zero.";
                }
                foreach (var (p, _) in SameBlockDebitGuard.SalePaidTransactions(tx))
                    if (p.Amount <= 0M || p.Fee < 0M)
                        return "Every sale payment must have a positive amount and a non-negative fee.";
            }
            catch { }
            return null;
        }

        // ── Fund-loss audit item 1 (freeze): no transaction from a frozen address ────────────────────────────────

        /// <summary>
        /// From Globals.AddressFreezeHeight a transaction whose sender is in Globals.FrozenAddresses is refused, whatever
        /// its type (an NFT sale's inner payments come from the same sender, so they are covered). Null when it passes.
        /// </summary>
        public static string? FrozenSender(Transaction tx, long height)
        {
            if (tx == null || height < Globals.AddressFreezeHeight || string.IsNullOrEmpty(tx.FromAddress)) return null;
            return Globals.FrozenAddresses.Contains(tx.FromAddress)
                ? $"Transactions from {tx.FromAddress} are frozen."
                : null;
        }

        // ── Fund-loss audit item 10: a reserve cannot send or burn fungible tokens ───────────────────────────────

        /// <summary>
        /// From Globals.ReserveTokenRulesHeight an FTKN_TX TokenTransfer() or TokenBurn() sent from a reserve (xRBX)
        /// address is refused: the transfer was whitelisted for reserves and got a Pending row, but the token apply moved
        /// the balance at once and neither CallBack() nor Recover() restores token accounts, so a stolen vault key emptied
        /// a reserve's tokens with no 24h window and no recovery; TokenMint() and TokenContractOwnerChange() likewise (re-audit). Other token functions (votes) are untouched. Null when
        /// the rule passes.
        /// </summary>
        public static string? ReserveTokenTransfer(Transaction tx, long height)
        {
            // Re-audit (9 Oct 2026): the apply and the validator dispatch TokenTransfer()/TokenBurn() by the Function in Data
            // for NFT_TX, SC_TX, TKNZ_TX and FTKN_TX alike, so the rule judges the function, not the TransactionType; and
            // Data may be the one-element array form the dispatchers also accept.
            if (tx == null || height < Globals.ReserveTokenRulesHeight || tx.TransactionType == TransactionType.TX
                || string.IsNullOrEmpty(tx.FromAddress) || !tx.FromAddress.StartsWith("xRBX") || string.IsNullOrEmpty(tx.Data))
                return null;
            var function = ContractFunctionOf(tx.Data);
            // Re-audit (9 Oct 2026): minting and handing over the token contract are the same kind of irreversible move.
            return function == "TokenTransfer()" || function == "TokenBurn()" || function == "TokenMint()" || function == "TokenContractOwnerChange()"
                || function == "TokenPause()" || function == "TokenBanAddress()"
                ? "A reserve account cannot send, burn or mint fungible tokens, hand over, pause or ban on a token contract: these moves are not deferred, callable back or recoverable."
                : null;
        }

        /// <summary>The contract Function named in a transaction's Data, whether Data is the object form or the one-element array form; null when neither.</summary>
        public static string? ContractFunctionOf(string? data)
        {
            if (string.IsNullOrWhiteSpace(data)) return null;
            try
            {
                var token = JToken.Parse(data);
                if (token is JArray arr) token = arr.Count > 0 ? arr[0] : null;
                return token is JObject obj ? obj["Function"]?.ToObject<string?>() : null;
            }
            catch { return null; }
        }

        // ── NEW-16: a coinbase transaction is a plain reward/fee record ─────────────────────────────────────────

        public static bool IsCoinbase(Transaction tx) => tx?.FromAddress == "Coinbase_BlkRwd" || tx?.FromAddress == "Coinbase_TrxFees";

        /// <summary>
        /// Coinbase transactions skip VerifyTX, the debit guard and every per-type rule (only their count, amount and
        /// recipient are checked), yet StateData.UpdateTreis applied their TransactionType and Data like any other
        /// transaction: a winning producer could make its coinbase an FTKN_TX TokenTransfer() of a victim's tokens to
        /// itself, an NFT Transfer(), a vBTC transfer or a reserve function. Producers only ever build TX with no Data.
        /// NEW-25 (correction): Fee, Nonce and UnlockTime are pinned too (0, 0, none - true of every coinbase on mainnet to
        /// 6,891,066 and testnet to 999,745), so with the reward and recipient checks every value-bearing field of an
        /// ordinary coinbase is fixed without recomputing its hash.
        /// </summary>
        public static string? CoinbaseShape(Transaction tx) =>
            IsCoinbase(tx) && (tx.TransactionType != TransactionType.TX || !string.IsNullOrEmpty(tx.Data)
                               || tx.Fee != 0M || tx.Nonce != 0 || tx.UnlockTime != null)
                ? "A coinbase transaction must be a plain TX with no Data, fee, nonce or unlock time."
                : null;

        /// <summary>
        /// NEW-25: the merkle root is built from each transaction's STORED Hash, and only VerifyTX recomputes it - which
        /// coinbase and genesis transactions never pass through. Their contents were therefore bound to nothing: a peer
        /// serving blocks to a syncing node could change a coinbase's (or genesis transaction's) recipient, amount, type
        /// or Data while keeping its Hash, the merkle root, the block hash and every signature (e.g. the special block at
        /// 3,074,185 crediting 50,000,000 to an attacker). Producers build them with Build(), so the hash recomputes.
        /// NEW-25 (correction): applied to genesis and the special block only. Decimal scale does not survive storage and
        /// relay (Fee 0.00 stored as 0), so mainnet coinbases at 399,792 and 811,860-3,074,180 do not recompute although
        /// their values are right; ordinary blocks pin the coinbase values instead (CoinbaseShape + reward/recipient).
        /// </summary>
        public static string? ContentMatchesHash(Transaction tx) =>
            tx != null && (string.IsNullOrEmpty(tx.Hash) || tx.GetHash() != tx.Hash)
                ? $"Transaction {tx.Hash} content does not match its hash."
                : null;

        /// <summary>
        /// tx.Height is covered by neither the transaction hash nor the block hash, and the apply reads it (vBTC withdrawal
        /// escrow: EscrowAppliesTo(tx.Height), stored RequestBlockHeight). A producer or relaying peer could set an escrowed
        /// request's Height to 1 so it was applied as pre-escrow (no debit; burned later at COMPLETE without a balance
        /// check). Validation and apply therefore use the block's own height for every transaction. The field is
        /// overwritten rather than checked: mainnet history contains non-coinbase transactions with Height 0 (blocks
        /// 2,684,414-2,750,422), and refusing them stopped a sync from genesis (fifth review).
        /// </summary>
        public static void NormalizeTransactionHeights(Block block)
        {
            if (block?.Transactions == null) return;
            foreach (var tx in block.Transactions)
                if (tx != null) tx.Height = block.Height;
        }

        // ── NEW-10: contract UIDs are exact ─────────────────────────────────────────────────────────────────────

        /// <summary>Format of a NEW contract's UID: what every wallet generator produces (lowercase GUID hex, ':', a timestamp).</summary>
        public static readonly System.Text.RegularExpressions.Regex CreationUidFormat =
            new(@"^[0-9a-f]{1,64}:[0-9]{1,20}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        // Case-insensitive: Newtonsoft binds JSON keys to properties case-insensitively, so {"scuid": ...} reaches the SCUID
        // property of an input and must be inspected too (fourth review).
        private static readonly HashSet<string> UidFieldNames = new(StringComparer.OrdinalIgnoreCase) { "ContractUID", "SmartContractUID", "SCUID" };

        /// <summary>
        /// Contract records are looked up through LiteDB's default collation, which is culture-aware and ignores case and
        /// invisible characters: "ABC:1", "abc:1" and "a\u00ADbc:1" all resolve to one record, while every in-memory set,
        /// reserve pending total and duplicate guard compared the raw strings. So (a) a new contract's UID must have the
        /// generator format, which no two distinct values can alias under any collation, and (b) every UID a
        /// transaction names must equal the stored record's UID exactly when such a record exists.
        /// </summary>
        public static string? ContractUids(Transaction tx)
        {
            if (tx == null || string.IsNullOrEmpty(tx.Data)) return null;
            var created = CreatedContractUid(tx);
            if (created != null && !CreationUidFormat.IsMatch(created))
                return $"Contract UID '{created}' must be lowercase hex, ':' and digits.";

            foreach (var uid in ReferencedContractUids(tx))
            {
                if (uid == created) continue;
                var error = InexactReference(uid);
                if (error != null)
                    return error;
            }
            return null;
        }

        /// <summary>The reason <paramref name="uid"/> names a stored contract under a different spelling (case, invisible
        /// characters), or null when it is exact or names no contract.</summary>
        public static string? InexactReference(string uid)
        {
            var record = SmartContractStateTrei.GetSmartContractState(uid);
            return record != null && !string.Equals(record.SmartContractUID, uid, StringComparison.Ordinal)
                ? $"Contract UID '{uid}' does not match the stored contract '{record.SmartContractUID}' exactly."
                : null;
        }

        /// <summary>
        /// Multi-contract inputs must name distinct contracts, judged by the contract each one resolves to rather than
        /// its spelling: the lookup ignores case and invisible characters, so "abc:1" and "ABC:1" are one contract
        /// (MTI#13). Two spellings of one contract always include one that differs from the stored UID, which the
        /// exactness rule above already refuses, so this refuses nothing that rule admits.
        /// </summary>
        public static bool HasDuplicateContract(IEnumerable<string?> uids)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var uid in uids)
            {
                var resolved = string.IsNullOrEmpty(uid) ? "" : SmartContractStateTrei.GetSmartContractState(uid)?.SmartContractUID ?? uid;
                if (!seen.Add(resolved))
                    return true;
            }
            return false;
        }

        /// <summary>Every contract UID named in the transaction data (any depth), plus the debit keys (privacy payloads).</summary>
        public static HashSet<string> ReferencedContractUids(Transaction tx)
        {
            var uids = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                var root = Newtonsoft.Json.Linq.JToken.Parse(tx.Data);
                foreach (var prop in (root as Newtonsoft.Json.Linq.JContainer)?.Descendants().OfType<Newtonsoft.Json.Linq.JProperty>() ?? Enumerable.Empty<Newtonsoft.Json.Linq.JProperty>())
                    if (UidFieldNames.Contains(prop.Name) && prop.Value.Type == Newtonsoft.Json.Linq.JTokenType.String)
                    {
                        var v = (string?)prop.Value;
                        if (!string.IsNullOrEmpty(v)) uids.Add(v);
                    }
            }
            catch { }
            foreach (var (key, _) in SameBlockDebitGuard.GetDebits(tx))
                if (key.Kind != SameBlockDebitGuard.LedgerKind.Native)
                    uids.Add(key.ContractUid);
            return uids;
        }

        /// <summary>NEW-06: registers a block transaction's contract creation; the reason when its UID was already created in this block.</summary>
        public const string SameDepositAddressInBlock = "Another vault created in this block has the same deposit address.";

        public static string? RegisterCreationInBlock(Transaction tx, HashSet<string> createdInBlock, long height = 0)
        {
            var uid = CreatedContractUid(tx);
            if (uid != null && !createdInBlock.Add(uid)) return $"Duplicate creation of contract {uid} within block.";
            // One deposit address, one vault: the creation rule looks at the vaults already in state, so two
            // creations naming one address could share a block; the second would then be a vault the chain does not
            // watch, reading the first one's Bitcoin.
            if (uid != null && height >= Globals.PathAuditRulesHeight && CreatedVaultDepositScript(tx) is { } script && !createdInBlock.Add("script:" + script))
                return SameDepositAddressInBlock;
            return null;
        }

        #region Time in block validation

        /// <summary>
        /// How far a block's timestamp may be from the moment its producer chose its transactions. A producer chooses
        /// by its own clock (admission) and stamps the block a moment later, or one second after the block before it
        /// when that one is ahead of its clock; every time rule is this much easier in a block than at admission, so
        /// that what a producer chose by its clock always stands in its block.
        /// </summary>
        public const int BlockTimeMarginSeconds = 300;

        /// <summary>
        /// The time a block's transactions are judged at. Block validation read the clock of the node doing it: a
        /// transaction at the edge of an hour old, a reserve send at the edge of its 24 hours or a recovery signature at
        /// the edge of its ten minutes was valid on one node and not on the next, and none of it was checked at all by a
        /// node reading the same block a day later. From Globals.PathAuditRulesHeight a block's transactions are judged
        /// by the block's own timestamp, the same on every node whenever it reads the block. Null: the node's clock
        /// (admission to the mempool, and blocks before the height).
        /// </summary>
        public static long? BlockTime(long? blockHeight, long? blockTimestamp)
            => blockHeight != null && blockTimestamp != null && blockHeight.Value >= Globals.PathAuditRulesHeight ? blockTimestamp : null;

        /// <summary>
        /// "Now" for a rule that is easier the earlier now is (an age limit, a deadline, a minimum time ahead): the
        /// block's timestamp less the margin in a block, the clock otherwise.
        /// </summary>
        public static long NowForAgeRules(long? blockTime) => blockTime != null ? blockTime.Value - BlockTimeMarginSeconds : TimeUtil.GetTime();

        /// <summary>"Now" for a rule that is easier the later now is (a wait that must have passed).</summary>
        public static long NowForWaitRules(long? blockTime) => blockTime != null ? blockTime.Value + BlockTimeMarginSeconds : TimeUtil.GetTime();

        /// <summary>A transaction's timestamp against its block's: not older than the age limit, not further ahead than the skew, each with the margin.</summary>
        public static bool TimestampOutsideItsBlock(Transaction tx, long blockTime)
            => blockTime - tx.Timestamp > Globals.MaxTxAgeSeconds + BlockTimeMarginSeconds
            || tx.Timestamp - blockTime > Globals.MaxFutureSkewSeconds + BlockTimeMarginSeconds;

        public const string TimestampOutsideItsBlockMessage = "The timestamp of this transaction is too far from its block's.";

        #endregion
    }
}
