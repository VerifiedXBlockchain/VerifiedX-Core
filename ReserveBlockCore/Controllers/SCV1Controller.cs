﻿using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ReserveBlockCore.Data;
using ReserveBlockCore.Models;
using ReserveBlockCore.Models.DST;
using ReserveBlockCore.Models.SmartContracts;
using ReserveBlockCore.P2P;
using ReserveBlockCore.Services;
using ReserveBlockCore.Utilities;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Principal;
using System.Text;
using System.Web;

namespace ReserveBlockCore.Controllers
{
    [ActionFilterController]
    [Route("scapi/[controller]")]
    [Route("scapi/[controller]/{somePassword?}")]
    [ApiController]
    public class SCV1Controller : ControllerBase
    {
        /// <summary>
        /// Returns summary of this API
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        public IEnumerable<string> Get()
        {
            return new string[] { "Smart", "Contracts", "API" };
        }

        /// <summary>
        /// Another test command
        /// </summary>
        /// <returns></returns>
        [HttpGet("{id}")]
        public string Get(string id)
        {
            //use Id to get specific commands
            var output = "Command not recognized."; // this will only display if command not recognized.
            var command = id.ToLower();
            switch (command)
            {
                //This is initial example. Returns Genesis block in JSON format.
                case "getSCData":
                    //Do something later
                    break;
            }

            return output;
        }

        /// <summary>
        /// You can pass your json payload here and the system will return it to ensure its formatted correct.
        /// </summary>
        /// <param name="jsonData"></param>
        /// <returns></returns>
        [HttpPost("SCPassTest")]
        public object SCPassTest([FromBody] object jsonData)
        {
            var output = jsonData;

            return output;
        }

        /// <summary>
        /// You can pass your json payload here and the system will return it to ensure it deserializes correctly. If it does returns it back serialized.
        /// </summary>
        /// <returns></returns>
        [HttpPost("SCPassDesTest")]
        public string SCPassDesTest([FromBody] object jsonData)
        {
            var output = jsonData.ToString();
            try
            {
                var scMain = JsonConvert.DeserializeObject<SmartContractMain>(jsonData.ToString());

                var json = JsonConvert.SerializeObject(scMain);

                output = json;
            }
            catch (Exception ex)
            {
                output = $"Error - {ex.ToString()}. Please Try Again.";
            }

            return output;
        }

        /// <summary>
        /// returns the owner of a smart contract **deprecated
        /// </summary>
        /// <param name="scUID"></param>
        /// <returns></returns>
        [HttpGet("GetCurrentSCOwner/{scUID}")]
        public async Task<string> GetCurrentSCOwner(string scUID)
        {
            var output = "";

            var scState = SmartContractStateTrei.GetSmartContractState(scUID);

            output = JsonConvert.SerializeObject(scState);

            return output;
        }

        /// <summary>
        /// Allows you to search or dump out all smart contracts associated to your wallet
        /// </summary>
        /// <param name="pageNumber"></param>
        /// <param name="search"></param>
        /// <returns></returns>
        [HttpGet]
        [Route("GetAllSmartContracts/{pageNumber}")]
        [Route("GetAllSmartContracts/{pageNumber}/{**search}")]
        public async Task<string> GetAllSmartContracts(int pageNumber = 1, string? search = "")
        {
            var output = "";
            Stopwatch stopwatch3 = Stopwatch.StartNew();
            try
            {
                List<SmartContractMain> scs = new List<SmartContractMain>();
                List<SmartContractMain> scMainList = new List<SmartContractMain>();
                List<SmartContractStateTrei> scStateMainList = new List<SmartContractStateTrei>();
                ConcurrentBag<SmartContractStateTrei> scStateMainBag = new ConcurrentBag<SmartContractStateTrei>();

                var maxIndex = pageNumber * 9;
                var startIndex = ((maxIndex - 9));
                var range = 9;

                if (search != "" && search != "~")
                {
                    if (search != null)
                    {
                        var result = await NFTSearchUtility.Search(search);
                        if (result != null)
                        {
                            scs = result;
                        }
                    }
                }
                else
                {
                    scs = SmartContractMain.SmartContractData.GetSCs()
                   .FindAll()
                   .ToList();
                }

                var scStateTrei = SmartContractStateTrei.GetSCST();
                var accounts = AccountData.GetAccounts().FindAll().ToList();

                foreach (var sc in scs)
                {
                    var scState = scStateTrei.FindOne(x => x.SmartContractUID == sc.SmartContractUID);
                    if (scState != null)
                    {
                        var exist = accounts.Exists(x => x.Address == scState.OwnerAddress || x.Address == scState.NextOwner);
                        var rExist = ReserveAccount.GetReserveAccountSingle(scState.OwnerAddress) != null ? true : false;
                        if (!rExist)
                        {
                            if (scState.NextOwner != null)
                                rExist = ReserveAccount.GetReserveAccountSingle(scState.NextOwner) != null ? true : false;
                        }
                        if (rExist)
                            rExist = scState.NextOwner != null ? ReserveAccount.GetReserveAccountSingle(scState.NextOwner) != null ? true : false : true;

                        if ((exist || rExist))
                            scStateMainBag.Add(scState);
                    }
                }

                scStateMainList = scStateMainBag.ToList();

                var scStateCount = scStateMainList.Count();

                if (maxIndex > scStateCount)
                    range = (range - (maxIndex - scStateCount));

                scStateMainList = scStateMainList.GetRange(startIndex, range);

                if (scStateMainList.Count > 0)
                {
                    foreach (var scState in scStateMainList)
                    {
                        var scMain = SmartContractMain.GenerateSmartContractInMemory(scState.ContractData);
                        var scMainRec = scs.Where(x => x.SmartContractUID == scMain.SmartContractUID).FirstOrDefault();

                        scMain.Id = scMainRec != null ? scMainRec.Id : 0;
                        scMainList.Add(scMain);
                    }
                    if (scMainList.Count() > 0)
                    {
                        var orderedMainList = scMainList.OrderByDescending(x => x.Id).ToList();
                        var json = JsonConvert.SerializeObject(new { Count = scStateCount, Results = orderedMainList });
                        output = json;
                    }
                }
                else
                {
                    output = JsonConvert.SerializeObject(new { Count = 0, Results = scMainList }); ;
                }
            }
            catch (Exception ex)
            {
                output = JsonConvert.SerializeObject(new { Count = 0, Results = "null" }); ;
            }

            return output;
        }

