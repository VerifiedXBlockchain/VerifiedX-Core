using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using ReserveBlockCore.Models.SmartContracts;
using ReserveBlockCore.Models;
using ReserveBlockCore.Services;
using ReserveBlockCore.Utilities;
using System.Text;
using ReserveBlockCore.Bitcoin.Models;
using System.IO;
using System;
using ReserveBlockCore.Data;
using ReserveBlockCore.P2P;

namespace ReserveBlockCore.Bitcoin.Services
{
    /// <summary>
    /// Service for vBTC V2 (MPC-based tokenized Bitcoin) operations
    /// </summary>
    public class VBTCService
    {
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

                // Calculate balance
                decimal balance = 0M;
                if (scState.SCStateTreiTokenizationTXes != null && scState.SCStateTreiTokenizationTXes.Any())
                {
                    var transactions = scState.SCStateTreiTokenizationTXes
                        .Where(x => x.FromAddress == fromAddress || x.ToAddress == fromAddress)
                        .ToList();

                    if (transactions.Any())
                    {
                        var received = transactions.Where(x => x.ToAddress == fromAddress).Sum(x => x.Amount);
                        var sent = transactions.Where(x => x.FromAddress == fromAddress).Sum(x => x.Amount);
                        balance = received - sent;
                    }
                }

                // Check sufficient balance
                if (balance < amount)
                {
                    SCLogUtility.Log($"Insufficient balance. Available: {balance}, Requested: {amount}", "VBTCService.TransferVBTC()");
                    return (false, $"Insufficient balance. Available: {balance}, Requested: {amount}");
                }

