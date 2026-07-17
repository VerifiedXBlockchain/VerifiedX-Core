using ReserveBlockCore.Data;
using ReserveBlockCore.Models;
using ReserveBlockCore.Models.SmartContracts;
using ReserveBlockCore.Services;
using ReserveBlockCore.Utilities;

namespace ReserveBlockCore.BrowserWalletServices
{
    public static class WalletVfxService
    {
        public static object GetAccounts()
        {
            var accounts = AccountData.GetAccounts()?.Query().Where(x => true).ToList();
            if (accounts == null || !accounts.Any())
                return Array.Empty<object>();

            return accounts.Select(a =>
            {
                var state = StateData.GetSpecificAccountStateTrei(a.Address);
                return new
                {
                    address = a.Address,
                    adnr = a.ADNR,
                    balance = state?.Balance ?? a.Balance,
                    lockedBalance = state?.LockedBalance ?? a.LockedBalance,
                    nonce = state?.Nonce ?? 0,
                    isValidating = a.IsValidating,
                    tokens = state?.TokenAccounts?.Select(t => new
                    {
                        scUID = t.SmartContractUID,
                        name = t.TokenName,
                        ticker = t.TokenTicker,
                        balance = t.Balance,
                        lockedBalance = t.LockedBalance,
                        decimals = t.DecimalPlaces
                    }) ?? Enumerable.Empty<object>()
                };
            }).ToList();
        }

        public static object GetTransactions(string address)
        {
            var txs = TransactionData.GetAll().Query()
                .Where(t => t.FromAddress == address || t.ToAddress == address)
                .OrderByDescending(t => t.Height)
                .Limit(50)
                .ToList();

            return txs;
        }

        public static object GetNFTs(string address)
        {
            var scStates = SmartContractStateTrei.GetSmartContractsOwnedByAddress(address)?.ToList();
            if (scStates == null || !scStates.Any())
                return Array.Empty<object>();

            return scStates.Select(sc =>
            {
                var main = SmartContractMain.SmartContractData.GetSmartContract(sc.SmartContractUID);
                return new
                {
                    scUID = sc.SmartContractUID,
                    ownerAddress = sc.OwnerAddress,
                    name = main?.Name ?? "Unknown",
                    description = main?.Description ?? "",
                    minterName = main?.MinterName ?? "",
                    minterAddress = main?.MinterAddress ?? "",
                    isPublished = main?.IsPublished ?? false,
                    isToken = main?.IsToken ?? false,
                    nonce = sc.Nonce
                };
            }).ToList();
        }

        public static async Task<(bool success, string message)> SendVFX(string from, string to, decimal amount)
        {
            var addrValid = AddressValidateUtility.ValidateAddress(to);
            if (!addrValid)
                return (false, "Invalid destination address.");

            if (Globals.IsWalletEncrypted && Globals.EncryptPassword.Length == 0)
                return (false, "Wallet is encrypted. Please decrypt first.");

            var result = await WalletService.SendTXOut(from, to, amount);

            var success = result.Contains("Success") || result.Contains("Hash") ||
                (!result.Contains("Fail") && !result.Contains("fail") && !result.Contains("Error"));
            return (success, result);
        }
    }
}