        /// <summary>
        /// Allows you to search or dump out all smart contracts associated to your wallet
        /// </summary>
        /// <param name="pageNumber"></param>
        /// <param name="excludeToken"></param>
        /// <param name="tokensOnly"></param>
        /// <param name="search"></param>
        /// <returns></returns>
        [HttpGet]
        [Route("GetAllSmartContracts/{pageNumber}/{excludeToken?}")]
        [Route("GetAllSmartContracts/{pageNumber}/{excludeToken?}/{tokensOnly?}")]
        [Route("GetAllSmartContracts/{pageNumber}/{excludeToken?}/{tokensOnly?}/{**search}")]
        public async Task<string> GetAllSmartContracts(int pageNumber = 1, bool? excludeToken = false, bool? tokensOnly = false, string? search = "")
        {
            var output = "";
            Stopwatch stopwatch3 = Stopwatch.StartNew();
            try
            {
                List<SmartContractMain> scs = new List<SmartContractMain>();
                List<SmartContractMain> scMainList = new List<SmartContractMain>();
                List<SmartContractStateTrei> scStateMainList = new List<SmartContractStateTrei>();
                ConcurrentBag<SmartContractStateTrei> scStateMainBag = new ConcurrentBag<SmartContractStateTrei>();

                var maxIndex = pageNumber * 9;
                var startIndex = ((maxIndex - 9));
                var range = 9;

                if (search != "" && search != "~")
                {
                    if (search != null)
                    {
                        var result = await NFTSearchUtility.Search(search);
                        if (result != null)
                        {
                            scs = result;
                        }
                    }
                }
                else
                {
                    //LiteDB.LiteException: 'Any/All requires simple parameter on left side. Eg: `
                    //x => x.Phones.Select(p => p.Number).Any(n => n > 5)`'
                    scs = SmartContractMain.SmartContractData.GetSCs()
                    .Find(x => x.Features == null ||
                        !x.Features.Where(y => y != null && y.FeatureName == FeatureName.Tokenization).Any())
                    .ToList();
                }

                var scStateTrei = SmartContractStateTrei.GetSCST();
                var accounts = AccountData.GetAccounts().FindAll().ToList();

                foreach (var sc in scs)
                {
                    var scState = scStateTrei.FindOne(x => x.SmartContractUID == sc.SmartContractUID);
                    if (scState != null)
                    {
                        var exist = accounts.Exists(x => x.Address == scState.OwnerAddress || x.Address == scState.NextOwner);
                        var rExist = ReserveAccount.GetReserveAccountSingle(scState.OwnerAddress) != null ? true : false;
                        if (!rExist)
                        {
                            if (scState.NextOwner != null)
                                rExist = ReserveAccount.GetReserveAccountSingle(scState.NextOwner) != null ? true : false;
                        }
                        if (rExist)
                            rExist = scState.NextOwner != null ? ReserveAccount.GetReserveAccountSingle(scState.NextOwner) != null ? true : false : true;

                        var isToken = scState.IsToken != null ? scState.IsToken.Value : false;

                        if(!tokensOnly.Value)
                        {
                            if (excludeToken.Value)
                            {
                                if (!isToken && (exist || rExist))
                                    scStateMainBag.Add(scState);
                            }
                            else
                            {
                                if ((exist || rExist))
                                    scStateMainBag.Add(scState);
                            }
                        }
                        else
                        {
                            if (isToken && (exist || rExist))
                                scStateMainBag.Add(scState);
                        }
                    }
                }

                scStateMainList = scStateMainBag.ToList();

                var scStateCount = scStateMainList.Count();

                if (maxIndex > scStateCount)
                    range = (range - (maxIndex - scStateCount));

                scStateMainList = scStateMainList.GetRange(startIndex, range);

                if (scStateMainList.Count > 0)
                {
                    foreach (var scState in scStateMainList)
                    {
                        var scMain = SmartContractMain.GenerateSmartContractInMemory(scState.ContractData);
                        var scMainRec = scs.Where(x => x.SmartContractUID == scMain.SmartContractUID).FirstOrDefault();

                        scMain.Id = scMainRec != null ? scMainRec.Id : 0;
                        scMainList.Add(scMain);
                    }
                    if (scMainList.Count() > 0)
                    {
                        var orderedMainList = scMainList.OrderByDescending(x => x.Id).ToList();
                        var json = JsonConvert.SerializeObject(new { Count = scStateCount, Results = orderedMainList });
                        output = json;
                    }
                }
                else
                {
                    output = JsonConvert.SerializeObject(new { Count = 0, Results = scMainList }); ;
                }
            }
            catch (Exception ex)
            {
                output = JsonConvert.SerializeObject(new { Count = 0, Results = "null" }); ;
            }

            return output;
        }

        /// <summary>
        /// Allows you to search or dump out minted smart contracts associated to your wallet
        /// </summary>
        /// <param name="pageNumber"></param>
        /// <param name="search"></param>
        /// <returns></returns>
        [HttpGet]
        [Route("GetMintedSmartContracts/{pageNumber}")]
        [Route("GetMintedSmartContracts/{pageNumber}/{**search}")]
        public async Task<string> GetMintedSmartContracts(int pageNumber = 1, string? search = "")
        {
            var output = "";
            try
            {
                List<SmartContractMain> scs = new List<SmartContractMain>();
                List<SmartContractMain> scMainList = new List<SmartContractMain>();
                List<SmartContractMain> scEvoMainList = new List<SmartContractMain>();
                ConcurrentBag<SmartContractMain> resultCollection = new ConcurrentBag<SmartContractMain>();

                if (search != "" && search != "~")
                {
                    if (search != null)
                    {
                        var result = await NFTSearchUtility.Search(search, true);
                        if (result != null)
                        {
                            scs = result;
                        }
                    }
                }
                else
                {
                    scs = SmartContractMain.SmartContractData.GetSCs().Find(x => x.IsMinter == true)
                    .Where(x => x.Features != null && x.Features.Any(y => y.FeatureName == FeatureName.Evolving))
                    .ToList();
                }


                var maxIndex = pageNumber * 9;
                var startIndex = ((maxIndex - 9));
                var range = 9;

                foreach (var sc in scs)
                {
                    var scStateTrei = SmartContractStateTrei.GetSmartContractState(sc.SmartContractUID);
                    if (scStateTrei != null)
                    {
                        var scMain = SmartContractMain.GenerateSmartContractInMemory(scStateTrei.ContractData);
                        if (scMain.Features != null)
                        {
                            scMain.Id = sc.Id;
                            var evoFeatures = scMain.Features.Where(x => x.FeatureName == FeatureName.Evolving).Select(x => x.FeatureFeatures).FirstOrDefault();
                            var isDynamic = false;
                            if (evoFeatures != null)
                            {
                                var evoFeatureList = (List<EvolvingFeature>)evoFeatures;
                                foreach (var feature in evoFeatureList)
                                {
                                    var evoFeature = (EvolvingFeature)feature;
                                    if (evoFeature.IsDynamic == true)
                                        isDynamic = true;
                                }
                            }

                            if (!isDynamic)
                                resultCollection.Add(scMain);
                        }
                    }
                }

                scMainList = resultCollection.ToList();

                var scscMainListCount = scMainList.Count();

                if (maxIndex > scscMainListCount)
                    range = (range - (maxIndex - scscMainListCount));

                scMainList = scMainList.GetRange(startIndex, range);

                if (scMainList.Count() > 0)
                {
                    var orderedMainList = scMainList.OrderByDescending(x => x.Id).ToList();
                    var json = JsonConvert.SerializeObject(new { Count = scscMainListCount, Results = orderedMainList });
                    output = json;
                }
                else
                {
                    var json = JsonConvert.SerializeObject(new { Count = 0, Results = scMainList });
                    output = json;
                }
            }
            catch (Exception ex)
            {
                var json = JsonConvert.SerializeObject(new { Count = 0, Results = "null" });
                output = json;
            }


            return output;
        }

