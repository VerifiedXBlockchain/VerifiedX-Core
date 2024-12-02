using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Newtonsoft.Json;
using ReserveBlockCore.Bitcoin.ElectrumX;
using ReserveBlockCore.Data;
using ReserveBlockCore.Extensions;
using ReserveBlockCore.Models;
using ReserveBlockCore.P2P;
using ReserveBlockCore.Services;
using ReserveBlockCore.Utilities;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Reflection.Metadata;
using System.Text;
using System.Threading.Tasks.Dataflow;


namespace ReserveBlockCore.Nodes
{
    public class ValidatorNode : IHostedService, IDisposable
    {
        public static IHubContext<P2PValidatorServer> HubContext;
        private readonly IHubContext<P2PValidatorServer> _hubContext;
        private readonly IHostApplicationLifetime _appLifetime;
        private static SemaphoreSlim ConsensusLock = new SemaphoreSlim(1, 1);
        private static bool ActiveValidatorRequestDone = false;
        private static bool AlertValidatorsOfStatusDone = false;
        static SemaphoreSlim NotifyExplorerLock = new SemaphoreSlim(1, 1);
        private static ConcurrentBag<(string, long)> ValidatorApprovalBag = new ConcurrentBag<(string, long)>();
        const int PROOF_COLLECTION_TIME = 5000; // 5 seconds
        const int APPROVAL_WINDOW = 12000;      // 12 seconds
        const int BLOCK_REQUEST_WINDOW = 12000;  // 3.5 seconds

        public ValidatorNode(IHubContext<P2PValidatorServer> hubContext, IHostApplicationLifetime appLifetime)
        {
            _hubContext = hubContext;
            HubContext = hubContext;
            _appLifetime = appLifetime;
        }

        public async Task StartAsync(CancellationToken stoppingToken)
        {
            //Request latest val list - RequestValidatorList()
            _ = ActiveValidatorRequest();

            //Alert vals you are online - OnlineMethod()
            _ = AlertValidatorsOfStatus();

            //Checks for active vals every 15 mins
            _ = ValidatorHeartbeat();

            //Notify Explorer for visibility.
            _ = NotifyExplorer();

            //Start consensus
            _ = StartConsensus();

            //return Task.CompletedTask;
        }
        public static async Task ActiveValidatorRequest()
        {
            bool waitForStartup = true;
            while (waitForStartup)
            {
                var delay = Task.Delay(new TimeSpan(0, 0, 5));
                if ((Globals.StopAllTimers || !Globals.IsChainSynced))
                {
                    await delay;
                    continue;
                }
                waitForStartup = false;
            }

            await P2PValidatorClient.RequestActiveValidators();

            ActiveValidatorRequestDone = true;
        }

        public static async Task AlertValidatorsOfStatus(bool comingOnline = true)
        {
            if (comingOnline)
            {
                while (true && !string.IsNullOrEmpty(Globals.ValidatorAddress))
                {
                    var delay = Task.Delay(new TimeSpan(0, 0, 5));
                    if ((Globals.StopAllTimers && !Globals.IsChainSynced) || Globals.Nodes.Count == 0)
                    {
                        await delay;
                        continue;
                    }

                    var valList = Globals.NetworkValidators.Values.ToList();

                    if(!valList.Any())
                    {
                        await delay;
                        continue;
                    }

                    var account = AccountData.GetLocalValidator();
                    var validators = Validators.Validator.GetAll();
                    var validator = validators.FindOne(x => x.Address == account.Address);
                    if (validator == null)
                        return;

                    var time = TimeUtil.GetTime().ToString();
                    var signature = SignatureService.ValidatorSignature(validator.Address + ":" + time + ":" + account.PublicKey);

                    var networkVal = new NetworkValidator { 
                        Address = validator.Address,
                        IPAddress = "0.0.0.0",
                        CheckFailCount = 0,
                        PublicKey = account.PublicKey,
                        Signature = signature,
                        UniqueName = validator.UniqueName,
                        SignatureMessage = validator.Address + ":" + time + ":" + account.PublicKey
                    };

                    var postData = JsonConvert.SerializeObject(networkVal);
                    var httpContent = new StringContent(postData, Encoding.UTF8, "application/json");

                    var coreCount = Environment.ProcessorCount;

                    if(coreCount >= 4 || Globals.RunUnsafeCode)
                    {
                        valList.ParallelLoop(async peer =>
                        {
                            using (var client = Globals.HttpClientFactory.CreateClient())
                            {
                                try
                                {
                                    var uri = $"http://{peer.IPAddress.Replace("::ffff:", "")}:{Globals.ValPort}/valapi/validator/status";
                                    await client.PostAsync(uri, httpContent).WaitAsync(new TimeSpan(0, 0, 4));
                                    await Task.Delay(100);
                                }
                                catch (Exception ex) { }

                            }
                        });
                    }
                    else
                    {
                        foreach(var peer in valList)
                        {
                            using (var client = Globals.HttpClientFactory.CreateClient())
                            {
                                try
                                {
                                    var uri = $"http://{peer.IPAddress.Replace("::ffff:", "")}:{Globals.ValPort}/valapi/validator/status";
                                    await client.PostAsync(uri, httpContent).WaitAsync(new TimeSpan(0, 0, 4));
                                    await Task.Delay(100);
                                }
                                catch (Exception ex) { }
                            }
                        }
                    }
                    

                    comingOnline = false;
                    AlertValidatorsOfStatusDone = true;
                    await Task.Delay(new TimeSpan(0, 1, 30));
                }
            }
            else
            {

            }
        }

