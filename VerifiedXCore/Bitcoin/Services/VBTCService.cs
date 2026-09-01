using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Data;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Models.SmartContracts;
using VerifiedXCore.P2P;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using System;
using System.IO;
using System.Linq;
using System.Text;

namespace VerifiedXCore.Bitcoin.Services
{
    /// <summary>
    /// Service for vBTC V2 (MPC-based tokenized Bitcoin) operations
    /// </summary>
    public class VBTCService
    {
        /// <summary>
        /// Spendable transparent vBTC for <paramref name="fromAddress"/> on contract <paramref name="scUid"/>:
        /// owner = BTC deposit balance + tokenization ledger; non-owner = ledger only (matches <see cref="TransferVBTC"/>).
        /// </summary>
        /// <summary>
        /// Owner ledger component for transparent vBTC balance math:
        /// SUM of ALL tokenization ledger rows involving the owner (so transfer debits and bridge
        /// locks stay debited — the BTC never left the deposit address for those), PLUS an add-back
        /// of completed withdrawal amounts, whose burn rows are already reflected in the ElectrumX
        /// deposit balance and would otherwise be double-counted.
        /// Owner available = ElectrumX deposit balance + this value.
        /// </summary>
        public static decimal GetOwnerLedgerBalance(SmartContractStateTrei scState, string ownerAddress, long currentHeight)
        {
            decimal ledgerBalance = 0M;
            if (scState.SCStateTreiTokenizationTXes != null && scState.SCStateTreiTokenizationTXes.Any())
            {
                var ownerRows = scState.SCStateTreiTokenizationTXes
                    .Where(x => x.FromAddress == ownerAddress || x.ToAddress == ownerAddress)
                    .ToList();

                if (ownerRows.Any())
                    ledgerBalance = ownerRows.Sum(x => x.Amount);
            }

            ledgerBalance += VBTCWithdrawalRequest.GetCompletedWithdrawalAmount(ownerAddress, scState.SmartContractUID, currentHeight);

            return ledgerBalance;
        }

        /// <summary>
        /// Deposit address for a vBTC contract: the local VBTCContractV2 record when present, else
        /// decompiled from the contract code stored in the state trei — available on ALL nodes, so
        /// owner balances resolve correctly on casters/remote nodes with no local record.
        /// </summary>
        public static string? ResolveDepositAddress(SmartContractStateTrei scState, VBTCContractV2? contract)
        {
            if (!string.IsNullOrEmpty(contract?.DepositAddress))
                return contract.DepositAddress;

            if (string.IsNullOrEmpty(scState?.ContractData))
                return null;

            try
            {
                var scMainDecompile = SmartContractMain.GenerateSmartContractInMemory(scState.ContractData);
                if (scMainDecompile?.Features != null)
                {
                    var tknzFeature = scMainDecompile.Features
                        .Where(x => x.FeatureName == FeatureName.TokenizationV2)
                        .Select(x => x.FeatureFeatures)
                        .FirstOrDefault();

                    if (tknzFeature is TokenizationV2Feature tknz)
                        return tknz.DepositAddress;
                }
            }
            catch (Exception decompileEx)
            {
                ErrorLogUtility.LogError($"Failed to decompile contract {scState.SmartContractUID} for deposit address: {decompileEx.Message}",
                    "VBTCService.ResolveDepositAddress()");
            }
            return null;
        }

        public static async Task<(bool success, decimal availableBalance, string? error)> TryGetAvailableTransparentVbtcBalance(string scUid, string fromAddress, long? blockHeight = null)
        {
            try
            {
                var scState = SmartContractStateTrei.GetSmartContractState(scUid);
                if (scState == null)
                {
                    return (false, 0M, $"Smart contract state not found: {scUid}");
                }

                // Calculate ledger balance from tokenization TXes (consensus data, available on all nodes)
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

                // Determine owner address and deposit address.
                // Try local DB first; fall back to State Trei + in-memory decompile for remote nodes.
                string? ownerAddress = null;
                string? depositAddress = null;
                decimal localCachedBalance = 0M;

                var vbtcContract = VBTCContractV2.GetContract(scUid);
                if (vbtcContract != null)
                {
                    ownerAddress = vbtcContract.OwnerAddress;
                    depositAddress = vbtcContract.DepositAddress;
                    localCachedBalance = vbtcContract.Balance;
                }
                else
                {
                    // Remote node fallback: owner address from State Trei, deposit address from contract code
                    ownerAddress = scState.OwnerAddress;
                    depositAddress = ResolveDepositAddress(scState, null);
                }

                bool isOwner = !string.IsNullOrEmpty(ownerAddress) && ownerAddress == fromAddress;

                if (!isOwner)
                {
                    // Non-owner: balance is purely from the tokenization ledger (works on all nodes)
                    return (true, ledgerBalance, null);
                }

                // Owner: must verify actual BTC deposit balance to prevent inflation.
                // Full ledger sum (transfer debits and bridge locks stay debited) plus an add-back
                // of completed withdrawals whose burn rows the ElectrumX balance already reflects.
                ledgerBalance = GetOwnerLedgerBalance(scState, fromAddress, blockHeight ?? Globals.LastBlock?.Height ?? 0);

                // Query Electrum for real-time balance of the deposit address.
                decimal btcDepositBalance = 0M;
                if (!string.IsNullOrEmpty(depositAddress))
                {
                    try
                    {
                        using var client = await VerifiedXCore.Bitcoin.Bitcoin.ElectrumXClient();
                        if (client != null)
                        {
                            var balance = await client.GetBalance(depositAddress, false);
                            btcDepositBalance = balance.Confirmed / 100_000_000M;
                        }
                        else
                        {
                            // Electrum unavailable — fall back to locally cached balance if we have it
                            btcDepositBalance = localCachedBalance;
                        }
                    }
                    catch (Exception elxEx)
                    {
                        // Electrum query failed — fall back to locally cached balance
                        ErrorLogUtility.LogError($"ElectrumX query failed for deposit balance, using cached: {elxEx.Message}",
                            "VBTCService.TryGetAvailableTransparentVbtcBalance()");
                        btcDepositBalance = localCachedBalance;
                    }
                }

                return (true, btcDepositBalance + ledgerBalance, null);
            }
            catch (Exception ex)
            {
                return (false, 0M, ex.Message);
            }
        }

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

                // Reserve (xRBX) accounts can own vBTC V2 contracts — include them or their
                // deposit-address Balance cache is never refreshed.
                var reserveAccounts = ReserveAccount.GetReserveAccounts();
                if (reserveAccounts != null)
                    allAddresses.AddRange(reserveAccounts.Select(r => r.Address));

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

                var utxoLookup = await BitcoinTransactionService.GetTaprootUTXOs(contract.DepositAddress);
                if (!utxoLookup.Success)
                {
                    // Inconclusive lookup (data sources unreachable) — never overwrite the cached
                    // balance with 0 on a failed lookup; keep the last known value.
                    ErrorLogUtility.LogError($"UTXO lookup inconclusive for {contract.SmartContractUID}; keeping cached balance {contract.Balance}: {utxoLookup.Error}",
                        "VBTCService.ScanSingleContractBalance()");
                    return contract.Balance;
                }

                decimal btcBalance = 0M;

