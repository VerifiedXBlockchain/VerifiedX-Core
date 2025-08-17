using Microsoft.AspNetCore.SignalR.Client;
using Newtonsoft.Json;
using ReserveBlockCore.Models;
using ReserveBlockCore.P2P;
using ReserveBlockCore.Utilities;

namespace ReserveBlockCore.Services
{
    /// <summary>
    /// Handles caster rotation detection, selection, notification, and transition.
    /// Ensures smooth transitions between caster groups with proper validator updates.
    /// </summary>
    public static class CasterRotationService
    {
        private static readonly SemaphoreSlim RotationLock = new(1, 1);
        private static long LastProcessedRotationHeight = -1;

        /// <summary>
        /// Checks if a block height triggers a caster rotation and processes it.
        /// Called during block validation to ensure rotations happen at correct intervals.
        /// </summary>
        /// <param name="blockHeight">Block height to check for rotation</param>
        /// <returns>True if rotation was processed, false if no rotation needed</returns>
        public static async Task<bool> ProcessCasterRotation(long blockHeight)
        {
            // Only check if we're in a mode that supports rotation
            var phase = await BootstrapService.GetCurrentPhase(blockHeight);
            if (phase == BootstrapService.ConsensusPhase.Bootstrap)
                return false; // No rotations during bootstrap

            // Check if this height triggers a rotation
            if (!CasterSelectionService.ShouldRotateCasters(blockHeight))
                return false;

            // Prevent duplicate processing of same rotation
            if (LastProcessedRotationHeight == blockHeight)
                return false;

            await RotationLock.WaitAsync();
            try
            {
                // Double-check after acquiring lock
                if (LastProcessedRotationHeight == blockHeight)
                    return false;

                LogUtility.Log($"Processing caster rotation at block height {blockHeight}", "CasterRotationService.ProcessCasterRotation");

                // Only current casters should initiate the rotation
                if (!Globals.IsBlockCaster)
                {
                    LogUtility.Log("Not a caster - waiting for rotation notification", "CasterRotationService.ProcessCasterRotation");
                    return false;
                }

                // Perform caster selection
                var newCasters = await CasterSelectionService.GetCurrentCasters(blockHeight);
                var oldCasters = Globals.BlockCasters.ToList();

                // Fallback: if no new casters are available, current casters choose themselves again
                if (newCasters == null || newCasters.Count == 0)
                {
                    LogUtility.Log($"No new casters available at height {blockHeight} - current casters will continue", "CasterRotationService.ProcessCasterRotation");
                    newCasters = oldCasters; // Keep current casters
                }
                else if (newCasters.Count < CasterConfiguration.GetMinCasters())
                {
                    LogUtility.Log($"Insufficient new casters ({newCasters.Count}) at height {blockHeight} - mixing with current casters", "CasterRotationService.ProcessCasterRotation");
                    // Mix new casters with some current casters to maintain minimum
                    var minCasters = CasterConfiguration.GetMinCasters();
                    var castersToKeep = minCasters - newCasters.Count;
                    var keepFromCurrent = oldCasters.Take(castersToKeep).ToList();
                    newCasters.AddRange(keepFromCurrent);
                }

                // Update local caster list
                await UpdateLocalCasterList(newCasters);

                // Broadcast new caster list to all validators
                await BroadcastCasterRotation(newCasters, blockHeight);

                // Log the rotation
                var newCasterAddresses = string.Join(", ", newCasters.Select(c => c.ValidatorAddress));
                LogUtility.Log($"Caster rotation complete at height {blockHeight} - New casters: {newCasterAddresses}", "CasterRotationService.ProcessCasterRotation");

                LastProcessedRotationHeight = blockHeight;
                return true;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error processing caster rotation at height {blockHeight}: {ex.Message}", "CasterRotationService.ProcessCasterRotation");
                return false;
            }
            finally
            {
                RotationLock.Release();
            }
        }

