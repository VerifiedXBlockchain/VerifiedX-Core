using System.Collections.Concurrent;
using ReserveBlockCore.Utilities;

namespace ReserveBlockCore.Services
{
    /// <summary>
    /// Cleanup service for MessageLocks dictionary to prevent unbounded growth
    /// Removes entries for disconnected/inactive peers
    /// </summary>
    public class MessageLocksCleanupService
    {
        private static Timer? _cleanupTimer;
        private static bool _isRunning = false;
        private const int CLEANUP_INTERVAL_MINUTES = 5;
        private const int INACTIVE_THRESHOLD_SECONDS = 300; // 5 minutes

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

            LogUtility.Log($"MessageLocks cleanup service started (interval: {CLEANUP_INTERVAL_MINUTES} minutes)",
                "MessageLocksCleanupService.Start");
        }

        public static void Stop()
        {
            if (_cleanupTimer != null)
            {
                _cleanupTimer.Dispose();
                _cleanupTimer = null;
                _isRunning = false;
                LogUtility.Log("MessageLocks cleanup service stopped",
                    "MessageLocksCleanupService.Stop");
            }
        }

        private static void RunCleanup()
        {
            if (Globals.StopAllTimers)
                return;

            try
            {
                var currentTime = TimeUtil.GetMillisecondTime();
                var inactiveCutoff = currentTime - (INACTIVE_THRESHOLD_SECONDS * 1000);
                
                var inactiveKeys = Globals.MessageLocks
                    .Where(kvp => kvp.Value.LastRequestTime < inactiveCutoff)
                    .Select(kvp => kvp.Key)
                    .ToList();

                int removedCount = 0;
                foreach (var key in inactiveKeys)
                {
                    // Verify peer is actually disconnected before removing
                    if (!Globals.P2PValDict.ContainsKey(key) && 
                        !Globals.P2PPeerDict.ContainsKey(key) &&
                        !Globals.FortisPool.TryGetFromKey1(key, out _))
                    {
                        if (Globals.MessageLocks.TryRemove(key, out _))
                        {
                            removedCount++;
                        }
                    }
                }

                if (removedCount > 0 && Globals.OptionalLogging)
                {
                    LogUtility.Log($"Cleaned up {removedCount} inactive MessageLocks entries",
                        "MessageLocksCleanupService.RunCleanup");
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error in MessageLocks cleanup: {ex.Message}",
                    "MessageLocksCleanupService.RunCleanup");
            }
        }
    }
}