                if (utxoLookup.Utxos.Any())
                {
                    // Sum all UTXO values (in satoshis) and convert to BTC
                    ulong totalSatoshis = 0;
                    foreach (var utxo in utxoLookup.Utxos)
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

                // Normalize destination up front so all checks (incl. xRBX rules) see the
                // resolved address, not an ADNR name.
                toAddress = toAddress.Replace(" ", "").ToAddressNormalize();

                // Check owner account exists. Reserve (xRBX) owners resolve through the
                // reserve store and exit on the reserve lifecycle (unlock delay + callback).
                var account = AccountData.GetSingleAccount(scState.OwnerAddress);
                var rAccount = scState.OwnerAddress.StartsWith("xRBX") ? ReserveAccount.GetReserveAccountSingle(scState.OwnerAddress) : null;

                if (account == null && rAccount == null)
                    return await SCLogUtility.LogAndReturn($"Owner address account not found.", "VBTCService.TransferOwnership()", false);

                bool isReserveOwner = rAccount != null;
                VerifiedXCore.EllipticCurve.PrivateKey? reserveKey = null;
                int reserveUnlockHours = 0;
                if (isReserveOwner)
                {
                    if (toAddress.StartsWith("xRBX"))
                        return await SCLogUtility.LogAndReturn("Reserve-held vBTC contracts can only be transferred to a normal VFX address.", "VBTCService.TransferOwnership()", false);

                    if (!Globals.ReserveAccountUnlockKeys.TryGetValue(scState.OwnerAddress, out var rAUK))
                        return await SCLogUtility.LogAndReturn("Reserve account is not unlocked. Please unlock it first.", "VBTCService.TransferOwnership()", false);

                    reserveUnlockHours = rAUK.UnlockTimeHours;
                    reserveKey = rAccount.GetPrivKey;
                }

                // Validate balance > 0 (including state trei tokenization TXs)
                //0 balance check remove for ownership transfers

                if (Globals.VBTCDefaultAssetOnly)
                {
                    // Step over beacons: default image only, nothing to upload.
                    _ = Task.Run(() => SmartContractService.TransferSmartContract(sc, toAddress, null, "NA", backupURL, isReserveOwner, reserveKey, reserveUnlockHours, TransactionType.TKNZ_TX));
                    var response = JsonConvert.SerializeObject(new { Success = true, Message = isReserveOwner ? "vBTC V2 Contract Transfer has been started (reserve: 24h unlock delay applies, callback-able until then)." : "vBTC V2 Contract Transfer has been started." });
                    SCLogUtility.Log($"SC Process Completed in CLI (beacon-free). SCUID: {sc.SmartContractUID}", "VBTCService.TransferOwnership()");
                    return response;
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
                        _ = Task.Run(() => SmartContractService.TransferSmartContract(sc, toAddress, connectedBeacon, md5List, backupURL, isReserveOwner, reserveKey, reserveUnlockHours, TransactionType.TKNZ_TX));
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
                // Get account and validate. Reserve (xRBX) senders resolve through the
                // reserve-account store and go out on the reserve lifecycle (unlock delay,
                // callback, recovery) — their only permitted destination is a normal VFX address.
                bool isReserveAccount = fromAddress.StartsWith("xRBX");
                var account = !isReserveAccount ? AccountData.GetSingleAccount(fromAddress) : null;
                var rAccount = isReserveAccount ? ReserveAccount.GetReserveAccountSingle(fromAddress) : null;
                if (account == null && rAccount == null)
                {
                    SCLogUtility.Log($"Account not found: {fromAddress}", "VBTCService.TransferVBTC()");
                    return (false, $"Account not found: {fromAddress}");
                }

                // Normalize before any destination checks so an ADNR name resolving to a
                // reserve address can't slip past the pre-flight (consensus still catches it).
                toAddress = toAddress.ToAddressNormalize();

                long? unlockTime = null;
                if (isReserveAccount)
                {
                    if (toAddress.StartsWith("xRBX"))
                        return (false, "Reserve accounts cannot send vBTC to another Reserve Account.");

                    if (Globals.ReserveAccountUnlockKeys.TryGetValue(fromAddress, out var rAUK))
                        unlockTime = TimeUtil.GetReserveTime(rAUK.UnlockTimeHours);
                    else
                        return (false, "Reserve account is no longer unlocked. Please unlock again.");
                }

                var balResult = await TryGetAvailableTransparentVbtcBalance(scUID, fromAddress);
                if (!balResult.success)
                {
                    SCLogUtility.Log(balResult.error ?? "Balance lookup failed", "VBTCService.TransferVBTC()");
                    return (false, balResult.error ?? "Could not resolve vBTC transparent balance.");
                }

                var availableBalance = balResult.availableBalance;
                if (isReserveAccount)
                    availableBalance -= ReserveTransactions.GetPendingVBTCTransferTotal(fromAddress, scUID);

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
                    Data = txData,
                    UnlockTime = unlockTime
                };

                tokenTx.Fee = VerifiedXCore.Services.FeeCalcService.CalculateTXFee(tokenTx);

                // Build and sign transaction
                tokenTx.Build();
                var txHash = tokenTx.Hash;
                var privateKey = !isReserveAccount ? account.GetPrivKey : rAccount.GetPrivKey;
                var publicKey = !isReserveAccount ? account.PublicKey : rAccount.PublicKey;

                if (privateKey == null)
                {
                    SCLogUtility.Log($"Private key was null for account {fromAddress}", "VBTCService.TransferVBTC()");
                    return (false, $"Private key was null for account {fromAddress}");
                }

                var signature = VerifiedXCore.Services.SignatureService.CreateSignature(txHash, privateKey, publicKey);
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
                    if (isReserveAccount)
                    {
                        // Reserve dispatch keeps the reserve balance bookkeeping consistent.
                        // noLockUp: true — VFX Amount is 0, only the fee moves.
                        await VerifiedXCore.Services.WalletService.SendReserveTransaction(tokenTx, rAccount, true);
                    }
                    else
                    {
                        await TransactionData.AddTxToWallet(tokenTx, true);
                        await AccountData.UpdateLocalBalance(fromAddress, tokenTx.Fee + tokenTx.Amount);
                        await TransactionData.AddToPool(tokenTx);
                        await P2PClient.SendTXMempool(tokenTx);
                    }
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
        /// Data.Function marker for a vBTC V2 multi-contract transfer. Distinct from the dead
        /// legacy "TransferVBTCMulti()" TKNZ-era name so the two shapes can never collide.
        /// </summary>
        public const string MultiTransferFunction = "TransferVBTCMultiV2()";

        /// <summary>
        /// Consensus cap on Inputs entries in one multi-contract transfer. The 30kb TX size cap
        /// also bounds it, but an explicit count is deterministic and cheap to check.
        /// </summary>
        public const int MaxMultiTransferInputs = 25;

        /// <summary>
        /// Per-contract vBTC outflows of a committed/candidate VBTC_V2_TRANSFER, for overspend
        /// accounting: multi-shaped Data (multi Function, no top-level ContractUID) yields one
        /// entry per input; anything else yields the single-shape (ContractUID, Amount) pair.
        /// A hybrid Data (multi Function PLUS top-level ContractUID) deliberately parses as
        /// single — that matches how pre-gate nodes validate/apply it, and post-gate the
        /// validator rejects the hybrid shape outright. Empty list on parse failure.
        /// </summary>
        public static List<(string ScUid, decimal Amount)> GetVbtcV2TransferOutflows(Transaction tx)
        {
            var outflows = new List<(string, decimal)>();
            try
            {
                if (tx.TransactionType != TransactionType.VBTC_V2_TRANSFER || string.IsNullOrEmpty(tx.Data))
                    return outflows;

                var jobj = JObject.Parse(tx.Data);
                var function = jobj["Function"]?.ToObject<string>();
                var scUID = jobj["ContractUID"]?.ToObject<string>();

                if (function == MultiTransferFunction && string.IsNullOrEmpty(scUID))
                {
                    var inputs = jobj["Inputs"]?.ToObject<List<VBTCV2MultiTransferInput>>();
                    if (inputs != null)
                    {
                        foreach (var input in inputs)
                        {
                            if (!string.IsNullOrEmpty(input.SCUID) && input.Amount > 0)
                                outflows.Add((input.SCUID, input.Amount));
                        }
                    }
                    return outflows;
                }

                var amount = jobj["Amount"]?.ToObject<decimal?>();
                if (!string.IsNullOrEmpty(scUID) && amount.HasValue && amount.Value > 0)
                    outflows.Add((scUID, amount.Value));
            }
            catch { }
            return outflows;
        }

        /// <summary>
        /// Transfer a total vBTC V2 amount to one recipient, auto-allocated across every contract
        /// the sender holds spendable balance on, as ONE transaction (one nonce, one fee).
        /// Falls back to the plain single-contract path when one contract covers the amount.
        /// Reserve (xRBX) senders are not supported — the reserve deferred-apply lifecycle is
        /// keyed on the single-contract shape.
        /// </summary>
        public static async Task<(bool Success, string Result, List<VBTCV2MultiTransferInput>? Allocations)> TransferVBTCMulti(string fromAddress, string toAddress, decimal totalAmount)
        {
            try
            {
                if (fromAddress.StartsWith("xRBX"))
                    return (false, "Reserve accounts cannot use multi-contract vBTC transfers. Send from a single contract instead.", null);

                if (totalAmount <= 0)
                    return (false, "Amount must be greater than zero.", null);

                if (totalAmount != Math.Round(totalAmount, 8))
                    return (false, "Amount cannot have more than 8 decimal places.", null);

                var account = AccountData.GetSingleAccount(fromAddress);
                if (account == null)
                    return (false, $"Account not found: {fromAddress}", null);

                toAddress = toAddress.ToAddressNormalize();

                // Allocate greedily across spendable contracts: largest available first so the TX
                // uses the fewest inputs (deterministic tie-break on SCUID). Availability matches
                // the single-transfer preflight, minus incomplete withdrawals and any outflows
                // already pending in the local mempool so we never craft a TX our own
                // overspend guards would reject.
                var pendingByContract = new Dictionary<string, decimal>();
                try
                {
                    var pool = TransactionData.GetPool();
                    var pendingTxs = pool.Find(x => x.FromAddress == fromAddress && x.TransactionType == TransactionType.VBTC_V2_TRANSFER).ToList();
                    foreach (var ptx in pendingTxs)
                    {
                        foreach (var (scUid, amt) in GetVbtcV2TransferOutflows(ptx))
                        {
                            pendingByContract.TryGetValue(scUid, out var cur);
                            pendingByContract[scUid] = cur + amt;
                        }
                    }
                }
                catch { }

                var candidates = new List<(string ScUid, decimal Available)>();
                var contracts = VBTCContractV2.GetAllContracts();
                if (contracts != null)
                {
                    foreach (var contract in contracts)
                    {
                        var scUid = contract.SmartContractUID;
                        if (string.IsNullOrEmpty(scUid) || candidates.Any(c => c.ScUid == scUid))
                            continue;

                        var balResult = await TryGetAvailableTransparentVbtcBalance(scUid, fromAddress);
                        if (!balResult.success)
                            continue;

                        var available = balResult.availableBalance;
                        available -= VBTCWithdrawalRequest.GetIncompleteWithdrawalAmount(fromAddress, scUid);
                        if (pendingByContract.TryGetValue(scUid, out var pendingOut))
                            available -= pendingOut;

                        available = Math.Round(available, 8);
                        if (available > 0)
                            candidates.Add((scUid, available));
                    }
                }

                if (!candidates.Any())
                    return (false, "No spendable vBTC balance found for this address.", null);

                candidates = candidates
                    .OrderByDescending(c => c.Available)
                    .ThenBy(c => c.ScUid, StringComparer.Ordinal)
                    .ToList();

                var allocations = new List<VBTCV2MultiTransferInput>();
                var remaining = totalAmount;
                foreach (var candidate in candidates)
                {
                    if (remaining <= 0)
                        break;

                    var useAmount = Math.Round(Math.Min(remaining, candidate.Available), 8);
                    if (useAmount < 0.00000001M)
                        continue;

                    allocations.Add(new VBTCV2MultiTransferInput { SCUID = candidate.ScUid, Amount = useAmount });
                    remaining -= useAmount;
                }

                if (remaining > 0)
                {
                    var totalAvailable = candidates.Sum(c => c.Available);
                    return (false, $"Insufficient combined vBTC balance. Available: {totalAvailable}, Requested: {totalAmount}", null);
                }

                if (allocations.Count == 1)
                {
                    // One contract covers it — use the plain single-contract path (works even
                    // before the multi activation height, and keeps the on-chain shape simple).
                    var singleResult = await TransferVBTC(allocations[0].SCUID, fromAddress, toAddress, totalAmount);
                    return (singleResult.Item1, singleResult.Item2, allocations);
                }

                if (Globals.LastBlock.Height + 1 < Globals.V2TransferMultiHeight)
                    return (false, "Multi-contract vBTC transfers are not active on this network yet. Send from a single contract instead.", null);

                if (allocations.Count > MaxMultiTransferInputs)
                    return (false, $"Transfer would require {allocations.Count} contract inputs (max {MaxMultiTransferInputs}). Send a smaller amount or consolidate first.", null);

                var txData = JsonConvert.SerializeObject(new
                {
                    Function = MultiTransferFunction,
                    FromAddress = fromAddress,
                    ToAddress = toAddress,
                    TotalAmount = totalAmount,
                    Inputs = allocations
                });

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

                tokenTx.Fee = VerifiedXCore.Services.FeeCalcService.CalculateTXFee(tokenTx);

                tokenTx.Build();
                var privateKey = account.GetPrivKey;
                if (privateKey == null)
                    return (false, $"Private key was null for account {fromAddress}", null);

                var signature = VerifiedXCore.Services.SignatureService.CreateSignature(tokenTx.Hash, privateKey, account.PublicKey);
                if (signature == "ERROR")
                    return (false, "TX Signature Failed.", null);

                tokenTx.Signature = signature;

                var result = await TransactionValidatorService.VerifyTX(tokenTx);
                if (result.Item1)
                {
                    await TransactionData.AddTxToWallet(tokenTx, true);
                    await AccountData.UpdateLocalBalance(fromAddress, tokenTx.Fee + tokenTx.Amount);
                    await TransactionData.AddToPool(tokenTx);
                    await P2PClient.SendTXMempool(tokenTx);

                    SCLogUtility.Log($"vBTC V2 Multi Transfer TX Success. Inputs: {allocations.Count}, TxHash: {tokenTx.Hash}", "VBTCService.TransferVBTCMulti()");
                    return (true, tokenTx.Hash, allocations);
                }

                SCLogUtility.Log($"vBTC V2 Multi Transfer TX Verify Failed: {result.Item2}", "VBTCService.TransferVBTCMulti()");
                return (false, $"TX Verify Failed: {result.Item2}", null);
            }
            catch (Exception ex)
            {
                SCLogUtility.Log($"vBTC V2 Multi Transfer Error: {ex.Message}", "VBTCService.TransferVBTCMulti()");
                return (false, $"Error: {ex.Message}", null);
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
                // Reserve-held vBTC is locked: consensus denies xRBX withdrawal requests.
                // Fail here with the real reason instead of a generic account-not-found.
                if (requestorAddress.StartsWith("xRBX"))
                    return (false, "Reserve accounts cannot request BTC withdrawals. Move the vBTC to a normal VFX address first.");

                // Get account and validate
                var account = AccountData.GetSingleAccount(requestorAddress);
                if (account == null)
                {
                    SCLogUtility.Log($"Account not found: {requestorAddress}", "VBTCService.RequestWithdrawal()");
                    return (false, $"Account not found: {requestorAddress}");
                }

                // FIND-003 FIX: Check if THIS USER already has an active withdrawal request (per-user tracking)
                var existingRequest = VBTCWithdrawalRequest.GetActiveRequest(requestorAddress, scUID);
                if (existingRequest != null)
                {
                    SCLogUtility.Log($"Active withdrawal already exists for user {requestorAddress}. Request Hash: {existingRequest.TransactionHash}", "VBTCService.RequestWithdrawal()");
                    return (false, $"You already have an active withdrawal request. Complete it before starting a new one. Request Hash: {existingRequest.TransactionHash}");
                }

                var balResult = await TryGetAvailableTransparentVbtcBalance(scUID, requestorAddress);
                if (!balResult.success)
                {
                    SCLogUtility.Log(balResult.error ?? "Balance lookup failed", "VBTCService.RequestWithdrawal()");
                    return (false, balResult.error ?? "Could not resolve vBTC transparent balance.");
                }

                if (balResult.availableBalance < amount)
                {
                    SCLogUtility.Log($"Insufficient balance. Available: {balResult.availableBalance}, Requested: {amount}", "VBTCService.RequestWithdrawal()");
                    return (false, $"Insufficient balance. Available: {balResult.availableBalance}, Requested: {amount}");
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

                withdrawalTx.Fee = VerifiedXCore.Services.FeeCalcService.CalculateTXFee(withdrawalTx);

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

                var signature = VerifiedXCore.Services.SignatureService.CreateSignature(txHash, privateKey, publicKey);
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
        public static async Task<(bool Success, string VFXTxHash, string BTCTxHash, string ErrorMessage, FROST.Models.FrostCeremonyOutcome? Ceremony)> CompleteWithdrawal(
            string scUID, string withdrawalRequestHash,
            decimal? delegatedAmount = null, string? delegatedBTCDestination = null, int? delegatedFeeRate = null,
            bool signOnly = false,
            FROST.Models.PreSignedLeaderAuth? preSignedAuth = null)
        {
            try
            {
                // Unified MPC: Any VFX wallet owner can now coordinate FROST signing directly.
                // The leader check in FrostStartup.cs has been relaxed to accept any valid VFX
                // signature, and FrostMPCService signs with AddressSignature (owner's key).

                // Get contract — try local DB first, fall back to State Trei for remote validators
                var vbtcContract = VBTCContractV2.GetContract(scUID);
                bool hasLocalContract = vbtcContract != null;

                // If local DB doesn't have the contract (e.g. remote validator node), reconstruct
                // the needed data from the State Trei + SmartContractMain which all nodes share.
                string depositAddress = null;
                int totalRegisteredValidators = 0;
                long lastValidatorActivityBlock = 0;
                List<string> snapshotAddresses = null; // DKG participant addresses from the contract

                if (vbtcContract != null)
                {
                    depositAddress = vbtcContract.DepositAddress;
                    totalRegisteredValidators = vbtcContract.TotalRegisteredValidators;
                    lastValidatorActivityBlock = vbtcContract.LastValidatorActivityBlock;
                    
                    // Try to get snapshot from local contract first
                    // If not available locally, we'll try State Trei below
                }
                
                // Always try to resolve the snapshot from State Trei (authoritative source for DKG participants)
                {
                    var scStateTreiRec = SmartContractStateTrei.GetSmartContractState(scUID);
                    if (scStateTreiRec != null && !string.IsNullOrEmpty(scStateTreiRec.ContractData))
                    {
                        try
                        {
                            var scMainDecompile = SmartContractMain.GenerateSmartContractInMemory(scStateTreiRec.ContractData);
                            if (scMainDecompile?.Features != null)
                            {
                                var tknzFeature = scMainDecompile.Features
                                    .Where(x => x.FeatureName == FeatureName.TokenizationV2)
                                    .Select(x => x.FeatureFeatures)
                                    .FirstOrDefault();

                                if (tknzFeature is TokenizationV2Feature tknz)
                                {
                                    snapshotAddresses = tknz.ValidatorAddressesSnapshot;
                                    
                                    // If we didn't have a local contract, fill in from State Trei
                                    if (vbtcContract == null)
                                    {
                                        depositAddress = tknz.DepositAddress;
                                        totalRegisteredValidators = tknz.ValidatorAddressesSnapshot?.Count ?? 0;
                                        lastValidatorActivityBlock = tknz.ProofBlockHeight;
                                        SCLogUtility.Log($"Resolved from State Trei — DepositAddress: {depositAddress}, Validators: {totalRegisteredValidators}, ActivityBlock: {lastValidatorActivityBlock}", "VBTCService.CompleteWithdrawal()");
                                    }
                                }
                            }
                        }
                        catch (Exception decompileEx)
                        {
                            SCLogUtility.Log($"Failed to decompile contract from State Trei: {decompileEx.Message}", "VBTCService.CompleteWithdrawal()");
                        }
                    }
                    else if (vbtcContract == null)
                    {
                        SCLogUtility.Log($"Smart contract state not found in State Trei and no local contract: {scUID}", "VBTCService.CompleteWithdrawal()");
                        return (false, string.Empty, string.Empty, $"vBTC V2 contract not found in local DB or State Trei: {scUID}", null);
                    }
                }

                if (string.IsNullOrEmpty(depositAddress))
                {
                    SCLogUtility.Log($"Deposit address is empty for contract: {scUID}", "VBTCService.CompleteWithdrawal()");
                    return (false, string.Empty, string.Empty, $"Deposit address not found for contract: {scUID}", null);
                }

                // FIND-003 FIX: Look up withdrawal request using per-user tracking.
                // VBTCWithdrawalRequest is a local DB record — remote validators may not have it if
                // StateData hasn't saved it yet or if the TX was processed differently on that node.
                // When the local lookup fails, fall back to delegated params passed from the requesting node.
                var withdrawalRequest = VBTCWithdrawalRequest.GetByTransactionHash(withdrawalRequestHash);
                var isTransientRequest = withdrawalRequest == null; // synthesized rows have no DB home for pinned-build persistence
                if (withdrawalRequest == null)
                {
                    // Check if we have delegated withdrawal details from the requesting node
                    if (delegatedAmount.HasValue && delegatedAmount.Value > 0 && !string.IsNullOrEmpty(delegatedBTCDestination))
                    {
                        SCLogUtility.Log($"Withdrawal request not in local DB for hash: {withdrawalRequestHash}. Using delegated params: Amount={delegatedAmount.Value}, Dest={delegatedBTCDestination}, FeeRate={delegatedFeeRate}", 
                            "VBTCService.CompleteWithdrawal()");
                        
                        // Create a transient withdrawal request from delegated data (not saved to DB)
                        withdrawalRequest = new VBTCWithdrawalRequest
                        {
                            TransactionHash = withdrawalRequestHash,
                            SmartContractUID = scUID,
                            Amount = delegatedAmount.Value,
                            BTCDestination = delegatedBTCDestination,
                            FeeRate = delegatedFeeRate ?? 10,
                            IsCompleted = false,
                            Status = VBTCWithdrawalStatus.Requested
                        };
                    }
                    else
                    {
                        SCLogUtility.Log($"Withdrawal request not found for hash: {withdrawalRequestHash} and no delegated params provided", "VBTCService.CompleteWithdrawal()");
                        return (false, string.Empty, string.Empty, $"Withdrawal request not found for hash: {withdrawalRequestHash}", null);
                    }
                }

                // Validate the request is not already completed
                if (withdrawalRequest.IsCompleted)
                {
                    SCLogUtility.Log($"Withdrawal request already completed: {withdrawalRequestHash}", "VBTCService.CompleteWithdrawal()");
                    return (false, string.Empty, string.Empty, $"Withdrawal request already completed: {withdrawalRequestHash}", null);
                }

                // ============================================================
                // FROST INTEGRATION: Execute Bitcoin Withdrawal Transaction
                // ============================================================
                
                SCLogUtility.Log($"Starting FROST withdrawal for contract {scUID}", "VBTCService.CompleteWithdrawal()");

                // Get validators for FROST signing — use the contract's DKG snapshot (only those validators
                // hold FROST key shares), filtered by the registry (to get IPs) and reachability probe.
                var allRegistryValidators = VBTCValidatorRegistry.GetActiveValidators();
                if (allRegistryValidators == null || !allRegistryValidators.Any())
                {
                    SCLogUtility.Log($"No active validators in registry for FROST signing", "VBTCService.CompleteWithdrawal()");
                    return (false, string.Empty, string.Empty, "No active validators in registry for FROST signing", null);
                }

                // Filter to only validators from the contract's DKG snapshot (they hold the key shares)
                List<VBTCValidator> snapshotValidators;
                if (snapshotAddresses != null && snapshotAddresses.Any())
                {
                    var snapshotSet = new HashSet<string>(snapshotAddresses);
                    snapshotValidators = allRegistryValidators.Where(v => snapshotSet.Contains(v.ValidatorAddress)).ToList();
                    SCLogUtility.Log($"[FROST Signing] Filtered to {snapshotValidators.Count} snapshot validators " +
                        $"(from {allRegistryValidators.Count} registry, {snapshotAddresses.Count} in snapshot)", "VBTCService.CompleteWithdrawal()");
                }
                else
                {
                    // Fallback: no snapshot — use PUBLIC validators only (§7.2: never pull S3C
                    // validators into a legacy public contract).
                    snapshotValidators = VBTCValidatorRegistry.GetPublicValidators();
                    SCLogUtility.Log($"[FROST Signing] WARNING: No snapshot addresses found for contract {scUID}. " +
                        $"Using all {allRegistryValidators.Count} registry validators (legacy fallback)", "VBTCService.CompleteWithdrawal()");
                }

                if (!snapshotValidators.Any())
                {
                    SCLogUtility.Log($"No snapshot validators found in registry for FROST signing. " +
                        $"Snapshot had {snapshotAddresses?.Count ?? 0} addresses but none matched active registry.", "VBTCService.CompleteWithdrawal()");
                    return (false, string.Empty, string.Empty, "No DKG snapshot validators are currently active in the registry", null);
                }

                // Probe reachability — only contact validators that are actually online
                var validators = await FrostMPCService.ProbeValidatorReachability(snapshotValidators);
                if (!validators.Any())
                {
                    SCLogUtility.Log($"No reachable validators for FROST signing (0/{snapshotValidators.Count} responded to health check)", "VBTCService.CompleteWithdrawal()");
                    return (false, string.Empty, string.Empty, $"No reachable validators for FROST signing (0/{snapshotValidators.Count} snapshot validators online)", null);
                }

                // Use the snapshot total (not registry total) for threshold calculation
                int snapshotTotal = snapshotAddresses?.Count ?? totalRegisteredValidators;

                // Calculate DYNAMIC adjusted threshold based on validator availability
                int adjustedThreshold = VBTCThresholdCalculator.CalculateAdjustedThreshold(
                    snapshotTotal,
                    validators.Count,
                    lastValidatorActivityBlock,
                    Globals.LastBlock.Height
                );
                
                // Calculate required validators based on adjusted threshold
                int requiredValidators = VBTCThresholdCalculator.CalculateRequiredValidators(adjustedThreshold, validators.Count);
                
                // Log threshold information
                string thresholdInfo = VBTCThresholdCalculator.GetThresholdExplanation(
                    snapshotTotal,
                    validators.Count,
                    lastValidatorActivityBlock,
                    Globals.LastBlock.Height
                );
                SCLogUtility.Log($"Threshold calculation (snapshot-based): {thresholdInfo}", "VBTCService.CompleteWithdrawal()");
                
                if (validators.Count < requiredValidators)
                {
                    SCLogUtility.Log($"Insufficient reachable validators. Have: {validators.Count}, Need: {requiredValidators} " +
                        $"(Adjusted threshold: {adjustedThreshold}%, Snapshot: {snapshotTotal})", "VBTCService.CompleteWithdrawal()");
                    return (false, string.Empty, string.Empty, $"Insufficient reachable validators. Have: {validators.Count}, Need: {requiredValidators} (Adjusted threshold: {adjustedThreshold}%)", null);
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
                    return (false, string.Empty, string.Empty, "Invalid withdrawal details — amount/destination not found in contract or request record", null);
                }
                long feeRate = withdrawalRequest.FeeRate != 0 ? withdrawalRequest.FeeRate : 10; // Default fee rate (sats/vB) - TODO: Get from withdrawal request

                // ── FIND-028 retry determinism ─────────────────────────────────────────────
                // If a previous attempt already announced a tx for this withdrawal, reuse it
                // verbatim (validators allow idempotent same-sighash re-signs). A rebuild that
                // selects a different equal-value UTXO — or a different input order — changes
                // the sighashes and is correctly refused by the validators' sighash pin.
                string? pinnedUnsignedTxHex = null;
                List<PinnedWithdrawalCoin>? pinnedCoins = null;
                HashSet<string>? preferredOutpoints = null;

                if (!string.IsNullOrEmpty(withdrawalRequest.PinnedUnsignedTxHex) && !string.IsNullOrEmpty(withdrawalRequest.PinnedCoinsJson))
                {
                    var parsedPinnedCoins = JsonConvert.DeserializeObject<List<PinnedWithdrawalCoin>>(withdrawalRequest.PinnedCoinsJson);
                    if (parsedPinnedCoins is { Count: > 0 })
                    {
                        // Stale-pin escape: reuse only while ≥1 pinned outpoint is still unspent.
                        // If ALL are gone, the pinned tx either CONFIRMED (this withdrawal is
                        // already paid — never sign another tx for it) or was conflicted away
                        // (safe to rebuild fresh). Inconclusive lookups → reuse (always safe) —
                        // including an UNKNOWN confirmation answer: rebuilding on "Electrum couldn't
                        // tell" produced a non-conflicting tx that pin-holding validators 409'd.
                        var pinLookup = await BitcoinTransactionService.GetTaprootUTXOs(depositAddress);
                        var anyPinnedUnspent = pinLookup.Success && parsedPinnedCoins.Any(c =>
                            pinLookup.Utxos.Any(u => $"{u.TxHash}:{u.TxPos}".ToLowerInvariant() == c.ToOutpointKey()));

                        var pinnedTxId = withdrawalRequest.LastSignedBtcTxId;
                        int? pinnedTxConfirmations = 0;
                        if (pinLookup.Success && !anyPinnedUnspent && !string.IsNullOrEmpty(pinnedTxId))
                        {
                            var confLookup = await BitcoinTransactionService.GetTransactionConfirmationsResilient(pinnedTxId);
                            pinnedTxConfirmations = confLookup.Confirmations;
                        }

                        var disposition = DecidePinnedTxDisposition(pinLookup.Success, anyPinnedUnspent, pinnedTxConfirmations);
                        switch (disposition)
                        {
                            case PinnedReuseDecision.ReusePinned:
                                pinnedUnsignedTxHex = withdrawalRequest.PinnedUnsignedTxHex;
                                pinnedCoins = parsedPinnedCoins;
                                SCLogUtility.Log($"Reusing pinned unsigned tx for withdrawal {withdrawalRequestHash} ({parsedPinnedCoins.Count} input(s)).", "VBTCService.CompleteWithdrawal()");
                                break;

                            case PinnedReuseDecision.AlreadyPaid:
                                SCLogUtility.Log($"Withdrawal {withdrawalRequestHash} already paid on-chain as {pinnedTxId} ({pinnedTxConfirmations} conf) — refusing to sign a second tx.", "VBTCService.CompleteWithdrawal()");
                                return (false, string.Empty, string.Empty,
                                    $"The previously signed Bitcoin tx {pinnedTxId} for this withdrawal is already confirmed on-chain — this withdrawal is paid. Complete/reconcile it instead of re-signing.", null);

                            case PinnedReuseDecision.RebuildFresh:
                                SCLogUtility.Log($"Pinned tx for withdrawal {withdrawalRequestHash} was conflicted away on-chain (all pinned outpoints spent, {pinnedTxId ?? "n/a"} known-unconfirmed) — clearing pin and rebuilding.", "VBTCService.CompleteWithdrawal()");
                                withdrawalRequest.PinnedUnsignedTxHex = null;
                                withdrawalRequest.PinnedCoinsJson = null;
                                if (!isTransientRequest)
                                    VBTCWithdrawalRequest.Save(withdrawalRequest, true);
                                break;
                        }
                    }
                }

                if (pinnedUnsignedTxHex == null)
                {
                    // Conflict-preference: if an EARLIER request for this contract left a pinned,
                    // unresolved tx behind, prefer its outpoints so our new tx conflicts with any
                    // outstanding validator pin (the condition under which a different withdrawal
                    // is allowed to sign — and the guarantee that at most one tx ever confirms).
                    var priorPinned = VBTCWithdrawalRequest.GetLatestPinnedForContract(scUID);
                    if (priorPinned != null && priorPinned.TransactionHash != withdrawalRequestHash && !string.IsNullOrEmpty(priorPinned.PinnedCoinsJson))
                    {
                        var priorCoins = JsonConvert.DeserializeObject<List<PinnedWithdrawalCoin>>(priorPinned.PinnedCoinsJson);
                        if (priorCoins is { Count: > 0 })
                        {
                            preferredOutpoints = priorCoins.Select(c => c.ToOutpointKey()).ToHashSet();
                            SCLogUtility.Log($"Preferring {preferredOutpoints.Count} outpoint(s) pinned by prior request {priorPinned.TransactionHash} for contract {scUID}.", "VBTCService.CompleteWithdrawal()");
                        }
                    }
                }

                // Execute FROST withdrawal (build + sign; broadcast only if not signOnly)
                // Use the withdrawal requestor's address as the FROST coordinator/leader —
                // they already proved token ownership during the withdrawal request step.
                var coordinatorAddress = withdrawalRequest.RequestorAddress;
                SCLogUtility.Log($"Executing FROST withdrawal: {withdrawalAmount} BTC to {btcDestination} (signOnly={signOnly}, coordinator={coordinatorAddress})", "VBTCService.CompleteWithdrawal()");

                var btcResult = await BitcoinTransactionService.ExecuteFROSTWithdrawal(
                    depositAddress,
                    btcDestination,
                    withdrawalAmount,
                    feeRate,
                    scUID,
                    validators,
                    adjustedThreshold,
                    broadcast: !signOnly,
                    coordinatorAddress: coordinatorAddress,
                    withdrawalRequestHash: withdrawalRequestHash,  // FIND-028: Validator-side dedup
                    preSignedAuth: preSignedAuth,
                    pinnedUnsignedTxHex: pinnedUnsignedTxHex,
                    pinnedCoins: pinnedCoins,
                    persistPinnedBuild: isTransientRequest ? null : (hex, coins) => PersistPinnedBuild(withdrawalRequestHash, hex, coins),
                    preferredOutpoints: preferredOutpoints
                );

                if (!btcResult.Success)
                {
                    SCLogUtility.Log($"Bitcoin transaction failed: {btcResult.ErrorMessage}", "VBTCService.CompleteWithdrawal()");

                    // Local-only observability: record what/when/why the last signing attempt failed
                    // on the stored request. Never touches IsCompleted/Status=Completed semantics.
                    RecordSigningFailureOnRequest(withdrawalRequestHash, btcResult.Ceremony);

                    return (false, string.Empty, string.Empty, $"Bitcoin transaction failed: {btcResult.ErrorMessage}", btcResult.Ceremony);
                }

                string btcTxHash = btcResult.TxHash;
                string signedTxHex = btcResult.SignedTxHex;
                SCLogUtility.Log($"FROST signing successful. TxHash: {btcTxHash}, SignedTxHex length: {signedTxHex?.Length ?? 0}", "VBTCService.CompleteWithdrawal()");

                // Persist the signed BTC txid on the stored request (local-only). If the caller dies
                // between signing and completion, this is the only durable pointer to the outstanding
                // signed transaction — a future watcher can use it to detect an out-of-band broadcast.
                PersistSignedBtcTxId(withdrawalRequestHash, btcTxHash);

                // signOnly mode: Return the signed TX hex without broadcasting or creating VFX TX.
                // The caller (wallet node) will handle broadcast and VFX completion TX.
                if (signOnly)
                {
                    SCLogUtility.Log($"signOnly mode: returning signed TX hex to caller. TxHash: {btcTxHash}", "VBTCService.CompleteWithdrawal()");
                    return (true, string.Empty, signedTxHex, string.Empty, null);
                }

                // ============================================================
                // Full mode: Continue with VFX completion transaction
                // ============================================================

                // Use validator address or first available account for transaction creation
                string fromAddress = withdrawalRequest.RequestorAddress;

                var account = AccountData.GetSingleAccount(fromAddress);
                if (account == null)
                {
                    SCLogUtility.Log($"Account not found: {fromAddress}", "VBTCService.CompleteWithdrawal()");
                    return (false, string.Empty, btcTxHash, $"Account not found: {fromAddress}", null);
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

                // Build transaction — self-transaction by the validator recording the withdrawal completion
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

                // BURN-EXIT FIX: Bridge/withdrawal-complete transactions MUST remain fee-free.
                // The previous call to FeeCalcService.CalculateTXFee(...) was overwriting Fee = 0.0M
                // with a non-zero value which broke consensus on fee-free TX types and caused
                // validator balance checks to reject casters that don't have a funded wallet.
                completionTx.Fee = 0M.ToNormalizeDecimal();

                // Build and sign transaction
                completionTx.Build();
                var txHash = completionTx.Hash;
                
                var privateKey = account.GetPrivKey;
                var publicKey = account.PublicKey;

                if (privateKey == null)
                {
                    SCLogUtility.Log($"Private key was null for account {fromAddress}", "VBTCService.CompleteWithdrawal()");
                    return (false, string.Empty, btcTxHash, $"Private key was null for account {fromAddress}", null);
                }

                var signature = VerifiedXCore.Services.SignatureService.CreateSignature(txHash, privateKey, publicKey);
                if (signature == "ERROR")
                {
                    SCLogUtility.Log($"TX Signature Failed. SCUID: {scUID}", "VBTCService.CompleteWithdrawal()");
                    return (false, string.Empty, btcTxHash, $"TX Signature Failed. SCUID: {scUID}", null);
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
                    return (true, completionTx.Hash, btcTxHash, string.Empty, null);
                }
                else
                {
                    SCLogUtility.Log($"vBTC V2 Withdrawal Complete TX Verify Failed: {scUID}. Result: {result.Item2}", "VBTCService.CompleteWithdrawal()");
                    return (false, string.Empty, btcTxHash, $"TX Verify Failed: {result.Item2}", null);
                }
            }
            catch (Exception ex)
            {
                SCLogUtility.Log($"vBTC V2 Withdrawal Complete Error: {ex.Message}", "VBTCService.CompleteWithdrawal()");
                return (false, string.Empty, string.Empty, $"Error: {ex.Message}", null);
            }
        }

        /// <summary>
        /// Local-only observability write: stamps the stored withdrawal request with the last
        /// signing failure's code/session/time. Purely informational for UIs and support — never
        /// touches IsCompleted or completion Status values (those are consensus-adjacent).
        /// </summary>
        private static void RecordSigningFailureOnRequest(string withdrawalRequestHash, FROST.Models.FrostCeremonyOutcome? ceremony)
        {
            try
            {
                var storedRequest = VBTCWithdrawalRequest.GetByTransactionHash(withdrawalRequestHash);
                if (storedRequest == null)
                    return;

                storedRequest.LastSigningFailureCode = ceremony?.FailureCode.ToString() ?? "Unknown";
                storedRequest.LastSigningFailureAt = TimeUtil.GetTime();
                storedRequest.LastSigningSessionId = ceremony?.SessionId ?? string.Empty;
                VBTCWithdrawalRequest.Save(storedRequest, true);
            }
            catch (Exception ex)
            {
                SCLogUtility.Log($"Failed to record signing failure on request {withdrawalRequestHash}: {ex.Message}", "VBTCService.RecordSigningFailureOnRequest()");
            }
        }

        /// <summary>
        /// What to do with a withdrawal's previously-pinned (announced) unsigned tx.
        /// </summary>
        public enum PinnedReuseDecision
        {
            /// <summary>Re-sign the identical pinned tx (idempotent under the validators' sighash pin).</summary>
            ReusePinned,
            /// <summary>The pinned tx confirmed — this withdrawal is already paid; never sign another tx for it.</summary>
            AlreadyPaid,
            /// <summary>The pinned tx was definitively conflicted away — safe to clear the pin and build fresh.</summary>
            RebuildFresh
        }

        /// <summary>
        /// FIND-028 stale-pin escape, as a pure function (no I/O) so the rule is unit-testable.
        /// The critical case: pinned outpoints all gone + confirmation lookup UNKNOWN (null) must
        /// REUSE, not rebuild. The old code collapsed "Electrum couldn't answer" into confs==0 and
        /// rebuilt a fresh NON-conflicting tx — which validators holding the pin correctly 409
        /// (FIND-028), deadlocking the withdrawal until their pin cleared. Reuse is always safe:
        /// re-signing the identical tx is idempotent under the validators' sighash rules.
        /// </summary>
        public static PinnedReuseDecision DecidePinnedTxDisposition(bool utxoLookupSucceeded, bool anyPinnedOutpointUnspent, int? pinnedTxConfirmations)
        {
            // Inconclusive UTXO view, or the pinned tx can still confirm as-is → reuse it verbatim.
            if (!utxoLookupSucceeded || anyPinnedOutpointUnspent)
                return PinnedReuseDecision.ReusePinned;

            if (pinnedTxConfirmations >= 1)
                return PinnedReuseDecision.AlreadyPaid;

            // All pinned outpoints spent, and no server could say whether OUR tx is what spent them
            // → unknown; keep reusing until a definitive answer arrives.
            if (pinnedTxConfirmations == null)
                return PinnedReuseDecision.ReusePinned;

            // Definitive: outpoints gone AND the pinned tx is known-unconfirmed → conflicted away.
            return PinnedReuseDecision.RebuildFresh;
        }

        /// <summary>
        /// FIND-028: persist the exact unsigned tx (and its coins) about to be announced to the
        /// validators, so any retry re-signs the identical sighashes instead of rebuilding.
        /// Written BEFORE the first announce; local-only, never consensus-read.
        /// </summary>
        private static void PersistPinnedBuild(string withdrawalRequestHash, string unsignedTxHex, List<PinnedWithdrawalCoin> coins)
        {
            try
            {
                var storedRequest = VBTCWithdrawalRequest.GetByTransactionHash(withdrawalRequestHash);
                if (storedRequest == null)
                    return;

                storedRequest.PinnedUnsignedTxHex = unsignedTxHex;
                storedRequest.PinnedCoinsJson = JsonConvert.SerializeObject(coins);
                VBTCWithdrawalRequest.Save(storedRequest, true);
            }
            catch (Exception ex)
            {
                SCLogUtility.Log($"Failed to persist pinned build on request {withdrawalRequestHash}: {ex.Message}", "VBTCService.PersistPinnedBuild()");
            }
        }

        /// <summary>
        /// Local-only: persist the signed BTC txid on the stored request so a signed-but-unbroadcast
        /// transaction remains traceable if the caller (e.g. a web wallet) dies before completing.
        /// </summary>
        private static void PersistSignedBtcTxId(string withdrawalRequestHash, string btcTxHash)
        {
            try
            {
                if (string.IsNullOrEmpty(btcTxHash))
                    return;

                var storedRequest = VBTCWithdrawalRequest.GetByTransactionHash(withdrawalRequestHash);
                if (storedRequest == null)
                    return;

                storedRequest.LastSignedBtcTxId = btcTxHash;
                VBTCWithdrawalRequest.Save(storedRequest, true);
            }
            catch (Exception ex)
            {
                SCLogUtility.Log($"Failed to persist signed BTC txid on request {withdrawalRequestHash}: {ex.Message}", "VBTCService.PersistSignedBtcTxId()");
            }
        }

        /// <summary>
        /// Cancel an active withdrawal request for a vBTC V2 contract.
        /// Only the original requestor can cancel their own withdrawal.
        /// </summary>
        /// <param name="scUID">Smart contract UID</param>
        /// <param name="requestorAddress">Address that originally requested the withdrawal</param>
        /// <param name="withdrawalRequestHash">Hash of the withdrawal request transaction to cancel</param>
        /// <returns>Success flag and message</returns>
        public static async Task<(bool, string)> CancelWithdrawal(string scUID, string requestorAddress, string withdrawalRequestHash)
        {
            try
            {
                // Get account and validate
                var account = AccountData.GetSingleAccount(requestorAddress);
                if (account == null)
                {
                    SCLogUtility.Log($"Account not found: {requestorAddress}", "VBTCService.CancelWithdrawal()");
                    return (false, $"Account not found: {requestorAddress}");
                }

                // Verify the withdrawal request exists and belongs to this user
                var existingRequest = VBTCWithdrawalRequest.GetByTransactionHash(withdrawalRequestHash);
                if (existingRequest == null)
                {
                    SCLogUtility.Log($"Withdrawal request not found: {withdrawalRequestHash}", "VBTCService.CancelWithdrawal()");
                    return (false, $"Withdrawal request not found: {withdrawalRequestHash}");
                }

                if (existingRequest.RequestorAddress != requestorAddress)
                {
                    SCLogUtility.Log($"Requestor address mismatch. Expected: {existingRequest.RequestorAddress}, Got: {requestorAddress}", "VBTCService.CancelWithdrawal()");
                    return (false, "Only the original withdrawal requestor can cancel this withdrawal.");
                }

                if (existingRequest.IsCompleted)
                {
                    SCLogUtility.Log($"Withdrawal already completed/cancelled: {withdrawalRequestHash}", "VBTCService.CancelWithdrawal()");
                    return (false, "Cannot cancel an already completed or cancelled withdrawal.");
                }

                // Create transaction data
                var txData = JsonConvert.SerializeObject(new
                {
                    Function = "VBTCWithdrawalCancel()",
                    ContractUID = scUID,
                    WithdrawalRequestHash = withdrawalRequestHash
                });

                // Build transaction — self-transaction from the requestor
                var cancelTx = new Transaction
                {
                    Timestamp = TimeUtil.GetTime(),
                    FromAddress = requestorAddress,
                    ToAddress = requestorAddress,
                    Amount = 0.0M,
                    Fee = 0.0M,
                    Nonce = AccountStateTrei.GetNextNonce(requestorAddress),
                    TransactionType = TransactionType.VBTC_V2_WITHDRAWAL_CANCEL,
                    Data = txData
                };

                cancelTx.Fee = VerifiedXCore.Services.FeeCalcService.CalculateTXFee(cancelTx);

                // Build and sign transaction
                cancelTx.Build();
                var txHash = cancelTx.Hash;

                var privateKey = account.GetPrivKey;
                var publicKey = account.PublicKey;

                if (privateKey == null)
                {
                    SCLogUtility.Log($"Private key was null for account {requestorAddress}", "VBTCService.CancelWithdrawal()");
                    return (false, $"Private key was null for account {requestorAddress}");
                }

                var signature = VerifiedXCore.Services.SignatureService.CreateSignature(txHash, privateKey, publicKey);
                if (signature == "ERROR")
                {
                    SCLogUtility.Log($"TX Signature Failed. SCUID: {scUID}", "VBTCService.CancelWithdrawal()");
                    return (false, $"TX Signature Failed. SCUID: {scUID}");
                }

                cancelTx.Signature = signature;

                // Verify transaction
                var result = await TransactionValidatorService.VerifyTX(cancelTx);
                if (result.Item1)
                {
                    await TransactionData.AddTxToWallet(cancelTx, true);
                    await AccountData.UpdateLocalBalance(requestorAddress, cancelTx.Fee + cancelTx.Amount);
                    await TransactionData.AddToPool(cancelTx);
                    await P2PClient.SendTXMempool(cancelTx);
                    SCLogUtility.Log($"vBTC V2 Withdrawal Cancel TX Success. SCUID: {scUID}, TxHash: {cancelTx.Hash}", "VBTCService.CancelWithdrawal()");
                    return (true, cancelTx.Hash);
                }
                else
                {
                    SCLogUtility.Log($"vBTC V2 Withdrawal Cancel TX Verify Failed: {scUID}. Result: {result.Item2}", "VBTCService.CancelWithdrawal()");
                    return (false, $"TX Verify Failed: {result.Item2}");
                }
            }
            catch (Exception ex)
            {
                SCLogUtility.Log($"vBTC V2 Withdrawal Cancel Error: {ex.Message}", "VBTCService.CancelWithdrawal()");
                return (false, $"Error: {ex.Message}");
            }
        }

        #region Bridge Lock / Unlock Transactions

        /// <summary>
        /// Create and broadcast a VBTC_V2_BRIDGE_LOCK transaction on the VFX chain.
        /// This deducts the vBTC balance in the State Trei so all nodes see the lock.
        /// </summary>
        /// <param name="scUID">Smart contract UID of the vBTC contract</param>
        /// <param name="ownerAddress">VFX address that owns the vBTC being locked</param>
        /// <param name="amount">Amount of vBTC to lock (in BTC, e.g. 0.001)</param>
        /// <param name="evmDestination">EVM address on Base to receive vBTC.b</param>
        /// <param name="lockIdOverride">Optional; default is a new GUID (format N). Must be unique on-chain.</param>
        /// <returns>(Success, TxHashOrError, LockId)</returns>
        /// <summary>
        /// S3C §6.3: resolve a contract's IsS3C flag — local record first, else state-trei
        /// decompile (works on any node; used by the consensus bridge-lock rejection too).
        /// </summary>
        public static bool ResolveContractIsS3C(string scUID)
        {
            var local = VBTCContractV2.GetContract(scUID);
            if (local != null) return local.IsS3C;
            try
            {
                var scState = SmartContractStateTrei.GetSmartContractState(scUID);
                if (scState != null && !string.IsNullOrEmpty(scState.ContractData))
                {
                    var scMain = SmartContractMain.GenerateSmartContractInMemory(scState.ContractData);
                    var tknz = scMain?.Features?
                        .Where(x => x.FeatureName == FeatureName.TokenizationV2)
                        .Select(x => x.FeatureFeatures)
                        .FirstOrDefault() as TokenizationV2Feature;
                    if (tknz != null) return tknz.IsS3C;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// S3C §7.4/§8: resolve a contract's DKG validator snapshot — local record first, else
        /// state-trei decompile (works on any node). Empty list if none (legacy no-snapshot).
        /// </summary>
        public static List<string> ResolveContractSnapshot(string scUID)
        {
            var local = VBTCContractV2.GetContract(scUID);
            if (local != null && local.ValidatorAddressesSnapshot != null && local.ValidatorAddressesSnapshot.Count > 0)
                return local.ValidatorAddressesSnapshot;
            try
            {
                var scState = SmartContractStateTrei.GetSmartContractState(scUID);
                if (scState != null && !string.IsNullOrEmpty(scState.ContractData))
                {
                    var scMain = SmartContractMain.GenerateSmartContractInMemory(scState.ContractData);
                    var tknz = scMain?.Features?
                        .Where(x => x.FeatureName == FeatureName.TokenizationV2)
                        .Select(x => x.FeatureFeatures)
                        .FirstOrDefault() as TokenizationV2Feature;
                    if (tknz?.ValidatorAddressesSnapshot != null) return tknz.ValidatorAddressesSnapshot;
                }
            }
            catch { }
            return new List<string>();
        }

        /// <summary>
        /// S3C §7.4: the eligible cancellation-voter set for a contract. Snapshot validators if the
        /// contract has a DKG snapshot; else the public validators (legacy fallback). The 75%
        /// denominator is this set's Count (FULL snapshot — dead members still count). Used
        /// identically by TransactionValidatorService (eligibility) and StateData (denominator).
        /// </summary>
        public static HashSet<string> ResolveCancellationVoterSet(string scUID)
        {
            var snapshot = ResolveContractSnapshot(scUID);
            if (snapshot.Count > 0)
                return new HashSet<string>(snapshot);
            return new HashSet<string>(VBTCValidatorRegistry.GetPublicValidators().Select(v => v.ValidatorAddress));
        }

        public static async Task<(bool Success, string TxHashOrError, string LockId)> CreateBridgeLockTx(
            string scUID, string ownerAddress, decimal amount, string evmDestination, string? lockIdOverride = null)
        {
            try
            {
                // Reserve-held vBTC is locked: consensus denies xRBX bridge locks.
                if (ownerAddress.StartsWith("xRBX"))
                    return (false, "Reserve accounts cannot bridge vBTC. Move the vBTC to a normal VFX address first.", string.Empty);

                var account = AccountData.GetSingleAccount(ownerAddress);
                if (account == null)
                {
                    SCLogUtility.Log($"Account not found: {ownerAddress}", "VBTCService.CreateBridgeLockTx()");
                    return (false, $"Account not found: {ownerAddress}", string.Empty);
                }

                // S3C §6.3: vBTC.b bridge is not available for S3C contracts (client-side gate;
                // also consensus-enforced at TX validation). Companion path (§5) is the route.
                if (ResolveContractIsS3C(scUID))
                    return (false, "vBTC.b bridge is not available for S3C contracts. Withdraw to your own public companion contract to bridge.", string.Empty);

                // Validate balance (subtract local bridge reservations not yet reflected in state trei)
                var balResult = await TryGetAvailableTransparentVbtcBalance(scUID, ownerAddress);
                if (!balResult.success)
                {
                    SCLogUtility.Log(balResult.error ?? "Balance lookup failed", "VBTCService.CreateBridgeLockTx()");
                    return (false, balResult.error ?? "Could not resolve vBTC transparent balance.", string.Empty);
                }

                var reservedLocal = BridgeLockRecord.GetLockedAmount(ownerAddress, scUID);
                var spendable = balResult.availableBalance - reservedLocal;
                if (spendable < amount)
                {
                    SCLogUtility.Log($"Insufficient balance. Available: {spendable} (raw: {balResult.availableBalance}, local bridge reserve: {reservedLocal}), Requested: {amount}", "VBTCService.CreateBridgeLockTx()");
                    return (false, $"Insufficient balance. Available: {spendable} BTC, Requested: {amount}", string.Empty);
                }

                // Auto-derive Base address from VFX key if evmDestination not provided
                if (string.IsNullOrWhiteSpace(evmDestination))
                    evmDestination = ValidatorEthKeyService.DeriveBaseAddressFromAccount(ownerAddress);

                // Validate EVM destination
                if (string.IsNullOrWhiteSpace(evmDestination) || !evmDestination.StartsWith("0x") || evmDestination.Length != 42)
                {
                    return (false, "Invalid EVM destination address. Must be 0x-prefixed, 42 characters.", string.Empty);
                }

                var lockId = string.IsNullOrWhiteSpace(lockIdOverride) ? Guid.NewGuid().ToString("N") : lockIdOverride.Trim();
                long amountSats = (long)(amount * 100_000_000M);

                // Create transaction data
                var txData = JsonConvert.SerializeObject(new
                {
                    Function = "VBTCBridgeLock()",
                    ContractUID = scUID,
                    LockId = lockId,
                    Amount = amount,
                    AmountSats = amountSats,
                    EvmDestination = evmDestination
                });

                // Build transaction (self-TX: from=to=owner, amount=0 VFX)
                var lockTx = new Transaction
                {
                    Timestamp = TimeUtil.GetTime(),
                    FromAddress = ownerAddress,
                    ToAddress = ownerAddress,
                    Amount = 0.0M,
                    Fee = 0.0M,
                    Nonce = AccountStateTrei.GetNextNonce(ownerAddress),
                    TransactionType = TransactionType.VBTC_V2_BRIDGE_LOCK,
                    Data = txData
                };

                lockTx.Fee = VerifiedXCore.Services.FeeCalcService.CalculateTXFee(lockTx);

                // Build and sign
                lockTx.Build();
                var txHash = lockTx.Hash;
                var privateKey = account.GetPrivKey;
                var publicKey = account.PublicKey;

                if (privateKey == null)
                {
                    SCLogUtility.Log($"Private key was null for account {ownerAddress}", "VBTCService.CreateBridgeLockTx()");
                    return (false, $"Private key was null for account {ownerAddress}", string.Empty);
                }

                var signature = VerifiedXCore.Services.SignatureService.CreateSignature(txHash, privateKey, publicKey);
                if (signature == "ERROR")
                {
                    SCLogUtility.Log($"TX Signature Failed. SCUID: {scUID}", "VBTCService.CreateBridgeLockTx()");
                    return (false, $"TX Signature Failed. SCUID: {scUID}", string.Empty);
                }

                lockTx.Signature = signature;

                // Verify transaction
                var result = await TransactionValidatorService.VerifyTX(lockTx);
                if (result.Item1)
                {
                    await TransactionData.AddTxToWallet(lockTx, true);
                    await AccountData.UpdateLocalBalance(ownerAddress, lockTx.Fee + lockTx.Amount);
                    await TransactionData.AddToPool(lockTx);
                    await P2PClient.SendTXMempool(lockTx);
                    SCLogUtility.Log($"vBTC V2 Bridge Lock TX Success. SCUID: {scUID}, TxHash: {lockTx.Hash}, LockId: {lockId}", "VBTCService.CreateBridgeLockTx()");
                    return (true, lockTx.Hash, lockId);
                }
                else
                {
                    SCLogUtility.Log($"vBTC V2 Bridge Lock TX Verify Failed: {scUID}. Result: {result.Item2}", "VBTCService.CreateBridgeLockTx()");
                    return (false, $"TX Verify Failed: {result.Item2}", string.Empty);
                }
            }
            catch (Exception ex)
            {
                SCLogUtility.Log($"vBTC V2 Bridge Lock Error: {ex.Message}", "VBTCService.CreateBridgeLockTx()");
                return (false, $"Error: {ex.Message}", string.Empty);
            }
        }

        /// <summary>
        /// Polls until <see cref="VBTCBridgeLockState"/> exists (lock included in a block) or the timeout elapses.
        /// Used before relay mint on Base so the VFX lock is consensus-valid.
        /// </summary>
        public static async Task<bool> WaitForBridgeLockInStateAsync(string lockId, int timeoutMs = 120_000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (VBTCBridgeLockState.GetByLockId(lockId) != null)
                    return true;
                var rec = BridgeLockRecord.GetByLockId(lockId);
                if (rec?.VfxLockConfirmedOnChain == true)
                    return true;
                await Task.Delay(400);
            }
            return VBTCBridgeLockState.GetByLockId(lockId) != null;
        }

        /// <summary>
        /// Create and broadcast a VBTC_V2_BRIDGE_UNLOCK transaction on the VFX chain.
        /// This restores the vBTC balance after a burn on Base has been detected.
        /// Called by the user's node when it detects its own ExitBurned event on Base.
        /// </summary>
        /// <param name="scUID">Smart contract UID of the vBTC contract</param>
        /// <param name="ownerAddress">VFX address that originally locked the vBTC</param>
        /// <param name="lockId">The lock ID being unlocked</param>
        /// <param name="amount">Amount being unlocked</param>
        /// <param name="exitBurnTxHash">The Base transaction hash of the burnForExit call</param>
        /// <returns>(Success, TxHashOrError)</returns>
        public static async Task<(bool Success, string TxHashOrError)> CreateBridgeUnlockTx(
            string scUID, string ownerAddress, string lockId, decimal amount, string exitBurnTxHash,
            IReadOnlyList<CasterConsensusVote>? casterConsensusVotes = null)
        {
            try
            {
                var account = AccountData.GetSingleAccount(ownerAddress);
                if (account == null)
                {
                    SCLogUtility.Log($"Account not found: {ownerAddress}", "VBTCService.CreateBridgeUnlockTx()");
                    return (false, $"Account not found: {ownerAddress}");
                }

                long amountSats = (long)(amount * 100_000_000M);

                // Create transaction data
                object payload = new
                {
                    Function = "VBTCBridgeUnlock()",
                    ContractUID = scUID,
                    LockId = lockId,
                    Amount = amount,
                    AmountSats = amountSats,
                    ExitBurnTxHash = exitBurnTxHash,
                    CasterConsensusVotes = casterConsensusVotes
                };
                var txData = JsonConvert.SerializeObject(payload);

                // Build transaction (self-TX: from=to=owner, amount=0 VFX)
                var unlockTx = new Transaction
                {
                    Timestamp = TimeUtil.GetTime(),
                    FromAddress = ownerAddress,
                    ToAddress = ownerAddress,
                    Amount = 0.0M,
                    Fee = 0.0M,
                    Nonce = AccountStateTrei.GetNextNonce(ownerAddress),
                    TransactionType = TransactionType.VBTC_V2_BRIDGE_UNLOCK,
                    Data = txData
                };

                unlockTx.Fee = 0.00M;

                // Build and sign
                unlockTx.Build();
                var txHash = unlockTx.Hash;
                var privateKey = account.GetPrivKey;
                var publicKey = account.PublicKey;

                if (privateKey == null)
                {
                    SCLogUtility.Log($"Private key was null for account {ownerAddress}", "VBTCService.CreateBridgeUnlockTx()");
                    return (false, $"Private key was null for account {ownerAddress}");
                }

                var signature = VerifiedXCore.Services.SignatureService.CreateSignature(txHash, privateKey, publicKey);
                if (signature == "ERROR")
                {
                    SCLogUtility.Log($"TX Signature Failed. SCUID: {scUID}", "VBTCService.CreateBridgeUnlockTx()");
                    return (false, $"TX Signature Failed. SCUID: {scUID}");
                }

                unlockTx.Signature = signature;

                // Verify transaction
                var result = await TransactionValidatorService.VerifyTX(unlockTx);
                if (result.Item1)
                {
                    await TransactionData.AddTxToWallet(unlockTx, true);
                    await AccountData.UpdateLocalBalance(ownerAddress, unlockTx.Fee + unlockTx.Amount);
                    await TransactionData.AddToPool(unlockTx);
                    await P2PClient.SendTXMempool(unlockTx);
                    SCLogUtility.Log($"vBTC V2 Bridge Unlock TX Success. SCUID: {scUID}, TxHash: {unlockTx.Hash}, LockId: {lockId}", "VBTCService.CreateBridgeUnlockTx()");
                    return (true, unlockTx.Hash);
                }
                else
                {
                    SCLogUtility.Log($"vBTC V2 Bridge Unlock TX Verify Failed: {scUID}. Result: {result.Item2}", "VBTCService.CreateBridgeUnlockTx()");
                    return (false, $"TX Verify Failed: {result.Item2}");
                }
            }
            catch (Exception ex)
            {
                SCLogUtility.Log($"vBTC V2 Bridge Unlock Error: {ex.Message}", "VBTCService.CreateBridgeUnlockTx()");
                return (false, $"Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Broadcast <see cref="TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC"/> after a Base <c>burnForBTCExit</c> is agreed by casters.
        /// V3: Now includes FIFO allocation plan and per-contract BTC withdrawal records so
        /// <see cref="StateData.ApplyVBTCBridgeExitToBTC"/> can apply partial unlocks to each lock.
        /// </summary>
        public static async Task<(bool Success, string TxHashOrError)> CreateBridgeExitToBTCTx(
            string ownerAddress,
            decimal totalAmount,
            string btcDestination,
            string baseBurnTxHash,
            List<PoolUnlockAllocation> allocations,
            List<BtcExitWithdrawalRecord>? btcWithdrawals = null,
            IReadOnlyList<CasterConsensusVote>? casterConsensusVotes = null)
        {
            try
            {
                var account = AccountData.GetSingleAccount(ownerAddress);
                if (account == null)
                    return (false, $"Account not found: {ownerAddress}");

                var totalAmountSats = (long)(totalAmount * 100_000_000M);
                var firstAlloc = allocations?.FirstOrDefault();
                var txData = JsonConvert.SerializeObject(new
                {
                    Function = "VBTCBridgeExitToBTC()",
                    // Backward-compatible fields required by TransactionValidatorService
                    ContractUID = firstAlloc?.SmartContractUID ?? "",
                    LockId = firstAlloc?.LockId ?? "",
                    Amount = totalAmount,
                    AmountSats = totalAmountSats,
                    // V3 fields
                    TotalAmount = totalAmount,
                    TotalAmountSats = totalAmountSats,
                    BtcDestination = btcDestination,
                    BaseBurnTxHash = baseBurnTxHash,
                    Allocations = allocations,
                    BtcWithdrawals = btcWithdrawals,
                    CasterConsensusVotes = casterConsensusVotes
                });

                var tx = new Transaction
                {
                    Timestamp = TimeUtil.GetTime(),
                    FromAddress = ownerAddress,
                    ToAddress = ownerAddress,
                    Amount = 0.0M,
                    Fee = 0.0M,
                    Nonce = AccountStateTrei.GetNextNonce(ownerAddress),
                    TransactionType = TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC,
                    Data = txData
                };
                tx.Fee = 0.00M;
                tx.Build();
                var privateKey = account.GetPrivKey;
                var publicKey = account.PublicKey;
                if (privateKey == null)
                    return (false, "Private key was null");
                tx.Signature = VerifiedXCore.Services.SignatureService.CreateSignature(tx.Hash, privateKey, publicKey);
                if (tx.Signature == "ERROR")
                    return (false, "TX Signature Failed");

                var result = await TransactionValidatorService.VerifyTX(tx);
                if (!result.Item1)
                    return (false, $"TX Verify Failed: {result.Item2}");

                await TransactionData.AddTxToWallet(tx, true);
                await AccountData.UpdateLocalBalance(ownerAddress, tx.Fee + tx.Amount);
                await TransactionData.AddToPool(tx);
                await P2PClient.SendTXMempool(tx);
                return (true, tx.Hash);
            }
            catch (Exception ex)
            {
                return (false, $"Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Broadcast <see cref="TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC_COMPLETE"/> after the caster has
        /// FROST-signed and broadcast the Bitcoin withdrawal for a <c>burnForBTCExit</c>.
        /// This is the consensus-critical second half of the BTC-exit flow — it is what causes
        /// <see cref="StateData.ApplyVBTCBridgeExitToBTCComplete"/> to mark the pending
        /// <see cref="VBTCBridgeBtcExitState"/> row as complete.
        /// Runs entirely on the caster — signer is the caster's validator address, Fee is 0, self-TX.
        /// </summary>
        /// <param name="signerAddress">Caster / validator VFX address (must have a local account).</param>
        /// <param name="baseBurnTxHash">The Base <c>burnForBTCExit</c> transaction hash (32-byte hex).</param>
        /// <param name="btcTxHash">The broadcasted Bitcoin withdrawal transaction hash.</param>
        public static async Task<(bool Success, string TxHashOrError)> CreateBridgeExitToBTCCompleteTx(
            string signerAddress,
            string baseBurnTxHash,
            string btcTxHash,
            List<BtcExitWithdrawalRecord>? btcWithdrawals = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(signerAddress))
                    return (false, "Signer address is required for bridge exit to BTC complete.");
                if (string.IsNullOrWhiteSpace(baseBurnTxHash))
                    return (false, "BaseBurnTxHash is required for bridge exit to BTC complete.");
                if (string.IsNullOrWhiteSpace(btcTxHash))
                    return (false, "BtcTxHash is required for bridge exit to BTC complete.");

                var account = AccountData.GetSingleAccount(signerAddress);
                if (account == null)
                {
                    SCLogUtility.Log($"Account not found for signer: {signerAddress}", "VBTCService.CreateBridgeExitToBTCCompleteTx()");
                    return (false, $"Account not found: {signerAddress}");
                }

                var privateKey = account.GetPrivKey;
                var publicKey = account.PublicKey;
                if (privateKey == null)
                {
                    SCLogUtility.Log($"Private key was null for account {signerAddress}", "VBTCService.CreateBridgeExitToBTCCompleteTx()");
                    return (false, $"Private key was null for account {signerAddress}");
                }

                var txData = JsonConvert.SerializeObject(new
                {
                    Function = "VBTCBridgeExitToBTCComplete()",
                    BaseBurnTxHash = baseBurnTxHash.Trim(),
                    BtcTxHash = btcTxHash.Trim(),
                    BtcWithdrawals = btcWithdrawals
                });

                // Self-TX signed by the caster. Fee = 0 (enforced by validator). Amount = 0.
                var completionTx = new Transaction
                {
                    Timestamp = TimeUtil.GetTime(),
                    FromAddress = signerAddress,
                    ToAddress = signerAddress,
                    Amount = 0.0M,
                    Fee = 0.0M,
                    Nonce = AccountStateTrei.GetNextNonce(signerAddress),
                    TransactionType = TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC_COMPLETE,
                    Data = txData
                };

                // Do NOT call FeeCalcService.CalculateTXFee here — bridge TXs must remain fee-free.
                completionTx.Fee = 0.00M;
                completionTx.Build();

                var signature = VerifiedXCore.Services.SignatureService.CreateSignature(completionTx.Hash, privateKey, publicKey);
                if (signature == "ERROR")
                {
                    SCLogUtility.Log($"TX signature failed for bridge exit-to-BTC complete. BurnHash: {baseBurnTxHash}", "VBTCService.CreateBridgeExitToBTCCompleteTx()");
                    return (false, "TX Signature Failed");
                }

                completionTx.Signature = signature;

                var result = await TransactionValidatorService.VerifyTX(completionTx);
                if (!result.Item1)
                {
                    SCLogUtility.Log($"Bridge exit-to-BTC complete TX verify failed: {result.Item2}. BurnHash: {baseBurnTxHash}, BtcTxHash: {btcTxHash}",
                        "VBTCService.CreateBridgeExitToBTCCompleteTx()");
                    return (false, $"TX Verify Failed: {result.Item2}");
                }

                await TransactionData.AddTxToWallet(completionTx, true);
                await AccountData.UpdateLocalBalance(signerAddress, completionTx.Fee + completionTx.Amount);
                await TransactionData.AddToPool(completionTx);
                await P2PClient.SendTXMempool(completionTx);

                SCLogUtility.Log(
                    $"vBTC V2 Bridge Exit-to-BTC Complete TX broadcast. TxHash: {completionTx.Hash}, BurnHash: {baseBurnTxHash}, BtcTxHash: {btcTxHash}",
                    "VBTCService.CreateBridgeExitToBTCCompleteTx()");

                return (true, completionTx.Hash);
            }
            catch (Exception ex)
            {
                SCLogUtility.Log($"Bridge exit-to-BTC complete error: {ex.Message}", "VBTCService.CreateBridgeExitToBTCCompleteTx()");
                return (false, $"Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Broadcast <see cref="TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC_FAIL"/> when FROST signing
        /// fails for one or more lock allocations during a BTC exit. This transaction:
        /// (1) Reverses the partial unlocks for the failed locks (restores their RemainingAmount).
        /// (2) Blacklists those locks so they are never selected for future FIFO allocations.
        /// (3) Allows the burn to be retried with different locks.
        /// </summary>
        /// <param name="signerAddress">Caster / validator VFX address.</param>
        /// <param name="baseBurnTxHash">The original Base burn transaction hash.</param>
        /// <param name="exitTxHash">The EXIT_TO_BTC transaction hash that reserved the locks.</param>
        /// <param name="failedAllocations">The allocations whose contracts failed FROST signing.</param>
        /// <param name="reason">Human-readable reason for the failure.</param>
        public static async Task<(bool Success, string TxHashOrError)> CreateBridgeExitToBTCFailTx(
            string signerAddress,
            string baseBurnTxHash,
            string exitTxHash,
            List<PoolUnlockAllocation> failedAllocations,
            string reason)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(signerAddress))
                    return (false, "Signer address is required for bridge exit fail TX.");
                if (string.IsNullOrWhiteSpace(baseBurnTxHash))
                    return (false, "BaseBurnTxHash is required for bridge exit fail TX.");
                if (failedAllocations == null || failedAllocations.Count == 0)
                    return (false, "FailedAllocations must contain at least one allocation.");

                var account = AccountData.GetSingleAccount(signerAddress);
                if (account == null)
                    return (false, $"Account not found: {signerAddress}");

                var privateKey = account.GetPrivKey;
                var publicKey = account.PublicKey;
                if (privateKey == null)
                    return (false, $"Private key was null for account {signerAddress}");

                var txData = JsonConvert.SerializeObject(new
                {
                    Function = "VBTCBridgeExitToBTCFail()",
                    BaseBurnTxHash = baseBurnTxHash.Trim(),
                    ExitTxHash = exitTxHash?.Trim() ?? "",
                    FailedAllocations = failedAllocations,
                    Reason = reason
                });

                var failTx = new Transaction
                {
                    Timestamp = TimeUtil.GetTime(),
                    FromAddress = signerAddress,
                    ToAddress = signerAddress,
                    Amount = 0.0M,
                    Fee = 0.0M,
                    Nonce = AccountStateTrei.GetNextNonce(signerAddress),
                    TransactionType = TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC_FAIL,
                    Data = txData
                };

                failTx.Fee = 0.00M;
                failTx.Build();

                var signature = VerifiedXCore.Services.SignatureService.CreateSignature(failTx.Hash, privateKey, publicKey);
                if (signature == "ERROR")
                    return (false, "TX Signature Failed");

                failTx.Signature = signature;

                var result = await TransactionValidatorService.VerifyTX(failTx);
                if (!result.Item1)
                    return (false, $"TX Verify Failed: {result.Item2}");

                await TransactionData.AddTxToWallet(failTx, true);
                await AccountData.UpdateLocalBalance(signerAddress, failTx.Fee + failTx.Amount);
                await TransactionData.AddToPool(failTx);
                await P2PClient.SendTXMempool(failTx);

                SCLogUtility.Log(
                    $"vBTC V2 Bridge Exit-to-BTC FAIL TX broadcast. TxHash: {failTx.Hash}, BurnHash: {baseBurnTxHash}, " +
                    $"FailedLocks: {failedAllocations.Count}, Reason: {reason}",
                    "VBTCService.CreateBridgeExitToBTCFailTx()");

                return (true, failTx.Hash);
            }
            catch (Exception ex)
            {
                SCLogUtility.Log($"Bridge exit-to-BTC fail error: {ex.Message}", "VBTCService.CreateBridgeExitToBTCFailTx()");
                return (false, $"Error: {ex.Message}");
            }
        }

        #endregion

        #region Backfill Local V2 Contract Records

        /// <summary>
        /// Startup/on-demand backfill: ensures local SmartContract + VBTCContractV2 records exist for
        /// every vBTC V2 contract in the state trei that a local account owns or holds a ledger balance
        /// on. Covers transfers received before receive-time record creation existed. Idempotent
        /// (all saves are insert-only), so it is safe to run every boot or via ResyncVBTCContracts.
        /// </summary>
        public static async Task<(int Scanned, int LocalInvolvement, int Created, int SkippedExisting)> BackfillLocalVBTCContracts()
        {
            int scanned = 0, localInvolvement = 0, created = 0, skippedExisting = 0;
            try
            {
                var accountDb = AccountData.GetAccounts();
                if (accountDb == null)
                    return (scanned, localInvolvement, created, skippedExisting);

                var localAddresses = accountDb.FindAll().Select(x => x.Address).ToHashSet();

                // Reserve (xRBX) accounts can own and hold vBTC V2 — a wallet holding vBTC
                // only on reserve addresses previously backfilled nothing after a restore.
                var reserveAccountsBackfill = ReserveAccount.GetReserveAccounts();
                if (reserveAccountsBackfill != null)
                {
                    foreach (var r in reserveAccountsBackfill)
                        localAddresses.Add(r.Address);
                }

                if (!localAddresses.Any())
                {
                    SCLogUtility.Log($"VBTC-TRACE [6-Backfill]: skipped — no local accounts.", "VBTCService.BackfillLocalVBTCContracts()");
                    return (scanned, localInvolvement, created, skippedExisting);
                }

                var scStateTrei = SmartContractStateTrei.GetSCST();
                if (scStateTrei == null)
                {
                    SCLogUtility.Log($"VBTC-TRACE [6-Backfill]: skipped — state trei DB was null.", "VBTCService.BackfillLocalVBTCContracts()");
                    return (scanned, localInvolvement, created, skippedExisting);
                }

                SCLogUtility.Log($"VBTC-TRACE [6-Backfill]: starting — local accounts: {localAddresses.Count}", "VBTCService.BackfillLocalVBTCContracts()");

                foreach (var scState in scStateTrei.Query().ToEnumerable())
                {
                    try
                    {
                        scanned++;
                        if (string.IsNullOrWhiteSpace(scState.SmartContractUID) || string.IsNullOrWhiteSpace(scState.ContractData))
                        {
                            SCLogUtility.Log($"VBTC-TRACE [6-Backfill]: SKIP (empty-contract-data) — SCUID: '{scState.SmartContractUID}'", "VBTCService.BackfillLocalVBTCContracts()");
                            continue;
                        }

                        var isLocalOwner = localAddresses.Contains(scState.OwnerAddress);
                        var hasLocalLedger = scState.SCStateTreiTokenizationTXes != null &&
                            scState.SCStateTreiTokenizationTXes.Any(t => localAddresses.Contains(t.ToAddress) || localAddresses.Contains(t.FromAddress));

                        if (!isLocalOwner && !hasLocalLedger)
                            continue;

                        localInvolvement++;

                        if (VBTCContractV2.GetContract(scState.SmartContractUID) != null)
                        {
                            skippedExisting++;
                            SCLogUtility.Log($"VBTC-TRACE [6-Backfill]: SKIP (already-exists) — SCUID: {scState.SmartContractUID}", "VBTCService.BackfillLocalVBTCContracts()");
                            continue; // record already present; logo handled by the startup logo sweep
                        }

                        // Prefer the locally-saved SC record; if it is missing OR lacks the V2 feature
                        // (stale/partial save), decompile the authoritative state-trei contract data.
                        var scMain = SmartContractMain.SmartContractData.GetSmartContract(scState.SmartContractUID);
                        if (scMain?.Features?.Exists(x => x.FeatureName == FeatureName.TokenizationV2) != true)
                            scMain = SmartContractMain.GenerateSmartContractInMemory(scState.ContractData);

                        if (scMain == null)
                        {
                            SCLogUtility.Log($"VBTC-TRACE [6-Backfill]: SKIP (decompile-failed) — SCUID: {scState.SmartContractUID}", "VBTCService.BackfillLocalVBTCContracts()");
                            continue;
                        }

                        if (scMain.Features?.Exists(x => x.FeatureName == FeatureName.TokenizationV2) != true)
                        {
                            SCLogUtility.Log($"VBTC-TRACE [6-Backfill]: SKIP (no-V2-feature) — SCUID: {scState.SmartContractUID}", "VBTCService.BackfillLocalVBTCContracts()");
                            continue;
                        }

                        SmartContractMain.SmartContractData.SaveSmartContract(scMain, null);

                        if (isLocalOwner)
                        {
                            await VBTCContractV2.SaveSmartContract(scMain, null, scState.OwnerAddress);
                        }
                        else
                        {
                            // FIND-001: the holder param is deliberately ignored — this creates
                            // the single canonical record (owner = minter); balances live in the
                            // state trei, so which local address triggered the backfill is moot.
                            await VBTCContractV2.SaveSmartContractTransfer(scMain, localAddresses.First());
                        }

                        if (Globals.VBTCDefaultAssetOnly)
                            await NFTAssetFileUtility.AssociateDefaultVBTCLogo(scState.SmartContractUID);

                        created++;
                        SCLogUtility.Log($"VBTC-TRACE [6-Backfill]: backfilled local vBTC V2 contract record: {scState.SmartContractUID}", "VBTCService.BackfillLocalVBTCContracts()");
                    }
                    catch (Exception scEx)
                    {
                        ErrorLogUtility.LogError($"VBTC-TRACE [6-Backfill]: backfill failed for SCUID: {scState.SmartContractUID}. Error: {scEx.Message}", "VBTCService.BackfillLocalVBTCContracts()");
                    }
                }

                SCLogUtility.Log($"VBTC-TRACE [6-Backfill]: complete — state records scanned: {scanned}, with local involvement: {localInvolvement}, records created: {created}, skipped existing: {skippedExisting}", "VBTCService.BackfillLocalVBTCContracts()");
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError(ex.ToString(), "VBTCService.BackfillLocalVBTCContracts()");
            }

            return (scanned, localInvolvement, created, skippedExisting);
        }

        #endregion
    }
}
