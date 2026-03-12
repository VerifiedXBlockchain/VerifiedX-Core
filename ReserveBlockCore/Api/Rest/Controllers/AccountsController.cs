using Microsoft.AspNetCore.Mvc;
using ReserveBlockCore.Api.Rest.Infrastructure;
using ReserveBlockCore.Api.Rest.Models;
using ReserveBlockCore.Api.Rest.Models.Requests;
using ReserveBlockCore.Data;
using ReserveBlockCore.Models;
using ReserveBlockCore.Models.SmartContracts;
using ReserveBlockCore.Utilities;

namespace ReserveBlockCore.Api.Rest.Controllers
{
    public class AccountsController : RestBaseController
    {
        /// <summary>
        /// List all accounts with balances
        /// </summary>
        [HttpGet]
        public IActionResult GetAll([FromQuery] PaginationParams paging)
        {
            var accountsDb = AccountData.GetAccounts();
            if (accountsDb.Count() == 0)
                return OkPaged(Enumerable.Empty<object>(), paging.Page, paging.PageSize, 0);

            var allAccounts = accountsDb.Query().Where(x => true).ToList();
            var totalCount = allAccounts.Count;

            var paged = allAccounts
                .Skip((paging.Page - 1) * paging.PageSize)
                .Take(paging.PageSize)
                .Select(account =>
                {
                    var state = StateData.GetSpecificAccountStateTrei(account.Address);
                    return new
                    {
                        Address = account.Address,
                        Adnr = account.ADNR,
                        Balance = state?.Balance ?? account.Balance,
                        LockedBalance = state?.LockedBalance ?? account.LockedBalance,
                        Nonce = state?.Nonce ?? 0,
                        IsValidating = account.IsValidating
                    };
                });

            return OkPaged(paged, paging.Page, paging.PageSize, totalCount);
        }

        /// <summary>
        /// Create a new address
        /// </summary>
        [HttpPost]
        public IActionResult Create()
        {
            Account account;
            if (Globals.HDWallet == true)
            {
                account = HDWallet.HDWalletData.GenerateAddress();
            }
            else
            {
                account = AccountData.CreateNewAccount();
            }

            LogUtility.Log("New Address Created: " + account.Address, "AccountsController.Create()");

            return Created(new { Address = account.Address, PrivateKey = account.GetKey });
        }

        /// <summary>
        /// Get account details by address
        /// </summary>
        [HttpGet("{address}")]
        public IActionResult GetByAddress(string address)
        {
            var account = AccountData.GetSingleAccount(address);
            if (account == null)
                return Fail("NOT_FOUND", $"Account not found: {address}", 404);

            var state = StateData.GetSpecificAccountStateTrei(address);

            var result = new
            {
                Address = account.Address,
                Adnr = account.ADNR,
                Balance = state?.Balance ?? account.Balance,
                LockedBalance = state?.LockedBalance ?? account.LockedBalance,
                Nonce = state?.Nonce ?? 0,
                IsValidating = account.IsValidating
            };

            return Ok(result);
        }

        /// <summary>
        /// Get balance for an address
        /// </summary>
        [HttpGet("{address}/balance")]
        public IActionResult GetBalance(string address)
        {
            var state = StateData.GetSpecificAccountStateTrei(address);
            if (state == null)
                return Fail("NOT_FOUND", $"Account not found on chain: {address}", 404);

            var result = new
            {
                Address = state.Key,
                Balance = state.Balance,
                LockedBalance = state.LockedBalance,
                TokenAccounts = state.TokenAccounts ?? new List<TokenAccount>()
            };

            return Ok(result);
        }

        /// <summary>
        /// Import a private key
        /// </summary>
        [HttpPost("import")]
        public async Task<IActionResult> ImportKey([FromBody] ImportKeyRequest request)
        {
            var account = await AccountData.RestoreAccount(request.PrivateKey, request.Scan);

            if (account == null || string.IsNullOrEmpty(account.Address))
                return Fail("IMPORT_FAILED", "Could not restore account from the provided key.");

            return Created(new { Address = account.Address });
        }

        /// <summary>
        /// Get address nonce
        /// </summary>
        [HttpGet("{address}/nonce")]
        public IActionResult GetNonce(string address)
        {
            var nextNonce = AccountStateTrei.GetNextNonce(address);
            return Ok(new { Address = address, Nonce = nextNonce });
        }

        /// <summary>
        /// Rescan for transactions
        /// </summary>
        [HttpPost("{address}/rescan")]
        public IActionResult Rescan(string address)
        {
            var account = AccountData.GetSingleAccount(address);
            if (account == null)
                return Fail("NOT_FOUND", $"Account not found: {address}", 404);

            _ = Task.Run(() => BlockchainRescanUtility.RescanForTransactions(account.Address));

            return Ok("Rescan started.");
        }

        /// <summary>
        /// Sync all account balances from state
        /// </summary>
        [HttpPost("sync-balances")]
        public IActionResult SyncBalances()
        {
            var accountsDb = AccountData.GetAccounts();
            var accounts = accountsDb.Query().Where(x => true).ToEnumerable();
            var rAccounts = ReserveAccount.GetReserveAccounts();

            if (accounts.Any())
            {
                foreach (var account in accounts)
                {
                    var stateTrei = StateData.GetSpecificAccountStateTrei(account.Address);
                    if (stateTrei != null)
                    {
                        account.Balance = stateTrei.Balance;
                        account.LockedBalance = stateTrei.LockedBalance;
                        accountsDb.UpdateSafe(account);
                    }
                }
            }

            if (rAccounts?.Count > 0)
            {
                foreach (var rAccount in rAccounts)
                {
                    var stateTrei = StateData.GetSpecificAccountStateTrei(rAccount.Address);
                    if (stateTrei != null)
                    {
                        rAccount.AvailableBalance = stateTrei.Balance;
                        rAccount.LockedBalance = stateTrei.LockedBalance;
                        ReserveAccount.SaveReserveAccount(rAccount);
                    }
                }
            }

            return Ok("Balance sync completed.");
        }

        /// <summary>
        /// List NFTs owned by an address
        /// </summary>
        [HttpGet("{address}/nfts")]
        public IActionResult GetNfts(string address)
        {
            var scStates = SmartContractStateTrei.GetSmartContractsOwnedByAddress(address)?.ToList();
            if (scStates == null || !scStates.Any())
                return Ok(Array.Empty<object>());

            var result = scStates.Select(sc =>
            {
                var main = SmartContractMain.SmartContractData.GetSmartContract(sc.SmartContractUID);
                return new
                {
                    ScUID = sc.SmartContractUID,
                    OwnerAddress = sc.OwnerAddress,
                    Name = main?.Name ?? "Unknown"
                };
            });

            return Ok(result);
        }

        /// <summary>
        /// Validate an address
        /// </summary>
        [HttpGet("{address}/validate")]
        public IActionResult Validate(string address)
        {
            var isValid = AddressValidateUtility.ValidateAddress(address);
            return Ok(new { Address = address, IsValid = isValid });
        }
    }
}
