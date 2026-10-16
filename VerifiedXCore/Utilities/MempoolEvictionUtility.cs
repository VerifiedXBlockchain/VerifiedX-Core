using VerifiedXCore.Extensions;
using Newtonsoft.Json;
using VerifiedXCore.Data;
using VerifiedXCore.Models;

namespace VerifiedXCore.Utilities
{
    /// <summary>
    /// HAL-071 Fix: Utility to prevent unbounded mempool growth through global caps and eviction
    /// </summary>
    public class MempoolEvictionUtility
    {
        /// <summary>
        /// Check if adding a transaction would exceed global mempool limits
        /// </summary>
        public static (bool canAdd, string reason) CanAddToMempool(Transaction tx, int currentCount, long currentSize)
        {
            // Check entry count limit
            if (currentCount >= Globals.MaxMempoolEntries)
            {
                return (false, "Mempool at maximum entry capacity");
            }

            // Calculate transaction size
            var txSize = CalculateTransactionSize(tx);

            // Check size limit
            if (currentSize + txSize > Globals.MaxMempoolSizeBytes)
            {
                return (false, "Mempool at maximum size capacity");
            }

            return (true, string.Empty);
        }

        /// <summary>
        /// The admission bounds every path that inserts a received or submitted transaction into the mempool applies
        /// first: the fee floor, then the global entry and size caps. When the pool is full, only entries rated strictly
        /// below the incoming transaction make room for it (for a deadline-bound one, also same-rated entries that are not);
        /// if there are none it is refused.
        /// </summary>
        public static (bool admit, string reason) CheckAdmissionBounds(Transaction tx)
        {
            var plan = PlanAdmission(tx);
            if (plan.admit)
                Evict(plan.victims);
            return (plan.admit, plan.reason);
        }

        /// <summary>
        /// Decides whether <paramref name="tx"/> can enter the pool, and which lower-rated entries must go to make room for
        /// it. Deletes nothing: the caller evicts the returned entries once the transaction is in.
        /// </summary>
        /// <summary>
        /// Transactions that stop being valid at a deadline: a reserve CallBack() or Recover() (until the send settles), a
        /// vBTC V2 withdrawal cancel or vote, and attestations.
        /// </summary>
        internal static bool IsDeadlineBound(Transaction tx)
        {
            if (tx.TransactionType == TransactionType.VBTC_V2_WITHDRAWAL_CANCEL
                || tx.TransactionType == TransactionType.VBTC_V2_WITHDRAWAL_VOTE
                || VerifiedXCore.Bitcoin.Attestation.BtcAttestTx.IsAttestationType(tx.TransactionType))
                return true;
            if (tx.TransactionType != TransactionType.RESERVE || string.IsNullOrEmpty(tx.Data))
                return false;
            try
            {
                var function = (string?)Newtonsoft.Json.Linq.JObject.Parse(tx.Data)["Function"];
                return function == "CallBack()" || function == "Recover()";
            }
            catch (Newtonsoft.Json.JsonException)
            {
                return false;
            }
        }

        /// <summary>How many of one sender's deadline-bound entries are kept from displacement by another deadline-bound one.</summary>
        internal const int ProtectedPerSender = 8;

        /// <summary>
        /// The deadline-bound entries no other deadline-bound transaction displaces: each sender's earliest
        /// <see cref="ProtectedPerSender"/>. Beyond that a sender's entries can go, so one sender's many callbacks cannot
        /// hold every protected place and keep out another owner's.
        /// </summary>
        private static HashSet<string> ProtectedEntries(List<Transaction> pooled) =>
            pooled.Where(x => x.Hash != null && IsDeadlineBound(x))
                .GroupBy(x => x.FromAddress ?? "")
                .SelectMany(g => g.OrderBy(x => x.Hash != null && ArrivedAtUtc.TryGetValue(x.Hash, out var at) ? at : DateTime.MinValue)
                    .ThenBy(x => x.Timestamp)
                    .Take(ProtectedPerSender))
                .Select(x => x.Hash)
                .ToHashSet();

