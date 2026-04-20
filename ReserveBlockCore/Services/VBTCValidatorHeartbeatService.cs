using Newtonsoft.Json;
using ReserveBlockCore.Bitcoin.Models;
using ReserveBlockCore.Bitcoin.Services;
using ReserveBlockCore.Data;
using ReserveBlockCore.Models;
using ReserveBlockCore.P2P;
using ReserveBlockCore.Utilities;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace ReserveBlockCore.Services
{
    /// <summary>
    /// Background service that maintains vBTC V2 validator heartbeat status.
    /// 
    /// Architecture (block-scan based):
    /// - On-chain heartbeat TXs are the SOURCE OF TRUTH for validator liveness.
    /// - Validators periodically broadcast HEARTBEAT TXs every HEARTBEAT_TX_INTERVAL blocks.
    /// - Active validators are derived by scanning the last 1000 blocks (see VBTCValidatorRegistry).
    /// - No persistent DB, no HTTP pings — the blockchain is the single source of truth.
    /// </summary>
    public class VBTCValidatorHeartbeatService
    {
        /// <summary>
        /// How often validators send on-chain heartbeat TXs (in blocks).
        /// ~500 blocks ≈ 1.4 hours at 10s/block.
        /// </summary>
        public const long HEARTBEAT_TX_INTERVAL = 500;

        /// <summary>
        /// How many blocks to wait after coming online before sending a heartbeat TX.
        /// Kept short so the network learns about the validator quickly.
        /// </summary>
        public const int STARTUP_HEARTBEAT_BLOCK_WAIT = 2;

        #region Main Loop

        /// <summary>
        /// Starts the self-heartbeat TX loop. Only runs on validator nodes.
        /// Periodically sends on-chain HEARTBEAT TXs to prove liveness.
        /// </summary>
        public static async Task VBTCValidatorHeartbeatLoop()
        {
            if (string.IsNullOrEmpty(Globals.ValidatorAddress))
            {
                LogUtility.Log("Not a validator - VBTCValidatorHeartbeatLoop will not run",
                    "VBTCValidatorHeartbeatService.VBTCValidatorHeartbeatLoop()");
                return;
            }

            // Wait for ports to be verified open before starting heartbeat loop
            while (!Globals.PortsOpened && !string.IsNullOrEmpty(Globals.ValidatorAddress))
            {
                await Task.Delay(5000);
            }

            // If validator was stopped (e.g. port check failed), exit the loop
            if (string.IsNullOrEmpty(Globals.ValidatorAddress))
                return;

            LogUtility.Log("Starting vBTC V2 validator self-heartbeat TX loop",
                "VBTCValidatorHeartbeatService.VBTCValidatorHeartbeatLoop()");

            // Start the self-heartbeat TX loop
            await SelfHeartbeatTxLoop();
        }

        #endregion

        #region Self-Heartbeat TX Loop

        /// <summary>
        /// Periodically sends on-chain VBTC_V2_VALIDATOR_HEARTBEAT transactions to prove liveness.
        /// Runs every 5 minutes, but only sends a TX if HEARTBEAT_TX_INTERVAL blocks have passed
        /// since the last heartbeat, or if the validator is not visible in the registry.
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
                        var myValidator = VBTCValidatorRegistry.GetValidator(Globals.ValidatorAddress);

                        bool shouldSendHeartbeat = false;
                        string reason = "";

                        if (myValidator == null)
                        {
                            // Not visible in registry — might have fallen out of the scan window
                            // or never registered. If we're supposed to be a validator, send heartbeat.
                            shouldSendHeartbeat = true;
                            reason = "not visible in block-scan registry — re-announcing";
                        }
                        else if (!myValidator.IsActive)
                        {
                            shouldSendHeartbeat = true;
                            reason = "self-healing: registry shows inactive";
                        }
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
        /// will see this TX when scanning recent blocks via VBTCValidatorRegistry.
        /// </summary>
        public static async Task<bool> SendHeartbeatTransaction()
        {
            try
            {
                if (string.IsNullOrEmpty(Globals.ValidatorAddress))
                    return false;

                // Guard: Check if there's already a pending heartbeat TX in the mempool
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
                        return true;
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

                var existingValidator = VBTCValidatorRegistry.GetValidator(Globals.ValidatorAddress);
                var previousIP = existingValidator?.IPAddress ?? "";

                var signature = SignatureService.CreateSignature(validator.Address, AccountData.GetPrivateKey(validator), validator.PublicKey);

                var heartbeatTx = new Transaction
                {
                    Timestamp = TimeUtil.GetTime(),
                    FromAddress = validator.Address,
                    ToAddress = validator.Address.ToAddressNormalize(),
                    Amount = 0M.ToNormalizeDecimal(),
                    Fee = 0M.ToNormalizeDecimal(),
                    Nonce = sTreiAcct.Nonce,
                    TransactionType = TransactionType.VBTC_V2_VALIDATOR_HEARTBEAT,
                    Data = JsonConvert.SerializeObject(new
                    {
                        ValidatorAddress = validator.Address,
                        IPAddress = ipAddress,
                        FrostPublicKey = validator.PublicKey,
                        BaseAddress = Globals.ValidatorBaseAddress,
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
    }
}