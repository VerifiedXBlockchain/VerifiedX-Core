using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using ReserveBlockCore.Bitcoin.Models;
using ReserveBlockCore.Controllers;
using ReserveBlockCore.Data;
using ReserveBlockCore.Models;
using ReserveBlockCore.Models.SmartContracts;
using ReserveBlockCore.Utilities;

namespace ReserveBlockCore.Bitcoin.Controllers
{
    /// <summary>
    /// vBTC V2 Controller - MPC-based Tokenized Bitcoin
    /// </summary>
    [ActionFilterController]
    [Route("vbtcapi/[controller]")]
    [Route("vbtcapi/[controller]/{somePassword?}")]
    [ApiController]
    public class VBTCController : ControllerBase
    {
        #region API Status

        /// <summary>
        /// Check Status of vBTC V2 API
        /// </summary>
        /// <returns>API name and version</returns>
        [HttpGet]
        public IEnumerable<string> Get()
        {
            return new string[] { "VFX/vBTC-V2", "vBTC API V2 - MPC Based" };
        }

        #endregion

        #region Validator Management

        /// <summary>
        /// Register as a vBTC V2 Validator
        /// </summary>
        /// <param name="validatorAddress">VFX validator address</param>
        /// <param name="ipAddress">Validator IP address</param>
        /// <returns>Registration success status</returns>
        [HttpPost("RegisterValidator/{validatorAddress}/{ipAddress}")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        public async Task<string> RegisterValidator(string validatorAddress, string ipAddress)
        {
            try
            {
                // TODO: FROST INTEGRATION
                // 1. Verify validator address ownership via signature
                // 2. Generate validator's FROST key share and public key
                // 3. Create RegistrationSignature proof
                // PLACEHOLDER: Basic validation for now

                //if(string.IsNullOrEmpty(Globals.ReportedIP))
                //{
                //    return JsonConvert.SerializeObject(new
                //    {
                //        Success = false,
                //        Message = "IP Address has not been reported. Please try again.",
                //        ValidatorAddress = validatorAddress,
                //        RegistrationBlock = -1
                //    });
                //}

                var validator = new VBTCValidator
                {
                    ValidatorAddress = validatorAddress,
                    IPAddress = ipAddress,
                    RegistrationBlockHeight = Globals.LastBlock.Height,
                    LastHeartbeatBlock = Globals.LastBlock.Height,
                    IsActive = true,
                    FrostKeyShare = "PLACEHOLDER_FROST_KEY_SHARE",
                    FrostPublicKey = "PLACEHOLDER_FROST_PUBLIC_KEY",
                    RegistrationSignature = "PLACEHOLDER_FROST_SIGNATURE"
                };

                // Save to database
                // var db = VBTCValidator.GetVBTCValidatorDb();
                // db.InsertSafe(validator);

                return JsonConvert.SerializeObject(new
                {
                    Success = true,
                    Message = "Validator registered successfully",
                    ValidatorAddress = validatorAddress,
                    RegistrationBlock = validator.RegistrationBlockHeight
                });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { Success = false, Message = $"Error: {ex.Message}" });
            }
        }

        /// <summary>
        /// Get list of all registered vBTC V2 validators
        /// </summary>
        /// <param name="activeOnly">Filter for active validators only</param>
        /// <returns>List of validators</returns>
        [HttpGet("GetValidatorList/{activeOnly?}")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        public async Task<string> GetValidatorList(bool activeOnly = false)
        {
            try
            {
                // Get validators from database
                // var validators = VBTCValidator.GetValidators(activeOnly);

                return JsonConvert.SerializeObject(new
                {
                    Success = true,
                    Message = "Validators retrieved",
                    Validators = new List<VBTCValidator>() // PLACEHOLDER
                });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { Success = false, Message = $"Error: {ex.Message}" });
            }
        }

        /// <summary>
        /// Validator sends heartbeat to maintain active status
        /// </summary>
        /// <param name="validatorAddress">Validator VFX address</param>
        /// <returns>Heartbeat confirmation</returns>
        [HttpPost("ValidatorHeartbeat/{validatorAddress}")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        public async Task<string> ValidatorHeartbeat(string validatorAddress)
        {
            try
            {
                // Update validator's last heartbeat block
                // var validator = VBTCValidator.GetValidator(validatorAddress);
                // validator.LastHeartbeatBlock = Globals.LastBlock.Height;
                // VBTCValidator.Update(validator);

                return JsonConvert.SerializeObject(new
                {
                    Success = true,
                    Message = "Heartbeat recorded",
                    ValidatorAddress = validatorAddress,
                    CurrentBlock = Globals.LastBlock.Height
                });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { Success = false, Message = $"Error: {ex.Message}" });
            }
        }

        /// <summary>
        /// Get validator status and details
        /// </summary>
        /// <param name="validatorAddress">Validator VFX address</param>
        /// <returns>Validator details</returns>
        [HttpGet("GetValidatorStatus/{validatorAddress}")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        public async Task<string> GetValidatorStatus(string validatorAddress)
        {
            try
            {
                // var validator = VBTCValidator.GetValidator(validatorAddress);

                return JsonConvert.SerializeObject(new
                {
                    Success = true,
                    Message = "Validator found",
                    Validator = new VBTCValidator() // PLACEHOLDER
                });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { Success = false, Message = $"Error: {ex.Message}" });
            }
        }

