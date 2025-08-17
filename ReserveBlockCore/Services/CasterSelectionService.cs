using ReserveBlockCore.Data;
using ReserveBlockCore.Models;
using ReserveBlockCore.Utilities;
using System.Security.Cryptography;
using System.Text;

namespace ReserveBlockCore.Services
{
    /// <summary>
    /// Handles algorithmic selection of block casters from the validator pool.
    /// Provides deterministic, fair, and verifiable caster selection and rotation.
    /// </summary>
    public static class CasterSelectionService
    {
        private static readonly SemaphoreSlim SelectionLock = new(1, 1);

        /// <summary>
        /// Gets the current casters based on the consensus phase and available validators.
        /// Handles bootstrap, transitional, and normal phases automatically.
        /// </summary>
        /// <param name="blockHeight">Current block height for selection</param>
        /// <returns>List of current casters</returns>
        public static async Task<List<Peers>> GetCurrentCasters(long blockHeight)
        {
            await SelectionLock.WaitAsync();
            try
            {
                var phase = await BootstrapService.GetCurrentPhase(blockHeight);
                
                switch (phase)
                {
                    case BootstrapService.ConsensusPhase.Bootstrap:
                        return await GetBootstrapCasters();

                    case BootstrapService.ConsensusPhase.Transitional:
                        return await GetTransitionalCasters(blockHeight);

                    case BootstrapService.ConsensusPhase.Normal:
                        return await GetAlgorithmicCasters(blockHeight);

                    default:
                        LogUtility.Log($"Unknown consensus phase: {phase}, falling back to bootstrap", "CasterSelectionService.GetCurrentCasters");
                        return await GetBootstrapCasters();
                }
            }
            finally
            {
                SelectionLock.Release();
            }
        }

        /// <summary>
        /// Gets bootstrap casters - uses hardcoded initial casters plus any active validators.
        /// Used during the initial bootstrap period to ensure network stability.
        /// </summary>
        /// <returns>List of bootstrap casters including hardcoded initial validators</returns>
        private static async Task<List<Peers>> GetBootstrapCasters()
        {
            try
            {
                var bootstrapCasters = new List<Peers>();
                
                // Add hardcoded bootstrap casters for initial chain startup
                var hardcodedCasters = GetHardcodedBootstrapCasters();
                bootstrapCasters.AddRange(hardcodedCasters);
                
                // Add any active validators from the network
                var eligibleValidators = await GetEligibleValidators();
                
                foreach (var validator in eligibleValidators)
                {
                    // Skip if already added as hardcoded caster
                    if (bootstrapCasters.Any(c => c.ValidatorAddress == validator.Address))
                        continue;
                        
                    var peer = new Peers
                    {
                        IsIncoming = false,
                        IsOutgoing = true,
                        PeerIP = validator.IPAddress,
                        FailCount = validator.CheckFailCount,
                        IsValidator = true,
                        ValidatorAddress = validator.Address,
                        ValidatorPublicKey = validator.PublicKey
                    };
                    
                    bootstrapCasters.Add(peer);
                }

                LogUtility.Log($"Bootstrap mode: {bootstrapCasters.Count} casters ({hardcodedCasters.Count} hardcoded, {eligibleValidators.Count} network)", "CasterSelectionService.GetBootstrapCasters");
                return bootstrapCasters;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error getting bootstrap casters: {ex.Message}", "CasterSelectionService.GetBootstrapCasters");
                
                // Fallback to just hardcoded casters if there's an error
                return GetHardcodedBootstrapCasters();
            }
        }

