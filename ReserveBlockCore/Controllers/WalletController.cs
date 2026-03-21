using Microsoft.AspNetCore.Mvc;
using NBitcoin.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ReserveBlockCore.Bitcoin.Models;
using ReserveBlockCore.Data;
using ReserveBlockCore.Models;
using ReserveBlockCore.Models.SmartContracts;
using ReserveBlockCore.Models.Privacy;
using ReserveBlockCore.Privacy;
using ReserveBlockCore.Services;
using ReserveBlockCore.Utilities;

namespace ReserveBlockCore.Controllers
{
    [Route("wallet")]
    [ApiController]
    public class WalletController : ControllerBase
    {
        // ── HTML page ─────────────────────────────────────────────────────────────
        [HttpGet("")]
        public ContentResult Index() => Content(GetHtml(), "text/html; charset=utf-8");

        // ── Accounts + balances + tokens ─────────────────────────────────────────
        [HttpGet("api/accounts")]
        public IActionResult GetAccounts()
        {
            try
            {
                var accounts = AccountData.GetAccounts()?.Query().Where(x => true).ToList();
                if (accounts == null || !accounts.Any())
                    return Ok(Array.Empty<object>());

                var result = accounts.Select(a =>
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
                });

                return Ok(result);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // ── Transaction history ───────────────────────────────────────────────────
        [HttpGet("api/txs/{address}")]
        public IActionResult GetTransactions(string address)
        {
            try
            {
                var txs = TransactionData.GetAll().Query()
                    .Where(t => t.FromAddress == address || t.ToAddress == address)
                    .OrderByDescending(t => t.Height)
                    .Limit(50)
                    .ToList();

                return Ok(txs);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // ── NFTs / Smart Contracts owned ─────────────────────────────────────────
        [HttpGet("api/nfts/{address}")]
        public IActionResult GetNFTs(string address)
        {
            try
            {
                var scStates = SmartContractStateTrei.GetSmartContractsOwnedByAddress(address)?.ToList();
                if (scStates == null || !scStates.Any())
                    return Ok(Array.Empty<object>());

                var result = scStates.Select(sc =>
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
                });

                return Ok(result);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // ── Bitcoin accounts ──────────────────────────────────────────────────────
        [HttpGet("api/btc")]
        public IActionResult GetBitcoinAccounts()
        {
            try
            {
                var accounts = BitcoinAccount.GetBitcoinAccounts();
                if (accounts == null || !accounts.Any())
                    return Ok(Array.Empty<object>());

                var result = accounts.Select(a => new
                {
                    address = a.Address,
                    adnr = a.ADNR,
                    balance = a.Balance,
                    isValidating = a.IsValidating
                });

                return Ok(result);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // ── vBTC contracts ────────────────────────────────────────────────────────
        [HttpGet("api/vbtc/{address}")]
        public IActionResult GetVBTC(string address)
        {
            try
            {
                // Find all vBTC contracts where address is owner OR has a balance via tokenization TXes
                var scStates = SmartContractStateTrei.GetvBTCSmartContracts(address);
                if (scStates == null || !scStates.Any())
                    return Ok(Array.Empty<object>());

                var resultList = new List<object>();
                var seen = new HashSet<string>();

                foreach (var scState in scStates)
                {
                    bool isOwner = scState.OwnerAddress == address;

                    // Calculate ledger balance from tokenization TXes
                    decimal ledgerBalance = 0M;
                    if (scState.SCStateTreiTokenizationTXes != null && scState.SCStateTreiTokenizationTXes.Any())
                    {
                        var transactions = scState.SCStateTreiTokenizationTXes
                            .Where(x => x.FromAddress == address || x.ToAddress == address)
                            .ToList();
                        if (transactions.Any())
                            ledgerBalance = transactions.Sum(x => x.Amount);
                    }

                    decimal totalBalance = ledgerBalance;
                    string depositAddress = "";

                    if (isOwner)
                    {
                        // Owner: add deposit address balance from local contract
                        var contract = VBTCContractV2.GetContract(scState.SmartContractUID);
                        if (contract != null)
                        {
                            depositAddress = contract.DepositAddress ?? "";
                            totalBalance = contract.Balance + ledgerBalance;
                        }
                    }

                    // Only include once per contract (deduplicate owner appearing in both owner + tx queries)
                    if (!seen.Add(scState.SmartContractUID))
                        continue;

                    if (totalBalance > 0M || isOwner)
                    {
                        var contract = VBTCContractV2.GetContract(scState.SmartContractUID);
                        resultList.Add(new
                        {
                            scUID = scState.SmartContractUID,
                            ownerAddress = scState.OwnerAddress,
                            depositAddress = depositAddress,
                            balance = totalBalance,
                            ledgerBalance = ledgerBalance,
                            isOwner = isOwner,
                            withdrawalStatus = contract?.WithdrawalStatus.ToString() ?? "None",
                            activeWithdrawalAmount = contract?.ActiveWithdrawalAmount ?? 0M,
                            activeWithdrawalDest = contract?.ActiveWithdrawalBTCDestination ?? "",
                            proofBlockHeight = contract?.ProofBlockHeight ?? 0,
                            totalValidators = contract?.TotalRegisteredValidators ?? 0,
                            requiredThreshold = contract?.RequiredThreshold ?? 0
                        });
                    }
                }

                return Ok(resultList);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // ── Send VFX ──────────────────────────────────────────────────────────────
        [HttpPost("api/send/vfx")]
        public async Task<IActionResult> SendVFX([FromBody] SendRequest req)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(req.From) || string.IsNullOrWhiteSpace(req.To) || string.IsNullOrWhiteSpace(req.Amount))
                    return BadRequest(new { success = false, message = "From, To, and Amount are required." });

                if (!decimal.TryParse(req.Amount, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out decimal amount) || amount <= 0)
                    return BadRequest(new { success = false, message = "Invalid amount." });

                var addrValid = AddressValidateUtility.ValidateAddress(req.To);
                if (!addrValid)
                    return BadRequest(new { success = false, message = "Invalid destination address." });

                if (Globals.IsWalletEncrypted && Globals.EncryptPassword.Length == 0)
                    return BadRequest(new { success = false, message = "Wallet is encrypted. Please decrypt first." });

                var result = await WalletService.SendTXOut(req.From, req.To, amount);

                var success = result.Contains("Success") || result.Contains("Hash") || (!result.Contains("Fail") && !result.Contains("fail") && !result.Contains("Error"));
                return Ok(new { success, message = result });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, message = ex.Message });
            }
        }

        public class SendRequest
        {
            public string From { get; set; } = "";
            public string To { get; set; } = "";
            public string Amount { get; set; } = "";
        }

        // ── vBTC Withdrawal: Request ──────────────────────────────────────────────
        [HttpPost("api/vbtc/withdraw/request")]
        public async Task<IActionResult> VBTCWithdrawRequest([FromBody] VBTCWDRequest req)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(req.ScUID) || string.IsNullOrWhiteSpace(req.OwnerAddress) ||
                    string.IsNullOrWhiteSpace(req.BTCAddress) || string.IsNullOrWhiteSpace(req.Amount))
                    return BadRequest(new { success = false, message = "scUID, ownerAddress, btcAddress, and amount are required." });

                if (!decimal.TryParse(req.Amount, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out decimal amount) || amount <= 0)
                    return BadRequest(new { success = false, message = "Invalid amount." });

                int feeRate = 10;
                if (!string.IsNullOrWhiteSpace(req.FeeRate) && int.TryParse(req.FeeRate, out int fr) && fr > 0)
                    feeRate = fr;

                var result = await Bitcoin.Services.VBTCService.RequestWithdrawal(req.ScUID, req.OwnerAddress, req.BTCAddress, amount, feeRate);
                return Ok(new { success = result.Item1, message = result.Item2 });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, message = ex.Message });
            }
        }

        // ── vBTC Withdrawal: Complete ─────────────────────────────────────────────
        [HttpPost("api/vbtc/withdraw/complete")]
        public async Task<IActionResult> VBTCWithdrawComplete([FromBody] VBTCWDComplete req)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(req.ScUID) || string.IsNullOrWhiteSpace(req.RequestHash))
                    return BadRequest(new { success = false, message = "scUID and requestHash are required." });

                if (string.IsNullOrEmpty(Globals.ValidatorAddress))
                {
                    var remoteResult = await DelegateWithdrawalToRemoteValidator(req.ScUID, req.RequestHash);
                    return Ok(new { success = true, message = "Withdrawal completed!", remoteResult });
                }

                var result = await Bitcoin.Services.VBTCService.CompleteWithdrawal(req.ScUID, req.RequestHash);
                if (result.Success)
                    return Ok(new { success = true, message = "Withdrawal completed!", vfxTxHash = result.VFXTxHash, btcTxHash = result.BTCTxHash });
                else
                    return Ok(new { success = false, message = result.ErrorMessage });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, message = ex.Message });
            }
        }

        // ── vBTC Withdrawal: Status ───────────────────────────────────────────────
        [HttpGet("api/vbtc/withdraw/status/{scUID}")]
        public IActionResult VBTCWithdrawStatus(string scUID)
        {
            try
            {
                var contract = VBTCContractV2.GetContract(scUID);
                if (contract == null)
                    return Ok(new { success = false, message = "Contract not found" });

                return Ok(new
                {
                    success = true,
                    status = contract.WithdrawalStatus.ToString(),
                    amount = contract.ActiveWithdrawalAmount ?? 0M,
                    destination = contract.ActiveWithdrawalBTCDestination ?? "",
                    requestHash = contract.ActiveWithdrawalRequestHash ?? ""
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, message = ex.Message });
            }
        }

        private async Task<string> DelegateWithdrawalToRemoteValidator(string scUID, string withdrawalRequestHash)
        {
            try
            {
                LogUtility.Log($"[FROST MPC] Non-validator node delegating withdrawal to remote validator. scUID: {scUID}",
                    "VBTCController.DelegateWithdrawalToRemoteValidator");

                // Look up the local withdrawal request to include details in the delegation payload.
                // The remote validator may not have this record in its local DB.
                decimal? localAmount = null;
                string? localBTCDestination = null;
                int? localFeeRate = null;

                var localWithdrawalRequest = VBTCWithdrawalRequest.GetByTransactionHash(withdrawalRequestHash);
                if (localWithdrawalRequest != null)
                {
                    localAmount = localWithdrawalRequest.Amount;
                    localBTCDestination = localWithdrawalRequest.BTCDestination;
                    localFeeRate = localWithdrawalRequest.FeeRate;
                    LogUtility.Log($"[FROST MPC] Including local withdrawal details in delegation: Amount={localAmount}, Dest={localBTCDestination}, FeeRate={localFeeRate}",
                        "VBTCController.DelegateWithdrawalToRemoteValidator");
                }
                else
                {
                    // Also try the local contract's Active* fields as a fallback
                    var localContract = VBTCContractV2.GetContract(scUID);
                    if (localContract != null && localContract.ActiveWithdrawalAmount.HasValue && localContract.ActiveWithdrawalAmount.Value > 0)
                    {
                        localAmount = localContract.ActiveWithdrawalAmount.Value;
                        localBTCDestination = localContract.ActiveWithdrawalBTCDestination;
                        localFeeRate = 10; // Default
                        LogUtility.Log($"[FROST MPC] Including contract Active* fields in delegation: Amount={localAmount}, Dest={localBTCDestination}",
                            "VBTCController.DelegateWithdrawalToRemoteValidator");
                    }
                    else
                    {
                        LogUtility.Log($"[FROST MPC] WARNING: No local withdrawal request or contract Active* fields found. Remote validator will need its own data.",
                            "VBTCController.DelegateWithdrawalToRemoteValidator");
                    }
                }

                // Discover active validators from the network
                var activeValidators = await VBTCValidator.FetchActiveValidatorsFromNetwork();
                if (activeValidators == null || !activeValidators.Any())
                {
                    return JsonConvert.SerializeObject(new { Success = false, Message = "No active validators available on the network. Cannot delegate withdrawal." });
                }

                // Try each validator until one successfully handles the withdrawal
                foreach (var validator in activeValidators)
                {
                    try
                    {
                        var ip = validator.IPAddress?.Replace("::ffff:", "");
                        if (string.IsNullOrEmpty(ip)) continue;

                        var url = $"http://{ip}:{Globals.FrostValidatorPort}/frost/mpc/withdrawal/complete";
                        using var client = Globals.HttpClientFactory.CreateClient();
                        client.Timeout = TimeSpan.FromSeconds(120); // Withdrawal can take time (FROST signing + BTC broadcast)

                        var payload = JsonConvert.SerializeObject(new
                        {
                            SmartContractUID = scUID,
                            WithdrawalRequestHash = withdrawalRequestHash,
                            Amount = localAmount,
                            BTCDestination = localBTCDestination,
                            FeeRate = localFeeRate
                        });
                        var content = new System.Net.Http.StringContent(payload, System.Text.Encoding.UTF8, "application/json");

                        var response = await client.PostAsync(url, content);
                        if (!response.IsSuccessStatusCode) continue;

                        var responseBody = await response.Content.ReadAsStringAsync();
                        var json = JObject.Parse(responseBody);

                        if (json["Success"]?.Value<bool>() == true)
                        {
                            LogUtility.Log($"[FROST MPC] FROST signing delegated successfully to validator {validator.ValidatorAddress} ({ip})",
                                "VBTCController.DelegateWithdrawalToRemoteValidator");

                            // Extract the signed TX hex from the validator response
                            var signedTxHex = json["SignedTxHex"]?.Value<string>();
                            if (string.IsNullOrEmpty(signedTxHex))
                            {
                                LogUtility.Log($"[FROST MPC] Validator returned success but no SignedTxHex. Response: {responseBody}",
                                    "VBTCController.DelegateWithdrawalToRemoteValidator");
                                continue; // Try next validator
                            }

                            // Step 1: Broadcast the signed BTC TX locally (single broadcast — only this wallet node)
                            LogUtility.Log($"[FROST MPC] Broadcasting signed BTC TX locally. Hex length: {signedTxHex.Length}",
                                "VBTCController.DelegateWithdrawalToRemoteValidator");

                            var btcNetwork = Globals.BTCNetwork;
                            var signedTx = NBitcoin.Transaction.Parse(signedTxHex, btcNetwork);
                            var broadcastResult = await ReserveBlockCore.Bitcoin.Services.BitcoinTransactionService.BroadcastTransaction(signedTx);

                            if (!broadcastResult.Success)
                            {
                                LogUtility.Log($"[FROST MPC] BTC broadcast failed: {broadcastResult.ErrorMessage}. Will try next validator.",
                                    "VBTCController.DelegateWithdrawalToRemoteValidator");
                                continue; // Try next validator
                            }

                            string btcTxHash = broadcastResult.TxHash;
                            LogUtility.Log($"[FROST MPC] BTC TX broadcast SUCCESS: {btcTxHash}",
                                "VBTCController.DelegateWithdrawalToRemoteValidator");

                            // Step 2: Update local contract record
                            try
                            {
                                var localContract = VBTCContractV2.GetContract(scUID);
                                if (localContract != null)
                                {
                                    localContract.LastValidatorActivityBlock = Globals.LastBlock.Height;
                                    VBTCContractV2.UpdateContract(localContract);
                                }
                            }
                            catch (Exception updateEx)
                            {
                                LogUtility.Log($"[FROST MPC] Warning: Failed to update local contract: {updateEx.Message}",
                                    "VBTCController.DelegateWithdrawalToRemoteValidator");
                            }

                            // Step 3: Create VFX completion TX locally (wallet node has the withdrawal request)
                            string vfxTxHash = string.Empty;
                            try
                            {
                                // Find the requestor account to sign the VFX TX
                                string fromAddress = localWithdrawalRequest?.RequestorAddress ?? string.Empty;
                                if (string.IsNullOrEmpty(fromAddress))
                                {
                                    var localContract = VBTCContractV2.GetContract(scUID);
                                    fromAddress = localContract?.OwnerAddress ?? string.Empty;
                                }

                                var account = AccountData.GetSingleAccount(fromAddress);
                                if (account != null)
                                {
                                    var txData = JsonConvert.SerializeObject(new
                                    {
                                        Function = "VBTCWithdrawalComplete()",
                                        ContractUID = scUID,
                                        WithdrawalRequestHash = withdrawalRequestHash,
                                        BTCTransactionHash = btcTxHash,
                                        Amount = localAmount ?? 0M,
                                        Destination = localBTCDestination ?? ""
                                    });

                                    var completionTx = new Transaction
                                    {
                                        Timestamp = TimeUtil.GetTime(),
                                        FromAddress = fromAddress,
                                        ToAddress = fromAddress,
                                        Amount = 0.0M,
                                        Fee = 0.0M,
                                        Nonce = AccountStateTrei.GetNextNonce(fromAddress),
                                        TransactionType = TransactionType.VBTC_V2_WITHDRAWAL_COMPLETE,
                                        Data = txData
                                    };

                                    completionTx.Fee = FeeCalcService.CalculateTXFee(completionTx);
                                    completionTx.Build();

                                    var privateKey = account.GetPrivKey;
                                    var publicKey = account.PublicKey;
                                    if (privateKey != null)
                                    {
                                        var signature = SignatureService.CreateSignature(completionTx.Hash, privateKey, publicKey);
                                        if (signature != "ERROR")
                                        {
                                            completionTx.Signature = signature;
                                            var verifyResult = await TransactionValidatorService.VerifyTX(completionTx);
                                            if (verifyResult.Item1)
                                            {
                                                await TransactionData.AddTxToWallet(completionTx, true);
                                                await AccountData.UpdateLocalBalance(fromAddress, completionTx.Fee + completionTx.Amount);
                                                await TransactionData.AddToPool(completionTx);
                                                await ReserveBlockCore.P2P.P2PClient.SendTXMempool(completionTx);
                                                vfxTxHash = completionTx.Hash;
                                                LogUtility.Log($"[FROST MPC] VFX completion TX broadcast SUCCESS: {vfxTxHash}",
                                                    "VBTCController.DelegateWithdrawalToRemoteValidator");
                                            }
                                            else
                                            {
                                                LogUtility.Log($"[FROST MPC] VFX completion TX verify failed: {verifyResult.Item2}. BTC TX already broadcast — VFX TX can be retried.",
                                                    "VBTCController.DelegateWithdrawalToRemoteValidator");
                                            }
                                        }
                                    }
                                }
                                else
                                {
                                    LogUtility.Log($"[FROST MPC] Account not found for {fromAddress} — cannot create VFX completion TX. BTC TX already broadcast.",
                                        "VBTCController.DelegateWithdrawalToRemoteValidator");
                                }
                            }
                            catch (Exception vfxEx)
                            {
                                LogUtility.Log($"[FROST MPC] VFX completion TX error (non-blocking): {vfxEx.Message}. BTC TX already broadcast: {btcTxHash}",
                                    "VBTCController.DelegateWithdrawalToRemoteValidator");
                            }

                            return JsonConvert.SerializeObject(new
                            {
                                Success = true,
                                Message = "vBTC V2 withdrawal completed successfully with FROST signing",
                                BTCTransactionHash = btcTxHash,
                                VFXTransactionHash = vfxTxHash,
                                Status = "Pending_BTC",
                                SmartContractUID = scUID
                            });
                        }
                        else
                        {
                            var errMsg = json["Message"]?.Value<string>() ?? "Unknown error";
                            LogUtility.Log($"[FROST MPC] Validator {validator.ValidatorAddress} rejected withdrawal: {errMsg}",
                                "VBTCController.DelegateWithdrawalToRemoteValidator");
                        }
                    }
                    catch (Exception ex)
                    {
                        LogUtility.Log($"[FROST MPC] Failed to delegate withdrawal to validator {validator.ValidatorAddress}: {ex.Message}",
                            "VBTCController.DelegateWithdrawalToRemoteValidator");
                    }
                }

                return JsonConvert.SerializeObject(new { Success = false, Message = "Failed to delegate withdrawal to any validator. No validator accepted the request." });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { Success = false, Message = $"Error delegating withdrawal: {ex.Message}" });
            }
        }

        public class VBTCWDRequest
        {
            public string ScUID { get; set; } = "";
            public string OwnerAddress { get; set; } = "";
            public string BTCAddress { get; set; } = "";
            public string Amount { get; set; } = "";
            public string FeeRate { get; set; } = "10";
        }

        public class VBTCWDComplete
        {
            public string ScUID { get; set; } = "";
            public string RequestHash { get; set; } = "";
        }

        // ── vBTC Transfer (send vBTC to another VFX address) ──────────────────────
        [HttpPost("api/vbtc/transfer")]
        public async Task<IActionResult> VBTCTransfer([FromBody] VBTCTransferRequest req)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(req.ScUID) || string.IsNullOrWhiteSpace(req.FromAddress) ||
                    string.IsNullOrWhiteSpace(req.ToAddress) || string.IsNullOrWhiteSpace(req.Amount))
                    return BadRequest(new { success = false, message = "scUID, fromAddress, toAddress, and amount are required." });

                if (!decimal.TryParse(req.Amount, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out decimal amount) || amount <= 0)
                    return BadRequest(new { success = false, message = "Invalid amount." });

                var addrValid = AddressValidateUtility.ValidateAddress(req.ToAddress);
                if (!addrValid)
                    return BadRequest(new { success = false, message = "Invalid destination VFX address." });

                var result = await Bitcoin.Services.VBTCService.TransferVBTC(req.ScUID, req.FromAddress, req.ToAddress, amount);
                return Ok(new { success = result.Item1, message = result.Item2 });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, message = ex.Message });
            }
        }

        public class VBTCTransferRequest
        {
            public string ScUID { get; set; } = "";
            public string FromAddress { get; set; } = "";
            public string ToAddress { get; set; } = "";
            public string Amount { get; set; } = "";
        }

        // ═══════════════════════════════════════════════════════════════════════════
        //  VFX Privacy / Shielded Endpoints
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// List all shielded addresses stored in the local privacy DB.
        /// </summary>
        [HttpGet("api/privacy/addresses")]
        public IActionResult GetShieldedAddresses()
        {
            try
            {
                var wallets = ShieldedWalletService.GetAll();
                if (wallets == null || !wallets.Any())
                    return Ok(Array.Empty<object>());

                var result = wallets.Select(w =>
                {
                    var vfxBal = (w.UnspentCommitments ?? new List<Models.Privacy.UnspentCommitment>())
                        .Where(c => c != null && !c.IsSpent && string.Equals(c.AssetType, "VFX", StringComparison.Ordinal))
                        .Sum(c => c.Amount);

                    return new
                    {
                        zfxAddress = w.ShieldedAddress,
                        transparentSourceAddress = w.TransparentSourceAddress ?? "",
                        vfxShieldedBalance = vfxBal,
                        unspentNotes = (w.UnspentCommitments ?? new List<Models.Privacy.UnspentCommitment>())
                            .Count(c => c != null && !c.IsSpent && string.Equals(c.AssetType, "VFX", StringComparison.Ordinal)),
                        lastScannedBlock = w.LastScannedBlock,
                        isViewOnly = w.IsViewOnly
                    };
                });

                return Ok(result);
            }
            catch (Exception ex) { return StatusCode(500, new { success = false, message = ex.Message }); }
        }

        /// <summary>
        /// Create a shielded address (zfx_) derived from the account's private key.
        /// Works for both single accounts and HD-derived accounts.
        /// <paramref name="address"/> is the transparent VFX address to associate.
        /// <paramref name="password"/> protects the spending key at rest (min 8 chars).
        /// </summary>
        [HttpGet("api/privacy/createShieldedAddress/{address}/{password}")]
        public IActionResult CreateShieldedAddress(string address, string password)
        {
            try
            {
                var account = AccountData.GetSingleAccount(address);
                if (account == null)
                    return Ok(new { success = false, message = $"Transparent address {address} not in local wallet." });

                // Derive shielded key material from the account's own private key.
                // This works for both single accounts and HD-derived accounts.
                var accountKey = account.GetKey;
                if (string.IsNullOrWhiteSpace(accountKey))
                    return Ok(new { success = false, message = "Cannot access account private key. Is the wallet locked?" });

                var keyMat = ShieldedHdDerivation.DeriveFromPrivateKey(accountKey);
                var wallet = ShieldedWalletService.CreateFromKeyMaterial(keyMat, address, password);
                ShieldedWalletService.Upsert(wallet);

                return Ok(new { success = true, zfxAddress = keyMat.ZfxAddress, transparentSourceAddress = address });
            }
            catch (Exception ex) { return StatusCode(500, new { success = false, message = ex.Message }); }
        }

        /// <summary>
        /// Get the shielded VFX balance for a zfx_ address.
        /// </summary>
        [HttpGet("api/privacy/balance/{zfxAddress}")]
        public IActionResult GetShieldedBalance(string zfxAddress)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(zfxAddress) || !zfxAddress.StartsWith("zfx_"))
                    return Ok(new { success = false, message = "Invalid zfx_ address." });

                var wallet = ShieldedWalletService.FindByZfxAddress(zfxAddress);
                if (wallet == null)
                    return Ok(new { success = false, message = "Wallet not found." });

                var vfxNotes = (wallet.UnspentCommitments ?? new List<UnspentCommitment>())
                    .Where(c => c != null && !c.IsSpent && string.Equals(c.AssetType, "VFX", StringComparison.Ordinal)).ToList();

                return Ok(new
                {
                    success = true,
                    zfxAddress,
                    vfxShieldedBalance = vfxNotes.Sum(c => c.Amount),
                    unspentNotes = vfxNotes.Count
                });
            }
            catch (Exception ex) { return StatusCode(500, new { success = false, message = ex.Message }); }
        }

        /// <summary>
        /// Shield VFX: transparent → shielded (T→Z).
        /// </summary>
        [HttpGet("api/privacy/shield/{fromAddress}/{zfxAddress}/{amount}")]
        public async Task<IActionResult> ShieldVFX(string fromAddress, string zfxAddress, decimal amount)
        {
            try
            {
                if (!AddressValidateUtility.ValidateAddress(fromAddress))
                    return Ok(new { success = false, message = "Invalid FromAddress." });
                var account = AccountData.GetSingleAccount(fromAddress);
                if (account == null)
                    return Ok(new { success = false, message = $"Transparent address {fromAddress} not in local wallet." });

                var nonce = AccountStateTrei.GetNextNonce(fromAddress);
                var ts = TimeUtil.GetTime();
                if (!VfxPrivateTransactionBuilder.TryBuildShield(
                        fromAddress,
                        amount,
                        Globals.MinFeePerKB,
                        nonce,
                        ts,
                        zfxAddress,
                        null,
                        out var tx,
                        out var buildErr,
                        DbContext.DB_Privacy))
                    return Ok(new { success = false, message = buildErr ?? "Failed to build shield TX." });

                tx!.Fee = FeeCalcService.CalculateTXFee(tx);
                tx.BuildPrivate();
                var pk = account.GetPrivKey;
                if (pk == null)
                    return Ok(new { success = false, message = "Cannot sign (wallet locked?)." });
                var sig = SignatureService.CreateSignature(tx.Hash, pk, account.PublicKey);
                if (sig == "ERROR")
                    return Ok(new { success = false, message = "Signature failed." });
                tx.Signature = sig;

                var (broadcastOk, json) = await PrivacyApiHelper.BroadcastVerifiedPrivateTxAsync(tx);
                return Ok(new { success = broadcastOk, hash = tx.Hash, type = "VFX_SHIELD", amount, fromAddress, zfxAddress, detail = json });
            }
            catch (Exception ex) { return StatusCode(500, new { success = false, message = ex.Message }); }
        }

        /// <summary>
        /// Unshield VFX: shielded → transparent (Z→T).
        /// Password defaults to empty string when not supplied.
        /// </summary>
        [HttpGet("api/privacy/unshield/{zfxAddress}/{toAddress}/{amount}")]
        public async Task<IActionResult> UnshieldVFX(string zfxAddress, string toAddress, decimal amount, [FromQuery] string? password = null)
        {
            try
            {
                if (!AddressValidateUtility.ValidateAddress(toAddress))
                    return Ok(new { success = false, message = $"Invalid transparent to-address: {toAddress}" });

                var w = ShieldedWalletService.FindByZfxAddress(zfxAddress);
                if (w == null)
                    return Ok(new { success = false, message = "No shielded wallet row for this zfx address." });
                if (!PrivacyApiHelper.TryGetKeyMaterial(w, password, out var keys, out var kmErr))
                    return Ok(new { success = false, message = kmErr ?? "Cannot unwrap keys." });

                var fee = Globals.PrivateTxFixedFee;
                if (!CommitmentSelectionService.TrySelectInputs(
                        w.UnspentCommitments ?? (IReadOnlyList<UnspentCommitment>)Array.Empty<UnspentCommitment>(),
                        amount + fee,
                        out var inputs,
                        out _,
                        out var selErr))
                    return Ok(new { success = false, message = selErr ?? "Input selection failed." });

                var ts = TimeUtil.GetTime();
                if (!VfxPrivateTransactionBuilder.TryBuildUnshield(
                        inputs,
                        amount,
                        toAddress,
                        keys,
                        ts,
                        out var tx,
                        out var buildErr,
                        DbContext.DB_Privacy))
                    return Ok(new { success = false, message = buildErr ?? "Failed to build unshield TX." });

                var (broadcastOk, json) = await PrivacyApiHelper.BroadcastVerifiedPrivateTxAsync(tx!);
                return Ok(new { success = broadcastOk, hash = tx!.Hash, type = "VFX_UNSHIELD", amount, zfxAddress, toAddress, detail = json });
            }
            catch (Exception ex) { return StatusCode(500, new { success = false, message = ex.Message }); }
        }

        /// <summary>
        /// Private transfer VFX: shielded → shielded (Z→Z).
        /// Password defaults to empty string when not supplied.
        /// </summary>
        [HttpGet("api/privacy/transfer/{fromZfxAddress}/{toZfxAddress}/{amount}")]
        public async Task<IActionResult> PrivateTransferVFX(string fromZfxAddress, string toZfxAddress, decimal amount, [FromQuery] string? password = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(toZfxAddress) || !toZfxAddress.StartsWith("zfx_"))
                    return Ok(new { success = false, message = "Invalid recipient zfx_ address." });

                var w = ShieldedWalletService.FindByZfxAddress(fromZfxAddress);
                if (w == null)
                    return Ok(new { success = false, message = "No shielded wallet row for this zfx address." });
                if (!PrivacyApiHelper.TryGetKeyMaterial(w, password, out var keys, out var kmErr))
                    return Ok(new { success = false, message = kmErr ?? "Cannot unwrap keys." });

                var fee = Globals.PrivateTxFixedFee;
                if (!CommitmentSelectionService.TrySelectInputs(
                        w.UnspentCommitments ?? (IReadOnlyList<UnspentCommitment>)Array.Empty<UnspentCommitment>(),
                        amount + fee,
                        out var inputs,
                        out _,
                        out var selErr))
                    return Ok(new { success = false, message = selErr ?? "Input selection failed." });

                var ts = TimeUtil.GetTime();
                if (!VfxPrivateTransactionBuilder.TryBuildPrivateTransfer(
                        inputs,
                        amount,
                        toZfxAddress,
                        keys,
                        ts,
                        out var tx,
                        out var buildErr,
                        DbContext.DB_Privacy))
                    return Ok(new { success = false, message = buildErr ?? "Failed to build private transfer TX." });

                var (broadcastOk, json) = await PrivacyApiHelper.BroadcastVerifiedPrivateTxAsync(tx!);
                return Ok(new { success = broadcastOk, hash = tx!.Hash, type = "VFX_PRIVATE_TRANSFER", amount, fromZfxAddress, toZfxAddress, detail = json });
            }
            catch (Exception ex) { return StatusCode(500, new { success = false, message = ex.Message }); }
        }

        /// <summary>
        /// Scan blockchain for owned shielded VFX commitments.
        /// Password defaults to empty string when not supplied.
        /// </summary>
        [HttpGet("api/privacy/scan/{zfxAddress}")]
        public IActionResult ScanShieldedVFX(string zfxAddress, [FromQuery] string? password = null, [FromQuery] long? fromBlock = null, [FromQuery] long? toBlock = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(zfxAddress) || !zfxAddress.StartsWith("zfx_"))
                    return Ok(new { success = false, message = "Invalid zfx_ address." });

                var w = ShieldedWalletService.FindByZfxAddress(zfxAddress);
                if (w == null)
                    return Ok(new { success = false, message = "No shielded wallet row for this zfx address." });
                if (!PrivacyApiHelper.TryGetKeyMaterial(w, password, out var keys, out var kmErr))
                    return Ok(new { success = false, message = kmErr ?? "Cannot unwrap keys." });

                long scanFrom = fromBlock ?? w.LastScannedBlock;
                long scanTo = toBlock ?? Globals.LastBlock.Height;
                int blocksScanned = 0, txsScanned = 0, newNotes = 0;

                var merged = new HashSet<string>(StringComparer.Ordinal);
                foreach (var u in w.UnspentCommitments ?? new List<UnspentCommitment>())
                {
                    if (!string.IsNullOrEmpty(u.Commitment))
                        merged.Add(u.Commitment);
                }

                for (long h = scanFrom; h <= scanTo; h++)
                {
                    var block = BlockchainData.GetBlockByHeight(h);
                    if (block?.Transactions == null) continue;
                    blocksScanned++;
                    foreach (var tx in block.Transactions)
                    {
                        txsScanned++;
                        if (tx?.Data == null || !PrivateTxPayloadCodec.TryDecode(tx.Data, out var payload, out _))
                            continue;
                        if (payload?.Outs == null)
                            continue;
                        foreach (var o in payload.Outs)
                        {
                            if (string.IsNullOrWhiteSpace(o.EncryptedNoteB64))
                                continue;
                            byte[] enc;
                            try { enc = Convert.FromBase64String(o.EncryptedNoteB64); }
                            catch { continue; }
                            if (!ShieldedNoteEncryption.TryOpen(enc, keys.EncryptionPrivateKey32, out var plain, out _))
                                continue;
                            if (!ShieldedPlainNoteCodec.TryDeserializeUtf8(plain, out var note, out _) || note == null)
                                continue;
                            var c = o.CommitmentB64;
                            if (string.IsNullOrEmpty(c) || merged.Contains(c))
                                continue;
                            merged.Add(c);
                            newNotes++;
                            byte[] r32 = Array.Empty<byte>();
                            if (!string.IsNullOrEmpty(note.RandomnessB64))
                            {
                                try { r32 = Convert.FromBase64String(note.RandomnessB64); }
                                catch { /* ignore */ }
                            }
                            w.UnspentCommitments ??= new List<UnspentCommitment>();
                            w.UnspentCommitments.Add(new UnspentCommitment
                            {
                                Commitment = c,
                                AssetType = note.AssetType ?? "",
                                Amount = note.Amount,
                                Randomness = r32,
                                TreePosition = 0,
                                BlockHeight = block.Height,
                                IsSpent = false
                            });
                            var key = note.AssetType ?? "";
                            if (!w.ShieldedBalances.ContainsKey(key))
                                w.ShieldedBalances[key] = 0;
                            w.ShieldedBalances[key] += note.Amount;
                        }
                    }
                }
                w.LastScannedBlock = Math.Max(w.LastScannedBlock, scanTo);
                ShieldedWalletService.Upsert(w);

                return Ok(new { success = true, zfxAddress, blocksScanned, transactionsScanned = txsScanned, newNotesFound = newNotes, fromHeight = scanFrom, toHeight = scanTo });
            }
            catch (Exception ex) { return StatusCode(500, new { success = false, message = ex.Message }); }
        }

        /// <summary>
        /// Get PLONK native library status and capabilities.
        /// </summary>
        [HttpGet("api/privacy/plonkStatus")]
        public IActionResult GetPlonkStatus()
        {
            try
            {
                PLONKSetup.RefreshVerificationCapability();
                uint caps = 0;
                try { caps = PlonkNative.plonk_capabilities(); } catch { }

                return Ok(new
                {
                    success = true,
                    proofVerificationImplemented = PLONKSetup.IsProofVerificationImplemented,
                    proofProvingImplemented = PLONKSetup.IsProofProvingImplemented,
                    enforcePlonkProofsForZk = Globals.EnforcePlonkProofsForZk,
                    nativeCapabilities = caps,
                    paramsBytesMirrored = Globals.PLONKParamsFileSize
                });
            }
            catch (Exception ex) { return StatusCode(500, new { success = false, message = ex.Message }); }
        }

        /// <summary>
        /// Get the VFX shielded pool state.
        /// </summary>
        [HttpGet("api/privacy/poolState")]
        public IActionResult GetShieldedPoolState()
        {
            try
            {
                var pool = ShieldedPoolService.GetOrCreateState("VFX");
                return Ok(new
                {
                    success = true,
                    assetType = pool.AssetType,
                    totalCommitments = pool.TotalCommitments,
                    totalShieldedSupply = pool.TotalShieldedSupply,
                    currentMerkleRoot = pool.CurrentMerkleRoot,
                    lastUpdateHeight = pool.LastUpdateHeight
                });
            }
            catch (Exception ex) { return StatusCode(500, new { success = false, message = ex.Message }); }
        }

        // ── Embedded HTML ─────────────────────────────────────────────────────────
        private static string GetHtml() => @"<!DOCTYPE html>
