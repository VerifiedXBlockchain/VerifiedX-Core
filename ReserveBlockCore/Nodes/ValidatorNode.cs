using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Newtonsoft.Json;
using ReserveBlockCore.Bitcoin.ElectrumX;
using ReserveBlockCore.Data;
using ReserveBlockCore.Models;
using ReserveBlockCore.P2P;
using ReserveBlockCore.Services;
using ReserveBlockCore.Utilities;
using System.Collections.Concurrent;
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

        public ValidatorNode(IHubContext<P2PValidatorServer> hubContext, IHostApplicationLifetime appLifetime)
        {
            _hubContext = hubContext;
            HubContext = hubContext;
            _appLifetime = appLifetime;
        }
        public async Task StartAsync(CancellationToken stoppingToken)
        {
            //Request latest val list - RequestValidatorList()
            await ActiveValidatorRequest();

            //Alert vals you are online - OnlineMethod()
            await AlertValidatorsOfStatus();

            //Checks for active vals every 15 mins
            _ = ValidatorHeartbeat();

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
        }

        public static async Task AlertValidatorsOfStatus(bool comingOnline = true)
        {
            if(comingOnline) 
            {
                while (true && !string.IsNullOrEmpty(Globals.ValidatorAddress))
                {
                    var delay = Task.Delay(new TimeSpan(0, 0, 5));
                    if ((Globals.StopAllTimers && !Globals.IsChainSynced) || Globals.Nodes.Count == 0)
                    {
                        await delay;
                        continue;
                    }

                    var peerList = await GetValList(true);

                    var peerDB = Peers.GetAll();

                    peerList.ParallelLoop(async peer =>
                    {
                        using (var client = Globals.HttpClientFactory.CreateClient())
                        {
                            try
                            {
                                var uri = $"http://{peer.PeerIP}:{Globals.ValPort}/api/validator/status/{Globals.ValidatorAddress}/{Globals.ValidatorPublicKey}";
                                await client.GetAsync(uri).WaitAsync(new TimeSpan(0, 0, 1));
                            }
                            catch (Exception ex) { }
                            
                        }
                    });
                }
            }
            else
            {

            }
        }

        public static async Task ValidatorHeartbeat()
        {
            while(true && !string.IsNullOrEmpty(Globals.ValidatorAddress))
            {
                var delay = Task.Delay(new TimeSpan(0, 0, 5));
                if ((Globals.StopAllTimers && !Globals.IsChainSynced) || Globals.Nodes.Count == 0)
                {
                    await delay;
                    continue;
                }

                var peerDB = Peers.GetAll();

                var peerList = await GetValList();

                ConcurrentBag<string> BadValidatorList = new ConcurrentBag<string>();

                peerList.ParallelLoop(async peer =>
                {
                    try
                    {
                        using (var client = Globals.HttpClientFactory.CreateClient())
                        {
                            var uri = $"http://{peer.PeerIP}:{Globals.ValPort}/api/validator/heartbeat";
                            var response = await client.GetAsync(uri).WaitAsync(new TimeSpan(0, 0, 2));

                            if (response != null)
                            {
                                if(!response.IsSuccessStatusCode)
                                    BadValidatorList.Add(peer.PeerIP);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        BadValidatorList.Add(peer.PeerIP);
                    }
                });
                
                foreach(var val in BadValidatorList)
                {
                    var validator = peerDB.FindOne(x => x.PeerIP == val);
                    if(validator != null)
                    {
                        validator.IsValidator = false;
                        peerDB.UpdateSafe(validator);
                    }
                }

                await Task.Delay(new TimeSpan(0,15,0));
            }
        }

        public static async Task StartConsensus()
        {
            var EpochTime = Globals.IsTestNet ? 1731454926600L : 1674172800000L;
            var BeginBlock = Globals.IsTestNet ? Globals.V4Height : Globals.V3Height;
            var PreviousHeight = -1L;
            var BlockDelay = Task.CompletedTask;

            ConsoleWriterService.Output("Booting up consensus loop");
            while (true && !string.IsNullOrEmpty(Globals.ValidatorAddress))
            {
                var delay = Task.Delay(new TimeSpan(0, 0, 5));
                if ((Globals.StopAllTimers && !Globals.IsChainSynced) || Globals.Nodes.Count == 0)
                {
                    await delay;
                    continue;
                }

                try
                {
                    var Height = Globals.LastBlock.Height + 1;

                    if (Height != Globals.LastBlock.Height + 1)
                        continue;

                    if (PreviousHeight != Height)
                    {
                        PreviousHeight = Height;
                        await Task.WhenAll(BlockDelay, Task.Delay(1500));
                        var CurrentTime = TimeUtil.GetMillisecondTime();
                        var DelayTimeCorrection = Globals.BlockTime * (Height - BeginBlock) - (CurrentTime - EpochTime);
                        var DelayTime = Math.Min(Math.Max(Globals.BlockTime + DelayTimeCorrection, Globals.BlockTimeMin), Globals.BlockTimeMax);
                        BlockDelay = Task.Delay((int)DelayTime);
                        ConsoleWriterService.Output("\r\nNext Consensus Delay: " + DelayTime + " (" + DelayTimeCorrection + ")");
                    }

                    if (Height != Globals.LastBlock.Height + 1)
                        continue;

                    //Generate Proofs for ALL vals
                    var proofs = await ProofUtility.GenerateProofs();
                    var winningProof = await ProofUtility.SortProofs(proofs);

                    //cast vote to master and subs
                    if (winningProof != null)
                    {
                        Globals.Proofs.Add(winningProof);
                        await Broadcast("2", JsonConvert.SerializeObject(winningProof), "SendWinningProofVote");
                    }

                    //await 
                    await Task.Delay(2500); //Give 3 seconds for other proofs. Might be able to reduce this.

                    var finalizedWinnerGroup = Globals.Proofs.GroupBy(x => x.Address).OrderByDescending(x => x.Count()).FirstOrDefault();
                    if (finalizedWinnerGroup != null)
                    {
                        var finalizedWinner = finalizedWinnerGroup.FirstOrDefault();
                        if (finalizedWinner != null)
                        {
                            if (finalizedWinner.Address == Globals.ValidatorAddress)
                            {
                                //Craft Block
                                var nextblock = Globals.LastBlock.Height + 1;
                                var block = await BlockchainData.CraftBlock_V5(
                                                Globals.ValidatorAddress,
                                                Globals.NetworkValidators.Count(),
                                                finalizedWinner.ProofHash, nextblock);

                                if(block != null)
                                {
                                    //Send block to others
                                    _ = Broadcast("7", JsonConvert.SerializeObject(block), "");
                                    _ = P2PValidatorClient.BroadcastBlock(block);
                                }
                            }
                            else
                            {
                                //Give winner time to craft. Might need to increase.
                                await Task.Delay(2000);
                                //Request block if it is not here
                                if (Globals.LastBlock.Height < finalizedWinner.BlockHeight)
                                {
                                    for(var i = 0; i < 3; i++)
                                    {
                                        try
                                        {
                                            using (var client = Globals.HttpClientFactory.CreateClient())
                                            {
                                                var uri = $"http://{finalizedWinner.IPAddress}:{Globals.ValPort}/api/validator/getblock/{finalizedWinner.BlockHeight}";
                                                var response = await client.GetAsync(uri).WaitAsync(new TimeSpan(0, 0, 2));

                                                if (response != null)
                                                {
                                                    if (response.IsSuccessStatusCode)
                                                    {
                                                        var responseBody = await response.Content.ReadAsStringAsync();
                                                        if( responseBody != null )
                                                        {
                                                            if(responseBody == "0")
                                                            {
                                                                await Task.Delay(1000);
                                                                continue;
                                                            }

                                                            var block = JsonConvert.DeserializeObject<Block>(responseBody);
                                                            if(block != null)
                                                            {
                                                                var IP = finalizedWinner.IPAddress;
                                                                var nextHeight = Globals.LastBlock.Height + 1;
                                                                var currentHeight = block.Height;

                                                                if (currentHeight >= nextHeight && BlockDownloadService.BlockDict.TryAdd(currentHeight, (block, IP)))
                                                                {
                                                                    await Task.Delay(2000);

                                                                    if (Globals.LastBlock.Height < block.Height)
                                                                        await BlockValidatorService.ValidateBlocks();

                                                                    if (nextHeight == currentHeight)
                                                                    {
                                                                        _ = Broadcast("7", JsonConvert.SerializeObject(block), "");
                                                                        _ = P2PValidatorClient.BroadcastBlock(block);
                                                                    }

                                                                    if (nextHeight < currentHeight)
                                                                        await BlockDownloadService.GetAllBlocks();
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
                                    }
                                }
                            }
                        }
                    }

                    //start over.
                    Globals.Proofs.Clear();
                    Globals.Proofs = new ConcurrentBag<Proof>();

                }
                catch (Exception ex)
                {

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
                    //send vote
                    //TODO: ADD METHOD HERE
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

        #region Get Val List
        public static async Task<Peers[]?> GetValList(bool skipConnectedNodes = false)
        {
            var peerDB = Peers.GetAll();

            var SkipIPs = new HashSet<string>(Globals.ValidatorNodes.Values.Select(x => x.NodeIP.Replace(":" + Globals.Port, ""))
            .Union(Globals.BannedIPs.Keys)
            .Union(Globals.SkipValPeers.Keys)
            .Union(Globals.ReportedIPs.Keys));

            if(!skipConnectedNodes)
            {
                var connectedNodes = Globals.ValidatorNodes.Values.Where(x => x.IsConnected).ToArray();

                foreach (var validator in connectedNodes)
                {
                    SkipIPs.Add(validator.NodeIP);
                }
            }
            
            if (Globals.ValidatorAddress == "xMpa8DxDLdC9SQPcAFBc2vqwyPsoFtrWyC")
            {
                SkipIPs.Add("144.126.156.101");
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