        /// <summary>
        /// Shows a single smart contract with the provided id
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        [HttpGet("GetSingleSmartContract/{id}")]
        public async Task<string> GetSingleSmartContract(string id)
        {
            var output = "";
            try
            {
                var sc = SmartContractMain.SmartContractData.GetSmartContract(id);

                if (sc == null)
                    return "null";

                var result = await SmartContractReaderService.ReadSmartContract(sc);

                var scMain = result.Item2;
                var scCode = result.Item1;

                var bytes = Encoding.Unicode.GetBytes(scCode);
                var scBase64 = bytes.ToCompress().ToBase64();
                var scMainUpdated = SmartContractMain.GenerateSmartContractInMemory(scBase64);
                if (scMainUpdated.Features != null)
                {
                    var featuresList = scMainUpdated.Features.Where(x => x.FeatureName == FeatureName.Evolving).FirstOrDefault();
                    int currentState = 0;
                    if (featuresList != null)
                    {
                        var evoFeatureList = (List<EvolvingFeature>)featuresList.FeatureFeatures;
                        var currentStage = evoFeatureList.Where(x => x.IsCurrentState == true).FirstOrDefault();
                        if (currentStage != null)
                        {
                            currentState = currentStage.EvolutionState;
                        }
                    }

                    var scMainFeatures = scMain.Features.Where(x => x.FeatureName == FeatureName.Evolving).FirstOrDefault();

                    if (scMainFeatures != null)
                    {
                        var scMainFeaturesList = (List<EvolvingFeature>)scMainFeatures.FeatureFeatures;
                        var evoStage = scMainFeaturesList.Where(x => x.EvolutionState == currentState).FirstOrDefault();
                        if (evoStage != null)
                        {
                            evoStage.IsCurrentState = true;
                            var stageList = scMainFeaturesList.Where(x => x.EvolutionState != currentState).ToList();
                            stageList.ForEach(x => { x.IsCurrentState = false; });
                        }
                    }
                }

                scMainUpdated.Id = sc.Id;
                var currentOwner = "";
                var scState = SmartContractStateTrei.GetSmartContractState(scMain.SmartContractUID);
                if (scState != null)
                {
                    currentOwner = scState.OwnerAddress;
                }

                var scInfo = new[]
                {
                new { SmartContract = scMain, SmartContractCode = scCode, CurrentOwner = currentOwner}
            };

                if (sc != null)
                {
                    var json = JsonConvert.SerializeObject(scInfo);
                    output = json;
                }
                else
                {
                    output = "null";
                }
            }
            catch (Exception ex)
            {
                output = ex.ToString();
            }

            return output;
        }

        /// <summary>
        /// Returns a list of scUIDS owned by an address
        /// </summary>
        /// <param name="address"></param>
        /// <returns></returns>
        [HttpGet("GetSmartContractsByAddress/{address}")]
        public async Task<string> GetSmartContractsByAddress(string address)
        {
            string output = "";
            List<string> scUIDList = new List<string>();
            var scList = SmartContractStateTrei.GetSmartContractsOwnedByAddress(address);

            if (scList != null)
            {
                foreach (var sc in scList)
                {
                    scUIDList.Add(sc.SmartContractUID);
                }
                output = JsonConvert.SerializeObject(new { Success = true, Message = $"Smart Contracts Found", SCUIDList = scUIDList }, Formatting.Indented);
            }
            else
            {
                output = JsonConvert.SerializeObject(new { Success = false, Message = $"No Smart Contracts Found", SCUIDList = scUIDList }, Formatting.Indented);
            }

            return output;

        }

        /// <summary>
        /// GetSmartContractsState
        /// </summary>
        /// <param name="address"></param>
        /// <returns></returns>
        [HttpGet("GetSmartContractsState/{**scUID}")]
        public async Task<string> GetSmartContractsState(string scUID)
        {
            string output = "";
            var scState = SmartContractStateTrei.GetSmartContractState(scUID);

            if (scState != null)
            {
                output = JsonConvert.SerializeObject(new { Success = true, Message = $"Smart Contracts Found", SCState = scState }, Formatting.Indented);
            }
            else
            {
                output = JsonConvert.SerializeObject(new { Success = false, Message = $"No Smart Contracts Found" }, Formatting.Indented);
            }

            return output;

        }

        /// <summary>
        /// Returns the locator beacon information for a smart contract assets.
        /// </summary>
        /// <param name="scUID"></param>
        /// <returns></returns>
        [HttpGet("GetLastKnownLocators/{scUID}")]
        public async Task<string> GetLastKnownLocators(string scUID)
        {
            string output = "";

            var scState = SmartContractStateTrei.GetSmartContractState(scUID);

            if (scState != null)
            {
                output = JsonConvert.SerializeObject(new { Result = "Success", Message = $"Locators Found.", Locators = scState.Locators });
            }
            else
            {
                output = JsonConvert.SerializeObject(new { Result = "Fail", Message = $"Locators Not Found." });
            }

            return output;

        }

        /// <summary>
        /// Lets you associate an asset to an NFT repo in the event media is lost or damage. MD5 must match.
        /// </summary>
        /// <param name="scUID"></param>
        /// <param name="assetPath"></param>
        /// <returns></returns>
        [HttpGet("AssociateNFTAsset/{scUID}/{**assetPath}")]
        public async Task<string> AssociateNFTAsset(string scUID, string assetPath)
        {
            string output = "";

            try
            {
                assetPath = assetPath.Replace("%2F", "/");
                string fileName = Path.GetFileName(assetPath);
                string incFileMD5 = assetPath.ToMD5();
                string scMD5List = "";
                Dictionary<string, string> assetMD5Dict = new Dictionary<string, string>();

                var scStateTrei = SmartContractStateTrei.GetSCST();
                if (scStateTrei != null)
                {
                    var scState = scStateTrei.FindOne(x => x.SmartContractUID == scUID);
                    if (scState != null)
                    {
                        scMD5List = scState.MD5List != null ? scState.MD5List : "";
                    }
                    else
                    {
                        output = JsonConvert.SerializeObject(new { Result = "Fail", Message = $"Could not find record of NFT on chain." });
                        return output;
                    }
                }

                if (!string.IsNullOrWhiteSpace(scMD5List))
                {
                    var scAssetList = scMD5List.Split("<>").ToList();
                    foreach (var scAsset in scAssetList)
                    {
                        var recSplit = scAsset.Split("::");

                        assetMD5Dict.Add(recSplit[0], recSplit[1]);
                    }
                }
                else
                {
                    output = JsonConvert.SerializeObject(new { Result = "Fail", Message = $"MD5 List was not found. Cannot verify integrity of asset media." });
                    return output;
                }

                var keyCheck = assetMD5Dict.ContainsKey(fileName);
                if (keyCheck)
                {
                    var chainMD5 = assetMD5Dict[fileName];
                    if (chainMD5 == incFileMD5)
                    {
                        //import
                        var result = NFTAssetFileUtility.MoveAsset(assetPath, fileName, scUID);
                        if (result == true)
                            output = JsonConvert.SerializeObject(new { Result = "Success", Message = $"Media associated with NFT." });
                        else
                            output = JsonConvert.SerializeObject(new { Result = "Fail", Message = $"Failed to move media. Please ensure file is not open anywhere." });
                        return output;
                    }
                    else
                    {
                        output = JsonConvert.SerializeObject(new { Result = "Fail", Message = $"Incoming media did not match the MD5 on chain." });
                        return output;
                    }
                }
                else
                {
                    output = JsonConvert.SerializeObject(new { Result = "Fail", Message = $"File was not found in on-chain list. Please ensure file name has been altered." });
                    return output;
                }
            }
            catch (Exception ex)
            {
                output = JsonConvert.SerializeObject(new { Result = "Fail", Message = $"Unknown error occured. Error {ex.ToString()}" });
                return output;
            }

        }

