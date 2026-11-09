using LiteDB;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Bitcoin.Attestation
{
    /// <summary>
    /// Payment attribution, from the VREE height (<see cref="BtcAttestRules.PaysByDestination"/>). A withdrawal or exit part
    /// was paid only as the exact Bitcoin transaction state had fixed for it. After a deep VFX rewind the chain the node is
    /// on has not seen the request, so the transaction its signers already signed (T) is unknown to it; the request mined
    /// again is fixed on whatever coins are free then (T'). When T confirmed it was a foreign spend: the request was voided
    /// and refunded, or left stuck, and the vault had paid for nothing recorded.
    ///
    /// Only the vault's key can spend its coins, so a transaction that spends them and pays an open withdrawal's or exit
    /// part's destination the amount it owes, less the fee the transaction itself paid, was signed by the vault's
    /// threshold for that payment. It settles the payment, as the fixed transaction would have: the coin's holder first,
    /// otherwise the oldest open one on the vault; one transaction pays one. Its transaction goes on the row as a pin and
    /// the coins it spent are written as held by the row, so a Bitcoin rewind that drops its block reopens the payment the
    /// way it reopens any other. A payment nothing was open for is kept (<see cref="VaultPaymentRow"/>) and claimed by the
    /// request or exit part applied later for that destination and amount, instead of fixing a second transaction.
    /// </summary>
    public static partial class BtcWithdrawals
    {
        /// <summary>
        /// The output of a transaction that pays this row, or null. The script is the row's destination; the amount is the
        /// row's less the fee the transaction paid, read from the transaction (every input of a vault-key transaction is a
        /// recorded vault coin), allowing the dust top-up the builder takes out of the payout, and never above the amount.
        /// </summary>
        internal static BtcTxOutput? PaidOutput(VaultWithdrawalRow row, IReadOnlyList<BtcTxOutput>? outputs, long inputSats)
        {
            if (outputs == null || outputs.Count == 0 || row.IsSweep || string.IsNullOrEmpty(row.DestinationScriptHex)) return null;
            var feeSats = inputSats - outputs.Sum(o => o.Sats);
            if (feeSats < 0) return null; // not every input is a coin this layer knows: the fee cannot be read
            foreach (var output in outputs)
            {
                if (!string.Equals(output.ScriptHex, row.DestinationScriptHex, StringComparison.OrdinalIgnoreCase) || output.Sats < BtcCanonicalWithdrawal.DustSats) continue;
                var amount = output.Sats + feeSats;
                if (amount <= row.AmountSats && amount >= PayableSats(row) - BtcCanonicalWithdrawal.DustSats) return output;
            }
            return null;
        }

        /// <summary>Whether a transaction already settled a row of the vault: one transaction pays one, whichever of its coins is looked at.</summary>
        private static bool PaidAlready(string vault, string txid, LiteDatabase? db) =>
            BtcAttestStore.WithdrawalsOf(vault, VaultWithdrawalState.Settled, db).Any(r => r.SettledTxid == txid);

        /// <summary>The spends of one transaction in a report's effects: the vault coins it spent, and what they held.</summary>
        private static (List<string> Inputs, long Sats) SpendsOf(string txid, string vault, IReadOnlyList<BtcCoinEffect> effects)
        {
            var spends = effects.Where(e => e.IsSpend && e.Txid == txid && e.SmartContractUID == vault).ToList();
            return (spends.Select(s => s.Outpoint).Distinct(StringComparer.Ordinal).ToList(), spends.Sum(s => s.Sats));
        }

        /// <summary>
        /// A spend the chain had not fixed, of a vault's coin, that no pinned transaction answers: it pays the open
        /// withdrawal or exit part of the vault it matches, the oldest first, or is kept for one that comes later. True when
        /// it settled one. Nothing happens below the height, or for a spend-only event (no outputs to read).
        /// </summary>
        private static bool AttributeSpend(VaultCoinRow coin, BtcCoinEffect spend, IReadOnlyList<BtcCoinEffect> effects, Transaction report, long vfxHeight, LiteDatabase? db)
        {
            if (!BtcAttestRules.PaysByDestination(vfxHeight) || spend.Outputs == null) return false;
            var vault = coin.SmartContractUID;
            // One transaction pays one: an earlier coin of this transaction settled a row already.
            if (PaidAlready(vault, spend.Txid, db)) return true;
            var (inputs, inputSats) = SpendsOf(spend.Txid, vault, effects);
            var open = BtcAttestStore.WithdrawalsOf(vault, VaultWithdrawalState.Waiting, db).Concat(BtcAttestStore.WithdrawalsOf(vault, VaultWithdrawalState.Pinned, db))
                .OrderBy(r => r.RequestVfxHeight).ThenBy(r => r.Ref, StringComparer.Ordinal);
            foreach (var row in open)
            {
                var paid = PaidOutput(row, spend.Outputs, inputSats);
                if (paid == null) continue;
                SettleByPayment(row, spend.Txid, spend.TxHex ?? "", inputs, inputSats, spend.Outputs, paid, report, vfxHeight, db);
                return true;
            }

            var kept = BtcAttestStore.Payment(spend.Txid, vault, db);
            if (kept != null)
            {
                // Confirmed again after a Bitcoin rewind dropped it: claimable again, as of this confirmation.
                if (!kept.Dropped) return false;
                kept.Dropped = false;
                kept.BitcoinHeight = spend.BitcoinHeight;
                kept.SeenAtVfxHeight = vfxHeight;
                kept.ReportHash = report?.Hash;
                kept.ReportTimestamp = report?.Timestamp ?? 0;
                BtcAttestStore.SavePayment(kept, db);
                return false;
            }
            BtcAttestStore.SavePayment(new VaultPaymentRow
            {
                Txid = spend.Txid, SmartContractUID = vault, TxHex = spend.TxHex ?? "", Inputs = inputs, InputSats = inputSats,
                Outputs = spend.Outputs.Select(o => new BtcTxOutput { ScriptHex = o.ScriptHex, Sats = o.Sats }).ToList(),
                BitcoinHeight = spend.BitcoinHeight, SeenAtVfxHeight = vfxHeight, ReportHash = report?.Hash, ReportTimestamp = report?.Timestamp ?? 0,
            }, db);
            SCLogUtility.Log($"Vault {vault}: Bitcoin transaction {spend.Txid} spent its coins and paid nothing open on it; kept for a request or exit part for what it paid, for {BtcAttestRules.RememberedPaymentBlocks} blocks.", "BtcWithdrawals.AttributeSpend()");
            return false;
        }

        /// <summary>
        /// A waiting row takes the payment a transaction already made for its destination and amount, if the vault has one
        /// kept and still claimable: settled by it, nothing fixed. True when it did.
        /// </summary>
        private static bool ClaimRememberedPayment(VaultWithdrawalRow row, long vfxHeight, LiteDatabase? db)
        {
            if (!BtcAttestRules.PaysByDestination(vfxHeight) || row.State != VaultWithdrawalState.Waiting || row.IsSweep || string.IsNullOrEmpty(row.DestinationScriptHex)) return false;
            foreach (var payment in BtcAttestStore.PaymentsOf(row.SmartContractUID, db)
                         .Where(p => p.ClaimedByRef == null && !p.Dropped && p.SeenAtVfxHeight >= vfxHeight - BtcAttestRules.RememberedPaymentBlocks)
                         .OrderBy(p => p.BitcoinHeight).ThenBy(p => p.Txid, StringComparer.Ordinal))
            {
                var paid = PaidOutput(row, payment.Outputs, payment.InputSats);
                if (paid == null) continue;
                payment.ClaimedByRef = row.Ref;
                BtcAttestStore.SavePayment(payment, db);
                var report = payment.ReportHash == null ? null : new Transaction { Hash = payment.ReportHash, Timestamp = payment.ReportTimestamp };
                SettleByPayment(row, payment.Txid, payment.TxHex, payment.Inputs, payment.InputSats, payment.Outputs, paid, report, vfxHeight, db);
                return true;
            }
            return false;
        }

        /// <summary>
        /// A Bitcoin rewind to <paramref name="bitcoinHeight"/>: a kept payment confirmed above it is no longer on Bitcoin. It is
        /// marked dropped, and claims nothing until it confirms again (<see cref="AttributeSpend"/> then takes it back up).
        /// One claimed already is reopened through its row, as any settlement is.
        /// </summary>
        private static void DropPaymentsAbove(long bitcoinHeight, LiteDatabase? db)
        {
            foreach (var payment in BtcAttestStore.PaymentsConfirmedAbove(bitcoinHeight, db).OrderBy(p => p.Id, StringComparer.Ordinal))
            {
                payment.Dropped = true;
                BtcAttestStore.SavePayment(payment, db);
            }
        }

        /// <summary>
        /// Settles a row by a transaction state had not fixed for it. The transaction goes on the row as a pin and the row is
        /// written as a holder of the coins it spent (Spent already), so that <see cref="TakeBack"/> reopens the row should
        /// Bitcoin drop the transaction's block, exactly as for a fixed transaction.
        /// </summary>
        private static void SettleByPayment(VaultWithdrawalRow row, string txid, string txHex, List<string> inputs, long inputSats, IReadOnlyList<BtcTxOutput> outputs, BtcTxOutput paid,
            Transaction? report, long vfxHeight, LiteDatabase? db)
        {
            var change = outputs.Where(o => o != paid && BtcAttestStore.ScriptRow(o.ScriptHex, db)?.SmartContractUID == row.SmartContractUID).Sum(o => o.Sats);
            row.Pins.Add(new VaultPin
            {
                Txid = txid, TxHex = txHex, Inputs = inputs, DestinationSats = paid.Sats, ChangeSats = change, FeeSats = inputSats - outputs.Sum(o => o.Sats),
                FeeRate = row.FeeRate, PinnedAtVfxHeight = vfxHeight,
            });
            foreach (var pin in row.Pins.Where(p => p.Txid != txid && !p.Dead && !p.Inputs.Any(inputs.Contains)))
                pin.Dead = true; // on other coins: it was never signed for this payment, or it would pay it twice; nobody signs it now
            var wasWaiting = row.State == VaultWithdrawalState.Waiting;
            row.State = VaultWithdrawalState.Pinned;
            BtcAttestStore.SaveWithdrawal(row, db);
            foreach (var outpoint in inputs)
            {
                var coin = BtcAttestStore.Coin(outpoint, db);
                if (coin == null || RefsOn(coin).Contains(row.Ref, StringComparer.Ordinal)) continue;
                var held = Copy(coin);
                if (held.PinnedToRef == null) held.PinnedToRef = row.Ref;
                else held.AlsoPinnedTo = (held.AlsoPinnedTo ?? new List<string>()).Append(row.Ref).ToList();
                BtcAttestStore.SaveCoin(held, coin, db);
            }
            SCLogUtility.Log($"{(row.IsBridgeExit ? $"Bridge exit {row.ExitBurnHash}'s part" : $"vBTC withdrawal {row.RequestHash}")} on {row.SmartContractUID} is paid by Bitcoin transaction {txid}, which the chain had not fixed for it{(wasWaiting ? "" : " (its fixed transaction is given up)")}: {paid.Sats} sats to its destination.",
                "BtcWithdrawals.SettleByPayment()");
            Settle(row, txid, report!, vfxHeight, db);
        }
    }
}
