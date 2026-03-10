using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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

        /// <summary>
        /// Maximum number of concurrent active (non-terminal) ceremonies allowed across all requestors.
        /// </summary>
        private const int MaxConcurrentCeremonies = 100;

        /// <summary>
        /// Check if a ceremony status is terminal (completed, failed, or timed out)
        /// </summary>
        private static bool IsCeremonyTerminal(CeremonyStatus status) =>
            status == CeremonyStatus.Completed || status == CeremonyStatus.Failed || status == CeremonyStatus.TimedOut;

        /// <summary>
        /// Public accessor for the cleanup service to prune stale ceremonies.
        /// Removes all ceremonies older than the given TTL, and any terminal ceremonies older than terminalTTL.
        /// Returns the number of ceremonies removed.
        /// </summary>
        public static int CleanupStaleCeremonies(long activeTtlSeconds = 3600, long terminalTtlSeconds = 3600)
        {
            var now = TimeUtil.GetTime();
            var removedCount = 0;

            foreach (var kvp in _ceremonies)
            {
                var ceremony = kvp.Value;
                var age = now - ceremony.InitiatedTimestamp;

                if (IsCeremonyTerminal(ceremony.Status))
                {
                    // Terminal ceremonies: remove after terminalTtlSeconds
                    if (age > terminalTtlSeconds)
                    {
                        if (_ceremonies.TryRemove(kvp.Key, out _))
                            removedCount++;
                    }
                }
                else
                {
                    // Active ceremonies: force-expire after activeTtlSeconds
                    if (age > activeTtlSeconds)
                    {
                        ceremony.Status = CeremonyStatus.TimedOut;
                        ceremony.ErrorMessage = "Ceremony expired due to inactivity (1 hour TTL exceeded).";
                        ceremony.CompletedTimestamp = now;
                        removedCount++;
                    }
                }
            }

            return removedCount;
        }

        /// <summary>
        /// Remove a specific ceremony from memory (used after contract creation consumes the result).
        /// </summary>
        public static bool RemoveCeremony(string ceremonyId) => _ceremonies.TryRemove(ceremonyId, out _);

        /// <summary>
        /// Static method to initiate a ceremony — called by both the local VBTCController endpoint
        /// and the public FrostStartup endpoint. Returns a JSON string result.
        /// </summary>
        public static async Task<string> InitiateMPCCeremonyStatic(string ownerAddress)
        {
            try
            {
                if (string.IsNullOrEmpty(ownerAddress))
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Owner address cannot be null" });

                // Anti-spam: Check if this owner already has an active (non-terminal) ceremony
                var existingActive = _ceremonies.Values.FirstOrDefault(c =>
                    c.OwnerAddress == ownerAddress && !IsCeremonyTerminal(c.Status));
                if (existingActive != null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        Success = false,
                        Message = "You already have an active MPC ceremony in progress. Complete or cancel it before starting a new one.",
                        ExistingCeremonyId = existingActive.CeremonyId,
                        Status = existingActive.Status.ToString(),
                        ProgressPercentage = existingActive.ProgressPercentage
                    });
                }

                // Anti-spam: Check global concurrent ceremony cap
                var activeCeremonyCount = _ceremonies.Values.Count(c => !IsCeremonyTerminal(c.Status));
                if (activeCeremonyCount >= MaxConcurrentCeremonies)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        Success = false,
                        Message = $"Maximum concurrent ceremony limit reached ({MaxConcurrentCeremonies}). Please try again later.",
                        ActiveCeremonies = activeCeremonyCount
                    });
                }

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

                // Start background ceremony process (validator-only local execution)
                _ = Task.Run(async () => await ExecuteMPCCeremonyLocallyStatic(ceremonyId));

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
        /// Static method to get ceremony status — called by both the local VBTCController endpoint
        /// and the public FrostStartup endpoint. Returns a JSON string result.
        /// </summary>
        public static string GetCeremonyStatusStatic(string ceremonyId)
        {
            try
            {
                if (string.IsNullOrEmpty(ceremonyId))
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Ceremony ID cannot be null" });

                if (!_ceremonies.TryGetValue(ceremonyId, out var ceremony))
                {
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Ceremony not found" });
                }

                return JsonConvert.SerializeObject(new
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
                    DepositAddress = ceremony.Status == CeremonyStatus.Completed ? ceremony.DepositAddress : null,
                    FrostGroupPublicKey = ceremony.Status == CeremonyStatus.Completed ? ceremony.FrostGroupPublicKey : null,
                    DKGProof = ceremony.Status == CeremonyStatus.Completed ? ceremony.DKGProof : null,
                    ValidatorCount = ceremony.ValidatorSnapshot?.Count ?? 0,
                    RequiredThreshold = ceremony.RequiredThreshold,
                    ProofBlockHeight = ceremony.ProofBlockHeight
                });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { Success = false, Message = $"Error: {ex.Message}" });
            }
        }

        /// <summary>
        /// Static version of ExecuteMPCCeremonyLocally for use by the FrostStartup public endpoint.
        /// This only runs the validator-local path (no remote delegation).
        /// </summary>
        private static async Task ExecuteMPCCeremonyLocallyStatic(string ceremonyId)
        {
            try
            {
                if (!_ceremonies.TryGetValue(ceremonyId, out var ceremony))
                    return;

                // This runs on a validator node — coordinate locally
                ceremony.Status = CeremonyStatus.ValidatingValidators;
                ceremony.ProgressPercentage = 5;

                var currentBlock = Globals.LastBlock.Height;
                var activeValidators = VBTCValidator.GetActiveValidatorsSinceBlock(currentBlock - 1000);
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
                ceremony.Status = CeremonyStatus.Round1InProgress;

                var dkgResult = await Services.FrostMPCService.CoordinateDKGCeremony(
                    ceremonyId,
                    ceremony.OwnerAddress,
                    activeValidators,
                    ceremony.RequiredThreshold,
                    (round, percentage) =>
                    {
                        ceremony.CurrentRound = round;
                        ceremony.ProgressPercentage = percentage;
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

                ceremony.DepositAddress = dkgResult.TaprootAddress;
                ceremony.FrostGroupPublicKey = dkgResult.GroupPublicKey;
                ceremony.DKGProof = dkgResult.DKGProof;
                ceremony.ProofBlockHeight = Globals.LastBlock.Height;
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

        // RegisterValidator removed — on-chain registration goes through
        // ValidatorService.SendVBTCV2RegistrationTx() called by StartupValidatorProcess().
        // Use GetValidatorList / GetValidatorStatus to query validators.

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
                var validators = activeOnly
                    ? VBTCValidator.GetActiveValidators()
                    : VBTCValidator.GetAllValidators();

                return JsonConvert.SerializeObject(new
                {
                    Success = true,
                    Message = "Validators retrieved",
                    Validators = validators ?? new List<VBTCValidator>()
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
                var validator = VBTCValidator.GetValidator(validatorAddress);
                if (validator == null)
                {
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Validator not found" });
                }
                VBTCValidator.UpdateHeartbeat(validatorAddress, Globals.LastBlock.Height);

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
                var validator = VBTCValidator.GetValidator(validatorAddress);
                if (validator == null)
                {
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Validator not found" });
                }

                return JsonConvert.SerializeObject(new
                {
                    Success = true,
                    Message = "Validator found",
                    Validator = validator
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

                // Anti-spam: Check if this owner already has an active (non-terminal) ceremony
                var existingActive = _ceremonies.Values.FirstOrDefault(c =>
                    c.OwnerAddress == ownerAddress && !IsCeremonyTerminal(c.Status));
                if (existingActive != null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        Success = false,
                        Message = "You already have an active MPC ceremony in progress. Complete or cancel it before starting a new one.",
                        ExistingCeremonyId = existingActive.CeremonyId,
                        Status = existingActive.Status.ToString(),
                        ProgressPercentage = existingActive.ProgressPercentage
                    });
                }

                // Anti-spam: Check global concurrent ceremony cap
                var activeCeremonyCount = _ceremonies.Values.Count(c => !IsCeremonyTerminal(c.Status));
                if (activeCeremonyCount >= MaxConcurrentCeremonies)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        Success = false,
                        Message = $"Maximum concurrent ceremony limit reached ({MaxConcurrentCeremonies}). Please try again later.",
                        ActiveCeremonies = activeCeremonyCount
                    });
                }

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
        /// Cancel an active MPC ceremony. Only the original owner can cancel.
        /// </summary>
        /// <param name="ceremonyId">Ceremony ID to cancel</param>
        /// <param name="ownerAddress">Owner address that initiated the ceremony</param>
        /// <returns>Cancellation result</returns>
        [HttpPost("CancelCeremony/{ceremonyId}/{ownerAddress}")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        public async Task<string> CancelCeremony(string ceremonyId, string ownerAddress)
        {
            try
            {
                if (string.IsNullOrEmpty(ceremonyId))
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Ceremony ID cannot be null" });

                if (string.IsNullOrEmpty(ownerAddress))
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Owner address cannot be null" });

                if (!_ceremonies.TryGetValue(ceremonyId, out var ceremony))
                {
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Ceremony not found" });
                }

                if (ceremony.OwnerAddress != ownerAddress)
                {
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Only the ceremony owner can cancel it" });
                }

                if (IsCeremonyTerminal(ceremony.Status))
                {
                    // Already terminal — just remove it
                    _ceremonies.TryRemove(ceremonyId, out _);
                    return JsonConvert.SerializeObject(new
                    {
                        Success = true,
                        Message = $"Ceremony was already in terminal state ({ceremony.Status}). Removed from memory."
                    });
                }

                // Mark as failed (cancelled)
                ceremony.Status = CeremonyStatus.Failed;
                ceremony.ErrorMessage = "Ceremony cancelled by owner";
                ceremony.CompletedTimestamp = TimeUtil.GetTime();

                // Remove immediately since owner explicitly cancelled
                _ceremonies.TryRemove(ceremonyId, out _);

                return JsonConvert.SerializeObject(new
                {
                    Success = true,
                    Message = "Ceremony cancelled and removed successfully",
                    CeremonyId = ceremonyId,
                    PreviousStatus = ceremony.Status.ToString()
                });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { Success = false, Message = $"Error: {ex.Message}" });
            }
        }

        /// <summary>
        /// Background task that executes the MPC (FROST DKG) ceremony.
        /// If this node is a validator, it coordinates the ceremony locally.
        /// If this node is a regular wallet, it delegates to a remote validator.
        /// </summary>
        private async Task ExecuteMPCCeremony(string ceremonyId)
        {
            try
            {
                if (!_ceremonies.TryGetValue(ceremonyId, out var ceremony))
                    return;

                // Non-validator wallet node: delegate the entire ceremony to a remote validator
                if (string.IsNullOrEmpty(Globals.ValidatorAddress))
                {
                    await ExecuteMPCCeremonyViaRemoteValidator(ceremonyId, ceremony);
                    return;
                }

                // Validator node: coordinate the ceremony locally
                await ExecuteMPCCeremonyLocally(ceremonyId, ceremony);
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

        /// <summary>
        /// Execute MPC ceremony locally (validator node path)
        /// </summary>
        private async Task ExecuteMPCCeremonyLocally(string ceremonyId, MPCCeremonyState ceremony)
        {
            // Update status: Validating validators
            ceremony.Status = CeremonyStatus.ValidatingValidators;
            ceremony.ProgressPercentage = 5;

            // Step 1: Get list of active validators
            var currentBlock = Globals.LastBlock.Height;
            var activeValidators = VBTCValidator.GetActiveValidatorsSinceBlock(currentBlock - 1000);
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
                    ceremony.CurrentRound = round;
                    ceremony.ProgressPercentage = percentage;

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

            ceremony.Status = CeremonyStatus.Completed;
            ceremony.ProgressPercentage = 100;
            ceremony.CompletedTimestamp = TimeUtil.GetTime();
        }

        /// <summary>
        /// Execute MPC ceremony by delegating to a remote validator (wallet node path).
        /// The wallet node sends the ceremony request to a validator, which coordinates the
        /// actual FROST DKG. The wallet then polls for completion and syncs the results locally.
        /// </summary>
        private async Task ExecuteMPCCeremonyViaRemoteValidator(string ceremonyId, MPCCeremonyState ceremony)
        {
            ceremony.Status = CeremonyStatus.ValidatingValidators;
            ceremony.ProgressPercentage = 5;

            // Step 1: Discover active validators from the network
            var activeValidators = await VBTCValidator.FetchActiveValidatorsFromNetwork();
            if (activeValidators == null || !activeValidators.Any())
            {
                ceremony.Status = CeremonyStatus.Failed;
                ceremony.ErrorMessage = "No active validators available on the network. Cannot delegate MPC ceremony.";
                ceremony.CompletedTimestamp = TimeUtil.GetTime();
                return;
            }

            // Step 2: Try each validator until one successfully initiates the ceremony
            string? remoteValidatorIP = null;
            string? remoteCeremonyId = null;

            foreach (var validator in activeValidators)
            {
                try
                {
                    var ip = validator.IPAddress?.Replace("::ffff:", "");
                    if (string.IsNullOrEmpty(ip)) continue;

                    var url = $"http://{ip}:{Globals.FrostValidatorPort}/frost/mpc/initiate/{ceremony.OwnerAddress}";
                    using var client = Globals.HttpClientFactory.CreateClient();
                    client.Timeout = TimeSpan.FromSeconds(15);

                    var response = await client.PostAsync(url, null);
                    if (!response.IsSuccessStatusCode) continue;

                    var responseBody = await response.Content.ReadAsStringAsync();
                    var json = Newtonsoft.Json.Linq.JObject.Parse(responseBody);

                    if (json["Success"]?.Value<bool>() == true && !string.IsNullOrEmpty(json["CeremonyId"]?.Value<string>()))
                    {
                        remoteValidatorIP = ip;
                        remoteCeremonyId = json["CeremonyId"]!.Value<string>();

                        LogUtility.Log($"[FROST MPC] Wallet node delegated ceremony to validator {validator.ValidatorAddress} ({ip}). Remote CeremonyId: {remoteCeremonyId}",
                            "VBTCController.ExecuteMPCCeremonyViaRemoteValidator");
                        break;
                    }
                }
                catch (Exception ex)
                {
                    LogUtility.Log($"[FROST MPC] Failed to delegate to validator {validator.ValidatorAddress}: {ex.Message}",
                        "VBTCController.ExecuteMPCCeremonyViaRemoteValidator");
                }
            }

            if (string.IsNullOrEmpty(remoteValidatorIP) || string.IsNullOrEmpty(remoteCeremonyId))
            {
                ceremony.Status = CeremonyStatus.Failed;
                ceremony.ErrorMessage = "Failed to delegate MPC ceremony to any validator. No validator accepted the request.";
                ceremony.CompletedTimestamp = TimeUtil.GetTime();
                return;
            }

            // Step 3: Mark ceremony as remote and store delegation info
            ceremony.IsRemote = true;
            ceremony.RemoteValidatorIP = remoteValidatorIP;
            ceremony.RemoteCeremonyId = remoteCeremonyId;
            ceremony.ProgressPercentage = 10;
            ceremony.Status = CeremonyStatus.Round1InProgress;

            // Step 4: Poll the remote validator until the ceremony completes or fails
            var maxPollAttempts = 120; // Up to ~4 minutes (120 * 2s)
            var pollInterval = 2000; // 2 seconds

            for (int attempt = 0; attempt < maxPollAttempts; attempt++)
            {
                await Task.Delay(pollInterval);

                try
                {
                    var statusUrl = $"http://{remoteValidatorIP}:{Globals.FrostValidatorPort}/frost/mpc/status/{remoteCeremonyId}";
                    using var client = Globals.HttpClientFactory.CreateClient();
                    client.Timeout = TimeSpan.FromSeconds(10);

                    var response = await client.GetAsync(statusUrl);
                    if (!response.IsSuccessStatusCode) continue;

                    var responseBody = await response.Content.ReadAsStringAsync();
                    var json = Newtonsoft.Json.Linq.JObject.Parse(responseBody);

                    if (json["Success"]?.Value<bool>() != true) continue;

                    var remoteStatus = json["Status"]?.Value<string>() ?? "";
                    var remoteProgress = json["ProgressPercentage"]?.Value<int>() ?? 0;
                    var remoteRound = json["CurrentRound"]?.Value<int>() ?? 0;

                    // Sync progress to local ceremony state
                    ceremony.CurrentRound = remoteRound;
                    ceremony.ProgressPercentage = remoteProgress;
                    if (Enum.TryParse<CeremonyStatus>(remoteStatus, out var parsedStatus))
                        ceremony.Status = parsedStatus;

                    // Check for completion
                    if (remoteStatus == "Completed")
                    {
                        ceremony.DepositAddress = json["DepositAddress"]?.Value<string>();
                        ceremony.FrostGroupPublicKey = json["FrostGroupPublicKey"]?.Value<string>();
                        ceremony.DKGProof = json["DKGProof"]?.Value<string>();
                        ceremony.ProofBlockHeight = json["ProofBlockHeight"]?.Value<long>() ?? 0;
                        ceremony.ValidatorSnapshot = json["ValidatorCount"]?.Value<int>() > 0
                            ? activeValidators.Select(v => v.ValidatorAddress).ToList()
                            : new List<string>();
                        ceremony.Status = CeremonyStatus.Completed;
                        ceremony.ProgressPercentage = 100;
                        ceremony.CompletedTimestamp = TimeUtil.GetTime();

                        LogUtility.Log($"[FROST MPC] Remote ceremony completed. Deposit address: {ceremony.DepositAddress}",
                            "VBTCController.ExecuteMPCCeremonyViaRemoteValidator");
                        return;
                    }

                    // Check for failure
                    if (remoteStatus == "Failed" || remoteStatus == "TimedOut")
                    {
                        ceremony.Status = CeremonyStatus.Failed;
                        ceremony.ErrorMessage = json["ErrorMessage"]?.Value<string>() ?? "Remote ceremony failed";
                        ceremony.CompletedTimestamp = TimeUtil.GetTime();
                        return;
                    }
                }
                catch (Exception ex)
                {
                    // Network hiccup during poll — continue retrying
                    LogUtility.Log($"[FROST MPC] Poll attempt {attempt + 1} failed: {ex.Message}",
                        "VBTCController.ExecuteMPCCeremonyViaRemoteValidator");
                }
            }

            // Timed out waiting for remote ceremony
            ceremony.Status = CeremonyStatus.TimedOut;
            ceremony.ErrorMessage = $"Timed out waiting for remote validator ({remoteValidatorIP}) to complete MPC ceremony after {maxPollAttempts * pollInterval / 1000} seconds.";
            ceremony.CompletedTimestamp = TimeUtil.GetTime();
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
                    SmartContractAsset = new SmartContractAsset
                    {
                        Name = "vbtc_v2_token",
                        Location = "default",
                        AssetAuthorName = payload.OwnerAddress,
                        FileSize = 0
                    },
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

                // Mark contract as published in the tokenized bitcoin database
                await TokenizedBitcoin.SetTokenContractIsPublished(scUID);

                // Ceremony results consumed — remove from memory immediately to free space
                RemoveCeremony(payload.CeremonyId);

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
                if (string.IsNullOrEmpty(scUID))
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Smart contract UID is required" });

                // FIND-025 Fix: Load real contract data instead of returning placeholders
                var contract = VBTCContractV2.GetContract(scUID);
                if (contract == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        Success = false,
                        Message = "vBTC V2 contract not found for the given scUID"
                    });
                }

                if (string.IsNullOrEmpty(contract.DepositAddress))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        Success = false,
                        Message = "Contract exists but deposit address has not been generated yet. DKG ceremony may not have completed."
                    });
                }

                return JsonConvert.SerializeObject(new
                {
                    Success = true,
                    Message = "Deposit address retrieved",
                    SmartContractUID = scUID,
                    DepositAddress = contract.DepositAddress,
                    FrostGroupPublicKey = contract.FrostGroupPublicKey ?? string.Empty,
                    RequiredThreshold = contract.RequiredThreshold,
                    DKGProof = contract.DKGProof ?? string.Empty
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

                // 3. Verify owner signature
                var signatureData = $"{payload.OwnerAddress}{payload.Name}{payload.Description}{payload.Ticker}{payload.CeremonyId}{payload.Timestamp}{payload.UniqueId}";
                var isValidSignature = SignatureService.VerifySignature(payload.OwnerAddress, signatureData, payload.OwnerSignature);
                if (!isValidSignature)
                {
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Invalid owner signature" });
                }

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
                    SmartContractAsset = new SmartContractAsset
                    {
                        Name = "vbtc_v2_token",
                        Location = "default",
                        AssetAuthorName = payload.OwnerAddress,
                        FileSize = 0
                    },
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
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Failed to generate smart contract code" });
                }

                // Save smart contract to databases
                SmartContractMain.SmartContractData.SaveSmartContract(result.Item2, result.Item1);
                await VBTCContractV2.SaveSmartContract(result.Item2, result.Item1, payload.OwnerAddress);

                // Create and broadcast mint transaction
                var scTx = await SmartContractService.MintSmartContractTx(result.Item2, TransactionType.VBTC_V2_CONTRACT_CREATE);
                if (scTx == null)
                {
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Failed to create or broadcast smart contract transaction" });
                }

                // Ceremony results consumed — remove from memory immediately to free space
                RemoveCeremony(payload.CeremonyId);

                return JsonConvert.SerializeObject(new
                {
                    Success = true,
                    Message = "vBTC V2 contract created and published to blockchain successfully via raw request",
                    SmartContractUID = scUID,
                    TransactionHash = scTx.Hash,
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
                    Success = false,
                    Message = "Multi-transfer is not yet supported. Use single TransferVBTC for individual transfers."
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

                // Validate required fields
                if (string.IsNullOrEmpty(payload.SmartContractUID) || string.IsNullOrEmpty(payload.OwnerAddress) ||
                    string.IsNullOrEmpty(payload.WithdrawalRequestHash))
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Required fields cannot be null" });

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

                // Save cancellation to database
                VBTCWithdrawalCancellation.SaveCancellation(cancellation);

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

                // Validate required fields
                if (string.IsNullOrEmpty(payload.CancellationUID) || string.IsNullOrEmpty(payload.ValidatorAddress))
                    return JsonConvert.SerializeObject(new { Success = false, Message = "CancellationUID and ValidatorAddress are required" });

                // Verify validator is active
                var validator = VBTCValidator.GetValidator(payload.ValidatorAddress);
                if (validator == null || !validator.IsActive)
                {
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Validator is not active or not found" });
                }

                // Get cancellation record
                var cancellation = VBTCWithdrawalCancellation.GetCancellation(payload.CancellationUID);
                if (cancellation == null)
                {
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Cancellation request not found" });
                }

                if (cancellation.IsProcessed)
                {
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Cancellation has already been processed" });
                }

                // Check if validator already voted
                if (VBTCWithdrawalCancellation.HasValidatorVoted(payload.CancellationUID, payload.ValidatorAddress))
                {
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Validator has already voted on this cancellation" });
                }

                // Record vote
                VBTCWithdrawalCancellation.AddVote(payload.CancellationUID, payload.ValidatorAddress, payload.Approve);

                // Refresh cancellation data after vote
                cancellation = VBTCWithdrawalCancellation.GetCancellation(payload.CancellationUID);
                int totalValidators = VBTCValidator.GetActiveValidatorCount();
                int votePercentage = totalValidators > 0 
                    ? VBTCWithdrawalCancellation.GetVotePercentage(payload.CancellationUID, totalValidators) 
                    : 0;

                // Check if 75% threshold reached
                if (votePercentage >= 75 && !cancellation!.IsProcessed)
                {
                    VBTCWithdrawalCancellation.MarkAsProcessed(payload.CancellationUID, true);
                    cancellation = VBTCWithdrawalCancellation.GetCancellation(payload.CancellationUID);
                }

                return JsonConvert.SerializeObject(new
                {
                    Success = true,
                    Message = "Vote recorded",
                    CancellationUID = payload.CancellationUID,
                    ApproveCount = cancellation?.ApproveCount ?? 0,
                    RejectCount = cancellation?.RejectCount ?? 0,
                    TotalValidators = totalValidators,
                    ApprovalPercentage = votePercentage,
                    IsApproved = cancellation?.IsApproved ?? false
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

                // 5. Verify VFX signature
                var signatureData = $"{payload.VFXAddress}{payload.BTCAddress}{payload.SmartContractUID}{payload.Amount}{payload.FeeRate}{payload.Timestamp}{payload.UniqueId}";
                var isValidSignature = SignatureService.VerifySignature(payload.VFXAddress, signatureData, payload.VFXSignature);
                if (!isValidSignature)
                {
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Invalid VFX signature" });
                }

                // 6. Validate balance from State Trei
                var scState = SmartContractStateTrei.GetSmartContractState(payload.SmartContractUID);
                if (scState != null && scState.SCStateTreiTokenizationTXes != null)
                {
                    var tokenTxs = scState.SCStateTreiTokenizationTXes
                        .Where(x => x.FromAddress == payload.VFXAddress || x.ToAddress == payload.VFXAddress).ToList();
                    var received = tokenTxs.Where(x => x.ToAddress == payload.VFXAddress).Sum(x => x.Amount);
                    var sent = tokenTxs.Where(x => x.FromAddress == payload.VFXAddress).Sum(x => x.Amount);
                    var vbtcBalance = received - sent;
                    if (payload.Amount > vbtcBalance)
                    {
                        return JsonConvert.SerializeObject(new { Success = false, Message = $"Insufficient vBTC balance. Available: {vbtcBalance}, Requested: {payload.Amount}" });
                    }
                }
                else
                {
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Smart contract state not found or no tokenization transactions exist." });
                }

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

                // 9. Update contract withdrawal status to "Requested"
                VBTCContractV2.UpdateWithdrawalStatus(payload.SmartContractUID, VBTCWithdrawalStatus.Requested);

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

                // 3. Verify validator is active and eligible
                var validator = VBTCValidator.GetValidator(payload.ValidatorAddress);
                if (validator == null || !validator.IsActive)
                {
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Validator is not active or not found" });
                }

                // 4. Verify validator signature (VFX signature over request data)
                var signatureData = $"{payload.SmartContractUID}{payload.WithdrawalRequestHash}{payload.ValidatorAddress}{payload.Timestamp}{payload.UniqueId}";
                var isValidSignature = SignatureService.VerifySignature(payload.ValidatorAddress, signatureData, payload.ValidatorSignature);
                if (!isValidSignature)
                {
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Invalid validator signature" });
                }

                // FIND-024 Fix: Wire to real FROST signing via VBTCService.CompleteWithdrawal
                // This uses the same path as the non-raw CompleteWithdrawal endpoint,
                // which coordinates the FROST MPC signing ceremony and broadcasts the BTC TX.
                var withdrawalResult = await Services.VBTCService.CompleteWithdrawal(
                    payload.SmartContractUID,
                    payload.WithdrawalRequestHash
                );

                if (!withdrawalResult.Success)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        Success = false,
                        Message = withdrawalResult.ErrorMessage ?? "FROST signing ceremony failed"
                    });
                }

                return JsonConvert.SerializeObject(new
                {
                    Success = true,
                    Message = "Raw withdrawal completed successfully via FROST signing",
                    BTCTransactionHash = withdrawalResult.BTCTxHash,
                    VFXTransactionHash = withdrawalResult.VFXTxHash,
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

                // 3. Verify owner signature
                var signatureData = $"{payload.SmartContractUID}{payload.OwnerAddress}{payload.WithdrawalRequestHash}{payload.BTCTxHash}{payload.FailureProof}{payload.Timestamp}{payload.UniqueId}";
                var isValidSignature = SignatureService.VerifySignature(payload.OwnerAddress, signatureData, payload.OwnerSignature);
                if (!isValidSignature)
                {
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Invalid owner signature" });
                }

                // 4. Verify owner is the contract owner
                var contract = VBTCContractV2.GetContract(payload.SmartContractUID);
                if (contract == null || contract.OwnerAddress != payload.OwnerAddress)
                {
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Only the contract owner can request cancellation" });
                }

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

                // 7. Save to database
                VBTCWithdrawalCancellation.SaveCancellation(cancellation);

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
                var contract = VBTCContractV2.GetContract(scUID);
                if (contract == null)
                {
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Contract not found" });
                }

                return JsonConvert.SerializeObject(new
                {
                    Success = true,
                    Message = "Contract details retrieved",
                    SmartContractUID = scUID,
                    Contract = contract
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
                var contract = VBTCContractV2.GetContract(scUID);
                if (contract == null)
                {
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Contract not found" });
                }

                return JsonConvert.SerializeObject(new
                {
                    Success = true,
                    Message = "Withdrawal history retrieved",
                    SmartContractUID = scUID,
                    WithdrawalHistory = contract.WithdrawalHistory ?? new List<VBTCWithdrawalHistory>()
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
                var contract = VBTCContractV2.GetContract(scUID);
                if (contract == null)
                {
                    return JsonConvert.SerializeObject(new { Success = false, Message = "Contract not found" });
                }

                var hasActiveWithdrawal = VBTCContractV2.HasActiveWithdrawal(scUID);

                return JsonConvert.SerializeObject(new
                {
                    Success = true,
                    Message = "Withdrawal status retrieved",
                    SmartContractUID = scUID,
                    Status = contract.WithdrawalStatus.ToString(),
                    HasActiveWithdrawal = hasActiveWithdrawal
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
                return JsonConvert.SerializeObject(new
                {
                    Success = true,
                    Message = "Default image retrieved",
                    EncodingFormat = "base64",
                    ImageExtension = "png",
                    ImageName = "defaultvBTC_V2.png",
                    ImageBase = string.Empty // No default image bundled; callers should provide their own
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
                var contracts = string.IsNullOrEmpty(address)
                    ? VBTCContractV2.GetAllContracts()
                    : VBTCContractV2.GetContractsByOwner(address);

                return JsonConvert.SerializeObject(new
                {
                    Success = true,
                    Message = "Contract list retrieved",
                    Contracts = contracts ?? new List<VBTCContractV2>()
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

