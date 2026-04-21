using Newtonsoft.Json;
using ReserveBlockCore.Bitcoin.Models;
using ReserveBlockCore.Data;
using ReserveBlockCore.Models;
using ReserveBlockCore.Utilities;
using System.Collections.Concurrent;
using System.Text;

namespace ReserveBlockCore.Bitcoin.Services
{
    /// <summary>
    /// Caster consensus service for burn exits (both burnForExit and burnForBTCExit).
    /// Implements BurnAlert broadcasting, lowest-hash-wins proposal/confirmation rounds,
    /// ProcessedBurnRegistry, stuck exit detection, FROST coordination for BTC exits,
    /// and catch-up sync on startup.
    /// </summary>
    public static class BurnExitConsensusService
    {
        private static readonly ConcurrentDictionary<string, ProcessedBurnRecord> _processedBurns = new();
        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, BurnExitProposal>> _proposals = new();
        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, BurnExitConfirmation>> _confirmations = new();

        private const int STUCK_EXIT_BLOCK_THRESHOLD = 50;
        private const int COMPLETED_TTL_HOURS = 24;
        private const int FAILED_TTL_HOURS = 1;
        private const int CONSENSUS_CHECK_INTERVAL_MS = 15_000;
        private const int PROPOSAL_WAIT_MS = 8_000;
        private const int CONFIRMATION_WAIT_MS = 8_000;

        public enum BurnExitType { BtcExit, VfxPoolUnlock }
        public enum BurnExitStatus { Pending, InConsensus, ConsensusReached, VfxTxCreated, FrostInProgress, Complete, Failed }

        public class ProcessedBurnRecord
        {
            public string BaseBurnTxHash { get; set; } = "";
            public BurnExitType ExitType { get; set; }
            public BurnExitStatus Status { get; set; } = BurnExitStatus.Pending;
            public string VfxLockId { get; set; } = "";
            public string BtcDestination { get; set; } = "";
            /// <summary>VFX destination address for V3 pool-based unlocks (VfxPoolUnlock exit type).</summary>
            public string VfxDestinationAddress { get; set; } = "";
            public decimal Amount { get; set; }
            public string BurnerAddress { get; set; } = "";
            public long DetectedAtBlock { get; set; }
            public DateTime DetectedAt { get; set; } = DateTime.UtcNow;
            public string HandlerCasterAddress { get; set; } = "";
            public List<CasterConsensusVote> ConsensusVotes { get; set; } = new();
            public string CompletionVfxTxHash { get; set; } = "";
            public string BtcTxHash { get; set; } = "";
            /// <summary>Serialized BtcExitWithdrawalRecord list for stuck exit COMPLETE retry.</summary>
            public string BtcWithdrawalsJson { get; set; } = "";
            /// <summary>Serialized PoolUnlockAllocation list — the original allocations reserved by EXIT_TO_BTC.</summary>
            public string AllocationsJson { get; set; } = "";
        }

        public class BurnAlert
        {
            public string BaseBurnTxHash { get; set; } = "";
            public BurnExitType ExitType { get; set; }
            public string VfxLockId { get; set; } = "";
            public string BtcDestination { get; set; } = "";
            /// <summary>VFX destination address for V3 pool-based unlocks.</summary>
            public string VfxDestinationAddress { get; set; } = "";
            public decimal Amount { get; set; }
            public string BurnerAddress { get; set; } = "";
            public string SenderCasterAddress { get; set; } = "";
        }

        public class BurnExitProposal
        {
            public string BaseBurnTxHash { get; set; } = "";
            public string ProposerCasterAddress { get; set; } = "";
            public string ProposerHash { get; set; } = "";
        }

        public class BurnExitConfirmation
        {
            public string BaseBurnTxHash { get; set; } = "";
            public string ConfirmingCasterAddress { get; set; } = "";
            public string AgreedHandlerAddress { get; set; } = "";
            public string Signature { get; set; } = "";
            public long Timestamp { get; set; }
        }

        /// <summary>Adaptive majority: max(2, casterCount / 2 + 1).</summary>
        public static int RequiredCasterSignatures => Math.Max(2, Globals.ActiveCasterCount / 2 + 1);