        private static (bool admit, string reason, List<Transaction> victims) PlanAdmission(Transaction tx)
        {
            var none = new List<Transaction>();
            var feeValidation = ValidateMinimumFee(tx);
            if (!feeValidation.isValid)
                return (false, feeValidation.reason, none);

            var rating = tx.TransactionRating ?? TransactionRating.F;
            var stats = GetAdmissionStats();
            var canAdd = CanAddToMempool(tx, stats.count, stats.sizeBytes);
            if (canAdd.canAdd)
                return (true, canAdd.reason, none);

            // A transaction valid only until a deadline may also displace same-rated ordinary entries: otherwise a pool
            // kept full of same-rated transfers holds it out until the deadline passes.
            var deadlineBound = IsDeadlineBound(tx);

            // A full pool that just had nothing to give up for this rating (or a better one) refuses at once:
            // otherwise every refused submission would pay for a whole-pool scan.
            if (!deadlineBound)
            {
                lock (AdmissionStatsLock)
                {
                    if (_noRoomForRatingUntilUtc > DateTime.UtcNow && rating >= _noRoomForRating)
                        return (false, canAdd.reason, none);
                }
            }

            var victims = PlanEviction(
                targetCount: (int)(Globals.MaxMempoolEntries * 0.95),
                targetSize: (long)(Globals.MaxMempoolSizeBytes * 0.95),
                onlyRatedBelow: rating,
                knownTotalSize: stats.sizeBytes,
                mustFitBytes: SizeOf(tx),
                sameRatedOrdinaryToo: deadlineBound);
            if (victims == null || victims.Count == 0)
            {
                if (!deadlineBound)
                {
                    lock (AdmissionStatsLock)
                    {
                        _noRoomForRating = rating;
                        _noRoomForRatingUntilUtc = DateTime.UtcNow + AdmissionSizeCacheTtl;
                    }
                }
                return (false, canAdd.reason, none);
            }

            var after = CanAddToMempool(tx, stats.count - victims.Count, stats.sizeBytes - victims.Sum(SizeOf));
            return (after.canAdd, after.reason, after.canAdd ? victims : none);
        }

        private static void Evict(List<Transaction> victims)
        {
            if (victims.Count == 0) return;
            ExecuteEviction(TransactionData.GetPool(), victims, out var freed);
            lock (AdmissionStatsLock) _cachedSizeBytes = Math.Max(0, _cachedSizeBytes - freed);
        }

        /// <summary>Serialises admissions, so a capacity decision and the insert it allows cannot interleave with another.</summary>
        private static readonly object AdmitLock = new();

        /// <summary>
        /// Applies the admission bounds and, if they admit, inserts <paramref name="tx"/> into <paramref name="mempool"/> in
        /// the same locked section, then counts its bytes and evicts what was planned to make room for it. Concurrent admissions therefore
        /// cannot together pass the entry or byte cap, and nothing is counted for a transaction that was not inserted.
        /// </summary>
        public static bool TryAdmitAndInsert(Transaction tx, LiteDB.ILiteCollection<Transaction> mempool) =>
            TryAdmitAndInsert(tx, mempool, out _);

