﻿using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Newtonsoft.Json;
using ReserveBlockCore.Beacon;
using ReserveBlockCore.Data;
using ReserveBlockCore.EllipticCurve;
using ReserveBlockCore.Models;
using ReserveBlockCore.P2P;
using ReserveBlockCore.Utilities;
using ReserveBlockCore.Extensions;
using System;
using System.Collections.Concurrent;
using System.Data.Common;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Numerics;
using System.Security;
using System.Transactions;
using System.Xml.Linq;
using static ReserveBlockCore.P2P.ConsensusClient;
using static System.Net.WebRequestMethods;
using System.Text;

namespace ReserveBlockCore.Services
{
    public class ClientCallService : IHostedService, IDisposable
    {
        #region Timers and Private Variables
        public static IHubContext<P2PAdjServer> HubContext;
        private readonly IHubContext<P2PAdjServer> _hubContext;
        private readonly IHostApplicationLifetime _appLifetime;
        private int executionCount = 0;        
        private Timer _fortisPoolTimer = null!;
        private Timer _checkpointTimer = null!;
        private Timer _blockStateSyncTimer = null!;
        private Timer _consensusBroadcastTimer = null!;
        private Timer _encryptedPasswordTimer = null!;
        private Timer _assetTimer = null!;
        private static bool FirstRun = false;
        private static bool StateSyncLock = false;
        private static bool AssetLock = false;
        private static bool BroadcastLock = false;

        public ClientCallService(IHubContext<P2PAdjServer> hubContext, IHostApplicationLifetime appLifetime)
        {
            _hubContext = hubContext;
            HubContext = hubContext;
            _appLifetime = appLifetime;
        }

        public Task StartAsync(CancellationToken stoppingToken)
        {
            _fortisPoolTimer = new Timer(DoFortisPoolBroadcastWork, null, TimeSpan.FromSeconds(90),
                TimeSpan.FromSeconds(90));

            if (Globals.ChainCheckPoint == true)
            {
                var interval = Globals.ChainCheckPointInterval;
                
                _checkpointTimer = new Timer(DoCheckpointWork, null, TimeSpan.FromSeconds(240),
                TimeSpan.FromHours(interval));
            }

            _encryptedPasswordTimer = new Timer(DoPasswordClearWork, null, TimeSpan.FromSeconds(5),
                TimeSpan.FromMinutes(Globals.PasswordClearTime));

            _assetTimer = new Timer(DoAssetWork, null, TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(5));

            _consensusBroadcastTimer = new Timer(DoConsensusBroadcastWork, null, TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(30));

            return Task.CompletedTask;
        }

        #endregion

        #region Checkpoint Work
        private async void DoCheckpointWork(object? state)
        {
            var retain = Globals.ChainCheckPointRetain;
            var path = GetPathUtility.GetDatabasePath();
            var checkpointPath = Globals.ChainCheckpointLocation;
            var zipPath = checkpointPath + "checkpoint_" + DateTime.Now.Ticks.ToString();

            try
            {
                var directoryCount = Directory.GetFiles(checkpointPath).Length;
                if(directoryCount >= retain)
                {
                    FileSystemInfo fileInfo = new DirectoryInfo(checkpointPath).GetFileSystemInfos()
                        .OrderBy(fi => fi.CreationTime).First();
                    fileInfo.Delete();
                }

                ZipFile.CreateFromDirectory(path, zipPath);
                var createDate = DateTime.Now.ToString();
                LogUtility.Log($"Checkpoint successfully created at: {createDate}", "ClientCallService.DoCheckpointWork()");
            }
            catch(Exception ex)
            {
                ErrorLogUtility.LogError($"Error creating checkpoint. Error Message: {ex.ToString()}", "ClientCallService.DoCheckpointWork()");
            }
        }

        #endregion

        #region Password Clear Work

        private async void DoPasswordClearWork(object? state)
        {
            if(Globals.IsWalletEncrypted == true)
            {
                if(string.IsNullOrEmpty(Globals.ValidatorAddress) && Globals.AdjudicateAccount == null && !Globals.NFTsDownloading)
                {
                    Globals.EncryptPassword.Dispose();
                    Globals.EncryptPassword = new SecureString();
                }
                //Password must remain in order to continue validating. It is not recommend to have your validator be your main source of funds wallet.
                //Recommend transferring funds out to a secure offline wallet. 
            }
        }
        #endregion

        #region Asset Download/Upload Work

