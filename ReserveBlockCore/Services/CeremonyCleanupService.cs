using ReserveBlockCore.Bitcoin.Controllers;
using ReserveBlockCore.Utilities;

namespace ReserveBlockCore.Services
{
    /// <summary>
    /// Background service to periodically clean up stale MPC ceremonies from validator memory.
    /// Active ceremonies older than 1 hour are force-expired (TimedOut).
    /// Terminal ceremonies (Completed/Failed/TimedOut) older than 1 hour are removed entirely.
    /// This prevents memory leaks from abandoned ceremony requests.
    /// </summary>
    public class CeremonyCleanupService
    {
        private static Timer? _cleanupTimer;
        private static bool _isRunning = false;

        /// <summary>
        /// Cleanup interval in minutes
        /// </summary>
        private const int CleanupIntervalMinutes = 10;

        /// <summary>
        /// Max age for active ceremonies before they are force-expired (seconds)
        /// </summary>
        private const long ActiveCeremonyTtlSeconds = 3600; // 1 hour

        /// <summary>
        /// Max age for terminal ceremonies before they are removed from memory (seconds)
        /// </summary>
        private const long TerminalCeremonyTtlSeconds = 3600; // 1 hour

        /// <summary>
        /// Start the ceremony cleanup timer
        /// </summary>
        public static void Start()
        {
            if (_isRunning)
                return;

            _isRunning = true;

            var intervalMs = CleanupIntervalMinutes * 60 * 1000;

            _cleanupTimer = new Timer(
                callback: _ => RunCleanup(),
                state: null,
                dueTime: intervalMs,
                period: intervalMs
            );

            LogUtility.Log($"[CeremonyCleanupService] Started (interval: {CleanupIntervalMinutes} minutes, TTL: {ActiveCeremonyTtlSeconds / 60} minutes)",
                "CeremonyCleanupService.Start");
        }

        /// <summary>
        /// Stop the ceremony cleanup timer
        /// </summary>
        public static void Stop()
        {
            if (_cleanupTimer != null)
            {
                _cleanupTimer.Dispose();
                _cleanupTimer = null;
                _isRunning = false;

                LogUtility.Log("[CeremonyCleanupService] Stopped", "CeremonyCleanupService.Stop");
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

                var removedCount = VBTCController.CleanupStaleCeremonies(ActiveCeremonyTtlSeconds, TerminalCeremonyTtlSeconds);

                if (removedCount > 0)
                {
                    LogUtility.Log($"[CeremonyCleanupService] Cleaned up {removedCount} stale/expired ceremonies",
                        "CeremonyCleanupService.RunCleanup");
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"[CeremonyCleanupService] Error during cleanup: {ex.Message}",
                    "CeremonyCleanupService.RunCleanup");
            }
        }
    }
}