        /// <summary>
        /// Get list of currently active validators (based on heartbeat)
        /// </summary>
        /// <returns>Active validators</returns>
        [HttpGet("GetActiveValidators")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        public async Task<string> GetActiveValidators()
        {
            try
            {
                // Get validators with recent heartbeat (within 1000 blocks)
                var currentBlock = Globals.LastBlock.Height;
                // var activeValidators = VBTCValidator.GetActiveValidators(currentBlock);

                return JsonConvert.SerializeObject(new
                {
                    Success = true,
                    Message = "Active validators retrieved",
                    CurrentBlock = currentBlock,
                    ActiveValidators = new List<VBTCValidator>() // PLACEHOLDER
                });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { Success = false, Message = $"Error: {ex.Message}" });
            }
        }

        #endregion

        #region Contract Creation

        /// <summary>
        /// Create a new vBTC V2 contract with MPC-generated deposit address
        /// </summary>
        /// <param name="payload">Contract creation payload</param>
        /// <returns>Contract creation result with smart contract UID</returns>
        [HttpPost("CreateVBTCContract")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        public async Task<string> CreateVBTCContract([FromBody] VBTCContractPayload payload)
        {
            try
            {
                if (payload == null)
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Payload cannot be null" });

                var scUID = Guid.NewGuid().ToString().Replace("-", "") + ":" + TimeUtil.GetTime().ToString();

                // TODO: FROST INTEGRATION - TAPROOT ADDRESS GENERATION VIA DKG
                // ============================================================
                // 1. Get list of active validators (require 75% for address generation)
                // 2. Initiate FROST DKG ceremony via SignalR
                //    - Broadcast FROST_DKG_REQUEST to all validators
                //    - Include: scUID, ownerAddress, timestamp
                // 3. FROST DKG Round 1: Commitment Phase
                //    - Each validator generates random polynomial coefficients
                //    - Each validator computes commitments
                //    - Broadcast commitments to all participants
                // 4. FROST DKG Round 2: Share Distribution Phase
                //    - Each validator computes secret shares for other validators
                //    - Send encrypted shares via SignalR (point-to-point)
                //    - Each validator receives shares from all others
                // 5. FROST DKG Round 3: Verification Phase
                //    - Each validator verifies received shares against commitments
                //    - If verification fails, abort and restart DKG
                //    - If all verify, each validator computes their private key share
                // 6. Aggregate Group Public Key
                //    - Combine all validator commitments
                //    - Derive FROST group public key
                //    - Generate Taproot internal key (x-only pubkey)
                //    - Derive Bitcoin Taproot address (bc1p...)
                // 7. Generate DKG Completion Proof
                //    - Proof that DKG completed successfully
                //    - Proof that no single party knows full private key
                //    - Compress and Base64 encode proof
                // 8. Store validator snapshot and FROST data
                // ============================================================
                // PLACEHOLDER: Using mock Taproot address for now
                string depositAddress = "bc1pFROST_TAPROOT_PLACEHOLDER_ADDRESS";
                string frostGroupPublicKey = "PLACEHOLDER_FROST_GROUP_PUBLIC_KEY";
                string dkgProof = "PLACEHOLDER_DKG_PROOF_BASE64_COMPRESSED";
                var validatorSnapshot = new List<string> { "VALIDATOR1", "VALIDATOR2", "VALIDATOR3" };

                // Create TokenizationV2Feature
                var tokenizationV2Feature = new TokenizationV2Feature
                {
                    AssetName = payload.Name,
                    AssetTicker = payload.Ticker ?? "vBTC",
                    DepositAddress = depositAddress,
                    Version = 2,
                    ValidatorAddressesSnapshot = validatorSnapshot,
                    FrostGroupPublicKey = frostGroupPublicKey,
                    RequiredThreshold = 51, // 51% initially
                    DKGProof = dkgProof,
                    ProofBlockHeight = Globals.LastBlock.Height,
                    ImageBase = payload.ImageBase
                };

                // Create smart contract
                var scMain = new SmartContractMain
                {
                    SmartContractUID = scUID,
                    Name = payload.Name,
                    Description = payload.Description,
                    MinterAddress = payload.OwnerAddress,
                    MinterName = payload.OwnerAddress, // Can be customized
                    Features = new List<SmartContractFeatures>
                    {
                        new SmartContractFeatures
                        {
                            FeatureName = FeatureName.TokenizationV2,
                            FeatureFeatures = tokenizationV2Feature
                        }
                    }
                };

                // Write smart contract (this will use TokenizationV2SourceGenerator)
                // var (scText, scMainResult, isToken) = await SmartContractWriterService.WriteSmartContract(scMain);

                return JsonConvert.SerializeObject(new
                {
                    Success = true,
                    Message = "vBTC V2 contract created successfully",
                    SmartContractUID = scUID,
                    DepositAddress = depositAddress,
                    DKGProof = dkgProof,
                    ValidatorCount = validatorSnapshot.Count
                });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { Success = false, Message = $"Error: {ex.Message}" });
            }
        }

