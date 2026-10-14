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

        /// <summary>Payment transactions one withdrawal may have pinned: the first and seven replacements.</summary>
        public const int MaxPins = 8;

        /// <summary>
        /// Release transactions one withdrawal may have pinned, counted apart from its payments. They used to share the
        /// payments' limit, so a requester who had raised the fee seven times could no longer give the withdrawal up,
        /// and one release at too low a fee could not be followed by another (second path audit, 14 October 2026).
        /// </summary>
        public const int MaxReleasePins = 4;

        /// <summary>Whether the withdrawal may have one more payment transaction pinned.</summary>
        public static bool HasRoomForPayment(VaultWithdrawalRow row) => row.Pins.Count(p => !p.IsRelease) < MaxPins;
        /// <summary>Whether the withdrawal may have one more release transaction pinned.</summary>
        public static bool HasRoomForRelease(VaultWithdrawalRow row) => row.Pins.Count(p => p.IsRelease) < MaxReleasePins;

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

        /// <summary>
        /// Why a fee bump or a release is refused; null when it is accepted. <paramref name="replacement"/> is the
        /// transaction it would pin, and <paramref name="isRelease"/> which of the two it is (one transaction type, two
        /// functions: both fix one more Bitcoin transaction on the withdrawal's own coins, at its requester's wish).
        /// </summary>
        private static string? FeeBumpRefusal(Transaction tx, out VaultWithdrawalRow? row, out BtcCanonicalTx? replacement, out long feeRate, out bool isRelease, LiteDatabase? db = null)
        {
            row = null;
            replacement = null;
            feeRate = 0;
            isRelease = false;
            if (string.IsNullOrWhiteSpace(tx.Data)) return "A fee bump carries data.";
            var data = JObject.Parse(tx.Data);
            var function = data["Function"]?.ToObject<string?>();
            isRelease = function == ReleaseFunction;
            if (function != FeeBumpFunction && !isRelease) return $"This transaction's function is {FeeBumpFunction} or {ReleaseFunction}.";
            if (tx.FromAddress != tx.ToAddress) return "A fee bump is sent by the requester to itself.";
            if (tx.Amount != 0M) return "A fee bump carries no amount.";

            var reference = FeeBumpRef(tx);
            if (reference == null) return "A fee bump names its contract and its withdrawal request.";
            if (!BtcAttestBalance.IsAttestedVault(Stored(data["ContractUID"]?.ToObject<string?>()), db))
                return "Only a withdrawal from a vault the chain watches can have its Bitcoin fee raised.";
            var asked = data["FeeRate"]?.ToObject<long?>();
            if (asked is not > 0) return "A fee bump names a fee rate above zero.";
            feeRate = asked.Value;
            if (feeRate > BtcCanonicalWithdrawal.MaxFeeRate) return $"A withdrawal's fee rate is at most {BtcCanonicalWithdrawal.MaxFeeRate} sat/vB; this names {feeRate}.";

            row = BtcAttestStore.Withdrawal(reference, db);
            if (row == null || row.IsBridgeExit || row.IsSweep) return "No such withdrawal request is known on this contract.";
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
            if (isRelease && !HasRoomForRelease(row)) return $"This withdrawal already has {MaxReleasePins} release transactions; no more are accepted.";
            if (!isRelease && !HasRoomForPayment(row)) return $"This withdrawal already has {MaxPins} Bitcoin transactions; no more replacements are accepted. It can still be released.";
            var newest = live[^1];
            var paying = newest.FeeRate > 0 ? newest.FeeRate : row.FeeRate;
            if (feeRate <= paying)
                return $"The new fee rate must be above the {paying} sat/vB the withdrawal's newest Bitcoin transaction pays.";

            if (isRelease)
            {
                replacement = BtcCanonicalWithdrawal.BuildRelease(row.SmartContractUID, feeRate, newest.Inputs, row.Ref, out var why, db);
                if (replacement == null) return $"The release cannot be built: {why}.";
                // Bitcoin nodes replace a transaction only by one that pays more in all, by at least its own relay cost.
                var atLeast = newest.FeeSats + BtcCanonicalWithdrawal.VSize(replacement.Inputs.Count);
                if (replacement.FeeSats < atLeast)
                    return $"At {feeRate} sat/vB the release pays {replacement.FeeSats} sats; it must pay at least {atLeast} sats to replace the withdrawal's newest Bitcoin transaction. Name a higher fee rate.";
                // Its fee comes out of the refund.
                if (replacement.FeeSats > row.AmountSats)
                    return $"At {feeRate} sat/vB the release costs {replacement.FeeSats} sats of Bitcoin fee, more than the {row.AmountSats} sats the withdrawal is for; the fee is taken out of the refund.";
                return null;
            }

            if (live.Any(p => p.IsRelease))
                return "This withdrawal is being released back to the vault; its payment is not raised any more. A release at a higher fee rate is accepted.";
            replacement = BtcCanonicalWithdrawal.BuildReplacement(row.SmartContractUID, row.DestinationScriptHex, PayableSats(row), feeRate, newest.Inputs, row.Ref, out var reason, db);
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
                var refusal = FeeBumpRefusal(tx, out var row, out var replacement, out var feeRate, out var isRelease, db);
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
                    ChangeSats = replacement.ChangeSats, FeeSats = replacement.FeeSats, FeeRate = feeRate, PinnedAtVfxHeight = vfxHeight, IsRelease = isRelease,
                });
                BtcAttestStore.SaveWithdrawal(row, db);
                SCLogUtility.Log(isRelease
                        ? $"vBTC withdrawal {row.RequestHash} on {row.SmartContractUID}: released by its requester; Bitcoin transaction {replacement.Txid} pinned, returning its coins to the vault at {feeRate} sat/vB ({replacement.FeeSats} sats, taken out of the refund). It is refunded when Bitcoin shows that transaction, or paid if a payment confirms first."
                        : $"vBTC withdrawal {row.RequestHash} on {row.SmartContractUID}: Bitcoin fee raised to {feeRate} sat/vB by its requester; replacement {replacement.Txid} pinned ({replacement.FeeSats} sats, paid out of the withdrawal).",
                    "BtcWithdrawals.ApplyFeeBump()");
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Applying fee bump {tx.Hash} failed: {ex}", "BtcWithdrawals.ApplyFeeBump()");
            }
        }
    }
}
