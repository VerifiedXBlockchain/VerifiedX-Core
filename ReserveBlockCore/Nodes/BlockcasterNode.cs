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
    public class BlockcasterNode : IHostedService, IDisposable
    {
        public static IHubContext<P2PBlockcasterServer> HubContext;
        private readonly IHubContext<P2PBlockcasterServer> _hubContext;
        private readonly IHostApplicationLifetime _appLifetime;
        private static SemaphoreSlim ConsensusLock = new SemaphoreSlim(1, 1);
        private static bool ActiveValidatorRequestDone = false;
        private static bool AlertValidatorsOfStatusDone = false;
        static SemaphoreSlim NotifyExplorerLock = new SemaphoreSlim(1, 1);
        private static ConcurrentBag<(string, long, string)> ValidatorApprovalBag = new ConcurrentBag<(string, long, string)>();
        const int PROOF_COLLECTION_TIME = 7000; // 7 seconds
        const int APPROVAL_WINDOW = 12000;      // 12 seconds
        const int BLOCK_REQUEST_WINDOW = 12000;  // 12 seconds

        public BlockcasterNode(IHubContext<P2PBlockcasterServer> hubContext, IHostApplicationLifetime appLifetime)
        {
            _hubContext = hubContext;
            HubContext = hubContext;
            _appLifetime = appLifetime;
        }

        public async Task StartAsync(CancellationToken stoppingToken)
        {
            _ = StartConsensus();
        }

        private static async Task StartConsensus()
        {
            //start consensus run here.  
            var delay = Task.Delay(new TimeSpan(0, 0, 5));
            var EpochTime = Globals.IsTestNet ? 1731454926600L : 1674172800000L;
            var BeginBlock = Globals.IsTestNet ? Globals.V4Height : Globals.V3Height;
            var PreviousHeight = -1L;
            var BlockDelay = Task.CompletedTask;
            ConsoleWriterService.OutputVal("Booting up consensus loop");

            while (true && !string.IsNullOrEmpty(Globals.ValidatorAddress))
            {
                ConsoleWriterService.OutputVal("Top of consensus loop");

                var casterList = Globals.BlockCasters.ToList();
                if (casterList.Any())
                {
                    if (casterList.Exists(x => x.ValidatorAddress == Globals.ValidatorAddress))
                        Globals.IsBlockCaster = true;
                    else
                        Globals.IsBlockCaster = false;
                }
                if (!Globals.IsBlockCaster)
                {
                    await Task.Delay(new TimeSpan(0, 0, 30));
                    continue;
                }

                Block? block = null;

                if (!Globals.BlockCasters.Any())
                {
                    //Get blockcasters if empty.
                    await ValidatorNode.GetBlockcasters();
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



                    var consensusHeader = new ConsensusHeader();
                    consensusHeader.Height = Height;
                    consensusHeader.Timestamp = TimeUtil.GetTime();
                    consensusHeader.NetworkValidatorCount = Globals.NetworkValidators.Count;

                    ValidatorApprovalBag = new ConcurrentBag<(string, long, string)>();
                    //Generate Proofs for ALL vals
                    ConsoleWriterService.OutputVal($"\r\nGenerating Proofs for height: {Height}.");
                    var proofs = await ProofUtility.GenerateProofs();
                    ConsoleWriterService.OutputVal($"\r\n{proofs.Count()} Proofs Generated");
                    var winningProof = await ProofUtility.SortProofs(proofs);
                    ConsoleWriterService.OutputVal($"\r\nSorting Proofs");

                    if (winningProof != null && proofs.Count() > 1)
                    {
                        ConsoleWriterService.OutputVal($"\r\nAttempting Proof on Address: {winningProof.Address}");
                        var verificationResult = false;
                        List<string> ExcludeValList = new List<string>();
                        while (!verificationResult)
                        {
                            if (winningProof != null)
                            {
                                //TODO:turn this into getting a block!
                                var nextblock = Globals.LastBlock.Height + 1;
                                var verificationResultTuple = await ProofUtility.VerifyWinnerAvailability(winningProof, nextblock);
                                verificationResult = verificationResultTuple.Item1;

                                if (!verificationResult)
                                {
                                    block = verificationResultTuple.Item2;

                                    ExcludeValList.Add(winningProof.Address);
                                    winningProof = await ProofUtility.SortProofs(
                                        proofs.Where(x => !ExcludeValList.Contains(x.Address)).ToList()
                                    );

                                    if (winningProof == null)
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
                                ConsoleWriterService.OutputVal($"\r\nFinalized winner : {finalizedWinner.Address}");

                                if (finalizedWinner.Address != winningProof.Address)
                                    block = null;

                                if (finalizedWinner.Address == Globals.ValidatorAddress)
                                {
                                    consensusHeader.WinningAddress = finalizedWinner.Address;
                                    ConsoleWriterService.OutputVal($"\r\nYou Won! Awaiting Approval To Craft Block");
                                    bool approved = false;
                                    ValidatorApprovalBag.Add(("local", finalizedWinner.BlockHeight, Globals.ValidatorAddress));

                                    var producers = ValidatorApprovalBag.Select(x => x.Item3).ToList();
                                    consensusHeader.ValidatorAddressReceiveList = producers;
                                    var failedProducersList = Globals.NetworkValidators.Values.Where(x => !producers.Contains(x.Address)).Select(x => x.Address).ToList();
                                    consensusHeader.ValidatorAddressFailList = failedProducersList;

                                    ProofUtility.PruneFailedProducers();

                                    var sw = Stopwatch.StartNew();
                                    while (!approved && sw.ElapsedMilliseconds < APPROVAL_WINDOW)
                                    {
                                        var approvalRate = (decimal)ValidatorApprovalBag
                                            .Count(x => x.Item2 == finalizedWinner.BlockHeight) / Globals.MaxBlockCasters;

                                        if (approvalRate >= 0.51M)
                                            approved = true;

                                        await Task.Delay(100);
                                    }

                                    if (approved)
                                    {
                                        var winnerFound = Globals.ProducerDict.TryGetValue(finalizedWinner.Address, out var winningCount);
                                        if (winnerFound)
                                        {
                                            Globals.ProducerDict[finalizedWinner.Address] = winningCount + 1;
                                        }
                                        else
                                        {
                                            Globals.ProducerDict.TryAdd(finalizedWinner.Address, 1);
                                        }
                                        var nextblock = Globals.LastBlock.Height + 1;

                                        if (block != null)
                                        {
                                            var addBlock = await BlockValidatorService.ValidateBlock(block, true, false, false, true);

                                            if (addBlock == true)
                                            {
                                                ConsoleWriterService.OutputVal($"\r\nSending block.");
                                                _ = Broadcast("7", JsonConvert.SerializeObject(block), "");
                                            }
                                            else
                                            {
                                                ConsoleWriterService.OutputVal($"\r\nBLOCK DID NOT VALIDATE!.");
                                            }

                                            //_ = P2PValidatorClient.BroadcastBlock(block);
                                        }
                                        else
                                        {
                                            block = await BlockchainData.CraftBlock_V5(
                                                    Globals.ValidatorAddress,
                                                    Globals.NetworkValidators.Count(),
                                                    finalizedWinner.ProofHash, finalizedWinner.BlockHeight);

                                            if (block != null)
                                            {
                                                ConsoleWriterService.OutputVal($"\r\nSending block.");
                                                _ = Broadcast("7", JsonConvert.SerializeObject(block), "");
                                            }
                                        }
                                    }
                                }
                                else
                                {
                                    var approvalSent = false;
                                    consensusHeader.WinningAddress = finalizedWinner.Address;

                                    var sw = Stopwatch.StartNew();
                                    while (!approvalSent && sw.ElapsedMilliseconds < APPROVAL_WINDOW)
                                    {
                                        using (var client = Globals.HttpClientFactory.CreateClient())
                                        {
                                            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(APPROVAL_WINDOW));
                                            try
                                            {
                                                var valAddr = Globals.ValidatorAddress;
                                                var uri = $"http://{finalizedWinner.IPAddress.Replace("::ffff:", "")}:{Globals.ValPort}/valapi/validator/sendapproval/{finalizedWinner.BlockHeight}/{valAddr}";
                                                var response = await client.GetAsync(uri, cts.Token);
                                                if (response.IsSuccessStatusCode)
                                                {
                                                    approvalSent = true;
                                                    ConsoleWriterService.OutputVal($"\r\nApproval sent to address: {finalizedWinner.Address}.");
                                                    ConsoleWriterService.OutputVal($"IP Address: {finalizedWinner.IPAddress}.");
                                                }
                                                else
                                                {
                                                    await Task.Delay(100);
                                                }
                                            }
                                            catch (Exception ex)
                                            {
                                                ConsoleWriterService.OutputVal($"\r\nError sending approval to address: {finalizedWinner.Address}.");
                                                ConsoleWriterService.OutputVal($"ERROR: {ex}.");
                                            }
                                        }
                                    }

                                    ConsoleWriterService.OutputVal($"\r\nYou did not win. Looking for block.");
                                    if (Globals.LastBlock.Height < finalizedWinner.BlockHeight)
                                    {
                                        bool blockFound = false;
                                        bool failedToReachConsensus = false;
                                        var swb = Stopwatch.StartNew();
                                        while (!blockFound && swb.ElapsedMilliseconds < BLOCK_REQUEST_WINDOW)
                                        {
                                            if (Globals.LastBlock.Height == finalizedWinner.BlockHeight)
                                            {
                                                ConsoleWriterService.OutputVal($"\r\nBlock found. Broadcasting.");
                                                _ = Broadcast("7", JsonConvert.SerializeObject(Globals.LastBlock), "");
                                                //_ = P2PValidatorClient.BroadcastBlock(Globals.LastBlock);
                                                blockFound = true;
                                                break;
                                            }

                                            try
                                            {
                                                using (var client = Globals.HttpClientFactory.CreateClient())
                                                {
                                                    foreach (var casters in Globals.BlockCasters)
                                                    {
                                                        ConsoleWriterService.OutputVal($"Requesting block from Caster: {casters.ValidatorAddress}");
                                                        var uri = $"http://{casters.PeerIP.Replace("::ffff:", "")}:{Globals.ValPort}/valapi/validator/getblock/{finalizedWinner.BlockHeight}";
                                                        var response = await client.GetAsync(uri).WaitAsync(new TimeSpan(0, 0, 0, 0, BLOCK_REQUEST_WINDOW));
                                                        if (response != null)
                                                        {
                                                            if (response.IsSuccessStatusCode)
                                                            {
                                                                var responseBody = await response.Content.ReadAsStringAsync();
                                                                if (responseBody != null)
                                                                {
                                                                    if (responseBody == "0")
                                                                    {
                                                                        ConsoleWriterService.OutputVal($"Response was 0 (zero)");
                                                                        failedToReachConsensus = true;
                                                                        await Task.Delay(75);
                                                                        continue;
                                                                    }

                                                                    failedToReachConsensus = false;
                                                                    ConsoleWriterService.OutputVal($"Response had non-zero data");
                                                                    block = JsonConvert.DeserializeObject<Block>(responseBody);
                                                                    if (block != null)
                                                                    {
                                                                        ConsoleWriterService.OutputVal($"Block deserialized. Height: {block.Height}");
                                                                        var IP = finalizedWinner.IPAddress;
                                                                        var nextHeight = Globals.LastBlock.Height + 1;
                                                                        var currentHeight = block.Height;

                                                                        if (!BlockDownloadService.BlockDict.ContainsKey(currentHeight))
                                                                        {
                                                                            ConsoleWriterService.OutputVal($"Inside block service A");
                                                                            BlockDownloadService.BlockDict[currentHeight] = (block, IP);
                                                                            if (nextHeight == currentHeight)
                                                                                await BlockValidatorService.ValidateBlocks();
                                                                            if (nextHeight < currentHeight)
                                                                                await BlockDownloadService.GetAllBlocks();
                                                                        }

                                                                        if (currentHeight == nextHeight && BlockDownloadService.BlockDict.TryAdd(currentHeight, (block, IP)))
                                                                        {
                                                                            blockFound = true;
                                                                            //_ = AddConsensusHeaderQueue(consensusHeader);
                                                                            if (Globals.LastBlock.Height < block.Height)
                                                                                await BlockValidatorService.ValidateBlocks();

                                                                            if (nextHeight == currentHeight)
                                                                            {
                                                                                ConsoleWriterService.OutputVal($"Inside block service B");
                                                                                _ = Broadcast("7", JsonConvert.SerializeObject(block), "");
                                                                                //_ = P2PValidatorClient.BroadcastBlock(block);
                                                                            }

                                                                            if (nextHeight < currentHeight)
                                                                                await BlockDownloadService.GetAllBlocks();

                                                                            break;
                                                                        }
                                                                        else
                                                                        {
                                                                            ConsoleWriterService.OutputVal($"Inside block service C");
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

                                                        await Task.Delay(75);
                                                    }
                                                }
                                            }
                                            catch (Exception ex) { }

                                            await Task.Delay(200);
                                        }

                                        if (!blockFound && failedToReachConsensus)
                                        {
                                            Globals.FailedProducerDict.TryGetValue(finalizedWinner.Address, out var failRec);
                                            if (failRec.Item1 != 0)
                                            {
                                                var currentTime = TimeUtil.GetTime(0, 0, -1);
                                                failRec.Item2 += 1;
                                                Globals.FailedProducerDict[finalizedWinner.Address] = failRec;
                                                if (failRec.Item2 >= 3)
                                                {
                                                    if (currentTime > failRec.Item1)
                                                    {
                                                        var exist = Globals.FailedProducers.Where(x => x == finalizedWinner.Address).FirstOrDefault();
                                                        if (exist == null)
                                                            Globals.FailedProducers.Add(finalizedWinner.Address);
                                                    }
                                                }

                                                //Reset timer
                                                if (failRec.Item2 < 3)
                                                {
                                                    if (failRec.Item1 < currentTime)
                                                    {
                                                        failRec.Item1 = TimeUtil.GetTime();
                                                        failRec.Item2 = 1;
                                                        Globals.FailedProducerDict[finalizedWinner.Address] = failRec;
                                                    }
                                                }
                                            }
                                            else
                                            {
                                                Globals.FailedProducerDict.TryAdd(finalizedWinner.Address, (TimeUtil.GetTime(), 1));
                                            }
                                            ConsoleWriterService.OutputVal($"\r\nValidator failed to produce block: {finalizedWinner.Address}");
                                            if (finalizedWinner.Address != "xMpa8DxDLdC9SQPcAFBc2vqwyPsoFtrWyC" &&
                                                finalizedWinner.Address != "xBRzJUZiXjE3hkrpzGYMSpYCHU1yPpu8cj")
                                                ProofUtility.AddFailedProducer(finalizedWinner.Address);

                                            //have to add immediately if this happens.
                                            if (failedToReachConsensus)
                                            {
                                                Globals.FailedProducers.Add(finalizedWinner.Address);
                                                ConsoleWriterService.OutputVal($"\r\nAddress: {finalizedWinner.Address} added to failed producers. (Globals.FailedProducers)");
                                            }

                                        }
                                        else
                                        {
                                            if (!failedToReachConsensus)
                                            {
                                                var winnerFound = Globals.ProducerDict.TryGetValue(finalizedWinner.Address, out var winningCount);
                                                if (winnerFound)
                                                {
                                                    Globals.ProducerDict[finalizedWinner.Address] = winningCount + 1;
                                                }
                                                else
                                                {
                                                    Globals.ProducerDict.TryAdd(finalizedWinner.Address, 1);
                                                }
                                            }
                                            else
                                            {
                                                Globals.FailedProducerDict.TryGetValue(finalizedWinner.Address, out var failRec);
                                                if (failRec.Item1 != 0)
                                                {
                                                    var currentTime = TimeUtil.GetTime(0, 0, -1);
                                                    failRec.Item2 += 1;
                                                    Globals.FailedProducerDict[finalizedWinner.Address] = failRec;
                                                    if (failRec.Item2 >= 30)
                                                    {
                                                        if (currentTime > failRec.Item1)
                                                        {
                                                            var exist = Globals.FailedProducers.Where(x => x == finalizedWinner.Address).FirstOrDefault();
                                                            if (exist == null)
                                                                Globals.FailedProducers.Add(finalizedWinner.Address);
                                                        }
                                                    }

                                                    //Reset timer
                                                    if (failRec.Item2 < 30)
                                                    {
                                                        if (failRec.Item1 < currentTime)
                                                        {
                                                            failRec.Item1 = TimeUtil.GetTime();
                                                            failRec.Item2 = 1;
                                                            Globals.FailedProducerDict[finalizedWinner.Address] = failRec;
                                                        }
                                                    }
                                                }
                                                else
                                                {
                                                    Globals.FailedProducerDict.TryAdd(finalizedWinner.Address, (TimeUtil.GetTime(), 1));
                                                }
                                                ConsoleWriterService.OutputVal($"\r\n2-Validator failed to produce block: {finalizedWinner.Address}");
                                                if (finalizedWinner.Address != "xMpa8DxDLdC9SQPcAFBc2vqwyPsoFtrWyC" &&
                                                    finalizedWinner.Address != "xBRzJUZiXjE3hkrpzGYMSpYCHU1yPpu8cj")
                                                    ProofUtility.AddFailedProducer(finalizedWinner.Address);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }

                    ConsoleWriterService.OutputVal($"\r\nStarting over.");
                    Globals.Proofs.Clear();
                    Globals.Proofs = new ConcurrentBag<Proof>();
                    await Task.Delay(50);
                }
                catch (Exception ex)
                {
                }
            }
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
                case "4":
                    _ = FailedToReachConsensus(data);
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
        //4
        public static async Task FailedToReachConsensus(string data)
        {
            if (string.IsNullOrEmpty(data)) return;

            var failedProducersList = JsonConvert.DeserializeObject<List<string>>(data);

            if (failedProducersList == null) return;

            foreach (var val in failedProducersList)
            {
                Globals.FailedProducerDict.TryGetValue(val, out var failRec);
                if (failRec.Item1 != 0)
                {
                    var currentTime = TimeUtil.GetTime(0, 0, -1);
                    failRec.Item2 += 1;
                    Globals.FailedProducerDict[val] = failRec;
                    if (failRec.Item2 >= 10)
                    {
                        if (currentTime > failRec.Item1)
                        {
                            var exist = Globals.FailedProducers.Where(x => x == val).FirstOrDefault();
                            if (exist == null)
                                Globals.FailedProducers.Add(val);
                        }
                    }

                    //Reset timer
                    if (failRec.Item2 < 10)
                    {
                        if (failRec.Item1 < currentTime)
                        {
                            failRec.Item1 = TimeUtil.GetTime();
                            failRec.Item2 = 1;
                            Globals.FailedProducerDict[val] = failRec;
                        }
                    }
                }
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

        #region Broadcast
        public static async Task Broadcast(string messageType, string data, string method = "")
        {
            await HubContext.Clients.All.SendAsync("GetCasterMessage", messageType, data);

            if (method == "") return;

            if (!Globals.BlockCasterNodes.Any()) return;

            var valNodeList = Globals.BlockCasterNodes.Values.Where(x => x.IsConnected).ToList();

            if (valNodeList == null || valNodeList.Count() == 0) return;

            foreach (var val in valNodeList)
            {
                var source = new CancellationTokenSource(2000);
                await val.Connection.InvokeCoreAsync(method, args: new object?[] { data }, source.Token);
            }
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

        #region Send Winning Proof Backup Method
        public static async Task SendWinningProof(Proof proof)
        {
            // Create a CancellationTokenSource with a timeout of 5 seconds
            var validators = Globals.BlockCasterNodes.Values.ToList();

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
                            var uri = $"http://{validator.NodeIP.Replace("::ffff:", "")}:{Globals.ValPort}/valapi/validator/ReceiveWinningProof";
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


        #region Get Approval

        public static async Task GetApproval(string? ip, long blockHeight, string validatorAddress)
        {
            if (ip == null)
                return;

            ip = ip.Replace("::ffff:", "");

            var alreadyApproved = ValidatorApprovalBag.Where(x => x.Item1 == ip).ToList();
            if (alreadyApproved.Any())
                return;

            ValidatorApprovalBag.Add((ip.Replace("::ffff:", ""), blockHeight, validatorAddress));
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
