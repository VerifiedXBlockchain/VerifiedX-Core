using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using ReserveBlockCore.Api.Rest.Infrastructure;
using ReserveBlockCore.Api.Rest.Models;
using ReserveBlockCore.Api.Rest.Models.Requests;
using ReserveBlockCore.Data;
using ReserveBlockCore.Models;
using ReserveBlockCore.Models.SmartContracts;
using ReserveBlockCore.Services;
using ReserveBlockCore.Utilities;

namespace ReserveBlockCore.Api.Rest.Controllers
{
    [Route("api/rest/reserve-accounts")]
    public class ReserveAccountsController : RestBaseController
    {
        /// <summary>
        /// List all reserve accounts
        /// </summary>
        [HttpGet]
        public IActionResult GetAll()
        {
            var reserveAccounts = ReserveAccount.GetReserveAccounts();
            if (reserveAccounts == null || reserveAccounts.Count == 0)
                return Ok(Array.Empty<object>());

            return Ok(reserveAccounts);
        }

        /// <summary>
        /// Create a new reserve account
        /// </summary>
        [HttpPost]
        public IActionResult Create([FromBody] CreateReserveAccountRequest request)
        {
            var result = ReserveAccount.CreateNewReserveAccount(request.Password, request.StoreRecoveryAccount);
            if (result == null)
                return Fail("CREATE_FAILED", "Failed to create reserve account.");

            return Created(result);
        }

        /// <summary>
        /// Get reserve account info
        /// </summary>
        [HttpGet("{address}")]
        public IActionResult GetInfo(string address)
        {
            var reserveAccount = ReserveAccount.GetReserveAccountSingle(address);
            if (reserveAccount == null)
                return Fail("NOT_FOUND", $"Reserve account not found: {address}", 404);

            return Ok(reserveAccount);
        }

        /// <summary>
        /// Publish a reserve account to the network
        /// </summary>
        [HttpPost("{address}/publish")]
        public async Task<IActionResult> Publish(string address, [FromBody] PublishReserveAccountRequest request)
        {
            var account = ReserveAccount.GetReserveAccountSingle(address);
            if (account == null)
                return Fail("NOT_FOUND", $"Reserve account not found: {address}", 404);

            if (account.AvailableBalance < 5)
                return Fail("INSUFFICIENT_BALANCE", "Account must have a balance of 5 VFX.");

            if (account.IsNetworkProtected)
                return Fail("ALREADY_PUBLISHED", "Account has already been published to the network.", 409);

            var result = await ReserveAccount.CreateReservePublishTx(account, request.Password);
            if (result.Item1 == null)
                return Fail("PUBLISH_FAILED", result.Item2);

            return Ok(new { TxHash = result.Item1.Hash });
        }

        /// <summary>
        /// Unlock a reserve account
        /// </summary>
        [HttpPost("{address}/unlock")]
        public IActionResult Unlock(string address, [FromBody] UnlockReserveAccountRequest request)
        {
            if (Globals.ReserveAccountUnlockKeys.ContainsKey(address))
                return Ok(new { AlreadyUnlocked = true, Message = "Reserve account is already unlocked." });

            var key = ReserveAccount.GetPrivateKey(address, request.Password);
            if (key == null)
                return Fail("INVALID_PASSWORD", "Key could not be created from password.");

            var rAccount = ReserveAccount.CreateNewReserveAccount(request.Password, false, true, key);
            if (rAccount.Address != address)
                return Fail("INVALID_CREDENTIALS", "Provided details were not correct. Please try a different password or address.");

            var rAUK = new ReserveAccountUnlockKey
            {
                DeleteAfterTime = TimeUtil.GetTime(0, Globals.WalletUnlockTime, 0, 0),
                Password = request.Password.ToSecureString(),
                UnlockTimeHours = request.UnlockTime
            };

            if (Globals.ReserveAccountUnlockKeys.ContainsKey(address))
                Globals.ReserveAccountUnlockKeys[address] = rAUK;
            else
                Globals.ReserveAccountUnlockKeys.TryAdd(address, rAUK);

            return Ok(new { AlreadyUnlocked = false, Message = $"Reserve account has been unlocked for {Globals.WalletUnlockTime} minutes." });
        }

        /// <summary>
        /// Send a reserve transaction
        /// </summary>
        [HttpPost("send")]
        public async Task<IActionResult> Send([FromBody] SendReserveTransactionRequest request)
        {
            if (request.FromAddress.StartsWith("xRBX") && request.ToAddress.StartsWith("xRBX"))
                return Fail("INVALID_DESTINATION", "Reserve accounts cannot send to another reserve account.");

            var addrCheck = AddressValidateUtility.ValidateAddress(request.ToAddress);
            if (!addrCheck)
                return Fail("INVALID_ADDRESS", "This is not a valid VFX address to send to.");

            var payload = new ReserveAccount.SendTransactionPayload
            {
                FromAddress = request.FromAddress,
                ToAddress = request.ToAddress,
                Amount = request.Amount * 1.0M,
                DecryptPassword = request.DecryptPassword,
                UnlockDelayHours = request.UnlockDelayHours
            };

            var result = await ReserveAccount.CreateReserveTx(payload);
            if (result.Item1 == null)
                return Fail("SEND_FAILED", result.Item2);

            return Ok(new { TxHash = result.Item1.Hash });
        }

