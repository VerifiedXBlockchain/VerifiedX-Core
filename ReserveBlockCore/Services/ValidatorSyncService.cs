using Microsoft.AspNetCore.SignalR.Client;
using Newtonsoft.Json;
using ReserveBlockCore.Models;
using ReserveBlockCore.P2P;
using ReserveBlockCore.Utilities;
using System.Collections.Concurrent;

namespace ReserveBlockCore.Services
{
    /// <summary>
    /// Ensures all casters and validators maintain synchronized, comprehensive validator lists.
    /// Critical for effective caster selection and network health monitoring.
    /// </summary>
    public static class ValidatorSyncService
    {
        private static readonly SemaphoreSlim SyncLock = new(1, 1);
        private static DateTime LastFullSync = DateTime.MinValue;
        private static readonly TimeSpan FullSyncInterval = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan IncrementalSyncInterval = TimeSpan.FromMinutes(2);

        /// <summary>
        /// Starts the validator synchronization service as a background task.
        /// Ensures all casters have comprehensive validator awareness.
        /// </summary>
        public static async Task StartValidatorSyncService()
        {
            _ = Task.Run(ValidatorSyncMonitor);
            LogUtility.Log("Validator synchronization service started", "ValidatorSyncService.StartValidatorSyncService");
        }

        /// <summary>
        /// Continuous monitoring service that ensures validator lists stay synchronized.
        /// </summary>
        private static async Task ValidatorSyncMonitor()
        {
            while (!Globals.StopAllTimers)
            {
                try
                {
                    await SyncLock.WaitAsync();

                    // Only sync if we're a validator or caster
                    if (string.IsNullOrEmpty(Globals.ValidatorAddress))
                    {
                        await Task.Delay(30000); // Check again in 30 seconds
                        continue;
                    }

                    var now = DateTime.UtcNow;
                    var needsFullSync = (now - LastFullSync) > FullSyncInterval;

                    if (needsFullSync)
                    {
                        await PerformFullValidatorSync();
                        LastFullSync = now;
                    }
                    else
                    {
                        await PerformIncrementalSync();
                    }

                    // If we're a caster, broadcast our validator list to others
                    if (Globals.IsBlockCaster)
                    {
                        await BroadcastValidatorListToCasters();
                    }
                }
                catch (Exception ex)
                {
                    ErrorLogUtility.LogError($"Validator sync monitor error: {ex.Message}", "ValidatorSyncService.ValidatorSyncMonitor");
                }
                finally
                {
                    SyncLock.Release();
                }

                // Wait before next sync cycle
                await Task.Delay(IncrementalSyncInterval);
            }
        }

        /// <summary>
        /// Performs a comprehensive sync of all validator information from the network.
        /// </summary>
        private static async Task PerformFullValidatorSync()
        {
            try
            {
                LogUtility.Log("Performing full validator synchronization", "ValidatorSyncService.PerformFullValidatorSync");

                // Request validator lists from all connected casters
                await RequestValidatorListsFromCasters();

                // Request validator lists from connected validators
                await RequestValidatorListsFromValidators();

                // Verify and update our local validator list
                await VerifyAndUpdateValidatorList();

                LogUtility.Log($"Full validator sync complete - {Globals.NetworkValidators.Count} validators in pool", "ValidatorSyncService.PerformFullValidatorSync");
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Full validator sync failed: {ex.Message}", "ValidatorSyncService.PerformFullValidatorSync");
            }
        }

        /// <summary>
        /// Performs incremental sync to catch any recent validator changes.
        /// </summary>
        private static async Task PerformIncrementalSync()
        {
            try
            {
                // Request active validators from a few random peers
                await P2PValidatorClient.RequestActiveValidators();

                // Clean up stale validator entries
                await CleanupStaleValidators();
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Incremental validator sync failed: {ex.Message}", "ValidatorSyncService.PerformIncrementalSync");
            }
        }

