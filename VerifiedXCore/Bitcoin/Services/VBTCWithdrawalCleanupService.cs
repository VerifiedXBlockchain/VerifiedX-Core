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

                // REPORT ONLY — this service must not write Status/IsCompleted.
                //
                // Both fields are read by consensus: TransactionValidatorService rejects
                // VBTC_V2_WITHDRAWAL_COMPLETE and VBTC_V2_WITHDRAWAL_CANCEL when IsCompleted is
                // set, and VerifyTX runs under block validation. This service fires from a
                // node-local 60-minute timer whose due time is measured from process start, and
                // its trigger mixes chain height with wall clock — so the moment a given node
                // flips a given row is not agreed on-chain. A node that had already retired a row
                // would reject a completion block that a freshly-restarted peer accepts: a fork,
                // permanently, since the retirement never un-sets.
                //
                // Retirement is also not needed for correctness: HasActiveContractRequest,
                // GetActiveRequest and IsRequestorInRepeatCooldown already treat rows this old as
                // non-blocking. It bought tidiness only.
                if (staleRows.Count > 0)
                {
                    LogUtility.Log($"{staleRows.Count} long-expired incomplete vBTC withdrawal request(s) are older than {RETIRE_AFTER_BLOCKS} blocks (already non-blocking under every gate; not mutated — Status/IsCompleted are consensus-read fields)",
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