        /// <summary>
        /// Transfer an NFT from a reserve account
        /// </summary>
        [HttpPost("transfer-nft")]
        public async Task<IActionResult> TransferNft([FromBody] ReserveTransferNftRequest request)
        {
            var fromAddress = ReserveAccount.GetReserveAccountSingle(request.FromAddress);
            if (fromAddress == null)
                return Fail("NOT_FOUND", "From address was not found in wallet.", 404);

            if (request.FromAddress.StartsWith("xRBX") && request.ToAddress.StartsWith("xRBX"))
                return Fail("INVALID_DESTINATION", "Reserve accounts cannot send to another reserve account.");

            var keyString = ReserveAccount.GetPrivateKey(request.FromAddress, request.DecryptPassword);
            if (keyString == null)
                return Fail("INVALID_PASSWORD", "Unable to get private key. Please ensure account is in wallet and password was correct.");

            var key = ReserveAccount.GetPrivateKey(keyString);
            if (key == null)
                return Fail("INVALID_PASSWORD", "Unable to get private key. Please ensure account is in wallet and password was correct.");

            var sc = SmartContractMain.SmartContractData.GetSmartContract(request.SmartContractUID);
            if (sc == null)
                return Fail("NOT_FOUND", $"Could not find smart contract with UID: {request.SmartContractUID}", 404);

            if (!sc.IsPublished)
                return Fail("NOT_PUBLISHED", "Smart contract found, but has not been minted.", 409);

            var payload = new ReserveAccount.SendNFTTransferPayload
            {
                FromAddress = request.FromAddress,
                ToAddress = request.ToAddress,
                DecryptPassword = request.DecryptPassword,
                UnlockDelayHours = request.UnlockDelayHours,
                SmartContractUID = request.SmartContractUID,
                BackupURL = request.BackupURL
            };

            var result = await ReserveAccount.CreateReserveNFTTransferTx(payload);
            if (!result.Item1)
                return Fail("TRANSFER_FAILED", result.Item2);

            return Ok(result.Item2);
        }

        /// <summary>
        /// Recover a reserve account
        /// </summary>
        [HttpPost("{address}/recover")]
        public async Task<IActionResult> Recover(string address, [FromBody] RecoverReserveAccountRequest request)
        {
            var account = ReserveAccount.GetReserveAccountSingle(address);
            if (account == null)
                return Fail("NOT_FOUND", $"Reserve account not found: {address}", 404);

            var result = await ReserveAccount.CreateReserveRecoverTx(account, request.Password, request.RecoveryPhrase);
            if (result.Item1 == null)
                return Fail("RECOVER_FAILED", result.Item2);

            return Ok(new { TxHash = result.Item1.Hash });
        }

        /// <summary>
        /// Restore a reserve account from restore code
        /// </summary>
        [HttpPost("restore")]
        public async Task<IActionResult> Restore([FromBody] RestoreReserveAccountRequest request)
        {
            var payload = new ReserveAccount.ReserveAccountRestorePayload
            {
                RestoreCode = request.RestoreCode,
                Password = request.Password,
                StoreRecoveryAccount = request.StoreRecoveryAccount,
                RescanForTx = request.RescanForTx,
                OnlyRestoreRecovery = request.OnlyRestoreRecovery
            };

            if (payload.OnlyRestoreRecovery)
            {
                var restoreCode = payload.RestoreCode.ToStringFromBase64().Split("//");
                var recoveryKey = restoreCode[1];

                var account = await AccountData.RestoreAccount(recoveryKey, payload.RescanForTx);
                if (account == null || string.IsNullOrEmpty(account.Address))
                    return Fail("RESTORE_FAILED", "Failed to restore recovery account.");

                return Created(new { Account = account });
            }
            else
            {
                var result = ReserveAccount.RestoreReserveAccount(payload.RestoreCode, payload.Password, payload.StoreRecoveryAccount, payload.RescanForTx);
                if (result == null)
                    return Fail("RESTORE_FAILED", "Failed to restore reserve account.");

                return Created(result);
            }
        }

        /// <summary>
        /// Callback a reserve account transaction
        /// </summary>
        [HttpPost("{hash}/callback")]
        public async Task<IActionResult> Callback(string hash, [FromBody] CallbackReserveAccountRequest request)
        {
            var tx = ReserveTransactions.GetTransactions(hash);
            if (tx == null)
                return Fail("NOT_FOUND", $"Could not find a reserve TX with hash: {hash}", 404);

            var account = ReserveAccount.GetReserveAccountSingle(tx.FromAddress);
            if (account == null)
                return Fail("NOT_FOUND", "Reserve account not found for this transaction.", 404);

            var result = await ReserveAccount.CreateReserveCallBackTx(account, request.Password, tx.Hash);
            if (result.Item1 == null)
                return Fail("CALLBACK_FAILED", result.Item2);

            return Ok(new { TxHash = result.Item1.Hash });
        }
    }
}