        /// <summary>
        /// Requests validator lists from all connected casters.
        /// </summary>
        private static async Task RequestValidatorListsFromCasters()
        {
            try
            {
                var casters = Globals.BlockCasters.ToList();
                var tasks = new List<Task>();

                foreach (var caster in casters)
                {
                    if (caster.ValidatorAddress == Globals.ValidatorAddress)
                        continue; // Skip self

                    tasks.Add(RequestValidatorListFromPeer(caster.PeerIP, Globals.ValPort));
                }

                if (tasks.Any())
                {
                    await Task.WhenAll(tasks);
                    LogUtility.Log($"Requested validator lists from {tasks.Count} casters", "ValidatorSyncService.RequestValidatorListsFromCasters");
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error requesting validator lists from casters: {ex.Message}", "ValidatorSyncService.RequestValidatorListsFromCasters");
            }
        }

        /// <summary>
        /// Requests validator lists from connected validators.
        /// </summary>
        private static async Task RequestValidatorListsFromValidators()
        {
            try
            {
                var validators = Globals.ValidatorNodes.Values.Where(v => v.IsConnected).Take(5).ToList();
                var tasks = new List<Task>();

                foreach (var validator in validators)
                {
                    tasks.Add(RequestValidatorListFromPeer(validator.NodeIP, Globals.ValPort));
                }

                if (tasks.Any())
                {
                    await Task.WhenAll(tasks);
                    LogUtility.Log($"Requested validator lists from {tasks.Count} validators", "ValidatorSyncService.RequestValidatorListsFromValidators");
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error requesting validator lists from validators: {ex.Message}", "ValidatorSyncService.RequestValidatorListsFromValidators");
            }
        }

        /// <summary>
        /// Requests a validator list from a specific peer.
        /// </summary>
        private static async Task RequestValidatorListFromPeer(string ipAddress, int port)
        {
            try
            {
                using var client = Globals.HttpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(10);

                var uri = $"http://{ipAddress.Replace("::ffff:", "")}:{Globals.ValAPIPort}/valapi/validator/ActiveVals";
                var response = await client.GetAsync(uri);

                if (response.IsSuccessStatusCode)
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    if (!string.IsNullOrEmpty(responseBody) && responseBody != "0")
                    {
                        try
                        {
                            // Decompress and decode the response
                            var decompressed = responseBody.ToDecompress().ToStringFromBase64();
                            var validators = JsonConvert.DeserializeObject<List<NetworkValidator>>(decompressed);

                            if (validators?.Any() == true)
                            {
                                await ProcessReceivedValidatorList(validators, ipAddress);
                            }
                        }
                        catch (Exception ex)
                        {
                            // Try direct JSON parsing if compression fails
                            var validators = JsonConvert.DeserializeObject<List<NetworkValidator>>(responseBody);
                            if (validators?.Any() == true)
                            {
                                await ProcessReceivedValidatorList(validators, ipAddress);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // This is expected for many peers, so don't log errors unless debugging
                // ErrorLogUtility.LogError($"Failed to get validator list from {ipAddress}: {ex.Message}", "ValidatorSyncService.RequestValidatorListFromPeer");
            }
        }

        /// <summary>
        /// Processes a received validator list and updates our local cache.
        /// </summary>
        private static async Task ProcessReceivedValidatorList(List<NetworkValidator> validators, string sourceIP)
        {
            try
            {
                var addedCount = 0;
                var updatedCount = 0;

                foreach (var validator in validators)
                {
                    // Validate the validator before adding
                    if (await ValidatorConnectivityService.ValidateValidatorForAdmission(validator))
                    {
                        if (Globals.NetworkValidators.TryGetValue(validator.Address, out var existingValidator))
                        {
                            // Update existing validator if this info is newer
                            if (string.IsNullOrEmpty(existingValidator.IPAddress) && !string.IsNullOrEmpty(validator.IPAddress))
                            {
                                Globals.NetworkValidators[validator.Address] = validator;
                                updatedCount++;
                            }
                        }
                        else
                        {
                            // Add new validator
                            Globals.NetworkValidators.TryAdd(validator.Address, validator);
                            addedCount++;
                        }
                    }
                }

                if (addedCount > 0 || updatedCount > 0)
                {
                    LogUtility.Log($"Processed validator list from {sourceIP}: {addedCount} added, {updatedCount} updated", "ValidatorSyncService.ProcessReceivedValidatorList");
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error processing validator list from {sourceIP}: {ex.Message}", "ValidatorSyncService.ProcessReceivedValidatorList");
            }
        }

        /// <summary>
        /// Verifies and updates our local validator list integrity.
        /// </summary>
        private static async Task VerifyAndUpdateValidatorList()
        {
            try
            {
                var validators = Globals.NetworkValidators.Values.ToList();
                var removedCount = 0;

                foreach (var validator in validators)
                {
                    // Check if validator still meets requirements
                    if (!await ValidatorConnectivityService.ValidateValidatorForAdmission(validator))
                    {
                        Globals.NetworkValidators.TryRemove(validator.Address, out _);
                        removedCount++;
                    }
                }

                if (removedCount > 0)
                {
                    LogUtility.Log($"Removed {removedCount} invalid validators from local cache", "ValidatorSyncService.VerifyAndUpdateValidatorList");
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error verifying validator list: {ex.Message}", "ValidatorSyncService.VerifyAndUpdateValidatorList");
            }
        }

        /// <summary>
        /// Broadcasts our validator list to other casters for synchronization.
        /// </summary>
        private static async Task BroadcastValidatorListToCasters()
        {
            try
            {
                var validators = Globals.NetworkValidators.Values.ToList();
                if (!validators.Any())
                    return;

                var validatorJson = JsonConvert.SerializeObject(validators);
                var casters = Globals.BlockCasters.Where(c => c.ValidatorAddress != Globals.ValidatorAddress).ToList();

                var tasks = new List<Task>();
                foreach (var caster in casters)
                {
                    tasks.Add(BroadcastToCaster(caster, validatorJson));
                }

                if (tasks.Any())
                {
                    await Task.WhenAll(tasks);
                    LogUtility.Log($"Broadcasted validator list ({validators.Count} validators) to {tasks.Count} casters", "ValidatorSyncService.BroadcastValidatorListToCasters");
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error broadcasting validator list: {ex.Message}", "ValidatorSyncService.BroadcastValidatorListToCasters");
            }
        }

        /// <summary>
        /// Broadcasts validator list to a specific caster.
        /// </summary>
        private static async Task BroadcastToCaster(Peers caster, string validatorJson)
        {
            try
            {
                // Try to find connected validator node
                if (Globals.ValidatorNodes.TryGetValue(caster.PeerIP, out var validatorNode) && validatorNode.IsConnected)
                {
                    var source = new CancellationTokenSource(5000);
                    await validatorNode.Connection.InvokeCoreAsync("SendNetworkValidatorList", args: new object?[] { validatorJson }, source.Token);
                }
            }
            catch (Exception ex)
            {
                // Expected for disconnected casters
                // ErrorLogUtility.LogError($"Failed to broadcast to caster {caster.ValidatorAddress}: {ex.Message}", "ValidatorSyncService.BroadcastToCaster");
            }
        }

        /// <summary>
        /// Removes stale validators that haven't been seen recently.
        /// </summary>
        private static async Task CleanupStaleValidators()
        {
            try
            {
                var staleThreshold = DateTime.UtcNow.AddMinutes(-30);
                var validators = Globals.NetworkValidators.Values.ToList();
                var removedCount = 0;

                foreach (var validator in validators)
                {
                    // Check if validator has been failing connectivity for too long
                    if (Globals.FailedConnectivityValidators.TryGetValue(validator.Address, out var failTime))
                    {
                        if (failTime < staleThreshold)
                        {
                            Globals.NetworkValidators.TryRemove(validator.Address, out _);
                            Globals.FailedConnectivityValidators.TryRemove(validator.Address, out _);
                            removedCount++;
                        }
                    }
                }

                if (removedCount > 0)
                {
                    LogUtility.Log($"Cleaned up {removedCount} stale validators", "ValidatorSyncService.CleanupStaleValidators");
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error cleaning up stale validators: {ex.Message}", "ValidatorSyncService.CleanupStaleValidators");
            }
        }

        /// <summary>
        /// Gets the current validator synchronization status.
        /// </summary>
        public static string GetSyncStatus()
        {
            var totalValidators = Globals.NetworkValidators.Count;
            var failedValidators = Globals.FailedConnectivityValidators.Count;
            var connectedCasters = Globals.BlockCasters.Count;
            var timeSinceLastSync = DateTime.UtcNow - LastFullSync;

            return $"Validator Sync Status - Total: {totalValidators}, " +
                   $"Failed: {failedValidators}, " +
                   $"Connected Casters: {connectedCasters}, " +
                   $"Last Full Sync: {timeSinceLastSync.TotalMinutes:F1}m ago";
        }

        /// <summary>
        /// Forces an immediate full synchronization.
        /// Useful for testing or when caster selection seems outdated.
        /// </summary>
        public static async Task ForceFullSync()
        {
            try
            {
                LogUtility.Log("Forcing full validator synchronization", "ValidatorSyncService.ForceFullSync");
                await PerformFullValidatorSync();
                LastFullSync = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Forced sync failed: {ex.Message}", "ValidatorSyncService.ForceFullSync");
            }
        }
    }
}