        /// <summary>
        /// Processes a received caster rotation notification from the network.
        /// Updates local caster list and connection status based on network consensus.
        /// </summary>
        /// <param name="newCasters">List of new casters</param>
        /// <param name="rotationHeight">Block height of the rotation</param>
        /// <returns>True if rotation was applied, false otherwise</returns>
        public static async Task<bool> ProcessCasterRotationNotification(List<Peers> newCasters, long rotationHeight)
        {
            await RotationLock.WaitAsync();
            try
            {
                // Validate the rotation
                if (!await ValidateRotationNotification(newCasters, rotationHeight))
                {
                    LogUtility.Log($"Invalid caster rotation notification received for height {rotationHeight}", "CasterRotationService.ProcessCasterRotationNotification");
                    return false;
                }

                // Apply the rotation
                await UpdateLocalCasterList(newCasters);

                // Update connection status
                var wasICaster = Globals.IsBlockCaster;
                var amINowCaster = newCasters.Any(c => c.ValidatorAddress == Globals.ValidatorAddress);
                
                if (wasICaster && !amINowCaster)
                {
                    LogUtility.Log($"Caster rotation: I am no longer a caster as of height {rotationHeight}", "CasterRotationService.ProcessCasterRotationNotification");
                }
                else if (!wasICaster && amINowCaster)
                {
                    LogUtility.Log($"Caster rotation: I am now a caster as of height {rotationHeight}", "CasterRotationService.ProcessCasterRotationNotification");
                }

                var newCasterAddresses = string.Join(", ", newCasters.Select(c => c.ValidatorAddress));
                LogUtility.Log($"Applied caster rotation notification for height {rotationHeight} - New casters: {newCasterAddresses}", "CasterRotationService.ProcessCasterRotationNotification");

                LastProcessedRotationHeight = rotationHeight;
                return true;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error processing caster rotation notification: {ex.Message}", "CasterRotationService.ProcessCasterRotationNotification");
                return false;
            }
            finally
            {
                RotationLock.Release();
            }
        }