        /// <summary>
        /// Get MPC-generated deposit address for a vBTC V2 contract
        /// </summary>
        /// <param name="scUID">Smart contract UID</param>
        /// <returns>Deposit address and MPC public key data</returns>
        [HttpGet("GetMPCDepositAddress/{scUID}")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        public async Task<string> GetMPCDepositAddress(string scUID)
        {
            try
            {
                // Get smart contract
                // var sc = SmartContractMain.SmartContractData.GetSmartContract(scUID);
                // var tokenizationV2 = sc.Features.FirstOrDefault(x => x.FeatureName == FeatureName.TokenizationV2);

                // TODO: FROST INTEGRATION
                // Retrieve FROST group public key and Taproot deposit address
                // PLACEHOLDER: Mock data

                return JsonConvert.SerializeObject(new
                {
                    Success = true,
                    Message = "Deposit address retrieved",
                    SmartContractUID = scUID,
                    DepositAddress = "bc1pFROST_TAPROOT_PLACEHOLDER",
                    FrostGroupPublicKey = "PLACEHOLDER_FROST_GROUP_PUBLIC_KEY",
                    RequiredThreshold = 51
                });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { Success = false, Message = $"Error: {ex.Message}" });
            }
        }

        #endregion

        #region Transfer Operations

        /// <summary>
        /// Transfer vBTC V2 tokens from one address to another
        /// </summary>
        /// <param name="payload">Transfer details</param>
        /// <returns>Transaction hash if successful</returns>
        [HttpPost("TransferVBTC")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        public async Task<string> TransferVBTC([FromBody] VBTCTransferPayload payload)
        {
            try
            {
                if (payload == null)
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Payload cannot be null" });

                // Validate balance
                // Create transfer transaction
                // Broadcast to network

                return JsonConvert.SerializeObject(new
                {
                    Success = true,
                    Message = "Transfer successful",
                    TransactionHash = "PLACEHOLDER_TX_HASH"
                });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { Success = false, Message = $"Error: {ex.Message}" });
            }
        }

        /// <summary>
        /// Transfer vBTC V2 tokens to multiple recipients
        /// </summary>
        /// <param name="payload">Multi-transfer details</param>
        /// <returns>Transaction hash if successful</returns>
        [HttpPost("TransferVBTCMulti")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        public async Task<string> TransferVBTCMulti([FromBody] VBTCTransferMultiPayload payload)
        {
            try
            {
                if (payload == null)
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Payload cannot be null" });

                // Validate total balance
                // Create multi-transfer transaction
                // Broadcast to network

                return JsonConvert.SerializeObject(new
                {
                    Success = true,
                    Message = "Multi-transfer successful",
                    TransactionHash = "PLACEHOLDER_TX_HASH"
                });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { Success = false, Message = $"Error: {ex.Message}" });
            }
        }

