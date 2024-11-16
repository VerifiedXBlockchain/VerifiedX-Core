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

        public static async Task<bool> AddValidatorToPool(NetworkValidator validator)
        {
            try
            {
                var verifySig = SignatureService.VerifySignature(
                                validator.Address,
                                validator.SignatureMessage,
                                validator.Signature);

                if (!verifySig)
                    return false;

                if (Globals.NetworkValidators.TryGetValue(validator.Address, out var networkVal))
                {
                    if (networkVal != null)
                    {
                        Globals.NetworkValidators[networkVal.Address] = validator;
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
                return false;
            }
        }
    }
}
