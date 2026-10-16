using Newtonsoft.Json.Linq;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Bitcoin.Services
{
    /// <summary>
    /// Rebuilds the vBTC withdrawal-request store (rsrvvbtcwithdrawalrequests.db) from chain data.
    ///
    /// The store is consensus-READ (owner balance add-back, active-request gates) but node-local
    /// WRITTEN — it only fills in while withdrawal transactions are processed live. A node whose
    /// Databases folder arrived by out-of-band file copy (the only "snapshot restore" that exists
    /// for full DB files) can boot with this store empty or partial, silently computing wrong
    /// owner balances AND skipping the consensus burn row in CompleteVBTCV2Withdrawal. Blocks are
    /// never pruned and embed full transaction payloads, so every mined withdrawal row is fully
    /// reconstructible from the block store.
    ///
    /// The rebuild is MERGE-ONLY by design: it inserts rows that are missing and applies terminal
    /// state transitions to rows that lack them, but never overwrites an existing row wholesale.
    /// VBTCWithdrawalRequest.Save(update:true) on a freshly-constructed object would null the
    /// FIND-028 pin fields (assigned unconditionally) and could downgrade Status — so existing
    /// rows are only ever mutated by loading them first. This also makes the rebuild idempotent
    /// and safe to run concurrently with live block processing: both writers converge on the same
    /// chain-derived state. Local-only rows (TransactionHash == "") and the local contract
    /// tracking on VBTCContractV2 are deliberately untouched.
    /// </summary>
    public static class VBTCWithdrawalStoreRebuildService
    {
        private const string REBUILD_META_COLLECTION = "rsrv_vbtcwd_rebuild_meta";

        /// <summary>Bounded back-scan window for on-the-fly request-row recovery, in blocks.
        /// A COMPLETE normally lands within EXPIRY_BLOCKS (360) of its REQUEST; stuck-UTXO flows
        /// can run longer, so the window is generous. ~20k blocks ≈ 2.7 days at 12s.</summary>
        private const long RECOVERY_SCAN_BLOCKS = 20_000;

        private static int _claimed = 0;   // one rebuild at a time
        private static int _running = 0;   // the store is being rewritten (block processing is held off meanwhile)
        private static long _started = 0;  // rebuilds that have begun rewriting the store

        /// <summary>
        /// The store is being rewritten, or a rewrite did not finish (it failed, or the node stopped during it) and rows
        /// it put back may not yet show how their withdrawals ended. Until a rebuild finishes, block validation refuses
        /// withdrawal transactions, nothing is signed from the store and no snapshot is taken of it.
        /// </summary>
        public static bool IsRunning => _running == 1 || Unfinished();

        /// <summary>How many rebuilds have begun rewriting the store (a snapshot copy compares it before and after).</summary>
        public static long RebuildsStarted => Interlocked.Read(ref _started);

        /// <summary>A rebuild is in progress (one at a time): what the operator endpoint and the status report show.</summary>
        public static bool InProgress => Volatile.Read(ref _claimed) == 1;

        /// <summary>A rebuild began rewriting the store and none has finished since (read error: taken as unfinished).</summary>
        public static bool IsUnfinished => Unfinished();

        private static bool Unfinished()
        {
            try { return GetMeta()?.Unfinished == true; }
            catch { return true; }
        }

        private static bool SetUnfinished(bool unfinished)
        {
            try
            {
                var db = GetMetaDb();
                if (db == null) return false;
                var meta = GetMeta() ?? new RebuildMeta();
                meta.Unfinished = unfinished;
                db.Upsert(meta);
                return true;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Withdrawal-store rebuild: could not record Unfinished={unfinished}: {ex.Message}", "VBTCWithdrawalStoreRebuildService.SetUnfinished()");
                return false;
            }
        }

        /// <summary>
        /// A state rebuild or a snapshot restore has rewritten this store from the chain and finished: a rebuild of it
        /// that did not finish no longer leaves rows unreconciled.
        /// </summary>
        public static void StoreRewrittenFromChain()
        {
            if (BlockRollbackUtility.WithdrawalStoreWiped) SetUnfinished(false);
        }
        internal static void SetRunningForTests(bool running) => Interlocked.Exchange(ref _running, running ? 1 : 0);
        internal static Func<long, bool>? FailAtBlockForTests;
        internal static Action<long>? BlockScannedForTests;
        public static RebuildResult? LastResult { get; private set; }

        public class RebuildMeta
        {
            public int Id { get; set; } = 1;
            /// <summary>A rebuild began rewriting the store and has not finished (see IsRunning).</summary>
            public bool Unfinished { get; set; }
            public long LastScanHeight { get; set; }
            public long LastScanUtc { get; set; }
            public int RequestsInserted { get; set; }
            public int CompletionsApplied { get; set; }
        }

        public class RebuildResult
        {
            public bool Success { get; set; }
            public string Message { get; set; } = "";
            public long BlocksScanned { get; set; }
            public long EndHeight { get; set; }
            public int RequestsInserted { get; set; }
            public int RequestsAlreadyPresent { get; set; }
            public int CompletionsApplied { get; set; }
            public int CancellationsCreated { get; set; }
            /// <summary>Rows on vaults the chain watches brought to the state the attestation table holds for them.</summary>
            public int AttestedOutcomesApplied { get; set; }
            public int DecidedCancellationsApplied { get; set; }
            public int VotesApplied { get; set; }
            public int Skipped { get; set; }
            public int Errors { get; set; }
            public long StartedUtc { get; set; }
            public long FinishedUtc { get; set; }
        }

        private static LiteDB.ILiteCollection<RebuildMeta>? GetMetaDb()
        {
            try
            {
                return DbContext.DB_VBTCWithdrawalRequests.GetCollection<RebuildMeta>(REBUILD_META_COLLECTION);
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError(ex.ToString(), "VBTCWithdrawalStoreRebuildService.GetMetaDb()");
                return null;
            }
        }

        public static RebuildMeta? GetMeta() => GetMetaDb()?.FindById(1);

        /// <summary>
        /// Startup probe: when the store has no mined rows and no rebuild has ever been recorded,
        /// scan the chain once. A fresh node scans a near-empty block store (cheap, then the marker
        /// suppresses re-scans); a node that lost the store file also lost the marker (same file),
        /// so the loss itself re-arms the probe.
        /// </summary>
        public static async Task MaybeAutoRebuildOnStartupAsync()
        {
            try
            {
                var vwrDb = VBTCWithdrawalRequest.GetVBTCWithdrawalRequestDb();
                if (vwrDb == null)
                    return;

                // A rebuild that did not finish is run again: until one does, the store is treated as being rebuilt.
                if (GetMeta()?.Unfinished == true)
                {
                    LogUtility.Log("vBTC withdrawal store: the last rebuild did not finish — rebuilding from chain.", "VBTCWithdrawalStoreRebuildService.MaybeAutoRebuildOnStartupAsync()");
                    var resumed = await RebuildFromChainAsync("startup: last rebuild did not finish");
                    LogUtility.Log($"vBTC withdrawal store startup rebuild finished: {resumed.Message}", "VBTCWithdrawalStoreRebuildService.MaybeAutoRebuildOnStartupAsync()");
                    return;
                }

                var minedRowCount = vwrDb.Query().Where(x => x.TransactionHash != "").Count();
                if (minedRowCount > 0)
                    return;

                if (GetMeta() != null)
                    return;

                LogUtility.Log("vBTC withdrawal store has no mined rows and no rebuild marker — rebuilding from chain.", "VBTCWithdrawalStoreRebuildService.MaybeAutoRebuildOnStartupAsync()");
                var result = await RebuildFromChainAsync("startup: empty store probe");
                LogUtility.Log($"vBTC withdrawal store startup rebuild finished: {result.Message}", "VBTCWithdrawalStoreRebuildService.MaybeAutoRebuildOnStartupAsync()");
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Startup withdrawal-store probe failed: {ex.Message}", "VBTCWithdrawalStoreRebuildService.MaybeAutoRebuildOnStartupAsync()");
            }
        }

        /// <summary>
        /// Streams every block ascending and merge-replays the four withdrawal transaction types
        /// into the store. Idempotent; never destructive.
        /// It runs holding the lock block validation, its commit and the state snapshots run under, so none of them
        /// reads (or copies) a row it has put back before the scan has brought it to how its withdrawal ended: blocks
        /// wait until it finishes. It does not run alongside a state rebuild or a snapshot restore (both rewrite this
        /// store), and stops if one starts.
        /// </summary>
        public static async Task<RebuildResult> RebuildFromChainAsync(string reason)
        {
            var result = new RebuildResult { StartedUtc = TimeUtil.GetTime() };

            if (Interlocked.CompareExchange(ref _claimed, 1, 0) != 0)
            {
                result.Message = "A rebuild is already running.";
                return result;
            }

            var holdingBlocks = false;
            try
            {
                if (StateBeingRewritten())
                {
                    result.Message = "A state rebuild or snapshot restore is running; rebuild the withdrawal store after it.";
                    return result;
                }
                await VerifiedXCore.Services.BlockValidatorService.ValidateBlockSemaphore.WaitAsync();
                holdingBlocks = true;
                if (StateBeingRewritten())
                {
                    result.Message = "A state rebuild or snapshot restore started; rebuild the withdrawal store after it.";
                    return result;
                }
                Interlocked.Increment(ref _started);
                Interlocked.Exchange(ref _running, 1);
                // Before the first row is written: a failure or a stop from here leaves it set until a rebuild (or a state
                // rebuild or restore) finishes. Not recorded, nothing is written.
                if (!SetUnfinished(true))
                {
                    result.Message = "The withdrawal store's rebuild marker could not be written; nothing was changed.";
                    ErrorLogUtility.LogError(result.Message, "VBTCWithdrawalStoreRebuildService.RebuildFromChainAsync()");
                    return result;
                }
                LogUtility.Log($"vBTC withdrawal store rebuild starting ({reason}); block processing waits until it finishes.", "VBTCWithdrawalStoreRebuildService.RebuildFromChainAsync()");

                var blocks = BlockchainData.GetBlocks();
                foreach (var block in blocks.Find(LiteDB.Query.All("Height", LiteDB.Query.Ascending)))
                {
                    if (StateBeingRewritten())
                    {
                        // The state rebuild or restore rewrites this store from the chain itself; it clears the marker when
                        // it finishes (StoreRewrittenFromChain).
                        result.Message = $"Stopped at height {block.Height}: a state rebuild or snapshot restore started, which rewrites the store.";
                        ErrorLogUtility.LogError(result.Message, "VBTCWithdrawalStoreRebuildService.RebuildFromChainAsync()");
                        return result;
                    }
                    result.BlocksScanned++;
                    result.EndHeight = block.Height;
                    BlockScannedForTests?.Invoke(block.Height);
                    if (FailAtBlockForTests?.Invoke(block.Height) == true) throw new InvalidOperationException("test failure");

                    if (result.BlocksScanned % 100_000 == 0)
                        LogUtility.Log($"vBTC withdrawal store rebuild progress: {result.BlocksScanned} blocks scanned (height {block.Height}).", "VBTCWithdrawalStoreRebuildService.RebuildFromChainAsync()");

                    if (block.Transactions == null)
                        continue;

                    foreach (var tx in block.Transactions)
                    {
                        try
                        {
                            switch (tx.TransactionType)
                            {
                                case TransactionType.VBTC_V2_WITHDRAWAL_REQUEST:
                                    ReplayRequest(tx, result);
                                    break;
                                case TransactionType.VBTC_V2_WITHDRAWAL_COMPLETE:
                                    ReplayComplete(tx, result);
                                    break;
                                case TransactionType.VBTC_V2_WITHDRAWAL_CANCEL:
                                    ReplayCancel(tx, result);
                                    break;
                                case TransactionType.VBTC_V2_WITHDRAWAL_VOTE:
                                    ReplayVote(tx, result);
                                    break;
                            }
                        }
                        catch (Exception txEx) when (!StateData.IsNodeLocalFault(txEx))
                        {
                            // What the transaction carries could not be applied (a field of the wrong shape, ...): live
                            // processing could not apply it either and skipped it the same way (StateData.IsNodeLocalFault).
                            result.Skipped++;
                            LogUtility.Log($"Withdrawal-store rebuild: tx {tx.Hash} at height {block.Height} skipped ({txEx.GetType().Name}: {txEx.Message}).", "VBTCWithdrawalStoreRebuildService.RebuildFromChainAsync()");
                        }
                        catch (Exception txEx)
                        {
                            result.Errors++;
                            ErrorLogUtility.LogError($"Withdrawal-store rebuild: tx {tx.Hash} at height {block.Height} failed: {txEx.Message}", "VBTCWithdrawalStoreRebuildService.RebuildFromChainAsync()");
                        }
                    }

                    // Yield periodically so a large scan does not monopolize the thread.
                    if (result.BlocksScanned % 10_000 == 0)
                        await Task.Delay(1);
                }

                ApplyAttestedOutcomes(result);
                ApplyDecidedCancellations(result);

                var meta = GetMeta() ?? new RebuildMeta();
                // A row this scan could not write or reconcile may still read open: the store stays unfinished.
                meta.Unfinished = result.Errors > 0;
                meta.LastScanHeight = result.EndHeight;
                meta.LastScanUtc = TimeUtil.GetTime();
                meta.RequestsInserted += result.RequestsInserted;
                meta.CompletionsApplied += result.CompletionsApplied;
                GetMetaDb()?.Upsert(meta);

                result.Success = result.Errors == 0;
                result.FinishedUtc = TimeUtil.GetTime();
                result.Message = $"Scanned {result.BlocksScanned} blocks to height {result.EndHeight}: " +
                                 $"{result.RequestsInserted} requests inserted, {result.RequestsAlreadyPresent} already present, " +
                                 $"{result.CompletionsApplied} completions applied, {result.AttestedOutcomesApplied} attested outcomes applied, {result.DecidedCancellationsApplied} decided cancellations applied, {result.CancellationsCreated} cancellations created, " +
                                 $"{result.VotesApplied} votes applied, {result.Skipped} skipped, {result.Errors} errors." +
                                 (result.Errors > 0 ? " The store is left unfinished: withdrawal transactions are refused and nothing is signed from it until a rebuild finishes without errors." : "");
                LogUtility.Log($"vBTC withdrawal store rebuild finished: {result.Message}", "VBTCWithdrawalStoreRebuildService.RebuildFromChainAsync()");
            }
            catch (Exception ex)
            {
                result.Message = $"Rebuild failed: {ex.Message}. The withdrawal store is left unfinished: withdrawal transactions are refused and nothing is signed from it until a rebuild finishes (POST /frost/withdrawals/rebuild, or the next start).";
                result.FinishedUtc = TimeUtil.GetTime();
                ErrorLogUtility.LogError(result.Message, "VBTCWithdrawalStoreRebuildService.RebuildFromChainAsync()");
            }
            finally
            {
                LastResult = result;
                Interlocked.Exchange(ref _running, 0);
                if (holdingBlocks) VerifiedXCore.Services.BlockValidatorService.ValidateBlockSemaphore.Release();
                Interlocked.Exchange(ref _claimed, 0);
            }

            return result;
        }

        private static bool StateBeingRewritten() => BlockRollbackUtility.IsResetTreisRunning || SnapshotRestoreUtility.IsRestoreRunning;

        /// <summary>
        /// A state rebuild or snapshot restore, its own flag raised, waits here before it rewrites this store: a running
        /// withdrawal-store rebuild sees the flag and stops at its next block, so the two never write the store at once.
        /// </summary>
        internal static async Task WaitUntilStoppedAsync()
        {
            while (Volatile.Read(ref _running) == 1)
                await Task.Delay(50);
        }

        /// <summary>
        /// On-the-fly recovery for StateData.CompleteVBTCV2Withdrawal: the local store is missing
        /// the request row a mined COMPLETE refers to, which on an unhealed node would silently
        /// skip the consensus burn row. The REQUEST tx is on chain — back-scan a bounded window of
        /// blocks below the COMPLETE's height, reconstruct the row, and return it so completion
        /// (including the burn) can proceed exactly as on healthy nodes.
        ///
        /// From Globals.PathAuditRulesHeight it adds only the rows that are missing. It used to save every row of the
        /// request over whatever the store held, so a completion that named the request's hash under ANOTHER contract
        /// (no row there, hence "missing") rewrote the request's real row as freshly requested: a paid or cancelled
        /// withdrawal became open again, to be cancelled and refunded a second time, and an owner lost the credit of a
        /// settled one (second path audit, 14 October 2026).
        /// </summary>
        public static VBTCWithdrawalRequest? TryRecoverRequestRowFromChain(string requestTxHash, long anchorHeight, string? scUID = null)
        {
            try
            {
                if (string.IsNullOrEmpty(requestTxHash))
                    return null;

                var toHeight = anchorHeight > 0 ? anchorHeight : (Globals.LastBlock?.Height ?? 0);
                var fromHeight = Math.Max(0, toHeight - RECOVERY_SCAN_BLOCKS);

                var blocks = BlockchainData.GetBlocks();
                foreach (var block in blocks.Find(LiteDB.Query.Between("Height", fromHeight, toHeight)))
                {
                    if (block.Transactions == null)
                        continue;

                    foreach (var tx in block.Transactions)
                    {
                        if (tx.TransactionType != TransactionType.VBTC_V2_WITHDRAWAL_REQUEST || tx.Hash != requestTxHash)
                            continue;

                        // A multi-contract REQUEST yields one row per input; all are restored so
                        // the node converges fully, and the caller's contract row is returned.
                        if (!TryBuildRequestRows(tx, out var rows) || rows == null || rows.Count == 0)
                            return null;

                        var missingOnly = toHeight >= Globals.PathAuditRulesHeight;
                        var saved = 0;
                        foreach (var row in rows)
                        {
                            if (missingOnly && VBTCWithdrawalRequest.GetByTransactionHash(row.TransactionHash, row.SmartContractUID) != null)
                                continue; // this row is here: what it has become since its request is never written over
                            // (Nor is the row of another mined request that carries the same UniqueId: Save falls back to
                            // that key, and used to write this request over it.)
                            if (VBTCWithdrawalRequest.Save(row, update: true, neverOverAnotherRequest: missingOnly)) saved++;
                        }

                        if (saved == 0 && !missingOnly)
                            return null;

                        if (saved > 0)
                            LogUtility.Log($"Recovered {saved} missing withdrawal request row(s) for {requestTxHash} from chain (height {block.Height}).", "VBTCWithdrawalStoreRebuildService.TryRecoverRequestRowFromChain()");
                        return VBTCWithdrawalRequest.GetByTransactionHash(requestTxHash, scUID);
                    }
                }

                ErrorLogUtility.LogError($"Could not recover withdrawal request {requestTxHash} within {RECOVERY_SCAN_BLOCKS} blocks below height {toHeight}. Run POST /frost/withdrawals/rebuild to heal the store from the full chain.", "VBTCWithdrawalStoreRebuildService.TryRecoverRequestRowFromChain()");
                return null;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Request-row recovery failed for {requestTxHash}: {ex.Message}", "VBTCWithdrawalStoreRebuildService.TryRecoverRequestRowFromChain()");
                return null;
            }
        }

        /// <summary>
        /// Mirrors StateData.RequestVBTCV2Withdrawal's row construction exactly (requestor bound to
        /// tx.FromAddress, UniqueId falls back to tx.Hash, mined height stamped). Keep the two in
        /// sync — this is the consensus-visible shape of a mined request row.
        /// </summary>
        public static bool TryBuildRequestRow(Transaction tx, out VBTCWithdrawalRequest? row)
        {
            row = null;
            if (!TryBuildRequestRows(tx, out var rows) || rows == null || rows.Count == 0)
                return false;

            row = rows[0];
            return true;
        }

        /// <summary>
        /// Every row a mined REQUEST tx produces: one for the single shape, one per input for the
        /// multi shape (Function "VBTCWithdrawalRequestMultiV2()"), dispatched on Function alone
        /// exactly as StateData does.
        /// Keep in sync with StateData.RequestVBTCV2Withdrawal / RequestVBTCV2WithdrawalMulti —
        /// this is the consensus-visible shape of mined request rows.
        /// </summary>
        public static bool TryBuildRequestRows(Transaction tx, out List<VBTCWithdrawalRequest>? rows)
        {
            rows = null;
            if (string.IsNullOrEmpty(tx.Data))
                return false;

            var jobjMulti = JObject.Parse(tx.Data);
            if (jobjMulti["Function"]?.ToObject<string?>() == VBTCService.MultiWithdrawalFunction)
            {
                return TryBuildMultiRequestRows(tx, jobjMulti, out rows);
            }

            if (!TryBuildSingleRequestRow(tx, jobjMulti, out var single) || single == null)
                return false;

            rows = new List<VBTCWithdrawalRequest> { single };
            return true;
        }

        private static bool TryBuildMultiRequestRows(Transaction tx, JObject jobj, out List<VBTCWithdrawalRequest>? rows)
        {
            rows = null;

            var btcAddress = jobj["BTCAddress"]?.ToObject<string?>();
            var feeRate = jobj["FeeRate"]?.ToObject<int?>();
            var uniqueId = jobj["UniqueId"]?.ToObject<string?>() ?? tx.Hash;
            var originalRequestTime = jobj["OriginalRequestTime"]?.ToObject<long?>() ?? tx.Timestamp;
            var originalSignature = jobj["OriginalSignature"]?.ToObject<string?>() ?? "";
            var inputs = jobj["Inputs"]?.ToObject<List<VBTCV2MultiWithdrawalInput>?>();

            if (string.IsNullOrEmpty(btcAddress) || !feeRate.HasValue || inputs == null || inputs.Count == 0)
                return false;

            var built = new List<VBTCWithdrawalRequest>();
            foreach (var input in inputs)
            {
                if (string.IsNullOrEmpty(input.SCUID) || input.Amount <= 0)
                    continue;

                built.Add(new VBTCWithdrawalRequest
                {
                    RequestorAddress = tx.FromAddress,
                    SmartContractUID = input.SCUID,
                    Amount = input.Amount,
                    BTCDestination = btcAddress,
                    FeeRate = feeRate.Value,
                    OriginalUniqueId = uniqueId,
                    OriginalRequestTime = originalRequestTime,
                    OriginalSignature = originalSignature,
                    Timestamp = tx.Timestamp,
                    TransactionHash = tx.Hash,
                    Status = VBTCWithdrawalStatus.Requested,
                    IsCompleted = false,
                    RequestBlockHeight = tx.Height
                });
            }

            if (built.Count == 0)
                return false;

            rows = built;
            return true;
        }

        private static bool TryBuildSingleRequestRow(Transaction tx, JObject jobj, out VBTCWithdrawalRequest? row)
        {
            row = null;
            var scUID = jobj["ContractUID"]?.ToObject<string?>();
            var btcAddress = jobj["BTCAddress"]?.ToObject<string?>();
            var amount = jobj["Amount"]?.ToObject<decimal?>();
            var feeRate = jobj["FeeRate"]?.ToObject<int?>();
            var uniqueId = jobj["UniqueId"]?.ToObject<string?>() ?? tx.Hash;
            var originalRequestTime = jobj["OriginalRequestTime"]?.ToObject<long?>() ?? tx.Timestamp;
            var originalSignature = jobj["OriginalSignature"]?.ToObject<string?>() ?? "";

            if (string.IsNullOrEmpty(scUID) || !amount.HasValue || !feeRate.HasValue || string.IsNullOrEmpty(btcAddress))
                return false;

            row = new VBTCWithdrawalRequest
            {
                RequestorAddress = tx.FromAddress,
                SmartContractUID = scUID!,
                Amount = amount.Value,
                BTCDestination = btcAddress,
                FeeRate = feeRate.Value,
                OriginalUniqueId = uniqueId,
                OriginalRequestTime = originalRequestTime,
                OriginalSignature = originalSignature,
                Timestamp = tx.Timestamp,
                TransactionHash = tx.Hash,
                Status = VBTCWithdrawalStatus.Requested,
                IsCompleted = false,
                RequestBlockHeight = tx.Height
            };
            return true;
        }

        private static void ReplayRequest(Transaction tx, RebuildResult result)
        {
            if (!TryBuildRequestRows(tx, out var rows) || rows == null || rows.Count == 0)
            {
                result.Skipped++;
                return;
            }

            // Per contract, not per TX: a multi-contract request may be half-present after a
            // partial store loss, so each missing row is restored on its own.
            foreach (var row in rows)
            {
                if (VBTCWithdrawalRequest.GetByTransactionHash(tx.Hash, row.SmartContractUID) != null)
                {
                    result.RequestsAlreadyPresent++;
                    continue;
                }

                // Only rows live processing wrote (StateData.RequestVBTCV2Withdrawal / ...Multi): a non-positive amount
                // never makes one (VX-01); on a vault the chain watches the row is written only after its escrow debit,
                // and every row written there is then recorded in the attestation table, so a request without that
                // record never had a row.
                if (row.Amount <= 0M
                    || (Attestation.BtcAttestBalance.IsAttestedVault(row.SmartContractUID)
                        && Attestation.BtcAttestStore.Withdrawal(Attestation.BtcWithdrawals.RefOf(tx.Hash, row.SmartContractUID)) == null))
                {
                    result.Skipped++;
                    continue;
                }

                // update:true matches live processing — it upgrades an API node's local
                // pre-registration row (same composite key, empty TransactionHash, no pins yet).
                if (VBTCWithdrawalRequest.Save(row, update: true))
                    result.RequestsInserted++;
                else
                    result.Errors++;
            }
        }

        private static void ReplayComplete(Transaction tx, RebuildResult result)
        {
            if (string.IsNullOrEmpty(tx.Data)) { result.Skipped++; return; }
            var jobj = JObject.Parse(tx.Data);
            var completeScUID = jobj["ContractUID"]?.ToObject<string?>();
            var withdrawalRequestHash = jobj["WithdrawalRequestHash"]?.ToObject<string?>();
            var btcTxHash = jobj["BTCTransactionHash"]?.ToObject<string?>();
            if (string.IsNullOrEmpty(withdrawalRequestHash) || string.IsNullOrEmpty(btcTxHash)) { result.Skipped++; return; }

            // Contract-scoped: one COMPLETE finalizes one contract's share of the request.
            var row = VBTCWithdrawalRequest.GetByTransactionHash(withdrawalRequestHash, completeScUID);
            if (row == null)
            {
                // REQUEST replays before COMPLETE in the same ascending scan; a miss means the
                // request tx itself was malformed. Nothing to converge on (live processing found no row either).
                result.Skipped++;
                ErrorLogUtility.LogError($"Withdrawal-store rebuild: COMPLETE {tx.Hash} references unknown request {withdrawalRequestHash}.", "VBTCWithdrawalStoreRebuildService.ReplayComplete()");
                return;
            }

            if (row.IsCompleted) { result.Skipped++; return; }

            // Same guards as StateData.CompleteVBTCV2Withdrawal.
            if (row.RequestorAddress != tx.FromAddress || row.Amount <= 0.0M) { result.Skipped++; return; }

            // Mutate the LOADED row so FIND-028 pin fields and local observability carry through.
            row.Status = VBTCWithdrawalStatus.Completed;
            row.IsCompleted = true;
            row.BTCTxHash = btcTxHash;
            if (VBTCWithdrawalRequest.Save(row, update: true))
                result.CompletionsApplied++;
            else
                result.Errors++;
        }

        /// <summary>
        /// A withdrawal on a vault the chain watches is not ended by a transaction this scan sees: Bitcoin completes
        /// it (a report), or the chain closes it by rule (void, expiry) or at once on its requester's cancel. All of
        /// that is in the attestation table, written from blocks like this store. A row this scan has just put back is
        /// "requested"; here it is brought to where the attestation table says its withdrawal ended. Merge-only like
        /// the rest: a row is loaded, changed and saved; one that already agrees is left alone.
        /// </summary>
        private static void ApplyAttestedOutcomes(RebuildResult result)
        {
            try
            {
                var closed = new[] { Attestation.VaultWithdrawalState.Settled, Attestation.VaultWithdrawalState.Void, Attestation.VaultWithdrawalState.Expired, Attestation.VaultWithdrawalState.Cancelled, Attestation.VaultWithdrawalState.Released };
                foreach (var attested in closed.SelectMany(state => Attestation.BtcAttestStore.WithdrawalsInState(state)).Where(w => !w.IsBridgeExit))
                {
                    var row = VBTCWithdrawalRequest.GetByTransactionHash(attested.RequestHash, attested.SmartContractUID);
                    if (row == null) continue;
                    var paid = attested.State == Attestation.VaultWithdrawalState.Settled;
                    var status = paid ? VBTCWithdrawalStatus.Completed : VBTCWithdrawalStatus.Cancelled;
                    if (row.IsCompleted && row.Status == status && (!paid || row.BTCTxHash == attested.SettledTxid)) continue;
                    row.Status = status;
                    row.IsCompleted = true;
                    row.BTCTxHash = paid ? attested.SettledTxid : row.BTCTxHash;
                    if (VBTCWithdrawalRequest.Save(row, update: true))
                        result.AttestedOutcomesApplied++;
                    else
                        result.Errors++;
                }
            }
            catch (Exception ex)
            {
                result.Errors++;
                ErrorLogUtility.LogError($"Withdrawal-store rebuild: applying the attested outcomes failed: {ex.Message}", "VBTCWithdrawalStoreRebuildService.ApplyAttestedOutcomes()");
            }
        }

        /// <summary>
        /// A cancellation this store already holds as approved has refunded its withdrawal, and the votes that decided it
        /// are skipped when replayed (the record is decided). A request row this scan has just put back for it reads
        /// "requested"; here it is closed as cancelled, as the deciding vote closed it. Merge-only: a row already closed
        /// (a burn-backed Completed row included) is left alone.
        /// </summary>
        private static void ApplyDecidedCancellations(RebuildResult result)
        {
            try
            {
                foreach (var cancellation in (VBTCWithdrawalCancellation.GetAllCancellations() ?? new List<VBTCWithdrawalCancellation>()).Where(c => c.IsProcessed && c.IsApproved))
                {
                    var row = VBTCWithdrawalRequest.GetByTransactionHash(cancellation.WithdrawalRequestHash, cancellation.SmartContractUID);
                    if (row == null || row.IsCompleted) continue;
                    row.Status = VBTCWithdrawalStatus.Cancelled;
                    row.IsCompleted = true;
                    if (VBTCWithdrawalRequest.Save(row, update: true))
                        result.DecidedCancellationsApplied++;
                    else
                        result.Errors++;
                }
            }
            catch (Exception ex)
            {
                result.Errors++;
                ErrorLogUtility.LogError($"Withdrawal-store rebuild: applying the decided cancellations failed: {ex.Message}", "VBTCWithdrawalStoreRebuildService.ApplyDecidedCancellations()");
            }
        }

        private static void ReplayCancel(Transaction tx, RebuildResult result)
        {
            if (string.IsNullOrEmpty(tx.Data)) { result.Skipped++; return; }

            // A vault the chain watches holds no cancellation vote: its cancel took effect when mined, and the outcome
            // is applied from the attestation table (ApplyAttestedOutcomes). No cancellation record is created for it.
            string? cancelledOn = null;
            try { cancelledOn = JObject.Parse(tx.Data)["ContractUID"]?.ToObject<string?>(); } catch { }
            if (Attestation.BtcAttestBalance.IsAttestedVault(SmartContractStateTrei.GetSmartContractState(cancelledOn ?? "")?.SmartContractUID ?? cancelledOn))
            {
                result.Skipped++;
                return;
            }

            // VbtcCancellationVoteRulesHeight: the same method live processing uses (skips a record that exists).
            if (VBTCCancellationVoting.RulesActive(tx.Height))
            {
                if (VBTCCancellationVoting.ApplyCancel(tx, out _)) result.CancellationsCreated++; else result.Skipped++;
                return;
            }

            var jobj = JObject.Parse(tx.Data);
            var scUID = jobj["ContractUID"]?.ToObject<string?>();
            var withdrawalRequestHash = jobj["WithdrawalRequestHash"]?.ToObject<string?>();
            var failureProof = jobj["FailureProof"]?.ToObject<string?>() ?? "";
            if (string.IsNullOrEmpty(scUID) || string.IsNullOrEmpty(withdrawalRequestHash)) { result.Skipped++; return; }

            var row = VBTCWithdrawalRequest.GetByTransactionHash(withdrawalRequestHash, scUID);
            if (row == null || row.RequestorAddress != tx.FromAddress || row.IsCompleted) { result.Skipped++; return; }

            if (VBTCWithdrawalCancellation.GetCancellationByWithdrawalHash(withdrawalRequestHash, scUID) != null) { result.Skipped++; return; }

            VBTCWithdrawalCancellation.SaveCancellation(new VBTCWithdrawalCancellation
            {
                CancellationUID = $"CANCEL_{tx.Hash}",
                SmartContractUID = scUID,
                OwnerAddress = tx.FromAddress,
                WithdrawalRequestHash = withdrawalRequestHash,
                BTCTxHash = "",
                FailureProof = failureProof,
                RequestTime = tx.Timestamp,
                ValidatorVotes = new Dictionary<string, bool>(),
                ApproveCount = 0,
                RejectCount = 0,
                IsApproved = false,
                IsProcessed = false
            });
            result.CancellationsCreated++;
        }

        private static void ReplayVote(Transaction tx, RebuildResult result)
        {
            if (string.IsNullOrEmpty(tx.Data)) { result.Skipped++; return; }

            // VbtcCancellationVoteRulesHeight: the same method live processing uses. Voters are read at the vote's own
            // height from committed blocks, so a replay years later decides as the network did. The escrow refund is a
            // ledger row, written by StateData for the deciding vote whoever counted it first (RefundDueFor).
            if (VBTCCancellationVoting.RulesActive(tx.Height))
            {
                if (VBTCCancellationVoting.ApplyVote(tx).Applied) result.VotesApplied++; else result.Skipped++;
                return;
            }

            var jobj = JObject.Parse(tx.Data);
            var cancellationUID = jobj["CancellationUID"]?.ToObject<string?>();
            var approve = jobj["Approve"]?.ToObject<bool?>() ?? false;
            if (string.IsNullOrEmpty(cancellationUID)) { result.Skipped++; return; }

            var cancellation = VBTCWithdrawalCancellation.GetCancellation(cancellationUID);
            if (cancellation == null || cancellation.IsProcessed) { result.Skipped++; return; }

            // Same eligibility as live processing (StateData.VoteOnVBTCV2Cancellation): the voter set and the voter as of
            // the vote's own height, read from committed blocks, so a replay decides as the network did then.
            var voterSet = VBTCService.ResolveCancellationVoterSet(cancellation.SmartContractUID, tx.Height);
            var validator = VBTCValidatorRegistry.GetValidatorAt(tx.FromAddress, tx.Height);
            if (validator == null || !validator.IsActive || !voterSet.Contains(tx.FromAddress)) { result.Skipped++; return; }

            if (VBTCWithdrawalCancellation.HasValidatorVoted(cancellationUID, tx.FromAddress)) { result.Skipped++; return; }

            VBTCWithdrawalCancellation.AddVote(cancellationUID, tx.FromAddress, approve);
            result.VotesApplied++;

            var totalValidatorCount = voterSet.Count;
            if (totalValidatorCount <= 0)
                return;

            cancellation = VBTCWithdrawalCancellation.GetCancellation(cancellationUID);
            if (cancellation == null)
                return;

            var approvalPercentage = (int)((double)cancellation.ApproveCount / totalValidatorCount * 100);
            if (approvalPercentage >= 75 && !cancellation.IsProcessed)
            {
                VBTCWithdrawalCancellation.MarkAsProcessed(cancellationUID, true);

                var row = VBTCWithdrawalRequest.GetByTransactionHash(cancellation.WithdrawalRequestHash, cancellation.SmartContractUID);
                // Mirror of the completed-guard in StateData.VoteOnVBTCV2Cancellation, at the vote's height: from
                // V2WithdrawalOwnerAddBackFixHeight a burn-backed Completed row never flips to Cancelled.
                var voteFixActive = tx.Height >= Globals.V2WithdrawalOwnerAddBackFixHeight;
                if (row != null && !(voteFixActive && row.IsCompleted && row.Status == VBTCWithdrawalStatus.Completed))
                {
                    row.Status = VBTCWithdrawalStatus.Cancelled;
                    row.IsCompleted = true;
                    VBTCWithdrawalRequest.Save(row, update: true);
                }
            }
        }
    }
}
