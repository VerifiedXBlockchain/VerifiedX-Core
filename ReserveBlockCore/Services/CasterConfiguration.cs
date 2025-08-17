using ReserveBlockCore.Models;

namespace ReserveBlockCore.Services
{
    /// <summary>
    /// Provides network-specific configuration for the dynamic caster system.
    /// Handles differences between TestNet and MainNet caster parameters.
    /// </summary>
    public static class CasterConfiguration
    {
        /// <summary>
        /// Gets the maximum number of casters for the current network.
        /// TestNet: 5 casters, MainNet: 7 casters
        /// </summary>
        /// <returns>Maximum caster count for current network</returns>
        public static int GetMaxCasters()
        {
            return Globals.IsTestNet ? 5 : 7;
        }

        /// <summary>
        /// Gets the minimum number of casters needed for network operation.
        /// TestNet: 3 casters, MainNet: 5 casters (must maintain consensus threshold)
        /// </summary>
        /// <returns>Minimum caster count for current network</returns>
        public static int GetMinCasters()
        {
            return Globals.IsTestNet ? 3 : 5;
        }

        /// <summary>
        /// Gets the consensus threshold (minimum approvals needed) for the current network.
        /// TestNet: 3 of 5 (60%), MainNet: 5 of 7 (71%)
        /// </summary>
        /// <returns>Minimum approvals required for consensus</returns>
        public static int GetConsensusThreshold()
        {
            return Globals.IsTestNet ? 3 : 5;
        }

        /// <summary>
        /// Gets the minimum number of validators required to transition from bootstrap to normal mode.
        /// Must have at least enough validators to fill all caster slots.
        /// </summary>
        /// <returns>Minimum validators needed for algorithmic caster selection</returns>
        public static int GetMinimumValidatorsForTransition()
        {
            return GetMaxCasters(); // Need full caster set to exit bootstrap
        }

        /// <summary>
        /// Gets the required stake amount for validator participation.
        /// Currently fixed at 5,000 VFX for both networks.
        /// </summary>
        /// <returns>Required stake amount in VFX</returns>
        public static decimal GetRequiredStakeAmount()
        {
            return ValidatorService.ValidatorRequiredAmount();
        }

        /// <summary>
        /// Gets the rotation frequency in blocks (how often casters change).
        /// 7,200 blocks = 24 hours at 12-second block times.
        /// </summary>
        /// <returns>Number of blocks between caster rotations</returns>
        public static int GetRotationFrequency()
        {
            return 7200; // 24 hours * 60 minutes * 60 seconds / 12 seconds per block
        }

        /// <summary>
        /// Gets the bootstrap period length in blocks.
        /// Grace period after deployment before attempting algorithmic selection.
        /// </summary>
        /// <returns>Number of blocks in bootstrap phase</returns>
        public static int GetBootstrapPeriod()
        {
            return 100; // ~20 minutes to allow validators to come online
        }

        /// <summary>
        /// Validates if a caster count meets the minimum requirements for consensus.
        /// </summary>
        /// <param name="casterCount">Number of available casters</param>
        /// <returns>True if sufficient for consensus, false otherwise</returns>
        public static bool HasSufficientCastersForConsensus(int casterCount)
        {
            return casterCount >= GetConsensusThreshold();
        }

        /// <summary>
        /// Validates if validator count is sufficient to exit bootstrap mode.
        /// </summary>
        /// <param name="validatorCount">Number of available validators</param>
        /// <returns>True if can transition to normal mode, false to stay in bootstrap</returns>
        public static bool CanTransitionFromBootstrap(int validatorCount)
        {
            return validatorCount >= GetMinimumValidatorsForTransition();
        }

        /// <summary>
        /// Gets network-specific configuration summary for logging/debugging.
        /// </summary>
        /// <returns>Configuration summary string</returns>
        public static string GetConfigurationSummary()
        {
            var network = Globals.IsTestNet ? "TestNet" : "MainNet";
            return $"Network: {network}, MaxCasters: {GetMaxCasters()}, " +
                   $"ConsensusThreshold: {GetConsensusThreshold()}, " +
                   $"StakeRequired: {GetRequiredStakeAmount()} VFX, " +
                   $"RotationFrequency: {GetRotationFrequency()} blocks";
        }
    }
}
