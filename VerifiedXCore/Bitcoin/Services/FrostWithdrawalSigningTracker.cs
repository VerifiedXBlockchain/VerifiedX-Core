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
    ///  - Per-CONTRACT outpoint pin (double-withdrawal guard): once a withdrawal's tx is signed,
    ///    its input outpoints are pinned for the contract. A DIFFERENT withdrawal's tx is only
    ///    signed if it CONFLICTS with the pinned tx (spends at least one pinned outpoint) —
    ///    Bitcoin then guarantees at most one of them ever confirms, closing the
    ///    sign-withhold-expire-resign double-payout path. The pin clears when the pinned tx (or a
    ///    conflict) is observed on-chain — see FrostStartup's spentness check — never by timeout.
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
        /// Per-contract outstanding-signed-transaction pins. Key = scUID.
        /// </summary>
        private static readonly ConcurrentDictionary<string, ContractOutpointPin> _contractPins = new();

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
        public static (bool Blocked, string Reason) CheckWithdrawalSigning(
            string scUID,
            string withdrawalRequestHash,
            int inputIndex = 0,
            string? messageHash = null,
            List<string>? allInputSighashes = null,
            List<string>? txInputOutpoints = null)
        {
            if (string.IsNullOrEmpty(scUID) || string.IsNullOrEmpty(withdrawalRequestHash))
                return (false, string.Empty); // Non-withdrawal signing, allow

            var key = BuildKey(scUID, withdrawalRequestHash);
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

                    if (record.Inputs.TryGetValue(inputIndex, out var input))
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

            // Contract-level double-withdrawal guard: while a DIFFERENT withdrawal's signed tx is
            // outstanding for this contract, only a CONFLICTING tx (sharing >=1 input outpoint) may
            // be signed. Deterministic largest-first coin selection makes legitimate successors
            // conflict naturally; only a disjoint-UTXO second payout is refused.
            if (_contractPins.TryGetValue(scUID, out var pin) &&
                !string.Equals(pin.WithdrawalRequestHash, withdrawalRequestHash, StringComparison.OrdinalIgnoreCase))
            {
                var conflicts = txInputOutpoints != null &&
                                txInputOutpoints.Any(op => pin.Outpoints.Contains(op, StringComparer.OrdinalIgnoreCase));
                if (!conflicts)
                {
                    return (true, $"Contract has an outstanding signed withdrawal tx ({pin.BtcTxId}, withdrawal {pin.WithdrawalRequestHash}). " +
                        $"A new withdrawal tx must spend at least one of its inputs [{string.Join(",", pin.Outpoints)}] so at most one can confirm. " +
                        $"PIN:{pin.BtcTxId}");
                }
            }

            return (false, string.Empty);
        }

        /// <summary>
        /// Record that a signing ceremony is starting for a withdrawal input.
        /// Called when the validator accepts a /frost/sign/start request. Pins the announced
        /// sighash set on first sight.
        /// </summary>
        public static void RecordSigningStarted(string scUID, string withdrawalRequestHash, string sessionId,
            int inputIndex = 0, string? messageHash = null, List<string>? allInputSighashes = null)
        {
            if (string.IsNullOrEmpty(scUID) || string.IsNullOrEmpty(withdrawalRequestHash))
                return;

            var key = BuildKey(scUID, withdrawalRequestHash);
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

                if (record.Inputs.TryGetValue(inputIndex, out var existing))
                {
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
                        Timestamp = TimeUtil.GetTime()
                    };
                }
            }

            LogUtility.Log($"[FROST Dedup] Recorded signing STARTED for withdrawal {withdrawalRequestHash} input {inputIndex} on contract {scUID}, session {sessionId}",
                "FrostWithdrawalSigningTracker.RecordSigningStarted");
        }

        /// <summary>
        /// Record that this validator generated a Round 2 signature share for a withdrawal input.
        /// Also pins the transaction's input outpoints at the CONTRACT level so a different
        /// withdrawal cannot sign a non-conflicting (double-payout) transaction while this signed
        /// one is outstanding.
        /// </summary>
        public static void RecordSigningCompleted(string scUID, string withdrawalRequestHash, string sessionId,
            int inputIndex = 0, string? messageHash = null, List<string>? txInputOutpoints = null, string? btcTxId = null)
        {
            if (string.IsNullOrEmpty(scUID) || string.IsNullOrEmpty(withdrawalRequestHash))
                return;

            var key = BuildKey(scUID, withdrawalRequestHash);
            var record = _withdrawals.GetOrAdd(key, _ => new WithdrawalRecord
            {
                ScUID = scUID,
                WithdrawalRequestHash = withdrawalRequestHash
            });

            lock (record.Lock)
            {
                record.LastActivity = TimeUtil.GetTime();
                record.Inputs[inputIndex] = new InputRecord
                {
                    SessionId = sessionId,
                    MessageHash = (messageHash ?? string.Empty).ToLowerInvariant(),
                    State = SigningState.Signed,
                    Timestamp = TimeUtil.GetTime()
                };
            }

            // Contract-level pin: my key share is now out for this transaction; until it is observed
            // on-chain (confirmed or conflicted away), any OTHER withdrawal tx for this contract must
            // conflict with it.
            if (txInputOutpoints != null && txInputOutpoints.Count > 0)
            {
                var pin = new ContractOutpointPin
                {
                    ScUID = scUID,
                    WithdrawalRequestHash = withdrawalRequestHash,
                    BtcTxId = btcTxId ?? string.Empty,
                    Outpoints = txInputOutpoints.Select(o => o.ToLowerInvariant()).ToList(),
                    Timestamp = TimeUtil.GetTime()
                };
                _contractPins.AddOrUpdate(scUID, pin, (_, _) => pin);
            }

            LogUtility.Log($"[FROST Dedup] Recorded signing COMPLETED for withdrawal {withdrawalRequestHash} input {inputIndex} on contract {scUID}, session {sessionId}" +
                (txInputOutpoints != null ? $", pinned {txInputOutpoints.Count} outpoint(s)" : ""),
                "FrostWithdrawalSigningTracker.RecordSigningCompleted");
        }

        /// <summary>
        /// Record that the signing ceremony failed for a withdrawal input. Allows retry after the
        /// cooldown. Never downgrades a Signed input.
        /// </summary>
        public static void RecordSigningFailed(string scUID, string withdrawalRequestHash, string sessionId, int inputIndex = 0)
        {
            if (string.IsNullOrEmpty(scUID) || string.IsNullOrEmpty(withdrawalRequestHash))
                return;

            var key = BuildKey(scUID, withdrawalRequestHash);
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
                    if (existing.State == SigningState.Signed)
                        return; // Don't downgrade

                    existing.State = SigningState.Failed;
                    existing.SessionId = sessionId;
                    existing.Timestamp = TimeUtil.GetTime();
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
        /// Returns the outstanding signed-transaction pin for a contract, if any.
        /// </summary>
        public static (string WithdrawalRequestHash, string BtcTxId, List<string> Outpoints)? GetContractPin(string scUID)
        {
            if (_contractPins.TryGetValue(scUID, out var pin))
                return (pin.WithdrawalRequestHash, pin.BtcTxId, pin.Outpoints.ToList());
            return null;
        }

        /// <summary>
        /// Clears the contract pin — call ONLY after observing on-chain that the pinned tx confirmed
        /// or that its inputs were spent by a conflicting tx (i.e. the pinned tx can never confirm).
        /// The pin has no time-based expiry: a signed, unbroadcast tx stays spendable forever.
        /// </summary>
        public static void ClearContractPin(string scUID, string reason)
        {
            if (_contractPins.TryRemove(scUID, out var pin))
            {
                LogUtility.Log($"[FROST Dedup] Cleared contract outpoint pin for {scUID} (tx {pin.BtcTxId}, withdrawal {pin.WithdrawalRequestHash}): {reason}",
                    "FrostWithdrawalSigningTracker.ClearContractPin");
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

        private static string BuildKey(string scUID, string withdrawalRequestHash)
            => $"{scUID}:{withdrawalRequestHash}";

        /// <summary>
        /// Per-withdrawal record: one sub-record per transaction input, plus the pinned sighash set.
        /// </summary>
        private class WithdrawalRecord
        {
            public string ScUID { get; set; } = string.Empty;
            public string WithdrawalRequestHash { get; set; } = string.Empty;
            /// <summary>Ordered sighash-per-input set announced by the first sign/start (lowercase hex).</summary>
            public List<string>? PinnedSighashes { get; set; }
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
            public long Timestamp { get; set; }
        }

        private enum SigningState
        {
            InProgress,
            Signed,
            Failed
        }
    }
}
