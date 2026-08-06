using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Bitcoin.Services
{
    /// <summary>
    /// Janitor for the vBTC withdrawal-request store. Marks incomplete requests that every gate
    /// already treats as non-blocking (expired far beyond the anti-grief window) as Cancelled so
    /// the LiteDB store and state snapshots stay tidy.
    ///
    /// Deterministically inert by construction: it only touches rows that are ALREADY non-blocking
    /// under HasActiveContractRequest / GetActiveRequest / IsRequestorInRepeatCooldown on every
    /// node (expired > RETIRE_AFTER_BLOCKS ≫ EXPIRY_BLOCKS + REPEAT_REQUEST_COOLDOWN_BLOCKS), so
    /// completing them cannot change any gate outcome or consensus decision.
    /// </summary>
    public class VBTCWithdrawalCleanupService
    {
        private static Timer? _cleanupTimer;
        private static bool _isRunning = false;

        /// <summary>
        /// Blocks past the mined height (or seconds past Timestamp for height-less rows) before an
        /// incomplete request is physically retired. ~1 week; vastly beyond every gate window.
        /// </summary>
        public const long RETIRE_AFTER_BLOCKS = 40_320;
        public const long RETIRE_AFTER_SECONDS = RETIRE_AFTER_BLOCKS * 10;

        private const int CLEANUP_INTERVAL_MINUTES = 60;

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

            LogUtility.Log($"vBTC withdrawal cleanup service started (interval: {CLEANUP_INTERVAL_MINUTES} minutes)",
                "VBTCWithdrawalCleanupService.Start");
        }

        public static void Stop()
        {
            if (_cleanupTimer != null)
            {
                _cleanupTimer.Dispose();
                _cleanupTimer = null;
                _isRunning = false;
            }
        }

        public static void RunCleanup()
        {
            try
            {
                if (Globals.StopAllTimers)
                    return;

                var vwrDb = VBTCWithdrawalRequest.GetVBTCWithdrawalRequestDb();
                if (vwrDb == null)
                    return;

                var currentHeight = Globals.LastBlock?.Height ?? 0;
                var currentTime = TimeUtil.GetTime();
                if (currentHeight == 0)
                    return; // Not synced yet — do nothing.

                var staleRows = vwrDb.Query()
                    .Where(x => !x.IsCompleted)
                    .ToList()
                    .Where(x => x.RequestBlockHeight > 0
                        ? currentHeight - x.RequestBlockHeight > RETIRE_AFTER_BLOCKS
                        : currentTime - x.Timestamp > RETIRE_AFTER_SECONDS)
                    .ToList();

                foreach (var row in staleRows)
                {
                    row.Status = VBTCWithdrawalStatus.Cancelled;
                    row.IsCompleted = true;
                    vwrDb.UpdateSafe(row);
                }

                if (staleRows.Count > 0)
                {
                    LogUtility.Log($"Retired {staleRows.Count} long-expired incomplete vBTC withdrawal request(s) (older than {RETIRE_AFTER_BLOCKS} blocks)",
                        "VBTCWithdrawalCleanupService.RunCleanup");
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error in vBTC withdrawal cleanup: {ex.Message}",
                    "VBTCWithdrawalCleanupService.RunCleanup");
            }
        }
    }
}
