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

        private static int _running = 0;
        public static bool IsRunning => _running == 1;
        public static RebuildResult? LastResult { get; private set; }

        public class RebuildMeta
        {
            public int Id { get; set; } = 1;
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
        /// into the store. Idempotent; never destructive; safe alongside live processing.
        /// </summary>
        public static async Task<RebuildResult> RebuildFromChainAsync(string reason)
        {
            var result = new RebuildResult { StartedUtc = TimeUtil.GetTime() };

            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            {
                result.Message = "A rebuild is already running.";
                return result;
            }

            try
            {
                LogUtility.Log($"vBTC withdrawal store rebuild starting ({reason}).", "VBTCWithdrawalStoreRebuildService.RebuildFromChainAsync()");

                var blocks = BlockchainData.GetBlocks();
                foreach (var block in blocks.Find(LiteDB.Query.All("Height", LiteDB.Query.Ascending)))
                {
                    result.BlocksScanned++;
                    result.EndHeight = block.Height;

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

                var meta = GetMeta() ?? new RebuildMeta();
                meta.LastScanHeight = result.EndHeight;
                meta.LastScanUtc = TimeUtil.GetTime();
                meta.RequestsInserted += result.RequestsInserted;
                meta.CompletionsApplied += result.CompletionsApplied;
                GetMetaDb()?.Upsert(meta);

                result.Success = true;
                result.FinishedUtc = TimeUtil.GetTime();
                result.Message = $"Scanned {result.BlocksScanned} blocks to height {result.EndHeight}: " +
                                 $"{result.RequestsInserted} requests inserted, {result.RequestsAlreadyPresent} already present, " +
                                 $"{result.CompletionsApplied} completions applied, {result.CancellationsCreated} cancellations created, " +
                                 $"{result.VotesApplied} votes applied, {result.Skipped} skipped, {result.Errors} errors.";
                LogUtility.Log($"vBTC withdrawal store rebuild finished: {result.Message}", "VBTCWithdrawalStoreRebuildService.RebuildFromChainAsync()");
            }
            catch (Exception ex)
            {
                result.Message = $"Rebuild failed: {ex.Message}";
                result.FinishedUtc = TimeUtil.GetTime();
                ErrorLogUtility.LogError(result.Message, "VBTCWithdrawalStoreRebuildService.RebuildFromChainAsync()");
            }
            finally
            {
                LastResult = result;
                Interlocked.Exchange(ref _running, 0);
            }

            return result;
        }

        /// <summary>
        /// On-the-fly recovery for StateData.CompleteVBTCV2Withdrawal: the local store is missing
        /// the request row a mined COMPLETE refers to, which on an unhealed node would silently
        /// skip the consensus burn row. The REQUEST tx is on chain — back-scan a bounded window of
        /// blocks below the COMPLETE's height, reconstruct the row, and return it so completion
        /// (including the burn) can proceed exactly as on healthy nodes.
        /// </summary>
        public static VBTCWithdrawalRequest? TryRecoverRequestRowFromChain(string requestTxHash, long anchorHeight)
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

                        if (!TryBuildRequestRow(tx, out var row) || row == null)
                            return null;

                        if (!VBTCWithdrawalRequest.Save(row, update: true))
                            return null;

                        LogUtility.Log($"Recovered missing withdrawal request row {requestTxHash} from chain (height {block.Height}).", "VBTCWithdrawalStoreRebuildService.TryRecoverRequestRowFromChain()");
                        return VBTCWithdrawalRequest.GetByTransactionHash(requestTxHash);
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
            if (string.IsNullOrEmpty(tx.Data))
                return false;

            var jobj = JObject.Parse(tx.Data);
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
                SmartContractUID = scUID,
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
            if (VBTCWithdrawalRequest.GetByTransactionHash(tx.Hash) != null)
            {
                result.RequestsAlreadyPresent++;
                return;
            }

            if (!TryBuildRequestRow(tx, out var row) || row == null)
            {
                result.Skipped++;
                return;
            }

            // update:true matches live processing — it upgrades an API node's local
            // pre-registration row (same composite key, empty TransactionHash, no pins yet).
            if (VBTCWithdrawalRequest.Save(row, update: true))
                result.RequestsInserted++;
            else
                result.Errors++;
        }

        private static void ReplayComplete(Transaction tx, RebuildResult result)
        {
            if (string.IsNullOrEmpty(tx.Data)) { result.Skipped++; return; }
            var jobj = JObject.Parse(tx.Data);
            var withdrawalRequestHash = jobj["WithdrawalRequestHash"]?.ToObject<string?>();
            var btcTxHash = jobj["BTCTransactionHash"]?.ToObject<string?>();
            if (string.IsNullOrEmpty(withdrawalRequestHash) || string.IsNullOrEmpty(btcTxHash)) { result.Skipped++; return; }

            var row = VBTCWithdrawalRequest.GetByTransactionHash(withdrawalRequestHash);
            if (row == null)
            {
                // REQUEST replays before COMPLETE in the same ascending scan; a miss means the
                // request tx itself was malformed. Nothing to converge on.
                result.Errors++;
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

        private static void ReplayCancel(Transaction tx, RebuildResult result)
        {
            if (string.IsNullOrEmpty(tx.Data)) { result.Skipped++; return; }
            var jobj = JObject.Parse(tx.Data);
            var scUID = jobj["ContractUID"]?.ToObject<string?>();
            var withdrawalRequestHash = jobj["WithdrawalRequestHash"]?.ToObject<string?>();
            var failureProof = jobj["FailureProof"]?.ToObject<string?>() ?? "";
            if (string.IsNullOrEmpty(scUID) || string.IsNullOrEmpty(withdrawalRequestHash)) { result.Skipped++; return; }

            var row = VBTCWithdrawalRequest.GetByTransactionHash(withdrawalRequestHash);
            if (row == null || row.RequestorAddress != tx.FromAddress || row.IsCompleted) { result.Skipped++; return; }

            if (VBTCWithdrawalCancellation.GetCancellationByWithdrawalHash(withdrawalRequestHash) != null) { result.Skipped++; return; }

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
            var jobj = JObject.Parse(tx.Data);
            var cancellationUID = jobj["CancellationUID"]?.ToObject<string?>();
            var approve = jobj["Approve"]?.ToObject<bool?>() ?? false;
            if (string.IsNullOrEmpty(cancellationUID)) { result.Skipped++; return; }

            var cancellation = VBTCWithdrawalCancellation.GetCancellation(cancellationUID);
            if (cancellation == null || cancellation.IsProcessed) { result.Skipped++; return; }

            // Same eligibility rules as live processing / ResetTreis replay: resolved against the
            // node's current registry + DKG snapshot (both chain-derived).
            var voterSet = VBTCService.ResolveCancellationVoterSet(cancellation.SmartContractUID);
            var validator = VBTCValidatorRegistry.GetValidator(tx.FromAddress);
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

                var row = VBTCWithdrawalRequest.GetByTransactionHash(cancellation.WithdrawalRequestHash);
                // Mirror of the completed-guard in StateData.VoteOnVBTCV2Cancellation: a burn-backed
                // Completed row must never flip to Cancelled.
                if (row != null && !(row.IsCompleted && row.Status == VBTCWithdrawalStatus.Completed))
                {
                    row.Status = VBTCWithdrawalStatus.Cancelled;
                    row.IsCompleted = true;
                    VBTCWithdrawalRequest.Save(row, update: true);
                }
            }
        }
    }
}