        public static async Task ValidatorHeartbeat()
        {
            while (true && !string.IsNullOrEmpty(Globals.ValidatorAddress))
            {
                var delay = Task.Delay(new TimeSpan(0, 0, 5));
                if ((Globals.StopAllTimers && !Globals.IsChainSynced) || Globals.Nodes.Count == 0)
                {
                    await delay;
                    continue;
                }

                if (!AlertValidatorsOfStatusDone || !ActiveValidatorRequestDone)
                {
                    await delay;
                    continue;
                }

                var peerList = Globals.NetworkValidators.Values.ToList();

                if (!peerList.Any()) 
                {
                    await delay;
                    continue;
                }

                ConcurrentBag<string> BadValidatorList = new ConcurrentBag<string>();

                var peerDB = Peers.GetAll();

                foreach (var val in peerList) {
                    var valBalance = AccountStateTrei.GetAccountBalance(val.Address);

                    if(valBalance < Globals.ValidatorRequiredRBX)
                        BadValidatorList.Add(val.IPAddress);
                }

                var coreCount = Environment.ProcessorCount;
                if (coreCount >= 4 || Globals.RunUnsafeCode) 
                {
                    var tasks = peerList.Select(async peer =>
                    {
                        try
                        {
                            if (!BadValidatorList.Contains(peer.IPAddress))
                            {
                                using (var client = Globals.HttpClientFactory.CreateClient())
                                {
                                    var uri = $"http://{peer.IPAddress.Replace("::ffff:", "")}:{Globals.ValPort}/valapi/validator/heartbeat/{Globals.ValidatorAddress}";

                                    var sw = Stopwatch.StartNew();
                                    var response = await client.GetAsync(uri).WaitAsync(new TimeSpan(0, 0, 4));
                                    sw.Stop();
                                    Globals.NetworkValidators.TryGetValue(peer.Address, out var networkValidator);
                                    await Task.Delay(75);

                                    if (networkValidator != null)
                                    {
                                        networkValidator.Latency = sw.ElapsedMilliseconds;
                                        Globals.NetworkValidators[networkValidator.Address] = networkValidator;
                                    }

                                    if (response != null)
                                    {
                                        if (response.StatusCode == HttpStatusCode.Accepted)
                                        {

                                        }

                                        if (!response.IsSuccessStatusCode)
                                            BadValidatorList.Add(peer.IPAddress);

                                        if (response.IsSuccessStatusCode)
                                        {

                                            if (networkValidator != null)
                                            {
                                                networkValidator.CheckFailCount = 0;
                                                Globals.NetworkValidators[networkValidator.Address] = networkValidator;
                                            }
                                        }
                                        else
                                        {
                                            BadValidatorList.Add(peer.IPAddress);
                                        }

                                    }
                                    else
                                    {
                                        BadValidatorList.Add(peer.IPAddress);
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            BadValidatorList.Add(peer.IPAddress);
                        }
                    }).ToList();

                    // Wait for all tasks to complete
                    await Task.WhenAll(tasks);
                }
                else
                {
                    foreach (var peer in peerList) 
                    {
                        try
                        {
                            if (!BadValidatorList.Contains(peer.IPAddress))
                            {
                                using (var client = Globals.HttpClientFactory.CreateClient())
                                {
                                    var uri = $"http://{peer.IPAddress.Replace("::ffff:", "")}:{Globals.ValPort}/valapi/validator/heartbeat/{Globals.ValidatorAddress}";

                                    var sw = Stopwatch.StartNew();
                                    var response = await client.GetAsync(uri).WaitAsync(new TimeSpan(0, 0, 4));
                                    sw.Stop();
                                    Globals.NetworkValidators.TryGetValue(peer.Address, out var networkValidator);
                                    await Task.Delay(75);

                                    if (networkValidator != null)
                                    {
                                        networkValidator.Latency = sw.ElapsedMilliseconds;
                                        Globals.NetworkValidators[networkValidator.Address] = networkValidator;
                                    }

                                    if (response != null)
                                    {
                                        if (response.StatusCode == HttpStatusCode.Accepted)
                                        {

                                        }

                                        if (!response.IsSuccessStatusCode)
                                            BadValidatorList.Add(peer.IPAddress);

                                        if (response.IsSuccessStatusCode)
                                        {

                                            if (networkValidator != null)
                                            {
                                                networkValidator.CheckFailCount = 0;
                                                Globals.NetworkValidators[networkValidator.Address] = networkValidator;
                                            }
                                        }
                                        else
                                        {
                                            BadValidatorList.Add(peer.IPAddress);
                                        }

                                    }
                                    else
                                    {
                                        BadValidatorList.Add(peer.IPAddress);
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            BadValidatorList.Add(peer.IPAddress);
                        }
                    }
                }

                foreach (var val in BadValidatorList)
                {
                    var networkVal = Globals.NetworkValidators.Values.Where(x => x.IPAddress == val).FirstOrDefault();
                    if(networkVal != null)
                    {
                        networkVal.CheckFailCount++;
                        Globals.NetworkValidators[networkVal.Address] = networkVal;

                        if (networkVal.CheckFailCount > 2)
                        {
                            var validator = peerDB.FindOne(x => x.PeerIP == val);
                            if (validator != null)
                            {
                                validator.IsValidator = false;
                                peerDB.UpdateSafe(validator);
                            }

                            Globals.NetworkValidators.TryRemove(networkVal.Address, out var _);
                            ValidatorLogUtility.Log($"Validator removed from pool: {networkVal.Address}", "ValidatorNode.ValidatorHeartbeat()");
                            ConsoleWriterService.OutputVal($"Validator removed from pool: {networkVal.Address}");
                        }
                    }
                }

                await Task.Delay(new TimeSpan(0, 1, 0));
            }
        }

        public static async Task StartConsensus()
        {
            var EpochTime = Globals.IsTestNet ? 1731454926600L : 1674172800000L;
            var BeginBlock = Globals.IsTestNet ? Globals.V4Height : Globals.V3Height;
            var PreviousHeight = -1L;
            var BlockDelay = Task.CompletedTask;

            ConsoleWriterService.OutputVal("Booting up consensus loop");
            while (true && !string.IsNullOrEmpty(Globals.ValidatorAddress))
            {
                var delay = Task.Delay(new TimeSpan(0, 0, 5));
                if ((Globals.StopAllTimers && !Globals.IsChainSynced) || Globals.Nodes.Count == 0)
                {
                    await delay;
                    continue;
                }

                if (!AlertValidatorsOfStatusDone || !ActiveValidatorRequestDone)
                {
                    await delay;
                    continue;
                }

                try
                {
                    var Height = Globals.LastBlock.Height + 1;

                    if (Height != Globals.LastBlock.Height + 1)
                        continue;

                    if (PreviousHeight == -1L) // First time running
                    {
                        await WaitForNextConsensusRound();
                    }

                    // Time correction only when height changes
                    if (PreviousHeight != Height)
                    {
                        PreviousHeight = Height;
                        await Task.WhenAll(BlockDelay, Task.Delay(1500));
                        var CurrentTime = TimeUtil.GetMillisecondTime();
                        var DelayTimeCorrection = Globals.BlockTime * (Height - BeginBlock) - (CurrentTime - EpochTime);
                        var DelayTime = Math.Min(Math.Max(Globals.BlockTime + DelayTimeCorrection, Globals.BlockTimeMin), Globals.BlockTimeMax);
                        BlockDelay = Task.Delay((int)DelayTime);
                        ConsoleWriterService.OutputVal("\r\nNext Consensus Delay: " + DelayTime + " (" + DelayTimeCorrection + ")");
                    }

                    ValidatorApprovalBag = new ConcurrentBag<(string, long)>();
                    //Generate Proofs for ALL vals
                    ConsoleWriterService.OutputVal("\r\nGenerating Proofs");
                    var proofs = await ProofUtility.GenerateProofs();
                    ConsoleWriterService.OutputVal($"\r\n{proofs.Count()} Proofs Generated");
                    var winningProof = await ProofUtility.SortProofs(proofs);
                    ConsoleWriterService.OutputVal($"\r\nSorting Proofs");

                    if (winningProof != null)
                    {
                        var verificationResult = false;
                        List<string> ExcludeValList = new List<string>();
                        while (!verificationResult)
                        {
                            if (winningProof != null)
                            {
                                verificationResult = await ProofUtility.VerifyWinnerAvailability(winningProof);
                                if (!verificationResult)
                                {
                                    ExcludeValList.Add(winningProof.Address);
                                    winningProof = await ProofUtility.SortProofs(
                                        proofs.Where(x => !ExcludeValList.Contains(x.Address)).ToList()
                                    );

                                    if(winningProof == null)
                                    {
                                        ExcludeValList.Clear();
                                        ExcludeValList = new List<string>();
                                    }
                                }
                                else
                                {
                                    ExcludeValList.Clear(); 
                                    ExcludeValList = new List<string>();
                                }

                            }
                            else
                            {
                                ExcludeValList.Clear();
                                ExcludeValList = new List<string>();
                                break;
                            }
                        }

                        if (winningProof == null)
                        {
                            ConsoleWriterService.OutputVal($"\r\nCould not connect to any nodes for winning proof. Starting over.");
                            continue;
                        }

                        ConsoleWriterService.OutputVal($"\r\nPotential Winner Found! Address: {winningProof.Address}");
                        Globals.Proofs.Add(winningProof);
                        await Broadcast("2", JsonConvert.SerializeObject(winningProof), "SendWinningProofVote");
                    }

                    //send proofs over rest call as backup to random nodes
                     _ = SendWinningProof(winningProof);

                    await Task.Delay(PROOF_COLLECTION_TIME);

                    var proofSnapshot = Globals.Proofs.ToList();

                    var finalizedWinnerGroup = proofSnapshot
                        .OrderBy(x => Math.Abs(x.VRFNumber))
                        .GroupBy(x => x.Address)
                        .OrderByDescending(x => x.Count())
                        .ThenBy(x => x.Min(y => Math.Abs(y.VRFNumber)))
                        .FirstOrDefault();

                    if (finalizedWinnerGroup != null)
                    {
                        var finalizedWinner = finalizedWinnerGroup.FirstOrDefault();
                        if (finalizedWinner != null)
                        {
                            if (finalizedWinner.Address == Globals.ValidatorAddress)
                            {
                                ConsoleWriterService.OutputVal($"\r\nYou Won! Awaiting Approval To Craft Block");
                                bool approved = false;
                                ValidatorApprovalBag.Add(("local", finalizedWinner.BlockHeight));

                                var valCount = Globals.NetworkValidators.Count() - Globals.FailedBlockProducers.Count();

                                var sw = Stopwatch.StartNew();
                                while (!approved && sw.ElapsedMilliseconds < APPROVAL_WINDOW)
                                {
                                    var approvalRate = (decimal)ValidatorApprovalBag
                                        .Count(x => x.Item2 == finalizedWinner.BlockHeight) / valCount;

                                    if (approvalRate >= 0.51M)
                                        approved = true;

                                    await Task.Delay(100);
                                }

                                if (approved)
                                {
                                    var nextblock = Globals.LastBlock.Height + 1;
                                    var block = await BlockchainData.CraftBlock_V5(
                                                    Globals.ValidatorAddress,
                                                    Globals.NetworkValidators.Count(),
                                                    finalizedWinner.ProofHash, nextblock);

                                    if (block != null)
                                    {
                                        ConsoleWriterService.OutputVal($"\r\nBlock crafted. Sending block.");
                                        _ = Broadcast("7", JsonConvert.SerializeObject(block), "");
                                        _ = P2PValidatorClient.BroadcastBlock(block);
                                    }
                                }
                            }
                            else
                            {
                                var approvalSent = false;

                                var sw = Stopwatch.StartNew();
                                while (!approvalSent && sw.ElapsedMilliseconds < APPROVAL_WINDOW)
                                {
                                    using (var client = Globals.HttpClientFactory.CreateClient())
                                    {
                                        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(APPROVAL_WINDOW));
                                        try
                                        {
                                            var uri = $"http://{finalizedWinner.IPAddress.Replace("::ffff:", "")}:{Globals.ValPort}/valapi/validator/sendapproval/{finalizedWinner.BlockHeight}";
                                            var response = await client.GetAsync(uri, cts.Token);
                                            if (response.IsSuccessStatusCode)
                                            {
                                                approvalSent = true;
                                            }
                                            else
                                            {
                                                await Task.Delay(100);
                                            }
                                        }
                                        catch (Exception e) { }
                                    }
                                }

                                ConsoleWriterService.OutputVal($"\r\nYou did not win. Looking for block.");
                                if (Globals.LastBlock.Height < finalizedWinner.BlockHeight)
                                {
                                    bool blockFound = false;
                                    var swb = Stopwatch.StartNew();
                                    while (!blockFound && swb.ElapsedMilliseconds < BLOCK_REQUEST_WINDOW)
                                    {
                                        if(Globals.LastBlock.Height == finalizedWinner.BlockHeight)
                                        {
                                            _ = Broadcast("7", JsonConvert.SerializeObject(Globals.LastBlock), "");
                                            _ = P2PValidatorClient.BroadcastBlock(Globals.LastBlock);
                                            blockFound = true;
                                            break;
                                        }
                                        try
                                        {
                                            using (var client = Globals.HttpClientFactory.CreateClient())
                                            {
                                                var uri = $"http://{finalizedWinner.IPAddress.Replace("::ffff:", "")}:{Globals.ValPort}/valapi/validator/getblock/{finalizedWinner.BlockHeight}";
                                                var response = await client.GetAsync(uri).WaitAsync(new TimeSpan(0,0,0,0,BLOCK_REQUEST_WINDOW));

                                                if (response != null)
                                                {
                                                    if (response.IsSuccessStatusCode)
                                                    {
                                                        var responseBody = await response.Content.ReadAsStringAsync();
                                                        if (responseBody != null)
                                                        {
                                                            if (responseBody == "0")
                                                            {
                                                                await Task.Delay(200);
                                                                continue;
                                                            }

                                                            var block = JsonConvert.DeserializeObject<Block>(responseBody);
                                                            if (block != null)
                                                            {
                                                                var IP = finalizedWinner.IPAddress;
                                                                var nextHeight = Globals.LastBlock.Height + 1;
                                                                var currentHeight = block.Height;

                                                                if (!BlockDownloadService.BlockDict.ContainsKey(currentHeight))
                                                                {
                                                                    BlockDownloadService.BlockDict[currentHeight] = (block, IP);
                                                                    if (nextHeight == currentHeight)
                                                                        await BlockValidatorService.ValidateBlocks();
                                                                    if (nextHeight < currentHeight)
                                                                        await BlockDownloadService.GetAllBlocks();
                                                                }

                                                                if (currentHeight == nextHeight && BlockDownloadService.BlockDict.TryAdd(currentHeight, (block, IP)))
                                                                {
                                                                    blockFound = true;

                                                                    if (Globals.LastBlock.Height < block.Height)
                                                                        await BlockValidatorService.ValidateBlocks();

                                                                    if (nextHeight == currentHeight)
                                                                    {
                                                                        _ = Broadcast("7", JsonConvert.SerializeObject(block), "");
                                                                        _ = P2PValidatorClient.BroadcastBlock(block);
                                                                    }

                                                                    if (nextHeight < currentHeight)
                                                                        await BlockDownloadService.GetAllBlocks();

                                                                    break;
                                                                }
                                                                else
                                                                {
                                                                    if (!BlockDownloadService.BlockDict.ContainsKey(currentHeight))
                                                                    {
                                                                        BlockDownloadService.BlockDict[currentHeight] = (block, IP);
                                                                        if (nextHeight == currentHeight)
                                                                            await BlockValidatorService.ValidateBlocks();
                                                                        if (nextHeight < currentHeight)
                                                                            await BlockDownloadService.GetAllBlocks();
                                                                    }
                                                                }
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                        }

                                        await Task.Delay(200);
                                    }

                                    if(!blockFound)
                                    {
                                        ConsoleWriterService.OutputVal($"\r\nValidator failed to produce block: {finalizedWinner.Address}");
                                        if(finalizedWinner.Address != "xMpa8DxDLdC9SQPcAFBc2vqwyPsoFtrWyC" && finalizedWinner.Address != "xBRzJUZiXjE3hkrpzGYMSpYCHU1yPpu8cj")
                                            ProofUtility.AddFailedProducer(finalizedWinner.Address);
                                    }
                                }
                            }
                        }
                    }

                    ConsoleWriterService.OutputVal($"\r\nStarting over.");
                    Globals.Proofs.Clear();
                    Globals.Proofs = new ConcurrentBag<Proof>();
                }
                catch (Exception ex)
                {
                    ErrorLogUtility.LogError($"Error in consensus loop: {ex.Message}", "ValidatorNode.StartConsensus()");
                }
            }
        }

        #region Process Data
        public static async Task ProcessData(string message, string data, string ipAddress)
        {
            if (string.IsNullOrEmpty(message))
                return;

            switch (message)
            {
                case "1":
                    _ = IpMessage(data);
                    break;
                case "2":
                    _ = ReceiveVote(data);
                    break;
                case "3":
                    _ = ReceiveNetworkValidator(data);
                    break;
                case "7":
                    _ = ReceiveConfirmedBlock(data);
                    break;
                case "7777":
                    _ = TxMessage(data);
                    break;
                case "9999":
                    _ = FailedToConnect(data);
                    break;
            }
        }

        #endregion

        #region Messages
        //1
        private static async Task IpMessage(string data)
        {
            var IP = data.ToString();
            if (Globals.ReportedIPs.TryGetValue(IP, out int Occurrences))
                Globals.ReportedIPs[IP]++;
            else
                Globals.ReportedIPs[IP] = 1;
        }

        //2
        private static async Task ReceiveVote(string data)
        {
            if (string.IsNullOrEmpty(data)) return;
            try
            {
                var proof = JsonConvert.DeserializeObject<Proof>(data);
                if (proof != null)
                {
                    if (proof.VerifyProof())
                        Globals.Proofs.Add(proof);
                }
            }
            catch (Exception ex)
            {
            }
        }

        //3
        private static async Task ReceiveNetworkValidator(string data)
        {
            try
            {
                var netVal = JsonConvert.DeserializeObject<NetworkValidator>(data);
                if (netVal == null)
                    return;

                await NetworkValidator.AddValidatorToPool(netVal);
            }
            catch (Exception ex)
            {

            }
        }

        //7
        public static async Task ReceiveConfirmedBlock(string data)
        {
            if (string.IsNullOrEmpty(data)) return;

            var nextBlock = JsonConvert.DeserializeObject<Block>(data);

            if (nextBlock == null) return;

            var lastBlockHeight = Globals.LastBlock.Height;
            if (lastBlockHeight < nextBlock.Height)
            {
                var result = await BlockValidatorService.ValidateBlock(nextBlock, true, false, false, true);
                if (result)
                {
                    if (nextBlock.Height > lastBlockHeight)
                    {
                        _ = P2PValidatorClient.BroadcastBlock(nextBlock, false);
                    }

                }
            }
        }

        //7777
        private static async Task TxMessage(string data)
        {
            var transaction = JsonConvert.DeserializeObject<Transaction>(data);
            if (transaction != null)
            {
                var ablList = Globals.ABL.ToList();
                if (ablList.Exists(x => x == transaction.FromAddress))
                {
                    return;
                }

                var isTxStale = await TransactionData.IsTxTimestampStale(transaction);
                if (!isTxStale)
                {
                    var mempool = TransactionData.GetPool();

                    if (mempool.Count() != 0)
                    {
                        var txFound = mempool.FindOne(x => x.Hash == transaction.Hash);
                        if (txFound == null)
                        {
                            var twSkipVerify = transaction.TransactionType == TransactionType.TKNZ_WD_OWNER ? true : false;
                            var txResult = !twSkipVerify ? await TransactionValidatorService.VerifyTX(transaction) : await TransactionValidatorService.VerifyTX(transaction, false, false, true);
                            if (txResult.Item1 == true)
                            {
                                var dblspndChk = await TransactionData.DoubleSpendReplayCheck(transaction);
                                var isCraftedIntoBlock = await TransactionData.HasTxBeenCraftedIntoBlock(transaction);
                                var rating = await TransactionRatingService.GetTransactionRating(transaction);
                                transaction.TransactionRating = rating;

                                if (dblspndChk == false && isCraftedIntoBlock == false && rating != TransactionRating.F)
                                {
                                    mempool.InsertSafe(transaction);
                                    _ = Broadcast("7777", data, "SendTxToMempoolVals");

                                }
                            }

                        }
                        else
                        {
                            //TODO Add this to also check in-mem blocks
                            var isCraftedIntoBlock = await TransactionData.HasTxBeenCraftedIntoBlock(transaction);
                            if (isCraftedIntoBlock)
                            {
                                try
                                {
                                    mempool.DeleteManySafe(x => x.Hash == transaction.Hash);// tx has been crafted into block. Remove.
                                }
                                catch (Exception ex)
                                {
                                    //delete failed
                                }
                            }
                        }
                    }
                    else
                    {
                        var twSkipVerify = transaction.TransactionType == TransactionType.TKNZ_WD_OWNER ? true : false;
                        var txResult = !twSkipVerify ? await TransactionValidatorService.VerifyTX(transaction) : await TransactionValidatorService.VerifyTX(transaction, false, false, true);
                        if (txResult.Item1 == true)
                        {
                            var dblspndChk = await TransactionData.DoubleSpendReplayCheck(transaction);
                            var isCraftedIntoBlock = await TransactionData.HasTxBeenCraftedIntoBlock(transaction);
                            var rating = await TransactionRatingService.GetTransactionRating(transaction);
                            transaction.TransactionRating = rating;

                            if (dblspndChk == false && isCraftedIntoBlock == false && rating != TransactionRating.F)
                            {
                                mempool.InsertSafe(transaction);
                            }
                        }
                    }
                }

            }
        }

        //9999
        public static async Task FailedToConnect(string data)
        {

        }
        #endregion

        #region Notify Explorer Status
        public static async Task NotifyExplorer()
        {
            while (true && !string.IsNullOrEmpty(Globals.ValidatorAddress))
            {
                var delay = Task.Delay(new TimeSpan(0, 1, 0));
                try
                {
                    if (Globals.StopAllTimers && !Globals.IsChainSynced)
                    {
                        await delay;
                        continue;
                    }

                    if(!Globals.ValidatorNodes.Any())
                    {
                        await delay;
                        continue;
                    }

                    var account = AccountData.GetLocalValidator();
                    if (account == null)
                        return;

                    var validator = Validators.Validator.GetAll().FindOne(x => x.Address == account.Address);
                    if (validator == null)
                        return;

                    var fortis = new FortisPool
                    {
                        Address = Globals.ValidatorAddress,
                        ConnectDate = Globals.ValidatorStartDate,
                        IpAddress = P2PClient.MostLikelyIP(),
                        LastAnswerSendDate = DateTime.UtcNow,
                        UniqueName = validator.UniqueName,
                        WalletVersion = validator.WalletVersion
                    };

                    List<FortisPool> fortisPool = new List<FortisPool> { fortis };

                    var listFortisPool = fortisPool.Select(x => new
                    {
                        ConnectionId = "NA",
                        x.ConnectDate,
                        x.LastAnswerSendDate,
                        x.IpAddress,
                        x.Address,
                        x.UniqueName,
                        x.WalletVersion
                    }).ToList();

                    //await NotifyExplorerLock.WaitAsync();

                    var fortisPoolStr = JsonConvert.SerializeObject(listFortisPool);

                    using (var client = Globals.HttpClientFactory.CreateClient())
                    {
                        string endpoint = Globals.IsTestNet ? "https://testnet-data.rbx.network/api/masternodes/send/" : "https://data.rbx.network/api/masternodes/send/";
                        var httpContent = new StringContent(fortisPoolStr, Encoding.UTF8, "application/json");
                        using (var Response = await client.PostAsync(endpoint, httpContent))
                        {
                            if (Response.StatusCode == System.Net.HttpStatusCode.OK)
                            {
                                //success
                                Globals.ExplorerValDataLastSend = DateTime.Now;
                                Globals.ExplorerValDataLastSendSuccess = true;
                                Globals.ExplorerValDataLastSendResponseCode = "200-OK";
                            }
                            else
                            {
                                //ErrorLogUtility.LogError($"Error sending payload to explorer. Response Code: {Response.StatusCode}. Reason: {Response.ReasonPhrase}", "ClientCallService.DoFortisPoolWork()");
                                Globals.ExplorerValDataLastSendSuccess = false;
                                Globals.ExplorerValDataLastSendResponseCode = Response.StatusCode.ToString();
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    ErrorLogUtility.LogError($"Failed to send validator list to explorer API. Error: {ex.ToString()}", "ValidatorService.NotifyExplorer()");
                    Globals.ExplorerValDataLastSendSuccess = false;
                }
                finally
                {
                    //NotifyExplorerLock.Release();
                }
                await delay;
            }

        }

        #endregion

        #region Broadcast
        public static async Task Broadcast(string messageType, string data, string method = "")
        {
            await HubContext.Clients.All.SendAsync("GetValMessage", messageType, data);

            if (method == "") return;

            if (!Globals.ValidatorNodes.Any()) return;

            var valNodeList = Globals.ValidatorNodes.Values.Where(x => x.IsConnected).ToList();

            if (valNodeList == null || valNodeList.Count() == 0) return;

            foreach (var val in valNodeList)
            {
                var source = new CancellationTokenSource(2000);
                await val.Connection.InvokeCoreAsync(method, args: new object?[] { data }, source.Token);
            }
        }

        #endregion

        #region Send Winning Proof Backup Method
        public static async Task SendWinningProof(Proof proof)
        {
            // Create a CancellationTokenSource with a timeout of 5 seconds
            var validators = Globals.NetworkValidators.Values.ToList();

            try
            {
                var rnd = new Random();
                var randomizedValidators = validators
                    .OrderBy(x => rnd.Next())
                    .ToList();

                var postData = JsonConvert.SerializeObject(proof);
                var httpContent = new StringContent(postData, Encoding.UTF8, "application/json");

                if (!randomizedValidators.Any())
                    return;

                var sw = Stopwatch.StartNew();
                randomizedValidators.ParallelLoop(async validator =>
                {
                    if (sw.ElapsedMilliseconds >= PROOF_COLLECTION_TIME)
                    {
                        // Stop processing if cancellation is requested
                        sw.Stop();
                        return;
                    }

                    using (var client = Globals.HttpClientFactory.CreateClient())
                    {
                        try
                        {
                            // Create a request-specific CancellationTokenSource with a 1-second timeout
                            var uri = $"http://{validator.IPAddress.Replace("::ffff:", "")}:{Globals.ValPort}/valapi/validator/ReceiveWinningProof";
                            await client.PostAsync(uri, httpContent).WaitAsync(new TimeSpan(0, 0, 2));
                            await Task.Delay(75);
                        }
                        catch (Exception ex)
                        {
                            // Log or handle the exception if needed
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error in proof distribution: {ex.Message}", "ValidatorNode.SendWinningProof()");
            }
        }
        #endregion

        #region Get Val List
        public static async Task<Peers[]?> GetValList(bool skipConnectedNodes = false)
        {
            var peerDB = Peers.GetAll();

            var SkipIPs = new HashSet<string>(Globals.ValidatorNodes.Values.Select(x => x.NodeIP.Replace(":" + Globals.Port, ""))
            .Union(Globals.BannedIPs.Keys)
            .Union(Globals.SkipValPeers.Keys)
            .Union(Globals.ReportedIPs.Keys));

            if (!skipConnectedNodes)
            {
                var connectedNodes = Globals.ValidatorNodes.Values.Where(x => x.IsConnected).ToArray();

                foreach (var validator in connectedNodes)
                {
                    SkipIPs.Add(validator.NodeIP);
                }
            }

            if (Globals.ValidatorAddress == "xMpa8DxDLdC9SQPcAFBc2vqwyPsoFtrWyC")
            {
                SkipIPs.Add("66.94.124.2");
            }

            var peerList = peerDB.Find(x => x.IsValidator).ToArray()
                .Where(x => !SkipIPs.Contains(x.PeerIP))
                .ToArray();

            if (!peerList.Any())
            {
                //clear out skipped peers to try again
                Globals.SkipValPeers.Clear();

                SkipIPs = new HashSet<string>(Globals.ValidatorNodes.Values.Select(x => x.NodeIP.Replace(":" + Globals.Port, ""))
                .Union(Globals.BannedIPs.Keys)
                .Union(Globals.SkipValPeers.Keys)
                .Union(Globals.ReportedIPs.Keys));

                peerList = peerDB.Find(x => x.IsValidator).ToArray()
                .Where(x => !SkipIPs.Contains(x.PeerIP))
                .ToArray();
            }

            return peerList;
        }
        #endregion

        #region Get Approval

        public static async Task GetApproval(string? ip, long blockHeight)
        {
            if (ip == null)
                return;

            var alreadyApproved = ValidatorApprovalBag.Where(x => x.Item1 == ip).ToList();
            if (alreadyApproved.Any())
                return;

            ValidatorApprovalBag.Add((ip.Replace("::ffff:", ""), blockHeight));
        }

        #endregion

        #region Wait For Next Consensus Round
        private static async Task WaitForNextConsensusRound()
        {
            var CurrentTime = TimeUtil.GetMillisecondTime();
            var LastBlockTime = Globals.LastBlock.Timestamp;
            var TimeSinceLastBlock = CurrentTime - LastBlockTime;

            // If we're in the middle of a consensus round, wait for the next one
            if (TimeSinceLastBlock < Globals.BlockTime)
            {
                var waitTime = Globals.BlockTime - TimeSinceLastBlock + 1000; // Add 1 second buffer
                await Task.Delay((int)waitTime);
            }
        }

        #endregion

        #region Stop/Dispose
        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public void Dispose()
        {

        }

        #endregion
    }
}
