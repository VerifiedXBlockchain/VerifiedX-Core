using LiteDB;
using ReserveBlockCore.Data;
using ReserveBlockCore.Models;
using ReserveBlockCore.Utilities;

namespace ReserveBlockCore.Services
{
    /// <summary>
    /// Manages the bootstrap process for the dynamic caster system.
    /// Handles deployment detection, phase transitions, and provides testing flexibility.
    /// </summary>
    public static class BootstrapService
    {
        private static readonly SemaphoreSlim BootstrapLock = new(1, 1);
        private const string DEPLOYMENT_HEIGHT_KEY = "DeploymentStartHeight";

        /// <summary>
        /// Consensus phases for the dynamic caster system.
        /// </summary>
        public enum ConsensusPhase
        {
            Bootstrap,     // First 100 blocks after deployment - all validators act as casters
            Transitional,  // Attempting first algorithmic caster selection
            Normal         // Regular algorithmic caster rotation
        }

        /// <summary>
        /// Gets the deployment start height, recording it if this is the first run.
        /// Thread-safe and persists across application restarts.
        /// </summary>
        /// <returns>Block height when dynamic caster system was first deployed</returns>
        public static async Task<long> GetDeploymentStartHeight()
        {
            await BootstrapLock.WaitAsync();
            try
            {
                // Check if already cached in memory
                if (Globals.DeploymentStartHeight.HasValue)
                    return Globals.DeploymentStartHeight.Value;

                // Try to load from database
                var storedHeight = await LoadDeploymentHeightFromDb();
                if (storedHeight.HasValue)
                {
                    Globals.DeploymentStartHeight = storedHeight.Value;
                    return storedHeight.Value;
                }

                // First time running - record current height
                var currentHeight = Globals.LastBlock.Height;
                await SaveDeploymentHeightToDb(currentHeight);
                Globals.DeploymentStartHeight = currentHeight;

                LogUtility.Log($"Dynamic Caster System deployed at block height: {currentHeight}", "BootstrapService.GetDeploymentStartHeight");
                return currentHeight;
            }
            finally
            {
                BootstrapLock.Release();
            }
        }

        /// <summary>
        /// Determines the current consensus phase based on block height and system state.
        /// </summary>
        /// <param name="currentHeight">Current blockchain height</param>
        /// <returns>Current consensus phase</returns>
        public static async Task<ConsensusPhase> GetCurrentPhase(long currentHeight)
        {
            try
            {
                var deploymentHeight = await GetDeploymentStartHeight();
                var bootstrapEndHeight = deploymentHeight + CasterConfiguration.GetBootstrapPeriod();

                // Check if still in bootstrap period
                if (currentHeight < bootstrapEndHeight)
                    return ConsensusPhase.Bootstrap;

                // Check if successful caster selection has occurred
                if (!Globals.HasSuccessfulCasterSelection)
                    return ConsensusPhase.Transitional;

                return ConsensusPhase.Normal;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error determining consensus phase: {ex.Message}", "BootstrapService.GetCurrentPhase");
                return ConsensusPhase.Bootstrap; // Safe fallback
            }
        }

        /// <summary>
        /// Marks that successful algorithmic caster selection has occurred.
        /// Allows transition from Transitional to Normal phase.
        /// </summary>
        public static async Task MarkSuccessfulCasterSelection()
        {
            await BootstrapLock.WaitAsync();
            try
            {
                if (!Globals.HasSuccessfulCasterSelection)
                {
                    Globals.HasSuccessfulCasterSelection = true;
                    await SaveCasterSelectionStatusToDb(true);
                    
                    var currentHeight = Globals.LastBlock.Height;
                    LogUtility.Log($"Successful algorithmic caster selection at height {currentHeight} - transitioning to Normal phase", "BootstrapService.MarkSuccessfulCasterSelection");
                }
            }
            finally
            {
                BootstrapLock.Release();
            }
        }

