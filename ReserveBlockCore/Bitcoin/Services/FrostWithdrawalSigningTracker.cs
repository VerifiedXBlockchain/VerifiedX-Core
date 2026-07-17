using System.Collections.Concurrent;
using ReserveBlockCore.Utilities;

namespace ReserveBlockCore.Bitcoin.Services
{
    /// <summary>
    /// FIND-028 Fix: Tracks which withdrawal request hashes this validator has already
    /// participated in FROST signing for. Prevents a malicious coordinator from requesting
    /// multiple signatures for the same withdrawal (double-spend protection at the network level).
    /// 
    /// This is the primary defense — even if a user modifies their local node code to bypass
    /// client-side checks, validators independently refuse to sign twice for the same withdrawal.
    /// </summary>
    public static class FrostWithdrawalSigningTracker
    {
        /// <summary>
        /// Tracks signing state for a withdrawal request.
        /// Key = "{scUID}:{withdrawalRequestHash}"
        /// </summary>
        private static readonly ConcurrentDictionary<string, SigningRecord> _signedWithdrawals = new();

        /// <summary>
        /// Cooldown period after a failed signing attempt before retry is allowed (seconds).
        /// </summary>
        private const int FAILED_RETRY_COOLDOWN_SECONDS = 60;

        /// <summary>
        /// Maximum age of records before cleanup (24 hours).
        /// </summary>
        private const int RECORD_EXPIRY_SECONDS = 86400;

        /// <summary>
        /// Check if this validator has already signed (or is currently signing) for a withdrawal.
        /// Returns (blocked, reason) — if blocked is true, the signing should be refused.
        /// </summary>
        public static (bool Blocked, string Reason) CheckWithdrawalSigning(string scUID, string withdrawalRequestHash)
        {
            if (string.IsNullOrEmpty(scUID) || string.IsNullOrEmpty(withdrawalRequestHash))
                return (false, string.Empty); // Non-withdrawal signing, allow

            var key = BuildKey(scUID, withdrawalRequestHash);
            
            if (!_signedWithdrawals.TryGetValue(key, out var record))
                return (false, string.Empty); // Never seen, allow

            var now = TimeUtil.GetTime();

            switch (record.State)
            {
                case SigningState.InProgress:
                    // If the in-progress record is older than 5 minutes, treat as stale/failed
                    if (now - record.Timestamp > 300)
                    {
                        record.State = SigningState.Failed;
                        record.Timestamp = now;
                        return (false, string.Empty); // Allow retry
                    }
                    return (true, $"Signing ceremony already in progress for this withdrawal (session: {record.SessionId})");

                case SigningState.Signed:
                    return (true, $"Already signed for this withdrawal request. BTC may have been broadcast. Session: {record.SessionId}");

                case SigningState.Failed:
                    // Allow retry after cooldown
                    if (now - record.Timestamp >= FAILED_RETRY_COOLDOWN_SECONDS)
                        return (false, string.Empty); // Cooldown passed, allow retry
                    var remaining = FAILED_RETRY_COOLDOWN_SECONDS - (now - record.Timestamp);
                    return (true, $"Signing failed recently. Retry allowed in {remaining} seconds.");

                default:
                    return (false, string.Empty);
            }
        }

        /// <summary>
        /// Record that a signing ceremony is starting for a withdrawal.
        /// Called when the validator accepts a /frost/sign/start request.
        /// </summary>
        public static void RecordSigningStarted(string scUID, string withdrawalRequestHash, string sessionId)
        {
            if (string.IsNullOrEmpty(scUID) || string.IsNullOrEmpty(withdrawalRequestHash))
                return;

            var key = BuildKey(scUID, withdrawalRequestHash);
            var record = new SigningRecord
            {
                ScUID = scUID,
                WithdrawalRequestHash = withdrawalRequestHash,
                SessionId = sessionId,
                State = SigningState.InProgress,
                Timestamp = TimeUtil.GetTime()
            };

            _signedWithdrawals.AddOrUpdate(key, record, (_, existing) =>
            {
                // Only overwrite if previous was Failed (retry) or stale InProgress
                if (existing.State == SigningState.Failed || 
                    (existing.State == SigningState.InProgress && TimeUtil.GetTime() - existing.Timestamp > 300))
                {
                    return record;
                }
                return existing; // Don't overwrite Signed state
            });

            LogUtility.Log($"[FROST Dedup] Recorded signing STARTED for withdrawal {withdrawalRequestHash} on contract {scUID}, session {sessionId}",
                "FrostWithdrawalSigningTracker.RecordSigningStarted");
        }

