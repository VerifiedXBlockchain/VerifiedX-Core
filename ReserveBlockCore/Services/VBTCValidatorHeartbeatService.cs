using ReserveBlockCore.Bitcoin.Models;
using ReserveBlockCore.Utilities;
using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace ReserveBlockCore.Services
{
    /// <summary>
    /// Background service that maintains vBTC V2 validator heartbeat status
    /// Only runs on validator nodes to prevent network overload
    /// </summary>
    public class VBTCValidatorHeartbeatService
    {
        private static readonly HttpClient httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        /// <summary>
        /// Heartbeat loop - only runs on validator nodes
        /// Pings each validator's ValAPI to update their LastHeartbeatBlock
        /// Runs every 10 minutes
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
                        // Skip inactive validators
                        if (!validator.IsActive)
                            continue;

                        // Skip self (we know we're active)
                        if (validator.ValidatorAddress == Globals.ValidatorAddress)
                        {
                            // Update our own heartbeat
                            validator.LastHeartbeatBlock = Globals.LastBlock.Height;
                            VBTCValidator.SaveValidator(validator);
                            continue;
                        }

                        try
                        {
                            // Ping validator's ValAPI
                            var url = $"http://{validator.IPAddress}:{Globals.ValAPIPort}/validatorapi/ValidatorController/Ping";
                            var response = await httpClient.GetAsync(url);

                            if (response.IsSuccessStatusCode)
                            {
                                // Validator is alive - update heartbeat
                                validator.LastHeartbeatBlock = Globals.LastBlock.Height;
                                VBTCValidator.SaveValidator(validator);

                                LogUtility.Log($"Heartbeat success: {validator.ValidatorAddress} ({validator.IPAddress})", 
                                    "VBTCValidatorHeartbeatService.VBTCValidatorHeartbeatLoop()");
                            }
                            else
                            {
                                LogUtility.Log($"Heartbeat failed (HTTP {response.StatusCode}): {validator.ValidatorAddress} ({validator.IPAddress})", 
                                    "VBTCValidatorHeartbeatService.VBTCValidatorHeartbeatLoop()");
                            }
                        }
                        catch (HttpRequestException ex)
                        {
                            LogUtility.Log($"Heartbeat failed (network error): {validator.ValidatorAddress} ({validator.IPAddress}) - {ex.Message}", 
                                "VBTCValidatorHeartbeatService.VBTCValidatorHeartbeatLoop()");
                        }
                        catch (TaskCanceledException)
                        {
                            LogUtility.Log($"Heartbeat timeout: {validator.ValidatorAddress} ({validator.IPAddress})", 
                                "VBTCValidatorHeartbeatService.VBTCValidatorHeartbeatLoop()");
                        }
                        catch (Exception ex)
                        {
                            ErrorLogUtility.LogError($"Error checking heartbeat for {validator.ValidatorAddress}: {ex}", 
                                "VBTCValidatorHeartbeatService.VBTCValidatorHeartbeatLoop()");
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
    }
}
