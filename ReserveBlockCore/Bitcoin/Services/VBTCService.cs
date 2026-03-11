using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ReserveBlockCore.Bitcoin.Models;
using ReserveBlockCore.Data;
using ReserveBlockCore.Extensions;
using ReserveBlockCore.Models;
using ReserveBlockCore.Models.SmartContracts;
using ReserveBlockCore.P2P;
using ReserveBlockCore.Services;
using ReserveBlockCore.Utilities;
using System;
using System.IO;
using System.Text;

namespace ReserveBlockCore.Bitcoin.Services
{
    /// <summary>
    /// Service for vBTC V2 (MPC-based tokenized Bitcoin) operations
    /// </summary>
    public class VBTCService
    {
        #region Deposit Balance Scanning

        /// <summary>
        /// Scan all owned vBTC V2 contracts' deposit addresses via Electrum
        /// and update the local Balance field. Called on startup and periodically.
        /// Mirrors the v1 pattern where the owner scans their own deposit address.
        /// </summary>
        public static async Task ScanVBTCV2Balances()
        {
            try
            {
                var accounts = AccountData.GetAccounts();
                if (accounts == null) return;

                var allAddresses = accounts.FindAll().Select(a => a.Address).ToList();
                if (!allAddresses.Any()) return;

                foreach (var address in allAddresses)
                {
                    var contracts = VBTCContractV2.GetContractsByOwner(address);
                    if (contracts == null || !contracts.Any()) continue;

                    foreach (var contract in contracts)
                    {
                        await ScanSingleContractBalance(contract);
                    }
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error scanning vBTC V2 balances: {ex.Message}", "VBTCService.ScanVBTCV2Balances()");
            }
        }

        /// <summary>
        /// Scan a single contract's deposit address via Electrum and update local balance.
        /// </summary>
        public static async Task<decimal> ScanSingleContractBalance(VBTCContractV2 contract)
        {
            try
            {
                if (string.IsNullOrEmpty(contract.DepositAddress))
                    return 0M;

                var utxos = await BitcoinTransactionService.GetTaprootUTXOs(contract.DepositAddress);
                decimal btcBalance = 0M;

                if (utxos != null && utxos.Any())
                {
                    // Sum all UTXO values (in satoshis) and convert to BTC
                    ulong totalSatoshis = 0;
                    foreach (var utxo in utxos)
                    {
                        totalSatoshis += utxo.Value;
                    }
                    btcBalance = totalSatoshis * BitcoinTransactionService.SatoshiMultiplier;
                }

                // Update contract balance locally
                if (contract.Balance != btcBalance)
                {
                    contract.Balance = btcBalance;
                    VBTCContractV2.UpdateContract(contract);
                    SCLogUtility.Log($"Updated vBTC V2 balance for {contract.SmartContractUID}: {btcBalance} BTC", 
                        "VBTCService.ScanSingleContractBalance()");
                }

                return btcBalance;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error scanning contract {contract.SmartContractUID}: {ex.Message}", 
                    "VBTCService.ScanSingleContractBalance()");
                return contract.Balance;
            }
        }

