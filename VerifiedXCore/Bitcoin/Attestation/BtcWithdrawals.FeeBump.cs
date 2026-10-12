using LiteDB;
using Newtonsoft.Json.Linq;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Bitcoin.Attestation
{
    /// <summary>
    /// A higher Bitcoin fee for a pinned withdrawal that is not confirming
    /// (docs/PLAN-vBTC-Bitcoin-Attestation.md, 5.2, "Supersession"). The requester sends one transaction,
    /// <see cref="TransactionType.VBTC_V2_COIN_PIN"/>, naming its withdrawal and a higher fee rate. Every node then
    /// works out the replacement the same way it worked out the first transaction, and records it as a second pin.
    ///
    /// <list type="bullet">
    /// <item>The replacement spends exactly the coins the withdrawal already holds. So it and every earlier transaction
    /// of the withdrawal exclude each other on Bitcoin: at most one can ever confirm, and whichever does settles the
    /// withdrawal. No new coin is taken from the vault and none is released.</item>
    /// <item>The higher fee comes out of the withdrawal's own amount, as the first fee did. The requester pays for its
    /// own urgency; the vault and its owner are not charged.</item>
    /// <item>Only the requester can ask, and only for a higher rate than the newest transaction pays, which is what
    /// Bitcoin nodes require before they relay a replacement.</item>
    /// <item>The earlier transactions stay valid to sign and to confirm. Nothing is revoked, because a signature for
    /// one of them may already exist.</item>
    /// </list>
    ///
    /// A bridge exit has no requester and pays a fixed rate; it has no fee bump.
    /// </summary>
    public static partial class BtcWithdrawals
    {
        public const string FeeBumpFunction = "VBTCWithdrawalFeeBump()";

        /// <summary>Transactions one withdrawal may have pinned: the first and seven replacements.</summary>
        public const int MaxPins = 8;

        /// <summary>The withdrawal a fee bump names, as "requestHash|scUID" with the contract as state spells it; null when it does not say.</summary>
        public static string? FeeBumpRef(Transaction tx)
        {
            try
            {
                if (tx?.TransactionType != TransactionType.VBTC_V2_COIN_PIN || string.IsNullOrWhiteSpace(tx.Data)) return null;
                var data = JObject.Parse(tx.Data);
                var scUID = Stored(data["ContractUID"]?.ToObject<string?>());
                var hash = data["WithdrawalRequestHash"]?.ToObject<string?>();
                return string.IsNullOrEmpty(scUID) || string.IsNullOrEmpty(hash) ? null : RefOf(hash, scUID);
            }
            catch { return null; }
        }

        /// <summary>Why a fee bump is refused; null when it is accepted. <paramref name="replacement"/> is the transaction it would pin.</summary>
        private static string? FeeBumpRefusal(Transaction tx, out VaultWithdrawalRow? row, out BtcCanonicalTx? replacement, out long feeRate, LiteDatabase? db = null)
        {
            row = null;
            replacement = null;
            feeRate = 0;
            if (string.IsNullOrWhiteSpace(tx.Data)) return "A fee bump carries data.";
            var data = JObject.Parse(tx.Data);
            if (data["Function"]?.ToObject<string?>() != FeeBumpFunction) return $"A fee bump's function is {FeeBumpFunction}.";
            if (tx.FromAddress != tx.ToAddress) return "A fee bump is sent by the requester to itself.";
            if (tx.Amount != 0M) return "A fee bump carries no amount.";

            var reference = FeeBumpRef(tx);
            if (reference == null) return "A fee bump names its contract and its withdrawal request.";
            if (!BtcAttestBalance.IsAttestedVault(Stored(data["ContractUID"]?.ToObject<string?>()), db))
                return "Only a withdrawal from a vault the chain watches can have its Bitcoin fee raised.";
            var asked = data["FeeRate"]?.ToObject<long?>();
            if (asked is not > 0) return "A fee bump names a fee rate above zero.";
            feeRate = asked.Value;

            row = BtcAttestStore.Withdrawal(reference, db);
            if (row == null || row.IsBridgeExit) return "No such withdrawal request is known on this contract.";
            if (row.Requester != tx.FromAddress) return "Only the requester can raise a withdrawal's Bitcoin fee.";
            switch (row.State)
            {
                case VaultWithdrawalState.Pinned: break;
                case VaultWithdrawalState.Waiting:
                    return "This withdrawal has no Bitcoin transaction yet. Cancel it and request again with the fee rate you want.";
                default:
                    return $"This withdrawal is already closed ({row.State}).";
            }

            var live = row.Pins.Where(p => !p.Dead).ToList();
            if (live.Count == 0) return "This withdrawal has no live Bitcoin transaction.";
            if (row.Pins.Count >= MaxPins) return $"This withdrawal already has {MaxPins} Bitcoin transactions; no more replacements are accepted.";
            var newest = live[^1];
            var paying = newest.FeeRate > 0 ? newest.FeeRate : row.FeeRate;
            if (feeRate <= paying)
                return $"The new fee rate must be above the {paying} sat/vB the withdrawal's newest Bitcoin transaction pays.";

            replacement = BtcCanonicalWithdrawal.BuildReplacement(row.SmartContractUID, row.DestinationScriptHex, row.AmountSats, feeRate, newest.Inputs, row.Ref, out var reason, db);
            if (replacement == null) return $"The replacement cannot be built: {reason}.";
            return null;
        }

        /// <summary>
        /// Applies a mined fee bump: the replacement becomes the withdrawal's newest pin. Checked again here against the
        /// state as it is now, so a bump that an earlier transaction of the same block has overtaken changes nothing.
        /// </summary>
        public static void ApplyFeeBump(Transaction tx, long vfxHeight, LiteDatabase? db = null)
        {
            try
            {
                var refusal = FeeBumpRefusal(tx, out var row, out var replacement, out var feeRate, db);
                if (refusal != null || row == null || replacement == null)
                {
                    ErrorLogUtility.LogError($"Fee bump {tx.Hash} was mined but changed nothing: {refusal}", "BtcWithdrawals.ApplyFeeBump()");
                    return;
                }
                foreach (var earlier in row.Pins.Where(p => !p.Dead))
                    earlier.Superseded = true;
                row.Pins.Add(new VaultPin
                {
                    Txid = replacement.Txid, TxHex = replacement.TxHex, Inputs = replacement.Inputs, DestinationSats = replacement.DestinationSats,
                    ChangeSats = replacement.ChangeSats, FeeSats = replacement.FeeSats, FeeRate = feeRate, PinnedAtVfxHeight = vfxHeight,
                });
                BtcAttestStore.SaveWithdrawal(row, db);
                SCLogUtility.Log($"vBTC withdrawal {row.RequestHash} on {row.SmartContractUID}: Bitcoin fee raised to {feeRate} sat/vB by its requester; replacement {replacement.Txid} pinned ({replacement.FeeSats} sats, paid out of the withdrawal).",
                    "BtcWithdrawals.ApplyFeeBump()");
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Applying fee bump {tx.Hash} failed: {ex}", "BtcWithdrawals.ApplyFeeBump()");
            }
        }
    }
}
