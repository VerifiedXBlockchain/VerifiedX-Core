using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ReserveBlockCore.Api.Rest.Infrastructure;
using ReserveBlockCore.Api.Rest.Models;
using ReserveBlockCore.Api.Rest.Models.Requests;
using ReserveBlockCore.Data;
using ReserveBlockCore.Models;
using ReserveBlockCore.Models.SmartContracts;
using ReserveBlockCore.P2P;
using ReserveBlockCore.Services;
using ReserveBlockCore.Utilities;
using System.Collections.Concurrent;
using System.Text;

namespace ReserveBlockCore.Api.Rest.Controllers
{
    [Route("api/rest/smart-contracts")]
    public class SmartContractsController : RestBaseController
    {
        /// <summary>
        /// List all smart contracts (paginated, searchable)
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetAll([FromQuery] PaginationParams paging, [FromQuery] string? search = null)
        {
            var scs = new List<SmartContractMain>();

            if (!string.IsNullOrEmpty(search) && search != "~")
            {
                var result = await NFTSearchUtility.Search(search);
                if (result != null)
                    scs = result;
            }
            else
            {
                scs = SmartContractMain.SmartContractData.GetSCs()
                    .FindAll()
                    .ToList();
            }

            var scStateTrei = SmartContractStateTrei.GetSCST();
            var accounts = AccountData.GetAccounts().FindAll().ToList();
            var scStateMainBag = new ConcurrentBag<SmartContractStateTrei>();

            foreach (var sc in scs)
            {
                var scState = scStateTrei.FindOne(x => x.SmartContractUID == sc.SmartContractUID);
                if (scState != null)
                {
                    var exist = accounts.Exists(x => x.Address == scState.OwnerAddress || x.Address == scState.NextOwner);
                    var rExist = ReserveAccount.GetReserveAccountSingle(scState.OwnerAddress) != null;
                    if (!rExist && scState.NextOwner != null)
                        rExist = ReserveAccount.GetReserveAccountSingle(scState.NextOwner) != null;
                    if (rExist)
                        rExist = scState.NextOwner != null ? ReserveAccount.GetReserveAccountSingle(scState.NextOwner) != null : true;

                    if (exist || rExist)
                        scStateMainBag.Add(scState);
                }
            }

            var scStateMainList = scStateMainBag.ToList();
            var totalCount = scStateMainList.Count;

            var pagedStates = scStateMainList
                .Skip((paging.Page - 1) * paging.PageSize)
                .Take(paging.PageSize)
                .ToList();

            var scMainList = new List<object>();
            foreach (var scState in pagedStates)
            {
                var scMain = SmartContractMain.GenerateSmartContractInMemory(scState.ContractData);
                var scMainRec = scs.Where(x => x.SmartContractUID == scMain.SmartContractUID).FirstOrDefault();
                scMain.Id = scMainRec != null ? scMainRec.Id : 0;
                scMainList.Add(scMain);
            }

            return OkPaged(scMainList, paging.Page, paging.PageSize, totalCount);
        }

        /// <summary>
        /// List minted smart contracts with evolving features
        /// </summary>
        [HttpGet("minted")]
        public async Task<IActionResult> GetMinted([FromQuery] PaginationParams paging, [FromQuery] string? search = null)
        {
            var scs = new List<SmartContractMain>();

            if (!string.IsNullOrEmpty(search) && search != "~")
            {
                var result = await NFTSearchUtility.Search(search, true);
                if (result != null)
                    scs = result;
            }
            else
            {
                scs = SmartContractMain.SmartContractData.GetSCs()
                    .Find(x => x.IsMinter == true)
                    .Where(x => x.Features != null && x.Features.Any(y => y.FeatureName == FeatureName.Evolving))
                    .ToList();
            }

            var resultCollection = new ConcurrentBag<SmartContractMain>();

            foreach (var sc in scs)
            {
                var scStateTrei = SmartContractStateTrei.GetSmartContractState(sc.SmartContractUID);
                if (scStateTrei != null)
                {
                    var scMain = SmartContractMain.GenerateSmartContractInMemory(scStateTrei.ContractData);
                    if (scMain.Features != null)
                    {
                        scMain.Id = sc.Id;
                        var evoFeatures = scMain.Features
                            .Where(x => x.FeatureName == FeatureName.Evolving)
                            .Select(x => x.FeatureFeatures)
                            .FirstOrDefault();
                        var isDynamic = false;
                        if (evoFeatures != null)
                        {
                            var evoFeatureList = (List<EvolvingFeature>)evoFeatures;
                            foreach (var feature in evoFeatureList)
                            {
                                if (((EvolvingFeature)feature).IsDynamic == true)
                                    isDynamic = true;
                            }
                        }

                        if (!isDynamic)
                            resultCollection.Add(scMain);
                    }
                }
            }

            var scMainList = resultCollection.ToList();
            var totalCount = scMainList.Count;

            var paged = scMainList
                .Skip((paging.Page - 1) * paging.PageSize)
                .Take(paging.PageSize)
                .OrderByDescending(x => x.Id)
                .ToList();

            return OkPaged(paged, paging.Page, paging.PageSize, totalCount);
        }

