using LiteDB;
using ReserveBlockCore.Data;
using ReserveBlockCore.Extensions;
using ReserveBlockCore.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ReserveBlockCore.Bitcoin.Models
{
    public class VBTCValidator
    {
        #region Variables
        public long Id { get; set; }
        public string ValidatorAddress { get; set; }      // VFX address
        public string IPAddress { get; set; }
        public string FrostPublicKey { get; set; }        // Validator's FROST public key (public, shareable)
        public long RegistrationBlockHeight { get; set; }
        public long LastHeartbeatBlock { get; set; }
        public bool IsActive { get; set; }
        public string RegistrationSignature { get; set; } // Proof of address ownership
        public string RegisterTransactionHash { get; set; }  // TX hash of join transaction
        public string ExitTransactionHash { get; set; }      // TX hash of exit transaction (if exited)
        public long? ExitBlockHeight { get; set; }           // Block height when exited (if exited)
        #endregion

        #region Database Methods
        public static ILiteCollection<VBTCValidator> GetDb()
        {
            var db = DbContext.DB_vBTC.GetCollection<VBTCValidator>(DbContext.RSRV_VBTC_V2_VALIDATORS);
            return db;
        }

        public static VBTCValidator? GetValidator(string validatorAddress)
        {
            var validators = GetDb();
            if (validators != null)
            {
                var validator = validators.FindOne(x => x.ValidatorAddress == validatorAddress);
                if (validator != null)
                {
                    return validator;
                }
            }

            return null;
        }

        public static List<VBTCValidator>? GetAllValidators()
        {
            var validators = GetDb();
            if (validators != null)
            {
                var validatorList = validators.FindAll().ToList();
                if (validatorList.Any())
                {
                    return validatorList;
                }
            }

            return null;
        }

        public static List<VBTCValidator>? GetActiveValidators()
        {
            var validators = GetDb();
            if (validators != null)
            {
                var validatorList = validators.Find(x => x.IsActive).ToList();
                if (validatorList.Any())
                {
                    return validatorList;
                }
            }

            return null;
        }

        public static List<VBTCValidator>? GetActiveValidatorsSinceBlock(long blockHeight)
        {
            var validators = GetDb();
            if (validators != null)
            {
                var validatorList = validators.Find(x => x.IsActive && x.LastHeartbeatBlock >= blockHeight).ToList();
                if (validatorList.Any())
                {
                    return validatorList;
                }
            }

            return null;
        }

        public static void SaveValidator(VBTCValidator validator)
        {
            try
            {
                var validators = GetDb();
                var existing = validators.FindOne(x => x.ValidatorAddress == validator.ValidatorAddress);

                if (existing == null)
                {
                    validators.InsertSafe(validator);
                }
                else
                {
                    validators.UpdateSafe(validator);
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError(ex.ToString(), "VBTCValidator.SaveValidator()");
            }
        }

        public static void UpdateHeartbeat(string validatorAddress, long blockHeight)
        {
            try
            {
                var validators = GetDb();
                var validator = validators.FindOne(x => x.ValidatorAddress == validatorAddress);

                if (validator != null)
                {
                    validator.LastHeartbeatBlock = blockHeight;
                    validator.IsActive = true;
                    validators.UpdateSafe(validator);
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError(ex.ToString(), "VBTCValidator.UpdateHeartbeat()");
            }
        }

        public static void MarkInactive(string validatorAddress)
        {
            try
            {
                var validators = GetDb();
                var validator = validators.FindOne(x => x.ValidatorAddress == validatorAddress);

                if (validator != null)
                {
                    validator.IsActive = false;
                    validators.UpdateSafe(validator);
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError(ex.ToString(), "VBTCValidator.MarkInactive()");
            }
        }

        public static void SetInactive(string validatorAddress, string exitTxHash, long exitBlockHeight)
        {
            try
            {
                var validators = GetDb();
                var validator = validators.FindOne(x => x.ValidatorAddress == validatorAddress);

                if (validator != null)
                {
                    validator.IsActive = false;
                    validator.ExitTransactionHash = exitTxHash;
                    validator.ExitBlockHeight = exitBlockHeight;
                    validators.UpdateSafe(validator);
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError(ex.ToString(), "VBTCValidator.SetInactive()");
            }
        }

        public static int GetActiveValidatorCount()
        {
            var validators = GetDb();
            if (validators != null)
            {
                return validators.Count(x => x.IsActive);
            }

            return 0;
        }

        public static void DeleteValidator(string validatorAddress)
        {
            try
            {
                var validators = GetDb();
                validators.DeleteManySafe(x => x.ValidatorAddress == validatorAddress);
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError(ex.ToString(), "VBTCValidator.DeleteValidator()");
            }
        }

        /// <summary>
        /// Fetches the active validator list from the network (for non-validators)
        /// Loops through known validators in local DB until one responds
        /// Updates local DB with fresh data from network
        /// </summary>
        public static async Task<List<VBTCValidator>?> FetchActiveValidatorsFromNetwork()
        {
            var httpClient = new System.Net.Http.HttpClient
            {
                Timeout = TimeSpan.FromSeconds(5)
            };

            try
            {
                var currentBlock = Globals.LastBlock.Height;
                
                // Get last known active validators from local DB
                var knownValidators = GetActiveValidators();
                
                if (knownValidators == null || !knownValidators.Any())
                {
                    LogUtility.Log("No known validators in local DB to query", 
                        "VBTCValidator.FetchActiveValidatorsFromNetwork()");
                    return null;
                }

                // Loop through known validators until one responds
                foreach (var validator in knownValidators)
                {
                    try
                    {
                        var url = $"http://{validator.IPAddress}:{Globals.ValAPIPort}/valapi/ValidatorController/GetActiveValidators";
                        
                        LogUtility.Log($"Fetching validator list from {validator.ValidatorAddress} ({validator.IPAddress})", 
                            "VBTCValidator.FetchActiveValidatorsFromNetwork()");
                        
                        var response = await httpClient.GetAsync(url);
                        
                        if (response.IsSuccessStatusCode)
                        {
                            var json = await response.Content.ReadAsStringAsync();
                            var result = Newtonsoft.Json.JsonConvert.DeserializeObject<dynamic>(json);
                            
                            if (result?.Success == true)
                            {
                                var validatorsList = result.ActiveValidators.ToObject<List<VBTCValidator>>();
                                
                                if (validatorsList != null && validatorsList.Any())
                                {
                                    // Update local DB cache with fresh data
                                    foreach (var fetchedValidator in validatorsList)
                                    {
                                        SaveValidator(fetchedValidator);
                                    }
                                    
                                    LogUtility.Log($"Successfully fetched {validatorsList.Count} validators from {validator.ValidatorAddress}", 
                                        "VBTCValidator.FetchActiveValidatorsFromNetwork()");
                                    
                                    return validatorsList;
                                }
                            }
                        }
                        else
                        {
                            LogUtility.Log($"Failed to fetch from {validator.ValidatorAddress} - HTTP {response.StatusCode}", 
                                "VBTCValidator.FetchActiveValidatorsFromNetwork()");
                        }
                    }
                    catch (System.Net.Http.HttpRequestException ex)
                    {
                        LogUtility.Log($"Network error fetching from {validator.ValidatorAddress}: {ex.Message}", 
                            "VBTCValidator.FetchActiveValidatorsFromNetwork()");
                    }
                    catch (TaskCanceledException)
                    {
                        LogUtility.Log($"Timeout fetching from {validator.ValidatorAddress}", 
                            "VBTCValidator.FetchActiveValidatorsFromNetwork()");
                    }
                    catch (Exception ex)
                    {
                        ErrorLogUtility.LogError($"Error fetching from {validator.ValidatorAddress}: {ex}", 
                            "VBTCValidator.FetchActiveValidatorsFromNetwork()");
                    }
                }
                
                // If we get here, none of the validators responded - fall back to local DB
                LogUtility.Log("No validators responded - falling back to local DB (may be stale)", 
                    "VBTCValidator.FetchActiveValidatorsFromNetwork()");
                return knownValidators;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error in FetchActiveValidatorsFromNetwork: {ex}", 
                    "VBTCValidator.FetchActiveValidatorsFromNetwork()");
                return null;
            }
            finally
            {
                httpClient?.Dispose();
            }
        }
        #endregion
    }
}