        /// <summary>
        /// Gets the hardcoded bootstrap casters for initial chain startup.
        /// These are the known validators that help bootstrap the network.
        /// </summary>
        /// <returns>List of hardcoded bootstrap casters</returns>
        private static List<Peers> GetHardcodedBootstrapCasters()
        {
            if (Globals.IsTestNet)
            {
                return new List<Peers>
                {
                    new Peers
                    {
                        IsIncoming = false,
                        IsOutgoing = true,
                        PeerIP = "144.126.156.102",
                        FailCount = 0,
                        IsValidator = true,
                        ValidatorAddress = "xBRzJUZiXjE3hkrpzGYMSpYCHU1yPpu8cj",
                        ValidatorPublicKey = "0498ea84777552a3609143275b0e083086071a6b1453bd46b87a05461d24e0ee99e7de2870a018240026ad6ba892a087df39447f91c5a8f8e50a53b6643c9e713c"
                    },
                    new Peers
                    {
                        IsIncoming = false,
                        IsOutgoing = true,
                        PeerIP = "66.94.124.2",
                        FailCount = 0,
                        IsValidator = true,
                        ValidatorAddress = "xMpa8DxDLdC9SQPcAFBc2vqwyPsoFtrWyC",
                        ValidatorPublicKey = "04eec44726e6442cc2ec0241f7c8c2a983d9cfbf9f68a2bc3e2040fd1053636f3779ffaeabcda9065627dee6d3ff5f080833e8ff8a3e93b8f17a600d0f7d090687"
                    },
                    new Peers
                    {
                        IsIncoming = false,
                        IsOutgoing = true,
                        PeerIP = "66.94.124.3",
                        FailCount = 0,
                        IsValidator = true,
                        ValidatorAddress = "xCkUC4rrh2AnfNf78D5Ps83pMywk5vrwpi",
                        ValidatorPublicKey = "0474f0a933dc0241d5fc6059eeb3a14350c4ba890e8e504ae144f33a41c0f5eb9aed9b98e33265507c55e2af1b0c61c5c2a87fa55d86acad0592c0f4774c97e62b"
                    }
                };
            }
            else
            {
                // MainNet hardcoded casters
                return new List<Peers>
                {
                    new Peers
                    {
                        IsIncoming = false,
                        IsOutgoing = true,
                        PeerIP = "15.204.9.193",
                        FailCount = 0,
                        IsValidator = true,
                        ValidatorAddress = "RK28ywrBfEXV5EuARn3etyVXMtcmywNxnM",
                        ValidatorPublicKey = "04b906f0da02bbc25f0d65f30a9f07bb9a93bc78aee1d894fd4492ff0b8c97e05e0fb0d698f13a852578b3cb6bd9c97440820b24c859ac43f35ccb7eb03e9eccc6"
                    },
                    new Peers
                    {
                        IsIncoming = false,
                        IsOutgoing = true,
                        PeerIP = "15.204.9.117",
                        FailCount = 0,
                        IsValidator = true,
                        ValidatorAddress = "RFoKrASMr19mg8S71Lf1F2suzxahG5Yj4N",
                        ValidatorPublicKey = "04aa4c95e0754b87a26f3c853f71a01e1ad5ed2d269d2f12b09dcb3b86637eb88550abe954c3bd99eb64b68dfcf8174bb0fb159b0a83bb2610e272f16094f37d9e"
                    },
                    new Peers
                    {
                        IsIncoming = false,
                        IsOutgoing = true,
                        PeerIP = "66.175.236.113",
                        FailCount = 0,
                        IsValidator = true,
                        ValidatorAddress = "RH9XAP3omXvk7P6Xe9fQ1C6nZQ1adJw2ZG",
                        ValidatorPublicKey = "0433c204046d55d09c62cf5ae83dcdb81a843eb45a1bff8ce93986bee8149812d639ce003a8714dada8efa270b7ba61c2b45c9a8840c40e03d3375dc86f68bab6e"
                    }
                };
            }
        }

