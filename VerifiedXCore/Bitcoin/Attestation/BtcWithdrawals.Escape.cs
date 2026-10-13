using LiteDB;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Bitcoin.Attestation
{
    /// <summary>
    /// The ways out of a pinned withdrawal that is not confirming (docs/PLAN-vBTC-Bitcoin-Attestation.md, 5.5). A
    /// pinned withdrawal is never refunded by time alone: its transaction may be signed, and a signed transaction can
    /// confirm whenever somebody broadcasts it. Every way out here therefore ends on Bitcoin, with a transaction that
    /// spends the same coins, so that at most one of them can ever confirm and the chain follows whichever does.
    ///
    /// <list type="bullet">
    /// <item><b>Fee bump</b> (BtcWithdrawals.FeeBump.cs): the requester has the payment fixed again at a higher fee.</item>
    /// <item><b>Release</b>: the requester gives the withdrawal up. A transaction that sends the withdrawal's coins
    /// back to the vault's own deposit script is fixed next to the payment. When Bitcoin shows it, no payment of the
    /// withdrawal can confirm any more: the withdrawal is closed and its requester has the vBTC back, less the Bitcoin
    /// fee of that transaction, and can request again. If a payment confirms first, the withdrawal is completed as
    /// usual and the release is dead.</item>
    /// <item><b>Takeover</b>: a withdrawal whose newest transaction has sat unconfirmed for
    /// <see cref="BtcAttestRules.StuckPinBlocks"/> blocks no longer keeps its coins to itself. Another withdrawal of
    /// the vault that the free coins cannot pay, and that pays a higher fee rate, is fixed on those coins too. The two
    /// now conflict on Bitcoin: the one that confirms is completed, the other is void and refunded in full. So one
    /// withdrawal with a fee too low to confirm, or one nobody signs, cannot hold a vault's coins for good.</item>
    /// </list>
    ///
    /// None of this needs anyone's judgement. Which transactions are fixed is a function of state and block height,
    /// and which one won is read from a Bitcoin report like every other settlement.
    /// </summary>
    public static partial class BtcWithdrawals
    {
        public const string ReleaseFunction = "VBTCWithdrawalRelease()";

        // ── Who holds a coin ────────────────────────────────────────────────────────────────────────────────

        /// <summary>The references recorded on a coin: the withdrawal it was first pinned for, then those that took it.</summary>
        internal static IEnumerable<string> RefsOn(VaultCoinRow coin)
        {
            if (coin.PinnedToRef != null) yield return coin.PinnedToRef;
            if (coin.AlsoPinnedTo != null)
                foreach (var other in coin.AlsoPinnedTo)
                    if (other != coin.PinnedToRef) yield return other;
        }

        /// <summary>
        /// The withdrawals that hold a coin now: each is still to be paid and has a transaction, not dead, that spends
        /// it. In the order recorded on the coin.
        /// </summary>
        internal static List<VaultWithdrawalRow> HoldersOf(VaultCoinRow coin, LiteDatabase? db)
        {
            var holders = new List<VaultWithdrawalRow>();
            foreach (var reference in RefsOn(coin))
            {
                var row = BtcAttestStore.Withdrawal(reference, db);
                if (row != null && row.State == VaultWithdrawalState.Pinned && row.Pins.Any(p => !p.Dead && p.Inputs.Contains(coin.Outpoint)))
                    holders.Add(row);
            }
            return holders;
        }

        /// <summary>Writes onto a coin who holds it now (first holder, then the rest); false when nobody does.</summary>
        internal static bool SetHolders(VaultCoinRow coin, LiteDatabase? db)
        {
            var holders = HoldersOf(coin, db).Select(h => h.Ref).ToList();
            coin.PinnedToRef = holders.Count > 0 ? holders[0] : null;
            coin.AlsoPinnedTo = holders.Count > 1 ? holders.Skip(1).ToList() : null;
            return holders.Count > 0;
        }

        // ── Takeover ────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Whether a pinned withdrawal has stopped moving, as seen by one that would pay <paramref name="feeRate"/>:
        /// its newest transaction was fixed at least <see cref="BtcAttestRules.StuckPinBlocks"/> blocks ago and pays a
        /// lower fee rate. A bridge exit's part is never taken: an exit is paid whole or not at all, and a part made
        /// void would leave the rest of it paid with nothing to match.
        /// </summary>
        private static bool IsStuck(VaultWithdrawalRow row, long feeRate, long vfxHeight)
        {
            if (row.IsBridgeExit || row.State != VaultWithdrawalState.Pinned) return false;
            var newest = row.Pins.LastOrDefault(p => !p.Dead);
            if (newest == null) return false;
            var pays = newest.FeeRate > 0 ? newest.FeeRate : row.FeeRate;
            return vfxHeight >= newest.PinnedAtVfxHeight + BtcAttestRules.StuckPinBlocks && pays < feeRate;
        }

        /// <summary>
        /// The vault's coins a new withdrawal paying <paramref name="feeRate"/> may be fixed on although they are
        /// held: every withdrawal that holds the coin has stopped moving. With each coin, its holders.
        /// </summary>
        internal static List<(VaultCoinRow Coin, List<VaultWithdrawalRow> Holders)> TakeableCoins(string scUID, long feeRate, long vfxHeight, LiteDatabase? db)
        {
            var result = new List<(VaultCoinRow, List<VaultWithdrawalRow>)>();
            var stuck = BtcAttestStore.WithdrawalsOf(scUID, VaultWithdrawalState.Pinned, db).Where(r => IsStuck(r, feeRate, vfxHeight)).ToList();
            if (stuck.Count == 0) return result;

            var outpoints = new SortedSet<string>(stuck.SelectMany(r => r.Pins.Where(p => !p.Dead).SelectMany(p => p.Inputs)), StringComparer.Ordinal);
            foreach (var outpoint in outpoints)
            {
                var coin = BtcAttestStore.Coin(outpoint, db);
                if (coin == null || coin.Status != VaultCoinStatus.Pinned || coin.SmartContractUID != scUID || coin.ValueSats <= 0) continue;
                var holders = HoldersOf(coin, db);
                if (holders.Count == 0 || holders.Any(h => !IsStuck(h, feeRate, vfxHeight))) continue; // somebody who holds it is still moving
                result.Add((coin, holders));
            }
            return result;
        }

        /// <summary>
        /// Every <see cref="BtcAttestRules.TakeRetryBlocks"/> blocks the withdrawals still waiting for coins are tried
        /// again: nothing else tells them that a withdrawal holding the vault's coins has just stopped moving (time
        /// passing changes no coin). In the order of every retry.
        /// </summary>
        private static void RetryWaitingForTakeover(long vfxHeight, LiteDatabase? db)
        {
            if (vfxHeight % BtcAttestRules.TakeRetryBlocks != 0) return;
            foreach (var waiting in InTurn(BtcAttestStore.WithdrawalsInState(VaultWithdrawalState.Waiting, db).Where(w => !w.IsBridgeExit)))
                TryPin(waiting, vfxHeight, db);
        }

        // ── Release ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Bitcoin shows a withdrawal's release: its coins are back in the vault (as a new coin, less the fee) and no
        /// payment of it can confirm. It is closed; its requester is refunded the amount less that Bitcoin fee, and the
        /// fee is added back to the owner's figure, so the one fee the vault paid is charged to the requester alone.
        /// </summary>
        private static void CloseReleased(VaultWithdrawalRow row, VaultPin release, Transaction report, long vfxHeight, LiteDatabase? db)
        {
            row.State = VaultWithdrawalState.Released;
            row.SettledTxid = release.Txid;
            row.ClosedAtVfxHeight = vfxHeight;
            row.WaitingReason = $"its requester released it: Bitcoin transaction {release.Txid} returned its coins to the vault";
            BtcAttestStore.SaveWithdrawal(row, db);
            ReleasePinnedCoins(row, db);
            CloseWhatRestedOnItsOtherTransactions(row, release.Txid, vfxHeight, db);

            var request = VBTCWithdrawalRequest.GetByTransactionHash(row.RequestHash, row.SmartContractUID);
            if (request != null && request.IsCompleted) return;
            if (request == null)
                ErrorLogUtility.LogError($"Withdrawal {row.Ref} released by {release.Txid} but its request row is missing; refunding from the attested record.", "BtcWithdrawals.CloseReleased()");
            else
            {
                request.Status = VBTCWithdrawalStatus.Cancelled;
                request.IsCompleted = true;
                VBTCWithdrawalRequest.Save(request, true);
            }

            // The row here holds the requester and the amount: the refund does not depend on the other table.
            var feeSats = Math.Min(Math.Max(release.FeeSats, 0), row.AmountSats); // validation holds the fee within the amount
            var refund = (row.AmountSats - feeSats) / BtcAttestBalance.SatsPerBtc;
            BtcAttestStore.AddReleaseFee(row.SmartContractUID, feeSats, db);
            if (refund > 0M && !StateData.RefundVbtcWithdrawalEscrow(row.SmartContractUID, row.Requester, refund))
                ErrorLogUtility.LogError($"Refund of {refund} to {row.Requester} on {row.SmartContractUID} could not be written.", "BtcWithdrawals.CloseReleased()");
            if (request != null) NoteOnLocalRecord(request, completed: false);
            SCLogUtility.Log($"vBTC withdrawal {row.RequestHash} on {row.SmartContractUID} released by Bitcoin transaction {release.Txid} (reported in {report?.Hash}): {refund} refunded to {row.Requester}, {feeSats} sats of Bitcoin fee kept from it.",
                "BtcWithdrawals.CloseReleased()");
        }
    }
}