        /// <summary>
        /// Creates the smart contract SEN for minting.
        /// </summary>
        /// <param name="jsonData"></param>
        /// <remarks>
        /// Sample request:
        ///     POST /CreateSmartContract
        ///     {
        ///     "Id":0,
        ///     "Name":"Trillium NFT",
        ///     "Description":"The First NFT From Trillium for RBX",
        ///     "MinterName":"The Minter",
        ///     "MinterAddress":"xMpa8DxDLdC9SQPcAFBc2vqwyPsoFtrWyC",
        ///     "SmartContractAsset":
        ///     {
        ///         "AssetId":"00000000-0000-0000-0000-000000000000",
        ///         "Name":"13.png",
        ///         "Location":"D:\\13.png",
        ///         "AssetAuthorName":"Author Man",
        ///         "Extension":".png",
        ///         "FileSize":1129
        ///     },
        ///     "IsPublic":false,
        ///     "SmartContractUID":null,
        ///     "IsMinter":false,
        ///     "IsPublished":false,
        ///     "Features":null
        ///     }
        /// </remarks>
        /// <returns></returns>
        [HttpPost("CreateSmartContract")]
        public async Task<string> CreateSmartContract([FromBody] object jsonData)
        {
            var output = "";

            try
            {
                SmartContractReturnData scReturnData = new SmartContractReturnData();
                var scMain = JsonConvert.DeserializeObject<SmartContractMain>(jsonData.ToString());

                if (scMain != null)
                {
                    SCLogUtility.Log($"Creating Smart Contract: {scMain.SmartContractUID}", "SCV1Controller.CreateSmartContract([FromBody] object jsonData)");
                }
                else
                {
                    SCLogUtility.Log($"scMain is null", "SCV1Controller.CreateSmartContract([FromBody] object jsonData) - Line 190");
                    throw new Exception("Smart Contract was null.");
                }
                try
                {
                    var featureList = scMain.Features;

                    if (featureList?.Count() > 0)
                    {
                        var royalty = featureList.Where(x => x.FeatureName == FeatureName.Royalty).FirstOrDefault();
                        if (royalty != null)
                        {
                            var royaltyFeatures = ((JObject)royalty.FeatureFeatures).ToObject<RoyaltyFeature>();
                            if (royaltyFeatures != null)
                            {
                                if (royaltyFeatures.RoyaltyType == RoyaltyType.Flat)
                                {
                                    throw new Exception("Flat rates may no longer be used.");
                                }
                                if (royaltyFeatures.RoyaltyAmount >= 1.0M)
                                {
                                    throw new Exception("Royalty cannot be over 1. Must be .99 or less.");
                                }
                            }
                        }
                    }

                    scMain.SCVersion = Globals.SCVersion;

                    var result = await SmartContractWriterService.WriteSmartContract(scMain);
                    scReturnData.Success = true;
                    scReturnData.SmartContractCode = result.Item1;
                    scReturnData.SmartContractMain = result.Item2;

                    var txData = "";

                    if (result.Item1 != null)
                    {
                        var bytes = Encoding.Unicode.GetBytes(result.Item1);
                        var scBase64 = bytes.ToCompress().ToBase64();
                        var function = result.Item3 ? "TokenDeploy()" : "Mint()";
                        var newSCInfo = new[]
                        {
                            new { Function = function, ContractUID = scMain.SmartContractUID, Data = scBase64}
                        };

                        txData = JsonConvert.SerializeObject(newSCInfo);
                    }

                    var nTx = new Transaction
                    {
                        Timestamp = TimeUtil.GetTime(),
                        FromAddress = scReturnData.SmartContractMain.MinterAddress,
                        ToAddress = scReturnData.SmartContractMain.MinterAddress,
                        Amount = 0.0M,
                        Fee = 0,
                        Nonce = AccountStateTrei.GetNextNonce(scMain.MinterAddress),
                        TransactionType = !result.Item3 ? TransactionType.NFT_MINT : TransactionType.FTKN_MINT,
                        Data = txData
                    };

                    //Calculate fee for tx.
                    nTx.Fee = FeeCalcService.CalculateTXFee(nTx);

                    nTx.Build();

                    var checkSize = await TransactionValidatorService.VerifyTXSize(nTx);

                    if(!checkSize)
                        throw new Exception("Image is too large for token image. Must be 25kb or less.");

                    SmartContractMain.SmartContractData.SaveSmartContract(result.Item2, result.Item1);

                    var scInfo = new[]
                    {
                    new {Success = true, SmartContract = result.Item2, SmartContractCode = result.Item1, Transaction = nTx}
                    };
                    var json = JsonConvert.SerializeObject(scInfo, Formatting.Indented);
                    output = json;
                    SCLogUtility.Log($"Smart Contract Creation Success: {scMain.SmartContractUID}", "SCV1Controller.CreateSmartContract([FromBody] object jsonData)");
                }
                catch (Exception ex)
                {
                    SCLogUtility.Log($"Failed to create TX for Smartcontract. Error: {ex.ToString()}", "SCV1Controller.CreateSmartContract([FromBody] object jsonData) - Line 231 catch");
                    scReturnData.Success = false;
                    scReturnData.SmartContractCode = "Failure";
                    scReturnData.SmartContractMain = scMain;

                    var scInfo = new[]
                    {
                    new {Success = false, SmartContract = scReturnData.SmartContractCode, SmartContractCode = scReturnData.SmartContractMain}
                    };
                    var json = JsonConvert.SerializeObject(scInfo, Formatting.Indented);
                    output = json;
                }

            }
            catch (Exception ex)
            {
                SCLogUtility.Log($"Failed to create smart contract. Error Message: {ex.ToString()}", "SCV1Controller.CreateSmartContract([FromBody] object jsonData) - Line 247 catch");
                output = $"Error - {ex.ToString()}. Please Try Again...";
            }


            return output;
        }

        /// <summary>
        /// Mints the contract onto the network created from /CreateSmartContract
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        [HttpGet("MintSmartContract/{id}")]
        public async Task<string> MintSmartContract(string id)
        {
            var output = "";

            var scMain = SmartContractMain.SmartContractData.GetSmartContract(id);

            if (scMain.IsPublished == true)
            {
                output = "This NFT has already been published";
                SCLogUtility.Log($"This NFT has already been published", "SCV1Controller.MintSmartContract(string id)");
            }
            else
            {
                var scTx = await SmartContractService.MintSmartContractTx(scMain);
                if (scTx == null)
                {
                    output = "Failed to publish smart contract: " + scMain.Name + ". Id: " + id;
                    SCLogUtility.Log($"Failed to publish smart contract: {scMain.SmartContractUID}", "SCV1Controller.MintSmartContract(string id)");
                }
                else
                {
                    output = "Smart contract has been published to mempool";
                    SCLogUtility.Log($"Smart contract has been published to mempool : {scMain.SmartContractUID}", "SCV1Controller.MintSmartContract(string id)");
                }
            }


            return output;
        }
        
