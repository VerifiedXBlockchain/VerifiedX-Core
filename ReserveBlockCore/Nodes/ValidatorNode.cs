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

            _ = BlockCasterMonitor();

            _ = ValidatorHeartbeat();

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
                    if(Globals.NextValidatorBlock != null)
                    {
                        if(Globals.NextValidatorBlock.Height == nextHeight)
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

            var peerList = Globals.BlockCasterNodes.Values.ToList();

            if (!peerList.Any())
            {
                return;
            }

            ConcurrentBag<string> CasterIPs = new ConcurrentBag<string>();
            ConcurrentDictionary<string, Peers> CasterDict = new ConcurrentDictionary<string, Peers>();

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
                            var uri = $"http://{peer.NodeIP.Replace("::ffff:", "")}:{Globals.ValPort}/valapi/Blockcasters/";

                            var response = await client.GetAsync(uri).WaitAsync(new TimeSpan(0, 0, 3));
                            await Task.Delay(75);

                            if (response != null)
                            {
                                if (response.IsSuccessStatusCode)
                                {
                                    var responseBody = await response.Content.ReadAsStringAsync();
                                    if (!string.IsNullOrEmpty(responseBody))
                                    {
                                        if(responseBody != "0")
                                        {
                                            var blockCasters = JsonConvert.DeserializeObject<List<Peers>?>(responseBody);
                                            if(blockCasters?.Count() > 0)
                                            {
                                                blockCasters.ForEach(x => {
                                                    CasterIPs.Add(x.PeerIP);
                                                    CasterDict.TryAdd(x.PeerIP, x);
                                                });
                                            }
                                        }
                                    }
                                    else
                                    {
                                        //bad response body.
                                    }
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
                            var uri = $"http://{peer.NodeIP.Replace("::ffff:", "")}:{Globals.ValPort}/valapi/Blockcasters/";

                            var response = await client.GetAsync(uri).WaitAsync(new TimeSpan(0, 0, 3));
                            await Task.Delay(75);

                            if (response != null)
                            {
                                if (response.IsSuccessStatusCode)
                                {
                                    var responseBody = await response.Content.ReadAsStringAsync();
                                    if (!string.IsNullOrEmpty(responseBody))
                                    {
                                        if (responseBody != "0")
                                        {
                                            var blockCasters = JsonConvert.DeserializeObject<List<Peers>?>(responseBody);
                                            if (blockCasters?.Count() > 0)
                                            {
                                                blockCasters.ForEach(x => {
                                                    CasterIPs.Add(x.PeerIP);
                                                    CasterDict.TryAdd(x.PeerIP, x);
                                                });
                                            }
                                        }
                                    }
                                    else
                                    {
                                        //bad response body.
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

            var top5IPs = CasterIPs
                .GroupBy(ip => ip)
                .OrderByDescending(g => g.Count())
                .Take(5)
                .Select(g => g.Key)
                .ToList();

            var top5Casters = top5IPs
                .Select(ip => new { IP = ip, Peer = CasterDict.TryGetValue(ip, out var peer) ? peer : null })
                .Where(x => x.Peer != null) 
                .Select(x => x.Peer)
                .ToList();


            foreach (var caster in top5Casters)
            {
                if(caster != null)
                    Globals.BlockCasters.Add(caster);
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
                                var uri = $"http://{peer.NodeIP.Replace("::ffff:", "")}:{Globals.ValPort}/valapi/validator/heartbeat/{Globals.ValidatorAddress}";

                                var sw = Stopwatch.StartNew();
                                var response = await client.GetAsync(uri).WaitAsync(new TimeSpan(0, 0, 3));
                                sw.Stop();
                                await Task.Delay(100);

                                if(response.IsSuccessStatusCode)
                                {
                                    if(response.StatusCode == HttpStatusCode.Accepted)
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
                                                uri = $"http://{peer.NodeIP.Replace("::ffff:", "")}:{Globals.ValPort}/valapi/validator/status";
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
                                var uri = $"http://{peer.NodeIP.Replace("::ffff:", "")}:{Globals.ValPort}/valapi/validator/heartbeat/{Globals.ValidatorAddress}";

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
                                                uri = $"http://{peer.NodeIP.Replace("::ffff:", "")}:{Globals.ValPort}/valapi/validator/status";
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

        #region Added ConsensusHeader to Queue
        public static async Task AddConsensusHeaderQueue(ConsensusHeader cHeader)
        {
            Globals.ConsensusHeaderQueue.Enqueue(cHeader);

            if(Globals.ConsensusHeaderQueue.Count() > 3456)
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