        /// <summary>
        /// Background loop that periodically scans vBTC V2 deposit balances.
        /// Runs initial scan on startup, then every 5 minutes.
        /// </summary>
        public static async Task VBTCV2BalanceScanLoop()
        {
            try
            {
                // Wait for startup to complete before first scan
                await Task.Delay(TimeSpan.FromSeconds(30));

                LogUtility.Log("Starting vBTC V2 deposit balance scan loop", "VBTCService.VBTCV2BalanceScanLoop()");

                while (true)
                {
                    try
                    {
                        await ScanVBTCV2Balances();
                    }
                    catch (Exception ex)
                    {
                        ErrorLogUtility.LogError($"Error in balance scan loop iteration: {ex.Message}", "VBTCService.VBTCV2BalanceScanLoop()");
                    }

                    // Wait 5 minutes between scans
                    await Task.Delay(TimeSpan.FromMinutes(5));
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Fatal error in balance scan loop: {ex.Message}", "VBTCService.VBTCV2BalanceScanLoop()");
            }
        }

        #endregion

        /// <summary>
        /// Transfer ownership of a vBTC V2 contract to another address
        /// </summary>
        /// <param name="scUID">Smart contract UID</param>
        /// <param name="toAddress">New owner address</param>
        /// <param name="backupURL">Optional backup URL</param>
        /// <returns>JSON result</returns>
        public static async Task<string> TransferOwnership(string scUID, string toAddress, string? backupURL = "")
        {
            try
            {
                // Get vBTC V2 contract
                var vbtcContract = VBTCContractV2.GetContract(scUID);

                if (vbtcContract == null)
                    return await SCLogUtility.LogAndReturn($"Failed to find vBTC V2 contract: {scUID}", "VBTCService.TransferOwnership()", false);

                // Get smart contract
                var sc = SmartContractMain.SmartContractData.GetSmartContract(scUID);

                if (sc == null)
                    return await SCLogUtility.LogAndReturn($"Failed to find Smart Contract Data: {scUID}", "VBTCService.TransferOwnership()", false);

                if (sc.Features == null)
                    return await SCLogUtility.LogAndReturn($"Contract has no features: {scUID}", "VBTCService.TransferOwnership()", false);

                // Get TokenizationV2 feature
                var tknzFeature = sc.Features.Where(x => x.FeatureName == FeatureName.TokenizationV2).Select(x => x.FeatureFeatures).FirstOrDefault();

                if (tknzFeature == null)
                    return await SCLogUtility.LogAndReturn($"Contract missing a TokenizationV2 feature: {scUID}", "VBTCService.TransferOwnership()", false);

                var tknz = (TokenizationV2Feature)tknzFeature;

                if (tknz == null)
                    return await SCLogUtility.LogAndReturn($"Token feature error: {scUID}", "VBTCService.TransferOwnership()", false);

                // Get smart contract state
                var scState = SmartContractStateTrei.GetSmartContractState(sc.SmartContractUID);

                if (scState == null)
                    return await SCLogUtility.LogAndReturn($"SC State Missing: {scUID}", "VBTCService.TransferOwnership()", false);

                // Check owner account exists
                var account = AccountData.GetSingleAccount(scState.OwnerAddress);

                if (account == null)
                    return await SCLogUtility.LogAndReturn($"Owner address account not found.", "VBTCService.TransferOwnership()", false);

                // Validate balance > 0 (including state trei tokenization TXs)
                if (scState.SCStateTreiTokenizationTXes != null)
                {
                    var balances = scState.SCStateTreiTokenizationTXes.Where(x => x.FromAddress == account.Address || x.ToAddress == account.Address).ToList();
                    if (balances.Any())
                    {
                        var balance = balances.Sum(x => x.Amount);
                        var finalBalance = vbtcContract.Balance + balance;
                        if (finalBalance <= 0)
                            return await SCLogUtility.LogAndReturn($"Cannot transfer a token with zero balance.", "VBTCService.TransferOwnership()", false);
                    }
                    else
                    {
                        if (vbtcContract.Balance <= 0M)
                            return await SCLogUtility.LogAndReturn($"Cannot transfer a token with zero balance.", "VBTCService.TransferOwnership()", false);
                    }
                }
                else
                {
                    if (vbtcContract.Balance <= 0M)
                        return await SCLogUtility.LogAndReturn($"Cannot transfer a token with zero balance.", "VBTCService.TransferOwnership()", false);
                }

                // Check beacons exist
                if (!Globals.Beacons.Any())
                    return await SCLogUtility.LogAndReturn("Error - You do not have any beacons stored.", "VBTCService.TransferOwnership()", false);

                if (!Globals.Beacon.Values.Where(x => x.IsConnected).Any())
                {
                    var beaconConnectionResult = await BeaconUtility.EstablishBeaconConnection(true, false);
                    if (!beaconConnectionResult)
                    {
                        return await SCLogUtility.LogAndReturn("Error - You failed to connect to any beacons.", "VBTCService.TransferOwnership()", false);
                    }
                }

                var connectedBeacon = Globals.Beacon.Values.Where(x => x.IsConnected).FirstOrDefault();
                if (connectedBeacon == null)
                    return await SCLogUtility.LogAndReturn("Error - You have lost connection to beacons. Please attempt to resend.", "VBTCService.TransferOwnership()", false);

                // Normalize address
                toAddress = toAddress.Replace(" ", "").ToAddressNormalize();
                var localAddress = AccountData.GetSingleAccount(toAddress);

                // Get assets and MD5 list
                var assets = await NFTAssetFileUtility.GetAssetListFromSmartContract(sc);
                var md5List = await MD5Utility.GetMD5FromSmartContract(sc);

                SCLogUtility.Log($"Sending the following assets for upload: {md5List}", "VBTCService.TransferOwnership()");

                // Upload to beacon if recipient is not local
                bool result = false;
                if (localAddress == null)
                {
                    result = await P2PClient.BeaconUploadRequest(connectedBeacon, assets, sc.SmartContractUID, toAddress, md5List).WaitAsync(new TimeSpan(0, 0, 10));
                    SCLogUtility.Log($"SC Beacon Upload Request Completed. SCUID: {sc.SmartContractUID}", "VBTCService.TransferOwnership()");
                }
                else
                {
                    result = true;
                }

                if (result == true)
                {
                    // Create asset queue item
                    var aqResult = AssetQueue.CreateAssetQueueItem(sc.SmartContractUID, toAddress, connectedBeacon.Beacons.BeaconLocator, md5List, assets,
                        AssetQueue.TransferType.Upload);
                    SCLogUtility.Log($"SC Asset Queue Items Completed. SCUID: {sc.SmartContractUID}", "VBTCService.TransferOwnership()");

                    if (aqResult)
                    {
                        // Transfer smart contract via standard transfer mechanism
                        _ = Task.Run(() => SmartContractService.TransferSmartContract(sc, toAddress, connectedBeacon, md5List, backupURL, false, null, 0, TransactionType.TKNZ_TX));
                        var success = JsonConvert.SerializeObject(new { Success = true, Message = "vBTC V2 Contract Transfer has been started." });
                        SCLogUtility.Log($"SC Process Completed in CLI. SCUID: {sc.SmartContractUID}. Response: {success}", "VBTCService.TransferOwnership()");
                        return success;
                    }
                    else
                    {
                        return await SCLogUtility.LogAndReturn($"Failed to add upload to Asset Queue - TX terminated. Data: scUID: {sc.SmartContractUID} | toAddress: {toAddress} | Locator: {connectedBeacon.Beacons.BeaconLocator} | MD5List: {md5List} | backupURL: {backupURL}", "VBTCService.TransferOwnership()", false);
                    }
                }
                else
                {
                    return await SCLogUtility.LogAndReturn($"Beacon upload failed. Result was : {result}", "VBTCService.TransferOwnership()", false);
                }
            }
            catch (Exception ex)
            {
                return await SCLogUtility.LogAndReturn($"Unknown Error: {ex}", "VBTCService.TransferOwnership()", false);
            }
        }

        /// <summary>
        /// Transfer vBTC V2 tokens from one address to another
        /// </summary>
        /// <param name="scUID">Smart contract UID</param>
        /// <param name="fromAddress">Sender address</param>
        /// <param name="toAddress">Recipient address</param>
        /// <param name="amount">Amount to transfer</param>
        /// <returns>Transaction hash if successful</returns>
        public static async Task<(bool, string)> TransferVBTC(string scUID, string fromAddress, string toAddress, decimal amount)
        {
            try
            {
                // Get account and validate
                var account = AccountData.GetSingleAccount(fromAddress);
                if (account == null)
                {
                    SCLogUtility.Log($"Account not found: {fromAddress}", "VBTCService.TransferVBTC()");
                    return (false, $"Account not found: {fromAddress}");
                }

                // Get contract and validate
                var vbtcContract = VBTCContractV2.GetContract(scUID);
                if (vbtcContract == null)
                {
                    SCLogUtility.Log($"vBTC V2 contract not found: {scUID}", "VBTCService.TransferVBTC()");
                    return (false, $"vBTC V2 contract not found: {scUID}");
                }

                // Get smart contract state
                var scState = SmartContractStateTrei.GetSmartContractState(scUID);
                if (scState == null)
                {
                    SCLogUtility.Log($"Smart contract state not found: {scUID}", "VBTCService.TransferVBTC()");
                    return (false, $"Smart contract state not found: {scUID}");
                }

                // Calculate balance using v1 pattern: owner gets deposit balance + ledger, non-owner gets just ledger
                decimal ledgerBalance = 0M;
                if (scState.SCStateTreiTokenizationTXes != null && scState.SCStateTreiTokenizationTXes.Any())
                {
                    var transactions = scState.SCStateTreiTokenizationTXes
                        .Where(x => x.FromAddress == fromAddress || x.ToAddress == fromAddress)
                        .ToList();

                    if (transactions.Any())
                    {
                        var received = transactions.Where(x => x.ToAddress == fromAddress).Sum(x => x.Amount);
                        var sent = transactions.Where(x => x.FromAddress == fromAddress).Sum(x => x.Amount);
                        ledgerBalance = received + sent;
                    }
                }

                // Owner's available = BTC deposit balance + ledger (ledger goes negative as owner sends)
                // Non-owner's available = just ledger (received - sent)
                bool isOwner = vbtcContract.OwnerAddress == fromAddress;
                decimal availableBalance = isOwner ? vbtcContract.Balance + ledgerBalance : ledgerBalance;

                // Check sufficient balance
                if (availableBalance < amount)
                {
                    SCLogUtility.Log($"Insufficient balance. Available: {availableBalance}, Requested: {amount}", "VBTCService.TransferVBTC()");
                    return (false, $"Insufficient balance. Available: {availableBalance}, Requested: {amount}");
                }

                toAddress = toAddress.ToAddressNormalize();

                // Create transaction data
                var txData = JsonConvert.SerializeObject(new
                {
                    Function = "TransferVBTCV2()",
                    ContractUID = scUID,
                    FromAddress = fromAddress,
                    ToAddress = toAddress,
                    Amount = amount
                });

                // Build transaction
                var tokenTx = new Transaction
                {
                    Timestamp = TimeUtil.GetTime(),
                    FromAddress = fromAddress,
                    ToAddress = toAddress,
                    Amount = 0.0M, // No VFX transferred, only vBTC
                    Fee = 0.0M,
                    Nonce = AccountStateTrei.GetNextNonce(fromAddress),
                    TransactionType = TransactionType.VBTC_V2_TRANSFER,
                    Data = txData
                };

                tokenTx.Fee = ReserveBlockCore.Services.FeeCalcService.CalculateTXFee(tokenTx);

                // Build and sign transaction
                tokenTx.Build();
                var txHash = tokenTx.Hash;
                var privateKey = account.GetPrivKey;
                var publicKey = account.PublicKey;

                if (privateKey == null)
                {
                    SCLogUtility.Log($"Private key was null for account {fromAddress}", "VBTCService.TransferVBTC()");
                    return (false, $"Private key was null for account {fromAddress}");
                }

                var signature = ReserveBlockCore.Services.SignatureService.CreateSignature(txHash, privateKey, publicKey);
                if (signature == "ERROR")
                {
                    SCLogUtility.Log($"TX Signature Failed. SCUID: {scUID}", "VBTCService.TransferVBTC()");
                    return (false, $"TX Signature Failed. SCUID: {scUID}");
                }

                tokenTx.Signature = signature;

                // Verify transaction
                var result = await TransactionValidatorService.VerifyTX(tokenTx);
                if (result.Item1)
                {
                    await TransactionData.AddTxToWallet(tokenTx, true);
                    await AccountData.UpdateLocalBalance(fromAddress, tokenTx.Fee + tokenTx.Amount);
                    await TransactionData.AddToPool(tokenTx);
                    await P2PClient.SendTXMempool(tokenTx);
                    SCLogUtility.Log($"vBTC V2 Transfer TX Success. SCUID: {scUID}, TxHash: {tokenTx.Hash}", "VBTCService.TransferVBTC()");
                    return (true, tokenTx.Hash);
                }
                else
                {
                    SCLogUtility.Log($"vBTC V2 Transfer TX Verify Failed: {scUID}. Result: {result.Item2}", "VBTCService.TransferVBTC()");
                    return (false, $"TX Verify Failed: {result.Item2}");
                }
            }
            catch (Exception ex)
            {
                SCLogUtility.Log($"vBTC V2 Transfer Error: {ex.Message}", "VBTCService.TransferVBTC()");
                return (false, $"Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Request withdrawal of vBTC to Bitcoin address.
        /// Any address with a vBTC balance in the contract can request a withdrawal — not just the owner.
        /// </summary>
        /// <param name="scUID">Smart contract UID</param>
        /// <param name="requestorAddress">Address requesting withdrawal (any address with a vBTC balance)</param>
        /// <param name="btcAddress">Bitcoin destination address</param>
        /// <param name="amount">Amount to withdraw</param>
        /// <param name="feeRate">Bitcoin fee rate (sats/vB)</param>
        /// <returns>Withdrawal request transaction hash</returns>
        public static async Task<(bool, string)> RequestWithdrawal(string scUID, string requestorAddress, string btcAddress, decimal amount, int feeRate)
        {
            try
            {
                // Get account and validate
                var account = AccountData.GetSingleAccount(requestorAddress);
                if (account == null)
                {
                    SCLogUtility.Log($"Account not found: {requestorAddress}", "VBTCService.RequestWithdrawal()");
                    return (false, $"Account not found: {requestorAddress}");
                }

                // Get contract and validate
                var vbtcContract = VBTCContractV2.GetContract(scUID);
                if (vbtcContract == null)
                {
                    SCLogUtility.Log($"vBTC V2 contract not found: {scUID}", "VBTCService.RequestWithdrawal()");
                    return (false, $"vBTC V2 contract not found: {scUID}");
                }

                // FIND-003 FIX: Check if THIS USER already has an active withdrawal request (per-user tracking)
                var existingRequest = VBTCWithdrawalRequest.GetActiveRequest(requestorAddress, scUID);
                if (existingRequest != null)
                {
                    SCLogUtility.Log($"Active withdrawal already exists for user {requestorAddress}. Request Hash: {existingRequest.TransactionHash}", "VBTCService.RequestWithdrawal()");
                    return (false, $"You already have an active withdrawal request. Complete it before starting a new one. Request Hash: {existingRequest.TransactionHash}");
                }

                // Get smart contract state and validate balance
                var scState = SmartContractStateTrei.GetSmartContractState(scUID);
                if (scState == null)
                {
                    SCLogUtility.Log($"Smart contract state not found: {scUID}", "VBTCService.RequestWithdrawal()");
                    return (false, $"Smart contract state not found: {scUID}");
                }

                // Calculate balance using v1 pattern: owner gets deposit balance + ledger
                decimal ledgerBalance = 0M;
                if (scState.SCStateTreiTokenizationTXes != null && scState.SCStateTreiTokenizationTXes.Any())
                {
                    var transactions = scState.SCStateTreiTokenizationTXes
                        .Where(x => x.FromAddress == requestorAddress || x.ToAddress == requestorAddress)
                        .ToList();

                    if (transactions.Any())
                    {
                        var received = transactions.Where(x => x.ToAddress == requestorAddress).Sum(x => x.Amount);
                        var sent = transactions.Where(x => x.FromAddress == requestorAddress).Sum(x => x.Amount);
                        ledgerBalance = received + sent;
                    }
                }

                // Owner's available = BTC deposit balance + ledger
                // Non-owner's available = just ledger
                bool isOwner = vbtcContract.OwnerAddress == requestorAddress;
                decimal availableBalance = isOwner ? vbtcContract.Balance + ledgerBalance : ledgerBalance;

                // Check sufficient balance
                if (availableBalance < amount)
                {
                    SCLogUtility.Log($"Insufficient balance. Available: {availableBalance}, Requested: {amount}", "VBTCService.RequestWithdrawal()");
                    return (false, $"Insufficient balance. Available: {availableBalance}, Requested: {amount}");
                }

                btcAddress = btcAddress.ToBTCAddressNormalize();

                // Create transaction data
                var txData = JsonConvert.SerializeObject(new
                {
                    Function = "VBTCWithdrawalRequest()",
                    ContractUID = scUID,
                    RequestorAddress = requestorAddress,
                    BTCAddress = btcAddress,
                    Amount = amount,
                    FeeRate = feeRate
                });

                // Build transaction — FromAddress and ToAddress are both the requestor's address
                var withdrawalTx = new Transaction
                {
                    Timestamp = TimeUtil.GetTime(),
                    FromAddress = requestorAddress,
                    ToAddress = requestorAddress, // The address of the balance owner requesting withdrawal
                    Amount = 0.0M,
                    Fee = 0.0M,
                    Nonce = AccountStateTrei.GetNextNonce(requestorAddress),
                    TransactionType = TransactionType.VBTC_V2_WITHDRAWAL_REQUEST,
                    Data = txData
                };

                withdrawalTx.Fee = ReserveBlockCore.Services.FeeCalcService.CalculateTXFee(withdrawalTx);

                // Build and sign transaction
                withdrawalTx.Build();
                var txHash = withdrawalTx.Hash;
                
                var privateKey = account.GetPrivKey;
                var publicKey = account.PublicKey;

                if (privateKey == null)
                {
                    SCLogUtility.Log($"Private key was null for account {requestorAddress}", "VBTCService.RequestWithdrawal()");
                    return (false, $"Private key was null for account {requestorAddress}");
                }

                var signature = ReserveBlockCore.Services.SignatureService.CreateSignature(txHash, privateKey, publicKey);
                if (signature == "ERROR")
                {
                    SCLogUtility.Log($"TX Signature Failed. SCUID: {scUID}", "VBTCService.RequestWithdrawal()");
                    return (false, $"TX Signature Failed. SCUID: {scUID}");
                }

                withdrawalTx.Signature = signature;

                // Verify transaction
                var result = await TransactionValidatorService.VerifyTX(withdrawalTx);
                if (result.Item1)
                {
                    await TransactionData.AddTxToWallet(withdrawalTx, true);
                    await AccountData.UpdateLocalBalance(requestorAddress, withdrawalTx.Fee + withdrawalTx.Amount);
                    await TransactionData.AddToPool(withdrawalTx);
                    await P2PClient.SendTXMempool(withdrawalTx);
                    SCLogUtility.Log($"vBTC V2 Withdrawal Request TX Success. SCUID: {scUID}, TxHash: {withdrawalTx.Hash}", "VBTCService.RequestWithdrawal()");
                    return (true, withdrawalTx.Hash);
                }
                else
                {
                    SCLogUtility.Log($"vBTC V2 Withdrawal Request TX Verify Failed: {scUID}. Result: {result.Item2}", "VBTCService.RequestWithdrawal()");
                    return (false, $"TX Verify Failed: {result.Item2}");
                }
            }
            catch (Exception ex)
            {
                SCLogUtility.Log($"vBTC V2 Withdrawal Request Error: {ex.Message}", "VBTCService.RequestWithdrawal()");
                return (false, $"Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Complete withdrawal by coordinating FROST signing and broadcasting Bitcoin transaction
        /// </summary>
        /// <param name="scUID">Smart contract UID</param>
        /// <param name="withdrawalRequestHash">Hash of withdrawal request transaction</param>
        /// <returns>Completion transaction hash and Bitcoin transaction hash</returns>
        public static async Task<(bool Success, string VFXTxHash, string BTCTxHash, string ErrorMessage)> CompleteWithdrawal(string scUID, string withdrawalRequestHash)
        {
            try
            {
                // FIND-027 Fix: Safety guard — CompleteWithdrawal requires FROST signing which can only
                // run on a validator node. If this is a non-validator, fail fast with a clear error
                // instead of crashing with a NullReferenceException deep in the FROST signing flow.
                if (string.IsNullOrEmpty(Globals.ValidatorAddress))
                {
                    SCLogUtility.Log($"CompleteWithdrawal called on non-validator node. This must be delegated to a validator.", "VBTCService.CompleteWithdrawal()");
                    return (false, string.Empty, string.Empty, 
                        "This node is not a validator and cannot coordinate FROST signing. The withdrawal must be delegated to a remote validator.");
                }

                // Get contract — try local DB first, fall back to State Trei for remote validators
                var vbtcContract = VBTCContractV2.GetContract(scUID);
                bool hasLocalContract = vbtcContract != null;

                // If local DB doesn't have the contract (e.g. remote validator node), reconstruct
                // the needed data from the State Trei + SmartContractMain which all nodes share.
                string depositAddress = null;
                int totalRegisteredValidators = 0;
                long lastValidatorActivityBlock = 0;

                if (vbtcContract != null)
                {
                    depositAddress = vbtcContract.DepositAddress;
                    totalRegisteredValidators = vbtcContract.TotalRegisteredValidators;
                    lastValidatorActivityBlock = vbtcContract.LastValidatorActivityBlock;
                }
                else
                {
                    SCLogUtility.Log($"VBTCContractV2 not in local DB for {scUID}, falling back to State Trei / SmartContractMain", "VBTCService.CompleteWithdrawal()");

                    // Get smart contract from the shared SmartContractMain database
                    var sc = SmartContractMain.SmartContractData.GetSmartContract(scUID);
                    if (sc == null || sc.Features == null)
                    {
                        SCLogUtility.Log($"Smart contract not found in SmartContractMain either: {scUID}", "VBTCService.CompleteWithdrawal()");
                        return (false, string.Empty, string.Empty, $"vBTC V2 contract not found in local DB or SmartContractMain: {scUID}");
                    }

                    var tknzFeature = sc.Features
                        .Where(x => x.FeatureName == FeatureName.TokenizationV2)
                        .Select(x => x.FeatureFeatures)
                        .FirstOrDefault();

                    if (tknzFeature == null)
                    {
                        SCLogUtility.Log($"No TokenizationV2 feature on contract: {scUID}", "VBTCService.CompleteWithdrawal()");
                        return (false, string.Empty, string.Empty, $"Contract {scUID} has no TokenizationV2 feature");
                    }

                    var tknz = (TokenizationV2Feature)tknzFeature;
                    depositAddress = tknz.DepositAddress;
                    totalRegisteredValidators = tknz.ValidatorAddressesSnapshot?.Count ?? 0;
                    lastValidatorActivityBlock = tknz.ProofBlockHeight; // Default to DKG completion block

                    SCLogUtility.Log($"Resolved from State Trei — DepositAddress: {depositAddress}, Validators: {totalRegisteredValidators}, ActivityBlock: {lastValidatorActivityBlock}", "VBTCService.CompleteWithdrawal()");
                }

                if (string.IsNullOrEmpty(depositAddress))
                {
                    SCLogUtility.Log($"Deposit address is empty for contract: {scUID}", "VBTCService.CompleteWithdrawal()");
                    return (false, string.Empty, string.Empty, $"Deposit address not found for contract: {scUID}");
                }

                // FIND-003 FIX: Look up withdrawal request using per-user tracking
                var withdrawalRequest = VBTCWithdrawalRequest.GetByTransactionHash(withdrawalRequestHash);
                if (withdrawalRequest == null)
                {
                    SCLogUtility.Log($"Withdrawal request not found for hash: {withdrawalRequestHash}", "VBTCService.CompleteWithdrawal()");
                    return (false, string.Empty, string.Empty, $"Withdrawal request not found for hash: {withdrawalRequestHash}");
                }

                // Validate the request is not already completed
                if (withdrawalRequest.IsCompleted)
                {
                    SCLogUtility.Log($"Withdrawal request already completed: {withdrawalRequestHash}", "VBTCService.CompleteWithdrawal()");
                    return (false, string.Empty, string.Empty, $"Withdrawal request already completed: {withdrawalRequestHash}");
                }

                // ============================================================
                // FROST INTEGRATION: Execute Bitcoin Withdrawal Transaction
                // ============================================================
                
                SCLogUtility.Log($"Starting FROST withdrawal for contract {scUID}", "VBTCService.CompleteWithdrawal()");

                // Get active validators for FROST signing
                var validators = VBTCValidator.GetActiveValidators();
                if (validators == null || !validators.Any())
                {
                    SCLogUtility.Log($"No active validators available for FROST signing", "VBTCService.CompleteWithdrawal()");
                    return (false, string.Empty, string.Empty, "No active validators available for FROST signing");
                }

                // Phase 5: Calculate DYNAMIC adjusted threshold based on validator availability
                int adjustedThreshold = VBTCThresholdCalculator.CalculateAdjustedThreshold(
                    totalRegisteredValidators,
                    validators.Count,
                    lastValidatorActivityBlock,
                    Globals.LastBlock.Height
                );
                
                // Calculate required validators based on adjusted threshold
                int requiredValidators = VBTCThresholdCalculator.CalculateRequiredValidators(adjustedThreshold, validators.Count);
                
                // Log threshold information
                string thresholdInfo = VBTCThresholdCalculator.GetThresholdExplanation(
                    totalRegisteredValidators,
                    validators.Count,
                    lastValidatorActivityBlock,
                    Globals.LastBlock.Height
                );
                SCLogUtility.Log($"Threshold calculation: {thresholdInfo}", "VBTCService.CompleteWithdrawal()");
                
                if (validators.Count < requiredValidators)
                {
                    SCLogUtility.Log($"Insufficient validators. Have: {validators.Count}, Need: {requiredValidators} (Adjusted threshold: {adjustedThreshold}%)", "VBTCService.CompleteWithdrawal()");
                    return (false, string.Empty, string.Empty, $"Insufficient validators. Have: {validators.Count}, Need: {requiredValidators} (Adjusted threshold: {adjustedThreshold}%)");
                }

                // Get withdrawal details — prefer contract Active* fields (set by StateData when TX is mined),
                // fall back to the VBTCWithdrawalRequest record (handles Raw path, remote validators, and timing edge cases)
                decimal withdrawalAmount;
                string btcDestination;

                if (hasLocalContract 
                    && vbtcContract!.ActiveWithdrawalAmount.HasValue && vbtcContract.ActiveWithdrawalAmount.Value > 0
                    && !string.IsNullOrEmpty(vbtcContract.ActiveWithdrawalBTCDestination))
                {
                    withdrawalAmount = vbtcContract.ActiveWithdrawalAmount.Value;
                    btcDestination = vbtcContract.ActiveWithdrawalBTCDestination;
                }
                else if (withdrawalRequest.Amount > 0 && !string.IsNullOrEmpty(withdrawalRequest.BTCDestination))
                {
                    // Fall back to withdrawal request record (Raw path or TX not yet mined into block)
                    SCLogUtility.Log($"Using withdrawal request record for details (contract Active* fields not yet populated). Amount: {withdrawalRequest.Amount}, Dest: {withdrawalRequest.BTCDestination}",
                        "VBTCService.CompleteWithdrawal()");
                    withdrawalAmount = withdrawalRequest.Amount;
                    btcDestination = withdrawalRequest.BTCDestination;
                }
                else
                {
                    SCLogUtility.Log($"Invalid withdrawal details in both contract and request record", "VBTCService.CompleteWithdrawal()");
                    return (false, string.Empty, string.Empty, "Invalid withdrawal details — amount/destination not found in contract or request record");
                }
                long feeRate = withdrawalRequest.FeeRate != 0 ? withdrawalRequest.FeeRate : 10; // Default fee rate (sats/vB) - TODO: Get from withdrawal request

                // Execute FROST withdrawal (build, sign, broadcast)
                SCLogUtility.Log($"Executing FROST withdrawal: {withdrawalAmount} BTC to {btcDestination}", "VBTCService.CompleteWithdrawal()");
                
                var btcResult = await BitcoinTransactionService.ExecuteFROSTWithdrawal(
                    depositAddress,
                    btcDestination,
                    withdrawalAmount,
                    feeRate,
                    scUID,
                    validators,
                    adjustedThreshold
                );

                if (!btcResult.Success)
                {
                    SCLogUtility.Log($"Bitcoin transaction failed: {btcResult.ErrorMessage}", "VBTCService.CompleteWithdrawal()");
                    return (false, string.Empty, string.Empty, $"Bitcoin transaction failed: {btcResult.ErrorMessage}");
                }

                string btcTxHash = btcResult.TxHash;
                SCLogUtility.Log($"Bitcoin transaction successful. TxHash: {btcTxHash}", "VBTCService.CompleteWithdrawal()");

                // ============================================================
                // End FROST Integration - Continue with VFX transaction
                // ============================================================

                // Use validator address or first available account for transaction creation
                string fromAddress = !string.IsNullOrEmpty(Globals.ValidatorAddress) ? Globals.ValidatorAddress : null;
                if (string.IsNullOrEmpty(fromAddress))
                {
                    var accounts = AccountData.GetAccounts();
                    if (accounts == null || accounts.Count() == 0)
                    {
                        SCLogUtility.Log($"No accounts available to create completion transaction", "VBTCService.CompleteWithdrawal()");
                        return (false, string.Empty, btcTxHash, "No accounts available to create completion transaction");
                    }
                    fromAddress = accounts.FindAll().First().Address;
                }

                var account = AccountData.GetSingleAccount(fromAddress);
                if (account == null)
                {
                    SCLogUtility.Log($"Account not found: {fromAddress}", "VBTCService.CompleteWithdrawal()");
                    return (false, string.Empty, btcTxHash, $"Account not found: {fromAddress}");
                }

                // Create transaction data (use resolved withdrawal details, not contract Active* fields which may be null)
                var txData = JsonConvert.SerializeObject(new
                {
                    Function = "VBTCWithdrawalComplete()",
                    ContractUID = scUID,
                    WithdrawalRequestHash = withdrawalRequestHash,
                    BTCTransactionHash = btcTxHash,
                    Amount = withdrawalAmount,
                    Destination = btcDestination
                });

                // Build transaction
                var completionTx = new Transaction
                {
                    Timestamp = TimeUtil.GetTime(),
                    FromAddress = fromAddress,
                    ToAddress = scUID,
                    Amount = 0.0M,
                    Fee = 0.0M,
                    Nonce = AccountStateTrei.GetNextNonce(fromAddress),
                    TransactionType = TransactionType.VBTC_V2_WITHDRAWAL_COMPLETE,
                    Data = txData
                };

                completionTx.Fee = ReserveBlockCore.Services.FeeCalcService.CalculateTXFee(completionTx);
                
                // Build and sign transaction
                completionTx.Build();
                var txHash = completionTx.Hash;
                
                var privateKey = account.GetPrivKey;
                var publicKey = account.PublicKey;

                if (privateKey == null)
                {
                    SCLogUtility.Log($"Private key was null for account {fromAddress}", "VBTCService.CompleteWithdrawal()");
                    return (false, string.Empty, btcTxHash, $"Private key was null for account {fromAddress}");
                }

                var signature = ReserveBlockCore.Services.SignatureService.CreateSignature(txHash, privateKey, publicKey);
                if (signature == "ERROR")
                {
                    SCLogUtility.Log($"TX Signature Failed. SCUID: {scUID}", "VBTCService.CompleteWithdrawal()");
                    return (false, string.Empty, btcTxHash, $"TX Signature Failed. SCUID: {scUID}");
                }

                completionTx.Signature = signature;

                // Verify transaction
                var result = await TransactionValidatorService.VerifyTX(completionTx);
                if (result.Item1)
                {
                    await TransactionData.AddTxToWallet(completionTx, true);
                    await AccountData.UpdateLocalBalance(fromAddress, completionTx.Fee + completionTx.Amount);
                    await TransactionData.AddToPool(completionTx);
                    await P2PClient.SendTXMempool(completionTx);
                    
                    // Phase 5: Update activity tracking after successful withdrawal (only if local contract exists)
                    if (hasLocalContract && vbtcContract != null)
                    {
                        vbtcContract.LastValidatorActivityBlock = Globals.LastBlock.Height;
                        VBTCContractV2.UpdateContract(vbtcContract);
                        SCLogUtility.Log($"Updated LastValidatorActivityBlock to {Globals.LastBlock.Height}", "VBTCService.CompleteWithdrawal()");
                    }
                    
                    SCLogUtility.Log($"vBTC V2 Withdrawal Complete TX Success. SCUID: {scUID}, TxHash: {completionTx.Hash}, BTCTxHash: {btcTxHash}", "VBTCService.CompleteWithdrawal()");
                    return (true, completionTx.Hash, btcTxHash, string.Empty);
                }
                else
                {
                    SCLogUtility.Log($"vBTC V2 Withdrawal Complete TX Verify Failed: {scUID}. Result: {result.Item2}", "VBTCService.CompleteWithdrawal()");
                    return (false, string.Empty, btcTxHash, $"TX Verify Failed: {result.Item2}");
                }
            }
            catch (Exception ex)
            {
                SCLogUtility.Log($"vBTC V2 Withdrawal Complete Error: {ex.Message}", "VBTCService.CompleteWithdrawal()");
                return (false, string.Empty, string.Empty, $"Error: {ex.Message}");
            }
        }
    }
}
