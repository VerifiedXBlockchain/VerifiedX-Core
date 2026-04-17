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

        public enum BurnExitType { VfxUnlock, BtcExit, VfxPoolUnlock }
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

                if (record.ExitType == BurnExitType.VfxUnlock)
                    await ExecuteVfxUnlock(record);
                else if (record.ExitType == BurnExitType.VfxPoolUnlock)
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

        private static async Task ExecuteVfxUnlock(ProcessedBurnRecord record)
        {
            var lockState = VBTCBridgeLockState.GetByLockId(record.VfxLockId);
            if (lockState == null)
            {
                record.Status = BurnExitStatus.Failed;
                ErrorLogUtility.LogError($"[BurnExitConsensus] Lock not found for lockId {record.VfxLockId}", "BurnExitConsensusService");
                return;
            }

            var result = await VBTCService.CreateBridgeUnlockTx(
                lockState.SmartContractUID,
                lockState.OwnerAddress,
                record.VfxLockId,
                lockState.Amount,
                record.BaseBurnTxHash,
                record.ConsensusVotes);

            if (result.Success)
            {
                record.Status = BurnExitStatus.Complete;
                record.CompletionVfxTxHash = result.TxHashOrError;
                LogUtility.Log($"[BurnExitConsensus] VFX unlock TX: {result.TxHashOrError} for burn {record.BaseBurnTxHash}", "BurnExitConsensusService");
            }
            else
            {
                record.Status = BurnExitStatus.Failed;
                ErrorLogUtility.LogError($"[BurnExitConsensus] VFX unlock failed: {result.TxHashOrError}", "BurnExitConsensusService");
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
        /// The entire pipeline runs caster-side with no user wallet / user node involvement:
        ///   (1) Broadcast <see cref="TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC"/> on VFX to record the intent.
        ///   (2) Wait for that TX to be confirmed in a block so state is consensus-valid.
        ///   (3) Coordinate a FROST sign-only round against the deposit UTXO to produce a signed BTC tx.
        ///   (4) Broadcast the signed BTC tx to the Bitcoin network.
        ///   (5) Broadcast <see cref="TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC_COMPLETE"/> on VFX to mark the burn
        ///       complete via <see cref="StateData.ApplyVBTCBridgeExitToBTCComplete"/>.
        /// </summary>
        private static async Task ExecuteBtcExit(ProcessedBurnRecord record)
        {
            var lockId = $"btcexit_{record.BaseBurnTxHash[..Math.Min(16, record.BaseBurnTxHash.Length)]}";
            var ownerAddress = Globals.ValidatorAddress;

            // Guard: non-validator casters can't run this flow — they'd fail at Account lookup / FROST signing.
            if (string.IsNullOrWhiteSpace(ownerAddress))
            {
                record.Status = BurnExitStatus.Failed;
                ErrorLogUtility.LogError($"[BurnExitConsensus] Cannot execute BTC exit: Globals.ValidatorAddress is empty (this node is not a validator). Burn: {record.BaseBurnTxHash}",
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

            // Find a vBTC contract UID
            var scUID = "";
            var vbtcContracts = VBTCContractV2.GetAllContracts();
            if (vbtcContracts != null && vbtcContracts.Any())
            {
                scUID = vbtcContracts.First().SmartContractUID;
                LogUtility.Log($"[BurnExitConsensus] BTC exit using contract {scUID} for burn {record.BaseBurnTxHash}", "BurnExitConsensusService.ExecuteBtcExit()");
            }

            if (string.IsNullOrEmpty(scUID))
            {
                record.Status = BurnExitStatus.Failed;
                ErrorLogUtility.LogError($"[BurnExitConsensus] No vBTC contract for BTC exit {record.BaseBurnTxHash}", "BurnExitConsensusService");
                return;
            }

            // ----------------------------------------------------------------
            // Step 1: Broadcast VFX EXIT_TO_BTC TX (records the pending exit state)
            // ----------------------------------------------------------------
            record.Status = BurnExitStatus.VfxTxCreated;
            var exitResult = await VBTCService.CreateBridgeExitToBTCTx(
                scUID, ownerAddress, lockId, record.Amount,
                record.BtcDestination, record.BaseBurnTxHash,
                record.ConsensusVotes);

            if (!exitResult.Success)
            {
                // Another caster may have already broadcast the EXIT_TO_BTC TX for this burn — that's fine, not a failure.
                // TransactionValidatorService rejects a second EXIT_TO_BTC for the same BaseBurnTxHash with "Duplicate Base burn transaction for bridge exit to BTC".
                // When we're the loser, the winning caster will drive completion; we should treat the burn as handled, not mark Failed and retry.
                var err = exitResult.TxHashOrError ?? string.Empty;
                if (err.IndexOf("Duplicate", StringComparison.OrdinalIgnoreCase) >= 0
                    || err.IndexOf("already", StringComparison.OrdinalIgnoreCase) >= 0
                    || err.IndexOf("pending bridge exit to BTC", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    record.Status = BurnExitStatus.Complete;
                    LogUtility.Log($"[BurnExitConsensus] EXIT_TO_BTC already broadcast by another caster for burn {record.BaseBurnTxHash} — treating as handled. Detail: {err}",
                        "BurnExitConsensusService.ExecuteBtcExit()");
                    return;
                }

                record.Status = BurnExitStatus.Failed;
                ErrorLogUtility.LogError($"[BurnExitConsensus] BTC exit VFX TX failed: {err}", "BurnExitConsensusService");
                return;
            }

            record.CompletionVfxTxHash = exitResult.TxHashOrError;
            LogUtility.Log($"[BurnExitConsensus] BTC exit VFX TX: {exitResult.TxHashOrError}. Waiting for block inclusion before FROST.", "BurnExitConsensusService");

            // ----------------------------------------------------------------
            // Step 2: Wait for VFX TX confirmation before FROST
            // ----------------------------------------------------------------
            record.Status = BurnExitStatus.FrostInProgress;
            var confirmed = await WaitForVfxTxConfirmation(exitResult.TxHashOrError, 120_000);
            if (!confirmed)
            {
                LogUtility.Log($"[BurnExitConsensus] VFX TX not confirmed in time for {record.BaseBurnTxHash}. Stuck exit detection will retry.", "BurnExitConsensusService");
                return; // Keep FrostInProgress — stuck exit detection handles retries
            }

            // ----------------------------------------------------------------
            // Step 3: FROST sign-only + Step 4: broadcast BTC + Step 5: EXIT_TO_BTC_COMPLETE VFX TX
            // ----------------------------------------------------------------
            try
            {
                // Use signOnly = true so CompleteWithdrawal returns the signed BTC hex WITHOUT creating
                // the legacy VBTC_V2_WITHDRAWAL_COMPLETE TX (which is the wrong TX type for the burn-exit flow).
                var frostResult = await VBTCService.CompleteWithdrawal(
                    scUID, exitResult.TxHashOrError,
                    delegatedAmount: record.Amount,
                    delegatedBTCDestination: record.BtcDestination,
                    delegatedFeeRate: 10,
                    signOnly: true);

                if (!frostResult.Success)
                {
                    LogUtility.Log($"[BurnExitConsensus] FROST sign-only failed: {frostResult.ErrorMessage}. Stuck exit will retry.", "BurnExitConsensusService");
                    return;
                }

                // frostResult.BTCTxHash contains the signed tx hex when signOnly = true
                var signedTxHex = frostResult.BTCTxHash;
                if (string.IsNullOrWhiteSpace(signedTxHex))
                {
                    LogUtility.Log($"[BurnExitConsensus] FROST sign-only produced empty signed hex for {record.BaseBurnTxHash}. Retrying later.", "BurnExitConsensusService");
                    return;
                }

                // Step 4: Broadcast signed BTC tx
                string btcTxHash;
                try
                {
                    var btcNetwork = Globals.BTCNetwork;
                    var signedBtcTx = NBitcoin.Transaction.Parse(signedTxHex, btcNetwork);
                    var broadcastResult = await BitcoinTransactionService.BroadcastTransaction(signedBtcTx);
                    if (!broadcastResult.Success)
                    {
                        LogUtility.Log($"[BurnExitConsensus] BTC broadcast failed for {record.BaseBurnTxHash}: {broadcastResult.ErrorMessage}. Stuck exit will retry.",
                            "BurnExitConsensusService");
                        return;
                    }
                    btcTxHash = broadcastResult.TxHash;
                }
                catch (Exception btcEx)
                {
                    ErrorLogUtility.LogError($"[BurnExitConsensus] Exception broadcasting signed BTC tx for {record.BaseBurnTxHash}: {btcEx}", "BurnExitConsensusService");
                    return;
                }

                record.BtcTxHash = btcTxHash;
                LogUtility.Log($"[BurnExitConsensus] BTC withdrawal broadcast. BTC TxHash: {btcTxHash}. Now broadcasting VFX EXIT_TO_BTC_COMPLETE.", "BurnExitConsensusService");

                // Step 5: Broadcast VFX EXIT_TO_BTC_COMPLETE TX (the ONLY TX type that StateData.ApplyVBTCBridgeExitToBTCComplete accepts)
                var completeResult = await VBTCService.CreateBridgeExitToBTCCompleteTx(
                    ownerAddress,
                    record.BaseBurnTxHash,
                    btcTxHash);

                if (!completeResult.Success)
                {
                    // Not fatal — BTC has already been sent. Another caster may finalize the completion TX.
                    LogUtility.Log($"[BurnExitConsensus] BTC sent ({btcTxHash}) but EXIT_TO_BTC_COMPLETE broadcast failed: {completeResult.TxHashOrError}. Stuck exit detection will retry the completion TX.",
                        "BurnExitConsensusService");
                    return;
                }

                record.Status = BurnExitStatus.Complete;
                LogUtility.Log($"[BurnExitConsensus] BTC exit complete! BTC TX: {btcTxHash}, VFX Complete TX: {completeResult.TxHashOrError}", "BurnExitConsensusService");
            }
            catch (Exception frostEx)
            {
                ErrorLogUtility.LogError($"[BurnExitConsensus] FROST/BTC-broadcast exception: {frostEx}", "BurnExitConsensusService");
            }
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
        /// Detects exits stuck in InConsensus/FrostInProgress for too long.
        /// Resets them to Pending for re-consensus (new handler via lowest-hash-wins).
        /// </summary>
        private static async Task CheckForStuckExits()
        {
            var currentBlock = Globals.LastBlock?.Height ?? 0;
            var stuckExits = _processedBurns.Values
                .Where(r => (r.Status == BurnExitStatus.InConsensus || r.Status == BurnExitStatus.FrostInProgress) &&
                            currentBlock - r.DetectedAtBlock > STUCK_EXIT_BLOCK_THRESHOLD)
                .ToList();

            foreach (var stuck in stuckExits)
            {
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
        private static string BurnTypeString(BurnExitType t) => t == BurnExitType.BtcExit ? "BTC_EXIT" : "EXIT";

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