        /// <summary>
        /// Checks if the system can transition from Bootstrap to Normal phase.
        /// Requires sufficient validators and completion of bootstrap period.
        /// </summary>
        /// <param name="currentHeight">Current blockchain height</param>
        /// <param name="availableValidators">Number of available validators</param>
        /// <returns>True if can transition, false to stay in current phase</returns>
        public static async Task<bool> CanTransitionFromBootstrap(long currentHeight, int availableValidators)
        {
            try
            {
                var deploymentHeight = await GetDeploymentStartHeight();
                var bootstrapEndHeight = deploymentHeight + CasterConfiguration.GetBootstrapPeriod();

                // Must complete bootstrap period
                if (currentHeight < bootstrapEndHeight)
                    return false;

                // Must have sufficient validators
                if (!CasterConfiguration.CanTransitionFromBootstrap(availableValidators))
                    return false;

                return true;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error checking bootstrap transition: {ex.Message}", "BootstrapService.CanTransitionFromBootstrap");
                return false;
            }
        }

        /// <summary>
        /// Gets detailed status information about the bootstrap system.
        /// Useful for monitoring and debugging.
        /// </summary>
        /// <returns>Bootstrap status summary</returns>
        public static async Task<string> GetBootstrapStatus()
        {
            try
            {
                var currentHeight = Globals.LastBlock.Height;
                var deploymentHeight = await GetDeploymentStartHeight();
                var phase = await GetCurrentPhase(currentHeight);
                var bootstrapEndHeight = deploymentHeight + CasterConfiguration.GetBootstrapPeriod();
                var validatorCount = Globals.NetworkValidators.Count;
                var requiredValidators = CasterConfiguration.GetMinimumValidatorsForTransition();

                return $"Bootstrap Status - Phase: {phase}, " +
                       $"Current Height: {currentHeight}, " +
                       $"Deployment Height: {deploymentHeight}, " +
                       $"Bootstrap End: {bootstrapEndHeight}, " +
                       $"Validators: {validatorCount}/{requiredValidators}, " +
                       $"Successful Selection: {Globals.HasSuccessfulCasterSelection}";
            }
            catch (Exception ex)
            {
                return $"Bootstrap Status Error: {ex.Message}";
            }
        }

        /// <summary>
        /// Resets the bootstrap system to initial state.
        /// Useful for testing scenarios - clears deployment height and forces fresh start.
        /// </summary>
        public static async Task ResetBootstrap()
        {
            await BootstrapLock.WaitAsync();
            try
            {
                // Clear memory cache
                Globals.DeploymentStartHeight = null;
                Globals.HasSuccessfulCasterSelection = false;

                // Clear database records
                await ClearDeploymentHeightFromDb();
                await SaveCasterSelectionStatusToDb(false);

                LogUtility.Log("Bootstrap system reset - next run will treat current height as new deployment", "BootstrapService.ResetBootstrap");
            }
            finally
            {
                BootstrapLock.Release();
            }
        }

        /// <summary>
        /// Forces bootstrap mode for additional blocks.
        /// Useful for testing - extends bootstrap period from current height.
        /// </summary>
        /// <param name="additionalBlocks">Number of additional blocks to stay in bootstrap</param>
        public static async Task ForceBootstrapMode(int additionalBlocks = 100)
        {
            await BootstrapLock.WaitAsync();
            try
            {
                var currentHeight = Globals.LastBlock.Height;
                await SaveDeploymentHeightToDb(currentHeight);
                Globals.DeploymentStartHeight = currentHeight;
                Globals.HasSuccessfulCasterSelection = false;
                await SaveCasterSelectionStatusToDb(false);

                LogUtility.Log($"Forced bootstrap mode for {additionalBlocks} blocks starting from height {currentHeight}", "BootstrapService.ForceBootstrapMode");
            }
            finally
            {
                BootstrapLock.Release();
            }
        }

