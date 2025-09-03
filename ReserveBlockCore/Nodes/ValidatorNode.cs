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
        private static ConcurrentBag<(string, long, string)> ValidatorApprovalBag = new ConcurrentBag<(string, long, string)>();
        const int PROOF_COLLECTION_TIME = 7000; // 7 seconds
        const int APPROVAL_WINDOW = 12000;      // 12 seconds
        const int BLOCK_REQUEST_WINDOW = 12000;  // 12 seconds

        public ValidatorNode(IHubContext<P2PValidatorServer> hubContext, IHostApplicationLifetime appLifetime)
        {
            _hubContext = hubContext;
            HubContext = hubContext;
            _appLifetime = appLifetime;
        }

        public async Task StartAsync(CancellationToken stoppingToken)
        {
            _ = ActiveValidatorRequest();

            await GetBlockcasters();

            //_ = BlockCasterMonitor();

            //_ = ValidatorHeartbeat();

            _ = NotifyExplorer();

            _ = GenerateValidBlock();
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

        public static async Task GenerateValidBlock()
        {
            var account = AccountData.GetLocalValidator();
            var validators = Validators.Validator.GetAll();
            var validator = validators.FindOne(x => x.Address == account.Address);

            while (true && !string.IsNullOrEmpty(Globals.ValidatorAddress))
            {
                var delay = Task.Delay(new TimeSpan(0, 0, 5));
                if ((Globals.StopAllTimers || !Globals.IsChainSynced) || Globals.Nodes.Count == 0)
                {
                    await delay;
                    continue;
                }
                if (validator == null)
                {
                    await delay;
                    continue;
                }
                var currentHeight = Globals.LastBlock.Height;
                var prevHash = Globals.LastBlock.Hash;
                var nextHeight = currentHeight + 1;
                var height = Globals.NextValidatorBlock.Height;

                var proof = await ProofUtility.CreateProof(validator.Address, account.PublicKey, nextHeight, prevHash);

                if (currentHeight >= height)
                {
                    //generate new block
                    //produce proof
                    if (Globals.NextValidatorBlock != null)
                    {
                        if (Globals.NextValidatorBlock.Height == nextHeight)
                        {
                            await Task.Delay(1000);
                            continue;
                        }
                    }
                    var block = await BlockchainData.CraftBlock_V5(
                                                    Globals.ValidatorAddress,
                                                    Globals.NetworkValidators.Count(),
                                                    proof.Item2, nextHeight, false, true);

                    if (block != null)
                    {
                        Globals.NextValidatorBlock = block;
                    }
                }

                await Task.Delay(1000);
                continue;
            }
        }

        #region Get Block Casters and Caster Monitor

        public static async Task BlockCasterMonitor(bool comingOnline = true)
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

                    await GetBlockcasters();

                    comingOnline = false;
                    AlertValidatorsOfStatusDone = true;
                    await Task.Delay(new TimeSpan(0, 1, 0));
                }
            }
        }

        public static async Task GetBlockcasters()
        {
            if ((Globals.StopAllTimers && !Globals.IsChainSynced) || Globals.Nodes.Count == 0)
            {
                return;
            }

            List<Peers> peerList = new List<Peers>()
            {

                new Peers
                {
                    IsIncoming = false,
                    IsOutgoing = true,
                    PeerIP = "15.204.9.193",
                    FailCount = 0,
                    IsValidator = true,
                    ValidatorAddress = "RK28ywrBfEXV5EuARn3etyVXMtcmywNxnM",
                    ValidatorPublicKey = "04b906f0da02bbc25f0d65f30a9f07bb9a93bc78aee1d894fd4492ff0b8c97e05e0fb0d698f13a852578b3cb6bd9c97440820b24c859ac43f35ccb7eb03e9eccc6"
                },
                new Peers
                {
                    IsIncoming = false,
                    IsOutgoing = true,
                    PeerIP = "15.204.9.117",
                    FailCount = 0,
                    IsValidator = true,
                    ValidatorAddress = "RFoKrASMr19mg8S71Lf1F2suzxahG5Yj4N",
                    ValidatorPublicKey = "04aa4c95e0754b87a26f3c853f71a01e1ad5ed2d269d2f12b09dcb3b86637eb88550abe954c3bd99eb64b68dfcf8174bb0fb159b0a83bb2610e272f16094f37d9e"
                },
                new Peers
                {
                    IsIncoming = false,
                    IsOutgoing = true,
                    PeerIP = "66.175.236.113",
                    FailCount = 0,
                    IsValidator = true,
                    ValidatorAddress = "RH9XAP3omXvk7P6Xe9fQ1C6nZQ1adJw2ZG",
                    ValidatorPublicKey = "0433c204046d55d09c62cf5ae83dcdb81a843eb45a1bff8ce93986bee8149812d639ce003a8714dada8efa270b7ba61c2b45c9a8840c40e03d3375dc86f68bab6e"
                }
            };

            if(Globals.IsTestNet)
            {
                peerList = new List<Peers>()
                {
                    new Peers
                    {
                        IsIncoming = false,
                        IsOutgoing = true,
                        PeerIP = "144.126.156.102",
                        FailCount = 0,
                        IsValidator = true,
                        ValidatorAddress = "xBRzJUZiXjE3hkrpzGYMSpYCHU1yPpu8cj",
                        ValidatorPublicKey = "0498ea84777552a3609143275b0e083086071a6b1453bd46b87a05461d24e0ee99e7de2870a018240026ad6ba892a087df39447f91c5a8f8e50a53b6643c9e713c"
                    },
                    new Peers
                    {
                        IsIncoming = false,
                        IsOutgoing = true,
                        PeerIP = "66.94.124.2",
                        FailCount = 0,
                        IsValidator = true,
                        ValidatorAddress = "xMpa8DxDLdC9SQPcAFBc2vqwyPsoFtrWyC",
                        ValidatorPublicKey = "04eec44726e6442cc2ec0241f7c8c2a983d9cfbf9f68a2bc3e2040fd1053636f3779ffaeabcda9065627dee6d3ff5f080833e8ff8a3e93b8f17a600d0f7d090687"
                    },
                    new Peers
                    {
                        IsIncoming = false,
                        IsOutgoing = true,
                        PeerIP = "66.94.124.3",
                        FailCount = 0,
                        IsValidator = true,
                        ValidatorAddress = "xCkUC4rrh2AnfNf78D5Ps83pMywk5vrwpi",
                        ValidatorPublicKey = "0474f0a933dc0241d5fc6059eeb3a14350c4ba890e8e504ae144f33a41c0f5eb9aed9b98e33265507c55e2af1b0c61c5c2a87fa55d86acad0592c0f4774c97e62b"
                    },
                };
            }

            foreach (var caster in peerList)
            {
                if (caster != null)
                {
                    var nCaster = Globals.BlockCasters.Where(x => x.ValidatorAddress == caster.ValidatorAddress).FirstOrDefault();
                    if (nCaster == null)
                        Globals.BlockCasters.Add(caster);
                }
            }
        }

        #endregion

        #region ValidatorHeartbeat

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

                var peerList = Globals.BlockCasterNodes.Values.ToList();

                if (!peerList.Any())
                {
                    await delay;
                    continue;
                }

                var peerDB = Peers.GetAll();

                var coreCount = Environment.ProcessorCount;
                if (coreCount >= 4 || Globals.RunUnsafeCode)
                {
                    var tasks = peerList.Select(async peer =>
                    {
                        try
                        {
                            using (var client = Globals.HttpClientFactory.CreateClient())
                            {
                                var uri = $"http://{peer.NodeIP.Replace("::ffff:", "")}:{Globals.ValAPIPort}/valapi/validator/heartbeat/{Globals.ValidatorAddress}";

                                var sw = Stopwatch.StartNew();
                                var response = await client.GetAsync(uri).WaitAsync(new TimeSpan(0, 0, 3));
                                sw.Stop();
                                await Task.Delay(100);

                                if (response.IsSuccessStatusCode)
                                {
                                    if (response.StatusCode == HttpStatusCode.Accepted)
                                    {
                                        var account = AccountData.GetLocalValidator();
                                        var validators = Validators.Validator.GetAll();
                                        var validator = validators.FindOne(x => x.Address == account.Address);
                                        if (validator != null)
                                        {
                                            var time = TimeUtil.GetTime().ToString();
                                            var signature = SignatureService.ValidatorSignature(validator.Address + ":" + time + ":" + account.PublicKey);

                                            var networkVal = new NetworkValidator
                                            {
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

                                            try
                                            {
                                                uri = $"http://{peer.NodeIP.Replace("::ffff:", "")}:{Globals.ValAPIPort}/valapi/validator/status";
                                                await client.PostAsync(uri, httpContent).WaitAsync(new TimeSpan(0, 0, 4));
                                                await Task.Delay(100);
                                            }
                                            catch (Exception ex) { }
                                        }


                                        //TODO
                                        //send peer details
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {

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
                            using (var client = Globals.HttpClientFactory.CreateClient())
                            {
                                var uri = $"http://{peer.NodeIP.Replace("::ffff:", "")}:{Globals.ValAPIPort}/valapi/validator/heartbeat/{Globals.ValidatorAddress}";

                                var sw = Stopwatch.StartNew();
                                var response = await client.GetAsync(uri).WaitAsync(new TimeSpan(0, 0, 3));
                                sw.Stop();
                                await Task.Delay(100);

                                if (response.IsSuccessStatusCode)
                                {
                                    if (response.StatusCode == HttpStatusCode.Accepted)
                                    {
                                        var account = AccountData.GetLocalValidator();
                                        var validators = Validators.Validator.GetAll();
                                        var validator = validators.FindOne(x => x.Address == account.Address);
                                        if (validator != null)
                                        {
                                            var time = TimeUtil.GetTime().ToString();
                                            var signature = SignatureService.ValidatorSignature(validator.Address + ":" + time + ":" + account.PublicKey);

                                            var networkVal = new NetworkValidator
                                            {
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

                                            try
                                            {
                                                uri = $"http://{peer.NodeIP.Replace("::ffff:", "")}:{Globals.ValAPIPort}/valapi/validator/status";
                                                await client.PostAsync(uri, httpContent).WaitAsync(new TimeSpan(0, 0, 4));
                                                await Task.Delay(100);
                                            }
                                            catch (Exception ex) { }
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

                await Task.Delay(new TimeSpan(0, 0, 30));
            }
        }

        #endregion

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
                                    _ = Broadcast("7777", transaction, "SendTxToMempoolVals");

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

        #region Added ConsensusHeader to Queue
        public static async Task AddConsensusHeaderQueue(ConsensusHeader cHeader)
        {
            Globals.ConsensusHeaderQueue.Enqueue(cHeader);

            if (Globals.ConsensusHeaderQueue.Count() > 3456)
                Globals.ConsensusHeaderQueue.TryDequeue(out _);
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
                        string endpoint = Globals.IsTestNet ? "https://data-testnet.verifiedx.io/api/masternodes/send/" : "https://data.verifiedx.io/api/masternodes/send/";
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
        public static async Task Broadcast(string messageType, Transaction data, string method = "")
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
