using System;

namespace ReserveBlockCore.Bitcoin.Services
{
    /// <summary>
    /// Calculates dynamic threshold adjustments for vBTC V2 based on validator availability
    /// Implements 24-hour safety gate with proportional threshold reduction
    /// </summary>
    public static class VBTCThresholdCalculator
    {
        // Constants
        private const int ORIGINAL_THRESHOLD = 51;
        private const int SAFETY_BUFFER_PERCENT = 10;
        private const int SAFETY_GATE_HOURS = 24;
        private const int SECONDS_PER_BLOCK = 12; // VFX: ~12 seconds per block
        private const int BLOCKS_PER_HOUR = 300; // 60 seconds/min × 60 min/hour / 12 sec/block = 300 blocks/hour
        private const int BLOCKS_PER_DAY = 7200; // 300 blocks/hour × 24 hours = 7,200 blocks/day
        private const int MINIMUM_VALIDATORS_ABSOLUTE = 3;
        private const int MINIMUM_REQUIRED_OF_THREE = 2; // If only 3 validators, require 2

        /// <summary>
        /// Calculate adjusted threshold based on validator availability and inactivity period
        /// </summary>
        /// <param name="totalRegisteredValidators">Total validators registered at DKG time</param>
        /// <param name="activeValidators">Currently active validators</param>
        /// <param name="lastActivityBlock">Block height of last successful activity</param>
        /// <param name="currentBlock">Current block height</param>
        /// <returns>Adjusted threshold percentage</returns>
        public static int CalculateAdjustedThreshold(
            int totalRegisteredValidators,
            int activeValidators,
            long lastActivityBlock,
            long currentBlock)
        {
            // Validate inputs
            if (totalRegisteredValidators <= 0)
                throw new ArgumentException("Total registered validators must be positive", nameof(totalRegisteredValidators));
            
            if (activeValidators < 0)
                throw new ArgumentException("Active validators cannot be negative", nameof(activeValidators));

            if (currentBlock < lastActivityBlock)
                throw new ArgumentException("Current block cannot be before last activity block");

            // If no validators active, return original threshold (will fail withdrawal, as expected)
            if (activeValidators == 0)
                return ORIGINAL_THRESHOLD;

            // Calculate time since last activity
            long blocksSinceActivity = currentBlock - lastActivityBlock;
            decimal hoursSinceActivity = (decimal)blocksSinceActivity / BLOCKS_PER_HOUR; // 12 sec/block = 300 blocks/hour

            // SAFETY GATE: Must wait 24 hours before adjusting threshold
            // This prevents instant exploitation during temporary network issues
            if (hoursSinceActivity < SAFETY_GATE_HOURS)
                return ORIGINAL_THRESHOLD;

            // Calculate available percentage
            decimal availablePercentage = ((decimal)activeValidators / totalRegisteredValidators) * 100m;

            // Add safety buffer (10%)
            decimal adjustedThreshold = Math.Min(ORIGINAL_THRESHOLD, availablePercentage + SAFETY_BUFFER_PERCENT);

            // SPECIAL CASE: If down to 3 validators, require 2 out of 3 (66.67%)
            if (activeValidators == MINIMUM_VALIDATORS_ABSOLUTE)
            {
                // Calculate percentage needed for 2 out of 3
                decimal twoOutOfThree = ((decimal)MINIMUM_REQUIRED_OF_THREE / MINIMUM_VALIDATORS_ABSOLUTE) * 100m;
                adjustedThreshold = Math.Max(adjustedThreshold, twoOutOfThree);
            }

            // SPECIAL CASE: Ensure at least 1 validator can sign if any are active
            // This handles edge cases where adjusted threshold rounds to 0
            if (activeValidators > 0)
            {
                decimal minThresholdForOne = (100.0m / activeValidators) + 5m;
                
                // But respect the 2-of-3 rule
                if (activeValidators == MINIMUM_VALIDATORS_ABSOLUTE)
                {
                    decimal twoOutOfThree = ((decimal)MINIMUM_REQUIRED_OF_THREE / MINIMUM_VALIDATORS_ABSOLUTE) * 100m;
                    minThresholdForOne = Math.Max(minThresholdForOne, twoOutOfThree);
                }

                adjustedThreshold = Math.Max(adjustedThreshold, minThresholdForOne);
            }

            // Never exceed original threshold
            adjustedThreshold = Math.Min(adjustedThreshold, ORIGINAL_THRESHOLD);

            return (int)Math.Ceiling(adjustedThreshold);
        }

        /// <summary>
        /// Calculate required number of validators based on threshold and available count
        /// </summary>
        /// <param name="threshold">Threshold percentage</param>
        /// <param name="availableValidators">Number of available validators</param>
        /// <returns>Required validator count</returns>
        public static int CalculateRequiredValidators(int threshold, int availableValidators)
        {
            if (availableValidators == 0)
                return 0;

            // Special case: 2 of 3 rule
            if (availableValidators == MINIMUM_VALIDATORS_ABSOLUTE)
                return MINIMUM_REQUIRED_OF_THREE;

            int required = (int)Math.Ceiling(availableValidators * (threshold / 100.0));
            
            // Ensure at least 1 if any validators available
            return Math.Max(1, required);
        }

        /// <summary>
        /// Check if threshold adjustment is available (24-hour gate passed)
        /// </summary>
        public static bool IsAdjustmentAvailable(long lastActivityBlock, long currentBlock)
        {
            long blocksSinceActivity = currentBlock - lastActivityBlock;
            decimal hoursSinceActivity = (decimal)blocksSinceActivity / BLOCKS_PER_HOUR;
            return hoursSinceActivity >= SAFETY_GATE_HOURS;
        }

        /// <summary>
        /// Get hours since last activity
        /// </summary>
        public static decimal GetHoursSinceActivity(long lastActivityBlock, long currentBlock)
        {
            long blocksSinceActivity = currentBlock - lastActivityBlock;
            return (decimal)blocksSinceActivity / BLOCKS_PER_HOUR;
        }

        /// <summary>
        /// Get a human-readable explanation of the current threshold
        /// </summary>
        public static string GetThresholdExplanation(
            int totalRegistered,
            int activeNow,
            long lastActivityBlock,
            long currentBlock)
        {
            decimal hoursSince = GetHoursSinceActivity(lastActivityBlock, currentBlock);
            int adjustedThreshold = CalculateAdjustedThreshold(totalRegistered, activeNow, lastActivityBlock, currentBlock);
            int required = CalculateRequiredValidators(adjustedThreshold, activeNow);

            if (hoursSince < SAFETY_GATE_HOURS)
            {
                return $"Original threshold: {ORIGINAL_THRESHOLD}% (Safety gate active: {hoursSince:F1}/{SAFETY_GATE_HOURS} hours). " +
                       $"Total: {totalRegistered}, Active: {activeNow}, Required: {required}";
            }
            else
            {
                decimal availablePercent = ((decimal)activeNow / totalRegistered) * 100m;
                return $"Adjusted threshold: {adjustedThreshold}% (Available: {availablePercent:F1}% + {SAFETY_BUFFER_PERCENT}% buffer). " +
                       $"Total: {totalRegistered}, Active: {activeNow}, Required: {required} validators. " +
                       $"Hours inactive: {hoursSince:F1}";
            }
        }
    }
}
