using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ReserveBlockCore.Bitcoin.Models;
using ReserveBlockCore.Data;
using ReserveBlockCore.Models;
using ReserveBlockCore.P2P;
using ReserveBlockCore.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace ReserveBlockCore.Services
{
    /// <summary>
    /// Background service that maintains vBTC V2 validator heartbeat status.
    /// 
    /// Architecture (post-overhaul):
    /// - On-chain heartbeat TXs are the SOURCE OF TRUTH for validator liveness.
    ///   Every node processes these TXs in BlockValidatorService, ensuring consensus.
    /// - HTTP pings are used for monitoring/logging only — they do NOT mark validators inactive.
    /// - Staleness is determined by blockchain height: if a validator's LastHeartbeatBlock
    ///   falls behind currentBlock by more than STALE_THRESHOLD, they are considered inactive.
    /// - Validators periodically broadcast HEARTBEAT TXs every HEARTBEAT_TX_INTERVAL blocks.
    /// - On startup (after sync), all nodes scan recent blocks to rebuild validator state.
    /// </summary>
    public class VBTCValidatorHeartbeatService
    {
        private static readonly HttpClient httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        /// <summary>
        /// How often validators send on-chain heartbeat TXs (in blocks).
        /// ~500 blocks ≈ 1.4 hours at 10s/block.
        /// </summary>
        public const long HEARTBEAT_TX_INTERVAL = 500;

        /// <summary>
        /// How many blocks before a validator is considered stale/inactive.
        /// Set to 3x the heartbeat interval for tolerance (missed heartbeat + propagation delay).
        /// ~1500 blocks ≈ 4.2 hours.
        /// </summary>
        public const long STALE_THRESHOLD = 1500;

        /// <summary>
        /// How far back to scan blocks on startup to rebuild validator state.
        /// ~720 blocks ≈ 2 hours at 10s/block (covers at least one full heartbeat cycle).
        /// </summary>
        public const long STARTUP_BLOCK_SCAN_WINDOW = 720;

        /// <summary>
        /// How many blocks to wait after coming online before sending a heartbeat TX.
        /// Kept short so the network learns about the validator quickly.
        /// </summary>
        public const int STARTUP_HEARTBEAT_BLOCK_WAIT = 2;

        // Tracks consecutive HTTP heartbeat failures per validator address (monitoring only)
        private static readonly Dictionary<string, int> _failureCounts = new Dictionary<string, int>();

        // 3 consecutive failures for logging escalation
        private const int MaxConsecutiveFailures = 3;

        #region Main Loops

        /// <summary>
        /// Combined heartbeat loop - only runs on validator nodes.
        /// 1. Monitors other validators via HTTP pings (non-authoritative — logging only)
        /// 2. Periodically sends on-chain heartbeat TXs to prove liveness
        /// 3. Self-heals if local record shows inactive or stale
        /// </summary>
        public static async Task VBTCValidatorHeartbeatLoop()
        {
            if (string.IsNullOrEmpty(Globals.ValidatorAddress))
            {
                LogUtility.Log("Not a validator - VBTCValidatorHeartbeatLoop will not run",
                    "VBTCValidatorHeartbeatService.VBTCValidatorHeartbeatLoop()");
                return;
            }

            LogUtility.Log("Starting vBTC V2 validator heartbeat loop (on-chain + HTTP monitoring)",
                "VBTCValidatorHeartbeatService.VBTCValidatorHeartbeatLoop()");

            // Start the self-heartbeat TX loop in parallel
            _ = Task.Run(SelfHeartbeatTxLoop);

            while (true)
            {
                try
                {
                    // Wait 10 minutes between HTTP monitoring checks
                    await Task.Delay(TimeSpan.FromMinutes(10));

                    // Get all validators from local database
                    var validators = VBTCValidator.GetAllValidators();
                    if (validators == null || !validators.Any())
                    {
                        LogUtility.Log("No validators found in database",
                            "VBTCValidatorHeartbeatService.VBTCValidatorHeartbeatLoop()");
                        continue;
                    }

                    LogUtility.Log($"Checking heartbeat for {validators.Count} validators",
                        "VBTCValidatorHeartbeatService.VBTCValidatorHeartbeatLoop()");

                    var currentBlock = Globals.LastBlock.Height;

                    foreach (var validator in validators)
                    {
                        // Handle self - we know we're alive, update local record
                        if (validator.ValidatorAddress == Globals.ValidatorAddress)
                        {
                            validator.LastHeartbeatBlock = currentBlock;
                            validator.IsActive = true;
                            VBTCValidator.SaveValidator(validator);
                            _failureCounts.Remove(validator.ValidatorAddress);
                            continue;
                        }

                        // Skip already-inactive validators for HTTP monitoring
                        if (!validator.IsActive)
                        {
                            _failureCounts.Remove(validator.ValidatorAddress);
                            continue;
                        }

                        // Check blockchain-based staleness FIRST
                        if (currentBlock - validator.LastHeartbeatBlock > STALE_THRESHOLD)
                        {
                            LogUtility.Log($"Validator {validator.ValidatorAddress} is STALE (LastHeartbeatBlock: {validator.LastHeartbeatBlock}, Current: {currentBlock}, Gap: {currentBlock - validator.LastHeartbeatBlock} > {STALE_THRESHOLD}). Marking inactive.",
                                "VBTCValidatorHeartbeatService.VBTCValidatorHeartbeatLoop()");
                            VBTCValidator.MarkInactive(validator.ValidatorAddress);
                            _failureCounts.Remove(validator.ValidatorAddress);
                            continue;
                        }

                        // HTTP ping for monitoring (NON-AUTHORITATIVE — does NOT mark inactive)
                        try
                        {
                            var url = $"http://{validator.IPAddress}:{Globals.ValAPIPort}/valapi/Validator/HeartBeat";
                            var response = await httpClient.GetAsync(url);

                            if (response.IsSuccessStatusCode)
                            {
                                // Optimistic fast-path: update heartbeat on successful ping
                                // This supplements the on-chain heartbeat for faster detection
                                validator.LastHeartbeatBlock = currentBlock;
                                VBTCValidator.SaveValidator(validator);
                                _failureCounts.Remove(validator.ValidatorAddress);

                                LogUtility.Log($"HTTP heartbeat success: {validator.ValidatorAddress} ({validator.IPAddress})",
                                    "VBTCValidatorHeartbeatService.VBTCValidatorHeartbeatLoop()");
                            }
                            else
                            {
                                // Log but do NOT mark inactive — blockchain is the authority
                                RecordHttpFailure(validator.ValidatorAddress, $"HTTP {response.StatusCode}");
                            }
                        }
                        catch (HttpRequestException ex)
                        {
                            RecordHttpFailure(validator.ValidatorAddress, $"network error: {ex.Message}");
                        }
                        catch (TaskCanceledException)
                        {
                            RecordHttpFailure(validator.ValidatorAddress, "timeout");
                        }
                        catch (Exception ex)
                        {
                            RecordHttpFailure(validator.ValidatorAddress, $"error: {ex.Message}");
                        }
                    }

                    LogUtility.Log("Heartbeat check complete",
                        "VBTCValidatorHeartbeatService.VBTCValidatorHeartbeatLoop()");
                }
                catch (Exception ex)
                {
                    ErrorLogUtility.LogError($"Error in heartbeat loop: {ex}",
                        "VBTCValidatorHeartbeatService.VBTCValidatorHeartbeatLoop()");
                }
            }
        }

        /// <summary>
        /// Periodically sends on-chain VBTC_V2_VALIDATOR_HEARTBEAT transactions to prove liveness.
        /// Runs every 5 minutes, but only sends a TX if HEARTBEAT_TX_INTERVAL blocks have passed
        /// since the last heartbeat, or if the validator's local record shows inactive/stale.
        /// </summary>
        private static async Task SelfHeartbeatTxLoop()
        {
            try
            {
                // Initial delay to let startup complete
                await Task.Delay(TimeSpan.FromMinutes(2));

                LogUtility.Log("Starting self-heartbeat TX loop",
                    "VBTCValidatorHeartbeatService.SelfHeartbeatTxLoop()");

                while (true)
                {
                    try
                    {
                        if (string.IsNullOrEmpty(Globals.ValidatorAddress) || !Globals.IsChainSynced)
                        {
                            await Task.Delay(TimeSpan.FromMinutes(1));
                            continue;
                        }

                        var currentBlock = Globals.LastBlock.Height;
                        var myValidator = VBTCValidator.GetValidator(Globals.ValidatorAddress);

                        if (myValidator == null)
                        {
                            // Not yet registered — skip
                            await Task.Delay(TimeSpan.FromMinutes(5));
                            continue;
                        }

                        bool shouldSendHeartbeat = false;
                        string reason = "";

                        // Check 1: Self-healing — if marked inactive, send immediately
                        if (!myValidator.IsActive)
                        {
                            shouldSendHeartbeat = true;
                            reason = "self-healing: local record shows inactive";
                        }
                        // Check 2: Periodic heartbeat — if enough blocks have passed
                        else if (currentBlock - myValidator.LastHeartbeatBlock >= HEARTBEAT_TX_INTERVAL)
                        {
                            shouldSendHeartbeat = true;
                            reason = $"periodic: {currentBlock - myValidator.LastHeartbeatBlock} blocks since last heartbeat (threshold: {HEARTBEAT_TX_INTERVAL})";
                        }

                        if (shouldSendHeartbeat)
                        {
                            LogUtility.Log($"Sending on-chain heartbeat TX. Reason: {reason}",
                                "VBTCValidatorHeartbeatService.SelfHeartbeatTxLoop()");

                            var success = await SendHeartbeatTransaction();
                            if (success)
                            {
                                LogUtility.Log("On-chain heartbeat TX sent successfully",
                                    "VBTCValidatorHeartbeatService.SelfHeartbeatTxLoop()");
                            }
                            else
                            {
                                ErrorLogUtility.LogError("Failed to send on-chain heartbeat TX",
                                    "VBTCValidatorHeartbeatService.SelfHeartbeatTxLoop()");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        ErrorLogUtility.LogError($"Error in self-heartbeat TX loop: {ex}",
                            "VBTCValidatorHeartbeatService.SelfHeartbeatTxLoop()");
                    }

                    // Check every 5 minutes
                    await Task.Delay(TimeSpan.FromMinutes(5));
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Fatal error in self-heartbeat TX loop: {ex}",
                    "VBTCValidatorHeartbeatService.SelfHeartbeatTxLoop()");
            }
        }

        #endregion

        #region On-Chain Heartbeat TX

        /// <summary>
        /// Sends a VBTC_V2_VALIDATOR_HEARTBEAT transaction to the network.
        /// This is the primary mechanism for proving validator liveness — all nodes
        /// process this TX in BlockValidatorService and update their local DB.
        /// </summary>
        public static async Task<bool> SendHeartbeatTransaction()
        {
            try
            {
                if (string.IsNullOrEmpty(Globals.ValidatorAddress))
                    return false;

                // Guard: Check if there's already a pending heartbeat TX in the mempool
                // This prevents duplicate TXs (same nonce) which cause block validation failures (-13 rollback)
                var mempool = TransactionData.GetPool();
                if (mempool != null)
                {
                    var pendingHeartbeat = mempool.FindAll()
                        .Where(x => x.FromAddress == Globals.ValidatorAddress && 
                               (x.TransactionType == TransactionType.VBTC_V2_VALIDATOR_HEARTBEAT ||
                                x.TransactionType == TransactionType.VBTC_V2_VALIDATOR_REGISTER))
                        .FirstOrDefault();
                    
                    if (pendingHeartbeat != null)
                    {
                        LogUtility.Log($"Skipping heartbeat TX - already have pending TX in mempool: {pendingHeartbeat.Hash} (Type: {pendingHeartbeat.TransactionType})",
                            "VBTCValidatorHeartbeatService.SendHeartbeatTransaction()");
                        return true; // Return true since we already have one pending
                    }
                }

                var validator = AccountData.GetSingleAccount(Globals.ValidatorAddress);
                if (validator == null) return false;

                var sTreiAcct = StateData.GetSpecificAccountStateTrei(validator.Address);
                if (sTreiAcct == null) return false;

                var ipAddress = Globals.ReportedIP;
                if (string.IsNullOrEmpty(ipAddress))
                {
                    LogUtility.Log("Cannot send heartbeat TX - IP address not reported yet",
                        "VBTCValidatorHeartbeatService.SendHeartbeatTransaction()");
                    return false;
                }

                var existingValidator = VBTCValidator.GetValidator(Globals.ValidatorAddress);
                var previousIP = existingValidator?.IPAddress ?? "";

                var signature = SignatureService.CreateSignature(validator.Address, AccountData.GetPrivateKey(validator), validator.PublicKey);

                var heartbeatTx = new Transaction
                {
                    Timestamp = TimeUtil.GetTime(),
                    FromAddress = validator.Address,
                    ToAddress = validator.Address,
                    Amount = 0M,
                    Fee = 0M,
                    Nonce = sTreiAcct.Nonce,
                    TransactionType = TransactionType.VBTC_V2_VALIDATOR_HEARTBEAT,
                    Data = JsonConvert.SerializeObject(new
                    {
                        ValidatorAddress = validator.Address,
                        IPAddress = ipAddress,
                        FrostPublicKey = validator.PublicKey,
                        ReactivationBlockHeight = Globals.LastBlock.Height,
                        PreviousIPAddress = previousIP,
                        Signature = signature
                    })
                };

                heartbeatTx.Build();
                var privateKey = AccountData.GetPrivateKey(validator);
                var txHash = heartbeatTx.Hash;
                var valTxSignature = SignatureService.CreateSignature(txHash, privateKey, validator.PublicKey);
                heartbeatTx.Signature = valTxSignature;

                heartbeatTx.ToAddress = heartbeatTx.ToAddress.ToAddressNormalize();
                heartbeatTx.Amount = heartbeatTx.Amount.ToNormalizeDecimal();
                var result = await TransactionValidatorService.VerifyTX(heartbeatTx);

                if (result.Item1)
                {
                    if (heartbeatTx.TransactionRating == null)
                    {
                        var rating = await TransactionRatingService.GetTransactionRating(heartbeatTx);
                        heartbeatTx.TransactionRating = rating;
                    }

                    await TransactionData.AddToPool(heartbeatTx);
                    await P2PClient.SendTXMempool(heartbeatTx);

                    LogUtility.Log($"vBTC V2 heartbeat TX broadcast: {heartbeatTx.Hash} (IP: {ipAddress})",
                        "VBTCValidatorHeartbeatService.SendHeartbeatTransaction()");
                    return true;
                }
                else
                {
                    ErrorLogUtility.LogError($"Heartbeat TX verification failed: {result.Item2}",
                        "VBTCValidatorHeartbeatService.SendHeartbeatTransaction()");
                    return false;
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error sending heartbeat TX: {ex}",
                    "VBTCValidatorHeartbeatService.SendHeartbeatTransaction()");
                return false;
            }
        }

        #endregion

        #region Post-Sync Block Scan

        /// <summary>
        /// Scans recent blocks for VBTC validator lifecycle transactions to rebuild
        /// the local validator database state. Called after chain sync completes.
        /// 
        /// This ensures ALL nodes (validators and non-validators) have an accurate,
        /// consensus-based view of which validators are active and their current IPs.
        /// </summary>
        public static async Task ScanRecentBlocksForValidatorState()
        {
            try
            {
                var currentHeight = Globals.LastBlock.Height;
                var scanFromHeight = Math.Max(0, currentHeight - STARTUP_BLOCK_SCAN_WINDOW);

                LogUtility.Log($"Scanning blocks {scanFromHeight} to {currentHeight} for validator state TXs ({currentHeight - scanFromHeight} blocks)",
                    "VBTCValidatorHeartbeatService.ScanRecentBlocksForValidatorState()");

                int heartbeatCount = 0;
                int registerCount = 0;
                int exitCount = 0;

                for (long height = scanFromHeight; height <= currentHeight; height++)
                {
                    try
                    {
                        var block = BlockchainData.GetBlockByHeight(height);
                        if (block == null || block.Transactions == null || !block.Transactions.Any())
                            continue;

                        foreach (var tx in block.Transactions)
                        {
                            if (tx.TransactionType == TransactionType.VBTC_V2_VALIDATOR_REGISTER)
                            {
                                ProcessValidatorRegisterFromBlock(tx, block.Height);
                                registerCount++;
                            }
                            else if (tx.TransactionType == TransactionType.VBTC_V2_VALIDATOR_HEARTBEAT)
                            {
                                ProcessValidatorHeartbeatFromBlock(tx, block.Height);
                                heartbeatCount++;
                            }
                            else if (tx.TransactionType == TransactionType.VBTC_V2_VALIDATOR_EXIT)
                            {
                                ProcessValidatorExitFromBlock(tx, block.Height);
                                exitCount++;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // Don't fail the entire scan for one bad block
                        ErrorLogUtility.LogError($"Error scanning block {height}: {ex.Message}",
                            "VBTCValidatorHeartbeatService.ScanRecentBlocksForValidatorState()");
                    }
                }

                // After scanning, apply staleness check to mark truly inactive validators
                var allValidators = VBTCValidator.GetAllValidators();
                if (allValidators != null)
                {
                    foreach (var validator in allValidators)
                    {
                        if (validator.IsActive && currentHeight - validator.LastHeartbeatBlock > STALE_THRESHOLD)
                        {
                            LogUtility.Log($"Post-scan staleness: Marking {validator.ValidatorAddress} inactive (LastHeartbeat: {validator.LastHeartbeatBlock}, Current: {currentHeight})",
                                "VBTCValidatorHeartbeatService.ScanRecentBlocksForValidatorState()");
                            VBTCValidator.MarkInactive(validator.ValidatorAddress);
                        }
                    }
                }

                LogUtility.Log($"Block scan complete. Found: {registerCount} registrations, {heartbeatCount} heartbeats, {exitCount} exits",
                    "VBTCValidatorHeartbeatService.ScanRecentBlocksForValidatorState()");
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error scanning blocks for validator state: {ex}",
                    "VBTCValidatorHeartbeatService.ScanRecentBlocksForValidatorState()");
            }
        }

        private static void ProcessValidatorRegisterFromBlock(Transaction tx, long blockHeight)
        {
            try
            {
                var jobj = JObject.Parse(tx.Data);
                var validatorAddress = jobj["ValidatorAddress"]?.ToObject<string>();
                var ipAddress = jobj["IPAddress"]?.ToObject<string>();
                var frostPublicKey = jobj["FrostPublicKey"]?.ToObject<string>();
                var registrationBlockHeight = jobj["RegistrationBlockHeight"]?.ToObject<long>();
                var signature = jobj["Signature"]?.ToObject<string>();

                if (string.IsNullOrEmpty(validatorAddress)) return;

                var existing = VBTCValidator.GetValidator(validatorAddress);
                if (existing != null)
                {
                    // Only update if block height is newer than what we have
                    if (blockHeight > existing.LastHeartbeatBlock)
                    {
                        existing.LastHeartbeatBlock = blockHeight;
                        existing.IsActive = true;
                        if (!string.IsNullOrEmpty(ipAddress)) existing.IPAddress = ipAddress;
                        if (!string.IsNullOrEmpty(frostPublicKey)) existing.FrostPublicKey = frostPublicKey;
                        VBTCValidator.SaveValidator(existing);
                    }
                }
                else
                {
                    var vbtcValidator = new VBTCValidator
                    {
                        ValidatorAddress = validatorAddress,
                        IPAddress = ipAddress ?? "",
                        RegistrationBlockHeight = registrationBlockHeight ?? blockHeight,
                        LastHeartbeatBlock = blockHeight,
                        IsActive = true,
                        FrostPublicKey = frostPublicKey ?? "",
                        RegistrationSignature = signature,
                        RegisterTransactionHash = tx.Hash
                    };
                    VBTCValidator.SaveValidator(vbtcValidator);
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error processing REGISTER TX in block scan: {ex.Message}",
                    "VBTCValidatorHeartbeatService.ProcessValidatorRegisterFromBlock()");
            }
        }

        private static void ProcessValidatorHeartbeatFromBlock(Transaction tx, long blockHeight)
        {
            try
            {
                var jobj = JObject.Parse(tx.Data);
                var validatorAddress = jobj["ValidatorAddress"]?.ToObject<string>();
                var ipAddress = jobj["IPAddress"]?.ToObject<string>();
                var frostPublicKey = jobj["FrostPublicKey"]?.ToObject<string>();

                if (string.IsNullOrEmpty(validatorAddress)) return;

                var existing = VBTCValidator.GetValidator(validatorAddress);
                if (existing != null)
                {
                    // Only update if block height is newer
                    if (blockHeight > existing.LastHeartbeatBlock)
                    {
                        existing.IPAddress = ipAddress ?? existing.IPAddress;
                        existing.IsActive = true;
                        existing.LastHeartbeatBlock = blockHeight;
                        if (!string.IsNullOrEmpty(frostPublicKey))
                            existing.FrostPublicKey = frostPublicKey;
                        VBTCValidator.SaveValidator(existing);
                    }
                }
                else
                {
                    // Create from heartbeat (may happen if we didn't see the registration)
                    var vbtcValidator = new VBTCValidator
                    {
                        ValidatorAddress = validatorAddress,
                        IPAddress = ipAddress ?? "",
                        RegistrationBlockHeight = blockHeight,
                        LastHeartbeatBlock = blockHeight,
                        IsActive = true,
                        FrostPublicKey = frostPublicKey ?? "",
                        RegisterTransactionHash = tx.Hash
                    };
                    VBTCValidator.SaveValidator(vbtcValidator);
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error processing HEARTBEAT TX in block scan: {ex.Message}",
                    "VBTCValidatorHeartbeatService.ProcessValidatorHeartbeatFromBlock()");
            }
        }

        private static void ProcessValidatorExitFromBlock(Transaction tx, long blockHeight)
        {
            try
            {
                var jobj = JObject.Parse(tx.Data);
                var validatorAddress = jobj["ValidatorAddress"]?.ToObject<string>();
                var exitBlockHeight = jobj["ExitBlockHeight"]?.ToObject<long>();

                if (string.IsNullOrEmpty(validatorAddress)) return;

                VBTCValidator.SetInactive(validatorAddress, tx.Hash, exitBlockHeight ?? blockHeight);
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error processing EXIT TX in block scan: {ex.Message}",
                    "VBTCValidatorHeartbeatService.ProcessValidatorExitFromBlock()");
            }
        }

        #endregion

        #region HTTP Monitoring (Non-Authoritative)

        /// <summary>
        /// Records an HTTP heartbeat failure for monitoring purposes.
        /// Does NOT mark validators as inactive — only logs warnings.
        /// Blockchain-based staleness detection handles inactivity.
        /// </summary>
        private static void RecordHttpFailure(string validatorAddress, string reason)
        {
            if (!_failureCounts.ContainsKey(validatorAddress))
                _failureCounts[validatorAddress] = 0;

            _failureCounts[validatorAddress]++;

            if (_failureCounts[validatorAddress] >= MaxConsecutiveFailures)
            {
                // DO NOT mark inactive — blockchain is the authority
                LogUtility.Log($"WARNING: Validator {validatorAddress} unreachable via HTTP for {MaxConsecutiveFailures} consecutive checks ({reason}). " +
                    $"Blockchain heartbeat will determine actual active status.",
                    "VBTCValidatorHeartbeatService.RecordHttpFailure()");
                _failureCounts.Remove(validatorAddress); // Reset counter
            }
            else
            {
                LogUtility.Log($"Validator {validatorAddress} HTTP heartbeat failure #{_failureCounts[validatorAddress]}/{MaxConsecutiveFailures} ({reason}). " +
                    $"Monitoring only — blockchain determines active status.",
                    "VBTCValidatorHeartbeatService.RecordHttpFailure()");
            }
        }

        #endregion
    }
}