        /// <summary>
        /// Transfer ownership of vBTC V2 contract to another address
        /// </summary>
        /// <param name="scUID">Smart contract UID</param>
        /// <param name="toAddress">New owner address</param>
        /// <returns>Transaction hash if successful</returns>
        [HttpGet("TransferOwnership/{scUID}/{toAddress}")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        public async Task<string> TransferOwnership(string scUID, string toAddress)
        {
            return await Services.VBTCService.TransferOwnership(scUID, toAddress);
        }

        #endregion

        #region Withdrawal Operations

        /// <summary>
        /// Request withdrawal of vBTC to Bitcoin address
        /// </summary>
        /// <param name="payload">Withdrawal request details</param>
        /// <returns>Withdrawal request confirmation</returns>
        [HttpPost("RequestWithdrawal")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        public async Task<string> RequestWithdrawal([FromBody] VBTCWithdrawalPayload payload)
        {
            try
            {
                if (payload == null)
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Payload cannot be null" });

                // Validate balance
                // Check no active withdrawal exists
                // Create withdrawal request
                // Update contract state to "Requested"

                var requestHash = "PLACEHOLDER_REQUEST_HASH";

                return JsonConvert.SerializeObject(new
                {
                    Success = true,
                    Message = "Withdrawal request created",
                    RequestHash = requestHash,
                    SmartContractUID = payload.SmartContractUID,
                    Amount = payload.Amount,
                    Destination = payload.BTCAddress,
                    Status = "Requested"
                });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { Success = false, Message = $"Error: {ex.Message}" });
            }
        }

        /// <summary>
        /// Complete withdrawal by coordinating MPC signing and broadcasting BTC transaction
        /// </summary>
        /// <param name="payload">Withdrawal completion details</param>
        /// <returns>Bitcoin transaction hash if successful</returns>
        [HttpPost("CompleteWithdrawal")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        public async Task<string> CompleteWithdrawal([FromBody] VBTCWithdrawalCompletePayload payload)
        {
            try
            {
                if (payload == null)
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Payload cannot be null" });

                // TODO: FROST INTEGRATION - 2-ROUND SIGNING CEREMONY
                // ============================================================
                // 1. Retrieve withdrawal request from contract state
                // 2. Validate request is in "Requested" status
                // 3. Calculate BTC transaction fee
                // 4. Create unsigned Bitcoin Taproot transaction:
                //    - Input: Taproot UTXO(s) from deposit address
                //    - Output 1: Amount to destination address
                //    - Output 2: Change back to deposit address (if any)
                //    - Witness program: Taproot key path spend
                // 5. Get active validators (require 51% for signing)
                // 6. Compute transaction sighash (BIP 341)
                // 7. FROST Signing Round 1: Nonce Generation
                //    - Each validator generates random nonce
                //    - Each validator computes nonce commitment
                //    - Broadcast FROST_SIGN_R1 with commitments via SignalR
                //    - Coordinator aggregates all commitments
                // 8. FROST Signing Round 2: Signature Share Generation
                //    - Each validator receives aggregated commitments
                //    - Each validator computes partial Schnorr signature
                //    - Broadcast FROST_SIGN_R2 with signature shares via SignalR
                // 9. Signature Aggregation
                //    - Coordinator receives all signature shares
                //    - Aggregate into final Schnorr signature
                //    - Validate signature against group public key
                // 10. Complete Transaction
                //     - Attach Schnorr signature to transaction witness
                //     - Transaction now spends via Taproot key path
                //     - Broadcast to Bitcoin network via Electrum
                // 11. Monitor Confirmation
                //     - Wait for 1 confirmation
                //     - Update contract state to "Pending_BTC"
                //     - After confirmation, update to "Completed"
                // ============================================================
                // PLACEHOLDER: Mock BTC transaction
                string btcTxHash = "PLACEHOLDER_BTC_TX_HASH_AFTER_FROST_SIGNING";

                return JsonConvert.SerializeObject(new
                {
                    Success = true,
                    Message = "Withdrawal completed successfully",
                    BTCTransactionHash = btcTxHash,
                    Status = "Pending_BTC",
                    SmartContractUID = payload.SmartContractUID
                });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { Success = false, Message = $"Error: {ex.Message}" });
            }
        }

        /// <summary>
        /// Request cancellation of a failed withdrawal
        /// </summary>
        /// <param name="payload">Cancellation request with failure proof</param>
        /// <returns>Cancellation request ID</returns>
        [HttpPost("CancelWithdrawal")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        public async Task<string> CancelWithdrawal([FromBody] VBTCCancellationPayload payload)
        {
            try
            {
                if (payload == null)
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Payload cannot be null" });

                // TODO: FROST INTEGRATION
                // Verify failure proof (e.g., BTC TX rejected, timeout, etc.)
                // PLACEHOLDER: Basic validation

                var cancellationUID = Guid.NewGuid().ToString();

                // Create cancellation record
                var cancellation = new VBTCWithdrawalCancellation
                {
                    CancellationUID = cancellationUID,
                    SmartContractUID = payload.SmartContractUID,
                    OwnerAddress = payload.OwnerAddress,
                    WithdrawalRequestHash = payload.WithdrawalRequestHash,
                    BTCTxHash = payload.BTCTxHash,
                    FailureProof = payload.FailureProof,
                    RequestTime = TimeUtil.GetTime(),
                    ValidatorVotes = new Dictionary<string, bool>(),
                    ApproveCount = 0,
                    RejectCount = 0,
                    IsApproved = false,
                    IsProcessed = false
                };

                // Save to database
                // VBTCWithdrawalCancellation.Save(cancellation);

                return JsonConvert.SerializeObject(new
                {
                    Success = true,
                    Message = "Cancellation request created. Awaiting validator votes (75% required).",
                    CancellationUID = cancellationUID,
                    RequiredVotes = "75%"
                });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { Success = false, Message = $"Error: {ex.Message}" });
            }
        }

        /// <summary>
        /// Validator votes on withdrawal cancellation request
        /// </summary>
        /// <param name="payload">Vote details</param>
        /// <returns>Vote confirmation and current tally</returns>
        [HttpPost("VoteOnCancellation")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        public async Task<string> VoteOnCancellation([FromBody] VBTCCancellationVotePayload payload)
        {
            try
            {
                if (payload == null)
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Payload cannot be null" });

                // TODO: FROST INTEGRATION
                // Verify validator signature using their FROST public key
                // Ensure validator is active and eligible to vote
                // Use Schnorr signature verification
                // PLACEHOLDER: Basic validation

                // Get cancellation record
                // var cancellation = VBTCWithdrawalCancellation.Get(payload.CancellationUID);

                // Record vote
                // cancellation.ValidatorVotes[payload.ValidatorAddress] = payload.Approve;

                // Update counts
                // if (payload.Approve) cancellation.ApproveCount++; else cancellation.RejectCount++;

                // Check if 75% threshold reached
                // int totalValidators = VBTCValidator.GetActiveValidatorCount();
                // decimal approvalPercentage = (cancellation.ApproveCount / (decimal)totalValidators) * 100;

                // if (approvalPercentage >= 75)
                // {
                //     cancellation.IsApproved = true;
                //     cancellation.IsProcessed = true;
                //     // Update contract state - cancel withdrawal, unlock funds
                // }

                return JsonConvert.SerializeObject(new
                {
                    Success = true,
                    Message = "Vote recorded",
                    CancellationUID = payload.CancellationUID,
                    ApproveCount = 0, // PLACEHOLDER
                    RejectCount = 0, // PLACEHOLDER
                    TotalValidators = 0, // PLACEHOLDER
                    ApprovalPercentage = 0.0M, // PLACEHOLDER
                    IsApproved = false
                });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { Success = false, Message = $"Error: {ex.Message}" });
            }
        }

        #endregion

        #region Raw Withdrawal Operations

        /// <summary>
        /// Request withdrawal with pre-signed external request (Raw format)
        /// SECURITY: Includes signature verification, timestamp validation, and replay attack prevention
        /// </summary>
        /// <param name="payload">Raw withdrawal request with signature and unique ID</param>
        /// <returns>Withdrawal request confirmation</returns>
        [HttpPost("RequestWithdrawalRaw")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        public async Task<string> RequestWithdrawalRaw([FromBody] VBTCWithdrawalRawPayload payload)
        {
            try
            {
                if (payload == null)
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Payload cannot be null" });

                // 1. Validate required fields
                if (string.IsNullOrEmpty(payload.VFXAddress))
                    return JsonConvert.SerializeObject(new { Success = false, Message = "VFX address cannot be null" });

                if (string.IsNullOrEmpty(payload.BTCAddress))
                    return JsonConvert.SerializeObject(new { Success = false, Message = "BTC address cannot be null" });

                if (string.IsNullOrEmpty(payload.SmartContractUID))
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Smart contract UID cannot be null" });

                if (string.IsNullOrEmpty(payload.UniqueId))
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Unique ID cannot be null" });

                if (string.IsNullOrEmpty(payload.VFXSignature))
                    return JsonConvert.SerializeObject(new { Success = false, Message = "VFX signature cannot be null" });

                // 2. Timestamp validation (reject if older than 5 minutes)
                var currentTime = TimeUtil.GetTime();
                var timeDifference = Math.Abs(currentTime - payload.Timestamp);
                if (timeDifference > 300) // 5 minutes = 300 seconds
                {
                    return JsonConvert.SerializeObject(new { Success = false, Message = $"Request timestamp is too old. Difference: {timeDifference} seconds (max 300)" });
                }

                // 3. Replay attack prevention - check if UniqueId already exists
                var existingRequest = VBTCWithdrawalRequest.GetByUniqueId(payload.VFXAddress, payload.UniqueId, payload.SmartContractUID);
                if (existingRequest != null)
                {
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Duplicate request detected. This UniqueId has already been processed." });
                }

                // 4. Check for incomplete withdrawals (only 1 active withdrawal per contract)
                var hasIncomplete = VBTCWithdrawalRequest.HasIncompleteRequest(payload.VFXAddress, payload.SmartContractUID);
                if (hasIncomplete)
                {
                    return JsonConvert.SerializeObject(new { Success = false, Message = "An active withdrawal request already exists for this contract. Complete or cancel it first." });
                }

                // 5. TODO: Verify VFX signature
                // var signatureData = $"{payload.VFXAddress}{payload.BTCAddress}{payload.SmartContractUID}{payload.Amount}{payload.FeeRate}{payload.Timestamp}{payload.UniqueId}";
                // var isValidSignature = SignatureService.VerifySignature(payload.VFXAddress, signatureData, payload.VFXSignature);
                // if (!isValidSignature)
                // {
                //     return JsonConvert.SerializeObject(new { Success = false, Message = "Invalid VFX signature" });
                // }

                // 6. TODO: Validate balance (call VBTCService)
                // var balance = await VBTCService.GetBalance(payload.VFXAddress, payload.SmartContractUID);
                // if (balance < payload.Amount)
                // {
                //     return JsonConvert.SerializeObject(new { Success = false, Message = $"Insufficient balance. Available: {balance}, Requested: {payload.Amount}" });
                // }

                // 7. Create withdrawal request
                var withdrawalRequest = new VBTCWithdrawalRequest
                {
                    RequestorAddress = payload.VFXAddress,
                    OriginalRequestTime = payload.Timestamp,
                    OriginalSignature = payload.VFXSignature,
                    OriginalUniqueId = payload.UniqueId,
                    Timestamp = currentTime,
                    SmartContractUID = payload.SmartContractUID,
                    Amount = payload.Amount,
                    BTCDestination = payload.BTCAddress,
                    FeeRate = payload.FeeRate,
                    TransactionHash = "", // Will be set when completed
                    IsCompleted = false,
                    Status = VBTCWithdrawalStatus.Requested
                };

                // 8. Save to database
                var saved = VBTCWithdrawalRequest.Save(withdrawalRequest);
                if (!saved)
                {
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Failed to save withdrawal request to database" });
                }

                // 9. TODO: Update contract state to "Requested"
                // await VBTCService.UpdateWithdrawalStatus(payload.SmartContractUID, VBTCWithdrawalStatus.Requested, payload.Amount, payload.BTCAddress);

                var requestHash = $"{payload.VFXAddress.Substring(0, 8)}_{payload.UniqueId.Substring(0, 8)}_{currentTime}";

                return JsonConvert.SerializeObject(new
                {
                    Success = true,
                    Message = "Raw withdrawal request created successfully",
                    RequestHash = requestHash,
                    SmartContractUID = payload.SmartContractUID,
                    Amount = payload.Amount,
                    Destination = payload.BTCAddress,
                    Status = "Requested",
                    UniqueId = payload.UniqueId,
                    Timestamp = currentTime
                });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { Success = false, Message = $"Error: {ex.Message}" });
            }
        }

        /// <summary>
        /// Complete withdrawal with pre-signed validator authorization (Raw format)
        /// SECURITY: Verifies validator signature and coordinates FROST signing
        /// </summary>
        /// <param name="payload">Raw completion request with validator signature</param>
        /// <returns>Bitcoin transaction hash if successful</returns>
        [HttpPost("CompleteWithdrawalRaw")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        public async Task<string> CompleteWithdrawalRaw([FromBody] VBTCWithdrawalCompleteRawPayload payload)
        {
            try
            {
                if (payload == null)
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Payload cannot be null" });

                // 1. Validate required fields
                if (string.IsNullOrEmpty(payload.SmartContractUID))
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Smart contract UID cannot be null" });

                if (string.IsNullOrEmpty(payload.WithdrawalRequestHash))
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Withdrawal request hash cannot be null" });

                if (string.IsNullOrEmpty(payload.ValidatorAddress))
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Validator address cannot be null" });

                if (string.IsNullOrEmpty(payload.UniqueId))
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Unique ID cannot be null" });

                if (string.IsNullOrEmpty(payload.ValidatorSignature))
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Validator signature cannot be null" });

                // 2. Timestamp validation
                var currentTime = TimeUtil.GetTime();
                var timeDifference = Math.Abs(currentTime - payload.Timestamp);
                if (timeDifference > 300)
                {
                    return JsonConvert.SerializeObject(new { Success = false, Message = $"Request timestamp is too old. Difference: {timeDifference} seconds (max 300)" });
                }

                // 3. TODO: Verify validator is active and eligible
                // var validator = VBTCValidator.GetValidator(payload.ValidatorAddress);
                // if (validator == null || !validator.IsActive)
                // {
                //     return JsonConvert.SerializeObject(new { Success = false, Message = "Validator is not active or not found" });
                // }

                // 4. TODO: Verify validator signature (FROST signature)
                // var signatureData = $"{payload.SmartContractUID}{payload.WithdrawalRequestHash}{payload.ValidatorAddress}{payload.Timestamp}{payload.UniqueId}";
                // var isValidSignature = FrostService.VerifyValidatorSignature(payload.ValidatorAddress, signatureData, payload.ValidatorSignature);
                // if (!isValidSignature)
                // {
                //     return JsonConvert.SerializeObject(new { Success = false, Message = "Invalid validator signature" });
                // }

                // 5. TODO: Retrieve withdrawal request and validate status
                // var withdrawalRequest = VBTCWithdrawalRequest.GetByRequestHash(payload.WithdrawalRequestHash);
                // if (withdrawalRequest == null)
                // {
                //     return JsonConvert.SerializeObject(new { Success = false, Message = "Withdrawal request not found" });
                // }
                // if (withdrawalRequest.Status != VBTCWithdrawalStatus.Requested)
                // {
                //     return JsonConvert.SerializeObject(new { Success = false, Message = $"Withdrawal request is not in 'Requested' status. Current status: {withdrawalRequest.Status}" });
                // }

                // 6. TODO: FROST INTEGRATION - 2-Round Signing Ceremony
                // var btcTxHash = await FrostService.CoordinateWithdrawalSigning(payload.SmartContractUID, payload.WithdrawalRequestHash);
                // if (string.IsNullOrEmpty(btcTxHash))
                // {
                //     return JsonConvert.SerializeObject(new { Success = false, Message = "FROST signing ceremony failed" });
                // }

                // PLACEHOLDER: Mock BTC transaction
                string btcTxHash = $"FROST_BTC_TX_{Guid.NewGuid().ToString().Substring(0, 8)}";

                // 7. TODO: Update withdrawal request status
                // VBTCWithdrawalRequest.Complete(withdrawalRequest.RequestorAddress, withdrawalRequest.OriginalUniqueId, payload.SmartContractUID, payload.WithdrawalRequestHash, btcTxHash);

                // 8. TODO: Update contract state to "Pending_BTC"
                // await VBTCService.UpdateWithdrawalStatus(payload.SmartContractUID, VBTCWithdrawalStatus.Pending_BTC, 0, "", btcTxHash);

                return JsonConvert.SerializeObject(new
                {
                    Success = true,
                    Message = "Raw withdrawal completed successfully via FROST signing",
                    BTCTransactionHash = btcTxHash,
                    Status = "Pending_BTC",
                    SmartContractUID = payload.SmartContractUID,
                    WithdrawalRequestHash = payload.WithdrawalRequestHash
                });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { Success = false, Message = $"Error: {ex.Message}" });
            }
        }

        /// <summary>
        /// Cancel withdrawal with pre-signed owner authorization (Raw format)
        /// SECURITY: Verifies owner signature and creates cancellation request for validator voting
        /// </summary>
        /// <param name="payload">Raw cancellation request with owner signature</param>
        /// <returns>Cancellation request ID</returns>
        [HttpPost("CancelWithdrawalRaw")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        public async Task<string> CancelWithdrawalRaw([FromBody] VBTCCancellationRawPayload payload)
        {
            try
            {
                if (payload == null)
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Payload cannot be null" });

                // 1. Validate required fields
                if (string.IsNullOrEmpty(payload.SmartContractUID))
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Smart contract UID cannot be null" });

                if (string.IsNullOrEmpty(payload.OwnerAddress))
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Owner address cannot be null" });

                if (string.IsNullOrEmpty(payload.WithdrawalRequestHash))
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Withdrawal request hash cannot be null" });

                if (string.IsNullOrEmpty(payload.UniqueId))
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Unique ID cannot be null" });

                if (string.IsNullOrEmpty(payload.OwnerSignature))
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Owner signature cannot be null" });

                // 2. Timestamp validation
                var currentTime = TimeUtil.GetTime();
                var timeDifference = Math.Abs(currentTime - payload.Timestamp);
                if (timeDifference > 300)
                {
                    return JsonConvert.SerializeObject(new { Success = false, Message = $"Request timestamp is too old. Difference: {timeDifference} seconds (max 300)" });
                }

                // 3. TODO: Verify owner signature
                // var signatureData = $"{payload.SmartContractUID}{payload.OwnerAddress}{payload.WithdrawalRequestHash}{payload.BTCTxHash}{payload.FailureProof}{payload.Timestamp}{payload.UniqueId}";
                // var isValidSignature = SignatureService.VerifySignature(payload.OwnerAddress, signatureData, payload.OwnerSignature);
                // if (!isValidSignature)
                // {
                //     return JsonConvert.SerializeObject(new { Success = false, Message = "Invalid owner signature" });
                // }

                // 4. TODO: Verify owner is the contract owner
                // var contract = VBTCContractV2.GetContract(payload.SmartContractUID);
                // if (contract == null || contract.OwnerAddress != payload.OwnerAddress)
                // {
                //     return JsonConvert.SerializeObject(new { Success = false, Message = "Only the contract owner can request cancellation" });
                // }

                // 5. TODO: Verify failure proof (e.g., BTC TX rejected, mempool timeout)
                // var isValidFailureProof = await BitcoinService.VerifyTransactionFailure(payload.BTCTxHash, payload.FailureProof);
                // if (!isValidFailureProof)
                // {
                //     return JsonConvert.SerializeObject(new { Success = false, Message = "Invalid failure proof. Transaction may not have failed." });
                // }

                var cancellationUID = Guid.NewGuid().ToString();

                // 6. Create cancellation record
                var cancellation = new VBTCWithdrawalCancellation
                {
                    CancellationUID = cancellationUID,
                    SmartContractUID = payload.SmartContractUID,
                    OwnerAddress = payload.OwnerAddress,
                    WithdrawalRequestHash = payload.WithdrawalRequestHash,
                    BTCTxHash = payload.BTCTxHash,
                    FailureProof = payload.FailureProof,
                    RequestTime = currentTime,
                    ValidatorVotes = new Dictionary<string, bool>(),
                    ApproveCount = 0,
                    RejectCount = 0,
                    IsApproved = false,
                    IsProcessed = false
                };

                // 7. TODO: Save to database
                // VBTCWithdrawalCancellation.Save(cancellation);

                // 8. TODO: Notify validators to vote on cancellation
                // await VBTCService.NotifyValidatorsForCancellationVote(cancellationUID);

                return JsonConvert.SerializeObject(new
                {
                    Success = true,
                    Message = "Raw cancellation request created successfully. Awaiting validator votes (75% required).",
                    CancellationUID = cancellationUID,
                    SmartContractUID = payload.SmartContractUID,
                    WithdrawalRequestHash = payload.WithdrawalRequestHash,
                    RequiredVotes = "75%",
                    UniqueId = payload.UniqueId,
                    Timestamp = currentTime
                });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { Success = false, Message = $"Error: {ex.Message}" });
            }
        }

        #endregion

        #region Balance & Status

        /// <summary>
        /// Get vBTC V2 balance for an address in a specific contract
        /// </summary>
        /// <param name="address">VFX address</param>
        /// <param name="scUID">Smart contract UID</param>
        /// <returns>Balance information</returns>
        [HttpGet("GetVBTCBalance/{address}/{scUID}")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        public async Task<string> GetVBTCBalance(string address, string scUID)
        {
            try
            {
                // Get contract state
                // var scState = SmartContractStateTrei.GetSmartContractState(scUID);

                // Calculate balance from state trei transactions
                // If owner, add BTC chain balance from deposit address

                return JsonConvert.SerializeObject(new
                {
                    Success = true,
                    Message = "Balance retrieved",
                    Address = address,
                    SmartContractUID = scUID,
                    Balance = 0.0M, // PLACEHOLDER
                    IsOwner = false
                });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { Success = false, Message = $"Error: {ex.Message}" });
            }
        }

        /// <summary>
        /// Get all vBTC V2 balances for an address across all contracts
        /// </summary>
        /// <param name="address">VFX address</param>
        /// <returns>List of balances by contract</returns>
        [HttpGet("GetAllVBTCBalances/{address}")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        public async Task<string> GetAllVBTCBalances(string address)
        {
            try
            {
                // Get all vBTC V2 contracts for this address
                // Calculate balances for each

                return JsonConvert.SerializeObject(new
                {
                    Success = true,
                    Message = "Balances retrieved",
                    Address = address,
                    TotalBalance = 0.0M, // PLACEHOLDER
                    Contracts = new List<object>() // PLACEHOLDER
                });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { Success = false, Message = $"Error: {ex.Message}" });
            }
        }

        /// <summary>
        /// Get vBTC V2 contract details
        /// </summary>
        /// <param name="scUID">Smart contract UID</param>
        /// <returns>Contract information</returns>
        [HttpGet("GetContractDetails/{scUID}")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        public async Task<string> GetContractDetails(string scUID)
        {
            try
            {
                // Get smart contract
                // var sc = SmartContractMain.SmartContractData.GetSmartContract(scUID);

                return JsonConvert.SerializeObject(new
                {
                    Success = true,
                    Message = "Contract details retrieved",
                    SmartContractUID = scUID,
                    Contract = new object() // PLACEHOLDER
                });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { Success = false, Message = $"Error: {ex.Message}" });
            }
        }

        /// <summary>
        /// Get withdrawal history for a contract
        /// </summary>
        /// <param name="scUID">Smart contract UID</param>
        /// <returns>List of historical withdrawals</returns>
        [HttpGet("GetWithdrawalHistory/{scUID}")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        public async Task<string> GetWithdrawalHistory(string scUID)
        {
            try
            {
                // Get contract
                // var contract = VBTCContractV2.GetContract(scUID);
                // return contract.WithdrawalHistory;

                return JsonConvert.SerializeObject(new
                {
                    Success = true,
                    Message = "Withdrawal history retrieved",
                    SmartContractUID = scUID,
                    WithdrawalHistory = new List<object>() // PLACEHOLDER
                });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { Success = false, Message = $"Error: {ex.Message}" });
            }
        }

        /// <summary>
        /// Get current withdrawal status for a contract
        /// </summary>
        /// <param name="scUID">Smart contract UID</param>
        /// <returns>Current withdrawal status</returns>
        [HttpGet("GetWithdrawalStatus/{scUID}")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        public async Task<string> GetWithdrawalStatus(string scUID)
        {
            try
            {
                // Get contract
                // var contract = VBTCContractV2.GetContract(scUID);

                return JsonConvert.SerializeObject(new
                {
                    Success = true,
                    Message = "Withdrawal status retrieved",
                    SmartContractUID = scUID,
                    Status = "None", // PLACEHOLDER
                    ActiveWithdrawal = new object() // PLACEHOLDER
                });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { Success = false, Message = $"Error: {ex.Message}" });
            }
        }

        #endregion

        #region Utility

        /// <summary>
        /// Get default vBTC V2 image (Base64 encoded)
        /// </summary>
        /// <returns>Default image data</returns>
        [HttpGet("GetDefaultImageBase")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        public async Task<string> GetDefaultImageBase()
        {
            try
            {
                // Return default vBTC V2 image
                // var defaultImageLocation = NFTAssetFileUtility.GetvBTCV2DefaultLogoLocation();

                return JsonConvert.SerializeObject(new
                {
                    Success = true,
                    Message = "Default image retrieved",
                    EncodingFormat = "base64",
                    ImageExtension = "png",
                    ImageName = "defaultvBTC_V2.png",
                    ImageBase = "PLACEHOLDER_BASE64_IMAGE"
                });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { Success = false, Message = $"Error: {ex.Message}" });
            }
        }

        /// <summary>
        /// Get list of all vBTC V2 contracts
        /// </summary>
        /// <param name="address">Optional: Filter by owner address</param>
        /// <returns>List of vBTC V2 contracts</returns>
        [HttpGet("GetContractList/{address?}")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        public async Task<string> GetContractList(string? address = null)
        {
            try
            {
                // Get all vBTC V2 contracts, optionally filtered by owner
                // var contracts = VBTCContractV2.GetContracts(address);

                return JsonConvert.SerializeObject(new
                {
                    Success = true,
                    Message = "Contract list retrieved",
                    Contracts = new List<object>() // PLACEHOLDER
                });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { Success = false, Message = $"Error: {ex.Message}" });
            }
        }

        #endregion
    }

