using LiteDB;
using Newtonsoft.Json.Linq;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Bitcoin.Attestation
{
    /// <summary>
    /// A withdrawal from a vault the chain watches, from request to Bitcoin
    /// (docs/PLAN-vBTC-Bitcoin-Attestation.md, section 5). Everything here runs inside block application and is worked
    /// out from state, so every node takes each step at the same block. The requester sends one transaction, the
    /// request, and nothing after it.
    ///
    /// <list type="number">
    /// <item><b>Request</b> (unchanged transaction): the amount is debited from the requester at once.</item>
    /// <item><b>Pin</b>: in the same block, the Bitcoin transaction that will pay it is worked out from the vault's
    /// coins (<see cref="BtcCanonicalWithdrawal"/>) and recorded with its coins, which become Pinned. No transaction
    /// and no vote is needed for this: it is a function of state. A request the vault's free coins cannot pay yet
    /// waits, and is tried again whenever the vault's coins change.</item>
    /// <item><b>Sign</b>: the vault's signers sign exactly the pinned transaction (they refuse any other).</item>
    /// <item><b>Settle</b>: when a Bitcoin report shows the pinned transaction, the request is Completed. Nobody sends
    /// a "complete": Bitcoin is the completion.</item>
    /// <item><b>Void</b>: when a report shows a pinned coin spent by some other transaction, the pinned transaction can
    /// never confirm; the request is closed and the requester is refunded.</item>
    /// <item><b>Expiry</b>: a request that was never pinned is closed and refunded after 360 blocks. Safe because no
    /// pin means no signer has signed anything.</item>
    /// <item><b>Cancel</b>: the requester may cancel only while there is no pin, for the same reason; it takes effect
    /// at once, with no vote.</item>
    /// <item><b>Fee bump</b>: the requester of a pinned withdrawal that is not confirming may have it pinned again at a
    /// higher fee rate, on the same coins (BtcWithdrawals.FeeBump.cs).</item>
    /// </list>
    /// </summary>
    public static partial class BtcWithdrawals
    {
        public static string RefOf(string requestHash, string scUID) => $"{requestHash}|{scUID}";

        // ── Rules checked by VerifyTX ───────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Why a withdrawal transaction is refused on a vault the chain watches; null when it is not refused here
        /// (other vaults, other types, or a transaction these rules allow).
        /// </summary>
        public static string? Refusal(Transaction tx)
        {
            try
            {
                switch (tx.TransactionType)
                {
                    case TransactionType.VBTC_V2_WITHDRAWAL_REQUEST:
                    {
                        var watched = VBTCService.GetVbtcV2WithdrawalOutflows(tx).Select(o => o.Item1).Where(u => BtcAttestBalance.IsAttestedVault(Stored(u))).ToList();
                        if (watched.Count == 0) return null;
                        var address = JObject.Parse(tx.Data)["BTCAddress"]?.ToObject<string?>();
                        return BtcCanonicalWithdrawal.TryDestinationScript(address, out _)
                            ? null
                            : $"The withdrawal's Bitcoin address is not an address on this network: {address}";
                    }
                    case TransactionType.VBTC_V2_WITHDRAWAL_COMPLETE:
                        return IsWatched(tx)
                            ? "A withdrawal from this vault is completed by the chain when Bitcoin shows its transaction; no completion transaction is accepted."
                            : null;
                    case TransactionType.VBTC_V2_WITHDRAWAL_VOTE:
                        return IsWatched(tx) ? "Withdrawals from this vault are not cancelled by vote." : null;
                    case TransactionType.VBTC_V2_COIN_PIN:
                        return FeeBumpRefusal(tx, out _, out _, out _);
                    case TransactionType.VBTC_V2_WITHDRAWAL_CANCEL:
                    {
                        if (!IsWatched(tx)) return null;
                        var data = JObject.Parse(tx.Data);
                        var scUID = Stored(data["ContractUID"]?.ToObject<string?>());
                        var hash = data["WithdrawalRequestHash"]?.ToObject<string?>();
                        if (string.IsNullOrEmpty(scUID) || string.IsNullOrEmpty(hash)) return "A cancellation names its contract and its withdrawal request.";
                        var row = BtcAttestStore.Withdrawal(RefOf(hash, scUID));
                        if (row == null) return $"No withdrawal request {hash} is known on contract {scUID}.";
                        if (row.Requester != tx.FromAddress) return "Only the requester can cancel a withdrawal.";
                        return row.State switch
                        {
                            VaultWithdrawalState.Waiting => null,
                            VaultWithdrawalState.Pinned => "This withdrawal's Bitcoin transaction is fixed and may already be signed, so it cannot be cancelled. It completes when Bitcoin confirms it.",
                            _ => $"This withdrawal is already closed ({row.State}).",
                        };
                    }
                    default:
                        return null;
                }
            }
            catch (Exception ex)
            {
                return $"The withdrawal transaction could not be read: {ex.Message}";
            }
        }

        /// <summary>
        /// Whether <paramref name="txid"/> is the Bitcoin transaction fixed in state for this withdrawal: the only one
        /// a signer of the vault may sign for it. The reason when it is not.
        /// </summary>
        public static (bool Ok, string Reason) IsPinnedTransaction(string requestHash, string scUID, string txid) =>
            IsPinnedTransactionOf(RefOf(requestHash, Stored(scUID) ?? scUID), txid);

        /// <summary>As <see cref="IsPinnedTransaction"/>, for one vault's part of a bridge exit.</summary>
        public static (bool Ok, string Reason) IsPinnedExitTransaction(VBTCBridgeBtcExitState exit, string scUID, string txid) =>
            IsPinnedTransactionOf(ExitRefOf(exit, Stored(scUID) ?? scUID), txid);

        private static (bool Ok, string Reason) IsPinnedTransactionOf(string reference, string txid)
        {
            var row = BtcAttestStore.Withdrawal(reference);
            if (row == null) return (false, "This withdrawal is not recorded for the vault in state");
            if (row.State != VaultWithdrawalState.Pinned)
                return (false, $"No Bitcoin transaction is fixed in state for this withdrawal ({row.State}); nothing may be signed for it");
            return row.Pins.Any(p => !p.Dead && string.Equals(p.Txid, txid, StringComparison.OrdinalIgnoreCase))
                ? (true, "")
                : (false, "Only the Bitcoin transaction fixed in state for this withdrawal may be signed");
        }

        /// <summary>Whether a complete, cancel or vote transaction names a vault the chain watches.</summary>
        public static bool IsWatched(Transaction tx)
        {
            if (tx == null || string.IsNullOrEmpty(tx.Data)) return false;
            if (tx.TransactionType != TransactionType.VBTC_V2_WITHDRAWAL_COMPLETE && tx.TransactionType != TransactionType.VBTC_V2_WITHDRAWAL_CANCEL
                && tx.TransactionType != TransactionType.VBTC_V2_WITHDRAWAL_VOTE)
                return false;
            try { return BtcAttestBalance.IsAttestedVault(Stored(JObject.Parse(tx.Data)["ContractUID"]?.ToObject<string?>())); }
            catch { return false; }
        }

        /// <summary>The contract's UID as state holds it (a lookup ignores case; the tables here are keyed by the stored spelling).</summary>
        private static string? Stored(string? scUID) =>
            string.IsNullOrEmpty(scUID) ? scUID : SmartContractStateTrei.GetSmartContractState(scUID)?.SmartContractUID ?? scUID;

        // ── Block application ───────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A withdrawal request on a watched vault has just been applied (its row written, its amount debited): record
        /// it and pin its Bitcoin transaction if the vault's free coins allow.
        /// </summary>
        public static void OnRequestApplied(string? scUID, string requestHash, long vfxHeight, LiteDatabase? db = null)
        {
            try
            {
                scUID = Stored(scUID);
                if (string.IsNullOrEmpty(scUID) || !BtcAttestBalance.IsAttestedVault(scUID, db)) return;
                var request = VBTCWithdrawalRequest.GetByTransactionHash(requestHash, scUID);
                if (request == null)
                {
                    ErrorLogUtility.LogError($"Withdrawal request {requestHash} on {scUID} has no row to pin.", "BtcWithdrawals.OnRequestApplied()");
                    return;
                }
                var reference = RefOf(requestHash, scUID);
                if (BtcAttestStore.Withdrawal(reference, db) != null) return;

                var row = new VaultWithdrawalRow
                {
                    Ref = reference, SmartContractUID = scUID, RequestHash = requestHash, Requester = request.RequestorAddress,
                    AmountSats = (long)(request.Amount * BtcAttestBalance.SatsPerBtc), FeeRate = request.FeeRate,
                    RequestVfxHeight = vfxHeight, State = VaultWithdrawalState.Waiting,
                };
                if (BtcCanonicalWithdrawal.TryDestinationScript(request.BTCDestination, out var script))
                    row.DestinationScriptHex = script;
                else
                    row.WaitingReason = "the destination is not a Bitcoin address on this network";
                BtcAttestStore.SaveWithdrawal(row, db);
                TryPin(row, vfxHeight, db);
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Recording withdrawal {requestHash} on {scUID} failed: {ex}", "BtcWithdrawals.OnRequestApplied()");
            }
        }

        /// <summary>
        /// A Bitcoin report has just changed vault coins. A pinned transaction that Bitcoin now shows settles its
        /// request; a pinned coin spent by anything else kills the transactions that needed it. Then the requests still
        /// waiting on the touched vaults are tried again, oldest first.
        /// </summary>
        public static void OnCoinsChanged(IReadOnlyList<BtcCoinEffect> effects, Transaction report, long vfxHeight, LiteDatabase? db = null)
        {
            if (effects == null || effects.Count == 0) return;
            try
            {
                var vaults = new SortedSet<string>(effects.Select(e => e.SmartContractUID), StringComparer.Ordinal);
                foreach (var spend in effects.Where(e => e.IsSpend))
                {
                    var coin = BtcAttestStore.Coin(spend.Outpoint, db);
                    if (coin?.PinnedToRef == null) continue; // not pinned: the Bitcoin left, nothing was owed for it (plan 5.7)
                    var row = BtcAttestStore.Withdrawal(coin.PinnedToRef, db);
                    if (row == null || row.State != VaultWithdrawalState.Pinned) continue;

                    if (row.Pins.Any(p => !p.Dead && p.Txid == spend.Txid))
                    {
                        Settle(row, spend.Txid, report, vfxHeight, db);
                        continue;
                    }
                    foreach (var pin in row.Pins.Where(p => !p.Dead && p.Inputs.Contains(spend.Outpoint)))
                        pin.Dead = true;
                    if (row.Pins.All(p => p.Dead))
                        Close(row, VaultWithdrawalState.Void, vfxHeight, db,
                            $"its coin {spend.Outpoint} was spent by another Bitcoin transaction ({spend.Txid})");
                    else
                        BtcAttestStore.SaveWithdrawal(row, db);
                }

                foreach (var vault in vaults)
                    foreach (var waiting in BtcAttestStore.WithdrawalsOf(vault, VaultWithdrawalState.Waiting, db)
                                 .OrderBy(w => w.RequestVfxHeight).ThenBy(w => w.Ref, StringComparer.Ordinal))
                        TryPin(waiting, vfxHeight, db);
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Settling withdrawals after Bitcoin report {report?.Hash} failed: {ex}", "BtcWithdrawals.OnCoinsChanged()");
            }
        }

        /// <summary>Before a block's transactions: requests that were never pinned and are past their window are closed and refunded.</summary>
        public static void OnBlockStart(long vfxHeight, LiteDatabase? db = null)
        {
            try
            {
                var exits = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in BtcAttestStore.WaitingWithdrawalsRequestedBefore(vfxHeight - VBTCWithdrawalRequest.EXPIRY_BLOCKS, db)
                             .OrderBy(w => w.RequestVfxHeight).ThenBy(w => w.Ref, StringComparer.Ordinal))
                {
                    if (!row.IsBridgeExit)
                        Close(row, VaultWithdrawalState.Expired, vfxHeight, db, $"it could not be pinned within {VBTCWithdrawalRequest.EXPIRY_BLOCKS} blocks ({row.WaitingReason})");
                    else if (exits.Add($"{row.RequestHash}@{row.RequestVfxHeight}"))
                        ExpireExit(row, vfxHeight, db); // an exit is failed as a whole, once
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Expiring unpinned withdrawals at {vfxHeight} failed: {ex}", "BtcWithdrawals.OnBlockStart()");
            }
        }

        /// <summary>
        /// Applies a cancellation on a watched vault. True when the transaction was one (whether or not it changed
        /// anything), so the vote-based handler leaves it alone.
        /// </summary>
        public static bool ApplyCancel(Transaction tx, long vfxHeight, LiteDatabase? db = null)
        {
            if (!IsWatched(tx)) return false;
            try
            {
                if (Refusal(tx) != null) return true; // block validation refuses it; applied anyway it changes nothing
                var data = JObject.Parse(tx.Data);
                var row = BtcAttestStore.Withdrawal(RefOf(data["WithdrawalRequestHash"]!.ToObject<string>()!, Stored(data["ContractUID"]!.ToObject<string>())!), db);
                if (row != null && row.State == VaultWithdrawalState.Waiting)
                    Close(row, VaultWithdrawalState.Cancelled, vfxHeight, db, "its requester cancelled it before it was pinned");
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Cancelling a withdrawal ({tx.Hash}) failed: {ex}", "BtcWithdrawals.ApplyCancel()");
            }
            return true;
        }

        // ── Steps ───────────────────────────────────────────────────────────────────────────────────────────

        private static bool TryPin(VaultWithdrawalRow row, long vfxHeight, LiteDatabase? db)
        {
            if (row.IsBridgeExit) return TryPinExit(row, vfxHeight, db); // with the exit's other vaults, or not at all
            if (row.State != VaultWithdrawalState.Waiting || string.IsNullOrEmpty(row.DestinationScriptHex)) return false;
            var tx = BtcCanonicalWithdrawal.Build(row.SmartContractUID, row.DestinationScriptHex, row.AmountSats, row.FeeRate, out var reason, db);
            if (tx == null)
            {
                NoteWaiting(row, reason, db);
                return false;
            }
            Pin(row, tx, vfxHeight, db);
            return true;
        }

        private static void NoteWaiting(VaultWithdrawalRow row, string? reason, LiteDatabase? db)
        {
            if (row.WaitingReason == reason) return;
            row.WaitingReason = reason;
            BtcAttestStore.SaveWithdrawal(row, db);
        }

        /// <summary>Records a transaction as the one that pays this row, and holds its coins for it.</summary>
        private static void Pin(VaultWithdrawalRow row, BtcCanonicalTx tx, long vfxHeight, LiteDatabase? db)
        {
            foreach (var outpoint in tx.Inputs)
            {
                var coin = BtcAttestStore.Coin(outpoint, db)!;
                var pinned = Copy(coin);
                pinned.Status = VaultCoinStatus.Pinned;
                pinned.PinnedToRef = row.Ref;
                BtcAttestStore.SaveCoin(pinned, coin, db);
            }
            row.Pins.Add(new VaultPin
            {
                Txid = tx.Txid, TxHex = tx.TxHex, Inputs = tx.Inputs, DestinationSats = tx.DestinationSats, ChangeSats = tx.ChangeSats,
                FeeSats = tx.FeeSats, FeeRate = row.FeeRate, PinnedAtVfxHeight = vfxHeight,
            });
            row.State = VaultWithdrawalState.Pinned;
            row.WaitingReason = null;
            BtcAttestStore.SaveWithdrawal(row, db);
        }

        private static void Settle(VaultWithdrawalRow row, string txid, Transaction report, long vfxHeight, LiteDatabase? db)
        {
            row.State = VaultWithdrawalState.Settled;
            row.SettledTxid = txid;
            row.ClosedAtVfxHeight = vfxHeight;
            BtcAttestStore.SaveWithdrawal(row, db);
            ReleasePinnedCoins(row, db); // those of the request's other transactions; the ones this transaction spent are Spent already
            if (row.IsBridgeExit)
            {
                SettleExitPart(row, txid, report, vfxHeight, db);
                return;
            }

            var request = VBTCWithdrawalRequest.GetByTransactionHash(row.RequestHash, row.SmartContractUID);
            if (request == null)
            {
                ErrorLogUtility.LogError($"Withdrawal {row.Ref} settled by {txid} but its request row is missing.", "BtcWithdrawals.Settle()");
                return;
            }
            // Completed is what the owner's balance adds back (VBTCWithdrawalRequest.GetCompletedWithdrawalAmount): the
            // Bitcoin has left the vault's coins in this same block, so the two always move together.
            request.Status = VBTCWithdrawalStatus.Completed;
            request.IsCompleted = true;
            request.BTCTxHash = txid;
            request.CompletionTxHash = report?.Hash;
            request.CompletionTimestamp = report?.Timestamp;
            VBTCWithdrawalRequest.Save(request, true);
            VBTCWithdrawalRequest.SaveCompletionRecord(request.TransactionHash, request.SmartContractUID, report?.Hash, report?.Timestamp); // the report is its completion
            NoteOnLocalRecord(request, completed: true);
            SCLogUtility.Log($"vBTC withdrawal {row.RequestHash} on {row.SmartContractUID} completed by Bitcoin transaction {txid} (reported in {report?.Hash}).", "BtcWithdrawals.Settle()");
        }

        /// <summary>Closes a request that will never be paid (void, expired, cancelled): its coins are freed and its requester refunded.</summary>
        private static void Close(VaultWithdrawalRow row, VaultWithdrawalState state, long vfxHeight, LiteDatabase? db, string why)
        {
            row.State = state;
            row.ClosedAtVfxHeight = vfxHeight;
            row.WaitingReason = why;
            BtcAttestStore.SaveWithdrawal(row, db);
            ReleasePinnedCoins(row, db);
            if (row.IsBridgeExit)
            {
                VoidExitPart(row, vfxHeight, db, why);
                return;
            }

            var request = VBTCWithdrawalRequest.GetByTransactionHash(row.RequestHash, row.SmartContractUID);
            if (request == null)
            {
                ErrorLogUtility.LogError($"Withdrawal {row.Ref} closed ({state}) but its request row is missing; no refund written.", "BtcWithdrawals.Close()");
                return;
            }
            if (request.IsCompleted) return;
            request.Status = VBTCWithdrawalStatus.Cancelled;
            request.IsCompleted = true;
            VBTCWithdrawalRequest.Save(request, true);
            if (!StateData.RefundVbtcWithdrawalEscrow(row.SmartContractUID, request.RequestorAddress, request.Amount))
                ErrorLogUtility.LogError($"Refund of {request.Amount} to {request.RequestorAddress} on {row.SmartContractUID} could not be written.", "BtcWithdrawals.Close()");
            NoteOnLocalRecord(request, completed: false);
            SCLogUtility.Log($"vBTC withdrawal {row.RequestHash} on {row.SmartContractUID} closed ({state}): {why}. {request.Amount} refunded to {request.RequestorAddress}.", "BtcWithdrawals.Close()");
        }

        /// <summary>
        /// The wallet's own record of a vault (present only on nodes whose wallet holds it; display only, never read by
        /// consensus) stops showing a withdrawal as active when the chain ends it, and lists it once paid: what the
        /// completion transaction's handler does for vaults the chain does not watch.
        /// </summary>
        private static void NoteOnLocalRecord(VBTCWithdrawalRequest request, bool completed)
        {
            try
            {
                var local = VBTCContractV2.GetContract(request.SmartContractUID);
                if (local == null) return;
                if (completed)
                {
                    local.WithdrawalHistory ??= new List<VBTCWithdrawalHistory>();
                    if (!local.WithdrawalHistory.Any(h => h.RequestHash == request.TransactionHash))
                        local.WithdrawalHistory.Add(new VBTCWithdrawalHistory
                        {
                            RequestHash = request.TransactionHash, CompletionHash = request.CompletionTxHash ?? "", BTCTransactionHash = request.BTCTxHash ?? "",
                            Amount = request.Amount, BTCDestination = request.BTCDestination, RequestTime = request.Timestamp,
                            CompletionTime = request.CompletionTimestamp ?? 0, FeeRate = request.FeeRate,
                        });
                }
                if (local.ActiveWithdrawalRequestHash == request.TransactionHash)
                {
                    local.WithdrawalStatus = completed ? VBTCWithdrawalStatus.Completed : VBTCWithdrawalStatus.None;
                    local.ActiveWithdrawalRequestHash = null;
                    local.ActiveWithdrawalAmount = 0;
                    local.ActiveWithdrawalBTCDestination = null;
                    local.ActiveWithdrawalFeeRate = 0;
                    local.ActiveWithdrawalRequestTime = 0;
                }
                VBTCContractV2.UpdateContract(local);
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"The wallet's record of vault {request.SmartContractUID} could not be updated after withdrawal {request.TransactionHash} ended: {ex.Message}", "BtcWithdrawals.NoteOnLocalRecord()");
            }
        }

        private static void ReleasePinnedCoins(VaultWithdrawalRow row, LiteDatabase? db)
        {
            foreach (var outpoint in row.Pins.SelectMany(p => p.Inputs).Distinct(StringComparer.Ordinal))
            {
                var coin = BtcAttestStore.Coin(outpoint, db);
                if (coin == null || coin.Status != VaultCoinStatus.Pinned || coin.PinnedToRef != row.Ref) continue;
                var free = Copy(coin);
                free.Status = VaultCoinStatus.Unspent;
                free.PinnedToRef = null;
                BtcAttestStore.SaveCoin(free, coin, db);
            }
        }

        private static VaultCoinRow Copy(VaultCoinRow c) => new()
        {
            Outpoint = c.Outpoint, SmartContractUID = c.SmartContractUID, ValueSats = c.ValueSats, Status = c.Status, CreatedInHeight = c.CreatedInHeight,
            IsCoinbase = c.IsCoinbase, SpentByTxid = c.SpentByTxid, SpentInHeight = c.SpentInHeight, PinnedToRef = c.PinnedToRef, LastModifiedHeight = c.LastModifiedHeight,
        };
    }
}