        /// <summary>
        /// Gets casters during transitional phase - attempts algorithmic selection,
        /// falls back to bootstrap if insufficient validators.
        /// </summary>
        /// <param name="blockHeight">Current block height</param>
        /// <returns>List of selected casters or bootstrap casters</returns>
        private static async Task<List<Peers>> GetTransitionalCasters(long blockHeight)
        {
            try
            {
                var eligibleValidators = await GetEligibleValidators();
                var maxCasters = CasterConfiguration.GetMaxCasters();
                var minValidators = CasterConfiguration.GetMinimumValidatorsForTransition();

                // Check if we can transition to algorithmic selection
                if (await BootstrapService.CanTransitionFromBootstrap(blockHeight, eligibleValidators.Count))
                {
                    var algorithmicCasters = await PerformAlgorithmicSelection(blockHeight, eligibleValidators, maxCasters);
                    
                    if (algorithmicCasters.Count >= CasterConfiguration.GetConsensusThreshold())
                    {
                        // Successful transition - mark it
                        await BootstrapService.MarkSuccessfulCasterSelection();
                        LogUtility.Log($"Transitional phase: Successfully selected {algorithmicCasters.Count} algorithmic casters", "CasterSelectionService.GetTransitionalCasters");
                        return algorithmicCasters;
                    }
                }

                // Fall back to bootstrap mode
                LogUtility.Log($"Transitional phase: Insufficient validators ({eligibleValidators.Count}/{minValidators}), staying in bootstrap", "CasterSelectionService.GetTransitionalCasters");
                return await GetBootstrapCasters();
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error in transitional caster selection: {ex.Message}", "CasterSelectionService.GetTransitionalCasters");
                return await GetBootstrapCasters();
            }
        }

        /// <summary>
        /// Gets casters using full algorithmic selection with rotation.
        /// Used in normal operation mode for decentralized caster management.
        /// </summary>
        /// <param name="blockHeight">Current block height</param>
        /// <returns>List of algorithmically selected casters</returns>
        private static async Task<List<Peers>> GetAlgorithmicCasters(long blockHeight)
        {
            try
            {
                var eligibleValidators = await GetEligibleValidators();
                var maxCasters = CasterConfiguration.GetMaxCasters();

                if (eligibleValidators.Count < CasterConfiguration.GetConsensusThreshold())
                {
                    LogUtility.Log($"Insufficient validators for normal operation ({eligibleValidators.Count}), reverting to bootstrap", "CasterSelectionService.GetAlgorithmicCasters");
                    
                    // Reset caster selection status to allow transitional phase
                    Globals.HasSuccessfulCasterSelection = false;
                    return await GetBootstrapCasters();
                }

                var selectedCasters = await PerformAlgorithmicSelection(blockHeight, eligibleValidators, maxCasters);
                LogUtility.Log($"Normal phase: Selected {selectedCasters.Count} algorithmic casters for rotation epoch", "CasterSelectionService.GetAlgorithmicCasters");
                
                return selectedCasters;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error in algorithmic caster selection: {ex.Message}", "CasterSelectionService.GetAlgorithmicCasters");
                return await GetBootstrapCasters();
            }
        }

        /// <summary>
        /// Performs the core algorithmic selection using deterministic randomness.
        /// Uses blockchain entropy to ensure fair, verifiable, and unpredictable selection.
        /// </summary>
        /// <param name="blockHeight">Current block height</param>
        /// <param name="eligibleValidators">Pool of eligible validators</param>
        /// <param name="maxCasters">Maximum number of casters to select</param>
        /// <returns>List of selected casters</returns>
        private static async Task<List<Peers>> PerformAlgorithmicSelection(long blockHeight, List<NetworkValidator> eligibleValidators, int maxCasters)
        {
            try
            {
                // Calculate rotation epoch (every 7,200 blocks = 24 hours)
                var rotationFrequency = CasterConfiguration.GetRotationFrequency();
                var rotationEpoch = blockHeight / rotationFrequency;

                // Generate deterministic entropy for selection
                var entropy = await GenerateSelectionEntropy(rotationEpoch, blockHeight);
                
                // Perform deterministic selection
                var selectedValidators = SelectValidatorsDeterministically(eligibleValidators, entropy, maxCasters);
                
                // Convert to Peers format with connectivity validation
                var selectedCasters = new List<Peers>();
                
                foreach (var validator in selectedValidators)
                {
                    // Verify connectivity before adding to caster list
                    if (await ValidatorConnectivityService.VerifyValidatorConnectivity(validator.IPAddress))
                    {
                        var peer = new Peers
                        {
                            IsIncoming = false,
                            IsOutgoing = true,
                            PeerIP = validator.IPAddress,
                            FailCount = validator.CheckFailCount,
                            IsValidator = true,
                            ValidatorAddress = validator.Address,
                            ValidatorPublicKey = validator.PublicKey
                        };
                        
                        selectedCasters.Add(peer);
                    }
                    else
                    {
                        LogUtility.Log($"Skipping validator {validator.Address} - connectivity check failed", "CasterSelectionService.PerformAlgorithmicSelection");
                    }
                }

                LogUtility.Log($"Algorithmic selection: {selectedCasters.Count}/{maxCasters} casters selected for epoch {rotationEpoch}", "CasterSelectionService.PerformAlgorithmicSelection");
                return selectedCasters;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error in algorithmic selection: {ex.Message}", "CasterSelectionService.PerformAlgorithmicSelection");
                return new List<Peers>();
            }
        }

