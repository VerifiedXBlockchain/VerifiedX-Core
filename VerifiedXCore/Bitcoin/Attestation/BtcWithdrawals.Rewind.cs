using LiteDB;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Bitcoin.Attestation
{
    /// <summary>
    /// What a deep Bitcoin reorganisation undoes (docs/PLAN-vBTC-Bitcoin-Attestation.md, section 7). Blocks whose
    /// vault events were already applied are no longer part of Bitcoin: everything those events did is taken back,
    /// and the blocks of the branch that replaced them are then reported like any others.
    ///
    /// <list type="bullet">
    /// <item>A coin created in an orphaned block no longer exists. It is marked Orphaned and stops counting. If the
    /// same transaction confirms on the new branch, the coin comes back under the same outpoint.</item>
    /// <item>A coin spent in an orphaned block is unspent again, and held again for its withdrawal if that withdrawal
    /// is still to be paid.</item>
    /// <item>A withdrawal or bridge exit part that an orphaned block completed is back to pinned: the same
    /// transactions, the same coins, its completion and the credit that went with it reversed. When its transaction
    /// confirms on the new branch it is completed again.</item>
    /// <item>A pinned transaction whose coin no longer exists stays pinned. Its signatures may be out; nothing else
    /// is ever signed for that withdrawal, and it confirms when the coin's own transaction is back.</item>
    /// <item>A withdrawal that an orphaned block made void, or showed as released, stays closed. Its requester was
    /// refunded in a VFX block, and VFX blocks are not undone. Its coins are free again.</item>
    /// </list>
    ///
    /// What cannot be taken back is what happened on this chain because of an orphaned deposit: vBTC already sent on
    /// or withdrawn against it. Until that deposit confirms again the vault holds less than its ledger says, and the
    /// owner's figure carries the difference (it can be below zero, and block validation then refuses every debit by
    /// the owner). That is the cost of crediting a deposit that Bitcoin later dropped, and why deposits wait for
    /// <see cref="BtcAttestRules.Depth"/> confirmations.
    /// </summary>
    public static partial class BtcWithdrawals
    {
        /// <summary>
        /// Takes back everything the events of Bitcoin blocks above <paramref name="bitcoinHeight"/> did to coins and
        /// withdrawals. Runs inside block application; a function of state, so every node does the same.
        /// </summary>
        internal static void OnRewind(long bitcoinHeight, Transaction rewind, long vfxHeight, LiteDatabase? db = null)
        {
            // A fault here is logged and passed on: the rewind then counts as not applied on this node and the node is
            // flagged (a rewind taken back half-way is state no other node has). It does not stop the chain.
            try
            {
                RewindStepForTests?.Invoke(vfxHeight);
                TakeBack(bitcoinHeight, rewind, vfxHeight, db);
            }
            catch (Exception ex)
            {
                VerifiedXCore.Data.StateData.NoteWithdrawalStoreStepFailed(vfxHeight, ex); // taking back writes the request rows
                ErrorLogUtility.LogError($"Bitcoin rewind to {bitcoinHeight} ({rewind?.Hash}) failed part-way: {ex}", "BtcWithdrawals.OnRewind()");
                throw;
            }
        }

        internal static Action<long>? RewindStepForTests;

        private static void TakeBack(long bitcoinHeight, Transaction rewind, long vfxHeight, LiteDatabase? db)
        {
            var touched = BtcAttestStore.CoinsTouchedAbove(bitcoinHeight, db).OrderBy(c => c.Outpoint, StringComparer.Ordinal).ToList();
            if (touched.Count == 0) return;

            // Completions that rested on an orphaned block.
            var completed = new SortedDictionary<string, VaultWithdrawalRow>(StringComparer.Ordinal);
            foreach (var coin in touched.Where(c => c.Status == VaultCoinStatus.Spent && c.SpentInHeight > bitcoinHeight && c.PinnedToRef != null))
            {
                foreach (var reference in RefsOn(coin)) // after a takeover the one that was paid need not be the first
                {
                    var row = BtcAttestStore.Withdrawal(reference, db);
                    if (row != null && row.State == VaultWithdrawalState.Settled && row.SettledTxid == coin.SpentByTxid)
                        completed[row.Ref] = row;
                }
            }
            foreach (var row in completed.Values)
                Unsettle(row, bitcoinHeight, rewind, db);

            // Coins.
            var vaults = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var coin in touched)
            {
                var next = Copy(coin);
                var wasSpentAbove = coin.Status == VaultCoinStatus.Spent && coin.SpentInHeight > bitcoinHeight;
                if (wasSpentAbove)
                {
                    next.SpentByTxid = null;
                    next.SpentInHeight = 0;
                }
                var held = SetHolders(next, db); // the withdrawals still to be paid by a transaction that spends it
                if (coin.CreatedInHeight > bitcoinHeight)
                {
                    next.Status = VaultCoinStatus.Orphaned;          // a transaction pinned on it stays pinned on it
                }
                else if (wasSpentAbove)
                {
                    next.Status = held ? VaultCoinStatus.Pinned : VaultCoinStatus.Unspent;
                    if (!held && coin.PinnedToRef != null)
                        ErrorLogUtility.LogError($"Bitcoin rewind to {bitcoinHeight}: coin {coin.Outpoint} of vault {coin.SmartContractUID} was spent by {coin.SpentByTxid} in a block Bitcoin no longer contains, which had closed {string.Join(", ", RefsOn(coin))}. That stays closed; the coin is free again. A transaction once pinned for it can confirm now.",
                            "BtcWithdrawals.OnRewind()");
                }
                else
                {
                    continue; // created and spent at or below the height: untouched (not selected, in fact)
                }
                if (next.Status == coin.Status && next.PinnedToRef == coin.PinnedToRef && next.SpentByTxid == coin.SpentByTxid && next.SpentInHeight == coin.SpentInHeight
                    && RefsOn(next).SequenceEqual(RefsOn(coin), StringComparer.Ordinal))
                    continue;
                BtcAttestStore.SaveCoin(next, coin, db);
                vaults.Add(coin.SmartContractUID);
            }

            SCLogUtility.Log($"Bitcoin rewind to block {bitcoinHeight} ({rewind?.Hash}): {touched.Count} coin(s) of {vaults.Count} vault(s) taken back, {completed.Count} completion(s) reversed.", "BtcWithdrawals.OnRewind()");
            RetryWaiting(vaults, vfxHeight, db);
        }

        /// <summary>A completion that rested on an orphaned block: the withdrawal (or exit part) is to be paid again, by the same transactions.</summary>
        private static void Unsettle(VaultWithdrawalRow row, long bitcoinHeight, Transaction rewind, LiteDatabase? db)
        {
            var paidBy = row.SettledTxid;
            row.State = VaultWithdrawalState.Pinned;
            row.SettledTxid = null;
            row.ClosedAtVfxHeight = 0;
            BtcAttestStore.SaveWithdrawal(row, db);

            if (row.IsBridgeExit)
            {
                BtcAttestStore.AddSettledExit(row.SmartContractUID, -row.AmountSats, db);
                var exit = VBTCBridgeBtcExitState.GetByBurnHash(row.ExitBurnHash ?? "");
                if (exit != null && IsPartOf(row, exit) && exit.IsComplete && !exit.IsFailedRecord)
                {
                    exit.IsComplete = false;
                    exit.BtcTxHash = null;
                    VBTCBridgeBtcExitState.Update(exit);
                }
                // The burn stays consumed: Bitcoin was paid for it once and its exit is open again, so nothing else may use it.
                SCLogUtility.Log($"Bitcoin rewind to block {bitcoinHeight}: the part of bridge exit {row.ExitBurnHash} on vault {row.SmartContractUID} is no longer paid ({paidBy} was in a block Bitcoin dropped); it is pinned again.", "BtcWithdrawals.Unsettle()");
                return;
            }

            if (row.IsSweep)
            {
                // Its fee is no longer paid: the vault holds its old key's coin again, and the sweep is fixed again.
                var paid = row.Pins.FirstOrDefault(p => p.Txid == paidBy);
                if (paid != null) BtcAttestStore.AddSweepFee(row.SmartContractUID, -paid.FeeSats, db);
                SCLogUtility.Log($"Bitcoin rewind to block {bitcoinHeight}: sweep {row.Ref} of vault {row.SmartContractUID} is no longer settled ({paidBy} was in a block Bitcoin dropped); it is pinned again.", "BtcWithdrawals.Unsettle()");
                return;
            }

            var request = VBTCWithdrawalRequest.GetByTransactionHash(row.RequestHash, row.SmartContractUID);
            if (request == null)
                // As in Settle: gone only by a fault of this node's store. OnRewind reports it and passes it on.
                throw new System.IO.IOException($"Withdrawal {row.Ref} was un-completed by a Bitcoin rewind but its request row is missing.");
            request.Status = VBTCWithdrawalStatus.Requested;
            request.IsCompleted = false;
            request.BTCTxHash = null;
            VBTCWithdrawalRequest.Save(request, true);
            VBTCWithdrawalRequest.SaveCompletionRecord(request.TransactionHash, request.SmartContractUID, null, null);
            NoteOnLocalRecordReopened(request);
            SCLogUtility.Log($"Bitcoin rewind to block {bitcoinHeight}: vBTC withdrawal {row.RequestHash} on {row.SmartContractUID} is no longer completed ({paidBy} was in a block Bitcoin dropped); it is pinned again.", "BtcWithdrawals.Unsettle()");
        }

        /// <summary>The wallet's own record of the vault (display only) shows the withdrawal as active again and no longer lists it as paid.</summary>
        private static void NoteOnLocalRecordReopened(VBTCWithdrawalRequest request)
        {
            try
            {
                var local = VBTCContractV2.GetContract(request.SmartContractUID);
                if (local == null) return;
                local.WithdrawalHistory?.RemoveAll(h => h.RequestHash == request.TransactionHash);
                local.WithdrawalStatus = VBTCWithdrawalStatus.Requested;
                local.ActiveWithdrawalRequestHash = request.TransactionHash;
                local.ActiveWithdrawalAmount = request.Amount;
                local.ActiveWithdrawalBTCDestination = request.BTCDestination;
                local.ActiveWithdrawalFeeRate = request.FeeRate;
                local.ActiveWithdrawalRequestTime = request.Timestamp;
                VBTCContractV2.UpdateContract(local);
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"The wallet's record of vault {request.SmartContractUID} could not be updated after withdrawal {request.TransactionHash} was re-opened: {ex.Message}", "BtcWithdrawals.NoteOnLocalRecordReopened()");
            }
        }
    }
}
