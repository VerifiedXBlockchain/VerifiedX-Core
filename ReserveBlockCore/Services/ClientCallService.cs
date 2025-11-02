using Microsoft.AspNetCore.DataProtection.KeyManagement;
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

            //_consensusBroadcastTimer = new Timer(DoConsensusBroadcastWork, null, TimeSpan.FromSeconds(30),
            //    TimeSpan.FromSeconds(30));

            return Task.CompletedTask;
        }

        #endregion

        public static async Task Broadcast(string messageType, Transaction data, string method = "")
        {
            await HubContext.Clients.All.SendAsync("GetMessage", messageType, data);

            if (method == "") return;

            if (!Globals.Nodes.Any()) return;

            var valNodeList = Globals.Nodes.Values.Where(x => x.IsConnected).ToList();

            if (valNodeList == null || valNodeList.Count() == 0) return;

            foreach (var val in valNodeList)
            {
                try
                {
                    var source = new CancellationTokenSource(2000);
                    await val.Connection.InvokeCoreAsync(method, args: new object?[] { data }, source.Token);
                }
                catch(Exception ex) 
                {
                    ErrorLogUtility.LogError($"Error sending tx: {ex}", "ClientCallService.Broadcast");
                }
                
            }
        }

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
                                string endpoint = Globals.IsTestNet ? "https://data-testnet.verifiedx.io/api/masternodes/send/" : "https://data.verifiedx.io/api/masternodes/send/";
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

                Globals.LastAdjudicateTime = TimeUtil.GetTime();
                Globals.BroadcastedTrxDict.Clear();
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Unknown Error: {ex.ToString()}", "ClientCallService.FinalizeWork");
            }
        }

        // HAL-063: ClearRoundDicts removed - legacy code for Messages/Hashes dictionaries that are no longer used
        public static void ClearRoundDicts(long height)
        {
            // This method is kept as a stub to avoid breaking existing references
            // Legacy clearing of Messages/Hashes has been removed
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