        /// <summary>
        /// Get smart contract details
        /// </summary>
        [HttpGet("{scUID}")]
        public async Task<IActionResult> GetSingle(string scUID)
        {
            var sc = SmartContractMain.SmartContractData.GetSmartContract(scUID);
            if (sc == null)
                return Fail("NOT_FOUND", $"Smart contract not found: {scUID}", 404);

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
                        currentState = currentStage.EvolutionState;
                }

                var scMainFeatures = scMain.Features?.Where(x => x.FeatureName == FeatureName.Evolving).FirstOrDefault();
                if (scMainFeatures != null)
                {
                    var scMainFeaturesList = (List<EvolvingFeature>)scMainFeatures.FeatureFeatures;
                    var evoStage = scMainFeaturesList.Where(x => x.EvolutionState == currentState).FirstOrDefault();
                    if (evoStage != null)
                    {
                        evoStage.IsCurrentState = true;
                        scMainFeaturesList.Where(x => x.EvolutionState != currentState).ToList()
                            .ForEach(x => { x.IsCurrentState = false; });
                    }
                }
            }

            scMainUpdated.Id = sc.Id;
            var currentOwner = "";
            var scState = SmartContractStateTrei.GetSmartContractState(scMain.SmartContractUID);
            if (scState != null)
                currentOwner = scState.OwnerAddress;

