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

        /// <summary>ESCALATION: Tracks consecutive failed recovery attempts.
        /// When RecoverAsync() returns false (blocks couldn't be validated after rollback+download),
        /// this counter increments. After ESCALATION_THRESHOLD failures, we skip the rollback approach
        /// entirely and trigger a full state rebuild via ResetTreis().</summary>
        private static int _consecutiveRecoveryFailures = 0;

        /// <summary>After this many consecutive RecoverAsync failures, escalate to full ResetTreis.</summary>
        public const int ESCALATION_THRESHOLD = 3;

        /// <summary>DOWNLOAD-PHASE: Set to true while GetAllBlocks() is running inside RecoverAsync.
        /// When true, ValidateBlock() should allow blocks through even though IsRecoveryInProgress is set,
        /// because these blocks are the ones we're downloading as part of recovery.</summary>
        private static volatile bool _isInDownloadPhase = false;

        /// <summary>Returns true when the recovery is in its download+validate phase and blocks should be allowed through.</summary>
        public static bool IsInDownloadPhase => _isInDownloadPhase;

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
                // ═══════════════════════════════════════════════════════════════
                // ESCALATION CHECK: If we've failed recovery too many times,
                // the state trie is likely corrupted and rollback+download won't
                // help. Escalate to a full state rebuild via ResetTreis().
                // ═══════════════════════════════════════════════════════════════
                if (_consecutiveRecoveryFailures >= ESCALATION_THRESHOLD)
                {
                    LogUtility.Log(
                        $"[{caller}] FORK-RECOVERY-ESCALATION: {_consecutiveRecoveryFailures} consecutive " +
                        $"recovery failures. Rollback+download is not fixing the issue. " +
                        $"Escalating to full state rebuild via ResetTreis().",
                        $"{caller}.ForkRecovery");
                    ConsoleWriterService.Output(
                        $"[{caller}] FORK-RECOVERY-ESCALATION: State trie appears corrupted after " +
                        $"{_consecutiveRecoveryFailures} failed recoveries. Starting full state rebuild...");

                    _consecutiveRecoveryFailures = 0; // Reset before rebuild to prevent re-triggering

                    try
                    {
                        var rebuilt = await BlockRollbackUtility.ResetTreis();
                        if (rebuilt)
                        {
                            LogUtility.Log(
                                $"[{caller}] FORK-RECOVERY-ESCALATION: Full state rebuild SUCCEEDED. " +
                                $"Tip: height={Globals.LastBlock.Height}",
                                $"{caller}.ForkRecovery");
                            ConsoleWriterService.Output(
                                $"[{caller}] FORK-RECOVERY-ESCALATION: State rebuild complete. " +
                                $"Now at height {Globals.LastBlock.Height}.");
                            return true;
                        }
                        else
                        {
                            LogUtility.Log(
                                $"[{caller}] FORK-RECOVERY-ESCALATION: ResetTreis returned false. " +
                                $"State may still be inconsistent.",
                                $"{caller}.ForkRecovery");
                            return false;
                        }
                    }
                    catch (Exception resetEx)
                    {
                        LogUtility.Log(
                            $"[{caller}] FORK-RECOVERY-ESCALATION: ResetTreis threw exception: {resetEx.Message}",
                            $"{caller}.ForkRecovery");
                        return false;
                    }
                }

                LogUtility.Log(
                    $"[{caller}] FORK-RECOVERY: Initiating self-heal at height {stuckHeight}. " +
                    $"Rolling back {blocksToRollback} block(s). (Failure count: {_consecutiveRecoveryFailures}/{ESCALATION_THRESHOLD})",
                    $"{caller}.ForkRecovery");
                ConsoleWriterService.Output(
                    $"[{caller}] FORK-RECOVERY: Rolling back {blocksToRollback} block(s) from height {stuckHeight}...");

                // Manage IsResyncing at the OUTER level only.
                // Pass manageIsResyncing=false to RollbackBlocksFast so it doesn't
                // prematurely clear the flag before BlockDownloadService.GetAllBlocks() runs.
                var wasResyncing = Globals.IsResyncing;
                Globals.IsResyncing = true;

                try
                {
                    // Step 1: Capture pre-rollback state for verification
                    var preRollbackHash = Globals.LastBlock.Hash;
                    var preRollbackHeight = Globals.LastBlock.Height;

                    // Step 2: Roll back the bad block(s) using fast incremental path
                    // manageIsResyncing=false prevents RollbackBlocksFast from clearing
                    // IsResyncing in its finally block — we keep it set until all recovery
                    // steps complete.
                    var rollbackResult = await BlockRollbackUtility.RollbackBlocksFast(blocksToRollback, manageIsResyncing: false);
                    if (rollbackResult)
                    {
                        LogUtility.Log(
                            $"[{caller}] FORK-RECOVERY: Fast rollback succeeded. " +
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

                    // FORK-FIX: Clear all non-permanent bans before attempting block downloads.
                    // During forks, validators get banned for hash mismatches, and ReleasePeer()
                    // removes them from Globals.Nodes. This leaves the node with no peers to
                    // download blocks from, causing recovery to silently fail. By clearing bans
                    // first, we allow those peers to reconnect and serve blocks.
                    LogUtility.Log(
                        $"[{caller}] FORK-RECOVERY: Clearing bans to allow peer reconnection for block downloads...",
                        $"{caller}.ForkRecovery");
                    BanService.UnbanAllForForkRecovery();

                    // Give peers a moment to reconnect after unbanning
                    await Task.Delay(2000);

                    // Step 3: Download the correct blocks from peers
                    // CRITICAL FIX: We must temporarily clear IsResyncing and set the download
                    // phase flag so that ValidateBlock() allows the downloaded blocks through.
                    // Without this, ValidateBlock()'s recovery guard silently drops ALL blocks
                    // because IsResyncing=true, causing recovery to fail every time.
                    LogUtility.Log(
                        $"[{caller}] FORK-RECOVERY: Downloading correct blocks from peers...",
                        $"{caller}.ForkRecovery");
                    try 
                    { 
                        // Allow block validation during download by clearing IsResyncing
                        // and signaling we're in the download phase of recovery.
                        Globals.IsResyncing = false;
                        _isInDownloadPhase = true;
                        
                        await BlockDownloadService.GetAllBlocks(); 
                        LogUtility.Log(
                            $"[{caller}] FORK-RECOVERY: Block download completed. " +
                            $"New height={Globals.LastBlock.Height}",
                            $"{caller}.ForkRecovery");
                    } 
                    catch (Exception blockDownloadEx)
                    {
                        LogUtility.Log(
                            $"[{caller}] FORK-RECOVERY: Block download failed: {blockDownloadEx.Message}. " +
                            $"This may indicate no peers available or API issues.",
                            $"{caller}.ForkRecovery");
                    }
                    finally
                    {
                        // Restore IsResyncing and clear download phase flag
                        _isInDownloadPhase = false;
                        Globals.IsResyncing = true; // Re-set until the outer finally restores it
                    }

                    // Step 4: Reset stuck tracking
                    _lastObservedHeight = -1;
                    _stuckCycles = 0;

                    // Verify recovery actually succeeded.
                    // Check that:
                    //   a) Height has advanced past the stuck point, OR
                    //   b) Height is at the stuck point but the hash has changed (we got the correct block)
                    var postHeight = Globals.LastBlock.Height;
                    var postHash = Globals.LastBlock.Hash;

                    bool heightAdvanced = postHeight > stuckHeight;
                    bool hashChanged = postHeight >= stuckHeight && postHash != preRollbackHash;

                    success = heightAdvanced || hashChanged;

                    if (success)
                    {
                        // Recovery worked — reset failure escalation counter
                        _consecutiveRecoveryFailures = 0;
                    }
                    else
                    {
                        // Recovery failed — increment escalation counter
                        _consecutiveRecoveryFailures++;
                        LogUtility.Log(
                            $"[{caller}] FORK-RECOVERY: WARNING — recovery did not change chain state. " +
                            $"Pre: height={preRollbackHeight} hash={preRollbackHash?[..Math.Min(16, preRollbackHash?.Length ?? 0)]}. " +
                            $"Post: height={postHeight} hash={postHash?[..Math.Min(16, postHash?.Length ?? 0)]}. " +
                            $"Consecutive failures: {_consecutiveRecoveryFailures}/{ESCALATION_THRESHOLD}. " +
                            $"Will escalate to ResetTreis after {ESCALATION_THRESHOLD} failures.",
                            $"{caller}.ForkRecovery");
                        // Don't reset stuck tracking if recovery didn't actually help —
                        // let the next cycle detect the stuck state and retry.
                        _lastObservedHeight = postHeight;
                        _stuckCycles = 0; // Reset to avoid immediate re-trigger but will accumulate again
                    }

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
                    // Restore resyncing flag only after ALL recovery steps complete
                    Globals.IsResyncing = wasResyncing;
                }
            }
            catch (Exception ex)
            {
                LogUtility.Log(
                    $"[{caller}] FORK-RECOVERY: Exception during recovery: {ex.Message}",
                    $"{caller}.ForkRecovery");
                // Increment failure counter on exception too
                _consecutiveRecoveryFailures++;
                // Reset stuck tracking even on failure so we don't immediately retry
                _stuckCycles = 0;
            }
            finally
            {
                _isInDownloadPhase = false; // Safety: ensure download phase is always cleared
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
