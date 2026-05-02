using ReserveBlockCore.Data;
using ReserveBlockCore.Services;
using System.Threading;

namespace ReserveBlockCore.Utilities
{
    /// <summary>
    /// FORK-RECOVERY: Shared self-healing utility for nodes (both validators and casters)
    /// that detect they are stuck on a minority-fork block.
    /// 
    /// When a node's lastBlock hash doesn't match the network majority and the height
    /// hasn't advanced for multiple check cycles, this utility:
    /// 1. Rolls back the bad block(s)
    /// 2. Re-downloads the correct block(s) from peers
    /// 3. Resets all failure counters
    /// 
    /// This requires NO human intervention — the node heals itself automatically.
    /// </summary>
    public static class ForkRecoveryUtility
    {
        /// <summary>Tracks the last observed block height for stuck detection.</summary>
        private static long _lastObservedHeight = -1;

        /// <summary>Number of consecutive check cycles where height hasn't advanced.</summary>
        private static int _stuckCycles = 0;

        /// <summary>Prevents concurrent recovery attempts.</summary>
        private static int _recoveryInProgress; // 0 = idle, 1 = running

        /// <summary>Number of stuck cycles before triggering recovery for validators/regular nodes.
        /// This is higher than the caster threshold because the BlockHeightCheckLoop runs less frequently.</summary>
        public const int VALIDATOR_STUCK_THRESHOLD = 5;

        /// <summary>Number of blocks to roll back during recovery. Usually 1 is enough
        /// since the fork typically occurs at the tip.</summary>
        public const int ROLLBACK_DEPTH = 1;

        /// <summary>
        /// Checks if the node's block height has been stuck and peers report a higher height.
        /// Call this periodically (e.g., from BlockHeightCheckLoop).
        /// If stuck for VALIDATOR_STUCK_THRESHOLD cycles with peers ahead, triggers recovery.
        /// </summary>
        /// <param name="myHeight">Current local block height</param>
        /// <param name="peerMaxHeight">Maximum height reported by peers</param>
        /// <param name="caller">Name of the calling context for logging (e.g., "Program", "ValidatorService")</param>
        /// <returns>True if recovery was triggered, false otherwise</returns>
        public static async Task<bool> CheckAndRecoverAsync(long myHeight, long peerMaxHeight, string caller = "ForkRecovery")
        {
            // If height advanced, reset tracking
            if (_lastObservedHeight != myHeight)
            {
                _lastObservedHeight = myHeight;
                _stuckCycles = 0;
                return false;
            }

            // Height hasn't changed — increment stuck counter
            _stuckCycles++;

            // Only trigger if peers are ahead (confirming the network is progressing without us)
            bool peersAhead = peerMaxHeight > myHeight;

            if (_stuckCycles >= VALIDATOR_STUCK_THRESHOLD && peersAhead)
            {
                LogUtility.Log(
                    $"[{caller}] FORK-RECOVERY: Detected {_stuckCycles} cycles stuck at height {myHeight} " +
                    $"while peers are at {peerMaxHeight} (delta={peerMaxHeight - myHeight}). Triggering self-heal.",
                    $"{caller}.ForkRecovery");
                ConsoleWriterService.Output(
                    $"[{caller}] FORK-RECOVERY: Stuck at height {myHeight} for {_stuckCycles} cycles. " +
                    $"Peers at {peerMaxHeight}. Initiating automatic rollback + resync...");

                var recovered = await RecoverAsync(myHeight, caller);
                return recovered;
            }
            else if (_stuckCycles > 0 && _stuckCycles % 3 == 0)
            {
                // Periodic diagnostic logging
                LogUtility.Log(
                    $"[{caller}] FORK-STUCK: {_stuckCycles} cycles at height {myHeight}. " +
                    $"peersAhead={peersAhead} peerMax={peerMaxHeight} threshold={VALIDATOR_STUCK_THRESHOLD}",
                    $"{caller}.ForkRecovery");
            }

            return false;
        }