        /// <summary>
        /// Updates the local caster list and connection status.
        /// </summary>
        /// <param name="newCasters">New list of casters</param>
        private static async Task UpdateLocalCasterList(List<Peers> newCasters)
        {
            // Update global caster list
            Globals.BlockCasters.Clear();
            foreach (var caster in newCasters)
            {
                Globals.BlockCasters.Add(caster);
            }

            // Update IsBlockCaster status
            if (!string.IsNullOrEmpty(Globals.ValidatorAddress))
            {
                Globals.IsBlockCaster = newCasters.Any(c => c.ValidatorAddress == Globals.ValidatorAddress);
            }

            // Trigger validator connection updates if we're a validator
            if (!string.IsNullOrEmpty(Globals.ValidatorAddress))
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(2000); // Give rotation time to settle
                    await UpdateValidatorConnections(newCasters);
                });
            }
        }

        /// <summary>
        /// Updates validator connections to reflect new caster list.
        /// Disconnects from old casters and connects to new ones.
        /// </summary>
        /// <param name="newCasters">New list of casters to connect to</param>
        private static async Task UpdateValidatorConnections(List<Peers> newCasters)
        {
            try
            {
                LogUtility.Log("Updating validator connections for caster rotation", "CasterRotationService.UpdateValidatorConnections");

                // Clear existing caster connections
                foreach (var existingCaster in Globals.BlockCasterNodes.Values.ToList())
                {
                    try
                    {
                        await existingCaster.Connection.DisposeAsync();
                        Globals.BlockCasterNodes.TryRemove(existingCaster.NodeIP, out _);
                    }
                    catch (Exception ex)
                    {
                        ErrorLogUtility.LogError($"Error disconnecting from old caster {existingCaster.NodeIP}: {ex.Message}", "CasterRotationService.UpdateValidatorConnections");
                    }
                }

                // Connect to new casters
                await Task.Delay(1000); // Brief delay
                await P2PValidatorClient.ConnectToBlockcaster();

                LogUtility.Log($"Validator connections updated - now connecting to {newCasters.Count} new casters", "CasterRotationService.UpdateValidatorConnections");
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error updating validator connections: {ex.Message}", "CasterRotationService.UpdateValidatorConnections");
            }
        }

        /// <summary>
        /// Broadcasts caster rotation to all connected validators.
        /// Uses the ValidatorNode's HubContext to broadcast to all connected validators.
        /// </summary>
        /// <param name="newCasters">New list of casters</param>
        /// <param name="rotationHeight">Block height of the rotation</param>
        private static async Task BroadcastCasterRotation(List<Peers> newCasters, long rotationHeight)
        {
            try
            {
                var rotationData = new
                {
                    NewCasters = newCasters,
                    RotationHeight = rotationHeight,
                    Timestamp = TimeUtil.GetTime()
                };

                var rotationJson = JsonConvert.SerializeObject(rotationData);

                // Broadcast via ValidatorNode's SignalR Hub to all connected validators
                await ValidatorNode.Broadcast("CasterRotation", rotationJson, "ReceiveCasterRotation");

                LogUtility.Log($"Broadcasted caster rotation to all connected validators for height {rotationHeight}", "CasterRotationService.BroadcastCasterRotation");

                // Also broadcast via caster nodes if available
                if (Globals.BlockCasterNodes.Any())
                {
                    var casterTasks = new List<Task>();
                    foreach (var casterNode in Globals.BlockCasterNodes.Values)
                    {
                        if (casterNode.IsConnected)
                        {
                            casterTasks.Add(BroadcastToCaster(casterNode, rotationJson));
                        }
                    }

                    if (casterTasks.Any())
                    {
                        await Task.WhenAll(casterTasks);
                        LogUtility.Log($"Broadcasted caster rotation to {casterTasks.Count} casters", "CasterRotationService.BroadcastCasterRotation");
                    }
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error broadcasting caster rotation: {ex.Message}", "CasterRotationService.BroadcastCasterRotation");
            }
        }

        /// <summary>
        /// Broadcasts rotation notification to a specific caster connection.
        /// </summary>
        private static async Task BroadcastToCaster(NodeInfo casterNode, string rotationJson)
        {
            try
            {
                using var source = new CancellationTokenSource(5000);
                await casterNode.Connection.InvokeCoreAsync("ReceiveCasterRotation", args: new object?[] { rotationJson }, source.Token);
            }
            catch (Exception ex)
            {
                // Expected for some disconnected casters
                // ErrorLogUtility.LogError($"Failed to broadcast to caster: {ex.Message}", "CasterRotationService.BroadcastToCaster");
            }
        }

        /// <summary>
        /// Validates a received rotation notification for authenticity and correctness.
        /// </summary>
        /// <param name="newCasters">New caster list to validate</param>
        /// <param name="rotationHeight">Claimed rotation height</param>
        /// <returns>True if rotation is valid, false otherwise</returns>
        private static async Task<bool> ValidateRotationNotification(List<Peers> newCasters, long rotationHeight)
        {
            try
            {
                // Check if rotation height is valid
                if (!CasterSelectionService.ShouldRotateCasters(rotationHeight))
                    return false;

                // Check if we haven't already processed this rotation
                if (LastProcessedRotationHeight >= rotationHeight)
                    return false;

                // Validate caster count matches network configuration
                var expectedMaxCasters = CasterConfiguration.GetMaxCasters();
                if (newCasters.Count > expectedMaxCasters)
                    return false;

                // Validate casters meet requirements (connectivity, stake, etc.)
                foreach (var caster in newCasters)
                {
                    if (string.IsNullOrEmpty(caster.ValidatorAddress))
                        return false;

                    // Could add additional validation here (stake check, connectivity, etc.)
                }

                return true;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error validating rotation notification: {ex.Message}", "CasterRotationService.ValidateRotationNotification");
                return false;
            }
        }

        /// <summary>
        /// Forces a caster rotation for testing purposes.
        /// Should only be used in test environments.
        /// </summary>
        public static async Task ForceRotationForTesting()
        {
            try
            {
                var currentHeight = Globals.LastBlock.Height;
                LogUtility.Log($"Forcing caster rotation at height {currentHeight} (TESTING)", "CasterRotationService.ForceRotationForTesting");

                var newCasters = await CasterSelectionService.GetCurrentCasters(currentHeight);
                await UpdateLocalCasterList(newCasters);
                await BroadcastCasterRotation(newCasters, currentHeight);

                LastProcessedRotationHeight = currentHeight;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error forcing rotation: {ex.Message}", "CasterRotationService.ForceRotationForTesting");
            }
        }

        /// <summary>
        /// Gets the current rotation status for monitoring.
        /// </summary>
        /// <returns>Rotation status summary</returns>
        public static string GetRotationStatus()
        {
            var currentHeight = Globals.LastBlock.Height;
            var rotationEpoch = CasterSelectionService.GetCurrentRotationEpoch(currentHeight);
            var rotationFrequency = CasterConfiguration.GetRotationFrequency();
            var blocksUntilRotation = rotationFrequency - (currentHeight % rotationFrequency);
            var lastProcessed = LastProcessedRotationHeight;

            return $"Caster Rotation Status - Current Height: {currentHeight}, " +
                   $"Rotation Epoch: {rotationEpoch}, " +
                   $"Blocks Until Next: {blocksUntilRotation}, " +
                   $"Last Processed: {lastProcessed}, " +
                   $"Current Casters: {Globals.BlockCasters.Count}, " +
                   $"Is Caster: {Globals.IsBlockCaster}";
        }
    }
}
