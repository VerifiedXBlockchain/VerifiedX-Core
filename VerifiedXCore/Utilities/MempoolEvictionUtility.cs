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
        /// below the incoming transaction make room for it; if there are none it is refused.
        /// </summary>
        public static (bool admit, string reason) CheckAdmissionBounds(Transaction tx)
        {
            var feeValidation = ValidateMinimumFee(tx);
            if (!feeValidation.isValid)
                return (false, feeValidation.reason);

            var rating = tx.TransactionRating ?? TransactionRating.F;
            var stats = GetAdmissionStats();
            var canAdd = CanAddToMempool(tx, stats.count, stats.sizeBytes);
            if (!canAdd.canAdd)
            {
                // A full pool that just had nothing to give up for this rating (or a better one) refuses at once:
                // otherwise every refused submission would pay for a whole-pool scan.
                lock (AdmissionStatsLock)
                {
                    if (_noRoomForRatingUntilUtc > DateTime.UtcNow && rating >= _noRoomForRating)
                        return canAdd;
                }

                var evicted = EvictLowestPriority(
                    targetCount: (int)(Globals.MaxMempoolEntries * 0.95),
                    targetSize: (long)(Globals.MaxMempoolSizeBytes * 0.95),
                    onlyRatedBelow: rating,
                    knownTotalSize: stats.sizeBytes,
                    freedBytes: out var freed,
                    mustFitBytes: SizeOf(tx));

                stats = (stats.count - evicted.Count, stats.sizeBytes - freed);
                lock (AdmissionStatsLock)
                {
                    _cachedSizeBytes = Math.Max(0, _cachedSizeBytes - freed);
                    if (evicted.Count == 0)
                    {
                        _noRoomForRating = rating;
                        _noRoomForRatingUntilUtc = DateTime.UtcNow + AdmissionSizeCacheTtl;
                    }
                }
                canAdd = CanAddToMempool(tx, stats.count, stats.sizeBytes);
            }

            return (canAdd.canAdd, canAdd.reason);
        }

        /// <summary>Serialises admissions, so a capacity decision and the insert it allows cannot interleave with another.</summary>
        private static readonly object AdmitLock = new();

        /// <summary>
        /// Applies <see cref="CheckAdmissionBounds"/> and, if it admits, inserts <paramref name="tx"/> into
        /// <paramref name="mempool"/> in the same locked section, then counts its bytes. Concurrent admissions therefore
        /// cannot together pass the entry or byte cap, and nothing is counted for a transaction that was not inserted.
        /// </summary>
        public static bool TryAdmitAndInsert(Transaction tx, LiteDB.ILiteCollection<Transaction> mempool)
        {
            lock (AdmitLock)
            {
                if (!CheckAdmissionBounds(tx).admit)
                    return false;
                mempool.InsertSafe(tx);
                NoteInserted(tx);
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
        /// Validate transaction meets minimum fee requirements (HAL-071 fee floor)
        /// </summary>
        public static (bool isValid, string reason) ValidateMinimumFee(Transaction tx)
        {
            // Calculate transaction size in KB
            var txSizeBytes = CalculateTransactionSize(tx);
            var txSizeKB = (decimal)txSizeBytes / 1024M;

            // Calculate minimum required fee
            var minRequiredFee = txSizeKB * Globals.MinFeePerKB;

            // For regular TX type, check fee vs amount
            if (tx.TransactionType == TransactionType.TX)
            {
                if (tx.Fee < minRequiredFee)
                {
                    return (false, $"Transaction fee {tx.Fee} below minimum {minRequiredFee:F8} RBX");
                }
            }

            // For other transaction types, the required RBX acts as the fee
            // (e.g., ADNR requires 5 RBX, DecShop requires 10 RBX)
            // These are already validated elsewhere, so we allow them

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
            freedBytes = 0;
            var evictedHashes = new List<string>();
            var mempool = TransactionData.GetPool();
            var allTxs = mempool.FindAll().ToList();

            if (allTxs.Count == 0)
                return evictedHashes;

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
                return evictedHashes;

            // Lowest rating first (F is the highest enum value). Within a rating the latest to arrive here go first: a burst
            // of new entries makes room by evicting itself, not the transactions that were already waiting. A row with no
            // recorded arrival (inserted locally, or before a restart) counts as the earliest.
            var sortedTxs = allTxs
                .Where(x => onlyRatedBelow == null || (x.TransactionRating ?? TransactionRating.F) > onlyRatedBelow.Value)
                .OrderByDescending(x => x.TransactionRating ?? TransactionRating.F)
                .ThenByDescending(x => x.Hash != null && ArrivedAtUtc.TryGetValue(x.Hash, out var at) ? at : DateTime.MinValue)
                .ToList();

            if (mustFitBytes != null)
            {
                var total = knownTotalSize ?? allTxs.Sum(SizeOf);
                int plannedCount = 0;
                long plannedBytes = 0;
                foreach (var t in sortedTxs)
                {
                    if ((countToEvict == 0 || plannedCount >= countToEvict) && (sizeToEvict == 0 || plannedBytes >= sizeToEvict))
                        break;
                    plannedCount++;
                    plannedBytes += SizeOf(t);
                }
                var fits = allTxs.Count - plannedCount < Globals.MaxMempoolEntries
                    && total - plannedBytes + mustFitBytes.Value <= Globals.MaxMempoolSizeBytes;
                if (!fits)
                    return evictedHashes;
            }

            long evictedSize = 0;
            int evictedCount = 0;

            foreach (var tx in sortedTxs)
            {
                // Stop if we've met targets
                if (countToEvict > 0 && evictedCount >= countToEvict)
                    break;
                if (sizeToEvict > 0 && evictedSize >= sizeToEvict)
                    break;

                // Evict this transaction
                try
                {
                    mempool.DeleteManySafe(x => x.Hash == tx.Hash);
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
