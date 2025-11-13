using Newtonsoft.Json;
using ReserveBlockCore.Data;
using ReserveBlockCore.Models;

namespace ReserveBlockCore.Utilities
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
        /// Evict lowest priority transactions to make room
        /// Returns list of transaction hashes that were evicted
        /// </summary>
        public static List<string> EvictLowestPriority(int targetCount = -1, long targetSize = -1)
        {
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
                long currentSize = allTxs.Sum(tx => CalculateTransactionSize(tx));
                if (currentSize > targetSize)
                {
                    sizeToEvict = currentSize - targetSize;
                }
            }

            if (countToEvict == 0 && sizeToEvict == 0)
                return evictedHashes;

            // Sort by priority (lowest first)
            // Priority order: F (fail) < E < D < C < B < A
            // Within same rating, oldest (by timestamp) evicted first
            var sortedTxs = allTxs
                .OrderByDescending(x => x.TransactionRating ?? TransactionRating.F) // F is highest enum value, so descending puts it first
                .ThenBy(x => x.Timestamp)
                .ToList();

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
                    evictedHashes.Add(tx.Hash);
                    evictedSize += CalculateTransactionSize(tx);
                    evictedCount++;
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
