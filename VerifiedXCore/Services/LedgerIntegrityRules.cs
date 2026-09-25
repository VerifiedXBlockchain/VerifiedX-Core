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
            return toAccount != null && string.Equals(toAccount.Key, dataFrom, StringComparison.Ordinal)
                ? "Token transfer recipient resolves to the sender's own account."
                : null;
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

        // ── NEW-13: a transaction's Height is its block's height ────────────────────────────────────────────────

        public const string TransactionHeightPrefix = "Transaction height";

        /// <summary>
        /// tx.Height is covered by neither the transaction hash nor the block hash, and the apply reads it (vBTC withdrawal
        /// escrow: EscrowAppliesTo(tx.Height), stored RequestBlockHeight). A producer or relaying peer could set an escrowed
        /// request's Height to 1 so it was applied as pre-escrow (no debit; burned later at COMPLETE without a balance
        /// check). Honest producers set it to the block height (BlockchainData.GiveOtherInfos).
        /// </summary>
        public static string? TransactionHeight(Transaction tx, long blockHeight) =>
            tx != null && tx.Height != blockHeight
                ? $"{TransactionHeightPrefix} {tx.Height} does not match block height {blockHeight}."
                : null;

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