        /// <summary>
        /// Background loop: catch-up sync on startup, then monitor pending burns and drive consensus.
        /// </summary>
        public static async Task ConsensusLoop(CancellationToken ct = default)
        {
            while (!Globals.IsChainSynced && !ct.IsCancellationRequested)
                await Task.Delay(5_000, ct);

            LogUtility.Log("[BurnExitConsensus] Consensus loop started. Running catch-up sync.", "BurnExitConsensusService");
            await CatchUpSync();

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    PruneExpiredRecords();
                    await CheckForStuckExits();
                    await ProcessPendingBurns();
                }
                catch (Exception ex)
                {
                    ErrorLogUtility.LogError($"[BurnExitConsensus] Error: {ex.Message}", "BurnExitConsensusService.ConsensusLoop()");
                }
                await Task.Delay(CONSENSUS_CHECK_INTERVAL_MS, ct);
            }
        }

        /// <summary>
        /// Catch-up sync on startup. Scans recent VFX blocks for already-processed burns
        /// to prevent duplicate handling after a caster restart.
        /// </summary>
        private static async Task CatchUpSync()
        {
            try
            {
                var currentHeight = Globals.LastBlock?.Height ?? 0;
                var scanFrom = Math.Max(0, currentHeight - 500);
                var blocksDb = BlockData.GetBlocks();
                if (blocksDb == null) return;
                var recentBlocks = blocksDb.Find(b => b.Height >= scanFrom && b.Height <= currentHeight).ToList();
                if (!recentBlocks.Any()) return;

                int syncedCount = 0;
                foreach (var block in recentBlocks)
                {
                    if (block.Transactions == null || !block.Transactions.Any()) continue;
                    foreach (var tx in block.Transactions)
                    {
                        if (tx.TransactionType == TransactionType.VBTC_V2_BRIDGE_UNLOCK ||
                            tx.TransactionType == TransactionType.VBTC_V2_BRIDGE_POOL_UNLOCK ||
                            tx.TransactionType == TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC ||
                            tx.TransactionType == TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC_COMPLETE)
                        {
                            try
                            {
                                var txObj = Newtonsoft.Json.Linq.JObject.Parse(tx.Data ?? "{}");
                                var burnTxHash = txObj["ExitBurnTxHash"]?.ToString() ??
                                                txObj["BaseBurnTxHash"]?.ToString() ?? "";
                                if (string.IsNullOrEmpty(burnTxHash)) continue;

                                if (!_processedBurns.ContainsKey(burnTxHash))
                                {
                                    _processedBurns.TryAdd(burnTxHash, new ProcessedBurnRecord
                                    {
                                        BaseBurnTxHash = burnTxHash,
                                        Status = BurnExitStatus.Complete,
                                        CompletionVfxTxHash = tx.Hash,
                                        DetectedAt = DateTime.UtcNow.AddHours(-12),
                                        DetectedAtBlock = block.Height
                                    });
                                    syncedCount++;
                                }
                            }
                            catch { }
                        }
                    }
                }
                LogUtility.Log($"[BurnExitConsensus] Catch-up sync complete. Marked {syncedCount} burns as processed.", "BurnExitConsensusService");
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"[BurnExitConsensus] Catch-up sync error: {ex.Message}", "BurnExitConsensusService");
            }
            await Task.CompletedTask;
        }

        /// <summary>Process pending burns that haven't entered consensus yet.</summary>
        private static async Task ProcessPendingBurns()
        {
            var pendingBurns = _processedBurns.Values
                .Where(r => r.Status == BurnExitStatus.Pending && Globals.IsBlockCaster)
                .ToList();

            foreach (var burn in pendingBurns)
                await RunConsensusRound(burn.BaseBurnTxHash);
        }

        /// <summary>
        /// Called by BaseBridgeExitWatchService when a burn event is detected on Base.
        /// </summary>
        public static async Task HandleDetectedBurn(string baseBurnTxHash, BurnExitType exitType,
            string vfxLockId, string btcDestination, decimal amount, string burnerAddress,
            string vfxDestinationAddress = "")
        {
            if (_processedBurns.ContainsKey(baseBurnTxHash))
            {
                LogUtility.Log($"[BurnExitConsensus] Skipping already-tracked burn: {baseBurnTxHash}", "BurnExitConsensusService");
                return;
            }

            var record = new ProcessedBurnRecord
            {
                BaseBurnTxHash = baseBurnTxHash,
                ExitType = exitType,
                VfxLockId = vfxLockId,
                BtcDestination = btcDestination,
                VfxDestinationAddress = vfxDestinationAddress,
                Amount = amount,
                BurnerAddress = burnerAddress,
                DetectedAtBlock = Globals.LastBlock?.Height ?? 0,
                Status = BurnExitStatus.Pending
            };

            if (!_processedBurns.TryAdd(baseBurnTxHash, record))
                return;

            LogUtility.Log($"[BurnExitConsensus] Detected burn: {baseBurnTxHash}, type={exitType}, amount={amount}", "BurnExitConsensusService");

            await BroadcastBurnAlert(new BurnAlert
            {
                BaseBurnTxHash = baseBurnTxHash,
                ExitType = exitType,
                VfxLockId = vfxLockId,
                BtcDestination = btcDestination,
                VfxDestinationAddress = vfxDestinationAddress,
                Amount = amount,
                BurnerAddress = burnerAddress,
                SenderCasterAddress = Globals.ValidatorAddress
            });

            await RunConsensusRound(baseBurnTxHash);
        }

        /// <summary>Handle BurnAlert from another caster via HTTP.</summary>
        public static async Task HandleBurnAlert(BurnAlert alert)
        {
            if (string.IsNullOrEmpty(alert.BaseBurnTxHash) || _processedBurns.ContainsKey(alert.BaseBurnTxHash))
                return;

            _processedBurns.TryAdd(alert.BaseBurnTxHash, new ProcessedBurnRecord
            {
                BaseBurnTxHash = alert.BaseBurnTxHash,
                ExitType = alert.ExitType,
                VfxLockId = alert.VfxLockId,
                BtcDestination = alert.BtcDestination,
                VfxDestinationAddress = alert.VfxDestinationAddress,
                Amount = alert.Amount,
                BurnerAddress = alert.BurnerAddress,
                DetectedAtBlock = Globals.LastBlock?.Height ?? 0,
                Status = BurnExitStatus.Pending
            });

            LogUtility.Log($"[BurnExitConsensus] Received BurnAlert from {alert.SenderCasterAddress}: {alert.BaseBurnTxHash}", "BurnExitConsensusService");
            await Task.CompletedTask;
        }

        /// <summary>
        /// Runs a consensus round using lowest-hash-wins tiebreaking.
        /// </summary>
        private static async Task RunConsensusRound(string baseBurnTxHash)
        {
            if (!_processedBurns.TryGetValue(baseBurnTxHash, out var record))
                return;
            if (record.Status != BurnExitStatus.Pending)
                return;

            record.Status = BurnExitStatus.InConsensus;

            // Step 1: Generate deterministic proposal hash
            var proposalHashInput = Encoding.UTF8.GetBytes($"{Globals.ValidatorAddress}:{baseBurnTxHash}");
            var proposalHash = Nethereum.Util.Sha3Keccack.Current.CalculateHashFromHex(Convert.ToHexString(proposalHashInput));

            var myProposal = new BurnExitProposal
            {
                BaseBurnTxHash = baseBurnTxHash,
                ProposerCasterAddress = Globals.ValidatorAddress,
                ProposerHash = proposalHash
            };

            var proposalDict = _proposals.GetOrAdd(baseBurnTxHash, _ => new ConcurrentDictionary<string, BurnExitProposal>());
            proposalDict[Globals.ValidatorAddress] = myProposal;

            // Step 2: Broadcast proposal
            await BroadcastToAllCasters("BurnExitProposal", myProposal);

            // Step 3: Wait for other proposals
            await Task.Delay(PROPOSAL_WAIT_MS);

            // Step 4: Determine lowest hash winner
            var allProposals = proposalDict.Values.ToList();
            if (!allProposals.Any())
            {
                record.Status = BurnExitStatus.Failed;
                LogUtility.Log($"[BurnExitConsensus] No proposals received for {baseBurnTxHash}. Marking as Failed.", "BurnExitConsensusService");
                return;
            }

            var winner = allProposals.OrderBy(p => p.ProposerHash, StringComparer.OrdinalIgnoreCase).First();
            record.HandlerCasterAddress = winner.ProposerCasterAddress;

            LogUtility.Log($"[BurnExitConsensus] Winner for {baseBurnTxHash}: {winner.ProposerCasterAddress} ({allProposals.Count} proposals)", "BurnExitConsensusService");

            // Step 5: Broadcast confirmation (sign the vote so TryVerifyVotes can validate it)
            var burnType = BurnTypeString(record.ExitType);
            var timestamp = TimeUtil.GetTime();
            var voteSig = SignVoteMessage(baseBurnTxHash, burnType, timestamp);
            var confirmation = new BurnExitConfirmation
            {
                BaseBurnTxHash = baseBurnTxHash,
                ConfirmingCasterAddress = Globals.ValidatorAddress,
                AgreedHandlerAddress = winner.ProposerCasterAddress,
                Signature = voteSig,
                Timestamp = timestamp
            };

            var confirmDict = _confirmations.GetOrAdd(baseBurnTxHash, _ => new ConcurrentDictionary<string, BurnExitConfirmation>());
            confirmDict[Globals.ValidatorAddress] = confirmation;
            await BroadcastToAllCasters("BurnExitConfirmation", confirmation);

            // Step 6: Wait for confirmations
            await Task.Delay(CONFIRMATION_WAIT_MS);

            var confirmations = confirmDict.Values
                .Where(c => c.AgreedHandlerAddress == winner.ProposerCasterAddress)
                .ToList();

            if (confirmations.Count < RequiredCasterSignatures)
            {
                LogUtility.Log($"[BurnExitConsensus] Insufficient confirmations for {baseBurnTxHash}: {confirmations.Count}/{RequiredCasterSignatures}", "BurnExitConsensusService");
                record.Status = BurnExitStatus.Pending; // Retry later
                return;
            }

            record.Status = BurnExitStatus.ConsensusReached;
            var voteBurnType = BurnTypeString(record.ExitType);
            record.ConsensusVotes = confirmations.Select(c => new CasterConsensusVote
            {
                CasterAddress = c.ConfirmingCasterAddress,
                BaseBurnTxHash = c.BaseBurnTxHash,
                BurnType = voteBurnType,
                Signature = c.Signature,
                Timestamp = c.Timestamp
            }).ToList();

            LogUtility.Log($"[BurnExitConsensus] Consensus reached for {baseBurnTxHash}. Handler: {winner.ProposerCasterAddress}, Votes: {confirmations.Count}", "BurnExitConsensusService");

            // Step 7: If this caster is the handler, execute
            if (winner.ProposerCasterAddress == Globals.ValidatorAddress && Globals.IsBlockCaster)
                await ExecuteExitTransaction(record);

            _proposals.TryRemove(baseBurnTxHash, out _);
            _confirmations.TryRemove(baseBurnTxHash, out _);
        }

        /// <summary>Handle proposal from another caster.</summary>
        public static void HandleBurnExitProposal(BurnExitProposal proposal)
        {
            if (string.IsNullOrEmpty(proposal.BaseBurnTxHash)) return;
            var dict = _proposals.GetOrAdd(proposal.BaseBurnTxHash, _ => new ConcurrentDictionary<string, BurnExitProposal>());
            dict[proposal.ProposerCasterAddress] = proposal;
            LogUtility.Log($"[BurnExitConsensus] Proposal from {proposal.ProposerCasterAddress} for {proposal.BaseBurnTxHash}", "BurnExitConsensusService");
        }

        /// <summary>Handle confirmation from another caster.</summary>
        public static void HandleBurnExitConfirmation(BurnExitConfirmation confirmation)
        {
            if (string.IsNullOrEmpty(confirmation.BaseBurnTxHash)) return;
            var dict = _confirmations.GetOrAdd(confirmation.BaseBurnTxHash, _ => new ConcurrentDictionary<string, BurnExitConfirmation>());
            dict[confirmation.ConfirmingCasterAddress] = confirmation;
            LogUtility.Log($"[BurnExitConsensus] Confirmation from {confirmation.ConfirmingCasterAddress} for {confirmation.BaseBurnTxHash}", "BurnExitConsensusService");
        }

        /// <summary>
        /// Execute the exit transaction: VFX unlock or BTC exit + FROST.
        /// </summary>
        private static async Task ExecuteExitTransaction(ProcessedBurnRecord record)
        {
            try
            {
                LogUtility.Log($"[BurnExitConsensus] Executing exit type={record.ExitType} for burn={record.BaseBurnTxHash}, amount={record.Amount}", "BurnExitConsensusService");

                if (record.ExitType == BurnExitType.VfxPoolUnlock)
                    await ExecuteVfxPoolUnlock(record);
                else if (record.ExitType == BurnExitType.BtcExit)
                    await ExecuteBtcExit(record);
            }
            catch (Exception ex)
            {
                record.Status = BurnExitStatus.Failed;
                ErrorLogUtility.LogError($"[BurnExitConsensus] ExecuteExitTransaction error: {ex.Message}", "BurnExitConsensusService");
            }
        }

        /// <summary>
        /// V3 pool-based unlock: compute FIFO allocation plan and broadcast VBTC_V2_BRIDGE_POOL_UNLOCK.
        /// </summary>
        private static async Task ExecuteVfxPoolUnlock(ProcessedBurnRecord record)
        {
            var vfxDestinationAddress = record.VfxDestinationAddress;
            LogUtility.Log($"[BurnExitConsensus] VfxPoolUnlock: vfxDest={vfxDestinationAddress}, amount={record.Amount}, burn={record.BaseBurnTxHash}", "BurnExitConsensusService.ExecuteVfxPoolUnlock()");
            var allocations = BridgePoolUnlockService.ComputeAllocationPlan(record.Amount);

            if (allocations == null || allocations.Count == 0)
            {
                record.Status = BurnExitStatus.Failed;
                ErrorLogUtility.LogError($"[BurnExitConsensus] No pool liquidity for VfxPoolUnlock {record.BaseBurnTxHash}, amount={record.Amount}",
                    "BurnExitConsensusService.ExecuteVfxPoolUnlock()");
                return;
            }

            LogUtility.Log($"[BurnExitConsensus] Pool allocation plan: {allocations.Count} lock(s) across contracts for {record.Amount} BTC", "BurnExitConsensusService.ExecuteVfxPoolUnlock()");

            var result = await BridgePoolUnlockService.CreateBridgePoolUnlockTx(
                Globals.ValidatorAddress,
                vfxDestinationAddress,
                record.Amount,
                record.BaseBurnTxHash,
                allocations,
                record.ConsensusVotes);

            if (result.Success)
            {
                record.Status = BurnExitStatus.Complete;
                record.CompletionVfxTxHash = result.TxHashOrError;
                LogUtility.Log($"[BurnExitConsensus] VFX pool unlock TX: {result.TxHashOrError} for burn {record.BaseBurnTxHash}",
                    "BurnExitConsensusService.ExecuteVfxPoolUnlock()");
            }
            else
            {
                record.Status = BurnExitStatus.Failed;
                ErrorLogUtility.LogError($"[BurnExitConsensus] VFX pool unlock failed: {result.TxHashOrError}",
                    "BurnExitConsensusService.ExecuteVfxPoolUnlock()");
            }
        }

        /// <summary>
        /// Executes the full BTC-exit flow on a caster node in response to a detected <c>burnForBTCExit</c> event.
        /// RESERVE-FIRST architecture: VFX EXIT_TO_BTC is broadcast BEFORE any BTC leaves the pool,
        /// preventing double-spend of FIFO allocations if FROST or BTC broadcast fails.
        /// Pipeline:
        ///   (1) Build exclusion set from blacklisted contracts.
        ///   (2) Compute FIFO allocation plan.
        ///   (3) Broadcast VFX EXIT_TO_BTC to reserve allocations on-chain (no BTC tx yet).
        ///   (4) Wait for VFX block confirmation — FIFO is now locked on-chain.
        ///   (5) For each contract group: FROST sign-only + broadcast BTC tx.
        ///   (6) Broadcast VFX EXIT_TO_BTC_COMPLETE with BTC tx hashes.
        /// On retry (stuck exit), if EXIT_TO_BTC was already confirmed, skips straight to step (5).
        /// </summary>
        private static async Task ExecuteBtcExit(ProcessedBurnRecord record)
        {
            var ownerAddress = Globals.ValidatorAddress;

            // Guard: non-validator casters can't run this flow.
            if (string.IsNullOrWhiteSpace(ownerAddress))
            {
                record.Status = BurnExitStatus.Failed;
                ErrorLogUtility.LogError($"[BurnExitConsensus] Cannot execute BTC exit: Globals.ValidatorAddress is empty. Burn: {record.BaseBurnTxHash}",
                    "BurnExitConsensusService.ExecuteBtcExit()");
                return;
            }

            var signerAccount = AccountData.GetSingleAccount(ownerAddress);
            if (signerAccount == null)
            {
                record.Status = BurnExitStatus.Failed;
                ErrorLogUtility.LogError($"[BurnExitConsensus] Cannot execute BTC exit: validator address {ownerAddress} has no local account record. Burn: {record.BaseBurnTxHash}",
                    "BurnExitConsensusService.ExecuteBtcExit()");
                return;
            }

            // ----------------------------------------------------------------
            // Step 1: Build initial exclusion set from blacklist
            // ----------------------------------------------------------------
            var excludedContracts = new HashSet<string>();
            var allAvailableLocks = VBTCBridgeLockState.GetAvailableLocksFIFO();
            if (allAvailableLocks != null)
            {
                var distinctContracts = allAvailableLocks.Select(l => l.SmartContractUID).Distinct();
                foreach (var scUID in distinctContracts)
                {
                    if (FrostContractBlacklist.IsBlacklisted(scUID))
                    {
                        excludedContracts.Add(scUID);
                        LogUtility.Log($"[BurnExitConsensus] Pre-excluding blacklisted contract {scUID} for BTC exit {record.BaseBurnTxHash}",
                            "BurnExitConsensusService.ExecuteBtcExit()");
                    }
                }
            }

            // Check if EXIT_TO_BTC was already confirmed (retry after FROST failure)
            bool exitToBtcAlreadyConfirmed = !string.IsNullOrEmpty(record.CompletionVfxTxHash);
            List<PoolUnlockAllocation> allSuccessfulAllocations;

            if (!exitToBtcAlreadyConfirmed)
            {
                // ----------------------------------------------------------------
                // Step 2: Compute FIFO allocation plan (full amount, one pass)
                // ----------------------------------------------------------------
                var allocations = BridgePoolUnlockService.ComputeAllocationPlan(record.Amount, excludedContracts);
                if (allocations == null || allocations.Count == 0)
                {
                    record.Status = BurnExitStatus.Failed;
                    ErrorLogUtility.LogError($"[BurnExitConsensus] No eligible locks for {record.Amount} BTC. Burn: {record.BaseBurnTxHash}",
                        "BurnExitConsensusService.ExecuteBtcExit()");
                    return;
                }

                allSuccessfulAllocations = allocations;
                record.AllocationsJson = JsonConvert.SerializeObject(allocations);

                LogUtility.Log($"[BurnExitConsensus] BTC exit FIFO allocation plan: {allocations.Count} lock(s) for {record.Amount} BTC, burn={record.BaseBurnTxHash}",
                    "BurnExitConsensusService.ExecuteBtcExit()");

                // ----------------------------------------------------------------
                // Step 3: Broadcast VFX EXIT_TO_BTC to reserve FIFO on-chain (no BtcWithdrawals yet)
                // ----------------------------------------------------------------
                record.Status = BurnExitStatus.VfxTxCreated;
                var exitResult = await VBTCService.CreateBridgeExitToBTCTx(
                    ownerAddress,
                    record.Amount,
                    record.BtcDestination,
                    record.BaseBurnTxHash,
                    allSuccessfulAllocations,
                    null, // No BTC withdrawals yet — reservation only
                    record.ConsensusVotes);

                if (!exitResult.Success)
                {
                    var err = exitResult.TxHashOrError ?? string.Empty;
                    if (err.IndexOf("Duplicate", StringComparison.OrdinalIgnoreCase) >= 0
                        || err.IndexOf("already", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        // Another caster already reserved — safe to treat as handled
                        record.Status = BurnExitStatus.Complete;
                        LogUtility.Log($"[BurnExitConsensus] EXIT_TO_BTC already broadcast for burn {record.BaseBurnTxHash} — treating as handled.",
                            "BurnExitConsensusService.ExecuteBtcExit()");
                        return;
                    }

                    record.Status = BurnExitStatus.Failed;
                    ErrorLogUtility.LogError($"[BurnExitConsensus] VFX EXIT_TO_BTC broadcast failed: {err}. No BTC was sent. Stuck exit will retry.",
                        "BurnExitConsensusService.ExecuteBtcExit()");
                    return;
                }

                record.CompletionVfxTxHash = exitResult.TxHashOrError;
                LogUtility.Log($"[BurnExitConsensus] VFX EXIT_TO_BTC TX: {exitResult.TxHashOrError}. Waiting for block inclusion before FROST signing.",
                    "BurnExitConsensusService.ExecuteBtcExit()");

                // ----------------------------------------------------------------
                // Step 4: Wait for VFX block confirmation — FIFO is now locked on-chain
                // ----------------------------------------------------------------
                var confirmed = await WaitForVfxTxConfirmation(exitResult.TxHashOrError, 120_000);
                if (!confirmed)
                {
                    LogUtility.Log($"[BurnExitConsensus] VFX EXIT_TO_BTC not confirmed in time for {record.BaseBurnTxHash}. Stuck exit detection will retry.",
                        "BurnExitConsensusService.ExecuteBtcExit()");
                    return;
                }

                LogUtility.Log($"[BurnExitConsensus] VFX EXIT_TO_BTC confirmed in block. Proceeding to FROST signing for burn {record.BaseBurnTxHash}.",
                    "BurnExitConsensusService.ExecuteBtcExit()");
            }
            else
            {
                // Retry path: EXIT_TO_BTC was confirmed but FROST failed previously.
                // Broadcast a FAIL TX to restore locks, then restart the full flow.
                LogUtility.Log($"[BurnExitConsensus] EXIT_TO_BTC already confirmed ({record.CompletionVfxTxHash}) but FROST not done. Broadcasting FAIL TX to restore locks before retry.",
                    "BurnExitConsensusService.ExecuteBtcExit()");

                try
                {
                    // Use the ORIGINAL allocations that were reserved by EXIT_TO_BTC,
                    // not freshly computed ones — the FAIL TX must restore the exact locks
                    // that were deducted on-chain by the original EXIT_TO_BTC.
                    List<PoolUnlockAllocation> retryAllocations;
                    if (!string.IsNullOrEmpty(record.AllocationsJson))
                    {
                        retryAllocations = JsonConvert.DeserializeObject<List<PoolUnlockAllocation>>(record.AllocationsJson)
                            ?? new List<PoolUnlockAllocation>();
                    }
                    else
                    {
                        // Fallback: if no stored allocations (e.g., pre-upgrade records), compute fresh.
                        // This is best-effort — may not match the original locks exactly.
                        ErrorLogUtility.LogError($"[BurnExitConsensus] No stored AllocationsJson for {record.BaseBurnTxHash}. Using fresh allocation (best-effort).",
                            "BurnExitConsensusService.ExecuteBtcExit()");
                        retryAllocations = BridgePoolUnlockService.ComputeAllocationPlan(record.Amount, excludedContracts)
                            ?? new List<PoolUnlockAllocation>();
                    }

                    var failResult = await VBTCService.CreateBridgeExitToBTCFailTx(
                        ownerAddress,
                        record.BaseBurnTxHash,
                        record.CompletionVfxTxHash,
                        retryAllocations,
                        "Retry path: restoring locks for fresh allocation");

                    if (failResult.Success)
                    {
                        LogUtility.Log($"[BurnExitConsensus] FAIL TX for retry: {failResult.TxHashOrError}. Waiting for confirmation before restart.",
                            "BurnExitConsensusService.ExecuteBtcExit()");

                        // Wait for FAIL TX confirmation before resetting to Pending,
                        // otherwise the retry could allocate locks that haven't been restored yet.
                        var failConfirmed = await WaitForVfxTxConfirmation(failResult.TxHashOrError, 120_000);
                        if (!failConfirmed)
                        {
                            LogUtility.Log($"[BurnExitConsensus] FAIL TX not confirmed in time. Stuck exit will retry next cycle.",
                                "BurnExitConsensusService.ExecuteBtcExit()");
                            return;
                        }

                        record.CompletionVfxTxHash = "";
                        record.Status = BurnExitStatus.Pending;
                        return; // Will be picked up again by ProcessPendingBurns
                    }
                    else
                    {
                        ErrorLogUtility.LogError($"[BurnExitConsensus] FAIL TX for retry failed: {failResult.TxHashOrError}. Cannot retry safely.",
                            "BurnExitConsensusService.ExecuteBtcExit()");
                        record.Status = BurnExitStatus.Failed;
                        return;
                    }
                }
                catch (Exception failEx)
                {
                    ErrorLogUtility.LogError($"[BurnExitConsensus] FAIL TX for retry exception: {failEx.Message}",
                        "BurnExitConsensusService.ExecuteBtcExit()");
                    record.Status = BurnExitStatus.Failed;
                    return;
                }
            }

            // ----------------------------------------------------------------
            // Step 5: Progressive FROST retry loop — sign + broadcast BTC
            // ----------------------------------------------------------------
            var btcWithdrawals = new List<BtcExitWithdrawalRecord>();
            decimal remainingAmount = record.Amount;
            const int MAX_RETRY_ROUNDS = 10;

            for (int round = 0; round < MAX_RETRY_ROUNDS && remainingAmount > 0.00000001M; round++)
            {
                // On first round, use the pre-computed allocations; on subsequent rounds, recompute
                List<PoolUnlockAllocation> roundAllocations;
                if (round == 0 && allSuccessfulAllocations.Count > 0)
                {
                    roundAllocations = allSuccessfulAllocations;
                }
                else
                {
                    roundAllocations = BridgePoolUnlockService.ComputeAllocationPlan(remainingAmount, excludedContracts)
                        ?? new List<PoolUnlockAllocation>();
                }

                if (roundAllocations.Count == 0)
                {
                    LogUtility.Log($"[BurnExitConsensus] No eligible locks for remaining {remainingAmount} BTC (round {round + 1}). Burn: {record.BaseBurnTxHash}",
                        "BurnExitConsensusService.ExecuteBtcExit()");
                    break;
                }

                LogUtility.Log($"[BurnExitConsensus] FROST signing round {round + 1}: {roundAllocations.Count} lock(s) for {remainingAmount} BTC, burn={record.BaseBurnTxHash}",
                    "BurnExitConsensusService.ExecuteBtcExit()");

                var contractGroups = roundAllocations
                    .GroupBy(a => a.SmartContractUID)
                    .ToList();

                bool needRecompute = false;

                foreach (var group in contractGroups)
                {
                    var scUID = group.Key;
                    var groupAmount = group.Sum(a => a.UnlockAmount);

                    if (excludedContracts.Contains(scUID))
                        continue;

                    try
                    {
                        record.Status = BurnExitStatus.FrostInProgress;

                        var syntheticHash = $"btcexit_{record.BaseBurnTxHash[..Math.Min(16, record.BaseBurnTxHash.Length)]}_{scUID[..Math.Min(8, scUID.Length)]}";

                        var frostResult = await VBTCService.CompleteWithdrawal(
                            scUID, syntheticHash,
                            delegatedAmount: groupAmount,
                            delegatedBTCDestination: record.BtcDestination,
                            delegatedFeeRate: 10,
                            signOnly: true);

                        if (!frostResult.Success)
                        {
                            FrostContractBlacklist.Blacklist(scUID, $"FROST signing failed: {frostResult.ErrorMessage}");
                            LogUtility.Log($"[BurnExitConsensus] FROST sign-only failed for contract {scUID}: {frostResult.ErrorMessage}. Auto-blacklisted. Will retry with other contracts.",
                                "BurnExitConsensusService.ExecuteBtcExit()");
                            excludedContracts.Add(scUID);
                            needRecompute = true;
                            break;
                        }

                        var signedTxHex = frostResult.BTCTxHash;
                        if (string.IsNullOrWhiteSpace(signedTxHex))
                        {
                            LogUtility.Log($"[BurnExitConsensus] FROST sign-only produced empty signed hex for contract {scUID}. Will retry with other contracts.",
                                "BurnExitConsensusService.ExecuteBtcExit()");
                            excludedContracts.Add(scUID);
                            needRecompute = true;
                            break;
                        }

                        // Broadcast signed BTC tx for this contract group
                        var btcNetwork = Globals.BTCNetwork;
                        var signedBtcTx = NBitcoin.Transaction.Parse(signedTxHex, btcNetwork);
                        var broadcastResult = await BitcoinTransactionService.BroadcastTransaction(signedBtcTx);
                        if (!broadcastResult.Success)
                        {
                            LogUtility.Log($"[BurnExitConsensus] BTC broadcast failed for contract {scUID}: {broadcastResult.ErrorMessage}. Will retry with other contracts.",
                                "BurnExitConsensusService.ExecuteBtcExit()");
                            excludedContracts.Add(scUID);
                            needRecompute = true;
                            break;
                        }

                        long groupAmountSats = (long)(groupAmount * 100_000_000M);
                        btcWithdrawals.Add(new BtcExitWithdrawalRecord
                        {
                            SmartContractUID = scUID,
                            Amount = groupAmount,
                            AmountSats = groupAmountSats,
                            BtcTxHash = broadcastResult.TxHash
                        });

                        remainingAmount -= groupAmount;

                        LogUtility.Log($"[BurnExitConsensus] BTC withdrawal broadcast for contract {scUID}: {groupAmount} BTC, BTC TxHash: {broadcastResult.TxHash}. Remaining: {remainingAmount} BTC",
                            "BurnExitConsensusService.ExecuteBtcExit()");
                    }
                    catch (Exception frostEx)
                    {
                        ErrorLogUtility.LogError($"[BurnExitConsensus] FROST/BTC exception for contract {scUID}: {frostEx}",
                            "BurnExitConsensusService.ExecuteBtcExit()");
                        FrostContractBlacklist.Blacklist(scUID, $"FROST exception: {frostEx.Message}");
                        excludedContracts.Add(scUID);
                        needRecompute = true;
                        break;
                    }
                }

                if (!needRecompute)
                    break;
            }

            // Check if any BTC withdrawals succeeded
            if (btcWithdrawals.Count == 0)
            {
                // All FROST signing failed — broadcast FAIL TX to restore the FIFO locks on-chain
                // so the stuck exit detector can retry the full flow with fresh allocations.
                LogUtility.Log($"[BurnExitConsensus] All contract groups failed for BTC exit {record.BaseBurnTxHash}. Broadcasting FAIL TX to restore FIFO locks.",
                    "BurnExitConsensusService.ExecuteBtcExit()");

                try
                {
                    var failResult = await VBTCService.CreateBridgeExitToBTCFailTx(
                        ownerAddress,
                        record.BaseBurnTxHash,
                        record.CompletionVfxTxHash,
                        allSuccessfulAllocations,
                        "All FROST signing rounds failed");

                    if (failResult.Success)
                    {
                        LogUtility.Log($"[BurnExitConsensus] FAIL TX broadcast: {failResult.TxHashOrError}. Locks will be restored on-chain. Stuck exit will retry full flow.",
                            "BurnExitConsensusService.ExecuteBtcExit()");
                        // Clear CompletionVfxTxHash so the retry does the full flow (Steps 2-6)
                        record.CompletionVfxTxHash = "";
                    }
                    else
                    {
                        ErrorLogUtility.LogError($"[BurnExitConsensus] FAIL TX broadcast failed: {failResult.TxHashOrError}. Locks remain deducted on-chain.",
                            "BurnExitConsensusService.ExecuteBtcExit()");
                    }
                }
                catch (Exception failEx)
                {
                    ErrorLogUtility.LogError($"[BurnExitConsensus] FAIL TX exception: {failEx.Message}",
                        "BurnExitConsensusService.ExecuteBtcExit()");
                }

                record.Status = BurnExitStatus.Failed;
                return;
            }

            var totalWithdrawnBtc = btcWithdrawals.Sum(w => w.Amount);
            record.BtcTxHash = string.Join(",", btcWithdrawals.Select(w => w.BtcTxHash));
            record.BtcWithdrawalsJson = JsonConvert.SerializeObject(btcWithdrawals);

            // If partial failure (some groups succeeded, some failed), broadcast a FAIL TX
            // for the failed allocations to restore/blacklist those locks on-chain.
            if (remainingAmount > 0.00000001M)
            {
                var succeededContracts = new HashSet<string>(btcWithdrawals.Select(w => w.SmartContractUID));
                var failedAllocations = allSuccessfulAllocations
                    .Where(a => !succeededContracts.Contains(a.SmartContractUID))
                    .ToList();

                if (failedAllocations.Count > 0)
                {
                    LogUtility.Log($"[BurnExitConsensus] Partial FROST failure: {btcWithdrawals.Count} succeeded, {failedAllocations.Count} allocation(s) failed. Broadcasting FAIL TX for failed locks.",
                        "BurnExitConsensusService.ExecuteBtcExit()");

                    try
                    {
                        var partialFailResult = await VBTCService.CreateBridgeExitToBTCFailTx(
                            ownerAddress,
                            record.BaseBurnTxHash,
                            record.CompletionVfxTxHash,
                            failedAllocations,
                            $"Partial FROST failure: {failedAllocations.Count} lock(s) failed");

                        if (partialFailResult.Success)
                        {
                            LogUtility.Log($"[BurnExitConsensus] Partial FAIL TX broadcast: {partialFailResult.TxHashOrError}. Failed locks will be restored/blacklisted on-chain.",
                                "BurnExitConsensusService.ExecuteBtcExit()");
                        }
                        else
                        {
                            ErrorLogUtility.LogError($"[BurnExitConsensus] Partial FAIL TX broadcast failed: {partialFailResult.TxHashOrError}. Failed locks remain deducted.",
                                "BurnExitConsensusService.ExecuteBtcExit()");
                        }
                    }
                    catch (Exception partialFailEx)
                    {
                        ErrorLogUtility.LogError($"[BurnExitConsensus] Partial FAIL TX exception: {partialFailEx.Message}",
                            "BurnExitConsensusService.ExecuteBtcExit()");
                    }
                }
            }

            LogUtility.Log($"[BurnExitConsensus] BTC exit: {btcWithdrawals.Count} BTC tx(s) broadcast, total={totalWithdrawnBtc} BTC. Now broadcasting VFX EXIT_TO_BTC_COMPLETE.",
                "BurnExitConsensusService.ExecuteBtcExit()");

            // ----------------------------------------------------------------
            // Step 6: Broadcast EXIT_TO_BTC_COMPLETE with all BTC tx hashes
            // ----------------------------------------------------------------
            var allBtcTxHashes = string.Join(",", btcWithdrawals.Select(w => w.BtcTxHash));
            var completeResult = await VBTCService.CreateBridgeExitToBTCCompleteTx(
                ownerAddress,
                record.BaseBurnTxHash,
                allBtcTxHashes,
                btcWithdrawals);

            if (!completeResult.Success)
            {
                LogUtility.Log($"[BurnExitConsensus] BTC sent but EXIT_TO_BTC_COMPLETE broadcast failed: {completeResult.TxHashOrError}. Stuck exit will retry.",
                    "BurnExitConsensusService.ExecuteBtcExit()");
                return;
            }

            record.Status = BurnExitStatus.Complete;
            LogUtility.Log($"[BurnExitConsensus] BTC exit complete! {btcWithdrawals.Count} BTC tx(s), VFX Complete TX: {completeResult.TxHashOrError}",
                "BurnExitConsensusService.ExecuteBtcExit()");
        }

        private static async Task<bool> WaitForVfxTxConfirmation(string txHash, int timeoutMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                // Check if the TX exists in the blockchain (confirmed in a block)
                var txDb = TransactionData.GetAll();
                if (txDb != null)
                {
                    var tx = txDb.FindOne(t => t.Hash == txHash);
                    if (tx != null && tx.Height > 0)
                        return true;
                }
                await Task.Delay(3_000);
            }
            return false;
        }

        private static async Task BroadcastBurnAlert(BurnAlert alert) =>
            await BroadcastToAllCasters("BurnAlert", alert);

        private static async Task BroadcastToAllCasters<T>(string endpoint, T payload)
        {
            var casters = Globals.BlockCasters.ToList();
            using var httpClient = Globals.HttpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromSeconds(10);
            var json = JsonConvert.SerializeObject(payload);

            var tasks = casters
                .Where(c => c.ValidatorAddress != Globals.ValidatorAddress && !string.IsNullOrEmpty(c.PeerIP))
                .Select(async c =>
                {
                    try
                    {
                        var url = $"http://{c.PeerIP}:{Globals.ValAPIPort}/valapi/Validator/{endpoint}";
                        await httpClient.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));
                    }
                    catch (Exception broadcastEx)
                    {
                        LogUtility.Log($"[BurnExitConsensus] Failed to broadcast {endpoint} to caster {c.ValidatorAddress} ({c.PeerIP}): {broadcastEx.Message}", "BurnExitConsensusService");
                    }
                });
            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// Detects exits stuck in any intermediate state for too long.
        /// For BTC exits where BTC was already sent, only retries the COMPLETE broadcast.
        /// For all other cases, resets to Pending for re-consensus.
        /// </summary>
        private static async Task CheckForStuckExits()
        {
            var currentBlock = Globals.LastBlock?.Height ?? 0;
            var stuckExits = _processedBurns.Values
                .Where(r => (r.Status == BurnExitStatus.InConsensus ||
                             r.Status == BurnExitStatus.ConsensusReached ||
                             r.Status == BurnExitStatus.VfxTxCreated ||
                             r.Status == BurnExitStatus.FrostInProgress) &&
                            currentBlock - r.DetectedAtBlock > STUCK_EXIT_BLOCK_THRESHOLD)
                .ToList();

            foreach (var stuck in stuckExits)
            {
                // If BTC was already sent (BtcTxHash populated), do NOT restart the full flow.
                // Only retry the EXIT_TO_BTC_COMPLETE broadcast to avoid double-sending BTC.
                if (stuck.ExitType == BurnExitType.BtcExit && !string.IsNullOrEmpty(stuck.BtcTxHash))
                {
                    LogUtility.Log($"[BurnExitConsensus] Stuck exit with BTC already sent: {stuck.BaseBurnTxHash} (status={stuck.Status}, btcTx={stuck.BtcTxHash}). Retrying COMPLETE broadcast only.",
                        "BurnExitConsensusService.CheckForStuckExits()");

                    try
                    {
                        var ownerAddress = Globals.ValidatorAddress;
                        var btcTxHashes = stuck.BtcTxHash;

                        // Reconstruct BtcExitWithdrawalRecords from stored JSON, falling back to hash-only
                        List<BtcExitWithdrawalRecord> btcWithdrawals;
                        if (!string.IsNullOrEmpty(stuck.BtcWithdrawalsJson))
                        {
                            btcWithdrawals = JsonConvert.DeserializeObject<List<BtcExitWithdrawalRecord>>(stuck.BtcWithdrawalsJson)
                                ?? new List<BtcExitWithdrawalRecord>();
                        }
                        else
                        {
                            var withdrawalHashes = btcTxHashes.Split(',', StringSplitOptions.RemoveEmptyEntries);
                            btcWithdrawals = withdrawalHashes.Select(h => new BtcExitWithdrawalRecord
                            {
                                BtcTxHash = h.Trim()
                            }).ToList();
                        }

                        var completeResult = await VBTCService.CreateBridgeExitToBTCCompleteTx(
                            ownerAddress,
                            stuck.BaseBurnTxHash,
                            btcTxHashes,
                            btcWithdrawals);

                        if (completeResult.Success)
                        {
                            stuck.Status = BurnExitStatus.Complete;
                            LogUtility.Log($"[BurnExitConsensus] Retry COMPLETE broadcast succeeded: {completeResult.TxHashOrError}",
                                "BurnExitConsensusService.CheckForStuckExits()");
                        }
                        else
                        {
                            LogUtility.Log($"[BurnExitConsensus] Retry COMPLETE broadcast failed: {completeResult.TxHashOrError}. Will retry next cycle.",
                                "BurnExitConsensusService.CheckForStuckExits()");
                            stuck.DetectedAtBlock = currentBlock; // Reset timer for next retry
                        }
                    }
                    catch (Exception ex)
                    {
                        ErrorLogUtility.LogError($"[BurnExitConsensus] Retry COMPLETE exception: {ex.Message}",
                            "BurnExitConsensusService.CheckForStuckExits()");
                        stuck.DetectedAtBlock = currentBlock;
                    }
                    continue;
                }

                LogUtility.Log($"[BurnExitConsensus] Stuck exit: {stuck.BaseBurnTxHash} (status={stuck.Status}). Retrying.", "BurnExitConsensusService");
                stuck.Status = BurnExitStatus.Pending;
                stuck.DetectedAtBlock = currentBlock;
                stuck.HandlerCasterAddress = "";
                stuck.ConsensusVotes.Clear();
                await RunConsensusRound(stuck.BaseBurnTxHash);
            }
        }

        private static void PruneExpiredRecords()
        {
            var now = DateTime.UtcNow;
            var toRemove = _processedBurns
                .Where(kvp =>
                    (kvp.Value.Status == BurnExitStatus.Complete && (now - kvp.Value.DetectedAt).TotalHours > COMPLETED_TTL_HOURS) ||
                    (kvp.Value.Status == BurnExitStatus.Failed && (now - kvp.Value.DetectedAt).TotalHours > FAILED_TTL_HOURS))
                .Select(kvp => kvp.Key).ToList();

            foreach (var key in toRemove)
                _processedBurns.TryRemove(key, out _);
        }

        public static Dictionary<string, object> GetRegistryStatus() => new()
        {
            ["Total"] = _processedBurns.Count,
            ["Pending"] = _processedBurns.Values.Count(r => r.Status == BurnExitStatus.Pending),
            ["InConsensus"] = _processedBurns.Values.Count(r => r.Status == BurnExitStatus.InConsensus),
            ["FrostInProgress"] = _processedBurns.Values.Count(r => r.Status == BurnExitStatus.FrostInProgress),
            ["Complete"] = _processedBurns.Values.Count(r => r.Status == BurnExitStatus.Complete),
            ["Failed"] = _processedBurns.Values.Count(r => r.Status == BurnExitStatus.Failed)
        };

        public static bool IsAlreadyProcessed(string baseBurnTxHash) =>
            _processedBurns.ContainsKey(baseBurnTxHash);

        /// <summary>Map internal BurnExitType enum to the string burn type used by BridgeCasterConsensus vote verification.</summary>
        private static string BurnTypeString(BurnExitType t) => t == BurnExitType.BtcExit ? "BTC_EXIT" : "POOL_EXIT";

        /// <summary>
        /// Sign a vote message for a burn exit confirmation using this node's validator key.
        /// Returns empty string on failure.
        /// </summary>
        private static string SignVoteMessage(string baseBurnTxHash, string burnType, long timestamp)
        {
            try
            {
                var account = Data.AccountData.GetSingleAccount(Globals.ValidatorAddress);
                if (account == null) return "";
                var privKey = account.GetPrivKey;
                var pubKey = account.PublicKey;
                if (privKey == null) return "";
                var msg = BridgeCasterConsensus.BuildVoteMessage(baseBurnTxHash, burnType, timestamp);
                var sig = ReserveBlockCore.Services.SignatureService.CreateSignature(msg, privKey, pubKey);
                return sig == "ERROR" ? "" : sig;
            }
            catch { return ""; }
        }
    }
}