        /// <summary>
        /// Creates a transaction to send a desired NFT from one wallet to another
        /// </summary>
        /// <param name="id"></param>
        /// <param name="toAddress"></param>
        /// <param name="backupURL"></param>
        /// <returns></returns>
        [HttpGet]
        [Route("TransferNFT/{id}/{toAddress}")]
        [Route("TransferNFT/{id}/{toAddress}/{**backupURL}")]
        public async Task<string> TransferNFT(string id, string toAddress, string? backupURL = "")
        {
            var output = "";
            try
            {
                var sc = SmartContractMain.SmartContractData.GetSmartContract(id);
                if (sc != null)
                {
                    if (sc.IsPublished == true)
                    {
                        //Get beacons here!
                        //This will eventually need to be a chosen parameter someone chooses.                         
                        if (!Globals.Beacons.Any())
                        {
                            output = JsonConvert.SerializeObject(new { Result = "Fail", Message = "You do not have any beacons stored." });
                            SCLogUtility.Log("Error - You do not have any beacons stored.", "SCV1Controller.TransferNFT()");
                            return output;
                        }
                        else
                        {
                            if(!Globals.Beacon.Values.Where(x => x.IsConnected).Any())
                            {
                                var beaconConnectionResult = await BeaconUtility.EstablishBeaconConnection(true, false);
                                if(!beaconConnectionResult)
                                {
                                    output = JsonConvert.SerializeObject(new { Result = "Fail", Message = "You failed to connect to any beacons." });
                                    SCLogUtility.Log("Error - You failed to connect to any beacons.", "SCV1Controller.TransferNFT()");
                                    return output;
                                }
                            }
                            var connectedBeacon = Globals.Beacon.Values.Where(x => x.IsConnected).FirstOrDefault();
                            if(connectedBeacon == null)
                            {
                                output = JsonConvert.SerializeObject(new { Result = "Fail", Message = "You have lost connection to beacons. Please attempt to resend." });
                                SCLogUtility.Log("Error - You have lost connection to beacons. Please attempt to resend.", "SCV1Controller.TransferNFT()");
                                return output;
                            }
                            toAddress = toAddress.Replace(" ", "").ToAddressNormalize();
                            var localAddress = AccountData.GetSingleAccount(toAddress);

                            var assets = await NFTAssetFileUtility.GetAssetListFromSmartContract(sc);
                            var md5List = await MD5Utility.GetMD5FromSmartContract(sc);

                            SCLogUtility.Log($"Sending the following assets for upload: {md5List}", "SCV1Controller.TransferNFT()");

                            bool result = false;
                            if (localAddress == null)
                            {
                                result = await P2PClient.BeaconUploadRequest(connectedBeacon, assets, sc.SmartContractUID, toAddress, md5List).WaitAsync(new TimeSpan(0,0,10));
                                SCLogUtility.Log($"NFT Beacon Upload Request Completed. SCUID: {sc.SmartContractUID}", "SCV1Controller.TransferNFT()");
                            }
                            else
                            {
                                result = true;
                            }

                            if (result == true)
                            {
                                var aqResult = AssetQueue.CreateAssetQueueItem(sc.SmartContractUID, toAddress, connectedBeacon.Beacons.BeaconLocator, md5List, assets,
                                    AssetQueue.TransferType.Upload);
                                SCLogUtility.Log($"NFT Asset Queue Items Completed. SCUID: {sc.SmartContractUID}", "SCV1Controller.TransferNFT()");

                                if (aqResult)
                                {
                                    _ = Task.Run(() => SmartContractService.TransferSmartContract(sc, toAddress, connectedBeacon, md5List, backupURL));
                                        
                                    var success = JsonConvert.SerializeObject(new {Result = "Success", Message = "NFT Transfer has been started." });
                                    output = success;
                                    SCLogUtility.Log($"NFT Process Completed in CLI. SCUID: {sc.SmartContractUID}. Response: {output}", "SCV1Controller.TransferNFT()");
                                    return output;
                                }
                                else
                                {
                                    output = JsonConvert.SerializeObject(new { Result = "Fail", Message = "Failed to add upload to Asset Queue. Please check logs for more details." });
                                    SCLogUtility.Log($"Failed to add upload to Asset Queue - TX terminated. Data: scUID: {sc.SmartContractUID} | toAddres: {toAddress} | Locator: {connectedBeacon.Beacons.BeaconLocator} | MD5List: {md5List} | backupURL: {backupURL}", "SCV1Controller.TransferNFT()");
                                }

                            }
                            else
                            {
                                output = JsonConvert.SerializeObject(new { Result = "Fail", Message = $"Beacon upload failed. Result was : {result}" });
                                SCLogUtility.Log($"Beacon upload failed. Result was : {result}", "SCV1Controller.TransferNFT()");
                            }
                        }
                    }
                    else
                    {
                        output = JsonConvert.SerializeObject(new { Result = "Fail", Message = "Smart Contract Found, but has not been minted." }); 
                    }
                }
                else
                {
                    output = JsonConvert.SerializeObject(new { Result = "Fail", Message = "No Smart Contract Found Locally." }); 
                }
            }
            catch(Exception ex)
            {
                output = JsonConvert.SerializeObject(new { Result = "Fail", Message = $"Unknown Error Occurred. Error: {ex.ToString()}" });
                SCLogUtility.Log($"Unknown Error Transfering NFT. Error: {ex.ToString()}", "SCV1Controller.TransferNFT()");
            }
            
            
            return output;
        }

        /// <summary>
        /// Creates a transaction that burns the desired NFT
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        [HttpGet("Burn/{id}")]
        public async Task<string> Burn(string id)
        {
            var output = "";

            var sc = SmartContractMain.SmartContractData.GetSmartContract(id);
            if (sc != null)
            {
                if (sc.IsPublished == true)
                {
                    var tx = await SmartContractService.BurnSmartContract(sc);

                    var txJson = JsonConvert.SerializeObject(tx);
                    output = txJson;
                }
            }

            return output;
        }

        /// <summary>
        /// Creates a transaction to start a sale for an NFT.
        /// </summary>
        /// <param name="scUID"></param>
        /// <param name="toAddress"></param>
        /// <param name="saleAmount"></param>
        /// <param name="backupURL"></param>
        /// <returns></returns>
        [HttpGet]
        [Route("TransferSale/{scUID}/{toAddress}/{saleAmount}")]
        [Route("TransferSale/{scUID}/{toAddress}/{saleAmount}/{**backupURL}")]
        public async Task<string> TransferSale(string scUID, string toAddress, decimal saleAmount, string? backupURL = "")
        {
            var sc = SmartContractMain.SmartContractData.GetSmartContract(scUID);
            if (sc != null)
            {
                if (sc.IsPublished == true)
                {
                    var purchaseKey = RandomStringUtility.GetRandomStringOnlyLetters(10, true);
                    var scState = SmartContractStateTrei.GetSmartContractState(scUID);

                    if (scState == null)
                        return JsonConvert.SerializeObject(new { Success = false, Message = $"Could not located state information for Smart Contract: {scUID}" });

                    var localAccount = AccountData.GetSingleAccount(scState.OwnerAddress);

                    if (localAccount == null)
                        return JsonConvert.SerializeObject(new { Success = false, Message = $"Local account not found. You wallet is not the owner of this NFT." });

                    if (!Globals.Beacons.Any())
                    {
                        SCLogUtility.Log("Error - You do not have any beacons stored.", "SCV1Controller.TransferNFT()");
                        return JsonConvert.SerializeObject(new { Success = false, Message = "You do not have any beacons stored." });
                    }

                    if (!Globals.Beacon.Values.Where(x => x.IsConnected).Any())
                    {
                        var beaconConnectionResult = await BeaconUtility.EstablishBeaconConnection(true, false);
                        if (!beaconConnectionResult)
                        {
                            SCLogUtility.Log("Error - You failed to connect to any beacons.", "SCV1Controller.TransferNFT()");
                            return  JsonConvert.SerializeObject(new { Success = false, Message = "You failed to connect to any beacons." });
                        }
                    }
                    var connectedBeacon = Globals.Beacon.Values.Where(x => x.IsConnected).FirstOrDefault();
                    if (connectedBeacon == null)
                    {
                        SCLogUtility.Log("Error - You have lost connection to beacons. Please attempt to resend.", "SCV1Controller.TransferNFT()");
                        return JsonConvert.SerializeObject(new { Success = false, Message = "You have lost connection to beacons. Please attempt to resend." });
                    }

                    toAddress = toAddress.Replace(" ", "").ToAddressNormalize();
                    var localAddress = AccountData.GetSingleAccount(toAddress);

                    var assets = await NFTAssetFileUtility.GetAssetListFromSmartContract(sc);
                    var md5List = await MD5Utility.GetMD5FromSmartContract(sc);

                    _ = Task.Run(() => SmartContractService.StartTransferSaleSmartContractTX(sc, 
                        scUID, toAddress, saleAmount, purchaseKey, connectedBeacon, md5List, backupURL));

                    var success = JsonConvert.SerializeObject(new { Success = true, Message = "NFT Transfer has been started." });
                    SCLogUtility.Log($"NFT Process Completed in CLI. SCUID: {sc.SmartContractUID}. Response: {success}", "SCV1Controller.TransferNFT()");
                    return success;                    

                }
                else
                {
                    return JsonConvert.SerializeObject(new { Success = false, Message = $"NFT is not published." });
                }
            }
            else
            {
                return JsonConvert.SerializeObject(new { Success = false, Message = $"NFT could not be found locally." });
            }
        }