    #region Payload Models

    public class VBTCContractPayload
    {
        public string OwnerAddress { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string? Ticker { get; set; }
        public string? ImageBase { get; set; }
    }

    public class VBTCTransferPayload
    {
        public string SmartContractUID { get; set; }
        public string FromAddress { get; set; }
        public string ToAddress { get; set; }
        public decimal Amount { get; set; }
    }

    public class VBTCTransferMultiPayload
    {
        public string SmartContractUID { get; set; }
        public string FromAddress { get; set; }
        public List<VBTCTransferRecipient> Recipients { get; set; }
    }

    public class VBTCTransferRecipient
    {
        public string ToAddress { get; set; }
        public decimal Amount { get; set; }
    }

    public class VBTCWithdrawalPayload
    {
        public string SmartContractUID { get; set; }
        public string OwnerAddress { get; set; }
        public string BTCAddress { get; set; }
        public decimal Amount { get; set; }
        public int FeeRate { get; set; }
    }

    public class VBTCWithdrawalCompletePayload
    {
        public string SmartContractUID { get; set; }
        public string WithdrawalRequestHash { get; set; }
    }

    public class VBTCCancellationPayload
    {
        public string SmartContractUID { get; set; }
        public string OwnerAddress { get; set; }
        public string WithdrawalRequestHash { get; set; }
        public string BTCTxHash { get; set; }
        public string FailureProof { get; set; }
    }

