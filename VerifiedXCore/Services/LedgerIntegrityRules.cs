using Newtonsoft.Json.Linq;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Models;
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
            var bindingError = SmartContractDeployBinding.Validate(payload.Data, uid, tx.FromAddress, isTokenDeploy: payload.Function == "TokenDeploy()", out var decompiled);
            if (bindingError != null) return bindingError;
            // Every reader of a vault's TokenizationV2 data decompiles the body, so a body that does not decompile is never a
            // vault; after activation the vBTC V2 creation type must still carry one that does.
            if (decompiled == null)
                return tx.TransactionType == TransactionType.VBTC_V2_CONTRACT_CREATE ? "vBTC V2 contract body could not be decompiled." : null;
            var v2 = decompiled.Features?.Where(f => f != null && f.FeatureName == FeatureName.TokenizationV2).ToList();
            if (v2 == null || v2.Count == 0) return null;
            if (v2.Count > 1) return "A contract may carry only one TokenizationV2 feature.";
            VerifiedXCore.Models.SmartContracts.TokenizationV2Feature? feature;
            try
            {
                feature = v2[0].FeatureFeatures as VerifiedXCore.Models.SmartContracts.TokenizationV2Feature
                    ?? Newtonsoft.Json.JsonConvert.DeserializeObject<VerifiedXCore.Models.SmartContracts.TokenizationV2Feature>(v2[0].FeatureFeatures?.ToString() ?? "");
            }
            catch { feature = null; }
            return VerifiedXCore.Bitcoin.FROST.FrostDkgAttestation.Validate(feature, uid, tx.FromAddress,
                () => VBTCValidatorRegistry.FundedOnly(VBTCValidatorRegistry.GetActiveValidatorsAt(height - 1))); // NEW-26 (follow-up): funded now
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
                var record = SmartContractStateTrei.GetSmartContractState(uid);
                if (record != null && !string.Equals(record.SmartContractUID, uid, StringComparison.Ordinal))
                    return $"Contract UID '{uid}' does not match the stored contract '{record.SmartContractUID}' exactly.";
            }
            return null;
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
        public static string? RegisterCreationInBlock(Transaction tx, HashSet<string> createdInBlock)
        {
            var uid = CreatedContractUid(tx);
            return uid != null && !createdInBlock.Add(uid) ? $"Duplicate creation of contract {uid} within block." : null;
        }
    }
}
