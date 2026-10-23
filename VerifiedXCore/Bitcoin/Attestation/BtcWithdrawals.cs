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
    /// <item><b>Release</b> and <b>takeover</b>: the ways out of a pinned withdrawal that still does not confirm
    /// (BtcWithdrawals.Escape.cs).</item>
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
                        var outflows = VBTCService.GetVbtcV2WithdrawalOutflows(tx).Where(o => BtcAttestBalance.IsAttestedVault(Stored(o.ScUid))).ToList();
                        var watched = outflows.Select(o => o.ScUid).ToList();
                        if (watched.Count == 0) return null;
                        // A request is mined once. Mined again (a producer putting it in a block after the recent-blocks
                        // check has forgotten it, with the nonce rule off), its requester was debited a second time with
                        // no row to pay, expire or cancel, and a settled withdrawal's request was reopened (fourth review,
                        // round 2). A new request has a new hash.
                        foreach (var vault in watched)
                            if (BtcAttestStore.Withdrawal(RefOf(tx.Hash, Stored(vault)!)) != null)
                                return $"Withdrawal request {tx.Hash} on vault {vault} was mined before.";
                        // Nobody is debited for a transaction the vault's key holders can no longer sign (plan 8.8).
                        foreach (var vault in watched)
                            if (BtcVaultKeys.CannotSignRefusal(Stored(vault)) is { } cannotSign)
                                return cannotSign;
                        var data = JObject.Parse(tx.Data);
                        var address = data["BTCAddress"]?.ToObject<string?>();
                        if (!BtcCanonicalWithdrawal.TryDestinationScript(address, out var script))
                            return $"The withdrawal's Bitcoin address is not an address on this network: {address}";
                        // Exactly the address, nothing around it: what is stored is what every reader parses.
                        if (address != address!.Trim())
                            return "The withdrawal's Bitcoin address has spaces around it.";
                        // The chain fixes a transaction that pays any kind of address, so what is left after the fee of
                        // the smallest one must be an output Bitcoin relays. A request below that could never be
                        // pinned: it would only hold its requester's vBTC until its window ran out.
                        var feeRate = data["FeeRate"]?.ToObject<long?>() ?? 0;
                        if (feeRate > BtcCanonicalWithdrawal.MaxFeeRate)
                            return $"A withdrawal's fee rate is at most {BtcCanonicalWithdrawal.MaxFeeRate} sat/vB; this one names {feeRate}.";
                        foreach (var (vault, amount) in outflows)
                        {
                            var sats = (long)(amount * BtcAttestBalance.SatsPerBtc);
                            long fee;
                            try { fee = checked(BtcCanonicalWithdrawal.VSize(1) * feeRate); }
                            catch (OverflowException) { return "The withdrawal's fee rate is out of range."; }
                            if (feeRate > 0 && sats - fee < BtcCanonicalWithdrawal.DustSats)
                                return $"A withdrawal of {sats} sats from vault {vault} at {feeRate} sat/vB leaves less than {BtcCanonicalWithdrawal.DustSats} sats to pay: its Bitcoin transaction costs at least {fee} sats, taken out of the amount. Raise the amount or lower the fee rate.";
                        }
                        // A payment to the vault's own address cannot be told from its change: the vault's signers
                        // would refuse the transaction and the withdrawal would hold its coins without ever being paid.
                        foreach (var vault in watched)
                            if (BtcAttestStore.ScriptRowsForVault(Stored(vault)).Any(k => k.ScriptHex == script))
                                return $"The withdrawal's Bitcoin address is the deposit address of vault {vault}, which it withdraws from. Bitcoin that is to stay in the vault is not withdrawn.";
                        return null;
                    }
                    case TransactionType.VBTC_V2_WITHDRAWAL_COMPLETE:
                        return IsWatched(tx)
                            ? "A withdrawal from this vault is completed by the chain when Bitcoin shows its transaction; no completion transaction is accepted."
                            : null;
                    case TransactionType.VBTC_V2_WITHDRAWAL_VOTE:
                        return IsWatched(tx) ? "Withdrawals from this vault are not cancelled by vote." : null;
                    case TransactionType.VBTC_V2_COIN_PIN:
                        return FeeBumpRefusal(tx, out _, out _, out _, out _);
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
                // Applied, this answer skips the cancel: a fault of this node's storage counts as a lost write to the
                // withdrawal store, so the block is not taken as applied (validation, which only refuses, is unchanged).
                if (StateData.IsNodeLocalFault(ex)) VerifiedXCore.Extensions.DbExtensions.NoteFailure(DbContext.RSRV_VBTC_WITHDRAWAL_REQUESTS);
                return $"The withdrawal transaction could not be read: {ex.Message}";
            }
        }

        /// <summary>
        /// The Bitcoin address rules of a withdrawal request (see <see cref="Refusal"/>), with the same answers, for the
        /// routes that build a request before its requester signs it: an address that is not one on this network, one
        /// with spaces around it, or the deposit address of a watched vault it withdraws from is refused there, so a
        /// signer is never handed a transaction the chain will not accept. The routes apply them to every vault, a
        /// little stricter than the chain, which applies them only to watched vaults: an older vault's withdrawal to
        /// such an address could never be paid either. Null when the address passes.
        /// </summary>
        public static string? DestinationRefusal(string? address, IEnumerable<string?> vaults)
        {
            if (!BtcCanonicalWithdrawal.TryDestinationScript(address, out var script))
                return $"The withdrawal's Bitcoin address is not an address on this network: {address}";
            if (address != address!.Trim())
                return "The withdrawal's Bitcoin address has spaces around it.";
            foreach (var vault in vaults)
                if (BtcAttestBalance.IsAttestedVault(Stored(vault)) && BtcAttestStore.ScriptRowsForVault(Stored(vault)).Any(k => k.ScriptHex == script))
                    return $"The withdrawal's Bitcoin address is the deposit address of vault {vault}, which it withdraws from. Bitcoin that is to stay in the vault is not withdrawn.";
            return null;
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

        /// <summary>
        /// Whether a signer of the vault gives its share for <paramref name="txid"/> now: it is fixed in state for this
        /// withdrawal, and the block that fixed it has <see cref="BtcWithdrawalSigner.SettleBlocks"/> blocks on it.
        /// </summary>
        public static (bool Ok, string Reason) MaySign(string requestHash, string scUID, string txid) =>
            IsPinnedTransactionOf(RefOf(requestHash, Stored(scUID) ?? scUID), txid, Globals.LastBlock?.Height ?? 0);

        /// <summary>As <see cref="MaySign"/>, for one vault's part of a bridge exit.</summary>
        public static (bool Ok, string Reason) MaySignExit(VBTCBridgeBtcExitState exit, string scUID, string txid) =>
            IsPinnedTransactionOf(ExitRefOf(exit, Stored(scUID) ?? scUID), txid, Globals.LastBlock?.Height ?? 0);

        private static (bool Ok, string Reason) IsPinnedTransactionOf(string reference, string txid, long? signingAtHeight = null)
        {
            var row = BtcAttestStore.Withdrawal(reference);
            if (row == null) return (false, "This withdrawal is not recorded for the vault in state");
            if (row.State != VaultWithdrawalState.Pinned)
                return (false, $"No Bitcoin transaction is fixed in state for this withdrawal ({row.State}); nothing may be signed for it");
            var pin = row.Pins.FirstOrDefault(p => !p.Dead && string.Equals(p.Txid, txid, StringComparison.OrdinalIgnoreCase));
            if (pin == null) return (false, "Only the Bitcoin transaction fixed in state for this withdrawal may be signed");
            if (signingAtHeight != null && signingAtHeight.Value < pin.PinnedAtVfxHeight + BtcWithdrawalSigner.SettleBlocks)
                return (false, $"This transaction was fixed at block {pin.PinnedAtVfxHeight}; it is signed from block {pin.PinnedAtVfxHeight + BtcWithdrawalSigner.SettleBlocks}, once the block that fixed it has settled");
            return (true, "");
        }

        /// <summary>
        /// Whether <paramref name="txid"/> is a release fixed for this withdrawal: the one fixed transaction that pays
        /// nobody, every sat of it (less the fee) returning to the vault.
        /// </summary>
        public static bool IsPinnedRelease(string requestHash, string scUID, string txid)
        {
            var row = BtcAttestStore.Withdrawal(RefOf(requestHash, Stored(scUID) ?? scUID));
            return row != null && row.State == VaultWithdrawalState.Pinned
                && row.Pins.Any(p => !p.Dead && p.IsRelease && string.Equals(p.Txid, txid, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// The script a pinned withdrawal pays, as state holds it (lowercase hex); null when there is no such
        /// withdrawal. The vault's signers check a transaction's outputs against this, not against an address parsed
        /// a second time.
        /// </summary>
        public static string? DestinationScriptOf(string requestHash, string scUID) =>
            NullIfEmpty(BtcAttestStore.Withdrawal(RefOf(requestHash, Stored(scUID) ?? scUID))?.DestinationScriptHex);

        /// <summary>As <see cref="DestinationScriptOf"/>, for one vault's part of a bridge exit.</summary>
        public static string? ExitDestinationScriptOf(VBTCBridgeBtcExitState exit, string scUID) =>
            NullIfEmpty(BtcAttestStore.Withdrawal(ExitRefOf(exit, Stored(scUID) ?? scUID))?.DestinationScriptHex);

        private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

        /// <summary>Whether a complete, cancel or vote transaction names a vault the chain watches.</summary>
        public static bool IsWatched(Transaction tx)
        {
            if (tx == null || string.IsNullOrEmpty(tx.Data)) return false;
            if (tx.TransactionType != TransactionType.VBTC_V2_WITHDRAWAL_COMPLETE && tx.TransactionType != TransactionType.VBTC_V2_WITHDRAWAL_CANCEL
                && tx.TransactionType != TransactionType.VBTC_V2_WITHDRAWAL_VOTE)
                return false;
            try { return BtcAttestBalance.IsAttestedVault(Stored(JObject.Parse(tx.Data)["ContractUID"]?.ToObject<string?>())); }
            catch (Exception ex)
            {
                // "Not watched" on a fault of this node's storage sends a block's apply down the other vault kind's path:
                // counted as a lost write to the withdrawal store, so that block is not taken as applied.
                if (StateData.IsNodeLocalFault(ex)) VerifiedXCore.Extensions.DbExtensions.NoteFailure(DbContext.RSRV_VBTC_WITHDRAWAL_REQUESTS);
                return false;
            }
        }

        /// <summary>The contract's UID as state holds it (a lookup ignores case; the tables here are keyed by the stored spelling).</summary>
        private static string? Stored(string? scUID) =>
            string.IsNullOrEmpty(scUID) ? scUID : SmartContractStateTrei.GetSmartContractState(scUID)?.SmartContractUID ?? scUID;

        // ── Block application ───────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A withdrawal request on a watched vault has just been applied (its row written, its amount debited): it is
        /// recorded. Its Bitcoin transaction is fixed once all of the block's transactions are applied
        /// (<see cref="OnBlockTransactionsApplied"/>).
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
            }
            catch (Exception ex)
            {
                VerifiedXCore.Data.StateData.NoteApplyStepFailed(vfxHeight, ex);
                ErrorLogUtility.LogError($"Recording withdrawal {requestHash} on {scUID} failed: {ex}", "BtcWithdrawals.OnRequestApplied()");
            }
        }

        /// <summary>
        /// After a block's transactions: the withdrawals the block requested are fixed on coins, the one paying the
        /// higher Bitcoin fee rate first, then by reference. Each used to be fixed as its transaction was applied, so
        /// when two of a block asked for a vault's last free coin, the one its producer had placed first got it.
        /// The order a producer gives a block's transactions now decides nothing here. No catch around the whole: a
        /// failure counts against the block's application, as at the block's start.
        /// </summary>
        public static void OnBlockTransactionsApplied(long vfxHeight, LiteDatabase? db = null)
        {
            if (!BtcAttestRules.Recording(vfxHeight)) return;
            Exception? failed = null;
            foreach (var requested in InTurn(BtcAttestStore.WaitingWithdrawalsRequestedAt(vfxHeight, db).Where(w => !w.IsBridgeExit)))
            {
                try
                {
                    // Read again: fixing an earlier one of the block may have changed what this one can have.
                    var row = BtcAttestStore.Withdrawal(requested.Ref, db);
                    if (row != null) TryPin(row, vfxHeight, db);
                }
                catch (Exception ex)
                {
                    failed ??= ex;
                    ErrorLogUtility.LogError($"Fixing withdrawal {requested.Ref} on coins at block {vfxHeight} failed: {ex}", "BtcWithdrawals.OnBlockTransactionsApplied()");
                }
            }
            if (failed != null) throw new VerifiedXCore.Data.NodeStateFaultException($"a withdrawal requested in block {vfxHeight} could not be fixed on coins", failed);
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
                    if (coin == null) continue;
                    if (coin.PinnedToRef == null)
                    {
                        // Not pinned: the Bitcoin left, nothing was owed for it (plan 5.7). Unless it is a transaction of a
                        // withdrawal that had stopped holding the coin (#262).
                        ShownWithoutAHolder(coin, spend, report, vfxHeight, db);
                        continue;
                    }

                    // Nearly always one withdrawal holds the coin. After a takeover several do, and Bitcoin has now
                    // confirmed the transaction of one of them (or of none).
                    var answered = false;
                    foreach (var row in HoldersOf(coin, db))
                    {
                        // Two withdrawals to the same address for the same amount, on the same coins at the same fee,
                        // are the same Bitcoin transaction. It paid once: it answers the first, and is a foreign spend
                        // to the other.
                        // A transaction of its own that was voided pays it as well: a Bitcoin reorganisation dropped the spend
                        // that voided it (#262).
                        var shown = answered ? null : row.Pins.FirstOrDefault(p => !p.Dead && p.Txid == spend.Txid)
                            ?? row.Pins.FirstOrDefault(p => p.Dead && !p.IsRelease && p.Txid == spend.Txid);
                        if (shown != null)
                        {
                            answered = true;
                            if (shown.IsRelease) CloseReleased(row, shown, report, vfxHeight, db);
                            else Settle(row, spend.Txid, report, vfxHeight, db);
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
                    if (!answered)
                        ShownWithoutAHolder(coin, spend, report, vfxHeight, db);
                }

                RetryWaiting(vaults, vfxHeight, db);
                // What a replaced key of these vaults now holds free (change of an earlier withdrawal, a late deposit) moves on.
                foreach (var vault in vaults)
                    PlanSweeps(vault, vfxHeight, db);
            }
            catch (Exception ex)
            {
                VerifiedXCore.Data.StateData.NoteWithdrawalStoreStepFailed(vfxHeight, ex); // settling writes the request rows
                ErrorLogUtility.LogError($"Settling withdrawals after Bitcoin report {report?.Hash} failed: {ex}", "BtcWithdrawals.OnCoinsChanged()");
            }
        }

        /// <summary>
        /// Bitcoin shows a transaction of a withdrawal that did not hold the coin it spends: one voided by a spend that a
        /// Bitcoin reorganisation later dropped (#262). A withdrawal still open is paid by it, and settled: its other
        /// transactions' coins are freed, so no validator signs them. One already paid or closed was paid again from the
        /// vault: nothing was owed for it, and the payment is recorded on it (<see cref="VaultWithdrawalRow.UnowedPaymentTxids"/>).
        /// </summary>
        private static void ShownWithoutAHolder(VaultCoinRow coin, BtcCoinEffect spend, Transaction report, long vfxHeight, LiteDatabase? db)
        {
            foreach (var state in Enum.GetValues<VaultWithdrawalState>())
                foreach (var row in BtcAttestStore.WithdrawalsOf(coin.SmartContractUID, state, db).OrderBy(r => r.Ref, StringComparer.Ordinal))
                {
                    var pin = row.Pins.FirstOrDefault(p => !p.IsRelease && p.Txid == spend.Txid && p.Inputs.Contains(spend.Outpoint));
                    if (pin == null || row.SettledTxid == spend.Txid) continue;
                    if (row.State is VaultWithdrawalState.Waiting or VaultWithdrawalState.Pinned)
                    {
                        ErrorLogUtility.LogError($"Withdrawal {row.Ref} on {row.SmartContractUID} is paid by its earlier transaction {spend.Txid}, voided before by a spend Bitcoin no longer contains; its other transactions are given up.",
                            "BtcWithdrawals.ShownWithoutAHolder()");
                        Settle(row, spend.Txid, report, vfxHeight, db);
                        continue;
                    }
                    row.UnowedPaymentTxids ??= new List<string>();
                    if (row.UnowedPaymentTxids.Contains(spend.Txid, StringComparer.Ordinal)) continue;
                    row.UnowedPaymentTxids.Add(spend.Txid);
                    BtcAttestStore.SaveWithdrawal(row, db);
                    ErrorLogUtility.LogError($"UNOWED PAYMENT: vault {row.SmartContractUID} paid withdrawal {row.Ref}{(row.IsBridgeExit ? $" (bridge exit {row.ExitBurnHash})" : "")} again: Bitcoin transaction {spend.Txid} confirmed after it was {row.State}{(row.SettledTxid != null ? $" by {row.SettledTxid}" : "")}. {pin.DestinationSats} sats left the vault for nothing owed.",
                        "BtcWithdrawals.ShownWithoutAHolder()");
                }
        }

        /// <summary>
        /// The requests still waiting on these vaults are tried again: their coins have changed. The one that pays the
        /// higher Bitcoin fee rate first, then the older. A coin that has just come back goes to the withdrawal most
        /// likely to confirm and give it back as change, and somebody who queues requests at a fee that never
        /// confirms does not get every coin a vault receives ahead of everybody else.
        /// </summary>
        private static void RetryWaiting(IEnumerable<string> vaults, long vfxHeight, LiteDatabase? db)
        {
            // The requests of this very block have their turn when all of its transactions are applied.
            foreach (var vault in vaults)
                foreach (var waiting in InTurn(BtcAttestStore.WithdrawalsOf(vault, VaultWithdrawalState.Waiting, db).Where(w => w.IsBridgeExit || w.RequestVfxHeight != vfxHeight)))
                    TryPin(waiting, vfxHeight, db);
        }

        /// <summary>The order waiting withdrawals are tried in: higher fee rate, then older, then by reference.</summary>
        private static IEnumerable<VaultWithdrawalRow> InTurn(IEnumerable<VaultWithdrawalRow> waiting) =>
            waiting.OrderByDescending(w => w.FeeRate).ThenBy(w => w.RequestVfxHeight).ThenBy(w => w.Ref, StringComparer.Ordinal);

        /// <summary>Before a block's transactions: requests that were never pinned and are past their window are closed and refunded.</summary>
        public static void OnBlockStart(long vfxHeight, LiteDatabase? db = null)
        {
            try
            {
                var exits = new HashSet<string>(StringComparer.Ordinal);
                var pinnedByVault = new Dictionary<string, List<VaultWithdrawalRow>>(StringComparer.Ordinal);
                foreach (var row in BtcAttestStore.WaitingWithdrawalsRequestedBefore(vfxHeight - VBTCWithdrawalRequest.EXPIRY_BLOCKS, db)
                             .OrderBy(w => w.RequestVfxHeight).ThenBy(w => w.Ref, StringComparer.Ordinal))
                {
                    if (!row.IsBridgeExit)
                    {
                        // Kept past its window while it is waiting for a withdrawal that pays less to stop moving,
                        // so that it can take that one's coins without having to be requested again.
                        if (!pinnedByVault.TryGetValue(row.SmartContractUID, out var pinned))
                            pinnedByVault[row.SmartContractUID] = pinned = BtcAttestStore.WithdrawalsOf(row.SmartContractUID, VaultWithdrawalState.Pinned, db);
                        if (vfxHeight <= KeptForTakeoverUntil(row, pinned)) continue;
                        Close(row, VaultWithdrawalState.Expired, vfxHeight, db, $"it could not be pinned within {VBTCWithdrawalRequest.EXPIRY_BLOCKS} blocks ({row.WaitingReason})");
                    }
                    else if (exits.Add($"{row.RequestHash}@{row.RequestVfxHeight}"))
                        ExpireExit(row, vfxHeight, db); // an exit is failed as a whole, once
                }
                RetryWaitingForTakeover(vfxHeight, db);
                RaiseStuckExitFees(vfxHeight, db); // and sweeps' (plan 8.8)
                PlanAllSweeps(vfxHeight, db);
            }
            catch (Exception ex)
            {
                // Passed on: what this does is consensus state, and a failure here counts against the block's
                // application (it used to be swallowed, leaving this node with other withdrawals than its peers).
                VerifiedXCore.Data.StateData.NoteWithdrawalStoreStepFailed(vfxHeight, ex); // closing writes the request rows
                ErrorLogUtility.LogError($"Expiring unpinned withdrawals at {vfxHeight} failed: {ex}", "BtcWithdrawals.OnBlockStart()");
                throw;
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
                VerifiedXCore.Data.StateData.NoteWithdrawalStoreStepFailed(vfxHeight, ex); // closing writes the request row
                ErrorLogUtility.LogError($"Cancelling a withdrawal ({tx.Hash}) failed: {ex}", "BtcWithdrawals.ApplyCancel()");
            }
            return true;
        }

        // ── Steps ───────────────────────────────────────────────────────────────────────────────────────────

        private static bool TryPin(VaultWithdrawalRow row, long vfxHeight, LiteDatabase? db)
        {
            if (row.IsBridgeExit) return TryPinExit(row, vfxHeight, db); // with the exit's other vaults, or not at all
            if (row.State != VaultWithdrawalState.Waiting || string.IsNullOrEmpty(row.DestinationScriptHex)) return false;
            // Not fixed on a key that cannot sign any more (plan 8.8): pinned, it could be neither paid nor given up; waiting,
            // it expires and its requester is refunded.
            if (BtcVaultKeys.CannotSignRefusal(row.SmartContractUID, db) is { } cannotSign)
            {
                NoteWaiting(row, cannotSign, db);
                return false;
            }
            row.ShortfallShareSats = ShortfallShare(row.SmartContractUID, row.AmountSats, db);
            var tx = BtcCanonicalWithdrawal.Build(row.SmartContractUID, row.DestinationScriptHex, PayableSats(row), row.FeeRate, out var reason, db);
            if (tx == null)
            {
                // The free coins cannot pay it. Coins held by withdrawals that have stopped moving may (plan 5.5).
                tx = BtcCanonicalWithdrawal.BuildTaking(row.SmartContractUID, row.DestinationScriptHex, PayableSats(row), row.FeeRate, vfxHeight, out var takenFrom, out _, db);
                if (tx == null)
                {
                    NoteWaiting(row, reason, db);
                    return false;
                }
                SCLogUtility.Log($"vBTC withdrawal {row.RequestHash} on {row.SmartContractUID} is fixed on coins held by {string.Join(", ", takenFrom)}, unconfirmed for {BtcAttestRules.StuckPinBlocks} blocks or more: Bitcoin transaction {tx.Txid} at {row.FeeRate} sat/vB. Whichever confirms is paid; the other is refunded.",
                    "BtcWithdrawals.TryPin()");
            }
            Pin(row, tx, vfxHeight, db);
            return true;
        }

        /// <summary>What a withdrawal's or exit part's Bitcoin transaction pays out of the vault: its amount less its share of a sweep shortfall.</summary>
        internal static long PayableSats(VaultWithdrawalRow row) => row.AmountSats - Math.Max(0, row.ShortfallShareSats);

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
                if (coin.Status == VaultCoinStatus.Pinned && coin.PinnedToRef != null && coin.PinnedToRef != row.Ref)
                {
                    // Taken from a withdrawal that stopped moving: both hold it until Bitcoin confirms one of them.
                    pinned.AlsoPinnedTo = (coin.AlsoPinnedTo ?? new List<string>()).Where(r => r != row.Ref).Append(row.Ref).ToList();
                }
                else
                {
                    pinned.Status = VaultCoinStatus.Pinned;
                    pinned.PinnedToRef = row.Ref;
                }
                BtcAttestStore.SaveCoin(pinned, coin, db);
            }
            row.Pins.Add(new VaultPin
            {
                Txid = tx.Txid, TxHex = tx.TxHex, Inputs = tx.Inputs, DestinationSats = tx.DestinationSats, ChangeSats = tx.ChangeSats,
                FeeSats = tx.FeeSats, FeeRate = row.FeeRate, PinnedAtVfxHeight = vfxHeight,
            });
            row.State = VaultWithdrawalState.Pinned;
            row.WaitingReason = null;
            row.ReopenedAtBitcoinHeight = 0; // a part fixed again after its wait (#262)
            BtcAttestStore.SaveWithdrawal(row, db);
        }

        private static void Settle(VaultWithdrawalRow row, string txid, Transaction report, long vfxHeight, LiteDatabase? db)
        {
            row.State = VaultWithdrawalState.Settled;
            row.SettledTxid = txid;
            row.ClosedAtVfxHeight = vfxHeight;
            BtcAttestStore.SaveWithdrawal(row, db);
            ReleasePinnedCoins(row, db); // those of the request's other transactions; the ones this transaction spent are Spent already
            CloseWhatRestedOnItsOtherTransactions(row, txid, vfxHeight, db);
            if (row.IsBridgeExit)
            {
                SettleExitPart(row, txid, report, vfxHeight, db);
                return;
            }
            if (row.IsSweep)
            {
                SettleSweep(row, txid, vfxHeight, db);
                return;
            }

            var request = VBTCWithdrawalRequest.GetByTransactionHash(row.RequestHash, row.SmartContractUID);
            if (request == null)
                // Every withdrawal recorded here was recorded from its request's row (OnRequestApplied), and that row is never
                // deleted: it is gone only by a fault of this node's store. Settled without it, the owner's add-back was
                // missing here alone and this node judged the owner's debits apart from its peers. A storage fault:
                // OnCoinsChanged reports it, the block is not applied and blocks are held until the state is rebuilt.
                throw new System.IO.IOException($"Withdrawal {row.Ref} settled by {txid} but its request row is missing.");
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
            CloseWhatRestedOnItsOtherTransactions(row, null, vfxHeight, db);
            if (row.IsBridgeExit)
            {
                VoidExitPart(row, vfxHeight, db, why);
                return;
            }
            if (row.IsSweep)
            {
                // Nothing was debited for a sweep: its coins are free again and the next sweep takes what is left.
                SCLogUtility.Log($"Vault {row.SmartContractUID}: sweep {row.Ref} closed ({state}): {why}.", "BtcWithdrawals.Close()");
                return;
            }

            var request = VBTCWithdrawalRequest.GetByTransactionHash(row.RequestHash, row.SmartContractUID);
            if (request == null)
            {
                // The row here holds the requester and the amount: the refund does not depend on the other table.
                var owed = row.AmountSats / BtcAttestBalance.SatsPerBtc;
                var written = StateData.RefundVbtcWithdrawalEscrow(row.SmartContractUID, row.Requester, owed);
                ErrorLogUtility.LogError($"Withdrawal {row.Ref} closed ({state}) but its request row is missing; {owed} refunded to {row.Requester} from the attested record ({(written ? "written" : "NOT written")}).", "BtcWithdrawals.Close()");
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
        /// A withdrawal is decided: one of its transactions confirmed, or none can any more. Every other transaction
        /// fixed for it spends the same coins and can now never confirm, so a coin one of them once created (it had
        /// confirmed, in a block a Bitcoin reorganisation then dropped) will never exist again. A withdrawal pinned on
        /// such a coin could never be paid and, being pinned, could never be cancelled or expire; it is closed and
        /// refunded here, and its requester can ask again. Reached only after a deep reorganisation.
        /// </summary>
        private static void CloseWhatRestedOnItsOtherTransactions(VaultWithdrawalRow row, string? confirmedTxid, long vfxHeight, LiteDatabase? db)
        {
            foreach (var lost in row.Pins.Where(p => p.Txid != confirmedTxid))
                for (var vout = 0; vout <= 1; vout++) // a payment's change is output 1; a release's only output is 0
                {
                    var coin = BtcAttestStore.Coin(BtcAttestStore.OutpointKey(lost.Txid, vout), db);
                    if (coin == null || coin.Status != VaultCoinStatus.Orphaned) continue;
                    foreach (var holder in HoldersOf(coin, db))
                    {
                        foreach (var pin in holder.Pins.Where(p => !p.Dead && p.Inputs.Contains(coin.Outpoint)))
                            pin.Dead = true;
                        if (holder.Pins.All(p => p.Dead))
                            Close(holder, VaultWithdrawalState.Void, vfxHeight, db,
                                $"its coin {coin.Outpoint} can never exist: Bitcoin transaction {lost.Txid}, which would create it, lost to {confirmedTxid ?? "another transaction"}");
                        else
                            BtcAttestStore.SaveWithdrawal(holder, db);
                    }
                }
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
                if (coin == null || coin.Status != VaultCoinStatus.Pinned || !RefsOn(coin).Contains(row.Ref, StringComparer.Ordinal)) continue;
                // The row is closed already, so it is no longer among the coin's holders. A coin another withdrawal
                // holds too (a takeover) stays pinned, for that one.
                var next = Copy(coin);
                if (!SetHolders(next, db))
                    next.Status = VaultCoinStatus.Unspent;
                BtcAttestStore.SaveCoin(next, coin, db);
            }
        }

        private static VaultCoinRow Copy(VaultCoinRow c) => new()
        {
            Outpoint = c.Outpoint, SmartContractUID = c.SmartContractUID, ValueSats = c.ValueSats, Status = c.Status, CreatedInHeight = c.CreatedInHeight,
            IsCoinbase = c.IsCoinbase, SpentByTxid = c.SpentByTxid, SpentInHeight = c.SpentInHeight, PinnedToRef = c.PinnedToRef,
            AlsoPinnedTo = c.AlsoPinnedTo?.ToList(), ScriptHex = c.ScriptHex, LastModifiedHeight = c.LastModifiedHeight,
        };
    }
}