        /// <summary>
        /// Complete NFT purchase
        /// </summary>
        /// <param name="keySign"></param>
        /// <param name="scUID"></param>
        /// <returns></returns>
        [HttpGet("CompleteTransferSale/{keySign}/{**scUID}")]
        public async Task<string> CompleteTransferSale(string keySign, string scUID)
        {
            var scStateTrei = SmartContractStateTrei.GetSmartContractState(scUID);

            if (scStateTrei == null)
                return JsonConvert.SerializeObject(new { Success = false, Message = "Smart Contract was not found." });

            var nextOwner = scStateTrei.NextOwner;
            var purchaseAmount = scStateTrei.PurchaseAmount;

            if (nextOwner == null || purchaseAmount == null)
                return JsonConvert.SerializeObject(new { Success = false, Message = $"Smart contract data missing or purchase already completed. Next Owner: {nextOwner} | Purchase Amount {purchaseAmount}" });

            var localAccount = AccountData.GetSingleAccount(nextOwner);

            if (localAccount == null)
                return JsonConvert.SerializeObject(new { Success = false, Message = $"A local account with next owner address was not found. Next Owner: {nextOwner}" });

            if (localAccount.Balance <= purchaseAmount.Value)
                return JsonConvert.SerializeObject(new { Success = false, Message = $"Not enough funds to purchase NFT. Purchase Amount {purchaseAmount} | Current Balance: {localAccount.Balance}." });

            var result = await SmartContractService.CompleteTransferSaleSmartContractTX(scUID, scStateTrei.OwnerAddress, purchaseAmount.Value, keySign);

            return JsonConvert.SerializeObject(new { Success = result.Item1 == null ? false : true, Message = result.Item2 });
        }

        /// <summary>
        /// Cancels a sale.
        /// </summary>
        /// <param name="scUID"></param>
        /// <returns></returns>
        [HttpGet("CancelSale/{scUID}")]
        public async Task<string> CancelSale(string scUID)
        {
            var scStateTrei = SmartContractStateTrei.GetSmartContractState(scUID);
            if (scStateTrei == null)
                return JsonConvert.SerializeObject(new { Success = false, Message = "Smart Contract was not found." });

            var currentOwner = scStateTrei.OwnerAddress;

            var localAccount = AccountData.GetSingleAccount(currentOwner);

            if (localAccount == null)
                return JsonConvert.SerializeObject(new { Success = false, Message = $"A local account with owner address was not found. Owner: {currentOwner}" });

            if(!scStateTrei.IsLocked)
                return JsonConvert.SerializeObject(new { Success = false, Message = $"This NFT is not locked for a sale." });

            if (string.IsNullOrEmpty(scStateTrei.NextOwner))
                return JsonConvert.SerializeObject(new { Success = false, Message = $"There is no next owner on this NFT. Nothing to Cancel." });



            return "";
        }

        /// <summary>
        /// Creates ownership script
        /// </summary>
        /// <param name="scUID"></param>
        /// <returns></returns>
        [HttpGet("ProveOwnership/{scUID}")]
        public async Task<string> ProveOwnership(string scUID)
        {
            var output = "";

            var scState = SmartContractStateTrei.GetSmartContractState(scUID);
            if (scState == null)
                return JsonConvert.SerializeObject(new { Success = false, Message = $"Could not located state information for Smart Contract: {scUID}" });

            var localAccount = AccountData.GetSingleAccount(scState.OwnerAddress);

            if (localAccount == null)
                return JsonConvert.SerializeObject(new { Success = false, Message = $"Local account not found. You wallet is not the owner of this NFT." });

            bool sigGood = false;
            var completedOwnershipScript = "";

            while (!sigGood)
            {
                var randomKey = RandomStringUtility.GetRandomStringOnlyLetters(8, false);
                var timestamp = TimeUtil.GetTime();

                var sigMessage = $"{randomKey}.{timestamp}";

                var sigScript = SignatureService.CreateSignature(sigMessage, localAccount.GetPrivKey, localAccount.PublicKey);

                completedOwnershipScript = $"{localAccount.Address}<>{sigMessage}<>{sigScript}<>{scUID}";

                var sigVerifies = SignatureService.VerifySignature(localAccount.Address, sigMessage, sigScript);

                if (sigVerifies)
                    sigGood = true;
            }

            return JsonConvert.SerializeObject(new { Success = true, Message = $"Ownership Script Created.", OwnershipScript = completedOwnershipScript });
        }

        /// <summary>
        /// Adds NFT to local from network if you own it.
        /// </summary>
        /// <param name="scUID"></param>
        /// <returns></returns>
        [HttpGet("AddNFTDataFromNetwork/{scUID}")]
        public async Task<string> AddNFTDataFromNetwork(string scUID)
        {
            var scState = SmartContractStateTrei.GetSmartContractState(scUID);
            if (scState == null)
                return JsonConvert.SerializeObject(new { Success = false, Message = $"Could not located state information for Smart Contract: {scUID}" });

            if (scState.OwnerAddress.StartsWith("xRBX"))
            {
                var localReserve = ReserveAccount.GetReserveAccountSingle(scState.OwnerAddress);
                if (localReserve == null)
                    return JsonConvert.SerializeObject(new { Success = false, Message = $"Local account not found. You wallet is not the owner of this NFT." });
            }
            else
            {
                var localAccount = AccountData.GetSingleAccount(scState.OwnerAddress);

                if (localAccount == null)
                    return JsonConvert.SerializeObject(new { Success = false, Message = $"Local account not found. You wallet is not the owner of this NFT." });
            }

            var sc = SmartContractMain.SmartContractData.GetSmartContract(scUID);
            if (sc == null)
            {
                var transferTask = Task.Run(() => { SmartContractMain.SmartContractData.CreateSmartContract(scState.ContractData); });
                bool isCompletedSuccessfully = transferTask.Wait(TimeSpan.FromMilliseconds(Globals.NFTTimeout * 1000));

                if (!isCompletedSuccessfully)
                {
                    SCLogUtility.Log("Failed to decompile smart contract for transfer in time.", "SCV1Controller.AddNFTDataFromNetwork()");
                    return JsonConvert.SerializeObject(new { Success = false, Message = $"NFT was not created. Failed to decompile smart contract." });
                }

                return JsonConvert.SerializeObject(new { Success = true, Message = $"NFT Created." });
            }
            else
            {
                SCLogUtility.Log("SC was not null. Contract already exist.", "SCV1Controller.AddNFTDataFromNetwork()");
                return JsonConvert.SerializeObject(new { Success = false, Message = $"NFT Already Exist." });
            }
        }