        /// <summary>
        /// Record that this validator successfully generated a signature share for the withdrawal.
        /// After this, the validator will refuse any further signing requests for the same withdrawal.
        /// Called after the validator generates its Round 2 signature share.
        /// </summary>
        public static void RecordSigningCompleted(string scUID, string withdrawalRequestHash, string sessionId)
        {
            if (string.IsNullOrEmpty(scUID) || string.IsNullOrEmpty(withdrawalRequestHash))
                return;

            var key = BuildKey(scUID, withdrawalRequestHash);
            var record = new SigningRecord
            {
                ScUID = scUID,
                WithdrawalRequestHash = withdrawalRequestHash,
                SessionId = sessionId,
                State = SigningState.Signed,
                Timestamp = TimeUtil.GetTime()
            };

            _signedWithdrawals.AddOrUpdate(key, record, (_, _) => record);

            LogUtility.Log($"[FROST Dedup] Recorded signing COMPLETED for withdrawal {withdrawalRequestHash} on contract {scUID}, session {sessionId}",
                "FrostWithdrawalSigningTracker.RecordSigningCompleted");
        }

        /// <summary>
        /// Record that the signing ceremony failed for a withdrawal.
        /// This allows retry after a cooldown period.
        /// </summary>
        public static void RecordSigningFailed(string scUID, string withdrawalRequestHash, string sessionId)
        {
            if (string.IsNullOrEmpty(scUID) || string.IsNullOrEmpty(withdrawalRequestHash))
                return;

            var key = BuildKey(scUID, withdrawalRequestHash);

            if (_signedWithdrawals.TryGetValue(key, out var existing))
            {
                // Don't downgrade from Signed to Failed
                if (existing.State == SigningState.Signed)
                    return;
            }

            var record = new SigningRecord
            {
                ScUID = scUID,
                WithdrawalRequestHash = withdrawalRequestHash,
                SessionId = sessionId,
                State = SigningState.Failed,
                Timestamp = TimeUtil.GetTime()
            };

            _signedWithdrawals.AddOrUpdate(key, record, (_, existing) =>
            {
                if (existing.State == SigningState.Signed)
                    return existing; // Don't downgrade
                return record;
            });

            LogUtility.Log($"[FROST Dedup] Recorded signing FAILED for withdrawal {withdrawalRequestHash} on contract {scUID}, session {sessionId}",
                "FrostWithdrawalSigningTracker.RecordSigningFailed");
        }

        /// <summary>
        /// Cleanup expired records (older than 24 hours).
        /// Called opportunistically during session cleanup.
        /// </summary>
        public static void CleanupExpiredRecords()
        {
            var now = TimeUtil.GetTime();
            var expired = _signedWithdrawals
                .Where(kvp => now - kvp.Value.Timestamp > RECORD_EXPIRY_SECONDS)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in expired)
            {
                _signedWithdrawals.TryRemove(key, out _);
            }

            if (expired.Count > 0)
            {
                LogUtility.Log($"[FROST Dedup] Cleaned up {expired.Count} expired signing records",
                    "FrostWithdrawalSigningTracker.CleanupExpiredRecords");
            }
        }

        private static string BuildKey(string scUID, string withdrawalRequestHash)
            => $"{scUID}:{withdrawalRequestHash}";

        /// <summary>
        /// Internal record tracking signing state for a withdrawal.
        /// </summary>
        private class SigningRecord
        {
            public string ScUID { get; set; } = string.Empty;
            public string WithdrawalRequestHash { get; set; } = string.Empty;
            public string SessionId { get; set; } = string.Empty;
            public SigningState State { get; set; }
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