<html lang='en'>
<head>
<meta charset='UTF-8'>
<meta name='viewport' content='width=device-width, initial-scale=1.0'>
<title>VFX Wallet</title>
<style>
*,*::before,*::after{box-sizing:border-box;margin:0;padding:0}
:root{
  --bg:#0d1117;--surface:#161b22;--surface2:#21262d;
  --border:#30363d;--border2:#21262d;
  --text:#e6edf3;--muted:#8b949e;
  --accent:#58a6ff;--accent-dark:#1f6feb;
  --green:#3fb950;--orange:#e3b341;--red:#f85149;--purple:#bc8cff;
}
body{background:var(--bg);color:var(--text);font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',system-ui,sans-serif;font-size:14px;line-height:1.5;min-height:100vh}
/* Header */
.hdr{background:var(--surface);border-bottom:1px solid var(--border);padding:0 24px;height:60px;display:flex;align-items:center;gap:14px;position:sticky;top:0;z-index:200}
.logo{display:flex;align-items:center;gap:10px;flex-shrink:0;cursor:pointer;text-decoration:none}
.logo-icon{width:34px;height:34px;background:linear-gradient(135deg,#3fb950,#58a6ff);border-radius:9px;display:flex;align-items:center;justify-content:center;font-weight:800;font-size:17px;color:#fff;flex-shrink:0}
.logo-text{font-size:17px;font-weight:700;color:var(--text);white-space:nowrap}
.logo-text span{color:var(--green)}
.addr-wrap{flex:1;display:flex;align-items:center;gap:10px;max-width:860px}
.addr-grp{display:flex;align-items:center;gap:6px;flex:1;min-width:0}
.addr-lbl{font-size:10px;font-weight:700;text-transform:uppercase;letter-spacing:.6px;color:var(--muted);flex-shrink:0}
.addr-sel{flex:1;background:var(--bg);border:1px solid var(--border);border-radius:8px;padding:8px 12px;color:var(--text);font-size:13px;font-family:'SF Mono','Fira Code',Consolas,monospace;outline:none;cursor:pointer;appearance:none;-webkit-appearance:none;background-image:url(""data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='12' height='12' fill='%238b949e' viewBox='0 0 16 16'%3E%3Cpath d='M7.247 11.14L2.451 5.658C1.885 5.013 2.345 4 3.204 4h9.592a1 1 0 0 1 .753 1.659l-4.796 5.48a1 1 0 0 1-1.506 0z'/%3E%3C/svg%3E"");background-repeat:no-repeat;background-position:right 10px center;padding-right:30px}
.addr-sel:focus{border-color:var(--accent)}
.icon-btn{background:var(--surface2);border:1px solid var(--border);color:var(--muted);width:34px;height:34px;border-radius:8px;cursor:pointer;font-size:15px;display:flex;align-items:center;justify-content:center;transition:all .2s;flex-shrink:0}
.icon-btn:hover{color:var(--text);border-color:var(--accent)}
.net-badge{background:rgba(63,185,80,.13);border:1px solid rgba(63,185,80,.3);color:var(--green);padding:4px 10px;border-radius:20px;font-size:11px;font-weight:700;text-transform:uppercase;letter-spacing:.5px;flex-shrink:0;margin-left:auto}
/* Tab bar */
.tab-bar{background:var(--surface);border-bottom:1px solid var(--border);padding:0 24px;display:flex;gap:2px;overflow-x:auto}
.tab-bar::-webkit-scrollbar{height:3px}.tab-bar::-webkit-scrollbar-thumb{background:var(--border)}
.tab-btn{padding:12px 18px;background:none;border:none;color:var(--muted);font-size:13px;font-weight:500;cursor:pointer;border-bottom:2px solid transparent;white-space:nowrap;transition:color .2s}
.tab-btn:hover{color:var(--text)}
.tab-btn.on{color:var(--accent);border-bottom-color:var(--accent)}
/* Main */
.main{max-width:1200px;margin:0 auto;padding:24px}
/* Panels */
.panel{display:none}.panel.on{display:block}
/* Balance hero */
.bal-hero{background:linear-gradient(135deg,#161b22 0%,#1a2233 100%);border:1px solid var(--border);border-radius:14px;padding:28px 32px;margin-bottom:20px;display:flex;align-items:center;justify-content:space-between;flex-wrap:wrap;gap:16px}
.bal-main{}
.bal-lbl{font-size:12px;font-weight:700;text-transform:uppercase;letter-spacing:.8px;color:var(--muted);margin-bottom:8px}
.bal-num{font-size:42px;font-weight:800;color:var(--text);font-variant-numeric:tabular-nums;letter-spacing:-1px}
.bal-num span{font-size:20px;color:var(--muted);font-weight:500;margin-left:6px}
.bal-locked{font-size:13px;color:var(--orange);margin-top:6px}
.bal-addr{font-family:'SF Mono','Fira Code',Consolas,monospace;font-size:11px;color:var(--muted);margin-top:8px;word-break:break-all;max-width:360px}
.send-btn{background:linear-gradient(135deg,var(--accent-dark),var(--accent));color:#fff;border:none;border-radius:10px;padding:12px 28px;font-size:14px;font-weight:600;cursor:pointer;transition:opacity .2s;white-space:nowrap}
.send-btn:hover{opacity:.85}
/* Stats row */
.stat-row{display:grid;grid-template-columns:repeat(auto-fill,minmax(170px,1fr));gap:12px;margin-bottom:24px}
.stat-card{background:var(--surface);border:1px solid var(--border);border-radius:10px;padding:16px 18px}
.stat-lbl{font-size:11px;font-weight:700;text-transform:uppercase;letter-spacing:.7px;color:var(--muted);margin-bottom:5px}
.stat-val{font-size:20px;font-weight:700;font-variant-numeric:tabular-nums}
.stat-val.acc{color:var(--accent)}.stat-val.grn{color:var(--green)}.stat-val.org{color:var(--orange)}
/* Section headers */
.sec-hdr{display:flex;align-items:center;justify-content:space-between;margin-bottom:12px}
.sec-ttl{font-size:15px;font-weight:700;color:var(--text)}
/* Table */
.tbl-wrap{background:var(--surface);border:1px solid var(--border);border-radius:12px;overflow:hidden;margin-bottom:24px}
.dtbl{width:100%;border-collapse:collapse}
.dtbl th{background:var(--surface2);padding:10px 16px;text-align:left;font-size:11px;font-weight:700;text-transform:uppercase;letter-spacing:.6px;color:var(--muted);border-bottom:1px solid var(--border)}
.dtbl td{padding:11px 16px;border-bottom:1px solid var(--border2);vertical-align:middle}
.dtbl tr:last-child td{border-bottom:none}
.dtbl tbody tr{transition:background .12s}
.dtbl tbody tr.clk{cursor:pointer}.dtbl tbody tr.clk:hover{background:var(--surface2)}
/* Token rows */
.tok-icon{width:28px;height:28px;background:linear-gradient(135deg,var(--accent-dark),var(--purple));border-radius:6px;display:flex;align-items:center;justify-content:center;font-size:11px;font-weight:700;color:#fff}
/* NFT grid */
.nft-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(220px,1fr));gap:16px;margin-bottom:24px}
.nft-card{background:var(--surface);border:1px solid var(--border);border-radius:12px;padding:20px;display:flex;flex-direction:column;gap:10px;transition:border-color .2s}
.nft-card:hover{border-color:var(--accent)}
.nft-icon{width:48px;height:48px;background:linear-gradient(135deg,var(--accent-dark),var(--purple));border-radius:10px;display:flex;align-items:center;justify-content:center;font-size:22px;margin-bottom:4px}
.nft-name{font-size:15px;font-weight:700;color:var(--text)}
.nft-uid{font-family:'SF Mono','Fira Code',Consolas,monospace;font-size:11px;color:var(--muted);word-break:break-all}
.nft-actions{display:flex;gap:8px;margin-top:4px}
.act-btn{flex:1;padding:8px;border-radius:7px;font-size:12px;font-weight:600;cursor:pointer;border:1px solid;text-align:center;transition:all .2s}
.act-btn.prim{background:rgba(88,166,255,.1);border-color:rgba(88,166,255,.3);color:var(--accent)}.act-btn.prim:hover{background:rgba(88,166,255,.2)}
.act-btn.sec{background:rgba(63,185,80,.1);border-color:rgba(63,185,80,.3);color:var(--green)}.act-btn.sec:hover{background:rgba(63,185,80,.2)}
/* Badges */
.badge{display:inline-block;padding:2px 8px;border-radius:4px;font-size:11px;font-weight:700;text-transform:uppercase}
.badge-nft{background:rgba(188,140,255,.1);color:var(--purple);border:1px solid rgba(188,140,255,.2)}
.badge-tok{background:rgba(88,166,255,.1);color:var(--accent);border:1px solid rgba(88,166,255,.2)}
.badge-ok{background:rgba(63,185,80,.13);color:var(--green)}
.badge-pend{background:rgba(227,179,65,.13);color:var(--orange)}
.badge-fail{background:rgba(248,81,73,.13);color:var(--red)}
code,.mono{font-family:'SF Mono','Fira Code',Consolas,monospace;font-size:12px}
.muted{color:var(--muted)}
.acc{color:var(--accent)}.grn{color:var(--green)}.org{color:var(--orange)}.red{color:var(--red)}
/* Empty state */
.empty{text-align:center;padding:60px 20px;color:var(--muted)}
.empty-icon{font-size:40px;margin-bottom:12px}
/* Loading */
.spin{display:inline-block;width:16px;height:16px;border:2px solid var(--border);border-top-color:var(--accent);border-radius:50%;animation:spin .7s linear infinite;vertical-align:middle;margin-right:6px}
@keyframes spin{to{transform:rotate(360deg)}}
.ld{text-align:center;padding:48px;color:var(--muted)}
/* vBTC cards */
.vbtc-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(300px,1fr));gap:16px;margin-bottom:24px}
.vbtc-card{background:var(--surface);border:1px solid var(--border);border-radius:12px;padding:20px;display:flex;flex-direction:column;gap:10px}
.vbtc-bal{font-size:28px;font-weight:800;color:var(--orange);font-variant-numeric:tabular-nums}
.vbtc-bal span{font-size:14px;color:var(--muted);font-weight:500;margin-left:4px}
.vbtc-row{display:flex;justify-content:space-between;align-items:center;font-size:12px}
.vbtc-row .k{color:var(--muted)}.vbtc-row .v{font-family:'SF Mono','Fira Code',Consolas,monospace;font-size:11px;word-break:break-all;text-align:right;max-width:200px}
/* BTC cards */
.btc-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(300px,1fr));gap:16px;margin-bottom:24px}
.btc-card{background:var(--surface);border:1px solid var(--border);border-radius:12px;padding:20px;display:flex;flex-direction:column;gap:10px}
.btc-bal{font-size:26px;font-weight:800;color:var(--orange);font-variant-numeric:tabular-nums}
.btc-bal span{font-size:13px;color:var(--muted);font-weight:500;margin-left:4px}
/* TX history */
.dir-in{color:var(--green)}.dir-out{color:var(--red)}
/* Modal */
.overlay{display:none;position:fixed;inset:0;background:rgba(0,0,0,.6);z-index:300;align-items:center;justify-content:center;backdrop-filter:blur(2px)}
.overlay.on{display:flex}
.modal{background:var(--surface);border:1px solid var(--border);border-radius:14px;padding:28px;width:100%;max-width:480px;max-height:90vh;overflow-y:auto}
.modal-hdr{display:flex;align-items:center;justify-content:space-between;margin-bottom:22px;padding-bottom:16px;border-bottom:1px solid var(--border)}
.modal-ttl{font-size:18px;font-weight:700}
.modal-close{background:none;border:none;color:var(--muted);font-size:22px;cursor:pointer;line-height:1;padding:2px 6px;border-radius:6px}
.modal-close:hover{color:var(--text);background:var(--surface2)}
.form-grp{display:flex;flex-direction:column;gap:6px;margin-bottom:16px}
.form-grp label{font-size:12px;font-weight:700;text-transform:uppercase;letter-spacing:.6px;color:var(--muted)}
.form-inp{background:var(--bg);border:1px solid var(--border);border-radius:8px;padding:10px 14px;color:var(--text);font-size:13px;outline:none;width:100%;transition:border-color .2s}
.form-inp:focus{border-color:var(--accent)}
.form-inp::placeholder{color:var(--muted)}
.form-inp:read-only{color:var(--muted);cursor:default}
.modal-foot{display:flex;gap:10px;margin-top:8px}
.btn-prim{flex:1;background:var(--accent-dark);color:#fff;border:none;border-radius:8px;padding:11px;font-size:14px;font-weight:600;cursor:pointer;transition:background .2s}
.btn-prim:hover{background:var(--accent)}
.btn-prim:disabled{opacity:.5;cursor:not-allowed}
.btn-sec{background:var(--surface2);color:var(--text);border:1px solid var(--border);border-radius:8px;padding:11px 18px;font-size:14px;cursor:pointer;transition:background .2s}
.btn-sec:hover{background:var(--border)}
.msg{padding:12px 14px;border-radius:8px;font-size:13px;margin-top:12px;display:none}
.msg.on{display:block}
.msg.ok{background:rgba(63,185,80,.1);border:1px solid rgba(63,185,80,.2);color:var(--green)}
.msg.err{background:rgba(248,81,73,.1);border:1px solid rgba(248,81,73,.2);color:var(--red)}
.tx-wrap{overflow-x:auto}
@media(max-width:768px){.hdr{padding:0 14px;flex-wrap:wrap;height:auto;padding-top:12px;padding-bottom:12px}.main{padding:14px}.bal-hero{padding:20px}.bal-num{font-size:32px}.logo-text{display:none}.net-badge{display:none}}
</style>
</head>
<body>
<header class='hdr'>
  <a class='logo' href='/explorer'>
    <div class='logo-icon'>W</div>
    <div class='logo-text'>VFX <span>Wallet</span></div>
  </a>
  <div class='addr-wrap'>
    <div class='addr-grp'>
      <span class='addr-lbl'>VFX</span>
      <select id='addr-sel' class='addr-sel' onchange='onAddrChange()'><option value=''>Loading...</option></select>
    </div>
    <div class='addr-grp'>
      <span class='addr-lbl'>BTC</span>
      <select id='btc-sel' class='addr-sel' onchange='onBtcChange()'><option value=''>Loading...</option></select>
    </div>
    <button class='icon-btn' onclick='refreshAll()' title='Refresh'>&#8635;</button>
  </div>
  <div id='nb' class='net-badge'>Network</div>
</header>
<nav class='tab-bar'>
  <button class='tab-btn on' onclick='switchTab(""overview"",this)'>Overview</button>
  <button class='tab-btn' onclick='switchTab(""nfts"",this)'>NFTs</button>
  <button class='tab-btn' onclick='switchTab(""vbtc"",this)'>vBTC</button>
  <button class='tab-btn' onclick='switchTab(""btc"",this)'>Bitcoin</button>
  <button class='tab-btn' onclick='switchTab(""history"",this)'>History</button>
  <button class='tab-btn' onclick='switchTab(""privacy"",this)'>&#128274; Privacy</button>
</nav>
<main class='main'>
  <!-- Overview -->
  <div id='p-overview' class='panel on'>
    <div id='bal-hero' class='bal-hero'>
      <div class='bal-main'>
        <div class='bal-lbl'>Available Balance</div>
        <div class='bal-num' id='bal-num'>-- <span>VFX</span></div>
        <div class='bal-locked' id='bal-locked'></div>
        <div class='bal-addr' id='bal-addr'></div>
      </div>
      <button class='send-btn' onclick='openSendVFX()'>&#8594; Send VFX</button>
    </div>
    <div id='tok-section'>
      <div class='sec-hdr'><span class='sec-ttl'>Token Balances</span></div>
      <div id='tok-content'><div class='ld'><span class='spin'></span>Loading tokens...</div></div>
    </div>
  </div>
  <!-- NFTs -->
  <div id='p-nfts' class='panel'>
    <div class='sec-hdr'><span class='sec-ttl'>NFTs & Smart Contracts</span></div>
    <div id='nft-content'><div class='ld'><span class='spin'></span>Loading NFTs...</div></div>
  </div>
  <!-- vBTC -->
  <div id='p-vbtc' class='panel'>
    <div class='sec-hdr'><span class='sec-ttl'>vBTC Contracts</span></div>
    <div id='vbtc-content'><div class='ld'><span class='spin'></span>Loading vBTC...</div></div>
  </div>
  <!-- Bitcoin -->
  <div id='p-btc' class='panel'>
    <div class='sec-hdr'><span class='sec-ttl'>Bitcoin Accounts</span></div>
    <div id='btc-content'><div class='ld'><span class='spin'></span>Loading BTC accounts...</div></div>
  </div>
  <!-- History -->
  <div id='p-history' class='panel'>
    <div class='sec-hdr'><span class='sec-ttl'>Transaction History</span></div>
    <div id='hist-content'><div class='ld'><span class='spin'></span>Loading transactions...</div></div>
  </div>
  <!-- Privacy -->
  <div id='p-privacy' class='panel'>
    <div id='priv-hero' class='bal-hero' style='background:linear-gradient(135deg,#161b22 0%,#1a1833 100%)'>
      <div class='bal-main'>
        <div class='bal-lbl'>&#128274; Shielded VFX Balance</div>
        <div class='bal-num' id='priv-bal-num'>-- <span>VFX</span></div>
        <div id='priv-notes' class='muted' style='font-size:12px;margin-top:4px'></div>
        <div id='priv-zfx-addr' class='bal-addr'></div>
      </div>
      <div style='display:flex;flex-direction:column;gap:8px'>
        <button class='send-btn' onclick='openCreateZfx()' style='background:linear-gradient(135deg,#6e40c9,#bc8cff)'>+ Create Shielded Address</button>
        <button class='send-btn' onclick='openShield()'>&#8595; Shield VFX</button>
      </div>
    </div>
    <div id='priv-zfx-sel-wrap' style='margin-bottom:16px;display:none'>
      <div class='form-grp'>
        <label>Shielded Address</label>
        <select id='priv-zfx-sel' class='addr-sel' onchange='onZfxChange()' style='max-width:600px'><option value=''>No shielded addresses</option></select>
      </div>
    </div>
    <div id='priv-actions' style='display:none;margin-bottom:20px'>
      <div class='nft-actions'>
        <button class='act-btn prim' onclick='openUnshield()'>&#8593; Unshield</button>
        <button class='act-btn prim' onclick='openPrivTransfer()'>&#8596; Private Transfer</button>
        <button class='act-btn sec' onclick='doScanZfx()'>&#128269; Scan for Notes</button>
      </div>
    </div>
    <div class='sec-hdr'><span class='sec-ttl'>System Status</span></div>
    <div id='priv-status-content'><div class='ld'><span class='spin'></span>Loading privacy status...</div></div>
  </div>
</main>

<!-- Send VFX Modal -->
<div class='overlay' id='send-overlay'>
  <div class='modal'>
    <div class='modal-hdr'>
      <div class='modal-ttl'>Send VFX</div>
      <button class='modal-close' onclick='closeSend()'>&#215;</button>
    </div>
    <div class='form-grp'>
      <label>From</label>
      <input class='form-inp' id='s-from' type='text' readonly>
    </div>
    <div class='form-grp'>
      <label>To Address</label>
      <input class='form-inp' id='s-to' type='text' placeholder='Enter destination VFX address...'>
    </div>
    <div class='form-grp'>
      <label>Amount (VFX)</label>
      <input class='form-inp' id='s-amount' type='text' placeholder='0.00000000'>
    </div>
    <div class='msg' id='s-msg'></div>
    <div class='modal-foot'>
      <button class='btn-sec' onclick='closeSend()'>Cancel</button>
      <button class='btn-prim' id='s-btn' onclick='doSendVFX()'>Send</button>
    </div>
  </div>
</div>

<!-- Transfer NFT Modal -->
<div class='overlay' id='nft-overlay'>
  <div class='modal'>
    <div class='modal-hdr'>
      <div class='modal-ttl'>Transfer NFT</div>
      <button class='modal-close' onclick='closeNFT()'>&#215;</button>
    </div>
    <input type='hidden' id='nft-scuid'>
    <div class='form-grp'>
      <label>NFT</label>
      <input class='form-inp' id='nft-name-disp' type='text' readonly>
    </div>
    <div class='form-grp'>
      <label>To Address</label>
      <input class='form-inp' id='nft-to' type='text' placeholder='Enter destination VFX address...'>
    </div>
    <div class='msg' id='nft-msg'></div>
    <div class='modal-foot'>
      <button class='btn-sec' onclick='closeNFT()'>Cancel</button>
      <button class='btn-prim' id='nft-btn' onclick='doTransferNFT()'>Transfer</button>
    </div>
  </div>
</div>

<!-- Transfer Token Modal -->
<div class='overlay' id='tok-overlay'>
  <div class='modal'>
    <div class='modal-hdr'>
      <div class='modal-ttl'>Send Token</div>
      <button class='modal-close' onclick='closeTok()'>&#215;</button>
    </div>
    <input type='hidden' id='tok-scuid'>
    <input type='hidden' id='tok-from-addr'>
    <div class='form-grp'>
      <label>Token</label>
      <input class='form-inp' id='tok-name-disp' type='text' readonly>
    </div>
    <div class='form-grp'>
      <label>To Address</label>
      <input class='form-inp' id='tok-to' type='text' placeholder='Enter destination VFX address...'>
    </div>
    <div class='form-grp'>
      <label>Amount</label>
      <input class='form-inp' id='tok-amount' type='text' placeholder='0.00'>
    </div>
    <div class='msg' id='tok-msg'></div>
    <div class='modal-foot'>
      <button class='btn-sec' onclick='closeTok()'>Cancel</button>
      <button class='btn-prim' id='tok-btn' onclick='doTransferToken()'>Send</button>
    </div>
  </div>
</div>

<!-- vBTC Withdrawal Modal -->
<div class='overlay' id='wd-overlay'>
  <div class='modal'>
    <div class='modal-hdr'>
      <div class='modal-ttl'>&#8383; vBTC Withdrawal</div>
      <button class='modal-close' onclick='closeWD()'>&#215;</button>
    </div>
    <input type='hidden' id='wd-scuid'>
    <input type='hidden' id='wd-owner'>
    <div class='form-grp'>
      <label>Contract</label>
      <input class='form-inp' id='wd-contract-disp' type='text' readonly>
    </div>
    <div class='form-grp'>
      <label>Destination BTC Address</label>
      <input class='form-inp' id='wd-btcaddr' type='text' placeholder='Enter Bitcoin address to receive BTC...'>
    </div>
    <div class='form-grp'>
      <label>Amount (BTC)</label>
      <input class='form-inp' id='wd-amount' type='text' placeholder='0.00000000'>
    </div>
    <div class='form-grp'>
      <label>Fee Rate (sat/vbyte)</label>
      <input class='form-inp' id='wd-fee' type='text' placeholder='10' value='10'>
    </div>
    <div class='msg' id='wd-msg'></div>
    <div class='modal-foot'>
      <button class='btn-sec' onclick='closeWD()'>Cancel</button>
      <button class='btn-prim' id='wd-btn' onclick='doWDRequest()'>Request Withdrawal</button>
    </div>
  </div>
</div>

<!-- vBTC Complete Withdrawal Modal -->
<div class='overlay' id='wdc-overlay'>
  <div class='modal'>
    <div class='modal-hdr'>
      <div class='modal-ttl'>&#8383; Complete Withdrawal</div>
      <button class='modal-close' onclick='closeWDC()'>&#215;</button>
    </div>
    <input type='hidden' id='wdc-scuid'>
    <input type='hidden' id='wdc-hash'>
    <div class='form-grp'>
      <label>Contract</label>
      <input class='form-inp' id='wdc-contract-disp' type='text' readonly>
    </div>
    <div class='form-grp'>
      <label>Pending Amount</label>
      <input class='form-inp' id='wdc-amount-disp' type='text' readonly>
    </div>
    <div class='form-grp'>
      <label>Destination</label>
      <input class='form-inp' id='wdc-dest-disp' type='text' readonly>
    </div>
    <div style='padding:12px;background:rgba(227,179,65,.1);border:1px solid rgba(227,179,65,.2);border-radius:8px;font-size:13px;color:var(--orange);margin-bottom:12px'>
      This will coordinate a FROST MPC signing ceremony with validators and broadcast the Bitcoin transaction.
    </div>
    <div class='msg' id='wdc-msg'></div>
    <div class='modal-foot'>
      <button class='btn-sec' onclick='closeWDC()'>Cancel</button>
      <button class='btn-prim' id='wdc-btn' onclick='doWDComplete()'>Complete Withdrawal</button>
    </div>
  </div>
</div>

<!-- Send vBTC Modal -->
<div class='overlay' id='vbtc-tx-overlay'>
  <div class='modal'>
    <div class='modal-hdr'>
      <div class='modal-ttl'>&#8383; Send vBTC</div>
      <button class='modal-close' onclick='closeVBTCTx()'>&#215;</button>
    </div>
    <input type='hidden' id='vbtc-tx-scuid'>
    <input type='hidden' id='vbtc-tx-from'>
    <div class='form-grp'>
      <label>Contract</label>
      <input class='form-inp' id='vbtc-tx-contract-disp' type='text' readonly>
    </div>
    <div class='form-grp'>
      <label>From</label>
      <input class='form-inp' id='vbtc-tx-from-disp' type='text' readonly>
    </div>
    <div class='form-grp'>
      <label>To (VFX Address)</label>
      <input class='form-inp' id='vbtc-tx-to' type='text' placeholder='Enter destination VFX address...'>
    </div>
    <div class='form-grp'>
      <label>Amount (vBTC)</label>
      <input class='form-inp' id='vbtc-tx-amount' type='text' placeholder='0.00000000'>
    </div>
    <div class='msg' id='vbtc-tx-msg'></div>
    <div class='modal-foot'>
      <button class='btn-sec' onclick='closeVBTCTx()'>Cancel</button>
      <button class='btn-prim' id='vbtc-tx-btn' onclick='doVBTCTransfer()'>Send vBTC</button>
    </div>
  </div>
</div>

<!-- Create Shielded Address Modal -->
<div class='overlay' id='czfx-overlay'>
  <div class='modal'>
    <div class='modal-hdr'>
      <div class='modal-ttl'>&#128274; Create Shielded Address</div>
      <button class='modal-close' onclick='closeCZfx()'>&#215;</button>
    </div>
    <div class='form-grp'>
      <label>Transparent VFX Address</label>
      <select id='czfx-addr' class='form-inp' style='cursor:pointer'></select>
    </div>
    <div class='form-grp'>
      <label>Password (min 8 chars, protects spending key)</label>
      <input class='form-inp' id='czfx-pwd' type='password' placeholder='Enter password...'>
    </div>
    <div style='padding:12px;background:rgba(188,140,255,.1);border:1px solid rgba(188,140,255,.2);border-radius:8px;font-size:13px;color:var(--purple);margin-bottom:12px'>
      This derives a <code>zfx_</code> shielded address from your account. The password encrypts your spending key at rest.
    </div>
    <div class='msg' id='czfx-msg'></div>
    <div class='modal-foot'>
      <button class='btn-sec' onclick='closeCZfx()'>Cancel</button>
      <button class='btn-prim' id='czfx-btn' onclick='doCreateZfx()'>Create</button>
    </div>
  </div>
</div>

<!-- Shield VFX Modal -->
<div class='overlay' id='shield-overlay'>
  <div class='modal'>
    <div class='modal-hdr'>
      <div class='modal-ttl'>&#8595; Shield VFX (T&#8594;Z)</div>
      <button class='modal-close' onclick='closeShield()'>&#215;</button>
    </div>
    <div class='form-grp'>
      <label>From Transparent Address</label>
      <select id='sh-from' class='form-inp' style='cursor:pointer'></select>
    </div>
    <div class='form-grp'>
      <label>To Shielded Address (zfx_)</label>
      <input class='form-inp' id='sh-zfx' type='text' placeholder='zfx_...'>
    </div>
    <div class='form-grp'>
      <label>Amount (VFX)</label>
      <input class='form-inp' id='sh-amount' type='text' placeholder='0.00000000'>
    </div>
    <div class='msg' id='sh-msg'></div>
    <div class='modal-foot'>
      <button class='btn-sec' onclick='closeShield()'>Cancel</button>
      <button class='btn-prim' id='sh-btn' onclick='doShield()'>Shield</button>
    </div>
  </div>
</div>

<!-- Unshield VFX Modal -->
<div class='overlay' id='unshield-overlay'>
  <div class='modal'>
    <div class='modal-hdr'>
      <div class='modal-ttl'>&#8593; Unshield VFX (Z&#8594;T)</div>
      <button class='modal-close' onclick='closeUnshield()'>&#215;</button>
    </div>
    <div class='form-grp'>
      <label>From Shielded Address</label>
      <input class='form-inp' id='ush-zfx' type='text' readonly>
    </div>
    <div class='form-grp'>
      <label>To Transparent Address</label>
      <select id='ush-to' class='form-inp' style='cursor:pointer'></select>
    </div>
    <div class='form-grp'>
      <label>Amount (VFX)</label>
      <input class='form-inp' id='ush-amount' type='text' placeholder='0.00000000'>
    </div>
    <div class='form-grp'>
      <label>Spending Password</label>
      <input class='form-inp' id='ush-pwd' type='password' placeholder='Password used when creating shielded address'>
    </div>
    <div class='msg' id='ush-msg'></div>
    <div class='modal-foot'>
      <button class='btn-sec' onclick='closeUnshield()'>Cancel</button>
      <button class='btn-prim' id='ush-btn' onclick='doUnshield()'>Unshield</button>
    </div>
  </div>
</div>

<!-- Private Transfer Modal -->
<div class='overlay' id='ptx-overlay'>
  <div class='modal'>
    <div class='modal-hdr'>
      <div class='modal-ttl'>&#8596; Private Transfer (Z&#8594;Z)</div>
      <button class='modal-close' onclick='closePTx()'>&#215;</button>
    </div>
    <div class='form-grp'>
      <label>From Shielded Address</label>
      <input class='form-inp' id='ptx-from' type='text' readonly>
    </div>
    <div class='form-grp'>
      <label>To Shielded Address (zfx_)</label>
      <input class='form-inp' id='ptx-to' type='text' placeholder='zfx_...'>
    </div>
    <div class='form-grp'>
      <label>Amount (VFX)</label>
      <input class='form-inp' id='ptx-amount' type='text' placeholder='0.00000000'>
    </div>
    <div class='form-grp'>
      <label>Spending Password</label>
      <input class='form-inp' id='ptx-pwd' type='password' placeholder='Password used when creating shielded address'>
    </div>
    <div class='msg' id='ptx-msg'></div>
    <div class='modal-foot'>
      <button class='btn-sec' onclick='closePTx()'>Cancel</button>
      <button class='btn-prim' id='ptx-btn' onclick='doPrivTransfer()'>Send</button>
    </div>
  </div>
</div>

<!-- Send BTC Modal -->
<div class='overlay' id='btc-overlay'>
  <div class='modal'>
    <div class='modal-hdr'>
      <div class='modal-ttl'>&#8383; Send Bitcoin</div>
      <button class='modal-close' onclick='closeBtcSend()'>&#215;</button>
    </div>
    <div class='form-grp'>
      <label>From (BTC Address)</label>
      <input class='form-inp' id='btc-s-from' type='text' readonly>
    </div>
    <div class='form-grp'>
      <label>To (BTC Address)</label>
      <input class='form-inp' id='btc-s-to' type='text' placeholder='Enter destination Bitcoin address...'>
    </div>
    <div class='form-grp'>
      <label>Amount (BTC)</label>
      <input class='form-inp' id='btc-s-amount' type='text' placeholder='0.00000000'>
    </div>
    <div class='form-grp'>
      <label>Fee Rate (sat/vbyte)</label>
      <input class='form-inp' id='btc-s-fee' type='text' placeholder='10' value='10'>
    </div>
    <div class='msg' id='btc-s-msg'></div>
    <div class='modal-foot'>
      <button class='btn-sec' onclick='closeBtcSend()'>Cancel</button>
      <button class='btn-prim' id='btc-s-btn' onclick='doSendBTC()'>Send BTC</button>
    </div>
  </div>
</div>

<script>
(function(){
var accounts=[],btcAccounts=[],selAddr=null,selBtcAddr=null,activeTab='overview',tabLoaded={};

document.addEventListener('DOMContentLoaded',function(){
  loadAccounts();
  loadBTC();
  setNB();
});

function setNB(){
  fetch('/explorer/api/stats').then(function(r){return r.json();}).then(function(d){
    el('nb').textContent=d.isTestNet?'Testnet':'Mainnet';
  }).catch(function(){});
}

/* ---- Accounts ---- */
function loadAccounts(){
  fetch('/wallet/api/accounts').then(function(r){return r.json();}).then(function(data){
    accounts=data;
    var sel=el('addr-sel');
    sel.innerHTML='';
    if(!data||!data.length){
      sel.innerHTML='<option value="">No accounts found</option>';
      return;
    }
    data.forEach(function(a){
      var opt=document.createElement('option');
      opt.value=a.address;
      opt.textContent=a.address.substring(0,20)+'... | '+fmtBal(a.balance)+' VFX'+(a.adnr?' | '+a.adnr:'');
      sel.appendChild(opt);
    });
    selAddr=data[0].address;
    renderOverview(data[0]);
    tabLoaded={overview:true};
  }).catch(function(){
    el('addr-sel').innerHTML='<option value="">Error loading accounts</option>';
  });
}

window.onAddrChange=function(){
  selAddr=el('addr-sel').value;
  tabLoaded={};
  var acc=accounts.find(function(a){return a.address===selAddr;});
  if(acc)renderOverview(acc);
  if(activeTab!=='overview')loadTab(activeTab);
};

window.refreshAll=function(){
  tabLoaded={};
  loadAccounts();
  loadBTC();
};

/* ---- Tab switching ---- */
window.switchTab=function(tab,btn){
  activeTab=tab;
  document.querySelectorAll('.tab-btn').forEach(function(b){b.classList.remove('on');});
  if(btn)btn.classList.add('on');
  document.querySelectorAll('.panel').forEach(function(p){p.classList.remove('on');});
  el('p-'+tab).classList.add('on');
  if(!tabLoaded[tab])loadTab(tab);
};

function loadTab(tab){
  tabLoaded[tab]=true;
  if(tab==='nfts')loadNFTs();
  else if(tab==='vbtc')loadVBTC();
  else if(tab==='btc')loadBTC();
  else if(tab==='history')loadHistory();
  else if(tab==='privacy')loadPrivacy();
}

/* ---- Overview ---- */
function renderOverview(acc){
  el('bal-num').innerHTML=fmtBal(acc.balance)+'<span>VFX</span>';
  el('bal-addr').textContent=acc.address+(acc.adnr?' ('+acc.adnr+')':'');
  if(acc.lockedBalance&&acc.lockedBalance>0){
    el('bal-locked').textContent='Locked: '+fmtBal(acc.lockedBalance)+' VFX';
  }else{
    el('bal-locked').textContent='';
  }
  renderTokens(acc.tokens||[]);
}

function renderTokens(tokens){
  var c=el('tok-content');
  if(!tokens||!tokens.length){
    c.innerHTML='<div class=""empty""><div class=""empty-icon"">&#128296;</div><div>No tokens found for this address</div></div>';
    return;
  }
  var rows=tokens.map(function(t){
    var ticker=t.ticker||t.name||'?';
    var initials=ticker.substring(0,2).toUpperCase();
    return '<tr>'+
      '<td><div style=""display:flex;align-items:center;gap:10px""><div class=""tok-icon"">'+esc(initials)+'</div><div><div style=""font-weight:600"">'+esc(t.name)+'</div><div class=""muted"" style=""font-size:11px"">'+esc(t.ticker)+'</div></div></div></td>'+
      '<td class=""grn"" style=""font-weight:700;font-variant-numeric:tabular-nums"">'+fmtTok(t.balance,t.decimals)+'</td>'+
      '<td class=""muted"" style=""font-variant-numeric:tabular-nums"">'+fmtTok(t.lockedBalance,t.decimals)+'</td>'+
      '<td><button class=""act-btn prim"" onclick=""openSendToken(\''+esc(t.scUID)+'\',\''+esc(t.name)+'\',\''+esc(t.ticker)+'\')"">&rarr; Send</button></td>'+
      '</tr>';
  }).join('');
  c.innerHTML='<div class=""tbl-wrap""><table class=""dtbl""><thead><tr><th>Token</th><th>Balance</th><th>Locked</th><th></th></tr></thead><tbody>'+rows+'</tbody></table></div>';
}

/* ---- NFTs ---- */
function loadNFTs(){
  if(!selAddr)return;
  el('nft-content').innerHTML='<div class=""ld""><span class=""spin""></span>Loading NFTs...</div>';
  fetch('/wallet/api/nfts/'+encodeURIComponent(selAddr)).then(function(r){return r.json();}).then(renderNFTs)
    .catch(function(){el('nft-content').innerHTML='<div class=""empty""><div class=""empty-icon"">&#9888;</div><div>Failed to load NFTs</div></div>';});
}

function renderNFTs(nfts){
  if(!nfts||!nfts.length){
    el('nft-content').innerHTML='<div class=""empty""><div class=""empty-icon"">&#128444;</div><div>No NFTs found for this address</div></div>';
    return;
  }
  var cards=nfts.map(function(n){
    var icon=n.isToken?'&#128296;':'&#127760;';
    var badge=n.isToken?'<span class=""badge badge-tok"">Token</span>':'<span class=""badge badge-nft"">NFT</span>';
    var pubBadge=n.isPublished?'<span class=""badge badge-ok"" style=""font-size:10px"">Published</span>':'<span class=""badge badge-pend"" style=""font-size:10px"">Draft</span>';
    var uid=n.scUID?n.scUID.substring(0,20)+'...':'N/A';
    return '<div class=""nft-card"">'+
      '<div class=""nft-icon"">'+icon+'</div>'+
      '<div class=""nft-name"">'+esc(n.name||'Unnamed')+'</div>'+
      '<div style=""display:flex;gap:6px;flex-wrap:wrap"">'+badge+pubBadge+'</div>'+
      '<div class=""nft-uid"" title=""'+esc(n.scUID)+'"">' +uid+'</div>'+
      (n.minterName?'<div class=""muted"" style=""font-size:11px"">By: '+esc(n.minterName)+'</div>':'')+
      '<div class=""nft-actions"">'+
      '<button class=""act-btn prim"" onclick=""openTransferNFT(\''+esc(n.scUID)+'\',\''+esc(n.name||'Unnamed')+'\')"">&rarr; Transfer</button>'+
      '</div>'+
      '</div>';
  }).join('');
  el('nft-content').innerHTML='<div class=""nft-grid"">'+cards+'</div>';
}

/* ---- vBTC ---- */
function loadVBTC(){
  if(!selAddr)return;
  el('vbtc-content').innerHTML='<div class=""ld""><span class=""spin""></span>Loading vBTC...</div>';
  fetch('/wallet/api/vbtc/'+encodeURIComponent(selAddr)).then(function(r){return r.json();}).then(renderVBTC)
    .catch(function(){el('vbtc-content').innerHTML='<div class=""empty""><div class=""empty-icon"">&#9888;</div><div>Failed to load vBTC</div></div>';});
}

function renderVBTC(contracts){
  if(!contracts||!contracts.length){
    el('vbtc-content').innerHTML='<div class=""empty""><div class=""empty-icon"">&#8383;</div><div>No vBTC contracts found for this address</div></div>';
    return;
  }
  var cards=contracts.map(function(c){
    var statusCls=c.withdrawalStatus==='None'?'badge-ok':c.withdrawalStatus==='Pending'?'badge-pend':'badge-tok';
    var canRequest=c.balance>0&&(c.withdrawalStatus==='None'||c.withdrawalStatus==='Completed');
    var canComplete=c.withdrawalStatus==='Requested';
    var btns='<div class=""nft-actions"" style=""margin-top:6px"">';
    if(c.balance>0)btns+='<button class=""act-btn prim"" onclick=""openVBTCTx(\''+esc(c.scUID)+'\','+c.balance+')"">&rarr; Send vBTC</button>';
    if(canRequest)btns+='<button class=""act-btn prim"" onclick=""openWD(\''+esc(c.scUID)+'\',\''+esc(c.ownerAddress)+'\','+c.balance+')"">&darr; Withdraw</button>';
    if(canComplete)btns+='<button class=""act-btn sec"" onclick=""openWDC(\''+esc(c.scUID)+'\','+c.activeWithdrawalAmount+',\''+esc(c.activeWithdrawalDest||'')+'\')"">&check; Complete Withdrawal</button>';
    btns+='</div>';
    return '<div class=""vbtc-card"">'+
      '<div class=""muted"" style=""font-size:11px;font-family:monospace"">'+esc(c.scUID||'')+'</div>'+
      '<div class=""vbtc-bal"">'+fmtBal(c.balance)+'<span>vBTC</span></div>'+
      '<div class=""vbtc-row""><span class=""k"">BTC Deposit</span><span class=""v"">'+esc(c.depositAddress||'N/A')+'</span></div>'+
      '<div class=""vbtc-row""><span class=""k"">Withdrawal Status</span><span class=""badge '+statusCls+'"">' +esc(c.withdrawalStatus)+'</span></div>'+
      (c.activeWithdrawalAmount?'<div class=""vbtc-row""><span class=""k"">Pending Withdrawal</span><span class=""v org"">'+c.activeWithdrawalAmount+' BTC &rarr; '+esc(c.activeWithdrawalDest||'')+'</span></div>':'')+
      '<div class=""vbtc-row""><span class=""k"">Validators</span><span class=""v"">'+c.totalValidators+' (threshold: '+c.requiredThreshold+')</span></div>'+
      '<div class=""vbtc-row""><span class=""k"">Proof Block</span><span class=""v"">#'+c.proofBlockHeight+'</span></div>'+
      btns+
      '</div>';
  }).join('');
  el('vbtc-content').innerHTML='<div class=""vbtc-grid"">'+cards+'</div>';
}

/* ---- Bitcoin ---- */
function loadBTC(){
  el('btc-content').innerHTML='<div class=""ld""><span class=""spin""></span>Loading Bitcoin accounts...</div>';
  fetch('/wallet/api/btc').then(function(r){return r.json();}).then(function(accs){
    btcAccounts=accs||[];
    var sel=el('btc-sel');
    sel.innerHTML='';
    if(!btcAccounts.length){
      sel.innerHTML='<option value="">No BTC accounts</option>';
    }else{
      btcAccounts.forEach(function(a){
        var opt=document.createElement('option');
        opt.value=a.address;
        opt.textContent=a.address.substring(0,22)+'... | '+fmtBal(a.balance)+' BTC';
        sel.appendChild(opt);
      });
      if(!selBtcAddr)selBtcAddr=btcAccounts[0].address;
    }
    renderBTC(selBtcAddr?btcAccounts.filter(function(a){return a.address===selBtcAddr;}):btcAccounts);
  }).catch(function(){
    el('btc-content').innerHTML='<div class=""empty""><div class=""empty-icon"">&#9888;</div><div>Failed to load Bitcoin accounts</div></div>';
  });
}

window.onBtcChange=function(){
  selBtcAddr=el('btc-sel').value;
  renderBTC(selBtcAddr?btcAccounts.filter(function(a){return a.address===selBtcAddr;}):btcAccounts);
};

function renderBTC(accs){
  if(!accs||!accs.length){
    el('btc-content').innerHTML='<div class=""empty""><div class=""empty-icon"">&#8383;</div><div>No Bitcoin accounts found</div></div>';
    return;
  }
  var cards=accs.map(function(a){
    return '<div class=""btc-card"">'+
      '<div class=""muted"" style=""font-size:11px"">Bitcoin Address</div>'+
      '<div class=""btc-bal"">'+fmtBal(a.balance)+'<span>BTC</span></div>'+
      '<div style=""font-family:monospace;font-size:12px;word-break:break-all;color:var(--text)"">'+esc(a.address||'N/A')+'</div>'+
      (a.adnr?'<div class=""muted"" style=""font-size:12px"">'+esc(a.adnr)+'</div>':'')+
      '<div class=""nft-actions"" style=""margin-top:8px""><button class=""act-btn prim"" onclick=""openSendBTC(\''+esc(a.address)+'\')"">&rarr; Send BTC</button></div>'+
      '</div>';
  }).join('');
  el('btc-content').innerHTML='<div class=""btc-grid"">'+cards+'</div>';
}

/* ---- History ---- */
function loadHistory(){
  if(!selAddr)return;
  el('hist-content').innerHTML='<div class=""ld""><span class=""spin""></span>Loading transactions...</div>';
  fetch('/wallet/api/txs/'+encodeURIComponent(selAddr)).then(function(r){return r.json();}).then(renderHistory)
    .catch(function(){el('hist-content').innerHTML='<div class=""empty""><div class=""empty-icon"">&#9888;</div><div>Failed to load transactions</div></div>';});
}

function renderHistory(txs){
  if(!txs||!txs.length){
    el('hist-content').innerHTML='<div class=""empty""><div class=""empty-icon"">&#128196;</div><div>No transactions found for this address</div></div>';
    return;
  }
  var rows=txs.map(function(tx){
    var dir=tx.fromAddress===selAddr?'out':'in';
    var dirLbl=dir==='out'?'<span class=""dir-out"">&#8593; Out</span>':'<span class=""dir-in"">&#8595; In</span>';
    var peer=dir==='out'?tx.toAddress:tx.fromAddress;
    var hashShort=tx.hash?tx.hash.substring(0,12)+'...':'N/A';
    var sc=stCls(tx.transactionStatus);var sn=stNm(tx.transactionStatus);
    var tt=ttype(tx.transactionType);
    return '<tr>'+
      '<td><code class=""muted"">'+hashShort+'</code></td>'+
      '<td>'+dirLbl+'</td>'+
      '<td><span class=""badge badge-tok"" style=""font-size:10px"">'+tt+'</span></td>'+
      '<td><code class=""muted"" title=""'+esc(peer||'')+'"">'+shn(peer||'--',18)+'</code></td>'+
      '<td class=""'+(dir==='in'?'grn':'red')+'"" style=""font-weight:600;font-variant-numeric:tabular-nums"">'+(dir==='in'?'+':'-')+tx.amount+' VFX</td>'+
      '<td><span class=""badge '+sc+'"">'+sn+'</span></td>'+
      '<td class=""muted"">'+ago(tx.timestamp)+'</td>'+
      '</tr>';
  }).join('');
  el('hist-content').innerHTML='<div class=""tx-wrap""><div class=""tbl-wrap""><table class=""dtbl""><thead><tr><th>Hash</th><th>Dir</th><th>Type</th><th>Peer</th><th>Amount</th><th>Status</th><th>Age</th></tr></thead><tbody>'+rows+'</tbody></table></div></div>';
}

/* ---- Send VFX ---- */
window.openSendVFX=function(){
  el('s-from').value=selAddr||'';
  el('s-to').value='';
  el('s-amount').value='';
  hideMsg('s-msg');
  el('send-overlay').classList.add('on');
};
window.closeSend=function(){el('send-overlay').classList.remove('on');};

window.doSendVFX=function(){
  var from=el('s-from').value.trim();
  var to=el('s-to').value.trim();
  var amt=el('s-amount').value.trim();
  if(!from||!to||!amt){showMsg('s-msg','Please fill all fields.','err');return;}
  var btn=el('s-btn');
  btn.disabled=true;btn.textContent='Sending...';
  fetch('/wallet/api/send/vfx',{
    method:'POST',
    headers:{'Content-Type':'application/json'},
    body:JSON.stringify({From:from,To:to,Amount:amt})
  }).then(function(r){return r.json();}).then(function(d){
    btn.disabled=false;btn.textContent='Send';
    if(d.success){
      showMsg('s-msg','Transaction sent successfully!','ok');
      setTimeout(function(){closeSend();tabLoaded.history=false;if(activeTab==='history')loadHistory();loadAccounts();},2000);
    }else{
      showMsg('s-msg',d.message||'Send failed.','err');
    }
  }).catch(function(){
    btn.disabled=false;btn.textContent='Send';
    showMsg('s-msg','Request failed. Please try again.','err');
  });
};

/* ---- Transfer NFT ---- */
window.openTransferNFT=function(scUID,name){
  el('nft-scuid').value=scUID;
  el('nft-name-disp').value=name+'   ('+scUID.substring(0,16)+'...)';
  el('nft-to').value='';
  hideMsg('nft-msg');
  el('nft-overlay').classList.add('on');
};
window.closeNFT=function(){el('nft-overlay').classList.remove('on');};

window.doTransferNFT=function(){
  var scUID=el('nft-scuid').value;
  var to=el('nft-to').value.trim();
  if(!to){showMsg('nft-msg','Please enter a destination address.','err');return;}
  var btn=el('nft-btn');
  btn.disabled=true;btn.textContent='Transferring...';
  fetch('/scapi/smartcontracts/TransferNFT/'+encodeURIComponent(scUID)+'/'+encodeURIComponent(to))
    .then(function(r){
      if(r.status===403)throw new Error('API must be enabled and unlocked to transfer NFTs. Use /api/v1/UnlockWallet/{password} first.');
      return r.text();
    })
    .then(function(t){
      btn.disabled=false;btn.textContent='Transfer';
      var ok=t.includes('Success')||t.includes('success');
      showMsg('nft-msg',t,ok?'ok':'err');
      if(ok){setTimeout(function(){closeNFT();tabLoaded.nfts=false;loadNFTs();},2000);}
    })
    .catch(function(e){
      btn.disabled=false;btn.textContent='Transfer';
      showMsg('nft-msg',e.message||'Transfer failed.','err');
    });
};

/* ---- Transfer Token ---- */
window.openSendToken=function(scUID,name,ticker){
  el('tok-scuid').value=scUID;
  el('tok-from-addr').value=selAddr||'';
  el('tok-name-disp').value=name+' ('+ticker+')';
  el('tok-to').value='';
  el('tok-amount').value='';
  hideMsg('tok-msg');
  el('tok-overlay').classList.add('on');
};
window.closeTok=function(){el('tok-overlay').classList.remove('on');};

window.doTransferToken=function(){
  var scUID=el('tok-scuid').value;
  var from=el('tok-from-addr').value;
  var to=el('tok-to').value.trim();
  var amt=el('tok-amount').value.trim();
  if(!to||!amt){showMsg('tok-msg','Please fill all fields.','err');return;}
  var btn=el('tok-btn');
  btn.disabled=true;btn.textContent='Sending...';
  fetch('/tkapi/tk/TransferToken/'+encodeURIComponent(scUID)+'/'+encodeURIComponent(from)+'/'+encodeURIComponent(to)+'/'+encodeURIComponent(amt))
    .then(function(r){
      if(r.status===403)throw new Error('API must be enabled and unlocked. Use /api/v1/UnlockWallet/{password} first.');
      return r.text();
    })
    .then(function(t){
      btn.disabled=false;btn.textContent='Send';
      var d;try{d=JSON.parse(t);}catch(e){d={Success:t.includes('Success'),Message:t};}
      var ok=d.Success||d.success;
      showMsg('tok-msg',d.Message||d.message||t,ok?'ok':'err');
      if(ok){setTimeout(function(){closeTok();loadAccounts();},2000);}
    })
    .catch(function(e){
      btn.disabled=false;btn.textContent='Send';
      showMsg('tok-msg',e.message||'Transfer failed.','err');
    });
};

/* ---- vBTC Withdrawal Request ---- */
window.openWD=function(scUID,owner,bal){
  el('wd-scuid').value=scUID;
  el('wd-owner').value=owner;
  el('wd-contract-disp').value=scUID;
  el('wd-btcaddr').value='';
  el('wd-amount').value=bal>0?bal.toFixed(8):'';
  el('wd-fee').value='10';
  hideMsg('wd-msg');
  el('wd-overlay').classList.add('on');
};
window.closeWD=function(){el('wd-overlay').classList.remove('on');};

window.doWDRequest=function(){
  var scUID=el('wd-scuid').value;
  var owner=el('wd-owner').value;
  var btcAddr=el('wd-btcaddr').value.trim();
  var amt=el('wd-amount').value.trim();
  var fee=el('wd-fee').value.trim()||'10';
  if(!btcAddr||!amt){showMsg('wd-msg','Please fill BTC address and amount.','err');return;}
  var btn=el('wd-btn');
  btn.disabled=true;btn.textContent='Requesting...';
  fetch('/wallet/api/vbtc/withdraw/request',{
    method:'POST',
    headers:{'Content-Type':'application/json'},
    body:JSON.stringify({ScUID:scUID,OwnerAddress:owner,BTCAddress:btcAddr,Amount:amt,FeeRate:fee})
  }).then(function(r){return r.json();}).then(function(d){
    btn.disabled=false;btn.textContent='Request Withdrawal';
    if(d.success){
      showMsg('wd-msg','Withdrawal request submitted! Wait for it to be mined, then click Complete Withdrawal.','ok');
      setTimeout(function(){closeWD();tabLoaded.vbtc=false;loadVBTC();},3000);
    }else{
      showMsg('wd-msg',d.message||'Request failed.','err');
    }
  }).catch(function(e){
    btn.disabled=false;btn.textContent='Request Withdrawal';
    showMsg('wd-msg',e.message||'Request failed.','err');
  });
};

/* ---- vBTC Complete Withdrawal ---- */
window.openWDC=function(scUID,amt,dest){
  el('wdc-scuid').value=scUID;
  el('wdc-contract-disp').value=scUID;
  el('wdc-amount-disp').value=amt?amt.toFixed(8)+' BTC':'Unknown';
  el('wdc-dest-disp').value=dest||'Unknown';
  el('wdc-hash').value='';
  hideMsg('wdc-msg');
  showMsg('wdc-msg','Fetching withdrawal status...','ok');
  fetch('/wallet/api/vbtc/withdraw/status/'+encodeURIComponent(scUID))
    .then(function(r){return r.json();}).then(function(d){
      if(d.success&&d.requestHash){
        el('wdc-hash').value=d.requestHash;
        el('wdc-amount-disp').value=(d.amount||amt||0).toFixed(8)+' BTC';
        el('wdc-dest-disp').value=d.destination||dest||'';
        hideMsg('wdc-msg');
      }else{
        showMsg('wdc-msg','Could not find request hash. The request may still be pending in the mempool.','err');
      }
    }).catch(function(){hideMsg('wdc-msg');});
  el('wdc-overlay').classList.add('on');
};
window.closeWDC=function(){el('wdc-overlay').classList.remove('on');};

window.doWDComplete=function(){
  var scUID=el('wdc-scuid').value;
  var hash=el('wdc-hash').value;
  if(!hash){showMsg('wdc-msg','No withdrawal request hash found. Wait for the request TX to be mined.','err');return;}
  var btn=el('wdc-btn');
  btn.disabled=true;btn.textContent='Completing... (FROST signing)';
  fetch('/wallet/api/vbtc/withdraw/complete',{
    method:'POST',
    headers:{'Content-Type':'application/json'},
    body:JSON.stringify({ScUID:scUID,RequestHash:hash})
  }).then(function(r){return r.json();}).then(function(d){
    btn.disabled=false;btn.textContent='Complete Withdrawal';
    if(d.success){
      var msg='Withdrawal completed! BTC TX: '+(d.btcTxHash||'pending');
      if(d.vfxTxHash)msg+=' | VFX TX: '+d.vfxTxHash;
      showMsg('wdc-msg',msg,'ok');
      setTimeout(function(){closeWDC();tabLoaded.vbtc=false;loadVBTC();},4000);
    }else{
      showMsg('wdc-msg',d.message||'Completion failed.','err');
    }
  }).catch(function(e){
    btn.disabled=false;btn.textContent='Complete Withdrawal';
    showMsg('wdc-msg',e.message||'Request failed.','err');
  });
};

/* ---- Send vBTC (transfer to another VFX address) ---- */
window.openVBTCTx=function(scUID,bal){
  el('vbtc-tx-scuid').value=scUID;
  el('vbtc-tx-from').value=selAddr||'';
  el('vbtc-tx-contract-disp').value=scUID;
  el('vbtc-tx-from-disp').value=selAddr||'';
  el('vbtc-tx-to').value='';
  el('vbtc-tx-amount').value='';
  hideMsg('vbtc-tx-msg');
  el('vbtc-tx-overlay').classList.add('on');
};
window.closeVBTCTx=function(){el('vbtc-tx-overlay').classList.remove('on');};

window.doVBTCTransfer=function(){
  var scUID=el('vbtc-tx-scuid').value;
  var from=el('vbtc-tx-from').value;
  var to=el('vbtc-tx-to').value.trim();
  var amt=el('vbtc-tx-amount').value.trim();
  if(!to||!amt){showMsg('vbtc-tx-msg','Please fill To address and Amount.','err');return;}
  var btn=el('vbtc-tx-btn');
  btn.disabled=true;btn.textContent='Sending...';
  fetch('/wallet/api/vbtc/transfer',{
    method:'POST',
    headers:{'Content-Type':'application/json'},
    body:JSON.stringify({ScUID:scUID,FromAddress:from,ToAddress:to,Amount:amt})
  }).then(function(r){return r.json();}).then(function(d){
    btn.disabled=false;btn.textContent='Send vBTC';
    if(d.success){
      showMsg('vbtc-tx-msg','vBTC sent successfully! TX: '+(d.message||''),'ok');
      setTimeout(function(){closeVBTCTx();tabLoaded.vbtc=false;loadVBTC();},2500);
    }else{
      showMsg('vbtc-tx-msg',d.message||'Transfer failed.','err');
    }
  }).catch(function(e){
    btn.disabled=false;btn.textContent='Send vBTC';
    showMsg('vbtc-tx-msg',e.message||'Request failed.','err');
  });
};

/* ---- Send BTC ---- */
window.openSendBTC=function(addr){
  el('btc-s-from').value=addr||'';
  el('btc-s-to').value='';
  el('btc-s-amount').value='';
  el('btc-s-fee').value='10';
  hideMsg('btc-s-msg');
  el('btc-overlay').classList.add('on');
};
window.closeBtcSend=function(){el('btc-overlay').classList.remove('on');};

window.doSendBTC=function(){
  var from=el('btc-s-from').value.trim();
  var to=el('btc-s-to').value.trim();
  var amt=el('btc-s-amount').value.trim();
  var fee=el('btc-s-fee').value.trim()||'10';
  if(!from||!to||!amt){showMsg('btc-s-msg','Please fill From, To, and Amount.','err');return;}
  var btn=el('btc-s-btn');
  btn.disabled=true;btn.textContent='Sending...';
  fetch('/btcapi/BTCV2/SendTransaction/'+encodeURIComponent(from)+'/'+encodeURIComponent(to)+'/'+encodeURIComponent(amt)+'/'+encodeURIComponent(fee))
    .then(function(r){
      if(r.status===403)throw new Error('API must be enabled. Use /api/v1/UnlockWallet/{password} first.');
      return r.json();
    })
    .then(function(d){
      btn.disabled=false;btn.textContent='Send BTC';
      var ok=d.Success||d.success||(d.Result&&(d.Result.includes('Success')||d.Result.includes('txid')));
      var msg=d.Message||d.message||d.Result||d.result||JSON.stringify(d);
      showMsg('btc-s-msg',msg,ok?'ok':'err');
      if(ok){setTimeout(function(){closeBtcSend();tabLoaded.btc=false;if(activeTab==='btc')loadBTC();else loadBTC();},2500);}
    })
    .catch(function(e){
      btn.disabled=false;btn.textContent='Send BTC';
      showMsg('btc-s-msg',e.message||'Request failed.','err');
    });
};

/* ---- Privacy ---- */
var knownZfx=[];var selZfx=null;

function loadPrivacy(){
  loadPlonkAndPool();
  fetch('/wallet/api/privacy/addresses').then(function(r){return r.json();}).then(function(data){
    if(data&&data.length){
      knownZfx=[];
      data.forEach(function(w){
        if(w.zfxAddress&&knownZfx.indexOf(w.zfxAddress)===-1)knownZfx.push(w.zfxAddress);
      });
      updateZfxSel();
      if(!selZfx||knownZfx.indexOf(selZfx)===-1)selZfx=knownZfx[0];
      el('priv-zfx-sel').value=selZfx;
      el('priv-zfx-sel-wrap').style.display='block';
      el('priv-actions').style.display='block';
      loadZfxBalance(selZfx);
    }else{
      el('priv-bal-num').innerHTML='-- <span>VFX</span>';
      el('priv-notes').textContent='No shielded address created yet';
      el('priv-zfx-addr').textContent='';
      el('priv-actions').style.display='none';
      el('priv-zfx-sel-wrap').style.display='none';
    }
  }).catch(function(){
    if(knownZfx.length>0){
      loadZfxBalance(selZfx||knownZfx[0]);
    }else{
      el('priv-bal-num').innerHTML='-- <span>VFX</span>';
      el('priv-notes').textContent='No shielded address created yet';
      el('priv-zfx-addr').textContent='';
      el('priv-actions').style.display='none';
      el('priv-zfx-sel-wrap').style.display='none';
    }
  });
}

function loadPlonkAndPool(){
  var html='';
  Promise.all([
    fetch('/wallet/api/privacy/plonkStatus').then(function(r){return r.json();}).catch(function(){return null;}),
    fetch('/wallet/api/privacy/poolState').then(function(r){return r.json();}).catch(function(){return null;})
  ]).then(function(results){
    var plonk=results[0];var pool=results[1];
    html+='<div class=""stat-row"">';
    if(plonk&&plonk.success){
      html+='<div class=""stat-card""><div class=""stat-lbl"">Proof Verification</div><div class=""stat-val '+(plonk.proofVerificationImplemented?'grn':'red')+'"">'+(plonk.proofVerificationImplemented?'Available':'Unavailable')+'</div></div>';
      html+='<div class=""stat-card""><div class=""stat-lbl"">Proof Proving</div><div class=""stat-val '+(plonk.proofProvingImplemented?'grn':'org')+'"">'+(plonk.proofProvingImplemented?'Available':'Unavailable')+'</div></div>';
      html+='<div class=""stat-card""><div class=""stat-lbl"">Enforce PLONK</div><div class=""stat-val '+(plonk.enforcePlonkProofsForZk?'grn':'org')+'"">'+(plonk.enforcePlonkProofsForZk?'Yes':'No')+'</div></div>';
      html+='<div class=""stat-card""><div class=""stat-lbl"">Native Caps</div><div class=""stat-val acc"">'+plonk.nativeCapabilities+'</div></div>';
      html+='<div class=""stat-card""><div class=""stat-lbl"">Params Mirror</div><div class=""stat-val acc"">'+(plonk.paramsBytesMirrored>0?Math.round(plonk.paramsBytesMirrored/1024)+' KB':'None')+'</div></div>';
    }else{
      html+='<div class=""stat-card""><div class=""stat-lbl"">PLONK Status</div><div class=""stat-val red"">Error</div></div>';
    }
    if(pool&&pool.success){
      html+='<div class=""stat-card""><div class=""stat-lbl"">Pool Asset</div><div class=""stat-val acc"">'+esc(pool.assetType)+'</div></div>';
      html+='<div class=""stat-card""><div class=""stat-lbl"">Total Commitments</div><div class=""stat-val grn"">'+pool.totalCommitments+'</div></div>';
      html+='<div class=""stat-card""><div class=""stat-lbl"">Shielded Supply</div><div class=""stat-val org"">'+fmtBal(pool.totalShieldedSupply)+' VFX</div></div>';
      html+='<div class=""stat-card""><div class=""stat-lbl"">Last Update</div><div class=""stat-val acc"">#'+pool.lastUpdateHeight+'</div></div>';
      html+='<div class=""stat-card""><div class=""stat-lbl"">Merkle Root</div><div class=""stat-val muted"" style=""font-size:11px;word-break:break-all"">'+(pool.currentMerkleRoot?shn(pool.currentMerkleRoot,20):'Empty')+'</div></div>';
    }else{
      html+='<div class=""stat-card""><div class=""stat-lbl"">Pool State</div><div class=""stat-val red"">Error</div></div>';
    }
    html+='</div>';
    el('priv-status-content').innerHTML=html;
  });
}

function loadZfxBalance(zfx){
  if(!zfx)return;
  selZfx=zfx;
  el('priv-zfx-addr').textContent=zfx;
  fetch('/wallet/api/privacy/balance/'+encodeURIComponent(zfx))
    .then(function(r){return r.json();})
    .then(function(d){
      if(d.success){
        el('priv-bal-num').innerHTML=fmtBal(d.vfxShieldedBalance)+'<span>VFX</span>';
        el('priv-notes').textContent=d.unspentNotes+' unspent note'+(d.unspentNotes!==1?'s':'');
      }else{
        el('priv-bal-num').innerHTML='-- <span>VFX</span>';
        el('priv-notes').textContent=d.message||'Error';
      }
    }).catch(function(){
      el('priv-bal-num').innerHTML='-- <span>VFX</span>';
      el('priv-notes').textContent='Failed to load balance';
    });
}

function addZfxAddr(zfx){
  if(knownZfx.indexOf(zfx)===-1)knownZfx.push(zfx);
  updateZfxSel();
  selZfx=zfx;
  el('priv-zfx-sel').value=zfx;
  el('priv-zfx-sel-wrap').style.display='block';
  el('priv-actions').style.display='block';
}

function updateZfxSel(){
  var sel=el('priv-zfx-sel');
  sel.innerHTML='';
  knownZfx.forEach(function(z){
    var opt=document.createElement('option');
    opt.value=z;opt.textContent=z;
    sel.appendChild(opt);
  });
}

window.onZfxChange=function(){
  selZfx=el('priv-zfx-sel').value;
  loadZfxBalance(selZfx);
};

function populateAddrSelect(selId){
  var sel=el(selId);sel.innerHTML='';
  accounts.forEach(function(a){
    var opt=document.createElement('option');
    opt.value=a.address;
    opt.textContent=a.address.substring(0,20)+'... | '+fmtBal(a.balance)+' VFX';
    sel.appendChild(opt);
  });
}

/* ---- Create Shielded Address ---- */
window.openCreateZfx=function(){
  populateAddrSelect('czfx-addr');
  el('czfx-pwd').value='';
  hideMsg('czfx-msg');
  el('czfx-overlay').classList.add('on');
};
window.closeCZfx=function(){el('czfx-overlay').classList.remove('on');};

window.doCreateZfx=function(){
  var addr=el('czfx-addr').value;
  var pwd=el('czfx-pwd').value;
  if(!addr){showMsg('czfx-msg','Select a VFX address.','err');return;}
  if(!pwd||pwd.length<8){showMsg('czfx-msg','Password must be at least 8 characters.','err');return;}
  var btn=el('czfx-btn');
  btn.disabled=true;btn.textContent='Creating...';
  fetch('/wallet/api/privacy/createShieldedAddress/'+encodeURIComponent(addr)+'/'+encodeURIComponent(pwd))
    .then(function(r){return r.json();}).then(function(d){
      btn.disabled=false;btn.textContent='Create';
      if(d.success){
        showMsg('czfx-msg','Shielded address created: '+d.zfxAddress,'ok');
        addZfxAddr(d.zfxAddress);
        loadZfxBalance(d.zfxAddress);
        setTimeout(function(){closeCZfx();},2500);
      }else{
        showMsg('czfx-msg',d.message||'Creation failed.','err');
      }
    }).catch(function(e){
      btn.disabled=false;btn.textContent='Create';
      showMsg('czfx-msg',e.message||'Request failed.','err');
    });
};

/* ---- Shield VFX ---- */
window.openShield=function(){
  populateAddrSelect('sh-from');
  el('sh-zfx').value=selZfx||'';
  el('sh-amount').value='';
  hideMsg('sh-msg');
  el('shield-overlay').classList.add('on');
};
window.closeShield=function(){el('shield-overlay').classList.remove('on');};

window.doShield=function(){
  var from=el('sh-from').value;
  var zfx=el('sh-zfx').value.trim();
  var amt=el('sh-amount').value.trim();
  if(!from||!zfx||!amt){showMsg('sh-msg','Please fill all fields.','err');return;}
  if(!zfx.startsWith('zfx_')){showMsg('sh-msg','Shielded address must start with zfx_','err');return;}
  var btn=el('sh-btn');
  btn.disabled=true;btn.textContent='Shielding...';
  fetch('/wallet/api/privacy/shield/'+encodeURIComponent(from)+'/'+encodeURIComponent(zfx)+'/'+encodeURIComponent(amt))
    .then(function(r){return r.json();}).then(function(d){
      btn.disabled=false;btn.textContent='Shield';
      if(d.success){
        showMsg('sh-msg','Shield TX broadcast! Hash: '+(d.hash||''),'ok');
        addZfxAddr(zfx);
        setTimeout(function(){closeShield();loadAccounts();loadZfxBalance(zfx);},2500);
      }else{
        showMsg('sh-msg',d.message||'Shield failed.','err');
      }
    }).catch(function(e){
      btn.disabled=false;btn.textContent='Shield';
      showMsg('sh-msg',e.message||'Request failed.','err');
    });
};

/* ---- Unshield VFX ---- */
window.openUnshield=function(){
  if(!selZfx){showMsg('priv-notes','No shielded address selected.','err');return;}
  el('ush-zfx').value=selZfx;
  populateAddrSelect('ush-to');
  el('ush-amount').value='';
  el('ush-pwd').value='';
  hideMsg('ush-msg');
  el('unshield-overlay').classList.add('on');
};
window.closeUnshield=function(){el('unshield-overlay').classList.remove('on');};

window.doUnshield=function(){
  var zfx=el('ush-zfx').value;
  var to=el('ush-to').value;
  var amt=el('ush-amount').value.trim();
  var pwd=el('ush-pwd').value;
  if(!zfx||!to||!amt){showMsg('ush-msg','Please fill all fields.','err');return;}
  var btn=el('ush-btn');
  btn.disabled=true;btn.textContent='Unshielding...';
  var url='/wallet/api/privacy/unshield/'+encodeURIComponent(zfx)+'/'+encodeURIComponent(to)+'/'+encodeURIComponent(amt);
  if(pwd)url+='?password='+encodeURIComponent(pwd);
  fetch(url).then(function(r){return r.json();}).then(function(d){
    btn.disabled=false;btn.textContent='Unshield';
    if(d.success){
      showMsg('ush-msg','Unshield TX broadcast! Hash: '+(d.hash||''),'ok');
      setTimeout(function(){closeUnshield();loadAccounts();loadZfxBalance(zfx);},2500);
    }else{
      showMsg('ush-msg',d.message||'Unshield failed.','err');
    }
  }).catch(function(e){
    btn.disabled=false;btn.textContent='Unshield';
    showMsg('ush-msg',e.message||'Request failed.','err');
  });
};

/* ---- Private Transfer ---- */
window.openPrivTransfer=function(){
  if(!selZfx){return;}
  el('ptx-from').value=selZfx;
  el('ptx-to').value='';
  el('ptx-amount').value='';
  el('ptx-pwd').value='';
  hideMsg('ptx-msg');
  el('ptx-overlay').classList.add('on');
};
window.closePTx=function(){el('ptx-overlay').classList.remove('on');};

window.doPrivTransfer=function(){
  var from=el('ptx-from').value;
  var to=el('ptx-to').value.trim();
  var amt=el('ptx-amount').value.trim();
  var pwd=el('ptx-pwd').value;
  if(!from||!to||!amt){showMsg('ptx-msg','Please fill all fields.','err');return;}
  if(!to.startsWith('zfx_')){showMsg('ptx-msg','Recipient must be a zfx_ address.','err');return;}
  var btn=el('ptx-btn');
  btn.disabled=true;btn.textContent='Sending...';
  var url='/wallet/api/privacy/transfer/'+encodeURIComponent(from)+'/'+encodeURIComponent(to)+'/'+encodeURIComponent(amt);
  if(pwd)url+='?password='+encodeURIComponent(pwd);
  fetch(url).then(function(r){return r.json();}).then(function(d){
    btn.disabled=false;btn.textContent='Send';
    if(d.success){
      showMsg('ptx-msg','Private transfer broadcast! Hash: '+(d.hash||''),'ok');
      setTimeout(function(){closePTx();loadZfxBalance(from);},2500);
    }else{
      showMsg('ptx-msg',d.message||'Transfer failed.','err');
    }
  }).catch(function(e){
    btn.disabled=false;btn.textContent='Send';
    showMsg('ptx-msg',e.message||'Request failed.','err');
  });
};

/* ---- Scan for Notes ---- */
window.doScanZfx=function(){
  if(!selZfx)return;
  var scanBtn=document.querySelector('#priv-actions .act-btn.sec');
  if(scanBtn){scanBtn.disabled=true;scanBtn.textContent='Scanning...';}
  var url='/wallet/api/privacy/scan/'+encodeURIComponent(selZfx);
  fetch(url).then(function(r){return r.json();}).then(function(d){
    if(scanBtn){scanBtn.disabled=false;scanBtn.innerHTML='&#128269; Scan for Notes';}
    if(d.success){
      var msg='Scanned '+d.blocksScanned+' blocks, '+d.transactionsScanned+' TXs. Found '+d.newNotesFound+' new note'+(d.newNotesFound!==1?'s':'')+'.';
      el('priv-notes').textContent=msg;
      loadZfxBalance(selZfx);
    }else{
      el('priv-notes').textContent=d.message||'Scan failed.';
    }
  }).catch(function(){
    if(scanBtn){scanBtn.disabled=false;scanBtn.innerHTML='&#128269; Scan for Notes';}
    el('priv-notes').textContent='Scan request failed.';
  });
};

/* ---- Helpers ---- */
function el(id){return document.getElementById(id);}
function esc(s){return s?String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/""/g,'&quot;').replace(/'/g,'&#39;'):'';}
function shn(s,max){return s?(s.length>max?s.substring(0,max)+'...':s):'--';}
function fmtBal(n){return n!=null?(+n).toFixed(8):'0.00000000';}
function fmtTok(n,dec){var d=dec!=null?dec:8;return n!=null?(+n).toFixed(d):'0';}

function showMsg(id,msg,type){
  var e=el(id);e.textContent=msg;e.className='msg '+type+' on';
}
function hideMsg(id){var e=el(id);e.className='msg';}

function ago(ts){
  var d=Math.floor(Date.now()/1000)-ts;
  if(d<5)return 'just now';if(d<60)return d+'s ago';
  if(d<3600)return Math.floor(d/60)+'m ago';if(d<86400)return Math.floor(d/3600)+'h ago';
  return Math.floor(d/86400)+'d ago';
}
function stCls(s){return['badge-pend','badge-ok','badge-fail','badge-pend','badge-pend','badge-ok','badge-fail','badge-fail'][s]||'badge-pend';}
function stNm(s){return['Pending','Success','Failed','Reserved','CalledBack','Recovered','ReplacedByFee','Invalid'][s]||'Unknown';}
function ttype(t){
  var n=['TX','NODE','NFT_MINT','NFT_TX','NFT_BURN','NFT_SALE','ADNR','DSTR','VOTE_TOPIC','VOTE','RESERVE','SC_MINT','SC_TX','SC_BURN','FTKN_MINT','FTKN_TX','FTKN_BURN','TKNZ_MINT','TKNZ_TX','TKNZ_BURN','TKNZ_WD_ARB','TKNZ_WD_OWNER','VBTC2_VAL_REG','VBTC2_VAL_HB','VBTC2_VAL_EXIT','VBTC2_CREATE','VBTC2_TX','VBTC2_WD_REQ','VBTC2_WD_COMP','VBTC2_WD_CANCEL','VBTC2_WD_VOTE'];
  return n[t]!==undefined?n[t]:'TYPE_'+t;
}
})();
</script>
</body>
</html>";
    }
}
