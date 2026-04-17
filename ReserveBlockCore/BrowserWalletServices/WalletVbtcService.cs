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
                isValidating = a.IsValidating,
                linkedEvmAddress = a.LinkedEvmAddress
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

                var activeValidators = Bitcoin.Services.VBTCValidatorRegistry.GetActiveValidators();
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

        /// <summary>
        /// Bridge vBTC to Base as vBTC.b (ERC-20). 
        /// User-driven: creates lock TX on VFX, collects validator attestations, 
        /// and submits mintWithProof on Base using the user's own ETH key (user pays gas).
        /// </summary>
        public static async Task<object> BridgeToBase(string scUID, string ownerAddress, decimal amount, string evmDestination)
        {
            try
            {
                var result = await Bitcoin.Services.UserBridgeMintService.ExecuteBridgeToBase(scUID, ownerAddress, amount, evmDestination);

                if (result.Success)
                {
                    return new
                    {
                        success = true,
                        message = result.Message,
                        lockId = result.LockId,
                        amount = amount,
                        evmDestination = evmDestination,
                        contractAddress = Bitcoin.Services.BaseBridgeService.ContractAddress,
                        chainId = Bitcoin.Services.BaseBridgeService.BaseChainId
                    };
                }
                else
                {
                    return new { success = false, message = result.Message };
                }
            }
            catch (Exception ex)
            {
                return new { success = false, message = $"Error bridging to Base: {ex.Message}" };
            }
        }

        /// <summary>
        /// Retry a failed or timed-out bridge mint for a specific lock ID.
        /// </summary>
        public static async Task<object> RetryBridgeMint(string lockId, string ownerAddress)
        {
            try
            {
                var result = await Bitcoin.Services.UserBridgeMintService.RetryMintForLock(lockId, ownerAddress);
                return new { success = result.Success, message = result.Message };
            }
            catch (Exception ex)
            {
                return new { success = false, message = $"Error retrying mint: {ex.Message}" };
            }
        }

        /// <summary>
        /// Pre-flight info for the Bridge to Base modal.
        /// Returns derived Base address, ETH balance, vBTC.b balance, available vBTC, and bridge config status.
        /// </summary>
        public static async Task<object> GetBridgePreflight(string ownerAddress, string scUID)
        {
            try
            {
                // Derive Base address from the VFX key
                var derivedBaseAddress = Bitcoin.Services.ValidatorEthKeyService.DeriveBaseAddressFromAccount(ownerAddress);
                var hasDerivedAddress = !string.IsNullOrEmpty(derivedBaseAddress);

                // Bridge config status
                var bridgeConfigured = Bitcoin.Services.BaseBridgeService.IsBridgeConfigured;
                var canReadEth = Bitcoin.Services.BaseBridgeService.CanReadEth;
                var canReadVbtc = Bitcoin.Services.BaseBridgeService.CanReadVbtcToken;
                var networkName = Bitcoin.Services.BaseBridgeService.BaseNetworkDisplayName;
                var chainId = Bitcoin.Services.BaseBridgeService.BaseChainId;
                var contractAddress = Bitcoin.Services.BaseBridgeService.ContractAddress;

                // Fetch vBTC available balance on VFX side
                decimal availableVbtc = 0M;
                string vbtcError = null;
                try
                {
                    var balResult = await Bitcoin.Services.VBTCService.TryGetAvailableTransparentVbtcBalance(scUID, ownerAddress);
                    if (balResult.success)
                    {
                        // Subtract local bridge reserves not yet confirmed on-chain
                        var reserved = Bitcoin.Models.BridgeLockRecord.GetLockedAmount(ownerAddress, scUID);
                        availableVbtc = balResult.availableBalance - reserved;
                        if (availableVbtc < 0) availableVbtc = 0;
                    }
                    else
                    {
                        vbtcError = balResult.error;
                    }
                }
                catch (Exception ex)
                {
                    vbtcError = ex.Message;
                }

                // Fetch ETH balance on Base for the derived address
                decimal? ethBalance = null;
                string ethError = null;
                if (hasDerivedAddress && canReadEth)
                {
                    try
                    {
                        var ethResult = await Bitcoin.Services.BaseBridgeService.GetEthBalanceAsync(derivedBaseAddress);
                        if (ethResult.Success)
                            ethBalance = ethResult.BalanceEth;
                        else
                            ethError = ethResult.Message;
                    }
                    catch (Exception ex) { ethError = ex.Message; }
                }

                // Fetch vBTC.b balance on Base for the derived address
                decimal? vbtcBBalance = null;
                string vbtcBError = null;
                if (hasDerivedAddress && canReadVbtc)
                {
                    try
                    {
                        var tokResult = await Bitcoin.Services.BaseBridgeService.GetBaseBalance(derivedBaseAddress);
                        if (tokResult.Success)
                            vbtcBBalance = tokResult.Balance;
                        else
                            vbtcBError = tokResult.Message;
                    }
                    catch (Exception ex) { vbtcBError = ex.Message; }
                }

                return new
                {
                    success = true,
                    // VFX side
                    ownerAddress = ownerAddress,
                    scUID = scUID,
                    availableVbtc = availableVbtc,
                    vbtcError = vbtcError,
                    // Derived Base address
                    derivedBaseAddress = derivedBaseAddress ?? "",
                    hasDerivedAddress = hasDerivedAddress,
                    // Base balances
                    ethBalance = ethBalance,
                    ethError = ethError,
                    vbtcBBalance = vbtcBBalance,
                    vbtcBError = vbtcBError,
                    // Config
                    bridgeConfigured = bridgeConfigured,
                    canReadEth = canReadEth,
                    canReadVbtc = canReadVbtc,
                    networkName = networkName,
                    chainId = chainId,
                    contractAddress = contractAddress
                };
            }
            catch (Exception ex)
            {
                return new { success = false, message = $"Preflight error: {ex.Message}" };
            }
        }

        /// <summary>
        /// Get the status of a bridge lock by lockId.
        /// Returns the BridgeLockRecord including attestation progress and status.
        /// </summary>
        public static object GetBridgeLockStatus(string lockId)
        {
            try
            {
                var record = BridgeLockRecord.GetByLockId(lockId);
                if (record == null)
                    return new { success = false, message = $"Bridge lock not found: {lockId}" };

                var sigCount = record.ValidatorSignatures?.Count ?? 0;

                return new
                {
                    success = true,
                    lockId = record.LockId,
                    scUID = record.SmartContractUID,
                    ownerAddress = record.OwnerAddress,
                    amount = record.Amount,
                    amountSats = record.AmountSats,
                    evmDestination = record.EvmDestination,
                    status = record.Status.ToString(),
                    vfxLockTxHash = record.VfxLockTxHash,
                    vfxLockConfirmedOnChain = record.VfxLockConfirmedOnChain,
                    vfxLockBlockHeight = record.VfxLockBlockHeight,
                    baseTxHash = record.BaseTxHash,
                    exitBurnTxHash = record.ExitBurnTxHash,
                    signaturesCollected = sigCount,
                    requiredSignatures = record.RequiredSignatures,
                    mintNonce = record.MintNonce,
                    signatures = record.ValidatorSignatures,
                    createdAtUtc = record.CreatedAtUtc,
                    relayedAtUtc = record.RelayedAtUtc,
                    finalizedAtUtc = record.FinalizedAtUtc,
                    errorMessage = record.ErrorMessage
                };
            }
            catch (Exception ex)
            {
                return new { success = false, message = $"Error retrieving bridge lock status: {ex.Message}" };
            }
        }
    }
}
