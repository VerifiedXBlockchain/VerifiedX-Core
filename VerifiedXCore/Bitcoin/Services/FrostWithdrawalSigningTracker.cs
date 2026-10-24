using VerifiedXCore.Bitcoin.Models;
using System.Collections.Concurrent;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Bitcoin.Services
{
    /// <summary>
    /// FIND-028 Fix (redesigned): Tracks this validator's FROST signing participation per withdrawal.
    ///
    /// The invariant protected is "at most ONE Bitcoin transaction per withdrawal request" —
    /// NOT "one ceremony per withdrawal". The original design marked the whole withdrawal
    /// terminal-Signed after the first input's round 2, which (a) made every multi-input
    /// withdrawal fail deterministically on input 1, and (b) made any coordinator-side failure
    /// AFTER share generation (e.g. aggregation) unretryable for 24h — the production
    /// "FROST signing failed for input 0" perma-stuck incident.
    ///
    /// Rules now:
    ///  - State is tracked per (withdrawal, inputIndex); each input's ceremony is independent.
    ///  - Re-signing the IDENTICAL sighash for an input is allowed even after Signed — the same
    ///    sighash can only produce a signature for the same transaction, which Bitcoin dedupes,
    ///    so idempotent re-signs are harmless and make failed ceremonies retryable.
    ///  - Signing a DIFFERENT sighash for an already-signed input is refused (that would be a
    ///    second transaction for the same withdrawal — the actual double-spend attack).
    ///  - The full sighash set announced on the first sign/start is pinned; later inputs must
    ///    match it (a fake "input 1" carrying another transaction's sighash is refused).
    ///  - Contract outpoint pins: once a withdrawal's tx is signed, its input outpoints are pinned
    ///    for the contract. Pins clear when the pinned tx (or a conflict) is observed on-chain —
    ///    see FrostStartup's release check — never by timeout.
    ///    * Before VbtcWithdrawalConcurrencyHeight (one pin per contract): a DIFFERENT withdrawal's
    ///      tx is only signed if it CONFLICTS with the pinned tx, so at most one of them confirms.
    ///      Without escrow that closed the sign-withhold-expire-resign double payout.
    ///    * From VbtcWithdrawalConcurrencyHeight (one pin per withdrawal): every request is escrowed,
    ///      so two withdrawals' transactions confirming is two legitimate payouts. A new tx is signed
    ///      when it spends NONE of another live withdrawal's pinned coins — a conflict would let one
    ///      withdrawal's payout knock out another's after it completed. The one exception is a
    ///      RECLAIMABLE pin (a withheld transaction; FrostStartup decides), whose coins a new tx may
    ///      take back — that coordinator then completes only after its tx confirms.
    ///
    /// This is the primary defense — even if a user modifies their local node code to bypass
    /// client-side checks, validators independently enforce these rules.
    /// </summary>
    public static class FrostWithdrawalSigningTracker
    {
        /// <summary>
        /// Per-withdrawal tracking. Key = "{scUID}:{withdrawalRequestHash}"
        /// </summary>
        private static readonly ConcurrentDictionary<string, WithdrawalRecord> _withdrawals = new();

        /// <summary>
        /// Outstanding-signed-transaction pins: scUID → (withdrawal request hash → pin). Before
        /// VbtcWithdrawalConcurrencyHeight a contract holds at most one pin (the latest signing replaces it).
        /// </summary>
        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ContractOutpointPin>> _contractPins = new();

        /// <summary>
        /// Cooldown period after a failed signing attempt before retry is allowed (seconds).
        /// </summary>
        private const int FAILED_RETRY_COOLDOWN_SECONDS = 60;

        /// <summary>
        /// Staleness threshold for an InProgress input ceremony (seconds).
        /// </summary>
        private const int IN_PROGRESS_STALE_SECONDS = 300;

        /// <summary>
        /// Maximum age of withdrawal records before cleanup (24 hours). Contract pins are NOT
        /// time-expired — a signed, unbroadcast transaction never stops being spendable.
        /// </summary>
        private const int RECORD_EXPIRY_SECONDS = 86400;

        /// <summary>Marker in a refusal caused by a pin; FrostStartup then runs the on-chain release and reclaim checks.</summary>
        public const string PinMarker = "PIN:";

        /// <summary>One outstanding signed transaction per withdrawal (concurrency) vs one per contract (legacy).</summary>
        public static bool ConcurrencyMode => VBTCService.WithdrawalConcurrencyActive(VBTCService.NextBlockHeight);

        /// <summary>
        /// Check whether this validator may participate in a signing ceremony for the given
        /// withdrawal input. Returns (blocked, reason) — if blocked, refuse the sign/start.
        /// </summary>
        /// <param name="scUID">Contract UID.</param>
        /// <param name="withdrawalRequestHash">Withdrawal request TX hash (null/empty = non-withdrawal signing, always allowed).</param>
        /// <param name="inputIndex">Transaction input this ceremony signs.</param>
        /// <param name="messageHash">The BIP341 sighash being signed (empty = legacy caller; falls back to withdrawal-level rules).</param>
        /// <param name="allInputSighashes">The full announced sighash set for the transaction (null = legacy single-input).</param>
        /// <param name="txInputOutpoints">"txid:vout" for every input of the transaction being signed (used by the contract-level conflict rule).</param>
        /// <param name="reclaimablePins">Withdrawal hashes whose pins FrostStartup found reclaimable (concurrency mode only).</param>
        /// <param name="transactionScope">Null on a vault the chain does not watch: one Bitcoin transaction per withdrawal,
        /// this validator's own rule. On a watched vault, the id of the transaction being signed: consensus may have
        /// pinned several for one withdrawal (a fee bump), all on the same coins, so each has a record of its own.</param>
        public static (bool Blocked, string Reason) CheckWithdrawalSigning(
            string scUID,
            string withdrawalRequestHash,
            int inputIndex = 0,
            string? messageHash = null,
            List<string>? allInputSighashes = null,
            List<string>? txInputOutpoints = null,
            ISet<string>? reclaimablePins = null,
            string? transactionScope = null)
        {
            if (string.IsNullOrEmpty(scUID) || string.IsNullOrEmpty(withdrawalRequestHash))
                return (false, string.Empty); // Non-withdrawal signing, allow

            var key = BuildKey(scUID, withdrawalRequestHash, transactionScope);
            var now = TimeUtil.GetTime();

            if (_withdrawals.TryGetValue(key, out var record))
            {
                lock (record.Lock)
                {
                    // Sighash-set pinning: the first start announced which sighashes make up this
                    // withdrawal's transaction. A later start whose set or per-input hash deviates is
                    // trying to smuggle a different transaction in under the same withdrawal.
                    if (record.PinnedSighashes != null && !string.IsNullOrEmpty(messageHash))
                    {
                        if (inputIndex < record.PinnedSighashes.Count &&
                            !string.Equals(record.PinnedSighashes[inputIndex], messageHash, StringComparison.OrdinalIgnoreCase))
                        {
                            return (true, $"Input {inputIndex} sighash does not match the transaction announced for this withdrawal — a withdrawal signs exactly one Bitcoin transaction");
                        }

                        if (inputIndex >= record.PinnedSighashes.Count)
                        {
                            return (true, $"Input index {inputIndex} exceeds the announced input count ({record.PinnedSighashes.Count}) for this withdrawal");
                        }
                    }

                    // A vault the chain watches: this record is for one transaction that consensus fixed, and its
                    // sighash was held to that above. Any number of ceremonies for it can only produce signatures of
                    // that same transaction, so one ceremony in progress, or one that failed, does not shut out the
                    // next. If it did, a single signer of the vault could keep every withdrawal from it unsigned for
                    // good by starting ceremonies and never finishing them (each start held the input for five
                    // minutes), and any signer of a vault may lead on a watched vault.
                    if (record.Inputs.TryGetValue(inputIndex, out var input) && string.IsNullOrEmpty(transactionScope))
                    {
                        switch (input.State)
                        {
                            case SigningState.InProgress:
                                if (now - input.Timestamp > IN_PROGRESS_STALE_SECONDS)
                                {
                                    input.State = SigningState.Failed;
                                    input.Timestamp = now;
                                    break; // Stale — allow retry (fall through to contract pin check)
                                }
                                return (true, $"Signing ceremony already in progress for input {inputIndex} of this withdrawal (session: {input.SessionId})");

                            case SigningState.Signed:
                                // Idempotent re-sign of the SAME transaction is allowed — this is
                                // what makes a coordinator-side failure (aggregation, wallet died)
                                // retryable instead of dead for 24h.
                                if (!string.IsNullOrEmpty(messageHash) &&
                                    string.Equals(input.MessageHash, messageHash, StringComparison.OrdinalIgnoreCase))
                                {
                                    break; // Same sighash — allow
                                }
                                return (true, $"Already signed input {inputIndex} for this withdrawal with a different transaction's sighash. A withdrawal signs exactly one Bitcoin transaction. Session: {input.SessionId}");

                            case SigningState.Failed:
                                if (now - input.Timestamp < FAILED_RETRY_COOLDOWN_SECONDS)
                                {
                                    var remaining = FAILED_RETRY_COOLDOWN_SECONDS - (now - input.Timestamp);
                                    return (true, $"Signing failed recently for input {inputIndex}. Retry allowed in {remaining} seconds.");
                                }
                                break; // Cooldown passed — allow
                        }
                    }
                }
            }

            // A vault the chain watches: which transactions may spend which coins is fixed in consensus state, and
            // FrostSigningAuthorization has already held this one to it. Consensus may fix two withdrawals on the same
            // coin (a withdrawal that stopped moving, and the one that takes its coins): Bitcoin confirms one and the
            // chain refunds the other. This validator's own coin rule below would refuse the second and keep the vault
            // stuck, so it is not applied.
            if (!string.IsNullOrEmpty(transactionScope))
                return (false, string.Empty);

            if (!_contractPins.TryGetValue(scUID, out var pins) || pins.IsEmpty)
                return (false, string.Empty);

            var others = pins.Values
                .Where(p => !string.Equals(p.WithdrawalRequestHash, withdrawalRequestHash, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p => p.Timestamp)
                .ToList();
            if (others.Count == 0)
                return (false, string.Empty);

            if (!ConcurrencyMode)
            {
                // Legacy contract-level double-withdrawal guard: while a DIFFERENT withdrawal's signed tx is
                // outstanding for this contract, only a CONFLICTING tx (sharing >=1 input outpoint) may
                // be signed. Deterministic largest-first coin selection makes legitimate successors
                // conflict naturally; only a disjoint-UTXO second payout is refused.
                var pin = others[0];
                var conflicts = txInputOutpoints != null &&
                                txInputOutpoints.Any(op => pin.Outpoints.Contains(op, StringComparer.OrdinalIgnoreCase));
                if (!conflicts)
                {
                    return (true, $"Contract has an outstanding signed withdrawal tx ({pin.BtcTxId}, withdrawal {pin.WithdrawalRequestHash}). " +
                        $"A new withdrawal tx must spend at least one of its inputs [{string.Join(",", pin.Outpoints)}] so at most one can confirm. " +
                        $"{PinMarker}{pin.BtcTxId}");
                }
                return (false, string.Empty);
            }

            // Concurrency: the tx must not take another live withdrawal's coins. Without its outpoints the
            // overlap cannot be checked (FrostSigningAuthorization derives them, so this is a malformed call).
            if (txInputOutpoints == null || txInputOutpoints.Count == 0)
                return (true, $"Transaction inputs were not announced; cannot check them against the {others.Count} outstanding signed withdrawal tx(s) on this contract. {PinMarker}{others[0].BtcTxId}");

            foreach (var pin in others)
            {
                var overlap = txInputOutpoints.Where(op => pin.Outpoints.Contains(op, StringComparer.OrdinalIgnoreCase)).ToList();
                if (overlap.Count == 0)
                    continue;
                if (reclaimablePins != null && reclaimablePins.Contains(pin.WithdrawalRequestHash))
                    continue; // withheld: its coins may be taken back
                return (true, $"The transaction spends [{string.Join(",", overlap)}], held by the outstanding signed tx {pin.BtcTxId} of withdrawal {pin.WithdrawalRequestHash}. " +
                    $"Build from other coins (see /frost/pins/{scUID}); they free up when that tx confirms. {PinMarker}{pin.BtcTxId}");
            }

            return (false, string.Empty);
        }

        /// <summary>
        /// Other withdrawals' pins on the contract that share a coin with <paramref name="txInputOutpoints"/>
        /// (the candidates FrostStartup evaluates for reclaim).
        /// </summary>
        public static List<ContractPinInfo> GetOverlappingPins(string scUID, string withdrawalRequestHash, IEnumerable<string>? txInputOutpoints)
        {
            if (txInputOutpoints == null)
                return new List<ContractPinInfo>();
            var ops = txInputOutpoints.Select(o => o.ToLowerInvariant()).ToHashSet();
            return GetContractPins(scUID)
                .Where(p => !string.Equals(p.WithdrawalRequestHash, withdrawalRequestHash, StringComparison.OrdinalIgnoreCase))
                .Where(p => p.Outpoints.Any(ops.Contains))
                .ToList();
        }

        /// <summary>
        /// Fund-loss audit item 7: CheckWithdrawalSigning and RecordSigningStarted under ONE acquisition of the
        /// withdrawal's lock. Separately, two sign/starts for one withdrawal input with different sighashes both
        /// passed the check before either was recorded, and both ceremonies then ran: one withdrawal, two payable
        /// Bitcoin transactions. Returns (true, reason) when the start is refused; nothing is recorded then.
        /// </summary>
        public static (bool Blocked, string Reason) TryBeginSigning(
            string scUID,
            string withdrawalRequestHash,
            string sessionId,
            int inputIndex = 0,
            string? messageHash = null,
            List<string>? allInputSighashes = null,
            List<string>? txInputOutpoints = null,
            ISet<string>? reclaimablePins = null,
            string? transactionScope = null,
            string? leaderAddress = null,
            string? btcTxId = null)
        {
            if (string.IsNullOrEmpty(scUID) || string.IsNullOrEmpty(withdrawalRequestHash))
                return (false, string.Empty); // Non-withdrawal signing, allow (nothing to record)

            var key = BuildKey(scUID, withdrawalRequestHash, transactionScope);
            var record = _withdrawals.GetOrAdd(key, _ => new WithdrawalRecord
            {
                ScUID = scUID,
                WithdrawalRequestHash = withdrawalRequestHash
            });
            lock (record.Lock) // re-entered by the two calls below (Monitor is re-entrant on this thread)
            {
                var (blocked, reason) = CheckWithdrawalSigning(scUID, withdrawalRequestHash, inputIndex, messageHash, allInputSighashes, txInputOutpoints, reclaimablePins, transactionScope);
                if (blocked)
                    return (true, reason);
                RecordSigningStarted(scUID, withdrawalRequestHash, sessionId, inputIndex, messageHash, allInputSighashes, transactionScope, leaderAddress, btcTxId);
                return (false, string.Empty);
            }
        }

        /// <summary>
        /// Fund-loss audit item 7 (round 2): whether <paramref name="sessionId"/> is the ceremony this tracker holds
        /// for the withdrawal input, signing the recorded sighash. Round 2 is where the share is produced, and it
        /// re-checked nothing: a session that lost (or bypassed) the start check could still get its share. A
        /// withdrawal input with no record here (expired, reset) is refused too: the coordinator starts again.
        /// </summary>
        public static (bool Ok, string Reason) ConfirmSessionHoldsInput(string? scUID, string? withdrawalRequestHash, int inputIndex, string? sessionId, string? messageHash,
            string? transactionScope = null)
        {
            if (string.IsNullOrEmpty(scUID) || string.IsNullOrEmpty(withdrawalRequestHash))
                return (true, string.Empty); // Non-withdrawal signing
            if (!_withdrawals.TryGetValue(BuildKey(scUID, withdrawalRequestHash, transactionScope), out var record))
                return (false, $"No signing record for withdrawal {withdrawalRequestHash} input {inputIndex} on this validator; start the ceremony again.");
            lock (record.Lock)
            {
                if (!record.Inputs.TryGetValue(inputIndex, out var input))
                    return (false, $"No signing record for withdrawal {withdrawalRequestHash} input {inputIndex} on this validator; start the ceremony again.");
                if (!string.IsNullOrEmpty(messageHash) && !string.IsNullOrEmpty(input.MessageHash)
                    && !string.Equals(input.MessageHash, messageHash, StringComparison.OrdinalIgnoreCase))
                    return (false, $"Session {sessionId} signs a sighash that is not the one recorded for withdrawal {withdrawalRequestHash} input {inputIndex}.");
                if (record.PinnedSighashes != null && !string.IsNullOrEmpty(messageHash)
                    && (inputIndex >= record.PinnedSighashes.Count || !string.Equals(record.PinnedSighashes[inputIndex], messageHash, StringComparison.OrdinalIgnoreCase)))
                    return (false, $"Session {sessionId} signs a sighash that is not the one announced for withdrawal {withdrawalRequestHash} input {inputIndex}.");
                // On a watched vault several ceremonies for the one fixed transaction may be open, one per leader.
                if (input.State == SigningState.InProgress && !string.Equals(input.SessionId, sessionId, StringComparison.Ordinal)
                    && !(input.SessionByLeader?.Values.Contains(sessionId ?? "", StringComparer.Ordinal) == true))
                    return (false, $"Session {sessionId} is not the ceremony this validator accepted for withdrawal {withdrawalRequestHash} input {inputIndex} (session {input.SessionId}).");
                if (input.State == SigningState.Failed)
                    return (false, $"The ceremony this validator accepted for withdrawal {withdrawalRequestHash} input {inputIndex} failed; start again.");
                return (true, string.Empty);
            }
        }

        /// <summary>
        /// Record that a signing ceremony is starting for a withdrawal input.
        /// Called when the validator accepts a /frost/sign/start request. Pins the announced
        /// sighash set on first sight.
        /// </summary>
        public static void RecordSigningStarted(string scUID, string withdrawalRequestHash, string sessionId,
            int inputIndex = 0, string? messageHash = null, List<string>? allInputSighashes = null, string? transactionScope = null, string? leaderAddress = null,
            string? btcTxId = null)
        {
            if (string.IsNullOrEmpty(scUID) || string.IsNullOrEmpty(withdrawalRequestHash))
                return;

            var key = BuildKey(scUID, withdrawalRequestHash, transactionScope);
            var record = _withdrawals.GetOrAdd(key, _ => new WithdrawalRecord
            {
                ScUID = scUID,
                WithdrawalRequestHash = withdrawalRequestHash
            });

            lock (record.Lock)
            {
                record.LastActivity = TimeUtil.GetTime();

                if (record.PinnedSighashes == null && allInputSighashes != null && allInputSighashes.Count > 0)
                    record.PinnedSighashes = allInputSighashes.Select(s => s.ToLowerInvariant()).ToList();
                if (string.IsNullOrEmpty(record.BtcTxId) && !string.IsNullOrWhiteSpace(btcTxId))
                    record.BtcTxId = btcTxId.Trim().ToLowerInvariant();

                if (record.Inputs.TryGetValue(inputIndex, out var existing))
                {
                    // A watched vault: each leader has one open ceremony for this fixed transaction; a new start by the
                    // same leader takes the place of its last, and nobody's start takes the place of another's.
                    if (!string.IsNullOrEmpty(transactionScope))
                        (existing.SessionByLeader ??= new Dictionary<string, string>(StringComparer.Ordinal))[leaderAddress ?? sessionId] = sessionId;

                    // Never downgrade a Signed input; refresh Failed/stale InProgress to InProgress.
                    if (existing.State == SigningState.Signed)
                        return;

                    existing.State = SigningState.InProgress;
                    existing.SessionId = sessionId;
                    existing.MessageHash = messageHash ?? existing.MessageHash;
                    existing.Timestamp = TimeUtil.GetTime();
                }
                else
                {
                    record.Inputs[inputIndex] = new InputRecord
                    {
                        SessionId = sessionId,
                        MessageHash = messageHash ?? string.Empty,
                        State = SigningState.InProgress,
                        Timestamp = TimeUtil.GetTime(),
                        SessionByLeader = string.IsNullOrEmpty(transactionScope) ? null : new Dictionary<string, string>(StringComparer.Ordinal) { [leaderAddress ?? sessionId] = sessionId },
                    };
                }
            }

            LogUtility.Log($"[FROST Dedup] Recorded signing STARTED for withdrawal {withdrawalRequestHash} input {inputIndex} on contract {scUID}, session {sessionId}",
                "FrostWithdrawalSigningTracker.RecordSigningStarted");
        }

        /// <summary>
        /// Record that this validator generated a Round 2 signature share for a withdrawal input.
        /// Also pins the transaction's input outpoints at the CONTRACT level (see the class summary).
        /// </summary>
        public static void RecordSigningCompleted(string scUID, string withdrawalRequestHash, string sessionId,
            int inputIndex = 0, string? messageHash = null, List<string>? txInputOutpoints = null, string? btcTxId = null, string? transactionScope = null)
        {
            if (string.IsNullOrEmpty(scUID) || string.IsNullOrEmpty(withdrawalRequestHash))
                return;

            // Durable evidence (survives restarts and the 24h in-memory expiry): a share for a
            // transaction of this withdrawal left this validator. Best effort; never blocks signing.
            try { FrostSignedWithdrawalEvidence.Record(scUID, withdrawalRequestHash, btcTxId, txInputOutpoints); } catch { }

            var key = BuildKey(scUID, withdrawalRequestHash, transactionScope);
            var record = _withdrawals.GetOrAdd(key, _ => new WithdrawalRecord
            {
                ScUID = scUID,
                WithdrawalRequestHash = withdrawalRequestHash
            });

            lock (record.Lock)
            {
                record.LastActivity = TimeUtil.GetTime();
                if (string.IsNullOrEmpty(record.BtcTxId) && !string.IsNullOrWhiteSpace(btcTxId))
                    record.BtcTxId = btcTxId.Trim().ToLowerInvariant();
                record.Inputs[inputIndex] = new InputRecord
                {
                    SessionId = sessionId,
                    MessageHash = (messageHash ?? string.Empty).ToLowerInvariant(),
                    State = SigningState.Signed,
                    Timestamp = TimeUtil.GetTime()
                };
            }

            // Contract-level pin: my key share is now out for this transaction; until it is observed
            // on-chain (confirmed or conflicted away) the rules in CheckWithdrawalSigning apply.
            // Not for a vault the chain watches (a transaction scope is given): its coins are held in consensus state.
            if (txInputOutpoints != null && txInputOutpoints.Count > 0 && string.IsNullOrEmpty(transactionScope))
            {
                var pin = new ContractOutpointPin
                {
                    ScUID = scUID,
                    WithdrawalRequestHash = withdrawalRequestHash,
                    BtcTxId = btcTxId ?? string.Empty,
                    Outpoints = txInputOutpoints.Select(o => o.ToLowerInvariant()).ToList(),
                    Timestamp = TimeUtil.GetTime()
                };
                if (ConcurrencyMode)
                {
                    PinsFor(scUID)[withdrawalRequestHash] = pin;
                }
                else
                {
                    // Legacy: one pin per contract — the latest signed tx replaces it.
                    var single = NewPinSet();
                    single[withdrawalRequestHash] = pin;
                    _contractPins[scUID] = single;
                }
            }

            LogUtility.Log($"[FROST Dedup] Recorded signing COMPLETED for withdrawal {withdrawalRequestHash} input {inputIndex} on contract {scUID}, session {sessionId}" +
                (txInputOutpoints != null ? $", pinned {txInputOutpoints.Count} outpoint(s)" : ""),
                "FrostWithdrawalSigningTracker.RecordSigningCompleted");
        }

        /// <summary>
        /// Re-creates a pin from durable evidence after a restart (the in-memory pins do not survive one, and a
        /// signed transaction stays broadcastable). Never replaces a live pin. <paramref name="signedAt"/> keeps
        /// the original signing time, which the reclaim grace period is measured from.
        /// </summary>
        public static bool RestorePin(string scUID, string withdrawalRequestHash, string? btcTxId, List<string> outpoints, long signedAt)
        {
            if (string.IsNullOrEmpty(scUID) || string.IsNullOrEmpty(withdrawalRequestHash) || outpoints == null || outpoints.Count == 0)
                return false;
            return PinsFor(scUID).TryAdd(withdrawalRequestHash, new ContractOutpointPin
            {
                ScUID = scUID,
                WithdrawalRequestHash = withdrawalRequestHash,
                BtcTxId = btcTxId ?? string.Empty,
                Outpoints = outpoints.Select(o => o.ToLowerInvariant()).ToList(),
                Timestamp = signedAt > 0 ? signedAt : TimeUtil.GetTime()
            });
        }

        /// <summary>
        /// The withdrawal's signed tx is provably dead (an input spent by a confirmed tx — FrostStartup checks):
        /// drop its in-memory signing record and pin so a replacement tx for the same withdrawal can be signed now,
        /// not after the 24h record expiry.
        /// </summary>
        public static void ForgetDeadTransaction(string scUID, string withdrawalRequestHash, string reason)
        {
            if (string.IsNullOrEmpty(scUID) || string.IsNullOrEmpty(withdrawalRequestHash))
                return;
            _withdrawals.TryRemove(BuildKey(scUID, withdrawalRequestHash), out _);
            ClearContractPin(scUID, withdrawalRequestHash, reason);
        }

        /// <summary>
        /// One signed transaction for the withdrawal, <paramref name="btcTxId"/>, can never confirm: its pin goes (only that
        /// transaction's), and the in-memory signing record too unless <paramref name="keepSigningRecord"/> (another
        /// transaction signed for the withdrawal is still held, and the record does not say which one it belongs to).
        /// </summary>
        public static void ForgetDeadTransaction(string scUID, string withdrawalRequestHash, string btcTxId, bool keepSigningRecord, string reason)
        {
            if (string.IsNullOrEmpty(scUID) || string.IsNullOrEmpty(withdrawalRequestHash))
                return;
            // The record goes only if it is that transaction's: a ceremony for another transaction may have started
            // under the same reference while the dead one was being looked up.
            // Only a record naming that transaction: one a start has just created names its own a moment later, and every
            // start names one (a record with none was only a failed attempt).
            if (!keepSigningRecord && _withdrawals.TryGetValue(BuildKey(scUID, withdrawalRequestHash), out var record))
                lock (record.Lock)
                    if (!string.IsNullOrEmpty(btcTxId) && string.Equals(record.BtcTxId, btcTxId, StringComparison.OrdinalIgnoreCase))
                        _withdrawals.TryRemove(new KeyValuePair<string, WithdrawalRecord>(BuildKey(scUID, withdrawalRequestHash), record));
            if (_contractPins.TryGetValue(scUID, out var pins) && pins.TryGetValue(withdrawalRequestHash, out var pin)
                && (string.IsNullOrEmpty(pin.BtcTxId) || string.Equals(pin.BtcTxId, btcTxId, StringComparison.OrdinalIgnoreCase)))
                ClearContractPin(scUID, withdrawalRequestHash, reason);
        }

        /// <summary>
        /// Record that the signing ceremony failed for a withdrawal input. Allows retry after the
        /// cooldown. Never downgrades a Signed input.
        /// </summary>
        public static void RecordSigningFailed(string scUID, string withdrawalRequestHash, string sessionId, int inputIndex = 0, string? transactionScope = null)
        {
            if (string.IsNullOrEmpty(scUID) || string.IsNullOrEmpty(withdrawalRequestHash))
                return;

            var key = BuildKey(scUID, withdrawalRequestHash, transactionScope);
            var record = _withdrawals.GetOrAdd(key, _ => new WithdrawalRecord
            {
                ScUID = scUID,
                WithdrawalRequestHash = withdrawalRequestHash
            });

            lock (record.Lock)
            {
                record.LastActivity = TimeUtil.GetTime();

                if (record.Inputs.TryGetValue(inputIndex, out var existing))
                {
                    // A watched vault: the failed ceremony is forgotten and the others for the same fixed transaction
                    // carry on; no cooldown is laid on the input (see CheckWithdrawalSigning).
                    if (!string.IsNullOrEmpty(transactionScope))
                    {
                        if (existing.SessionByLeader != null)
                            foreach (var leader in existing.SessionByLeader.Where(kv => kv.Value == sessionId).Select(kv => kv.Key).ToList())
                                existing.SessionByLeader.Remove(leader);
                        if (existing.State == SigningState.InProgress && existing.SessionId == sessionId)
                            existing.SessionId = existing.SessionByLeader?.Values.FirstOrDefault() ?? string.Empty;
                        return;
                    }

                    if (existing.State == SigningState.Signed)
                        return; // Don't downgrade

                    existing.State = SigningState.Failed;
                    existing.SessionId = sessionId;
                    existing.Timestamp = TimeUtil.GetTime();
                }
                else if (!string.IsNullOrEmpty(transactionScope))
                {
                    return; // nothing was recorded for it
                }
                else
                {
                    record.Inputs[inputIndex] = new InputRecord
                    {
                        SessionId = sessionId,
                        State = SigningState.Failed,
                        Timestamp = TimeUtil.GetTime()
                    };
                }
            }

            LogUtility.Log($"[FROST Dedup] Recorded signing FAILED for withdrawal {withdrawalRequestHash} input {inputIndex} on contract {scUID}, session {sessionId}",
                "FrostWithdrawalSigningTracker.RecordSigningFailed");
        }

        /// <summary>
        /// The most recent outstanding signed-transaction pin for a contract, if any (the only one before
        /// VbtcWithdrawalConcurrencyHeight). Use <see cref="GetContractPins"/> for all of them.
        /// </summary>
        public static (string WithdrawalRequestHash, string BtcTxId, List<string> Outpoints)? GetContractPin(string scUID)
        {
            var latest = GetContractPins(scUID).OrderByDescending(p => p.PinnedAt).FirstOrDefault();
            if (latest == null)
                return null;
            return (latest.WithdrawalRequestHash, latest.BtcTxId, latest.Outpoints.ToList());
        }

        /// <summary>Every outstanding signed-transaction pin for a contract.</summary>
        public static List<ContractPinInfo> GetContractPins(string scUID)
        {
            if (string.IsNullOrEmpty(scUID) || !_contractPins.TryGetValue(scUID, out var pins))
                return new List<ContractPinInfo>();
            return pins.Values.Select(ToInfo).ToList();
        }

        /// <summary>
        /// Clears every pin on the contract (operator override) — call ONLY after observing on-chain that
        /// the pinned txs are resolved. The pin has no time-based expiry: a signed, unbroadcast tx stays
        /// spendable forever.
        /// </summary>
        public static void ClearContractPin(string scUID, string reason)
        {
            if (_contractPins.TryRemove(scUID, out var pins))
            {
                foreach (var pin in pins.Values)
                    LogUtility.Log($"[FROST Dedup] Cleared contract outpoint pin for {scUID} (tx {pin.BtcTxId}, withdrawal {pin.WithdrawalRequestHash}): {reason}",
                        "FrostWithdrawalSigningTracker.ClearContractPin");
            }
        }

        /// <summary>
        /// Clears one withdrawal's pin — call ONLY after observing on-chain that its tx confirmed or can never
        /// confirm (an input spent by a confirmed transaction).
        /// </summary>
        public static void ClearContractPin(string scUID, string withdrawalRequestHash, string reason)
        {
            if (_contractPins.TryGetValue(scUID, out var pins) && pins.TryRemove(withdrawalRequestHash, out var pin))
            {
                LogUtility.Log($"[FROST Dedup] Cleared contract outpoint pin for {scUID} (tx {pin.BtcTxId}, withdrawal {pin.WithdrawalRequestHash}): {reason}",
                    "FrostWithdrawalSigningTracker.ClearContractPin");
                if (pins.IsEmpty)
                    _contractPins.TryRemove(new KeyValuePair<string, ConcurrentDictionary<string, ContractOutpointPin>>(scUID, pins));
            }
        }

        /// <summary>
        /// Snapshot of every outstanding pin, for the background reconciler and the /frost/status and
        /// /frost/pins endpoints. All fields are on-chain/contract-public data.
        /// </summary>
        public static List<ContractPinInfo> GetAllContractPins()
        {
            return _contractPins.Values.SelectMany(p => p.Values).Select(ToInfo).ToList();
        }

        /// <summary>
        /// Stamps the outcome of a pin-release check onto the live pins (kept + why), so a pin that
        /// is being held is visibly being held for a REASON — the old release path kept pins with
        /// no telemetry at all, which is how two validators sat on a confirmed tx's pin for hours.
        /// No-op if the pin no longer exists (released concurrently).
        /// </summary>
        public static void NotePinReleaseCheck(string scUID, string outcome)
        {
            if (_contractPins.TryGetValue(scUID, out var pins))
            {
                foreach (var pin in pins.Values)
                {
                    pin.LastReleaseCheckTime = TimeUtil.GetTime();
                    pin.LastReleaseCheckOutcome = outcome;
                }
            }
        }

        /// <summary>Stamps one withdrawal's pin with the outcome of its release check.</summary>
        public static void NotePinReleaseCheck(string scUID, string withdrawalRequestHash, string outcome)
        {
            if (_contractPins.TryGetValue(scUID, out var pins) && pins.TryGetValue(withdrawalRequestHash, out var pin))
            {
                pin.LastReleaseCheckTime = TimeUtil.GetTime();
                pin.LastReleaseCheckOutcome = outcome;
            }
        }

        /// <summary>
        /// Cleanup expired withdrawal records (older than 24 hours). Contract pins are intentionally
        /// NOT expired here — see ClearContractPin.
        /// </summary>
        public static void CleanupExpiredRecords()
        {
            var now = TimeUtil.GetTime();
            var expired = _withdrawals
                .Where(kvp => now - kvp.Value.LastActivity > RECORD_EXPIRY_SECONDS)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in expired)
            {
                _withdrawals.TryRemove(key, out _);
            }

            if (expired.Count > 0)
            {
                LogUtility.Log($"[FROST Dedup] Cleaned up {expired.Count} expired signing records",
                    "FrostWithdrawalSigningTracker.CleanupExpiredRecords");
            }
        }

        /// <summary>
        /// Test-only: reset all state.
        /// </summary>
        public static void ResetForTests()
        {
            _withdrawals.Clear();
            _contractPins.Clear();
        }

        private static string BuildKey(string scUID, string withdrawalRequestHash, string? transactionScope = null)
            => string.IsNullOrEmpty(transactionScope) ? $"{scUID}:{withdrawalRequestHash}" : $"{scUID}:{withdrawalRequestHash}#{transactionScope.ToLowerInvariant()}";

        private static ConcurrentDictionary<string, ContractOutpointPin> NewPinSet()
            => new ConcurrentDictionary<string, ContractOutpointPin>(StringComparer.OrdinalIgnoreCase);

        private static ConcurrentDictionary<string, ContractOutpointPin> PinsFor(string scUID)
            => _contractPins.GetOrAdd(scUID, _ => NewPinSet());

        private static ContractPinInfo ToInfo(ContractOutpointPin pin) => new ContractPinInfo
        {
            ScUID = pin.ScUID,
            WithdrawalRequestHash = pin.WithdrawalRequestHash,
            BtcTxId = pin.BtcTxId,
            Outpoints = pin.Outpoints.ToList(),
            PinnedAt = pin.Timestamp,
            LastReleaseCheckTime = pin.LastReleaseCheckTime,
            LastReleaseCheckOutcome = pin.LastReleaseCheckOutcome
        };

        /// <summary>
        /// True if this validator has produced a signature share for ANY input of a Bitcoin
        /// transaction for the withdrawal, or holds the contract-level outpoint pin for it. Used to
        /// refuse cancellation votes: a signed transaction can be broadcast at any time.
        /// </summary>
        public static bool HasSignedTransaction(string scUID, string withdrawalRequestHash)
        {
            if (string.IsNullOrEmpty(scUID) || string.IsNullOrEmpty(withdrawalRequestHash)) return false;

            if (_withdrawals.TryGetValue(BuildKey(scUID, withdrawalRequestHash), out var record))
            {
                lock (record.Lock)
                {
                    if (record.Inputs.Values.Any(i => i.State == SigningState.Signed))
                        return true;
                }
            }

            if (_contractPins.TryGetValue(scUID, out var pins) && pins.ContainsKey(withdrawalRequestHash))
                return true;

            // Durable evidence outlives the in-memory records.
            try { if (FrostSignedWithdrawalEvidence.Get(scUID, withdrawalRequestHash) != null) return true; } catch { }

            return false;
        }

        /// <summary>
        /// True while a signing ceremony for the withdrawal is running on this validator (an input started and not yet
        /// signed, failed or stale). A cancellation vote waits for it to end: until then this validator cannot say
        /// whether it will have signed.
        /// </summary>
        public static bool HasCeremonyInProgress(string scUID, string withdrawalRequestHash)
        {
            if (string.IsNullOrEmpty(scUID) || string.IsNullOrEmpty(withdrawalRequestHash)) return false;
            if (!_withdrawals.TryGetValue(BuildKey(scUID, withdrawalRequestHash), out var record)) return false;
            var now = TimeUtil.GetTime();
            lock (record.Lock)
            {
                return record.Inputs.Values.Any(i => i.State == SigningState.InProgress && now - i.Timestamp <= IN_PROGRESS_STALE_SECONDS);
            }
        }

        /// <summary>
        /// Per-withdrawal record: one sub-record per transaction input, plus the pinned sighash set.
        /// </summary>
        private class WithdrawalRecord
        {
            public string ScUID { get; set; } = string.Empty;
            public string WithdrawalRequestHash { get; set; } = string.Empty;
            /// <summary>Ordered sighash-per-input set announced by the first sign/start (lowercase hex).</summary>
            public List<string>? PinnedSighashes { get; set; }
            /// <summary>The transaction this record is for (the first one started here), when the start named it.</summary>
            public string BtcTxId { get; set; } = string.Empty;
            public Dictionary<int, InputRecord> Inputs { get; } = new();
            public long LastActivity { get; set; } = TimeUtil.GetTime();
            public object Lock { get; } = new();
        }

        private class InputRecord
        {
            public string SessionId { get; set; } = string.Empty;
            public string MessageHash { get; set; } = string.Empty;
            public SigningState State { get; set; }
            public long Timestamp { get; set; }
            /// <summary>
            /// A vault the chain watches only: the open ceremony of each leader for this input of the one fixed
            /// transaction (leader address to session). Null on any other vault, where one ceremony holds an input.
            /// </summary>
            public Dictionary<string, string>? SessionByLeader { get; set; }
        }

        /// <summary>
        /// A signed-but-not-yet-observed-on-chain withdrawal transaction for a contract.
        /// </summary>
        private class ContractOutpointPin
        {
            public string ScUID { get; set; } = string.Empty;
            public string WithdrawalRequestHash { get; set; } = string.Empty;
            public string BtcTxId { get; set; } = string.Empty;
            public List<string> Outpoints { get; set; } = new();
            /// <summary>Unix time the transaction was signed (restored from evidence after a restart).</summary>
            public long Timestamp { get; set; }
            /// <summary>Unix time of the last release check that ran against this pin (0 = never checked).</summary>
            public long LastReleaseCheckTime { get; set; }
            /// <summary>Outcome of that check ("kept: ..." detail); empty until the first check.</summary>
            public string LastReleaseCheckOutcome { get; set; } = string.Empty;
        }

        /// <summary>
        /// Public snapshot of a contract pin — see GetAllContractPins.
        /// </summary>
        public class ContractPinInfo
        {
            public string ScUID { get; set; } = string.Empty;
            public string WithdrawalRequestHash { get; set; } = string.Empty;
            public string BtcTxId { get; set; } = string.Empty;
            public List<string> Outpoints { get; set; } = new();
            public long PinnedAt { get; set; }
            public long LastReleaseCheckTime { get; set; }
            public string LastReleaseCheckOutcome { get; set; } = string.Empty;
        }

        private enum SigningState
        {
            InProgress,
            Signed,
            Failed
        }
    }
}