        /// <param name="reason">Why the transaction was not admitted; empty when it was.</param>
        public static bool TryAdmitAndInsert(Transaction tx, LiteDB.ILiteCollection<Transaction> mempool, out string reason)
        {
            lock (AdmitLock)
            {
                // Two deliveries of one transaction can both pass a caller's "not pooled" check before either inserts. The
                // second is the same transaction (same hash and signature): it is already admitted, and a refusal here made
                // that caller's cleanup delete the first copy. A pooled copy with another signature is refused.
                var pooled = string.IsNullOrEmpty(tx.Hash) ? null : mempool.FindOne(x => x.Hash == tx.Hash);
                if (pooled != null)
                {
                    var same = string.Equals(pooled.Hash, tx.Hash, StringComparison.Ordinal) // the lookup ignores case
                        && string.Equals(pooled.Signature, tx.Signature, StringComparison.Ordinal);
                    reason = same ? "" : "A different transaction with this hash is in the mempool.";
                    return same;
                }

                var plan = PlanAdmission(tx);
                reason = plan.reason;
                if (!plan.admit)
                    return false;
                // Inserted before anything is evicted for it: an insert that fails leaves the pool as it was.
                if (mempool.InsertSafe(tx) == null)
                {
                    reason = "The mempool insert failed.";
                    return false;
                }
                NoteInserted(tx);
                Evict(plan.victims);
                return true;
            }
        }

        /// <summary>Counts a transaction just inserted into the mempool toward the byte total and records when it arrived.</summary>
        public static void NoteInserted(Transaction tx)
        {
            lock (AdmissionStatsLock) _cachedSizeBytes += SizeOf(tx);
            if (!string.IsNullOrEmpty(tx.Hash))
                ArrivedAtUtc[tx.Hash] = DateTime.UtcNow;
        }

        private static readonly object AdmissionStatsLock = new();
        private static long _cachedSizeBytes;
        private static DateTime _cachedSizeAtUtc = DateTime.MinValue;
        private static TransactionRating _noRoomForRating;
        private static DateTime _noRoomForRatingUntilUtc = DateTime.MinValue;

        /// <summary>Serialized size per pooled hash, so a refresh does not re-serialize rows it has already measured.</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> SizeByHash = new();

        /// <summary>
        /// When this node admitted each hash. Eviction within a rating goes by arrival here, not by the sender-chosen
        /// Timestamp, which a sender could backdate to look as if it had been waiting longest.
        /// </summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> ArrivedAtUtc = new();

        /// <summary>How long the pool's byte total is reused between full recounts; the entry count is always exact.</summary>
        internal static TimeSpan AdmissionSizeCacheTtl = TimeSpan.FromSeconds(2);

        private static long SizeOf(Transaction tx) =>
            string.IsNullOrEmpty(tx.Hash) ? CalculateTransactionSize(tx) : SizeByHash.GetOrAdd(tx.Hash, _ => CalculateTransactionSize(tx));

        /// <summary>
        /// The pool's entry count and byte size for an admission decision. The count is read from the index. The byte
        /// total is recounted at most every <see cref="AdmissionSizeCacheTtl"/>; in between, admissions add to it and
        /// evictions subtract from it, so it never reads lower than what this check let in.
        /// </summary>
        public static (int count, long sizeBytes) GetAdmissionStats()
        {
            var mempool = TransactionData.GetPool();
            var count = mempool.Count();
            lock (AdmissionStatsLock)
            {
                if (DateTime.UtcNow - _cachedSizeAtUtc >= AdmissionSizeCacheTtl)
                {
                    var rows = mempool.FindAll().ToList();
                    var live = new HashSet<string>(rows.Where(t => t.Hash != null).Select(t => t.Hash), StringComparer.Ordinal);
                    foreach (var gone in SizeByHash.Keys.Where(h => !live.Contains(h)).ToList())
                        SizeByHash.TryRemove(gone, out _);
                    foreach (var gone in ArrivedAtUtc.Keys.Where(h => !live.Contains(h)).ToList())
                        ArrivedAtUtc.TryRemove(gone, out _);
                    _cachedSizeBytes = rows.Sum(SizeOf);
                    _cachedSizeAtUtc = DateTime.UtcNow;
                }
                return (count, _cachedSizeBytes);
            }
        }

        internal static void RecordArrivalForTests(string hash, DateTime atUtc) => ArrivedAtUtc[hash] = atUtc;

        internal static bool RefusingWithoutScanFor(TransactionRating rating)
        {
            lock (AdmissionStatsLock) return _noRoomForRatingUntilUtc > DateTime.UtcNow && rating >= _noRoomForRating;
        }