                // Create transaction data
                var txData = JsonConvert.SerializeObject(new
                {
                    Function = "VBTCTransfer()",
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

                // Build and sign transaction
                tokenTx.Build();
                var txHash = tokenTx.Hash;
                tokenTx.Fee = FeeCalcService.CalculateTXFee(tokenTx);

                var privateKey = account.GetPrivKey;
                var publicKey = account.PublicKey;

                if (privateKey == null)
                {
                    SCLogUtility.Log($"Private key was null for account {fromAddress}", "VBTCService.TransferVBTC()");
                    return (false, $"Private key was null for account {fromAddress}");
                }

                var signature = SignatureService.CreateSignature(txHash, privateKey, publicKey);
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
                    TransactionData.AddTxToWallet(tokenTx, true);
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
        /// Request withdrawal of vBTC to Bitcoin address
        /// </summary>
        /// <param name="scUID">Smart contract UID</param>
        /// <param name="ownerAddress">Owner address</param>
        /// <param name="btcAddress">Bitcoin destination address</param>
        /// <param name="amount">Amount to withdraw</param>
        /// <param name="feeRate">Bitcoin fee rate (sats/vB)</param>
        /// <returns>Withdrawal request transaction hash</returns>
        public static async Task<(bool, string)> RequestWithdrawal(string scUID, string ownerAddress, string btcAddress, decimal amount, int feeRate)
        {
            try
            {
                // Get account and validate
                var account = AccountData.GetSingleAccount(ownerAddress);
                if (account == null)
                {
                    SCLogUtility.Log($"Account not found: {ownerAddress}", "VBTCService.RequestWithdrawal()");
                    return (false, $"Account not found: {ownerAddress}");
                }

                // Get contract and validate
                var vbtcContract = VBTCContractV2.GetContract(scUID);
                if (vbtcContract == null)
                {
                    SCLogUtility.Log($"vBTC V2 contract not found: {scUID}", "VBTCService.RequestWithdrawal()");
                    return (false, $"vBTC V2 contract not found: {scUID}");
                }

                // Validate owner
                if (vbtcContract.OwnerAddress != ownerAddress)
                {
                    SCLogUtility.Log($"Only contract owner can request withdrawal. Owner: {vbtcContract.OwnerAddress}", "VBTCService.RequestWithdrawal()");
                    return (false, "Only contract owner can request withdrawal");
                }

                // Check no active withdrawal exists
                if (vbtcContract.WithdrawalStatus != VBTCWithdrawalStatus.None)
                {
                    SCLogUtility.Log($"Active withdrawal already exists. Status: {vbtcContract.WithdrawalStatus}", "VBTCService.RequestWithdrawal()");
                    return (false, $"Active withdrawal already exists. Status: {vbtcContract.WithdrawalStatus}");
                }

                // Get smart contract state and validate balance
                var scState = SmartContractStateTrei.GetSmartContractState(scUID);
                if (scState == null)
                {
                    SCLogUtility.Log($"Smart contract state not found: {scUID}", "VBTCService.RequestWithdrawal()");
                    return (false, $"Smart contract state not found: {scUID}");
                }

                // Calculate balance
                decimal balance = 0M;
                if (scState.SCStateTreiTokenizationTXes != null && scState.SCStateTreiTokenizationTXes.Any())
                {
                    var transactions = scState.SCStateTreiTokenizationTXes
                        .Where(x => x.FromAddress == ownerAddress || x.ToAddress == ownerAddress)
                        .ToList();

                    if (transactions.Any())
                    {
                        var received = transactions.Where(x => x.ToAddress == ownerAddress).Sum(x => x.Amount);
                        var sent = transactions.Where(x => x.FromAddress == ownerAddress).Sum(x => x.Amount);
                        balance = received - sent;
                    }
                }

                // Check sufficient balance
                if (balance < amount)
                {
                    SCLogUtility.Log($"Insufficient balance. Available: {balance}, Requested: {amount}", "VBTCService.RequestWithdrawal()");
                    return (false, $"Insufficient balance. Available: {balance}, Requested: {amount}");
                }

                // Create transaction data
                var txData = JsonConvert.SerializeObject(new
                {
                    Function = "VBTCWithdrawalRequest()",
                    ContractUID = scUID,
                    OwnerAddress = ownerAddress,
                    BTCAddress = btcAddress,
                    Amount = amount,
                    FeeRate = feeRate
                });

                // Build transaction
                var withdrawalTx = new Transaction
                {
                    Timestamp = TimeUtil.GetTime(),
                    FromAddress = ownerAddress,
                    ToAddress = scUID, // Withdrawal target is the contract itself
                    Amount = 0.0M,
                    Fee = 0.0M,
                    Nonce = AccountStateTrei.GetNextNonce(ownerAddress),
                    TransactionType = TransactionType.VBTC_V2_WITHDRAWAL_REQUEST,
                    Data = txData
                };

                // Build and sign transaction
                withdrawalTx.Build();
                var txHash = withdrawalTx.Hash;
                withdrawalTx.Fee = FeeCalcService.CalculateTXFee(withdrawalTx);

                var privateKey = account.GetPrivKey;
                var publicKey = account.PublicKey;

                if (privateKey == null)
                {
                    SCLogUtility.Log($"Private key was null for account {ownerAddress}", "VBTCService.RequestWithdrawal()");
                    return (false, $"Private key was null for account {ownerAddress}");
                }

                var signature = SignatureService.CreateSignature(txHash, privateKey, publicKey);
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
                    TransactionData.AddTxToWallet(withdrawalTx, true);
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
        /// <param name="btcTxHash">Bitcoin transaction hash (from FROST signing)</param>
        /// <returns>Completion transaction hash</returns>
        public static async Task<(bool, string)> CompleteWithdrawal(string scUID, string withdrawalRequestHash, string btcTxHash)
        {
            try
            {
                // Get contract and validate
                var vbtcContract = VBTCContractV2.GetContract(scUID);
                if (vbtcContract == null)
                {
                    SCLogUtility.Log($"vBTC V2 contract not found: {scUID}", "VBTCService.CompleteWithdrawal()");
                    return (false, $"vBTC V2 contract not found: {scUID}");
                }

                // Validate withdrawal status
                if (vbtcContract.WithdrawalStatus != VBTCWithdrawalStatus.Requested)
                {
                    SCLogUtility.Log($"No active withdrawal request. Status: {vbtcContract.WithdrawalStatus}", "VBTCService.CompleteWithdrawal()");
                    return (false, $"No active withdrawal request. Status: {vbtcContract.WithdrawalStatus}");
                }

                // Validate withdrawal request hash matches
                if (vbtcContract.ActiveWithdrawalRequestHash != withdrawalRequestHash)
                {
                    SCLogUtility.Log($"Withdrawal request hash mismatch", "VBTCService.CompleteWithdrawal()");
                    return (false, "Withdrawal request hash mismatch");
                }

                // Use validator address or first available account for transaction creation
                string fromAddress = !string.IsNullOrEmpty(Globals.ValidatorAddress) ? Globals.ValidatorAddress : null;
                if (string.IsNullOrEmpty(fromAddress))
                {
                    var accounts = AccountData.GetAccounts();
                    if (accounts == null || !accounts.Any())
                    {
                        SCLogUtility.Log($"No accounts available to create completion transaction", "VBTCService.CompleteWithdrawal()");
                        return (false, "No accounts available to create completion transaction");
                    }
                    fromAddress = accounts.First().Address;
                }

                var account = AccountData.GetSingleAccount(fromAddress);
                if (account == null)
                {
                    SCLogUtility.Log($"Account not found: {fromAddress}", "VBTCService.CompleteWithdrawal()");
                    return (false, $"Account not found: {fromAddress}");
                }

                // Create transaction data
                var txData = JsonConvert.SerializeObject(new
                {
                    Function = "VBTCWithdrawalComplete()",
                    ContractUID = scUID,
                    WithdrawalRequestHash = withdrawalRequestHash,
                    BTCTransactionHash = btcTxHash,
                    Amount = vbtcContract.ActiveWithdrawalAmount,
                    Destination = vbtcContract.ActiveWithdrawalBTCDestination
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

                // Build and sign transaction
                completionTx.Build();
                var txHash = completionTx.Hash;
                completionTx.Fee = FeeCalcService.CalculateTXFee(completionTx);

                var privateKey = account.GetPrivKey;
                var publicKey = account.PublicKey;

                if (privateKey == null)
                {
                    SCLogUtility.Log($"Private key was null for account {fromAddress}", "VBTCService.CompleteWithdrawal()");
                    return (false, $"Private key was null for account {fromAddress}");
                }

                var signature = SignatureService.CreateSignature(txHash, privateKey, publicKey);
                if (signature == "ERROR")
                {
                    SCLogUtility.Log($"TX Signature Failed. SCUID: {scUID}", "VBTCService.CompleteWithdrawal()");
                    return (false, $"TX Signature Failed. SCUID: {scUID}");
                }

                completionTx.Signature = signature;

                // Verify transaction
                var result = await TransactionValidatorService.VerifyTX(completionTx);
                if (result.Item1)
                {
                    TransactionData.AddTxToWallet(completionTx, true);
                    SCLogUtility.Log($"vBTC V2 Withdrawal Complete TX Success. SCUID: {scUID}, TxHash: {completionTx.Hash}, BTCTxHash: {btcTxHash}", "VBTCService.CompleteWithdrawal()");
                    return (true, completionTx.Hash);
                }
                else
                {
                    SCLogUtility.Log($"vBTC V2 Withdrawal Complete TX Verify Failed: {scUID}. Result: {result.Item2}", "VBTCService.CompleteWithdrawal()");
                    return (false, $"TX Verify Failed: {result.Item2}");
                }
            }
            catch (Exception ex)
            {
                SCLogUtility.Log($"vBTC V2 Withdrawal Complete Error: {ex.Message}", "VBTCService.CompleteWithdrawal()");
                return (false, $"Error: {ex.Message}");
            }
        }
    }
}