        /// <summary>
        /// Performs the actual fork recovery: rollback + resync.
        /// Can be called directly by casters or validators when they detect a fork.
        /// Thread-safe via Interlocked guard.
        /// </summary>
        /// <param name="stuckHeight">The height at which the node is stuck</param>
        /// <param name="caller">Name of the calling context for logging</param>
        /// <param name="blocksToRollback">Number of blocks to roll back (default: 1)</param>
        /// <returns>True if recovery succeeded, false otherwise</returns>
        public static async Task<bool> RecoverAsync(long stuckHeight, string caller = "ForkRecovery", int blocksToRollback = ROLLBACK_DEPTH)
        {
            // Prevent concurrent recovery attempts
            if (Interlocked.CompareExchange(ref _recoveryInProgress, 1, 0) != 0)
            {
                LogUtility.Log(
                    $"[{caller}] FORK-RECOVERY: Recovery already in progress, skipping.",
                    $"{caller}.ForkRecovery");
                return false;
            }

            bool success = false;
            try
            {
                LogUtility.Log(
                    $"[{caller}] FORK-RECOVERY: Initiating self-heal at height {stuckHeight}. " +
                    $"Rolling back {blocksToRollback} block(s).",
                    $"{caller}.ForkRecovery");
                ConsoleWriterService.Output(
                    $"[{caller}] FORK-RECOVERY: Rolling back {blocksToRollback} block(s) from height {stuckHeight}...");

                // Step 1: Set resyncing flag to prevent other operations from interfering
                var wasResyncing = Globals.IsResyncing;
                Globals.IsResyncing = true;

                try
                {
                    // Step 2: Roll back the bad block(s)
                    var rollbackResult = await BlockRollbackUtility.RollbackBlocks(blocksToRollback);
                    if (rollbackResult)
                    {
                        LogUtility.Log(
                            $"[{caller}] FORK-RECOVERY: Rollback succeeded. " +
                            $"New lastBlock height={Globals.LastBlock.Height} " +
                            $"hash={Globals.LastBlock.Hash?[..Math.Min(16, Globals.LastBlock.Hash?.Length ?? 0)]}",
                            $"{caller}.ForkRecovery");
                    }
                    else
                    {
                        LogUtility.Log(
                            $"[{caller}] FORK-RECOVERY: Rollback returned false — chain may need deeper repair.",
                            $"{caller}.ForkRecovery");
                    }

                    // Step 3: Download the correct blocks from peers
                    LogUtility.Log(
                        $"[{caller}] FORK-RECOVERY: Downloading correct blocks from peers...",
                        $"{caller}.ForkRecovery");
                    try { await BlockDownloadService.GetAllBlocks(); } catch { }

                    // Step 4: Reset stuck tracking
                    _lastObservedHeight = -1;
                    _stuckCycles = 0;

                    success = Globals.LastBlock.Height >= stuckHeight;

                    LogUtility.Log(
                        $"[{caller}] FORK-RECOVERY: Self-heal {(success ? "COMPLETE" : "PARTIAL")}. " +
                        $"New lastBlock height={Globals.LastBlock.Height} " +
                        $"hash={Globals.LastBlock.Hash?[..Math.Min(16, Globals.LastBlock.Hash?.Length ?? 0)]}. " +
                        $"Previous stuck height was {stuckHeight}.",
                        $"{caller}.ForkRecovery");
                    ConsoleWriterService.Output(
                        $"[{caller}] FORK-RECOVERY: Self-heal {(success ? "complete" : "partial")}. " +
                        $"Now at height {Globals.LastBlock.Height}.");
                }
                finally
                {
                    // Restore resyncing flag
                    Globals.IsResyncing = wasResyncing;
                }
            }
            catch (Exception ex)
            {
                LogUtility.Log(
                    $"[{caller}] FORK-RECOVERY: Exception during recovery: {ex.Message}",
                    $"{caller}.ForkRecovery");
                // Reset stuck tracking even on failure so we don't immediately retry
                _stuckCycles = 0;
            }
            finally
            {
                Interlocked.Exchange(ref _recoveryInProgress, 0);
            }

            return success;
        }

        /// <summary>
        /// Resets all tracking state. Called after a successful block commit
        /// to ensure the stuck counter doesn't carry over from a previous stall.
        /// </summary>
        public static void ResetTracking()
        {
            _lastObservedHeight = -1;
            _stuckCycles = 0;
        }

        /// <summary>
        /// Returns true if a recovery operation is currently in progress.
        /// </summary>
        public static bool IsRecoveryInProgress => Volatile.Read(ref _recoveryInProgress) != 0;
    }
}