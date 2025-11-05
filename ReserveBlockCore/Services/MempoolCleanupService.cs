using ReserveBlockCore.Utilities;

namespace ReserveBlockCore.Services
{
    /// <summary>
    /// HAL-071 Fix: Background service to periodically clean up the mempool
    /// </summary>
    public class MempoolCleanupService
    {
        private static Timer? _cleanupTimer;
        private static bool _isRunning = false;

        /// <summary>
        /// Start the mempool cleanup timer
        /// </summary>
        public static void Start()
        {
            if (_isRunning)
                return;

            _isRunning = true;

            // Run cleanup every N minutes (configured in Globals.MempoolCleanupIntervalMinutes)
            var intervalMs = Globals.MempoolCleanupIntervalMinutes * 60 * 1000;
            
            _cleanupTimer = new Timer(
                callback: _ => RunCleanup(),
                state: null,
                dueTime: intervalMs, // First run after interval
                period: intervalMs   // Subsequent runs every interval
            );

            ErrorLogUtility.LogError($"HAL-071: Mempool cleanup service started (interval: {Globals.MempoolCleanupIntervalMinutes} minutes)",
                "MempoolCleanupService.Start");
        }

        /// <summary>
        /// Stop the mempool cleanup timer
        /// </summary>
        public static void Stop()
        {
            if (_cleanupTimer != null)
            {
                _cleanupTimer.Dispose();
                _cleanupTimer = null;
                _isRunning = false;

                ErrorLogUtility.LogError("HAL-071: Mempool cleanup service stopped",
                    "MempoolCleanupService.Stop");
            }
        }

        /// <summary>
        /// Execute the cleanup logic
        /// </summary>
        private static void RunCleanup()
        {
            try
            {
                if (Globals.StopAllTimers)
                    return;

                var statsBefore = MempoolEvictionUtility.GetMempoolStats();

                // Run the cleanup
                MempoolEvictionUtility.CleanupMempool();

                var statsAfter = MempoolEvictionUtility.GetMempoolStats();

                // Log stats if there was a significant change
                if (statsBefore.count != statsAfter.count || 
                    Math.Abs(statsBefore.sizeBytes - statsAfter.sizeBytes) > 1000000) // > 1MB change
                {
                    ErrorLogUtility.LogError(
                        $"HAL-071: Mempool cleanup completed. Before: {statsBefore.count} txs ({statsBefore.sizeBytes} bytes), " +
                        $"After: {statsAfter.count} txs ({statsAfter.sizeBytes} bytes)",
                        "MempoolCleanupService.RunCleanup");
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"HAL-071: Error in mempool cleanup: {ex.Message}",
                    "MempoolCleanupService.RunCleanup");
            }
        }

        /// <summary>
        /// Manually trigger a cleanup (for testing or emergency use)
        /// </summary>
        public static void TriggerManualCleanup()
        {
            RunCleanup();
        }
    }
}