        /// <summary>
        /// Creates ownership script RSRV compatible
        /// </summary>
        /// <param name="scUID"></param>
        /// <param name="password"></param>
        /// <returns></returns>
        [HttpGet("ProveOwnership/{scUID}/{password?}")]
        public async Task<string> ProveOwnership(string scUID, string? password = null)
        {
            var output = "";

            var scState = SmartContractStateTrei.GetSmartContractState(scUID);
            if (scState == null)
                return JsonConvert.SerializeObject(new { Success = false, Message = $"Could not located state information for Smart Contract: {scUID}" });

            if(scState.OwnerAddress.StartsWith("xRBX"))
            {
                var localReserve = ReserveAccount.GetReserveAccountSingle(scState.OwnerAddress);
                if(localReserve == null)
                    return JsonConvert.SerializeObject(new { Success = false, Message = $"Local account not found. You wallet is not the owner of this NFT." });

                if(string.IsNullOrEmpty(password))
                    return JsonConvert.SerializeObject(new { Success = false, Message = $"Password cannot be empty for Reserve Account." });

                bool sigGood = false;
                var completedOwnershipScript = "";
                var privateKey = ReserveAccount.GetPrivateKey(localReserve, password, true);

                while (!sigGood)
                {
                    var randomKey = RandomStringUtility.GetRandomStringOnlyLetters(8, false);
                    var timestamp = TimeUtil.GetTime();

                    var sigMessage = $"{randomKey}.{timestamp}";

                    var sigScript = SignatureService.CreateSignature(sigMessage, privateKey, localReserve.PublicKey);

                    completedOwnershipScript = $"{localReserve.Address}<>{sigMessage}<>{sigScript}<>{scUID}";

                    var sigVerifies = SignatureService.VerifySignature(localReserve.Address, sigMessage, sigScript);

                    if (sigVerifies)
                        sigGood = true;
                }

                return JsonConvert.SerializeObject(new { Success = true, Message = $"Ownership Script Created.", OwnershipScript = completedOwnershipScript });
            }
            else
            {
                var localAccount = AccountData.GetSingleAccount(scState.OwnerAddress);

                if (localAccount == null)
                    return JsonConvert.SerializeObject(new { Success = false, Message = $"Local account not found. You wallet is not the owner of this NFT." });

                bool sigGood = false;
                var completedOwnershipScript = "";

                while (!sigGood)
                {
                    var randomKey = RandomStringUtility.GetRandomStringOnlyLetters(8, false);
                    var timestamp = TimeUtil.GetTime();

                    var sigMessage = $"{randomKey}.{timestamp}";

                    var sigScript = SignatureService.CreateSignature(sigMessage, localAccount.GetPrivKey, localAccount.PublicKey);

                    completedOwnershipScript = $"{localAccount.Address}<>{sigMessage}<>{sigScript}<>{scUID}";

                    var sigVerifies = SignatureService.VerifySignature(localAccount.Address, sigMessage, sigScript);

                    if (sigVerifies)
                        sigGood = true;
                }

                return JsonConvert.SerializeObject(new { Success = true, Message = $"Ownership Script Created.", OwnershipScript = completedOwnershipScript });
            }

            
        }

        /// <summary>
        /// Verify Ownership Script
        /// </summary>
        /// <param name="ownershipScript"></param>
        /// <returns></returns>
        [HttpGet("VerifyOwnership/{**ownershipScript}")]
        public async Task<string> VerifyOwnership(string ownershipScript)
        {
            try
            {
                var osArray = ownershipScript.Split(new string[] { "<>" }, StringSplitOptions.None);

                if (osArray == null)
                    return JsonConvert.SerializeObject(new { Success = false, Message = $"Owner script was not formatted properly." });

                bool timeExpired = false;
                var address = osArray[0];
                var message = osArray[1].Replace("%2F", "/");
                var sigScript = osArray[2].Replace("%2F", "/");
                var scUID = osArray[3].Replace("%2F", "/");

                var messageArray = message.Split(".");

                var timeParse = int.TryParse(messageArray[1], out int timeCreated);

                if(!timeParse)
                    return JsonConvert.SerializeObject(new { Success = false, Message = $"Could not parse time." });

                var currentTime = TimeUtil.GetTime();

                if(currentTime > timeCreated + 3600)
                    timeExpired = true;

                if (address == null || message == null || sigScript == null)
                    return JsonConvert.SerializeObject(new { Success = false, Message = $"Owner script was not formatted properly.", IsTimeExpired = true });

                var isSigGood = SignatureService.VerifySignature(address, message, sigScript);

                if (isSigGood == false)
                    return JsonConvert.SerializeObject(new { Success = false, Message = $"Ownership --> NOT VERIFIED <--", IsTimeExpired = true });

                var scState = SmartContractStateTrei.GetSmartContractState(scUID);

                if(scState == null)
                    return JsonConvert.SerializeObject(new { Success = false, Message = $"SC State was not found.", IsTimeExpired = true });

                if(scState.OwnerAddress != address)
                    return JsonConvert.SerializeObject(new { Success = false, Message = $"State owner does not match supplied address." });

                return JsonConvert.SerializeObject(new { Success = true, Message = $"Ownership  --> VERIFIED <--", IsTimeExpired = timeExpired });
            }
            catch
            {
                return JsonConvert.SerializeObject(new { Success = false, Message = $"Ownership  --> NOT VERIFIED <--", IsTimeExpired = true });
            }
            
        }

        /// <summary>
        /// DO NOT USE - Creates a transaction that burns the desired NFT
        /// </summary>
        /// <param name="scUID"></param>
        /// <returns></returns>
        [HttpGet("BurnBypass/{scUID}")]
        public async Task<string> BurnBypass(string scUID)
        {
            var output = "";

            var tx = await SmartContractService.BurnSmartContractBypass(scUID);

            var txJson = JsonConvert.SerializeObject(tx);
            output = txJson;
               

            return output;
        }

        /// <summary>
        /// Creates a transaction to evolve an NFT
        /// </summary>
        /// <param name="id"></param>
        /// <param name="toAddress"></param>
        /// <returns></returns>
        [HttpGet("Evolve/{id}/{toAddress}")]
        public async Task<string> Evolve(string id, string toAddress)
        {
            var output = "";

            toAddress = toAddress.ToAddressNormalize();

            var tx = await SmartContractService.EvolveSmartContract(id, toAddress);

            if (tx == null)
            {
                output = "Failed to Evolve - TX";
            }
            else
            {
                var txJson = JsonConvert.SerializeObject(tx);
                output = txJson;
            }

            return output;
        }

