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

        /// <summary>NEW-06: registers a block transaction's contract creation; the reason when its UID was already created in this block.</summary>
        public static string? RegisterCreationInBlock(Transaction tx, HashSet<string> createdInBlock)
        {
            var uid = CreatedContractUid(tx);
            return uid != null && !createdInBlock.Add(uid) ? $"Duplicate creation of contract {uid} within block." : null;
        }
    }
}