        /// <summary>
        /// Generates deterministic entropy for caster selection using blockchain data.
        /// Ensures unpredictable but verifiable randomness that all nodes can reproduce.
        /// </summary>
        /// <param name="rotationEpoch">Current rotation epoch</param>
        /// <param name="blockHeight">Current block height</param>
        /// <returns>Deterministic entropy string</returns>
        private static async Task<string> GenerateSelectionEntropy(long rotationEpoch, long blockHeight)
        {
            try
            {
                // Use previous block hash for entropy (prevents pre-computation)
                var previousBlockHash = Globals.LastBlock.Hash ?? "";
                
                // Combine multiple sources of entropy
                var entropyInput = $"{rotationEpoch}:{blockHeight}:{previousBlockHash}:CASTER_SELECTION_SEED_V1";
                
                // Generate SHA256 hash for deterministic randomness
                using var sha256 = SHA256.Create();
                var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(entropyInput));
                var entropy = Convert.ToHexString(hashBytes);
                
                await Task.CompletedTask; // Keep async for consistency
                return entropy;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error generating selection entropy: {ex.Message}", "CasterSelectionService.GenerateSelectionEntropy");
                // Fallback entropy
                return blockHeight.ToString();
            }
        }

        /// <summary>
        /// Selects validators deterministically using entropy and modular arithmetic.
        /// Ensures fair distribution and prevents manipulation while being reproducible.
        /// </summary>
        /// <param name="eligibleValidators">Pool of eligible validators</param>
        /// <param name="entropy">Deterministic entropy for selection</param>
        /// <param name="maxCasters">Number of casters to select</param>
        /// <returns>List of selected validators</returns>
        private static List<NetworkValidator> SelectValidatorsDeterministically(List<NetworkValidator> eligibleValidators, string entropy, int maxCasters)
        {
            try
            {
                if (!eligibleValidators.Any())
                    return new List<NetworkValidator>();

                // Sort validators by address for consistent ordering
                var sortedValidators = eligibleValidators.OrderBy(v => v.Address).ToList();
                var selectedValidators = new List<NetworkValidator>();
                var usedIndices = new HashSet<int>();
                
                // Select up to maxCasters validators
                var castersToSelect = Math.Min(maxCasters, sortedValidators.Count);
                
                for (int i = 0; i < castersToSelect; i++)
                {
                    // Generate unique seed for each selection
                    var selectionSeed = $"{entropy}:{i}";
                    var seedHash = SHA256.HashData(Encoding.UTF8.GetBytes(selectionSeed));
                    var seedInt = BitConverter.ToInt32(seedHash, 0);
                    
                    // Use modular arithmetic to select index
                    var availableValidators = sortedValidators.Where((v, idx) => !usedIndices.Contains(idx)).ToList();
                    if (!availableValidators.Any())
                        break;
                        
                    var selectedIndex = Math.Abs(seedInt) % availableValidators.Count;
                    var selectedValidator = availableValidators[selectedIndex];
                    
                    // Find original index and mark as used
                    var originalIndex = sortedValidators.IndexOf(selectedValidator);
                    usedIndices.Add(originalIndex);
                    selectedValidators.Add(selectedValidator);
                }

                return selectedValidators;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error in deterministic validator selection: {ex.Message}", "CasterSelectionService.SelectValidatorsDeterministically");
                return new List<NetworkValidator>();
            }
        }

