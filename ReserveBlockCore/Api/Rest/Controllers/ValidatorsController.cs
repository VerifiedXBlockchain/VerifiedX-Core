using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using ReserveBlockCore.Api.Rest.Infrastructure;
using ReserveBlockCore.Api.Rest.Models;
using ReserveBlockCore.Api.Rest.Models.Requests;
using ReserveBlockCore.Data;
using ReserveBlockCore.Models;
using ReserveBlockCore.Services;

namespace ReserveBlockCore.Api.Rest.Controllers
{
    public class ValidatorsController : RestBaseController
    {
        /// <summary>
        /// List validator-eligible accounts
        /// </summary>
        [HttpGet]
        public IActionResult GetAll()
        {
            var accounts = AccountData.GetPossibleValidatorAccounts();
            if (accounts.Count() == 0)
                return Ok(Array.Empty<object>());

            return Ok(accounts.ToList());
        }

        /// <summary>
        /// Check if currently validating
        /// </summary>
        [HttpGet("status")]
        public IActionResult GetStatus()
        {
            var isValidating = false;

            if (!string.IsNullOrEmpty(Globals.ValidatorAddress))
            {
                isValidating = Globals.ValidatorReceiving && Globals.ValidatorSending && Globals.ValidatorBalanceGood;
            }

            return Ok(new
            {
                IsValidating = isValidating,
                ValidatorAddress = Globals.ValidatorAddress,
                Receiving = Globals.ValidatorReceiving,
                Sending = Globals.ValidatorSending,
                BalanceGood = Globals.ValidatorBalanceGood
            });
        }

        /// <summary>
        /// Start validating (turn on existing validator)
        /// </summary>
        [HttpPost("start")]
        public IActionResult Start([FromBody] StartValidatorRequest request)
        {
            var validators = Validators.Validator.GetAll();
            var validator = validators.FindOne(x => x.Address == request.Address);
            if (validator == null)
                return Fail("NOT_FOUND", "No validator account has been found. Please create one.", 404);

            var accounts = AccountData.GetAccounts();
            var presentValidator = accounts.FindOne(x => x.IsValidating == true);
            if (presentValidator != null)
                return Fail("ALREADY_ACTIVE", $"There is already an account flagged as validator: {presentValidator.Address}", 409);

            var account = AccountData.GetSingleAccount(request.Address);
            if (account == null)
                return Fail("NOT_FOUND", "The requested account was not found in wallet.", 404);

            var stateTreiBalance = AccountStateTrei.GetAccountBalance(account.Address);
            if (stateTreiBalance < ValidatorService.ValidatorRequiredAmount())
                return Fail("INSUFFICIENT_BALANCE", $"The balance for this account is under {ValidatorService.ValidatorRequiredAmount()}.");

            account.IsValidating = true;
            accounts.UpdateSafe(account);
            Globals.ValidatorAddress = account.Address;
            Globals.ValidatorPublicKey = account.PublicKey;

            return Ok($"The requested account has been turned on: {account.Address}");
        }

        /// <summary>
        /// Stop validating
        /// </summary>
        [HttpPost("stop")]
        public async Task<IActionResult> Stop([FromBody] StopValidatorRequest request)
        {
            var accounts = AccountData.GetAccounts();
            var presentValidator = accounts.FindOne(x => x.IsValidating == true);
            if (presentValidator == null)
                return Fail("NOT_ACTIVE", "There are currently no active validators running.", 409);

            await ValidatorService.DoMasterNodeStop();

            return Ok($"The validator has been turned off: {presentValidator.Address}");
        }

        /// <summary>
        /// Get validator info by address
        /// </summary>
        [HttpGet("{address}")]
        public IActionResult GetInfo(string address)
        {
            var validators = Validators.Validator.GetAll();
            var validator = validators.FindOne(x => x.Address == address);
            if (validator == null)
                return Fail("NOT_FOUND", "Validator not on network yet.", 404);

            return Ok(new { Address = validator.Address, UniqueName = validator.UniqueName });
        }

        /// <summary>
        /// Register as a validator
        /// </summary>
        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] RegisterValidatorRequest request)
        {
            var valAccount = AccountData.GetPossibleValidatorAccounts();
            if (valAccount.Count() == 0)
                return Fail("NO_ELIGIBLE", "No eligible accounts were found that can validate.");

            var accountCheck = valAccount.Where(x => x.Address == request.Address).FirstOrDefault();
            if (accountCheck == null)
                return Fail("NOT_FOUND", "Account provided was not found in wallet.", 404);

            if (accountCheck.IsValidating)
                return Fail("ALREADY_ACTIVE", "Node is already flagged as validator.", 409);

            var valResult = await ValidatorService.StartValidating(accountCheck, request.UniqueName);

            return Ok(valResult);
        }

        /// <summary>
        /// Change validator name
        /// </summary>
        [HttpPut("name")]
        public IActionResult ChangeName([FromBody] ChangeValidatorNameRequest request)
        {
            if (string.IsNullOrWhiteSpace(Globals.ValidatorAddress))
                return Fail("NOT_ACTIVE", "No active validator found.");

            var validatorTable = Validators.Validator.GetAll();
            var validator = validatorTable.FindOne(x => x.Address == Globals.ValidatorAddress);
            if (validator == null)
                return Fail("NOT_FOUND", "Validator record not found.", 404);

            validator.UniqueName = request.UniqueName;
            validatorTable.UpdateSafe(validator);

            return Ok("Validator unique name updated. Please restart wallet.");
        }

        /// <summary>
        /// Reset validator
        /// </summary>
        [HttpPost("reset")]
        public async Task<IActionResult> Reset()
        {
            var result = await ValidatorService.ValidatorErrorReset();
            if (!result)
                return Fail("RESET_FAILED", "Validator reset failed.");

            return Ok("Validator reset successful.");
        }

        /// <summary>
        /// Get network validator pool
        /// </summary>
        [HttpGet("pool")]
        public IActionResult GetPool()
        {
            if (Globals.NetworkValidators.Any())
                return Ok(Globals.NetworkValidators);

            return Ok(Array.Empty<object>());
        }
    }
}
