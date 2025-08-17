using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Newtonsoft.Json;
using ReserveBlockCore.Services;

namespace ReserveBlockCore.Models
{
    public class NetworkValidator
    {
        public string IPAddress { get; set; }
        public string Address { get; set; }
        public string UniqueName { get; set; }
        public string PublicKey { get; set; }
        public string Signature { get; set; }
        public string SignatureMessage { get; set; }
        public int CheckFailCount { get; set; }
        public long Latency { get; set; }

        public static async Task<bool> AddValidatorToPool(NetworkValidator validator)
        {
            try
            {
                // Enhanced validation using our new connectivity service
                var isValidForAdmission = await ValidatorConnectivityService.ValidateValidatorForAdmission(validator);
                
                if (!isValidForAdmission)
                {
                    // ValidatorConnectivityService already logs the specific reason
                    return false;
                }

                // If we reach here, the validator passed all checks (signature, stake, connectivity)
                if (Globals.NetworkValidators.TryGetValue(validator.Address, out var networkVal))
                {
                    if (networkVal != null)
                    {
                        validator.CheckFailCount = networkVal.CheckFailCount;
                        Globals.NetworkValidators[networkVal.Address] = validator;
                    }
                    else
                    {
                        Globals.NetworkValidators.TryAdd(validator.Address, validator);
                    }
                }
                else
                {
                    Globals.NetworkValidators.TryAdd(validator.Address, validator);
                }

                return true;
            }
            catch (Exception ex)
            {
                Utilities.ErrorLogUtility.LogError($"Failed to add validator {validator.Address} to pool: {ex.Message}", "NetworkValidator.AddValidatorToPool");
                return false;
            }
        }
    }
}