        /// <summary>
        /// Gets eligible validators for caster selection.
        /// Filters by stake requirement, connectivity, and health status.
        /// </summary>
        /// <returns>List of eligible validators</returns>
        private static async Task<List<NetworkValidator>> GetEligibleValidators()
        {
            try
            {
                var allValidators = Globals.NetworkValidators.Values.ToList();
                var eligibleValidators = new List<NetworkValidator>();

                foreach (var validator in allValidators)
                {
                    // Check stake requirement
                    if (!await HasValidStake(validator.Address))
                        continue;

                    // Check health status (fail count)
                    if (validator.CheckFailCount >= 3)
                        continue;

                    // Passed all checks
                    eligibleValidators.Add(validator);
                }

                return eligibleValidators;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error getting eligible validators: {ex.Message}", "CasterSelectionService.GetEligibleValidators");
                return new List<NetworkValidator>();
            }
        }

        /// <summary>
        /// Checks if a validator has the required stake amount.
        /// </summary>
        /// <param name="address">Validator address to check</param>
        /// <returns>True if has required stake, false otherwise</returns>
        private static async Task<bool> HasValidStake(string address)
        {
            try
            {
                var account = StateData.GetSpecificAccountStateTrei(address);
                await Task.CompletedTask; // Keep async for consistency
                return account != null && account.Balance >= CasterConfiguration.GetRequiredStakeAmount();
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Gets the current rotation epoch for the given block height.
        /// Used to determine when caster rotations should occur.
        /// </summary>
        /// <param name="blockHeight">Block height to check</param>
        /// <returns>Current rotation epoch number</returns>
        public static long GetCurrentRotationEpoch(long blockHeight)
        {
            var rotationFrequency = CasterConfiguration.GetRotationFrequency();
            return blockHeight / rotationFrequency;
        }

        /// <summary>
        /// Checks if a caster rotation should occur at the given block height.
        /// </summary>
        /// <param name="blockHeight">Block height to check</param>
        /// <returns>True if rotation should occur, false otherwise</returns>
        public static bool ShouldRotateCasters(long blockHeight)
        {
            var rotationFrequency = CasterConfiguration.GetRotationFrequency();
            return blockHeight % rotationFrequency == 0;
        }

        /// <summary>
        /// Gets status information about the current caster selection.
        /// Useful for monitoring and debugging.
        /// </summary>
        /// <param name="blockHeight">Current block height</param>
        /// <returns>Caster selection status summary</returns>
        public static async Task<string> GetSelectionStatus(long blockHeight)
        {
            try
            {
                var phase = await BootstrapService.GetCurrentPhase(blockHeight);
                var rotationEpoch = GetCurrentRotationEpoch(blockHeight);
                var eligibleValidators = await GetEligibleValidators();
                var currentCasters = await GetCurrentCasters(blockHeight);
                var rotationFrequency = CasterConfiguration.GetRotationFrequency();
                var blocksUntilRotation = rotationFrequency - (blockHeight % rotationFrequency);

                return $"Caster Selection Status - Phase: {phase}, " +
                       $"Epoch: {rotationEpoch}, " +
                       $"Eligible Validators: {eligibleValidators.Count}, " +
                       $"Current Casters: {currentCasters.Count}, " +
                       $"Blocks Until Rotation: {blocksUntilRotation}";
            }
            catch (Exception ex)
            {
                return $"Caster Selection Status Error: {ex.Message}";
            }
        }

        /// <summary>
        /// Forces a refresh of the caster selection.
        /// Useful for testing and emergency situations.
        /// </summary>
        public static async Task RefreshCasterSelection()
        {
            try
            {
                var currentHeight = Globals.LastBlock.Height;
                var newCasters = await GetCurrentCasters(currentHeight);
                
                // Update global caster list
                Globals.BlockCasters.Clear();
                foreach (var caster in newCasters)
                {
                    Globals.BlockCasters.Add(caster);
                }

                // Update IsBlockCaster flag for current node
                Globals.IsBlockCaster = newCasters.Any(c => c.ValidatorAddress == Globals.ValidatorAddress);

                LogUtility.Log($"Caster selection refreshed: {newCasters.Count} casters, IsBlockCaster: {Globals.IsBlockCaster}", "CasterSelectionService.RefreshCasterSelection");
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error refreshing caster selection: {ex.Message}", "CasterSelectionService.RefreshCasterSelection");
            }
        }
    }
}
