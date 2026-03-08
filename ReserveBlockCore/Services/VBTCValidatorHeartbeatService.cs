using ReserveBlockCore.Bitcoin.Models;
using ReserveBlockCore.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace ReserveBlockCore.Services
{
    /// <summary>
    /// Background service that maintains vBTC V2 validator heartbeat status.
    /// Only runs on validator nodes to prevent network overload.
    /// Tracks consecutive failures and marks validators as inactive after
    /// MaxConsecutiveFailures missed heartbeats (~30 minutes at 10-min intervals).
    /// </summary>
    public class VBTCValidatorHeartbeatService
    {
        private static readonly HttpClient httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        // Tracks consecutive heartbeat failures per validator address
        private static readonly Dictionary<string, int> _failureCounts = new Dictionary<string, int>();

        // 3 consecutive failures = ~30 minutes offline before marking as inactive
        private const int MaxConsecutiveFailures = 3;

        /// <summary>
        /// Heartbeat loop - only runs on validator nodes.
        /// Pings each validator's ValAPI to update their LastHeartbeatBlock.
        /// Runs every 10 minutes. After MaxConsecutiveFailures failed pings,
        /// marks the validator as inactive in the local DB.
        /// </summary>
        public static async Task VBTCValidatorHeartbeatLoop()
        {
            // IMPORTANT: Only validators should run this loop
            // Non-validators fetch the list on-demand via ValAPI
            if (string.IsNullOrEmpty(Globals.ValidatorAddress))
            {
                LogUtility.Log("Not a validator - VBTCValidatorHeartbeatLoop will not run", 
                    "VBTCValidatorHeartbeatService.VBTCValidatorHeartbeatLoop()");
                return;
            }

            LogUtility.Log("Starting vBTC V2 validator heartbeat loop", 
                "VBTCValidatorHeartbeatService.VBTCValidatorHeartbeatLoop()");

            while (true)
            {
                try
                {
                    // Wait 10 minutes between heartbeat checks
                    await Task.Delay(TimeSpan.FromMinutes(10));

                    // Get all validators from local database
                    var validators = VBTCValidator.GetAllValidators();
                    if (validators == null || !validators.Any())
                    {
                        LogUtility.Log("No validators found in database", 
                            "VBTCValidatorHeartbeatService.VBTCValidatorHeartbeatLoop()");
                        continue;
                    }

                    LogUtility.Log($"Checking heartbeat for {validators.Count} validators", 
                        "VBTCValidatorHeartbeatService.VBTCValidatorHeartbeatLoop()");

                    foreach (var validator in validators)
                    {
                        // Clear stale failure counts for already-inactive validators
                        if (!validator.IsActive)
                        {
                            _failureCounts.Remove(validator.ValidatorAddress);
                            continue;
                        }

                        // Handle self - we know we're alive
                        if (validator.ValidatorAddress == Globals.ValidatorAddress)
                        {
                            validator.LastHeartbeatBlock = Globals.LastBlock.Height;
                            VBTCValidator.SaveValidator(validator);
                            _failureCounts.Remove(validator.ValidatorAddress); // Reset any stale count
                            continue;
                        }

                        try
                        {
                            // Ping validator's ValAPI
                            var url = $"http://{validator.IPAddress}:{Globals.ValAPIPort}/valapi/Validator/HeartBeat";
                            var response = await httpClient.GetAsync(url);

                            if (response.IsSuccessStatusCode)
                            {
                                // Validator is alive - update heartbeat and reset failure count
                                validator.LastHeartbeatBlock = Globals.LastBlock.Height;
                                VBTCValidator.SaveValidator(validator);
                                _failureCounts.Remove(validator.ValidatorAddress);

                                LogUtility.Log($"Heartbeat success: {validator.ValidatorAddress} ({validator.IPAddress})", 
                                    "VBTCValidatorHeartbeatService.VBTCValidatorHeartbeatLoop()");
                            }
                            else
                            {
                                LogUtility.Log($"Heartbeat failed (HTTP {response.StatusCode}): {validator.ValidatorAddress} ({validator.IPAddress})", 
                                    "VBTCValidatorHeartbeatService.VBTCValidatorHeartbeatLoop()");
                                RecordFailure(validator.ValidatorAddress);
                            }
                        }
                        catch (HttpRequestException ex)
                        {
                            LogUtility.Log($"Heartbeat failed (network error): {validator.ValidatorAddress} ({validator.IPAddress}) - {ex.Message}", 
                                "VBTCValidatorHeartbeatService.VBTCValidatorHeartbeatLoop()");
                            RecordFailure(validator.ValidatorAddress);
                        }
                        catch (TaskCanceledException)
                        {
                            LogUtility.Log($"Heartbeat timeout: {validator.ValidatorAddress} ({validator.IPAddress})", 
                                "VBTCValidatorHeartbeatService.VBTCValidatorHeartbeatLoop()");
                            RecordFailure(validator.ValidatorAddress);
                        }
                        catch (Exception ex)
                        {
                            ErrorLogUtility.LogError($"Error checking heartbeat for {validator.ValidatorAddress}: {ex}", 
                                "VBTCValidatorHeartbeatService.VBTCValidatorHeartbeatLoop()");
                            RecordFailure(validator.ValidatorAddress);
                        }
                    }

                    LogUtility.Log("Heartbeat check complete", 
                        "VBTCValidatorHeartbeatService.VBTCValidatorHeartbeatLoop()");
                }
                catch (Exception ex)
                {
                    ErrorLogUtility.LogError($"Error in heartbeat loop: {ex}", 
                        "VBTCValidatorHeartbeatService.VBTCValidatorHeartbeatLoop()");
                }
            }
        }

        /// <summary>
        /// Records a consecutive heartbeat failure for a validator.
        /// After MaxConsecutiveFailures, the validator is marked as inactive in the local DB.
        /// The failure count is reset once the validator comes back online or is marked inactive.
        /// </summary>
        private static void RecordFailure(string validatorAddress)
        {
            if (!_failureCounts.ContainsKey(validatorAddress))
                _failureCounts[validatorAddress] = 0;

            _failureCounts[validatorAddress]++;

            if (_failureCounts[validatorAddress] >= MaxConsecutiveFailures)
            {
                VBTCValidator.MarkInactive(validatorAddress);
                _failureCounts.Remove(validatorAddress);
                LogUtility.Log($"Validator {validatorAddress} marked as INACTIVE after {MaxConsecutiveFailures} consecutive heartbeat failures (~{MaxConsecutiveFailures * 10} minutes offline).",
                    "VBTCValidatorHeartbeatService.RecordFailure()");
            }
            else
            {
                LogUtility.Log($"Validator {validatorAddress} heartbeat failure #{_failureCounts[validatorAddress]}/{MaxConsecutiveFailures}. Will mark inactive after {MaxConsecutiveFailures} consecutive failures.",
                    "VBTCValidatorHeartbeatService.RecordFailure()");
            }
        }
    }
}
