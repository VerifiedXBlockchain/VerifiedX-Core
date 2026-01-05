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
    }
}