        internal static void InvalidateAdmissionStats()
        {
            lock (AdmissionStatsLock)
            {
                _cachedSizeAtUtc = DateTime.MinValue;
                _noRoomForRatingUntilUtc = DateTime.MinValue;
            }
        }

        /// <summary>
        /// Calculate transaction size in bytes
        /// </summary>
        public static long CalculateTransactionSize(Transaction tx)
        {
            var txJson = JsonConvert.SerializeObject(tx);
            return txJson.Length;
        }

        /// <summary>
        /// Types the per-KB floor does not apply to, because the protocol prices them another way: the fee-free types
        /// VerifyTX exempts from its minimum fee (keep the two lists in step), the types that pay a fixed VFX cost in their
        /// amount (ADNR, voting topics, DecShop), the private types (a flat fee in the payload) and the sender-less
        /// Bitcoin attestation types. Every other type pays at least <see cref="Globals.MinFeePerKB"/> per KB.
        /// </summary>
        private static readonly HashSet<TransactionType> FeeFloorExempt = new()
        {
            TransactionType.TKNZ_WD_ARB,
            TransactionType.VBTC_V2_VALIDATOR_REGISTER,
            TransactionType.VBTC_V2_VALIDATOR_EXIT,
            TransactionType.VBTC_V2_VALIDATOR_HEARTBEAT,
            TransactionType.VBTC_V2_WITHDRAWAL_COMPLETE,
            TransactionType.VBTC_V2_BRIDGE_UNLOCK,
            TransactionType.VBTC_V2_BRIDGE_POOL_UNLOCK,
            TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC,
            TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC_COMPLETE,
            TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC_FAIL,
            TransactionType.ADNR,
            TransactionType.VOTE_TOPIC,
            TransactionType.DSTR,
        };

        internal static bool PaysOwnFixedFee(TransactionType type) =>
            FeeFloorExempt.Contains(type)
            || VerifiedXCore.Privacy.PrivateTransactionTypes.IsPrivateTransaction(type)
            || VerifiedXCore.Bitcoin.Attestation.BtcAttestTx.IsAttestationType(type);

        /// <summary>
        /// Validate transaction meets minimum fee requirements (HAL-071 fee floor)
        /// </summary>
        public static (bool isValid, string reason) ValidateMinimumFee(Transaction tx)
        {
            // Calculate transaction size in KB
            var txSizeBytes = CalculateTransactionSize(tx);
            var txSizeKB = (decimal)txSizeBytes / 1024M;

            // Calculate minimum required fee
            var minRequiredFee = txSizeKB * Globals.MinFeePerKB;

            if (!PaysOwnFixedFee(tx.TransactionType) && tx.Fee < minRequiredFee)
                return (false, $"Transaction fee {tx.Fee} below minimum {minRequiredFee:F8} RBX");

            return (true, string.Empty);
        }

        /// <summary>
        /// Evict lowest priority transactions to make room. With <paramref name="onlyRatedBelow"/> (an incoming
        /// transaction's rating), only entries rated strictly below it are candidates, so a sender cannot push out
        /// transactions rated as well as its own. Returns the hashes that were evicted.
        /// </summary>
        public static List<string> EvictLowestPriority(int targetCount = -1, long targetSize = -1, TransactionRating? onlyRatedBelow = null) =>
            EvictLowestPriority(targetCount, targetSize, onlyRatedBelow, null, out _);

        /// <param name="knownTotalSize">The pool's byte total when the caller already has it, so it is not summed again.</param>
        /// <param name="freedBytes">Bytes the evicted entries took up.</param>
        /// <param name="mustFitBytes">An incoming transaction's size: nothing is evicted unless evicting the chosen entries
        /// leaves room for it under both caps, so honest entries are never removed for a transaction that is then refused.</param>
        internal static List<string> EvictLowestPriority(int targetCount, long targetSize, TransactionRating? onlyRatedBelow, long? knownTotalSize, out long freedBytes, long? mustFitBytes = null)
        {
            var victims = PlanEviction(targetCount, targetSize, onlyRatedBelow, knownTotalSize, mustFitBytes);
            return ExecuteEviction(TransactionData.GetPool(), victims ?? new List<Transaction>(), out freedBytes);
        }

