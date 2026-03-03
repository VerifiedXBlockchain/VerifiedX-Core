using System.Collections.Concurrent;
using ReserveBlockCore.Data;
using ReserveBlockCore.Utilities;

namespace ReserveBlockCore.Services
{
    /// <summary>
    /// Cleanup service for broadcast tracking dictionaries to prevent unbounded growth
    /// Removes old entries from various broadcast tracking collections
    /// </summary>
    public class BroadcastTrackingCleanupService
    {
        private static Timer? _cleanupTimer;
        private static bool _isRunning = false;
        private const int CLEANUP_INTERVAL_MINUTES = 10;
        private const int MAX_AGE_MINUTES = 30; // Remove entries older than 30 minutes

        public static void Start()
        {
            if (_isRunning)
                return;

            _isRunning = true;
            var intervalMs = CLEANUP_INTERVAL_MINUTES * 60 * 1000;

            _cleanupTimer = new Timer(
                callback: _ => RunCleanup(),
                state: null,
                dueTime: intervalMs,
                period: intervalMs
            );

            LogUtility.Log($"Broadcast tracking cleanup service started (interval: {CLEANUP_INTERVAL_MINUTES} minutes)",
                "BroadcastTrackingCleanupService.Start");
        }

        public static void Stop()
        {
            if (_cleanupTimer != null)
            {
                _cleanupTimer.Dispose();
                _cleanupTimer = null;
                _isRunning = false;
                LogUtility.Log("Broadcast tracking cleanup service stopped",
                    "BroadcastTrackingCleanupService.Stop");
            }
        }

        private static void RunCleanup()
        {
            if (Globals.StopAllTimers)
                return;

            try
            {
                int totalRemoved = 0;
                var currentTime = DateTime.UtcNow;
                var cutoffTime = currentTime.AddMinutes(-MAX_AGE_MINUTES);

                // Cleanup ProofsBroadcasted
                var oldProofs = Globals.ProofsBroadcasted
                    .Where(kvp => kvp.Value.HasValue && kvp.Value.Value < cutoffTime)
                    .Select(kvp => kvp.Key)
                    .ToList();
                foreach (var key in oldProofs)
                {
                    if (Globals.ProofsBroadcasted.TryRemove(key, out _))
                        totalRemoved++;
                }

                // Cleanup BlockQueueBroadcasted
                var currentHeight = Globals.LastBlock.Height;
                var oldBlocks = Globals.BlockQueueBroadcasted
                    .Where(kvp => kvp.Key < currentHeight - 100) // Keep last 100 blocks
                    .Select(kvp => kvp.Key)
                    .ToList();
                foreach (var key in oldBlocks)
                {
                    if (Globals.BlockQueueBroadcasted.TryRemove(key, out _))
                        totalRemoved++;
                }

                // Cleanup BroadcastedTrxDict (check mempool)
                var mempool = TransactionData.GetMempool();
                var mempoolHashes = new HashSet<string>(mempool.Select(x => x.Hash));
                
                var oldTxs = Globals.BroadcastedTrxDict.Keys
                    .Where(hash => !mempoolHashes.Contains(hash))
                    .ToList();
                foreach (var key in oldTxs)
                {
                    if (Globals.BroadcastedTrxDict.TryRemove(key, out _))
                        totalRemoved++;
                }

                // Cleanup ConsensusBroadcastedTrxDict (check mempool)
                var oldConsensusTxs = Globals.ConsensusBroadcastedTrxDict.Keys
                    .Where(hash => !mempoolHashes.Contains(hash))
                    .ToList();
                foreach (var key in oldConsensusTxs)
                {
                    if (Globals.ConsensusBroadcastedTrxDict.TryRemove(key, out _))
                        totalRemoved++;
                }

                // Cleanup CasterRoundDict (keep only recent rounds)
                var oldRounds = Globals.CasterRoundDict
                    .Where(kvp => kvp.Key < currentHeight - 1000) // Keep last 1000 heights
                    .Select(kvp => kvp.Key)
                    .ToList();
                foreach (var key in oldRounds)
                {
                    if (Globals.CasterRoundDict.TryRemove(key, out _))
                        totalRemoved++;
                }

                // Cleanup CasterRoundAuditDict (keep only recent audits)
                var oldAudits = Globals.CasterRoundAuditDict
                    .Where(kvp => kvp.Key < currentHeight - 1000)
                    .Select(kvp => kvp.Key)
                    .ToList();
                foreach (var key in oldAudits)
                {
                    if (Globals.CasterRoundAuditDict.TryRemove(key, out _))
                        totalRemoved++;
                }

                // Cleanup DuplicatesBroadcastedDict (old duplicates)
                var oldDuplicates = Globals.DuplicatesBroadcastedDict
                    .Where(kvp => kvp.Value.LastDetection < cutoffTime)
                    .Select(kvp => kvp.Key)
                    .ToList();
                foreach (var key in oldDuplicates)
                {
                    if (Globals.DuplicatesBroadcastedDict.TryRemove(key, out _))
                        totalRemoved++;
                }

                if (totalRemoved > 0 && Globals.OptionalLogging)
                {
                    LogUtility.Log($"Cleaned up {totalRemoved} old broadcast tracking entries",
                        "BroadcastTrackingCleanupService.RunCleanup");
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error in broadcast tracking cleanup: {ex.Message}",
                    "BroadcastTrackingCleanupService.RunCleanup");
            }
        }
    }
}
