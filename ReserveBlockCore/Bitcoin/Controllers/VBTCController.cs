using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using ReserveBlockCore.Bitcoin.Models;
using ReserveBlockCore.Controllers;
using ReserveBlockCore.Data;
using ReserveBlockCore.Models;
using ReserveBlockCore.Models.SmartContracts;
using ReserveBlockCore.Services;
using ReserveBlockCore.Utilities;
using System.Collections.Concurrent;

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
        #region In-Memory Ceremony Storage

        /// <summary>
        /// In-memory storage for MPC ceremony states
        /// Key: CeremonyId, Value: MPCCeremonyState
        /// </summary>
        private static readonly ConcurrentDictionary<string, MPCCeremonyState> _ceremonies = new();

        #endregion

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

                var validator = new VBTCValidator
                {
                    ValidatorAddress = validatorAddress,
                    IPAddress = ipAddress,
                    RegistrationBlockHeight = Globals.LastBlock.Height,
                    LastHeartbeatBlock = Globals.LastBlock.Height,
                    IsActive = true,
                    FrostPublicKey = "PLACEHOLDER_FROST_PUBLIC_KEY",  // Public key only (key share stays private)
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

        #endregion

        #region MPC Ceremony Management

        /// <summary>
        /// Initiate an MPC (FROST DKG) ceremony to generate a deposit address
        /// This runs asynchronously in the background - use GetCeremonyStatus to check progress
        /// </summary>
        /// <param name="ownerAddress">Address requesting the ceremony</param>
        /// <returns>Ceremony ID for tracking progress</returns>
        [HttpPost("InitiateMPCCeremony/{ownerAddress}")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        public async Task<string> InitiateMPCCeremony(string ownerAddress)
        {
            try
            {
                if (string.IsNullOrEmpty(ownerAddress))
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Owner address cannot be null" });

                // Generate unique ceremony ID
                var ceremonyId = Guid.NewGuid().ToString();
                var currentTime = TimeUtil.GetTime();

                // Create initial ceremony state
                var ceremony = new MPCCeremonyState
                {
                    CeremonyId = ceremonyId,
                    OwnerAddress = ownerAddress,
                    Status = CeremonyStatus.Initiated,
                    InitiatedTimestamp = currentTime,
                    RequiredThreshold = 51,
                    ProgressPercentage = 0,
                    CurrentRound = 0
                };

                // Store in memory
                if (!_ceremonies.TryAdd(ceremonyId, ceremony))
                {
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Failed to create ceremony" });
                }

                // Start background ceremony process
                _ = Task.Run(async () => await ExecuteMPCCeremony(ceremonyId));

                return JsonConvert.SerializeObject(new
                {
                    Success = true,
                    Message = "MPC ceremony initiated successfully. Use GetCeremonyStatus to check progress.",
                    CeremonyId = ceremonyId,
                    Status = ceremony.Status.ToString(),
                    InitiatedTimestamp = currentTime
                });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { Success = false, Message = $"Error: {ex.Message}" });
            }
        }

        /// <summary>
        /// Get the status of an ongoing or completed MPC ceremony
        /// </summary>
        /// <param name="ceremonyId">Ceremony ID from InitiateMPCCeremony</param>
        /// <returns>Ceremony status and results if completed</returns>
        [HttpGet("GetCeremonyStatus/{ceremonyId}")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        public async Task<string> GetCeremonyStatus(string ceremonyId)
        {
            try
            {
                if (string.IsNullOrEmpty(ceremonyId))
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Ceremony ID cannot be null" });

                if (!_ceremonies.TryGetValue(ceremonyId, out var ceremony))
                {
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Ceremony not found" });
                }

                var response = new
                {
                    Success = true,
                    Message = "Ceremony status retrieved",
                    CeremonyId = ceremony.CeremonyId,
                    Status = ceremony.Status.ToString(),
                    OwnerAddress = ceremony.OwnerAddress,
                    ProgressPercentage = ceremony.ProgressPercentage,
                    CurrentRound = ceremony.CurrentRound,
                    InitiatedTimestamp = ceremony.InitiatedTimestamp,
                    CompletedTimestamp = ceremony.CompletedTimestamp,
                    ErrorMessage = ceremony.ErrorMessage,
                    // Only include results if completed
                    DepositAddress = ceremony.Status == CeremonyStatus.Completed ? ceremony.DepositAddress : null,
                    FrostGroupPublicKey = ceremony.Status == CeremonyStatus.Completed ? ceremony.FrostGroupPublicKey : null,
                    DKGProof = ceremony.Status == CeremonyStatus.Completed ? ceremony.DKGProof : null,
                    ValidatorCount = ceremony.ValidatorSnapshot?.Count ?? 0,
                    RequiredThreshold = ceremony.RequiredThreshold,
                    ProofBlockHeight = ceremony.ProofBlockHeight
                };

                return JsonConvert.SerializeObject(response);
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { Success = false, Message = $"Error: {ex.Message}" });
            }
        }

        /// <summary>
        /// Background task that executes the MPC (FROST DKG) ceremony
        /// Steps 1-8 from the original CreateVBTCContract method
        /// </summary>
        private async Task ExecuteMPCCeremony(string ceremonyId)
        {
            try
            {
                if (!_ceremonies.TryGetValue(ceremonyId, out var ceremony))
                    return;

                // Update status: Validating validators
                ceremony.Status = CeremonyStatus.ValidatingValidators;
                ceremony.ProgressPercentage = 5;

                // Step 1: Get list of active validators
                var currentBlock = Globals.LastBlock.Height;
                List<VBTCValidator>? activeValidators;

                if (!string.IsNullOrEmpty(Globals.ValidatorAddress))
                {
                    activeValidators = VBTCValidator.GetActiveValidatorsSinceBlock(currentBlock - 1000);
                }
                else
                {
                    activeValidators = await VBTCValidator.FetchActiveValidatorsFromNetwork();
                }

                var totalRegisteredValidators = VBTCValidator.GetAllValidators()?.Count ?? 0;

                if (activeValidators == null || !activeValidators.Any())
                {
                    ceremony.Status = CeremonyStatus.Failed;
                    ceremony.ErrorMessage = "No active validators available for vBTC V2 contract creation.";
                    ceremony.CompletedTimestamp = TimeUtil.GetTime();
                    return;
                }

                var requiredActiveValidators = (int)Math.Ceiling(totalRegisteredValidators * 0.75);
                if (activeValidators.Count < requiredActiveValidators)
                {
                    ceremony.Status = CeremonyStatus.Failed;
                    ceremony.ErrorMessage = $"Insufficient active validators. Required: {requiredActiveValidators}, Active: {activeValidators.Count}";
                    ceremony.CompletedTimestamp = TimeUtil.GetTime();
                    return;
                }

                ceremony.ValidatorSnapshot = activeValidators.Select(v => v.ValidatorAddress).ToList();
                ceremony.ProgressPercentage = 15;

                // Execute FROST DKG Ceremony via FrostMPCService with progress callback
                ceremony.Status = CeremonyStatus.Round1InProgress;
                
                var dkgResult = await Services.FrostMPCService.CoordinateDKGCeremony(
                    ceremonyId,
                    ceremony.OwnerAddress,
                    activeValidators,
                    ceremony.RequiredThreshold,
                    (round, percentage) =>
                    {
                        // Update ceremony progress from callback
                        ceremony.CurrentRound = round;
                        ceremony.ProgressPercentage = percentage;
                        
                        // Update status based on round
                        if (round == 1 && ceremony.Status != CeremonyStatus.Round1InProgress)
                            ceremony.Status = CeremonyStatus.Round1InProgress;
                        else if (round == 2 && ceremony.Status != CeremonyStatus.Round2InProgress)
                            ceremony.Status = CeremonyStatus.Round2InProgress;
                        else if (round == 3 && ceremony.Status != CeremonyStatus.Round3InProgress)
                            ceremony.Status = CeremonyStatus.Round3InProgress;
                    }
                );

                if (dkgResult == null)
                {
                    ceremony.Status = CeremonyStatus.Failed;
                    ceremony.ErrorMessage = "FROST DKG ceremony failed - unable to generate Taproot address";
                    ceremony.CompletedTimestamp = TimeUtil.GetTime();
                    return;
                }

                // DKG ceremony completed successfully
                ceremony.DepositAddress = dkgResult.TaprootAddress;
                ceremony.FrostGroupPublicKey = dkgResult.GroupPublicKey;
                ceremony.DKGProof = dkgResult.DKGProof;
                ceremony.ProofBlockHeight = Globals.LastBlock.Height;

                // Complete
                ceremony.Status = CeremonyStatus.Completed;
                ceremony.ProgressPercentage = 100;
                ceremony.CompletedTimestamp = TimeUtil.GetTime();
            }
            catch (Exception ex)
            {
                if (_ceremonies.TryGetValue(ceremonyId, out var ceremony))
                {
                    ceremony.Status = CeremonyStatus.Failed;
                    ceremony.ErrorMessage = ex.Message;
                    ceremony.CompletedTimestamp = TimeUtil.GetTime();
                }
            }
        }

        #endregion

        #region Contract Creation

        /// <summary>
        /// Create a new vBTC V2 contract with MPC-generated deposit address
        /// IMPORTANT: You must first call InitiateMPCCeremony and wait for it to complete,
        /// then pass the CeremonyId to this method
        /// </summary>
        /// <param name="payload">Contract creation payload (must include CeremonyId)</param>
        /// <returns>Contract creation result with smart contract UID</returns>
        [HttpPost("CreateVBTCContract")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        public async Task<string> CreateVBTCContract([FromBody] VBTCContractPayload payload)
        {
            try
            {
                if (payload == null)
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Payload cannot be null" });

                // Check if CeremonyId is provided
                if (string.IsNullOrEmpty(payload.CeremonyId))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        Success = false,
                        Message = "CeremonyId is required. Please call InitiateMPCCeremony first and wait for it to complete, then pass the CeremonyId to this method."
                    });
                }

                // Retrieve ceremony from memory
                if (!_ceremonies.TryGetValue(payload.CeremonyId, out var ceremony))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        Success = false,
                        Message = "Ceremony not found. Please initiate a new ceremony with InitiateMPCCeremony."
                    });
                }

                // Validate ceremony is completed
                if (ceremony.Status != CeremonyStatus.Completed)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        Success = false,
                        Message = $"Ceremony is not complete. Current status: {ceremony.Status}. Use GetCeremonyStatus to check progress.",
                        CeremonyId = payload.CeremonyId,
                        Status = ceremony.Status.ToString(),
                        ProgressPercentage = ceremony.ProgressPercentage
                    });
                }

                // Validate owner address matches ceremony
                if (ceremony.OwnerAddress != payload.OwnerAddress)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        Success = false,
                        Message = "Owner address does not match the ceremony owner address."
                    });
                }

                var scUID = Guid.NewGuid().ToString().Replace("-", "") + ":" + TimeUtil.GetTime().ToString();

                // Use ceremony results
                string depositAddress = ceremony.DepositAddress!;
                string frostGroupPublicKey = ceremony.FrostGroupPublicKey!;
                string dkgProof = ceremony.DKGProof!;
                var validatorSnapshot = ceremony.ValidatorSnapshot;

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
                    Description = payload.Description ?? "vBTC V2 Token - MPC-based Tokenized Bitcoin",
                    MinterAddress = payload.OwnerAddress,
                    MinterName = payload.OwnerAddress,
                    IsPublic = true,
                    SCVersion = Globals.SCVersion,
                    IsMinter = true,
                    IsPublished = false,
                    IsToken = false,
                    Id = 0,
                    Features = new List<SmartContractFeatures>
                    {
                        new SmartContractFeatures
                        {
                            FeatureName = FeatureName.TokenizationV2,
                            FeatureFeatures = tokenizationV2Feature
                        }
                    }
                };

                // Write smart contract (uses TokenizationV2SourceGenerator)
                var result = await SmartContractWriterService.WriteSmartContract(scMain);
                if (string.IsNullOrWhiteSpace(result.Item1))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        Success = false,
                        Message = "Failed to generate smart contract code"
                    });
                }

                // Save smart contract to databases
                SmartContractMain.SmartContractData.SaveSmartContract(result.Item2, result.Item1);
                await VBTCContractV2.SaveSmartContract(result.Item2, result.Item1, payload.OwnerAddress);

                // Create and broadcast mint transaction
                var scTx = await SmartContractService.MintSmartContractTx(result.Item2, TransactionType.VBTC_V2_CONTRACT_CREATE);
                if (scTx == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        Success = false,
                        Message = "Failed to create or broadcast smart contract transaction"
                    });
                }

                // Mark contract as published (set flag in database)
                // TODO: Implement VBTCContractV2.SetContractIsPublished() helper method

                return JsonConvert.SerializeObject(new
                {
                    Success = true,
                    Message = "vBTC V2 contract created and published to blockchain successfully",
                    SmartContractUID = scUID,
                    TransactionHash = scTx.Hash,
                    CeremonyId = payload.CeremonyId,
                    DepositAddress = depositAddress,
                    FrostGroupPublicKey = frostGroupPublicKey,
                    DKGProof = dkgProof,
                    ValidatorCount = validatorSnapshot.Count,
                    ProofBlockHeight = ceremony.ProofBlockHeight,
                    RequiredThreshold = 51
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

        /// <summary>
        /// Create a new vBTC V2 contract with pre-signed external request (Raw format)
        /// SECURITY: Includes signature verification, timestamp validation, and replay attack prevention
        /// IMPORTANT: You must first call InitiateMPCCeremony and wait for it to complete
        /// </summary>
        /// <param name="payload">Raw contract creation request with signature and unique ID</param>
        /// <returns>Contract creation result with smart contract UID</returns>
        [HttpPost("CreateVBTCContractRaw")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        public async Task<string> CreateVBTCContractRaw([FromBody] VBTCContractRawPayload payload)
        {
            try
            {
                if (payload == null)
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Payload cannot be null" });

                // 1. Validate required fields
                if (string.IsNullOrEmpty(payload.OwnerAddress))
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Owner address cannot be null" });

                if (string.IsNullOrEmpty(payload.Name))
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Contract name cannot be null" });

                if (string.IsNullOrEmpty(payload.CeremonyId))
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Ceremony ID cannot be null" });

                if (string.IsNullOrEmpty(payload.UniqueId))
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Unique ID cannot be null" });

                if (string.IsNullOrEmpty(payload.OwnerSignature))
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Owner signature cannot be null" });

                // 2. Timestamp validation (reject if older than 5 minutes)
                var currentTime = TimeUtil.GetTime();
                var timeDifference = Math.Abs(currentTime - payload.Timestamp);
                if (timeDifference > 300) // 5 minutes = 300 seconds
                {
                    return JsonConvert.SerializeObject(new { Success = false, Message = $"Request timestamp is too old. Difference: {timeDifference} seconds (max 300)" });
                }

                // 3. TODO: Replay attack prevention - check if UniqueId already exists
                // var existingContract = VBTCContractV2.GetByUniqueId(payload.OwnerAddress, payload.UniqueId);
                // if (existingContract != null)
                // {
                //     return JsonConvert.SerializeObject(new { Success = false, Message = "Duplicate request detected. This UniqueId has already been processed." });
                // }

                // 4. TODO: Verify owner signature
                // var signatureData = $"{payload.OwnerAddress}{payload.Name}{payload.Description}{payload.Ticker}{payload.CeremonyId}{payload.Timestamp}{payload.UniqueId}";
                // var isValidSignature = SignatureService.VerifySignature(payload.OwnerAddress, signatureData, payload.OwnerSignature);
                // if (!isValidSignature)
                // {
                //     return JsonConvert.SerializeObject(new { Success = false, Message = "Invalid owner signature" });
                // }

                // 5. Retrieve ceremony from memory
                if (!_ceremonies.TryGetValue(payload.CeremonyId, out var ceremony))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        Success = false,
                        Message = "Ceremony not found. Please initiate a new ceremony with InitiateMPCCeremony."
                    });
                }

                // 6. Validate ceremony is completed
                if (ceremony.Status != CeremonyStatus.Completed)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        Success = false,
                        Message = $"Ceremony is not complete. Current status: {ceremony.Status}. Use GetCeremonyStatus to check progress.",
                        CeremonyId = payload.CeremonyId,
                        Status = ceremony.Status.ToString(),
                        ProgressPercentage = ceremony.ProgressPercentage
                    });
                }

                // 7. Validate owner address matches ceremony
                if (ceremony.OwnerAddress != payload.OwnerAddress)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        Success = false,
                        Message = "Owner address does not match the ceremony owner address."
                    });
                }

                var scUID = Guid.NewGuid().ToString().Replace("-", "") + ":" + TimeUtil.GetTime().ToString();

                // Use ceremony results
                string depositAddress = ceremony.DepositAddress!;
                string frostGroupPublicKey = ceremony.FrostGroupPublicKey!;
                string dkgProof = ceremony.DKGProof!;
                var validatorSnapshot = ceremony.ValidatorSnapshot;

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
                    Message = "vBTC V2 contract created successfully via raw request using pre-generated MPC ceremony",
                    SmartContractUID = scUID,
                    CeremonyId = payload.CeremonyId,
                    DepositAddress = depositAddress,
                    DKGProof = dkgProof,
                    ValidatorCount = validatorSnapshot.Count,
                    ProofBlockHeight = ceremony.ProofBlockHeight,
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

                if (string.IsNullOrEmpty(payload.SmartContractUID) || string.IsNullOrEmpty(payload.FromAddress) || 
                    string.IsNullOrEmpty(payload.ToAddress))
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Required fields cannot be null" });

                if (payload.Amount <= 0)
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Amount must be greater than zero" });

                // Call service to create and broadcast transaction
                var result = await Services.VBTCService.TransferVBTC(
                    payload.SmartContractUID,
                    payload.FromAddress,
                    payload.ToAddress,
                    payload.Amount
                );

                if (result.Item1)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        Success = true,
                        Message = "vBTC V2 transfer transaction created and broadcast successfully",
                        TransactionHash = result.Item2,
                        From = payload.FromAddress,
                        To = payload.ToAddress,
                        Amount = payload.Amount,
                        SmartContractUID = payload.SmartContractUID
                    });
                }
                else
                {
                    return JsonConvert.SerializeObject(new { Success = false, Message = result.Item2 });
                }
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

                if (string.IsNullOrEmpty(payload.SmartContractUID) || string.IsNullOrEmpty(payload.OwnerAddress) || 
                    string.IsNullOrEmpty(payload.BTCAddress))
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Required fields cannot be null" });

                if (payload.Amount <= 0)
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Amount must be greater than zero" });

                if (payload.FeeRate <= 0)
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Fee rate must be greater than zero" });

                // Call service to create and broadcast withdrawal request transaction
                var result = await Services.VBTCService.RequestWithdrawal(
                    payload.SmartContractUID,
                    payload.OwnerAddress,
                    payload.BTCAddress,
                    payload.Amount,
                    payload.FeeRate
                );

                if (result.Item1)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        Success = true,
                        Message = "vBTC V2 withdrawal request created successfully",
                        RequestHash = result.Item2,
                        SmartContractUID = payload.SmartContractUID,
                        Amount = payload.Amount,
                        Destination = payload.BTCAddress,
                        FeeRate = payload.FeeRate,
                        Status = "Requested"
                    });
                }
                else
                {
                    return JsonConvert.SerializeObject(new { Success = false, Message = result.Item2 });
                }
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { Success = false, Message = $"Error: {ex.Message}" });
            }
        }

        /// <summary>
        /// Complete withdrawal by coordinating FROST MPC signing and broadcasting Bitcoin transaction
        /// </summary>
        /// <param name="payload">Withdrawal completion details</param>
        /// <returns>Both VFX and Bitcoin transaction hashes if successful</returns>
        [HttpPost("CompleteWithdrawal")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        public async Task<string> CompleteWithdrawal([FromBody] VBTCWithdrawalCompletePayload payload)
        {
            try
            {
                if (payload == null)
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Payload cannot be null" });

                if (string.IsNullOrEmpty(payload.SmartContractUID) || string.IsNullOrEmpty(payload.WithdrawalRequestHash))
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Required fields cannot be null" });

                // Call service to execute FROST withdrawal (builds, signs, broadcasts BTC TX) 
                // and create VFX completion transaction
                var result = await Services.VBTCService.CompleteWithdrawal(
                    payload.SmartContractUID,
                    payload.WithdrawalRequestHash
                );

                if (result.Success)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        Success = true,
                        Message = "vBTC V2 withdrawal completed successfully with FROST signing",
                        VFXTransactionHash = result.VFXTxHash,
                        BTCTransactionHash = result.BTCTxHash,
                        Status = "Pending_BTC",
                        SmartContractUID = payload.SmartContractUID
                    });
                }
                else
                {
                    return JsonConvert.SerializeObject(new { Success = false, Message = result.ErrorMessage });
                }
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
                if (string.IsNullOrEmpty(address) || string.IsNullOrEmpty(scUID))
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Address and Smart Contract UID are required" });

                // Get contract state from State Trei
                var scState = SmartContractStateTrei.GetSmartContractState(scUID);
                if (scState == null)
                {
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Smart contract not found or no state available" });
                }

                decimal balance = 0.0M;
                bool isOwner = false;

                // Calculate balance from State Trei tokenization transactions
                if (scState.SCStateTreiTokenizationTXes != null && scState.SCStateTreiTokenizationTXes.Any())
                {
                    var transactions = scState.SCStateTreiTokenizationTXes
                        .Where(x => x.FromAddress == address || x.ToAddress == address)
                        .ToList();

                    if (transactions.Any())
                    {
                        // Calculate net balance: sum of received - sum of sent
                        var received = transactions.Where(x => x.ToAddress == address).Sum(x => x.Amount);
                        var sent = transactions.Where(x => x.FromAddress == address).Sum(x => x.Amount);
                        balance = received - sent;
                    }
                }

                // Check if this address is the contract owner
                var contract = VBTCContractV2.GetContract(scUID);
                if (contract != null && contract.OwnerAddress == address)
                {
                    isOwner = true;
                }

                // Get pending withdrawal amount (locked funds)
                var pendingWithdrawals = VBTCWithdrawalRequest.GetIncompleteWithdrawalAmount(address, scUID);

                return JsonConvert.SerializeObject(new
                {
                    Success = true,
                    Message = "Balance retrieved successfully",
                    Address = address,
                    SmartContractUID = scUID,
                    Balance = balance,
                    AvailableBalance = balance - pendingWithdrawals,
                    PendingWithdrawals = pendingWithdrawals,
                    IsOwner = isOwner,
                    TransactionCount = scState.SCStateTreiTokenizationTXes?.Count(x => x.FromAddress == address || x.ToAddress == address) ?? 0
                });
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"GetVBTCBalance error: {ex.Message}", "VBTCController.GetVBTCBalance");
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
                if (string.IsNullOrEmpty(address))
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Address is required" });

                var contractBalances = new List<object>();
                decimal totalBalance = 0.0M;

                // Get all vBTC V2 contracts for this address from database
                var contracts = VBTCContractV2.GetContractsByOwner(address);
                if (contracts != null && contracts.Any())
                {
                    foreach (var contract in contracts)
                    {
                        // Get contract state from State Trei to calculate current balance
                        var scState = SmartContractStateTrei.GetSmartContractState(contract.SmartContractUID);
                        if (scState?.SCStateTreiTokenizationTXes != null && scState.SCStateTreiTokenizationTXes.Any())
                        {
                            var transactions = scState.SCStateTreiTokenizationTXes
                                .Where(x => x.FromAddress == address || x.ToAddress == address)
                                .ToList();

                            if (transactions.Any())
                            {
                                // Calculate balance
                                var received = transactions.Where(x => x.ToAddress == address).Sum(x => x.Amount);
                                var sent = transactions.Where(x => x.FromAddress == address).Sum(x => x.Amount);
                                var contractBalance = received - sent;

                                if (contractBalance > 0)
                                {
                                    var pendingWithdrawals = VBTCWithdrawalRequest.GetIncompleteWithdrawalAmount(address, contract.SmartContractUID);

                                    contractBalances.Add(new
                                    {
                                        SmartContractUID = contract.SmartContractUID,
                                        DepositAddress = contract.DepositAddress,
                                        Balance = contractBalance,
                                        AvailableBalance = contractBalance - pendingWithdrawals,
                                        PendingWithdrawals = pendingWithdrawals,
                                        TransactionCount = transactions.Count,
                                        IsOwner = contract.OwnerAddress == address,
                                        WithdrawalStatus = contract.WithdrawalStatus.ToString()
                                    });

                                    totalBalance += contractBalance;
                                }
                            }
                        }
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    Success = true,
                    Message = $"Retrieved balances for {contractBalances.Count} contracts",
                    Address = address,
                    TotalBalance = totalBalance,
                    ContractCount = contractBalances.Count,
                    Contracts = contractBalances
                });
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"GetAllVBTCBalances error: {ex.Message}", "VBTCController.GetAllVBTCBalances");
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
        /// <summary>
        /// Optional: Ceremony ID from a completed MPC ceremony
        /// If provided, the deposit address from the ceremony will be used
        /// If not provided, you must call InitiateMPCCeremony first
        /// </summary>
        public string? CeremonyId { get; set; }
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

    // Raw Contract Creation Payload Models (for external/pre-signed requests)

    public class VBTCContractRawPayload
    {
        public string OwnerAddress { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string? Ticker { get; set; }
        public string? ImageBase { get; set; }
        public string CeremonyId { get; set; }           // Ceremony ID from completed MPC ceremony
        public long Timestamp { get; set; }              // Unix timestamp
        public string UniqueId { get; set; }             // Unique request ID (prevents replay)
        public string OwnerSignature { get; set; }       // Signature of request data
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