        /// <summary>
        /// The entries an eviction would remove, lowest rating first: null when nothing needs to go, or when
        /// <paramref name="mustFitBytes"/> is given and removing them would not leave room for it. Deletes nothing.
        /// </summary>
        /// <param name="sameRatedOrdinaryToo">Entries rated the same as <paramref name="onlyRatedBelow"/> may go too, unless
        /// they are themselves deadline-bound and among their sender's first <see cref="ProtectedPerSender"/> such entries.</param>
        private static List<Transaction>? PlanEviction(int targetCount, long targetSize, TransactionRating? onlyRatedBelow, long? knownTotalSize, long? mustFitBytes, bool sameRatedOrdinaryToo = false)
        {
            var mempool = TransactionData.GetPool();
            var allTxs = mempool.FindAll().ToList();

            if (allTxs.Count == 0)
                return null;

            // Determine how many to evict
            int countToEvict = 0;
            long sizeToEvict = 0;

            if (targetCount > 0 && allTxs.Count > targetCount)
            {
                countToEvict = allTxs.Count - targetCount;
            }

            if (targetSize > 0)
            {
                long currentSize = knownTotalSize ?? allTxs.Sum(SizeOf);
                if (currentSize > targetSize)
                {
                    sizeToEvict = currentSize - targetSize;
                }
            }

            if (countToEvict == 0 && sizeToEvict == 0)
                return null;

            // Lowest rating first (F is the highest enum value). Within a rating the latest to arrive here go first: a burst
            // of new entries makes room by evicting itself, not the transactions that were already waiting. A row with no
            // recorded arrival (inserted locally, or before a restart) counts as the earliest.
            var protectedHashes = sameRatedOrdinaryToo ? ProtectedEntries(allTxs) : null;
            var sortedTxs = allTxs
                .Where(x => onlyRatedBelow == null || (x.TransactionRating ?? TransactionRating.F) > onlyRatedBelow.Value
                    || (sameRatedOrdinaryToo && (x.TransactionRating ?? TransactionRating.F) == onlyRatedBelow.Value && !protectedHashes!.Contains(x.Hash)))
                .OrderByDescending(x => x.TransactionRating ?? TransactionRating.F)
                .ThenByDescending(x => x.Hash != null && ArrivedAtUtc.TryGetValue(x.Hash, out var at) ? at : DateTime.MinValue)
                .ToList();

            // The entries to evict, chosen once: until both targets are met. The loop below removes exactly these, so what
            // is evicted is what was checked.
            var victims = new List<Transaction>();
            long plannedBytes = 0;
            foreach (var t in sortedTxs)
            {
                if ((countToEvict == 0 || victims.Count >= countToEvict) && (sizeToEvict == 0 || plannedBytes >= sizeToEvict))
                    break;
                victims.Add(t);
                plannedBytes += SizeOf(t);
            }

            if (mustFitBytes != null)
            {
                var total = knownTotalSize ?? allTxs.Sum(SizeOf);
                var fits = allTxs.Count - victims.Count < Globals.MaxMempoolEntries
                    && total - plannedBytes + mustFitBytes.Value <= Globals.MaxMempoolSizeBytes;
                if (!fits)
                    return null;
            }

            return victims;
        }