        private async void DoAssetWork(object? state)
        {
            if (!AssetLock && Globals.AutoDownloadNFTAsset)
            {
                AssetLock = true;
                {
                    bool encPassNeeded = false;
                    var currentDate = DateTime.UtcNow;
                    var aqDB = AssetQueue.GetAssetQueue();
                    if(aqDB != null)
                    {
                        var aqList = aqDB.Find(x => x.NextAttempt != null && x.NextAttempt <= currentDate && x.IsComplete != true && 
                            x.AssetTransferType == AssetQueue.TransferType.Download && x.Attempts < 4).ToList();

                        if(aqList.Count() > 0)
                        {
                            if(!Globals.IsWalletEncrypted || (Globals.IsWalletEncrypted && Globals.EncryptPassword.Length > 0))
                            {
                                Globals.NFTsDownloading = true;
                                foreach (var aq in aqList)
                                {
                                    aq.Attempts = aq.Attempts < 4 ? aq.Attempts + 1 : aq.Attempts;
                                    var nextAttemptValue = AssetQueue.GetNextAttemptInterval(aq.Attempts);
                                    aq.NextAttempt = DateTime.UtcNow.AddSeconds(nextAttemptValue);
                                    try
                                    {
                                        var result = await NFTAssetFileUtility.DownloadAssetFromBeacon(aq.SmartContractUID, aq.Locator, "NA", aq.MD5List);
                                        if (result == "Success")
                                        {
                                            SCLogUtility.Log($"Download Request has been sent", "ClientCallService.DoAssetWork()");
                                            aq.IsComplete = true;
                                            aq.Attempts = 0;
                                            aq.NextAttempt = DateTime.UtcNow;
                                            aqDB.UpdateSafe(aq);
                                        }
                                        else
                                        {
                                            SCLogUtility.Log($"Download Request has not been sent. Reason: {result}", "ClientCallService.DoAssetWork()");
                                            aqDB.UpdateSafe(aq);
                                        }

                                    }
                                    catch (Exception ex)
                                    {
                                        SCLogUtility.Log($"Error Performing Asset Download. Error: {ex.ToString()}", "ClientCallService.DoAssetWork()");
                                    }
                                }
                                Globals.NFTsDownloading = false;
                                Globals.NFTFilesReadyEPN = false;
                            }
                            else
                            {
                                //set global var to true
                                Globals.NFTFilesReadyEPN = true;
                            }
                            
                        }
                        else
                        {
                            //set global var to false
                            Globals.NFTFilesReadyEPN = false;
                        }

                        var curDate = DateTime.UtcNow;
                        var aqCompleteList = aqDB.Find(x =>  x.IsComplete == true && x.IsDownloaded == false &&
                            x.AssetTransferType == AssetQueue.TransferType.Download && x.NextAttempt <= curDate).ToList();

                        if(aqCompleteList.Count() > 0)
                        {
                            foreach(var aq in aqCompleteList)
                            {
                                try
                                {
                                    await NFTAssetFileUtility.CheckForAssets(aq);
                                    aq.Attempts = aq.Attempts < 4 ? aq.Attempts + 1 : aq.Attempts;
                                    var nextAttemptValue = AssetQueue.GetNextAttemptInterval(aq.Attempts);
                                    aq.NextAttempt = DateTime.UtcNow.AddSeconds(nextAttemptValue);
                                    //attempt to get file again. call out to beacon
                                    if (aq.MediaListJson != null)
                                    {
                                        var assetList = JsonConvert.DeserializeObject<List<string>>(aq.MediaListJson);
                                        if (assetList != null)
                                        {
                                            if (assetList.Count() > 0)
                                            {
                                                foreach (string asset in assetList)
                                                {
                                                    var path = NFTAssetFileUtility.NFTAssetPath(asset, aq.SmartContractUID);
                                                    var fileExist = System.IO.File.Exists(path);
                                                    if (!fileExist)
                                                    {
                                                        try
                                                        {
                                                            var fileCheckResult = await P2PClient.BeaconFileReadyCheck(aq.SmartContractUID, asset, aq.Locator);
                                                            if (fileCheckResult)
                                                            {
                                                                var beaconString = aq.Locator.ToStringFromBase64();
                                                                var beacon = JsonConvert.DeserializeObject<BeaconInfo.BeaconInfoJson>(beaconString);

                                                                if (beacon != null)
                                                                {
                                                                    BeaconResponse rsp = await BeaconClient.Receive_New(asset, beacon.IPAddress, beacon.Port, aq.SmartContractUID);
                                                                    if (rsp.Status != 1)
                                                                    {
                                                                        //failed to download
                                                                    }
                                                                }
                                                            }
                                                        }
                                                        catch { }
                                                    }
                                                }
                                                   
                                            }
                                        }
                                            
                                    }
                                        
                                    
                                    //Look to see if media exist
                                    await NFTAssetFileUtility.CheckForAssets(aq);
                                }
                                catch { }
                            }
                        }
                    }
                }

                AssetLock = false;
            }
        }
        #endregion

        #region Block State Sync Work
        private async void DoBlockStateSyncWork(object? state)
        {
            if(!StateSyncLock)
            {
                StateSyncLock = true;
                //await StateTreiSyncService.SyncAccountStateTrei();
                StateSyncLock = false;
            }
            else
            {
                //overlap has occurred.
            }
        }

        #endregion