        /// <summary>
        /// Creates a transaction to devolve an NFT
        /// </summary>
        /// <param name="id"></param>
        /// <param name="toAddress"></param>
        /// <returns></returns>
        [HttpGet("Devolve/{id}/{toAddress}")]
        public async Task<string> Devolve(string id, string toAddress)
        {
            string output;

            toAddress = toAddress.ToAddressNormalize();

            var tx = await SmartContractService.DevolveSmartContract(id, toAddress);

            if (tx == null)
            {
                output = "Failed to Devolve - TX";
            }
            else
            {
                var txJson = JsonConvert.SerializeObject(tx);
                output = txJson;
            }

            return output;
        }

        /// <summary>
        /// Creates a transaction to evolve an NFT to a specfic state
        /// </summary>
        /// <param name="id"></param>
        /// <param name="toAddress"></param>
        /// <param name="evolveState"></param>
        /// <returns></returns>
        [HttpGet("EvolveSpecific/{id}/{toAddress}/{evolveState}")]
        public async Task<string> EvolveSpecific(string id, string toAddress, int evolveState)
        {
            string output;

            toAddress = toAddress.ToAddressNormalize();

            var tx = await SmartContractService.ChangeEvolveStateSpecific(id, toAddress, evolveState);

            if (tx == null)
            {
                output = "Failed to Change State - TX";
            }
            else
            {
                var txJson = JsonConvert.SerializeObject(tx);
                output = txJson;
            }

            return output;
        }

        /// <summary>
        ///  Makes NFT public for DST shop
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        [HttpGet("ChangeNFTPublicState/{id}")]
        public async Task<string> ChangeNFTPublicState(string id)
        {
            var output = "";

            //Get SmartContractMain.IsPublic and set to True.
            var sc = SmartContractMain.SmartContractData.GetSmartContract(id);
            if(sc != null)
            {
                sc.IsPublic ^= true;
                SmartContractMain.SmartContractData.UpdateSmartContract(sc);
            }
            return output;
        }
        /// <summary>
        ///  Attempt to call media from beacon
        /// </summary>
        /// <param name="scUID"></param>
        /// <returns></returns>
        [HttpGet("CallMediaFromBeacon/{scUID}")]
        public async Task<string> CallMediaFromBeacon(string scUID)
        {
            var output = "";

            //Get SmartContractMain.IsPublic and set to True.
            var scState = SmartContractStateTrei.GetSmartContractState(scUID);

            if(scState == null)
                return JsonConvert.SerializeObject(new { Success = false, Message = "scState record was null. Please ensure blocks are synced to height." });

            var locators = scState.Locators;
            var md5List = scState.MD5List;

            var account = AccountData.GetSingleAccount(scState.OwnerAddress);

            if(account == null)
                return JsonConvert.SerializeObject(new { Success = false, Message = "You are not the registered owner of this NFT." });

            var sc = SmartContractMain.SmartContractData.GetSmartContract(scUID);

            if (sc == null)
                return JsonConvert.SerializeObject(new { Success = false, Message = "Smart contract was not found locally." });

            if(locators == null || md5List== null)
                return JsonConvert.SerializeObject(new { Success = false, Message = "Locators and/or MD5 List cannot be null." });

            var assetList = await MD5Utility.GetAssetList(md5List);
            var aqResult = AssetQueue.CreateAssetQueueItem(scUID, account.Address, locators, md5List, assetList, AssetQueue.TransferType.Download, true);
            SCLogUtility.Log($"NFT Transfer - Asset Queue items created.", "SCV1Controller.CallMediaFromBeacon()");

            output = JsonConvert.SerializeObject(new { Success = true, Message = "Call to beacon process has started. Please check your NFT or logs for more details." });
            
            return output;
        }

        /// <summary>
        ///  Creates thumbnails for known image types
        /// </summary>
        /// <param name="scUID"></param>
        /// <returns></returns>
        [HttpGet("CreateThumbnails/{scUID}")]
        public async Task<string> CreateThumbnails(string scUID)
        {
            var output = "";

            //Get SmartContractMain.IsPublic and set to True.
            var sc = SmartContractMain.SmartContractData.GetSmartContract(scUID);
            if (sc != null)
            {
                _ = NFTAssetFileUtility.GenerateThumbnails(scUID);
                output = JsonConvert.SerializeObject(new { Success = true, Message = "Thumbnail generation process started." });
            }
            return output;
        }

        /// <summary>
        /// Returns the NFTs asset location.
        /// </summary>
        /// <param name="scUID"></param>
        /// <param name="fileName"></param>
        /// <returns></returns>
        [HttpGet("GetNFTAssetLocation/{scUID}/{**fileName}")]
        public async Task<string> GetNFTAssetLocation(string scUID, string fileName)
        {
            var output = "";

            try
            {
                output = NFTAssetFileUtility.NFTAssetPath(fileName, scUID);
            }
            catch { output = "Error"; }

            return output;

        }

        /// <summary>
        /// returns the smart contract data generated from on chain information
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        [HttpGet("GetSmartContractData/{id}")]
        public async Task<string> GetSmartContractData(string id)
        {
            var output = "";
            bool exit = false;
            while (!exit)
            {
                if(!Globals.TreisUpdating)
                {
                    var scStateTrei = SmartContractStateTrei.GetSmartContractState(id);
                    if (scStateTrei != null)
                    {
                        var scMain = SmartContractMain.GenerateSmartContractInMemory(scStateTrei.ContractData);
                        output = JsonConvert.SerializeObject(new { SmartContractMain = scMain, CurrentOwner = scStateTrei.OwnerAddress });
                    }
                    exit = true;
                }
                else
                {
                    await Task.Delay(50);
                }
                
            }

            return output;
        }

        /// <summary>
        /// Test dynamic NFT 
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        [HttpGet("TestDynamicNFT/{id}")]
        public async Task<string> TestDynamicNFT(string id)
        {
            var output = "";

            var sc = SmartContractMain.SmartContractData.GetSmartContract(id);

            var result = await SmartContractReaderService.ReadSmartContract(sc);

            var scMain = result.Item2;
            var scCode = result.Item1;

            var bytes = Encoding.Unicode.GetBytes(scCode);
            var scBase64 = bytes.ToCompress().ToBase64();

            SmartContractMain.SmartContractData.CreateSmartContract(scBase64);

            return output;
        }

        /// <summary>
        /// Test if smart contract is present.
        /// </summary>
        /// <param name="id"></param>
        /// <param name="toAddress"></param>
        /// <returns></returns>
        [HttpGet("TestRemove/{id}/{toAddress}")]
        public async Task<string> TestRemove(string id, string toAddress)
        {
            var output = "";

            toAddress = toAddress.ToAddressNormalize();

            var sc = SmartContractMain.SmartContractData.GetSmartContract(id);
            if (sc != null)
            {
                if (sc.IsPublished == true)
                {
                    var result = await SmartContractReaderService.ReadSmartContract(sc);

                    var scText = result.Item1;
                    var bytes = Encoding.Unicode.GetBytes(scText);
                    var compressBase64 = SmartContractUtility.Compress(bytes).ToBase64();

                    SmartContractMain.SmartContractData.CreateSmartContract(compressBase64);

                }
                else
                {
                    output = "Smart Contract Found, but has not been minted.";
                }
            }
            else
            {
                output = "No Smart Contract Found Locally.";
            }

            return output;
        }
    }
}