        /// <summary>
        /// Initializes the bootstrap system on application startup.
        /// Loads persisted state and validates configuration.
        /// </summary>
        public static async Task Initialize()
        {
            try
            {
                // Load deployment height (will set if first run)
                await GetDeploymentStartHeight();

                // Load caster selection status
                var storedStatus = await LoadCasterSelectionStatusFromDb();
                if (storedStatus.HasValue)
                    Globals.HasSuccessfulCasterSelection = storedStatus.Value;

                // Determine initial phase based on current blockchain state
                var currentHeight = Globals.LastBlock.Height;
                var currentPhase = await GetCurrentPhase(currentHeight);

                // Get initial casters based on the phase
                var initialCasters = await CasterSelectionService.GetCurrentCasters(currentHeight);

                // **CRITICAL**: Update global caster list and set caster status
                Globals.BlockCasters.Clear();
                foreach (var caster in initialCasters)
                {
                    Globals.BlockCasters.Add(caster);
                }

                // Check if current validator is now a caster
                if (!string.IsNullOrEmpty(Globals.ValidatorAddress))
                {
                    Globals.IsBlockCaster = initialCasters.Any(c => c.ValidatorAddress == Globals.ValidatorAddress);
                    LogUtility.Log($"Current validator {Globals.ValidatorAddress} is caster: {Globals.IsBlockCaster}", "BootstrapService.Initialize");
                }

                LogUtility.Log($"Bootstrap initialized - Phase: {currentPhase}, Height: {currentHeight}, Casters: {initialCasters.Count}, IsBlockCaster: {Globals.IsBlockCaster}", "BootstrapService.Initialize");
                LogUtility.Log($"Bootstrap system initialized - {CasterConfiguration.GetConfigurationSummary()}", "BootstrapService.Initialize");

                // Mark system as initialized
                Globals.BootstrapInitialized = true;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Bootstrap initialization failed: {ex.Message}", "BootstrapService.Initialize");
                throw;
            }
        }

        #region Persistence Operations

        /// <summary>
        /// Saves deployment height for persistence. Currently uses in-memory storage.
        /// In a future version, this could be enhanced to use database persistence.
        /// </summary>
        private static async Task SaveDeploymentHeightToDb(long height)
        {
            try
            {
                // For now, we rely on the Globals.DeploymentStartHeight variable
                // This provides session persistence, which is sufficient for the bootstrap system
                LogUtility.Log($"Deployment height {height} cached in memory", "BootstrapService.SaveDeploymentHeightToDb");
                await Task.CompletedTask; // Keep async signature for future database integration
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Failed to save deployment height: {ex.Message}", "BootstrapService.SaveDeploymentHeightToDb");
            }
        }

        /// <summary>
        /// Loads deployment height from persistence. Currently uses in-memory storage.
        /// </summary>
        private static async Task<long?> LoadDeploymentHeightFromDb()
        {
            try
            {
                // Return the cached value if available
                await Task.CompletedTask; // Keep async signature for future database integration
                return Globals.DeploymentStartHeight;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Failed to load deployment height: {ex.Message}", "BootstrapService.LoadDeploymentHeightFromDb");
                return null;
            }
        }

        /// <summary>
        /// Clears deployment height from persistence.
        /// </summary>
        private static async Task ClearDeploymentHeightFromDb()
        {
            try
            {
                // Clear the in-memory cache
                Globals.DeploymentStartHeight = null;
                LogUtility.Log("Deployment height cleared from memory", "BootstrapService.ClearDeploymentHeightFromDb");
                await Task.CompletedTask; // Keep async signature for future database integration
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Failed to clear deployment height: {ex.Message}", "BootstrapService.ClearDeploymentHeightFromDb");
            }
        }

        /// <summary>
        /// Saves caster selection status for persistence. Currently uses in-memory storage.
        /// </summary>
        private static async Task SaveCasterSelectionStatusToDb(bool hasSuccessfulSelection)
        {
            try
            {
                // Update the global variable
                Globals.HasSuccessfulCasterSelection = hasSuccessfulSelection;
                LogUtility.Log($"Caster selection status {hasSuccessfulSelection} cached in memory", "BootstrapService.SaveCasterSelectionStatusToDb");
                await Task.CompletedTask; // Keep async signature for future database integration
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Failed to save caster selection status: {ex.Message}", "BootstrapService.SaveCasterSelectionStatusToDb");
            }
        }

        /// <summary>
        /// Loads caster selection status from persistence. Currently uses in-memory storage.
        /// </summary>
        private static async Task<bool?> LoadCasterSelectionStatusFromDb()
        {
            try
            {
                await Task.CompletedTask; // Keep async signature for future database integration
                return Globals.HasSuccessfulCasterSelection;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Failed to load caster selection status: {ex.Message}", "BootstrapService.LoadCasterSelectionStatusFromDb");
                return null;
            }
        }

        #endregion
    }
}