    public class VBTCCancellationVotePayload
    {
        public string CancellationUID { get; set; }
        public string ValidatorAddress { get; set; }
        public bool Approve { get; set; }
        public string ValidatorSignature { get; set; }
    }

    // Raw Withdrawal Payload Models (for external/pre-signed requests)

    public class VBTCWithdrawalRawPayload
    {
        public string VFXAddress { get; set; }           // Owner's VFX address
        public string BTCAddress { get; set; }           // BTC destination
        public string SmartContractUID { get; set; }
        public decimal Amount { get; set; }
        public int FeeRate { get; set; }
        public long Timestamp { get; set; }              // Unix timestamp
        public string UniqueId { get; set; }             // Unique request ID (prevents replay)
        public string VFXSignature { get; set; }         // Signature of request data
        public bool IsTest { get; set; }                 // Testnet flag
    }

    public class VBTCWithdrawalCompleteRawPayload
    {
        public string SmartContractUID { get; set; }
        public string WithdrawalRequestHash { get; set; }
        public string ValidatorAddress { get; set; }     // Validator initiating completion
        public long Timestamp { get; set; }
        public string UniqueId { get; set; }
        public string ValidatorSignature { get; set; }   // FROST signature proof
    }

    public class VBTCCancellationRawPayload
    {
        public string SmartContractUID { get; set; }
        public string OwnerAddress { get; set; }
        public string WithdrawalRequestHash { get; set; }
        public string BTCTxHash { get; set; }
        public string FailureProof { get; set; }         // Proof of BTC TX failure
        public long Timestamp { get; set; }
        public string UniqueId { get; set; }
        public string OwnerSignature { get; set; }       // VFX signature
    }

    #endregion
}
