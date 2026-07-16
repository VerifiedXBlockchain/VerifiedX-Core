using ReserveBlockCore.Data;
using ReserveBlockCore.Models;
using ReserveBlockCore.Services;

namespace ReserveBlockCore.Utilities
{
    /// <summary>
    /// Fast fork/crash recovery: restores all chain-derived state from the newest snapshot slot
    /// at or below the rollback target, replays the few local blocks between the snapshot height
    /// and the target through StateData.UpdateTreis, and resyncs the local wallet for the replayed
    /// range only. Seconds instead of the ~45-minute full genesis replay (ResetTreis), which
    /// remains the fallback whenever no usable slot covers the target or the restore fails.
    /// </summary>
    public static class SnapshotRestoreUtility
    {
        private static volatile bool _isRestoreRunning = false;

        /// <summary>Public accessor so block validation can hold off while a restore is in progress.</summary>
        public static bool IsRestoreRunning => _isRestoreRunning;

        /// <summary>
        /// Attempts a snapshot restore so that state matches the chain at <paramref name="targetHeight"/>.
        /// Blocks above the target are deleted (fork case — callers re-download the correct tip via
        /// BlockDownloadService.GetAllBlocks). For crash recovery pass the current tip height.
        /// Returns false when no usable slot covers the target or the restore/replay fails —
        /// callers should then fall back to BlockRollbackUtility.ResetTreis().
        /// </summary>
        /// <param name="targetHeight">Chain height state should be restored to (inclusive).</param>
        /// <param name="manageFlags">If true, sets/restores Globals.IsResyncing + StopAllTimers.
        /// Pass false when the caller manages those flags itself.</param>
        public static async Task<bool> TryRestoreAsync(long targetHeight, bool manageFlags = true)
        {
            if (_isRestoreRunning)
            {
                Console.WriteLine("[SnapshotRestore] BLOCKED: restore already running. Ignoring duplicate request.");
                return false;
            }
            if (BlockRollbackUtility.IsResetTreisRunning)
            {
                Console.WriteLine("[SnapshotRestore] BLOCKED: ResetTreis is running.");
                return false;
            }

            // Verified picker: a slot must not only sit at/below the rollback target, its recorded
            // block hash must match the local block store at that height. This rejects snapshots
            // taken on a fork branch that ran deeper than the detector's rollback target.
            var slot = StateSnapshotService.PickVerifiedSlotForRestore(targetHeight);
            if (slot == null)
            {
                LogUtility.Log($"[SnapshotRestore] No usable, anchor-verified snapshot slot at or below height {targetHeight} — caller should fall back to ResetTreis.", "SnapshotRestoreUtility");
                return false;
            }

            _isRestoreRunning = true;
            var wasResyncing = Globals.IsResyncing;
            var wasStopTimers = Globals.StopAllTimers;
            if (manageFlags)
            {
                Globals.IsResyncing = true;
                Globals.StopAllTimers = true;
            }

            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                Console.WriteLine($"[SnapshotRestore] Restoring state from slot {slot.SlotId} (height {slot.Height}) to target height {targetHeight}...");
                LogUtility.Log($"[SnapshotRestore] Start: slot={slot.SlotId} slotHeight={slot.Height} target={targetHeight}", "SnapshotRestoreUtility");

                // Mark state dirty for the duration — if the process dies mid-restore, the next
                // startup sees a not-synced state trie and runs recovery again (restore first,
                // ResetTreis as fallback). SetSynced at the end clears this.
                StateTreiStatusService.SetFailed($"Snapshot restore in progress (slot {slot.SlotId} → {targetHeight}).");

                // 1) Wipe all chain-derived state, then copy the slot back into the live collections.
                BlockRollbackUtility.WipeChainDerivedState();
                if (!StateSnapshotService.RestoreSlotToLive(slot.SlotId))
                {
                    ErrorLogUtility.LogError("[SnapshotRestore] Slot copy failed after wipe — state left dirty; caller must run ResetTreis.", "SnapshotRestoreUtility");
                    return false;
                }

                // 2) Local wallet transactions above the snapshot height are re-derived by the
                //    replay below (fork-removed ones disappear, kept ones are re-inserted).
                var walletTxs = TransactionData.GetAll();
                walletTxs.DeleteManySafe(x => x.Height > slot.Height);

                // 3) Mempool nonces/balances may no longer line up with restored state — clear it.
                try
                {
                    var mempool = TransactionData.GetPool();
                    if (mempool != null) mempool.DeleteAllSafe();
                }
                catch { }

                // 4) Fork case: drop blocks above the target. The re-download (caller) fetches the
                //    canonical replacements. Crash case: target == tip, nothing to delete.
                var blockStore = Block.GetBlocks();
                blockStore.DeleteManySafe(x => x.Height > targetHeight);
                DbContext.DB.Checkpoint();

                // 5) Replay local blocks (slotHeight, target] through the standard commit path.
                var walletAccounts = AccountData.GetAccounts().FindAll().ToList();
                var walletAddresses = new HashSet<string>(walletAccounts.Select(a => a.Address));
                long replayed = 0;
                long replayFails = 0;
                int walletTxInserts = 0;

                var replayBlocks = blockStore.Query()
                    .Where(x => x.Height > slot.Height && x.Height <= targetHeight)
                    .OrderBy(x => x.Height)
                    .ToEnumerable();

                foreach (var block in replayBlocks)
                {
                    try
                    {
                        var applied = await StateData.UpdateTreis(block);
                        if (!applied) replayFails++;
                        replayed++;

                        // Scoped equivalent of ResetTreis Step 4b: re-insert wallet-relevant TXs
                        // for the replayed range only.
                        if (walletAddresses.Count > 0)
                        {
                            foreach (var tx in block.Transactions)
                            {
                                if (walletAddresses.Contains(tx.ToAddress) || walletAddresses.Contains(tx.FromAddress))
                                {
                                    var existing = walletTxs.FindOne(x => x.Hash == tx.Hash);
                                    if (existing == null)
                                    {
                                        walletTxs.InsertSafe(tx);
                                        walletTxInserts++;
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        replayFails++;
                        ErrorLogUtility.LogError($"[SnapshotRestore] Error replaying block {block.Height}: {ex.Message}", "SnapshotRestoreUtility");
                    }
                }

                if (replayFails > 0)
                {
                    ErrorLogUtility.LogError($"[SnapshotRestore] Replay had {replayFails} failure(s) — state left dirty; caller must run ResetTreis.", "SnapshotRestoreUtility");
                    return false;
                }

                await ReserveService.Run(); //apply any reserve TXs unlocked as of the restored height

                // 6) Resync local wallet balances from the restored + replayed state trei.
                var accounts = AccountData.GetAccounts();
                foreach (var account in walletAccounts)
                {
                    var stateEntry = StateData.GetSpecificAccountStateTrei(account.Address);
                    account.Balance = stateEntry?.Balance ?? 0M;
                    accounts.UpdateSafe(account);
                }

                // 7) In-memory tip + queue cleanup, then flush everything.
                BlockRollbackUtility.RefreshInMemoryTip(targetHeight);
                await DbContext.CheckPoint();

                // 8) State is verified consistent up to the target. Slots newer than the target
                //    contain rolled-back blocks — invalidate them.
                StateTreiStatusService.SetSynced(targetHeight);
                StateSnapshotService.InvalidateAbove(targetHeight);

                sw.Stop();
                Console.WriteLine($"[SnapshotRestore] SUCCESS: restored to height {targetHeight} (replayed {replayed} block(s), {walletTxInserts} wallet TX(s)) in {sw.Elapsed.TotalSeconds:F1}s.");
                LogUtility.Log($"[SnapshotRestore] Success: slot={slot.SlotId} target={targetHeight} replayed={replayed} in {sw.ElapsedMilliseconds} ms", "SnapshotRestoreUtility");
                return true;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"[SnapshotRestore] CRITICAL: {ex}", "SnapshotRestoreUtility");
                // State may be partially restored — status stays failed so recovery re-runs.
                return false;
            }
            finally
            {
                _isRestoreRunning = false;
                if (manageFlags)
                {
                    Globals.IsResyncing = wasResyncing;
                    Globals.StopAllTimers = wasStopTimers;
                }
            }
        }
    }
}