        #region Consensus Broadcast Work
        private async void DoConsensusBroadcastWork(object? state)
        {
            try
            {
                if (Globals.AdjudicateAccount == null)
                {
                    await _consensusBroadcastTimer.DisposeAsync();
                    BroadcastLock = true;
                    await Task.Delay(500);
                }
                    
                if (!BroadcastLock)
                {
                    BroadcastLock = true;
                    var txsToBroadcastAdj = Globals.ConsensusBroadcastedTrxDict.Values.Where(x => !x.IsBroadcastedToAdj).ToList();
                    //Throttled to 10 per send for now.
                    Random rnd = new Random();
                    var txsToBroadcastVal = Globals.ConsensusBroadcastedTrxDict.Values.Where(x => !x.IsBroadcastedToVal).OrderBy(x => rnd.Next()).Take(10).ToList();

                    if (txsToBroadcastAdj.Count() > 0)
                    {
                        var txHashes = JsonConvert.SerializeObject(txsToBroadcastAdj.Select(x => x.Hash).ToArray());
                        AdjLogUtility.Log($"UTC: {DateTime.UtcNow} - Sending TX List to Other Nodes: {txHashes}", "ClientCallService.DoConsensusBroadcastWork()-1");

                        int count = 1;
                        var nodes = Globals.Nodes.Values.Where(x => x.IsConnected).ToList();
                        foreach (var node in nodes)
                        {
                            var source = new CancellationTokenSource(5000);
                            var result = await node.Connection.InvokeCoreAsync<bool>("GetBroadcastedTx", args: new object?[] { txsToBroadcastAdj }, source.Token);
                            AdjLogUtility.Log($"{count}. UTC: {DateTime.UtcNow} - TX List Sent to: {node.NodeIP}. Result: {result}", "ClientCallService.DoConsensusBroadcastWork()-2");
                            count += 1;
                        }

                        foreach (var tx in txsToBroadcastAdj)
                        {
                            if(Globals.ConsensusBroadcastedTrxDict.ContainsKey(tx.Hash))
                                Globals.ConsensusBroadcastedTrxDict[tx.Hash].IsBroadcastedToAdj = true;
                        }
                    }

                    var mempool = TransactionData.GetPool();

                    //Attempt to rebroadcast a stale tx to get it confirmed
                    //Putting in try catch for now to ensure normal TXs still move.
                    try
                    {
                        var currentTimeLessThree = TimeUtil.GetTime(-120);
                        var rebroadcastTxs = mempool.Query().Where(x => x.Timestamp <= currentTimeLessThree).ToEnumerable();

                        if (rebroadcastTxs.Count() > 0)
                        {
                            foreach (var tx in rebroadcastTxs)
                            {
                                if (Globals.ConsensusBroadcastedTrxDict.ContainsKey(tx.Hash))
                                {
                                    if (Globals.ConsensusBroadcastedTrxDict[tx.Hash].RebroadcastCount < 3)
                                    {
                                        Globals.ConsensusBroadcastedTrxDict[tx.Hash].RebroadcastCount += 1; //add to attempts
                                        Globals.ConsensusBroadcastedTrxDict[tx.Hash].IsBroadcastedToVal = false; //attempt rebroadcast
                                    }
                                }
                            }
                            txsToBroadcastVal.Clear();
                            txsToBroadcastVal = new List<TransactionBroadcast>();
                            txsToBroadcastVal = Globals.ConsensusBroadcastedTrxDict.Values.Where(x => !x.IsBroadcastedToVal).OrderBy(x => rnd.Next()).Take(10).ToList();
                        }
                    }
                    catch(Exception ex)
                    {
                        AdjLogUtility.Log($"Error Doing Rebroadcast. Error: {ex.ToString()}", "ClientCallService.DoConsensusBroadcastWork()-E1");
                    }

                    if (txsToBroadcastVal.Count() > 0)
                    {
                        var txHashes = JsonConvert.SerializeObject(txsToBroadcastVal.Select(x => x.Hash).ToArray());
                        AdjLogUtility.Log($"UTC: {DateTime.UtcNow} - Sending TX List to Validators: {txHashes}", "ClientCallService.DoConsensusBroadcastWork()-3");
                        foreach (var transaction in txsToBroadcastVal)
                        {
                            var isTxStale = await TransactionData.IsTxTimestampStale(transaction.Transaction);
                            if (!isTxStale)
                            {
                                if (mempool.Count() != 0)
                                {
                                    var txFound = mempool.FindOne(x => x.Hash == transaction.Hash);
                                    if (txFound == null)
                                    {
                                        var isCraftedIntoBlock = await TransactionData.HasTxBeenCraftedIntoBlock(transaction.Transaction);
                                        if (!isCraftedIntoBlock)
                                        {
                                            mempool.InsertSafe(transaction.Transaction);
                                            var txOutput = "";
                                            txOutput = JsonConvert.SerializeObject(transaction.Transaction);
                                            await _hubContext.Clients.All.SendAsync("GetAdjMessage", "tx", txOutput).WaitAsync(new TimeSpan(0,0,25));
                                            if (Globals.ConsensusBroadcastedTrxDict.ContainsKey(transaction.Hash))
                                                Globals.ConsensusBroadcastedTrxDict[transaction.Hash].IsBroadcastedToVal = true;
                                            await Task.Delay(400);
                                        }
                                        else
                                        {
                                            Globals.ConsensusBroadcastedTrxDict.TryRemove(transaction.Hash, out _);
                                            Globals.BroadcastedTrxDict.TryRemove(transaction.Hash, out _);
                                        }
                                    }
                                    else
                                    {

                                        var isCraftedIntoBlock = await TransactionData.HasTxBeenCraftedIntoBlock(transaction.Transaction);
                                        if (!isCraftedIntoBlock)
                                        {
                                            var txOutput = "";
                                            txOutput = JsonConvert.SerializeObject(transaction.Transaction);
                                            await _hubContext.Clients.All.SendAsync("GetAdjMessage", "tx", txOutput).WaitAsync(new TimeSpan(0, 0, 25));
                                            if (Globals.ConsensusBroadcastedTrxDict.ContainsKey(transaction.Hash))
                                                Globals.ConsensusBroadcastedTrxDict[transaction.Hash].IsBroadcastedToVal = true;
                                            await Task.Delay(400);
                                        }
                                        else
                                        {
                                            try
                                            {
                                                Globals.ConsensusBroadcastedTrxDict.TryRemove(transaction.Hash, out _);
                                                Globals.BroadcastedTrxDict.TryRemove(transaction.Hash, out _);
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
                                    var isCraftedIntoBlock = await TransactionData.HasTxBeenCraftedIntoBlock(transaction.Transaction);

                                    if (!isCraftedIntoBlock)
                                    {
                                        mempool.InsertSafe(transaction.Transaction);
                                        var txOutput = "";
                                        txOutput = JsonConvert.SerializeObject(transaction.Transaction);
                                        await _hubContext.Clients.All.SendAsync("GetAdjMessage", "tx", txOutput).WaitAsync(new TimeSpan(0, 0, 25));
                                        if (Globals.ConsensusBroadcastedTrxDict.ContainsKey(transaction.Hash))
                                            Globals.ConsensusBroadcastedTrxDict[transaction.Hash].IsBroadcastedToVal = true;
                                        await Task.Delay(400);
                                    }
                                    else
                                    {
                                        Globals.ConsensusBroadcastedTrxDict.TryRemove(transaction.Hash, out _);
                                        Globals.BroadcastedTrxDict.TryRemove(transaction.Hash, out _);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AdjLogUtility.Log($"Error broadcasting TXs. Error: {ex.ToString()}", "ClientCallService.DoConsensusBroadcastWork()");
                ErrorLogUtility.LogError($"Error broadcasting TXs. Error: {ex.ToString()}", "ClientCallService.DoConsensusBroadcastWork()");
            }
            
            BroadcastLock = false;
        }

        #endregion

        #region Fortis Pool Broadcast
        private async void DoFortisPoolBroadcastWork(object? state)
        {
            try
            {
                if (Globals.StopAllTimers == false)
                {
                    if (Globals.AdjudicateAccount != null)
                    {
                        var fortisPool = Globals.FortisPool.Values.Where(x => x.LastAnswerSendDate != null)
                            .Select(x => new
                            {
                                x.Context.ConnectionId,
                                x.ConnectDate,
                                x.LastAnswerSendDate,
                                x.IpAddress,
                                x.Address,
                                x.UniqueName,
                                x.WalletVersion
                            }).ToList();

                        var fortisPoolStr = JsonConvert.SerializeObject(fortisPool);

                        try
                        {
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
                                    }
                                    else
                                    {
                                        //ErrorLogUtility.LogError($"Error sending payload to explorer. Response Code: {Response.StatusCode}. Reason: {Response.ReasonPhrase}", "ClientCallService.DoFortisPoolWork()");
                                        Globals.ExplorerValDataLastSendSuccess = false;
                                    }
                                }
                            }
                        }
                        catch(Exception ex)
                        {
                            ErrorLogUtility.LogError($"Failed to send validator list to explorer API. Error: {ex.ToString()}", "ClientCallService.DoFortisPoolWork()");
                            Globals.ExplorerValDataLastSendSuccess = false;
                        }
                        
                    }
                }

                //if (Globals.AdjudicateAccount != null)
                //{
                //}
                //rebroadcast TXs
                //var pool = TransactionData.GetPool();
                //var mempool = TransactionData.GetMempool();
                //var blockHeight = Globals.LastBlock.Height;
                //if(mempool != null)
                //{
                //    var currentTime = TimeUtil.GetTime(-120);
                //    if (mempool.Count() > 0)
                //    {
                //        foreach(var tx in mempool)
                //        {
                //            var txBeenBroadcasted = Globals.BroadcastedTrxDict.ContainsKey(tx.Hash);
                //            if(!txBeenBroadcasted)
                //            {
                //                var txTime = tx.Timestamp;
                //                var sendTx = currentTime > txTime ? true : false;
                //                if (sendTx)
                //                {
                //                    var txResult = await TransactionValidatorService.VerifyTX(tx);
                //                    if (txResult.Item1 == true)
                //                    {
                //                        var dblspndChk = await TransactionData.DoubleSpendReplayCheck(tx);
                //                        var isCraftedIntoBlock = await TransactionData.HasTxBeenCraftedIntoBlock(tx);

                //                        if (dblspndChk == false && isCraftedIntoBlock == false && tx.TransactionRating != TransactionRating.F)
                //                        {
                //                            var txOutput = "";
                //                            txOutput = JsonConvert.SerializeObject(tx);
                //                            await _hubContext.Clients.All.SendAsync("GetAdjMessage", "tx", txOutput);//sends messages to all in fortis pool
                //                            Globals.BroadcastedTrxDict[tx.Hash] = tx;
                //                        }
                //                        else
                //                        {
                //                            try
                //                            {
                //                                pool.DeleteManySafe(x => x.Hash == tx.Hash);// tx has been crafted into block. Remove.
                //                            }
                //                            catch (Exception ex)
                //                            {                                                
                //                                //delete failed
                //                            }
                //                        }
                //                    }
                //                    else
                //                    {
                //                        try
                //                        {
                //                            pool.DeleteManySafe(x => x.Hash == tx.Hash);// tx has been crafted into block. Remove.
                //                        }
                //                        catch (Exception ex)
                //                        {                                            
                //                            //delete failed
                //                        }
                //                    }

                //                }
                //            }
                //        }
                //    }
                //}
                
            }
            catch (Exception ex)
            {
                //no node found
                Console.WriteLine("Error: ClientCallService.DoFortisPoolWork(): " + ex.ToString());
            }
        }

        #endregion

        #region Do work V3
        private static async Task SendDuplicateMessage(string conId, int dupType)
        {
            if(dupType == 0)
            {
                try
                {
                    await HubContext.Clients.Client(conId).SendAsync("GetAdjMessage", "dupIP", "0", new CancellationTokenSource(1000).Token);
                }
                catch { }
            }

            if(dupType == 1)
            {
                try
                {
                    await HubContext.Clients.Client(conId).SendAsync("GetAdjMessage", "dupAddr", "1", new CancellationTokenSource(1000).Token);
                }
                catch { }
            }
        }

        public static int CombineRandoms(IList<int> randoms, int minValue, int maxValue)
        {
            return Modulo(randoms.Sum(x => (long)x), maxValue - minValue) + minValue;
        }

        public static int Modulo(long k, int n)
        {
            var mod = k % n;
            return mod < 0 ? (int)mod + n : (int)mod;
        }        
        public static async Task DoWorkV3()
        {           
            if (Interlocked.Exchange(ref Globals.AdjudicateLock, 1) == 1 || Globals.AdjudicateAccount == null)
                return;

            while (Globals.Nodes.Count == 0 || Globals.StopAllTimers)
                await Task.Delay(20);

            var EpochTime = Globals.IsTestNet ? 1673397583280L : 1674172800000L;
            var BeginBlock = Globals.IsTestNet ? 101013 : Globals.V3Height;

            var PreviousHeight = -1L;            
            var BlockDelay = Task.CompletedTask;
            ConsoleWriterService.Output("Booting up consensus loop");            
            while (true)
            {                               
                try
                {                    
                    var Height = Globals.LastBlock.Height + 1;
                    ClearRoundDicts(Height);
                    TaskQuestionUtility.CreateTaskQuestion("rndNum");
                    var State = ConsensusServer.GetState();
                    var Answer = State.Answer;
                    var Signers = Globals.Signers.Keys.ToHashSet();
                    var Majority = Signers.Count / 2 + 1;

                    while (Globals.BlocksDownloadSlim.CurrentCount == 0 || Globals.Nodes.Values.Where(x => x.IsConnected).Count() < Majority - 1)
                        await Task.Delay(20);

                    if (Height != Globals.LastBlock.Height + 1)
                        continue;
                    
                    if(PreviousHeight != Height)
                    {                                               
                        PreviousHeight = Height;
                        await Task.WhenAll(BlockDelay, Task.Delay(3500));
                        var CurrentTime = TimeUtil.GetMillisecondTime();
                        var DelayTimeCorrection = 25000 * (Height - BeginBlock) - (CurrentTime - EpochTime);                        
                        var DelayTime = Math.Min(Math.Max(25000 + DelayTimeCorrection, 20000), 30000);
                        BlockDelay = Task.Delay((int)DelayTime);
                        ConsoleWriterService.Output("\r\nNext Consensus Delay: " + DelayTime + " (" + DelayTimeCorrection +  ")");                        
                    }                    

                    if (Height != Globals.LastBlock.Height + 1)
                        continue;

                    ConsoleWriterService.Output("Start of Consensus at height " + Height);
                    var MyDecryptedAnswer = Height + ":" + Answer;
                    var MyEncryptedAnswer = State.EncryptedAnswer;
                    var MyEncryptedAnswerSignature = SignatureService.AdjudicatorSignature(MyEncryptedAnswer);
                    var EncryptedAnswers = await ConsensusClient.ConsensusRun(0, MyEncryptedAnswer, MyEncryptedAnswerSignature, 2000, RunType.Initial);
                    
                    if (Globals.LastBlock.Height + 1 != Height || EncryptedAnswers == null)                    
                        continue;                   

                    ConsoleWriterService.Output("EncryptedAnswer Consensus at height " + Height);
                                        
                    var MySubmissions = Globals.TaskAnswerDictV3.Where(x => x.Key.Height == Height).Select(x => x.Value).ToArray();
                    ConsoleWriterService.Output("My submission count " + MySubmissions.Length);
                    var MySubmissionsString = SerializeSubmissions(MySubmissions);
                    var MySubmissionsSignature = SignatureService.AdjudicatorSignature(MySubmissionsString);
                    var Submissions = await ConsensusClient.ConsensusRun(1, MySubmissionsString, MySubmissionsSignature, 2000, RunType.Middle);

                    if (Globals.LastBlock.Height + 1 != Height || Submissions == null)
                        continue;

                    ConsoleWriterService.Output("Submissions Consensus at height " + Height);
                    
                    var ValidSubmissions = Submissions                        
                        .Select(x => DeserializeSubmissions(x.Message))
                        .SelectMany(x => x)
                        .Select(x => (x.IPAddress, x.RBXAddress, x.Answer))
                        .Distinct()
                        .ToArray();

                    if (!ValidSubmissions.Any())
                        continue;

                    ConsoleWriterService.Output("Number of valid submissions: " + ValidSubmissions.Length);                    

                    var BadIPs = ValidSubmissions.Select(x => x.IPAddress).GroupBy(x => x).Where(x => x.Count() > 1)
                        .Select(x => x.First()).ToHashSet();
                    var BadAddresses = ValidSubmissions.Select(x => x.RBXAddress).GroupBy(x => x).Where(x => x.Count() > 1)
                        .Select(x => x.First()).ToHashSet();

                    BadIPs.ParallelLoop(ip =>
                    {
                        if (Globals.FortisPool.TryRemoveFromKey1(ip, out var pool))
                            _ = HadBadValidator(pool.Item2, ip, 0);
                    });

                    BadAddresses.ParallelLoop(address =>
                    {
                        if (Globals.FortisPool.TryRemoveFromKey2(address, out var pool))
                            _ = HadBadValidator(pool.Item2, address, 1);
                    });

                    ConsensusServer.UpdateState(isUsed: true);
                    var DecryptedAnswers = await ConsensusClient.ConsensusRun(2, MyDecryptedAnswer, MyEncryptedAnswer, 2000, RunType.Middle);

                    if (Globals.LastBlock.Height + 1 != Height || DecryptedAnswers == null)
                        continue;

                    ConsoleWriterService.Output("DecryptedAnswer Consensus at height " + Height);

                    var Answers = DecryptedAnswers.Select(x =>
                    {
                        var split = x.Message.Split(':');
                        var encryptedAnswer = EncryptedAnswers.Where(y => y.Address == x.Address).FirstOrDefault();
                        if (split.Length != 2 || long.Parse(split[0]) != Height || encryptedAnswer.Address == null ||
                            !SignatureService.VerifySignature(x.Address, x.Message, encryptedAnswer.Message))
                            return -1;
                        return int.Parse(split[1]);
                    })
                    .Where(x => x != -1)
                    .ToArray();
                    var ChosenAnswer = CombineRandoms(Answers, 0, int.MaxValue);
                    ConsoleWriterService.Output("Chosen answer: " + ChosenAnswer);

                    if (Answers.Length < Majority)
                        continue;

                    var PotentialWinners = ValidSubmissions                        
                        .Where(x => !BadIPs.Contains(x.IPAddress) && !BadAddresses.Contains(x.RBXAddress))
                        .GroupBy(x => x.Answer)
                        .Where(x => x.Count() == 1)
                        .Select(x => x.First())
                        .OrderBy(x => Math.Abs(x.Answer - ChosenAnswer))
                        .ThenBy(x => x.Answer)                        
                        .Where(x => AccountStateTrei.GetAccountBalance(x.RBXAddress) >= ValidatorService.ValidatorRequiredAmount())
                        .Take(30)
                        .ToArray();

                    foreach (var winner in PotentialWinners)
                        Globals.TaskSelectedNumbersV3[(winner.RBXAddress, Height)] = winner;

                    var WinnerPool = PotentialWinners
                        .Select(x =>
                        {
                            if (Globals.FortisPool.TryGetFromKey2(x.RBXAddress, out var Out))
                                return Out.Value;
                            return null;
                        })
                        .Where(x => x != null)
                        .ToArray();

                    ConsoleWriterService.Output("Potential winners total: " + PotentialWinners.Length + 
                        ". Requesting total: " + WinnerPool.Length);

                    Parallel.ForEach(WinnerPool, new ParallelOptions { MaxDegreeOfParallelism = 30 }, fortis =>
                    {
                        try
                        {
                            _ = HubContext.Clients.Client(fortis.Context.ConnectionId).SendAsync("GetAdjMessage", "sendWinningBlock", "",
                                new CancellationTokenSource(3000).Token);
                        }
                        catch (Exception ex)
                        {
                        }
                    });
                    
                    await Task.Delay(3000);
                    var MySubmittedWinners = PotentialWinners.Select(x =>
                    {
                        if (Globals.TaskWinnerDictV3.TryGetValue((x.RBXAddress, Height), out var block))
                            return block;
                        return null;
                    })
                    .Where(x => x != null)
                    .ToArray();

                    ConsoleWriterService.Output("Received winners total: " + MySubmittedWinners.Length);

                    var MySubmittedWinnersString = JsonConvert.SerializeObject(MySubmittedWinners);
                    var MySubmittedWinnersSignature = SignatureService.AdjudicatorSignature(MySubmittedWinnersString);
                    var SubmittedWinners = await ConsensusClient.ConsensusRun(3, MySubmittedWinnersString, MySubmittedWinnersSignature, 2000, RunType.Middle);

                    if (Globals.LastBlock.Height + 1 != Height || SubmittedWinners == null)
                        continue;

                    ConsoleWriterService.Output("Submitted Winner Consensus at height " + Height);

                    if (SubmittedWinners.Length == 0 && HubContext?.Clients != null)
                    {
                        try
                        {
                            await HubContext.Clients.All.SendAsync("GetAdjMessage", "taskResult", JsonConvert.SerializeObject(Globals.LastBlock),
                                new CancellationTokenSource(10000).Token);
                        }
                        catch { }
                        continue;
                    }

                    var WinnerDict = SubmittedWinners.Select(x => JsonConvert.DeserializeObject<Block[]>(x.Message))
                        .SelectMany(x => x)
                        .GroupBy(x => x.Hash)
                        .Select(x => x.First())
                        .GroupBy(x => x.Validator)
                        .Where(x => x.Count() == 1)
                        .Select(x => x.First())
                        .ToDictionary(x => x.Validator, x => x);

                    var OrderedWinners = PotentialWinners.Select(x => WinnerDict.GetValueOrDefault(x.RBXAddress)).Where(x => x != null).ToArray();

                    ConsoleWriterService.Output("Winner collected total: " + OrderedWinners.Length);

                    Block Winner = null;
                    foreach(var winner in OrderedWinners)
                        if(await BlockValidatorService.ValidateBlockForTask(winner, true))
                        {
                            Winner = winner;
                            break;
                        }

                    if(Winner == null)
                        continue;

                    ConsoleWriterService.Output("Winner found. Hash: " + Winner.Hash);

                    var WinnerHasheSignature = Winner.Hash + ":" + SignatureService.AdjudicatorSignature(Winner.Hash);                    
                    var WinnerHashSignature = SignatureService.AdjudicatorSignature(WinnerHasheSignature);                    
                                        
                    var HashResult = await ConsensusClient.ConsensusRun(4, WinnerHasheSignature, WinnerHashSignature, 2000, RunType.Last);

                    if (Globals.LastBlock.Height + 1 != Height || HashResult == null)
                        continue;

                    ConsoleWriterService.Output("WinnerHashSignature Consensus at height " + Height);
                    if(Globals.ConsensusStartHeight == -1)
                        Globals.ConsensusStartHeight = Height;
                    Globals.ConsensusSucceses++;

                    var Hashes = HashResult.Select(x => {
                        var split = x.Message.Split(':');
                        return (x.Address, Hash: split[0], Signature: split[1]);
                    })
                    .Where(x => x.Hash == Winner.Hash)
                    .Take(Majority)
                    .ToArray();

                    if(Hashes.Length != Majority)
                        continue;
                  
                    var signature = string.Join("|", Hashes.Select(x => x.Address + ":" + x.Signature));
                    Winner.AdjudicatorSignature = signature;

                    ConsensusServer.UpdateState(methodCode: 0, status: (int)ConsensusStatus.Processing);
                    await BlockValidatorService.ValidateBlock(Winner, false);     
                }
                catch(Exception ex)
                {
                    Console.WriteLine("Error: " + ex.ToString());
                    Console.WriteLine("Client Call Service");
                }
            }
        }

        public static async Task HadBadValidator(FortisPool pool, string key, int type)
        {            
            try
            {
                Globals.DuplicatesBroadcastedDict.TryGetValue(key, out var result);

                if (result == null)
                {
                    await SendDuplicateMessage(pool.Context.ConnectionId, type);
                    DuplicateValidators dupVal = new DuplicateValidators
                    {
                        Address = pool.Address,
                        IPAddress = pool.IpAddress,
                        LastNotified = DateTime.UtcNow,
                        LastDetection = DateTime.UtcNow,
                        NotifyCount = 1,
                        Reason = type == 0 ? DuplicateValidators.ReasonFor.DuplicateIP : DuplicateValidators.ReasonFor.DuplicateAddress,
                        StopNotify = false
                    };
                    Globals.DuplicatesBroadcastedDict[key] = dupVal;
                }
                else
                {
                    //If stop notify is false and we haven't sent a message in 30 minutes send another and add to count x/3
                    if (!result.StopNotify && result.LastNotified < DateTime.UtcNow.AddMinutes(-30))
                    {
                        await SendDuplicateMessage(pool.Context.ConnectionId, type);
                        result.LastNotified = DateTime.UtcNow;
                        result.LastDetection = DateTime.UtcNow;
                        result.NotifyCount += 1;
                        result.StopNotify = result.NotifyCount >= 3 ? true : false;

                        Globals.DuplicatesBroadcastedDict[key] = result;
                    }
                    else
                    {
                        //Stop notify is true and we wil no longer alert for 12 hours.
                        if (result.StopNotify)
                        {
                            if (result.LastNotified < DateTime.UtcNow.AddHours(-12))
                            {
                                //if they've gone 2 hours without acting a duplicate we will remove them 12 hours later.
                                if (result.LastDetection < DateTime.UtcNow.AddHours(-2))
                                    Globals.DuplicatesBroadcastedDict.TryRemove(key, out _);
                            }
                        }
                    }
                }
            }
            catch { }

            try
            {
                pool.Context?.Abort();
            }
            catch { }
        }

        public static async Task FinalizeWork(Block block)
        {
            try
            {
                ConsensusServer.UpdateState(methodCode: 0, status: (int)ConsensusStatus.Processing);
                if (Globals.BlocksDownloadSlim.CurrentCount == 0)
                    return;

                ConsoleWriterService.Output("Task Completed and Block Found: " + block.Height.ToString());
                ConsoleWriterService.Output(DateTime.Now.ToString());

                var Now = TimeUtil.GetMillisecondTime();
                ConsoleWriterService.Output("Sending Blocks Now - Height: " + block.Height.ToString() + " at " + Now);
                var data = JsonConvert.SerializeObject(block);

                Parallel.ForEach(Globals.Nodes.Values.Where(x => x.Address != Globals.AdjudicateAccount.Address), new ParallelOptions { MaxDegreeOfParallelism = Globals.Signers.Count },
                    node =>
                {
                    try
                    {
                        _ = node.InvokeAsync<bool>("ReceiveBlock", new object?[] { block }, () => new CancellationTokenSource(5000).Token,
                            "ReceiveBlock");
                    }
                    catch { }
                });

                if (HubContext?.Clients != null)
                    try
                    {
                        await HubContext.Clients.All.SendAsync("GetAdjMessage", "taskResult", data,    
                            new CancellationTokenSource(10000).Token);
                    }
                    catch { }
                Now = TimeUtil.GetMillisecondTime();
                ConsoleWriterService.Output("Done sending - Height: " + block.Height.ToString() + " at " + Now);

                await ProcessFortisPoolV3(Globals.TaskAnswerDictV3.Keys.Select(x => x.RBXAddress).ToArray());
                ConsoleWriterService.Output("Fortis Pool Processed");

                foreach (var key in Globals.TaskAnswerDictV3.Keys)
                    if (key.Height <= block.Height)
                        Globals.TaskAnswerDictV3.TryRemove(key, out _);

                Globals.LastAdjudicateTime = TimeUtil.GetTime();
                Globals.BroadcastedTrxDict.Clear();
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Unknown Error: {ex.ToString()}", "ClientCallService.FinalizeWork");
            }
        }

        public static void ClearRoundDicts(long height)
        {
            ConsensusServer.Messages.Clear();
            ConsensusServer.Hashes.Clear();

            foreach (var key in Globals.TaskSelectedNumbersV3.Keys)
                if (key.Height <= height)
                    Globals.TaskSelectedNumbersV3.TryRemove(key, out _);

            foreach (var key in Globals.TaskWinnerDictV3.Keys)
                if (key.Height <= height)
                    Globals.TaskWinnerDictV3.TryRemove(key, out _);           
        }

        public static string SerializeSubmissions((string IPAddress, string RBXAddress, int Answer)[] submissions)
        {
            return string.Join("|", submissions.Select(x => x.IPAddress + ":" + x.RBXAddress + ":" + x.Answer));
        }

        public static (string IPAddress, string RBXAddress, int Answer)[] DeserializeSubmissions(string submisisons)
        {
            return submisisons.Split('|').Where(x => !string.IsNullOrWhiteSpace(x)).Select(x =>
            {
                var split = x.Split(':');
                return (split[0], split[1], int.Parse(split[2]));
            }).ToArray();
        }

        #endregion

        #region Adjudicator Sign Block 

        private async Task<string> AdjudicatorSignBlock(string message, string address)
        {            
            var account = AccountData.GetSingleAccount(address);

            BigInteger b1 = BigInteger.Parse(account.GetKey, NumberStyles.AllowHexSpecifier);//converts hex private key into big int.
            PrivateKey privateKey = new PrivateKey("secp256k1", b1);

            var sig = SignatureService.CreateSignature(message, privateKey, account.PublicKey);

            return sig;
        }

        #endregion

        #region Process Fortis Pool
        public async Task ProcessFortisPoolV2(IList<TaskNumberAnswerV2> taskAnswerList)
        {
            try
            {
                if (taskAnswerList != null)
                {
                    foreach (TaskNumberAnswerV2 taskAnswer in taskAnswerList)
                    {
                        if (Globals.FortisPool.TryGetFromKey2(taskAnswer.Address, out var validator))
                            validator.Value.LastAnswerSendDate = DateTime.UtcNow;
                    }
                }

                var nodeWithAnswer = Globals.FortisPool.Values.Where(x => x.LastAnswerSendDate != null).ToList();
                var deadNodes = nodeWithAnswer.Where(x => x.LastAnswerSendDate.Value.AddMinutes(15) <= DateTime.UtcNow).ToList();
                foreach (var deadNode in deadNodes)
                {
                    Globals.FortisPool.TryRemoveFromKey1(deadNode.IpAddress, out _);
                    deadNode.Context?.Abort();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error: ClientCallService.ProcessFortisPool: " + ex.ToString());
            }
        }

        public static async Task ProcessFortisPoolV3(IList<string> rbxAddressSubmissions)
        {
            try
            {
                if (rbxAddressSubmissions != null)
                {
                    foreach (var address in rbxAddressSubmissions)
                    {
                        if (Globals.FortisPool.TryGetFromKey2(address, out var validator))
                            validator.Value.LastAnswerSendDate = DateTime.UtcNow;
                    }
                }

                var nodeWithAnswer = Globals.FortisPool.Values.Where(x => x.LastAnswerSendDate != null).ToList();
                var deadNodes = nodeWithAnswer.Where(x => x.LastAnswerSendDate.Value.AddMinutes(15) <= DateTime.UtcNow).ToList();
                foreach (var deadNode in deadNodes)
                {
                    Globals.FortisPool.TryRemoveFromKey1(deadNode.IpAddress, out _);
                    deadNode.Context?.Abort();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error: ClientCallService.ProcessFortisPool: " + ex.ToString());
            }
        }

        #endregion

        #region Stop and Dispose

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public void Dispose()
        {            
            _fortisPoolTimer.Dispose();
            _blockStateSyncTimer.Dispose();
            _checkpointTimer.Dispose();
        }

        #endregion
    }
}