            return Ok(new { SmartContract = scMain, SmartContractCode = scCode, CurrentOwner = currentOwner });
        }

        /// <summary>
        /// Get smart contract state
        /// </summary>
        [HttpGet("{scUID}/state")]
        public IActionResult GetState(string scUID)
        {
            var scState = SmartContractStateTrei.GetSmartContractState(scUID);
            if (scState == null)
                return Fail("NOT_FOUND", $"Smart contract state not found: {scUID}", 404);

            return Ok(scState);
        }

        /// <summary>
        /// Get on-chain smart contract data
        /// </summary>
        [HttpGet("{scUID}/data")]
        public async Task<IActionResult> GetData(string scUID)
        {
            while (Globals.TreisUpdating)
                await Task.Delay(50);

            var scStateTrei = SmartContractStateTrei.GetSmartContractState(scUID);
            if (scStateTrei == null)
                return Fail("NOT_FOUND", $"Smart contract state not found: {scUID}", 404);

            var scMain = SmartContractMain.GenerateSmartContractInMemory(scStateTrei.ContractData);

            return Ok(new { SmartContractMain = scMain, CurrentOwner = scStateTrei.OwnerAddress });
        }

        /// <summary>
        /// Create a smart contract
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> Create([FromBody] object jsonData)
        {
            var scMain = JsonConvert.DeserializeObject<SmartContractMain>(jsonData.ToString()!);
            if (scMain == null)
                return Fail("INVALID_DATA", "Could not deserialize smart contract data.");

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
                            return Fail("VALIDATION_ERROR", "Flat rates may no longer be used.");
                        if (royaltyFeatures.RoyaltyAmount >= 1.0M)
                            return Fail("VALIDATION_ERROR", "Royalty cannot be over 1. Must be .99 or less.");
                    }
                }
            }

            scMain.SCVersion = Globals.SCVersion;

            var result = await SmartContractWriterService.WriteSmartContract(scMain);
            var scCode = result.Item1;
            var scMainResult = result.Item2;

            var txData = "";
            if (scCode != null)
            {
                var bytes = Encoding.Unicode.GetBytes(scCode);
                var scBase64 = bytes.ToCompress().ToBase64();
                var function = result.Item3 ? "TokenDeploy()" : "Mint()";
                var newSCInfo = new[]
                {
                    new { Function = function, ContractUID = scMain.SmartContractUID, Data = scBase64 }
                };
                txData = JsonConvert.SerializeObject(newSCInfo);
            }

            var nTx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(),
                FromAddress = scMainResult.MinterAddress,
                ToAddress = scMainResult.MinterAddress,
                Amount = 0.0M,
                Fee = 0,
                Nonce = AccountStateTrei.GetNextNonce(scMain.MinterAddress),
                TransactionType = !result.Item3 ? TransactionType.NFT_MINT : TransactionType.FTKN_MINT,
                Data = txData
            };

            nTx.Fee = FeeCalcService.CalculateTXFee(nTx);
            nTx.Build();

            var checkSize = await TransactionValidatorService.VerifyTXSize(nTx);
            if (!checkSize)
                return Fail("VALIDATION_ERROR", "Image is too large for token image. Must be 25kb or less.");

            SmartContractMain.SmartContractData.SaveSmartContract(scMainResult, scCode);

            return Created(new { SmartContract = scMainResult, SmartContractCode = scCode, Transaction = nTx });
        }

        /// <summary>
        /// Mint/publish a smart contract
        /// </summary>
        [HttpPost("{scUID}/mint")]
        public async Task<IActionResult> Mint(string scUID)
        {
            var scMain = SmartContractMain.SmartContractData.GetSmartContract(scUID);
            if (scMain == null)
                return Fail("NOT_FOUND", $"Smart contract not found: {scUID}", 404);

            if (scMain.IsPublished == true)
                return Fail("ALREADY_PUBLISHED", "This NFT has already been published.", 409);

            var scTx = await SmartContractService.MintSmartContractTx(scMain);
            if (scTx == null)
                return Fail("MINT_FAILED", $"Failed to publish smart contract: {scMain.Name}. Id: {scUID}");

            return Ok("Smart contract has been published to mempool.");
        }

        /// <summary>
        /// Transfer an NFT
        /// </summary>
        [HttpPost("{scUID}/transfer")]
        public async Task<IActionResult> Transfer(string scUID, [FromBody] TransferNftRequest request)
        {
            var sc = SmartContractMain.SmartContractData.GetSmartContract(scUID);
            if (sc == null)
                return Fail("NOT_FOUND", "No smart contract found locally.", 404);

            if (sc.IsPublished != true)
                return Fail("NOT_PUBLISHED", "Smart contract found, but has not been minted.", 409);

            if (!Globals.Beacons.Any())
                return Fail("NO_BEACONS", "You do not have any beacons stored.");

            if (!Globals.Beacon.Values.Where(x => x.IsConnected).Any())
            {
                var beaconConnectionResult = await BeaconUtility.EstablishBeaconConnection(true, false);
                if (!beaconConnectionResult)
                    return Fail("BEACON_FAILED", "You failed to connect to any beacons.");
            }

            var connectedBeacon = Globals.Beacon.Values.Where(x => x.IsConnected).FirstOrDefault();
            if (connectedBeacon == null)
                return Fail("BEACON_LOST", "You have lost connection to beacons. Please attempt to resend.");

            var toAddress = request.ToAddress.Replace(" ", "").ToAddressNormalize();
            var localAddress = AccountData.GetSingleAccount(toAddress);

            var assets = await NFTAssetFileUtility.GetAssetListFromSmartContract(sc);
            var md5List = await MD5Utility.GetMD5FromSmartContract(sc);

            bool uploadResult = false;
            if (localAddress == null)
            {
                uploadResult = await P2PClient.BeaconUploadRequest(connectedBeacon, assets, sc.SmartContractUID, toAddress, md5List)
                    .WaitAsync(new TimeSpan(0, 0, 10));
            }
            else
            {
                uploadResult = true;
            }

            if (!uploadResult)
                return Fail("UPLOAD_FAILED", "Beacon upload failed.");

            var aqResult = AssetQueue.CreateAssetQueueItem(sc.SmartContractUID, toAddress,
                connectedBeacon.Beacons.BeaconLocator, md5List, assets, AssetQueue.TransferType.Upload);

            if (!aqResult)
                return Fail("QUEUE_FAILED", "Failed to add upload to Asset Queue.");

            _ = Task.Run(() => SmartContractService.TransferSmartContract(sc, toAddress, connectedBeacon, md5List, request.BackupURL ?? ""));

            return Ok("NFT Transfer has been started.");
        }

        /// <summary>
        /// Burn an NFT
        /// </summary>
        [HttpPost("{scUID}/burn")]
        public async Task<IActionResult> Burn(string scUID)
        {
            var sc = SmartContractMain.SmartContractData.GetSmartContract(scUID);
            if (sc == null)
                return Fail("NOT_FOUND", $"Smart contract not found: {scUID}", 404);

            if (sc.IsPublished != true)
                return Fail("NOT_PUBLISHED", "Smart contract found, but has not been minted.", 409);

            var tx = await SmartContractService.BurnSmartContract(sc);
            if (tx == null)
                return Fail("BURN_FAILED", "Failed to burn smart contract.");

            return Ok(tx);
        }

        /// <summary>
        /// Evolve an NFT
        /// </summary>
        [HttpPost("{scUID}/evolve")]
        public async Task<IActionResult> Evolve(string scUID, [FromBody] EvolveRequest request)
        {
            var toAddress = request.ToAddress.ToAddressNormalize();
            var tx = await SmartContractService.EvolveSmartContract(scUID, toAddress);

            if (tx == null)
                return Fail("EVOLVE_FAILED", "Failed to evolve smart contract.");

            return Ok(tx);
        }

        /// <summary>
        /// Devolve an NFT
        /// </summary>
        [HttpPost("{scUID}/devolve")]
        public async Task<IActionResult> Devolve(string scUID, [FromBody] DevolveRequest request)
        {
            var toAddress = request.ToAddress.ToAddressNormalize();
            var tx = await SmartContractService.DevolveSmartContract(scUID, toAddress);

            if (tx == null)
                return Fail("DEVOLVE_FAILED", "Failed to devolve smart contract.");

            return Ok(tx);
        }

        /// <summary>
        /// Start a sale for an NFT
        /// </summary>
        [HttpPost("{scUID}/sale")]
        public async Task<IActionResult> StartSale(string scUID, [FromBody] TransferSaleRequest request)
        {
            var sc = SmartContractMain.SmartContractData.GetSmartContract(scUID);
            if (sc == null)
                return Fail("NOT_FOUND", "NFT could not be found locally.", 404);

            if (sc.IsPublished != true)
                return Fail("NOT_PUBLISHED", "NFT is not published.", 409);

            var purchaseKey = RandomStringUtility.GetRandomStringOnlyLetters(10, true);
            var scState = SmartContractStateTrei.GetSmartContractState(scUID);
            if (scState == null)
                return Fail("STATE_NOT_FOUND", $"Could not locate state information for smart contract: {scUID}", 404);

            var localAccount = AccountData.GetSingleAccount(scState.OwnerAddress);
            if (localAccount == null)
                return Fail("NOT_OWNER", "Local account not found. Your wallet is not the owner of this NFT.", 403);

            if (!Globals.Beacons.Any())
                return Fail("NO_BEACONS", "You do not have any beacons stored.");

            if (!Globals.Beacon.Values.Where(x => x.IsConnected).Any())
            {
                var beaconConnectionResult = await BeaconUtility.EstablishBeaconConnection(true, false);
                if (!beaconConnectionResult)
                    return Fail("BEACON_FAILED", "You failed to connect to any beacons.");
            }

            var connectedBeacon = Globals.Beacon.Values.Where(x => x.IsConnected).FirstOrDefault();
            if (connectedBeacon == null)
                return Fail("BEACON_LOST", "You have lost connection to beacons. Please attempt to resend.");

            var toAddress = request.ToAddress.Replace(" ", "").ToAddressNormalize();
            var assets = await NFTAssetFileUtility.GetAssetListFromSmartContract(sc);
            var md5List = await MD5Utility.GetMD5FromSmartContract(sc);

            _ = Task.Run(() => SmartContractService.StartTransferSaleSmartContractTX(sc,
                scUID, toAddress, request.SaleAmount, purchaseKey, connectedBeacon, md5List, request.BackupURL ?? ""));

            return Ok("NFT Transfer has been started.");
        }

        /// <summary>
        /// Complete an NFT sale
        /// </summary>
        [HttpPost("{scUID}/sale/complete")]
        public async Task<IActionResult> CompleteSale(string scUID, [FromBody] CompleteSaleRequest request)
        {
            var scStateTrei = SmartContractStateTrei.GetSmartContractState(scUID);
            if (scStateTrei == null)
                return Fail("NOT_FOUND", "Smart contract was not found.", 404);

            var nextOwner = scStateTrei.NextOwner;
            var purchaseAmount = scStateTrei.PurchaseAmount;

            if (nextOwner == null || purchaseAmount == null)
                return Fail("INVALID_STATE", $"Smart contract data missing or purchase already completed. Next Owner: {nextOwner} | Purchase Amount: {purchaseAmount}");

            var localAccount = AccountData.GetSingleAccount(nextOwner);
            if (localAccount == null)
                return Fail("NOT_FOUND", $"A local account with next owner address was not found. Next Owner: {nextOwner}", 404);

            if (localAccount.Balance <= purchaseAmount.Value)
                return Fail("INSUFFICIENT_FUNDS", $"Not enough funds to purchase NFT. Purchase Amount: {purchaseAmount} | Current Balance: {localAccount.Balance}");

            var result = await SmartContractService.CompleteTransferSaleSmartContractTX(scUID, scStateTrei.OwnerAddress, purchaseAmount.Value, request.KeySign);

            if (result.Item1 == null)
                return Fail("SALE_FAILED", result.Item2);

            return Ok(result.Item2);
        }

        /// <summary>
        /// Cancel an NFT sale
        /// </summary>
        [HttpDelete("{scUID}/sale")]
        public IActionResult CancelSale(string scUID)
        {
            var scStateTrei = SmartContractStateTrei.GetSmartContractState(scUID);
            if (scStateTrei == null)
                return Fail("NOT_FOUND", "Smart contract was not found.", 404);

            var currentOwner = scStateTrei.OwnerAddress;
            var localAccount = AccountData.GetSingleAccount(currentOwner);
            if (localAccount == null)
                return Fail("NOT_OWNER", $"A local account with owner address was not found. Owner: {currentOwner}", 403);

            if (!scStateTrei.IsLocked)
                return Fail("NOT_LOCKED", "This NFT is not locked for a sale.", 409);

            if (string.IsNullOrEmpty(scStateTrei.NextOwner))
                return Fail("NO_PENDING_SALE", "There is no next owner on this NFT. Nothing to cancel.", 409);

            return Ok("Sale cancellation processed.");
        }

        /// <summary>
        /// Prove ownership of an NFT
        /// </summary>
        [HttpGet("{scUID}/ownership")]
        public IActionResult ProveOwnership(string scUID)
        {
            var scState = SmartContractStateTrei.GetSmartContractState(scUID);
            if (scState == null)
                return Fail("NOT_FOUND", $"Could not locate state information for smart contract: {scUID}", 404);

            var localAccount = AccountData.GetSingleAccount(scState.OwnerAddress);
            if (localAccount == null)
                return Fail("NOT_OWNER", "Local account not found. Your wallet is not the owner of this NFT.", 403);

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

            return Ok(new { OwnershipScript = completedOwnershipScript });
        }

        /// <summary>
        /// Verify an ownership script
        /// </summary>
        [HttpPost("{scUID}/ownership/verify")]
        public IActionResult VerifyOwnership(string scUID, [FromBody] VerifyOwnershipRequest request)
        {
            var osArray = request.OwnershipScript.Split(new string[] { "<>" }, StringSplitOptions.None);
            if (osArray == null || osArray.Length < 4)
                return Fail("INVALID_SCRIPT", "Ownership script was not formatted properly.");

            var address = osArray[0];
            var message = osArray[1];
            var sigScript = osArray[2];
            var scriptScUID = osArray[3];

            if (scriptScUID != scUID)
                return Fail("SCUID_MISMATCH", "The scUID in the ownership script does not match the route scUID.");

            var messageArray = message.Split(".");
            if (messageArray.Length < 2)
                return Fail("INVALID_SCRIPT", "Message format in ownership script is invalid.");

            var timeParse = int.TryParse(messageArray[1], out int timeCreated);
            if (!timeParse)
                return Fail("INVALID_SCRIPT", "Could not parse time from ownership script.");

            var currentTime = TimeUtil.GetTime();
            var timeExpired = currentTime > timeCreated + 3600;

            var isSigGood = SignatureService.VerifySignature(address, message, sigScript);
            if (!isSigGood)
                return Fail("VERIFICATION_FAILED", "Ownership NOT VERIFIED.");

            var scState = SmartContractStateTrei.GetSmartContractState(scUID);
            if (scState == null)
                return Fail("NOT_FOUND", "Smart contract state was not found.", 404);

            if (scState.OwnerAddress != address)
                return Fail("OWNER_MISMATCH", "State owner does not match supplied address.");

            return Ok(new { Verified = true, IsTimeExpired = timeExpired });
        }
    }
}
