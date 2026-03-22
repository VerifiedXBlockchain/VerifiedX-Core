using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ReserveBlockCore.Bitcoin.Models;
using ReserveBlockCore.Data;
using ReserveBlockCore.Models;
using ReserveBlockCore.Services;
using ReserveBlockCore.Utilities;

namespace ReserveBlockCore.BrowserWalletServices
{
    public static class WalletVbtcService
    {
        public static object GetBitcoinAccounts()
        {
            var accounts = BitcoinAccount.GetBitcoinAccounts();
            if (accounts == null || !accounts.Any())
                return Array.Empty<object>();

            return accounts.Select(a => new
            {
                address = a.Address,
                adnr = a.ADNR,
                balance = a.Balance,
                isValidating = a.IsValidating
            }).ToList();
        }

        public static object GetVBTCContracts(string address)
        {
            var scStates = SmartContractStateTrei.GetvBTCSmartContracts(address);
            if (scStates == null || !scStates.Any())
                return Array.Empty<object>();

            var resultList = new List<object>();
            var seen = new HashSet<string>();

            foreach (var scState in scStates)
            {
                bool isOwner = scState.OwnerAddress == address;

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
                    var contract = VBTCContractV2.GetContract(scState.SmartContractUID);
                    if (contract != null)
                    {
                        depositAddress = contract.DepositAddress ?? "";
                        totalBalance = contract.Balance + ledgerBalance;
                    }
                }

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

            return resultList;
        }

        public static async Task<(bool success, string message)> RequestWithdrawal(
            string scUID, string ownerAddress, string btcAddress, decimal amount, int feeRate)
        {
            var result = await Bitcoin.Services.VBTCService.RequestWithdrawal(scUID, ownerAddress, btcAddress, amount, feeRate);
            return (result.Item1, result.Item2);
        }

        public static async Task<(bool success, string message, string? vfxTxHash, string? btcTxHash, string? remoteResult)> CompleteWithdrawal(
            string scUID, string requestHash)
        {
            if (string.IsNullOrEmpty(Globals.ValidatorAddress))
            {
                var remoteResult = await DelegateWithdrawalToRemoteValidator(scUID, requestHash);
                return (true, "Withdrawal completed!", null, null, remoteResult);
            }

            var result = await Bitcoin.Services.VBTCService.CompleteWithdrawal(scUID, requestHash);
            if (result.Success)
                return (true, "Withdrawal completed!", result.VFXTxHash, result.BTCTxHash, null);
            else
                return (false, result.ErrorMessage, null, null, null);
        }

        public static object GetWithdrawStatus(string scUID)
        {
            var contract = VBTCContractV2.GetContract(scUID);
            if (contract == null)
                return new { success = false, message = "Contract not found" };

            return new
            {
                success = true,
                status = contract.WithdrawalStatus.ToString(),
                amount = contract.ActiveWithdrawalAmount ?? 0M,
                destination = contract.ActiveWithdrawalBTCDestination ?? "",
                requestHash = contract.ActiveWithdrawalRequestHash ?? ""
            };
        }

        public static async Task<(bool success, string message)> TransferVBTC(
            string scUID, string fromAddress, string toAddress, decimal amount)
        {
            var addrValid = AddressValidateUtility.ValidateAddress(toAddress);
            if (!addrValid)
                return (false, "Invalid destination VFX address.");

            var result = await Bitcoin.Services.VBTCService.TransferVBTC(scUID, fromAddress, toAddress, amount);
            return (result.Item1, result.Item2);
        }

        private static async Task<string> DelegateWithdrawalToRemoteValidator(string scUID, string withdrawalRequestHash)
        {
            try
            {
                LogUtility.Log($"[FROST MPC] Non-validator node delegating withdrawal to remote validator. scUID: {scUID}",
                    "VBTCController.DelegateWithdrawalToRemoteValidator");

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
                    var localContract = VBTCContractV2.GetContract(scUID);
                    if (localContract != null && localContract.ActiveWithdrawalAmount.HasValue && localContract.ActiveWithdrawalAmount.Value > 0)
                    {
                        localAmount = localContract.ActiveWithdrawalAmount.Value;
                        localBTCDestination = localContract.ActiveWithdrawalBTCDestination;
                        localFeeRate = 10;
                        LogUtility.Log($"[FROST MPC] Including contract Active* fields in delegation: Amount={localAmount}, Dest={localBTCDestination}",
                            "VBTCController.DelegateWithdrawalToRemoteValidator");
                    }
                    else
                    {
                        LogUtility.Log($"[FROST MPC] WARNING: No local withdrawal request or contract Active* fields found. Remote validator will need its own data.",
                            "VBTCController.DelegateWithdrawalToRemoteValidator");
                    }
                }

                var activeValidators = await VBTCValidator.FetchActiveValidatorsFromNetwork();
                if (activeValidators == null || !activeValidators.Any())
                {
                    return JsonConvert.SerializeObject(new { Success = false, Message = "No active validators available on the network. Cannot delegate withdrawal." });
                }

                foreach (var validator in activeValidators)
                {
                    try
                    {
                        var ip = validator.IPAddress?.Replace("::ffff:", "");
                        if (string.IsNullOrEmpty(ip)) continue;

                        var url = $"http://{ip}:{Globals.FrostValidatorPort}/frost/mpc/withdrawal/complete";
                        using var client = Globals.HttpClientFactory.CreateClient();
                        client.Timeout = TimeSpan.FromSeconds(120);

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

                            var signedTxHex = json["SignedTxHex"]?.Value<string>();
                            if (string.IsNullOrEmpty(signedTxHex))
                            {
                                LogUtility.Log($"[FROST MPC] Validator returned success but no SignedTxHex. Response: {responseBody}",
                                    "VBTCController.DelegateWithdrawalToRemoteValidator");
                                continue;
                            }

                            LogUtility.Log($"[FROST MPC] Broadcasting signed BTC TX locally. Hex length: {signedTxHex.Length}",
                                "VBTCController.DelegateWithdrawalToRemoteValidator");

                            var btcNetwork = Globals.BTCNetwork;
                            var signedTx = NBitcoin.Transaction.Parse(signedTxHex, btcNetwork);
                            var broadcastResult = await ReserveBlockCore.Bitcoin.Services.BitcoinTransactionService.BroadcastTransaction(signedTx);

                            if (!broadcastResult.Success)
                            {
                                LogUtility.Log($"[FROST MPC] BTC broadcast failed: {broadcastResult.ErrorMessage}. Will try next validator.",
                                    "VBTCController.DelegateWithdrawalToRemoteValidator");
                                continue;
                            }

                            string btcTxHash = broadcastResult.TxHash;
                            LogUtility.Log($"[FROST MPC] BTC TX broadcast SUCCESS: {btcTxHash}",
                                "VBTCController.DelegateWithdrawalToRemoteValidator");

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

                            string vfxTxHash = string.Empty;
                            try
                            {
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
    }
}