using LiteDB;
using ReserveBlockCore.Data;
using ReserveBlockCore.Extensions;
using ReserveBlockCore.Utilities;
using ReserveBlockCore.Models;
using ReserveBlockCore.Models.SmartContracts;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ReserveBlockCore.Bitcoin.Models
{
    public class VBTCContractV2
    {
        #region Variables
        public long Id { get; set; }
        public string SmartContractUID { get; set; }
        public string OwnerAddress { get; set; }
        public string DepositAddress { get; set; }        // BTC Taproot address from FROST
        public decimal Balance { get; set; }
        
        // FROST Data
        public List<string> ValidatorAddressesSnapshot { get; set; }
        public string FrostGroupPublicKey { get; set; }   // Aggregated FROST group public key
        public int RequiredThreshold { get; set; }        // Initially 51
        
        // DKG Proof Data
        public string DKGProof { get; set; }              // Base64 + compressed
        public long ProofBlockHeight { get; set; }
        
        // Withdrawal State
        public VBTCWithdrawalStatus WithdrawalStatus { get; set; }
        public string? ActiveWithdrawalBTCDestination { get; set; }
        public decimal? ActiveWithdrawalAmount { get; set; }
        public string? ActiveWithdrawalRequestHash { get; set; }
        public long? WithdrawalRequestBlock { get; set; }
        public int ActiveWithdrawalFeeRate { get; set; }
        public long ActiveWithdrawalRequestTime { get; set; }
        
        // Historical Withdrawals
        public List<VBTCWithdrawalHistory> WithdrawalHistory { get; set; }
        #endregion

        #region Database Methods
        public static ILiteCollection<VBTCContractV2> GetDb()
        {
            var db = DbContext.DB_vBTC.GetCollection<VBTCContractV2>(DbContext.RSRV_VBTC_V2_CONTRACTS);
            return db;
        }

        public static VBTCContractV2? GetContract(string smartContractUID)
        {
            var contracts = GetDb();
            if (contracts != null)
            {
                var contract = contracts.FindOne(x => x.SmartContractUID == smartContractUID);
                if (contract != null)
                {
                    return contract;
                }
            }

            return null;
        }

        public static List<VBTCContractV2>? GetAllContracts()
        {
            var contracts = GetDb();
            if (contracts != null)
            {
                var contractList = contracts.FindAll().ToList();
                if (contractList.Any())
                {
                    return contractList;
                }
            }

            return null;
        }

        public static List<VBTCContractV2>? GetContractsByOwner(string ownerAddress)
        {
            var contracts = GetDb();
            if (contracts != null)
            {
                var contractList = contracts.Find(x => x.OwnerAddress == ownerAddress).ToList();
                if (contractList.Any())
                {
                    return contractList;
                }
            }

            return null;
        }

        public static void SaveContract(VBTCContractV2 contract)
        {
            try
            {
                var contracts = GetDb();
                var existing = contracts.FindOne(x => x.SmartContractUID == contract.SmartContractUID);

                if (existing == null)
                {
                    contracts.InsertSafe(contract);
                }
                else
                {
                    contracts.UpdateSafe(contract);
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError(ex.ToString(), "VBTCContractV2.SaveContract()");
            }
        }

        /// <summary>
        /// Save vBTC V2 smart contract to local database (for contract owners)
        /// </summary>
        public static async Task SaveSmartContract(SmartContractMain scMain, string? scText = null, string? rbxAddress = null)
        {
            var contracts = GetDb();

            var exist = contracts.FindOne(x => x.SmartContractUID == scMain.SmartContractUID);

            if (exist == null)
            {
                if (scMain.Features != null)
                {
                    var tknzV2Feature = scMain.Features
                        .Where(x => x.FeatureName == FeatureName.TokenizationV2)
                        .Select(x => x.FeatureFeatures)
                        .FirstOrDefault();

                    if (tknzV2Feature != null)
                    {
                        var tknz = (TokenizationV2Feature)tknzV2Feature;
                        if (tknz != null)
                        {
                            VBTCContractV2 contract = new VBTCContractV2
                            {
                                SmartContractUID = scMain.SmartContractUID,
                                OwnerAddress = rbxAddress ?? scMain.MinterAddress,
                                DepositAddress = tknz.DepositAddress,
                                Balance = 0, // Initial balance is 0, calculated from state trei
                                ValidatorAddressesSnapshot = tknz.ValidatorAddressesSnapshot ?? new List<string>(),
                                FrostGroupPublicKey = tknz.FrostGroupPublicKey,
                                RequiredThreshold = tknz.RequiredThreshold,
                                DKGProof = tknz.DKGProof,
                                ProofBlockHeight = tknz.ProofBlockHeight,
                                WithdrawalStatus = VBTCWithdrawalStatus.None,
                                WithdrawalHistory = new List<VBTCWithdrawalHistory>()
                            };
                            contracts.InsertSafe(contract);
                        }
                        else
                        {
                            ErrorLogUtility.LogError("Failed to cast TokenizationV2Feature", "VBTCContractV2.SaveSmartContract()");
                        }
                    }
                    else
                    {
                        ErrorLogUtility.LogError($"No TokenizationV2 feature found on SC: {scMain.SmartContractUID}", "VBTCContractV2.SaveSmartContract()");
                    }
                }
                else
                {
                    ErrorLogUtility.LogError($"No features found on SC: {scMain.SmartContractUID}", "VBTCContractV2.SaveSmartContract()");
                }
            }
        }

        /// <summary>
        /// Save vBTC V2 smart contract for balance holders (when someone receives vBTC V2 tokens)
        /// </summary>
        public static async Task SaveSmartContractTransfer(SmartContractMain scMain, string rbxAddress, string? scText = null)
        {
            var contracts = GetDb();

            // Check if contract already exists for this specific address
            var exist = contracts.FindOne(x => x.SmartContractUID == scMain.SmartContractUID && x.OwnerAddress == rbxAddress);

            if (exist == null)
            {
                if (scMain.Features != null)
                {
                    var tknzV2Feature = scMain.Features
                        .Where(x => x.FeatureName == FeatureName.TokenizationV2)
                        .Select(x => x.FeatureFeatures)
                        .FirstOrDefault();

                    if (tknzV2Feature != null)
                    {
                        var tknz = (TokenizationV2Feature)tknzV2Feature;
                        if (tknz != null)
                        {
                            // Calculate balance from state trei
                            decimal balance = 0.0M;
                            var scState = SmartContractStateTrei.GetSmartContractState(scMain.SmartContractUID);
                            if (scState?.SCStateTreiTokenizationTXes != null)
                            {
                                var transactions = scState.SCStateTreiTokenizationTXes
                                    .Where(x => x.FromAddress == rbxAddress || x.ToAddress == rbxAddress)
                                    .ToList();
                                balance = transactions.Sum(x => x.Amount);
                            }

                            VBTCContractV2 contract = new VBTCContractV2
                            {
                                SmartContractUID = scMain.SmartContractUID,
                                OwnerAddress = rbxAddress, // This user is a holder, not the original owner
                                DepositAddress = tknz.DepositAddress,
                                Balance = balance,
                                ValidatorAddressesSnapshot = tknz.ValidatorAddressesSnapshot ?? new List<string>(),
                                FrostGroupPublicKey = tknz.FrostGroupPublicKey,
                                RequiredThreshold = tknz.RequiredThreshold,
                                DKGProof = tknz.DKGProof,
                                ProofBlockHeight = tknz.ProofBlockHeight,
                                WithdrawalStatus = VBTCWithdrawalStatus.None,
                                WithdrawalHistory = new List<VBTCWithdrawalHistory>()
                            };
                            contracts.InsertSafe(contract);
                        }
                        else
                        {
                            ErrorLogUtility.LogError("Failed to cast TokenizationV2Feature", "VBTCContractV2.SaveSmartContractTransfer()");
                        }
                    }
                    else
                    {
                        ErrorLogUtility.LogError($"No TokenizationV2 feature found on SC: {scMain.SmartContractUID}", "VBTCContractV2.SaveSmartContractTransfer()");
                    }
                }
                else
                {
                    ErrorLogUtility.LogError($"No features found on SC: {scMain.SmartContractUID}", "VBTCContractV2.SaveSmartContractTransfer()");
                }
            }
        }

        public static void UpdateBalance(string smartContractUID, decimal newBalance)
        {
            try
            {
                var contracts = GetDb();
                var contract = contracts.FindOne(x => x.SmartContractUID == smartContractUID);

                if (contract != null)
                {
                    contract.Balance = newBalance;
                    contracts.UpdateSafe(contract);
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError(ex.ToString(), "VBTCContractV2.UpdateBalance()");
            }
        }

        public static void UpdateWithdrawalStatus(string smartContractUID, VBTCWithdrawalStatus status, 
            string? btcDestination = null, decimal? amount = null, string? requestHash = null, long? requestBlock = null)
        {
            try
            {
                var contracts = GetDb();
                var contract = contracts.FindOne(x => x.SmartContractUID == smartContractUID);

                if (contract != null)
                {
                    contract.WithdrawalStatus = status;
                    
                    if (status == VBTCWithdrawalStatus.Requested)
                    {
                        contract.ActiveWithdrawalBTCDestination = btcDestination;
                        contract.ActiveWithdrawalAmount = amount;
                        contract.ActiveWithdrawalRequestHash = requestHash;
                        contract.WithdrawalRequestBlock = requestBlock;
                    }
                    else if (status == VBTCWithdrawalStatus.None || status == VBTCWithdrawalStatus.Completed || status == VBTCWithdrawalStatus.Cancelled)
                    {
                        // Clear active withdrawal data
                        contract.ActiveWithdrawalBTCDestination = null;
                        contract.ActiveWithdrawalAmount = null;
                        contract.ActiveWithdrawalRequestHash = null;
                        contract.WithdrawalRequestBlock = null;
                    }

                    contracts.UpdateSafe(contract);
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError(ex.ToString(), "VBTCContractV2.UpdateWithdrawalStatus()");
            }
        }

        public static void AddWithdrawalRecord(string smartContractUID, VBTCWithdrawalHistory record)
        {
            try
            {
                var contracts = GetDb();
                var contract = contracts.FindOne(x => x.SmartContractUID == smartContractUID);

                if (contract != null)
                {
                    if (contract.WithdrawalHistory == null)
                    {
                        contract.WithdrawalHistory = new List<VBTCWithdrawalHistory>();
                    }

                    contract.WithdrawalHistory.Add(record);
                    contracts.UpdateSafe(contract);
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError(ex.ToString(), "VBTCContractV2.AddWithdrawalRecord()");
            }
        }

        public static void DeleteContract(string smartContractUID)
        {
            try
            {
                var contracts = GetDb();
                contracts.DeleteManySafe(x => x.SmartContractUID == smartContractUID);
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError(ex.ToString(), "VBTCContractV2.DeleteContract()");
            }
        }

        public static bool HasActiveWithdrawal(string smartContractUID)
        {
            var contract = GetContract(smartContractUID);
            if (contract != null)
            {
                return contract.WithdrawalStatus == VBTCWithdrawalStatus.Requested || 
                       contract.WithdrawalStatus == VBTCWithdrawalStatus.Pending_BTC;
            }
            return false;
        }

        public static void UpdateContract(VBTCContractV2 contract)
        {
            try
            {
                var contracts = GetDb();
                contracts.UpdateSafe(contract);
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError(ex.ToString(), "VBTCContractV2.UpdateContract()");
            }
        }
        #endregion
    }

    public enum VBTCWithdrawalStatus
    {
        None,
        Requested,
        Pending_BTC,
        Completed,
        Cancelled
    }

    public class VBTCWithdrawalRecord
    {
        public string RequestTxHash { get; set; }
        public string CompletionTxHash { get; set; }
        public string BTCTxHash { get; set; }
        public decimal Amount { get; set; }
        public string Destination { get; set; }
        public long RequestBlock { get; set; }
        public long? CompletionBlock { get; set; }
        public VBTCWithdrawalStatus Status { get; set; }
    }

    public class VBTCWithdrawalHistory
    {
        public string RequestHash { get; set; }
        public string CompletionHash { get; set; }
        public string BTCTransactionHash { get; set; }
        public decimal Amount { get; set; }
        public string BTCDestination { get; set; }
        public long RequestTime { get; set; }
        public long CompletionTime { get; set; }
        public int FeeRate { get; set; }
    }
}