        /// <summary>Removes <paramref name="victims"/> from the pool and returns the hashes actually removed.</summary>
        private static List<string> ExecuteEviction(LiteDB.ILiteCollection<Transaction> mempool, List<Transaction> victims, out long freedBytes)
        {
            freedBytes = 0;
            var evictedHashes = new List<string>();
            long evictedSize = 0;
            int evictedCount = 0;

            foreach (var tx in victims)
            {
                // Evict this transaction
                try
                {
                    if (mempool.DeleteManySafe(x => x.Hash == tx.Hash) == 0)
                        continue; // not removed: neither counted nor reported as evicted
                    TransactionData.ReleasePrivateMempoolNullifiersForTx(tx.Hash);
                    evictedHashes.Add(tx.Hash);
                    evictedSize += SizeOf(tx);
                    evictedCount++;
                    if (tx.Hash != null)
                    {
                        SizeByHash.TryRemove(tx.Hash, out _);
                        ArrivedAtUtc.TryRemove(tx.Hash, out _);
                    }
                }
                catch (Exception ex)
                {
                    ErrorLogUtility.LogError($"Failed to evict transaction {tx.Hash}: {ex.Message}", 
                        "MempoolEvictionUtility.EvictLowestPriority");
                }
            }

            if (evictedHashes.Count > 0)
            {
                ErrorLogUtility.LogError($"HAL-071: Evicted {evictedHashes.Count} transactions from mempool (freed {evictedSize} bytes)", 
                    "MempoolEvictionUtility.EvictLowestPriority");
            }

            freedBytes = evictedSize;
            return evictedHashes;
        }

        /// <summary>
        /// Get current mempool statistics
        /// </summary>
        public static (int count, long sizeBytes) GetMempoolStats()
        {
            var mempool = TransactionData.GetPool();
            var allTxs = mempool.FindAll().ToList();
            
            int count = allTxs.Count;
            long size = allTxs.Sum(tx => CalculateTransactionSize(tx));

            return (count, size);
        }

        /// <summary>
        /// Cleanup stale transactions and enforce global limits
        /// Should be called periodically (e.g., every 5 minutes)
        /// </summary>
        public static void CleanupMempool()
        {
            try
            {
                var mempool = TransactionData.GetPool();
                var allTxs = mempool.FindAll().ToList();

                if (allTxs.Count == 0)
                    return;

                var removedCount = 0;
                var currentTime = TimeUtil.GetTime();

                // Remove stale transactions (already handled by existing timestamp validation)
                foreach (var tx in allTxs)
                {
                    var isTxStale = TransactionData.IsTxTimestampStale(tx).GetAwaiter().GetResult();
                    if (isTxStale)
                    {
                        try
                        {
                            mempool.DeleteManySafe(x => x.Hash == tx.Hash);
                            TransactionData.ReleasePrivateMempoolNullifiersForTx(tx.Hash);
                            removedCount++;
                        }
                        catch { }
                    }
                }

                // Enforce global limits
                var stats = GetMempoolStats();
                
                // If over entry limit, evict excess
                if (stats.count > Globals.MaxMempoolEntries)
                {
                    int targetCount = (int)(Globals.MaxMempoolEntries * 0.9); // Evict down to 90% capacity
                    var evicted = EvictLowestPriority(targetCount: targetCount);
                    removedCount += evicted.Count;
                }

                // If over size limit, evict excess
                if (stats.sizeBytes > Globals.MaxMempoolSizeBytes)
                {
                    long targetSize = (long)(Globals.MaxMempoolSizeBytes * 0.9); // Evict down to 90% capacity
                    var evicted = EvictLowestPriority(targetSize: targetSize);
                    removedCount += evicted.Count;
                }

                if (removedCount > 0)
                {
                    var newStats = GetMempoolStats();
                    ErrorLogUtility.LogError($"HAL-071 Mempool cleanup: Removed {removedCount} transactions. " +
                        $"Mempool now: {newStats.count} entries, {newStats.sizeBytes} bytes",
                        "MempoolEvictionUtility.CleanupMempool");
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error during mempool cleanup: {ex.Message}", 
                    "MempoolEvictionUtility.CleanupMempool");
            }
        }
    }
}
