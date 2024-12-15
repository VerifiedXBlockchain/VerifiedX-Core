﻿using ReserveBlockCore.Extensions;
using Microsoft.AspNetCore.SignalR.Client;
using Newtonsoft.Json;
using ReserveBlockCore.Beacon;
using ReserveBlockCore.Data;
using ReserveBlockCore.EllipticCurve;
using ReserveBlockCore.Models;
using ReserveBlockCore.Nodes;
using ReserveBlockCore.Services;
using ReserveBlockCore.Utilities;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using System.Xml.Linq;
using System.Net;

namespace ReserveBlockCore.P2P
{
    public class P2PClient : IAsyncDisposable, IDisposable
    {
        #region HubConnection Variables        
        /// <summary>
        /// Below are reserved for adjudicators to open up communications fortis pool participation and block solving.
        /// </summary>

        private static HubConnection? hubBeaconConnection; //reserved for beacon

        private static long _MaxHeight = -1;
        public static bool IsBeaconConnected => hubBeaconConnection?.State == HubConnectionState.Connected;

        #endregion

        #region Get Available HubConnections for Peers
        public static async Task RemoveNode(NodeInfo node)
        {
            if(Globals.AdjudicateAccount == null && Globals.Nodes.TryRemove(node.NodeIP, out _) && node.Connection != null)
                await node.Connection.DisposeAsync();            
        }

        #endregion

        #region Check which HubConnections are actively connected

        public static async Task<bool> ArePeersConnected()
        {
            await DropDisconnectedPeers();
            return Globals.Nodes.Any();
        }
        public static async Task DropDisconnectedPeers()
        {
            foreach (var node in Globals.Nodes.Values)
            {
                if(!node.IsConnected)                
                    await RemoveNode(node);
            }
        }
        public static string MostLikelyIP()
        {
            // Function to check if an IP is within private ranges
            bool IsPrivateIP(string ip)
            {
                var ipParts = ip.Split('.').Select(int.Parse).ToArray();
                if (ipParts[0] == 10)
                    return true; // 10.0.0.0/8
                if (ipParts[0] == 172 && ipParts[1] >= 16 && ipParts[1] <= 31)
                    return true; // 172.16.0.0/12
                if (ipParts[0] == 192 && ipParts[1] == 168)
                    return true; // 192.168.0.0/16
                if (ip == "127.0.0.1")
                    return true; // localhost loopback

                return false;
            }

            return Globals.ReportedIPs.Count != 0 ?
                Globals.ReportedIPs
                    .Where(y => !IsPrivateIP(y.Key)) // Filter out private IPs
                    .OrderByDescending(y => y.Value)
                    .Select(y => y.Key)
                    .FirstOrDefault() ?? "NA" : "NA";
        }

        public static async Task DropLowBandwidthPeers()
        {
            if (Globals.AdjudicateAccount != null)
                return;

            await DropDisconnectedPeers();

            var PeersWithSamples = Globals.Nodes.Where(x => x.Value.SendingBlockTime > 60000)
                .Select(x => new
                {
                    Node = x.Value,
                    BandWidth = x.Value.TotalDataSent / ((double)x.Value.SendingBlockTime)
                })
                .OrderBy(x => x.BandWidth)
                .ToArray();

            var Length = PeersWithSamples.Length;
            if (Length < 3)
                return;

            var MedianBandWidth = Length % 2 == 0 ? .5 * (PeersWithSamples[Length / 2 - 1].BandWidth + PeersWithSamples[Length / 2].BandWidth) :
                PeersWithSamples[Length / 2 - 1].BandWidth;

            foreach (var peer in PeersWithSamples.Where(x => x.BandWidth < .5 * MedianBandWidth))
                await RemoveNode(peer.Node);                        
        }

        public static void UpdateMaxHeight(long height)
        {
            _MaxHeight = height;
        }

        public static long MaxHeight()
        {
            return Math.Max(Globals.LastBlock.Height, _MaxHeight);
        }

        #endregion

        #region Hub Dispose
        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        public async ValueTask DisposeAsync()
        {
            await DisposeAsyncCore().ConfigureAwait(false);

            Dispose(disposing: false);
            #pragma warning disable CA1816
            GC.SuppressFinalize(this);
            #pragma warning restore CA1816
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach(var node in Globals.Nodes.Values)
                    if(node.Connection != null)
                        node.Connection.DisposeAsync().ConfigureAwait(false).GetAwaiter().GetResult();                
            }
        }

        protected virtual async ValueTask DisposeAsyncCore()
        {
            foreach (var node in Globals.Nodes.Values)
                if(node.Connection != null)
                    await node.Connection.DisposeAsync();
        }

        #endregion

        #region Hubconnection Connect Methods 1-6

        private static ConcurrentDictionary<string, bool> ConnectLock = new ConcurrentDictionary<string, bool>();
        private static async Task Connect(Peers peer)
        {
            var url = "http://" + peer.PeerIP + ":" + Globals.Port + "/blockchain";
            try
            {
                if (!ConnectLock.TryAdd(url, true))
                    return;
                var hubConnection = new HubConnectionBuilder()
                       .WithUrl(url, options =>
                       {

                       })                       
                       .Build();

                var IPAddress = GetPathUtility.IPFromURL(url);
                hubConnection.On<string, string>("GetMessage", async (message, data) =>
                {                    
                    if (message == "blk" || message == "IP")
                    {
                        if (data?.Length > 1179648)
                            return;

                        if (message != "IP")
                        {
                            await NodeDataProcessor.ProcessData(message, data, IPAddress);
                        }
                        else
                        {
                            var IP = data.ToString();
                            if (Globals.ReportedIPs.TryGetValue(IP, out int Occurrences))
                                Globals.ReportedIPs[IP]++;
                            else
                                Globals.ReportedIPs[IP] = 1;
                        }
                    }                    
                });

                await hubConnection.StartAsync(new CancellationTokenSource(8000).Token);
                if (hubConnection.ConnectionId == null)
                {
                    Globals.SkipPeers.TryAdd(peer.PeerIP, 0);
                    peer.FailCount += 1;
                    if (peer.FailCount > 4)
                        peer.IsOutgoing = false;
                    Peers.GetAll()?.UpdateSafe(peer);
                    return;
                }
                    

                var node = new NodeInfo
                {
                    Connection = hubConnection,
                    NodeIP = IPAddress,
                    NodeHeight = 0,
                    NodeLastChecked = null,
                    NodeLatency = 0,
                    IsSendingBlock = 0,
                    SendingBlockTime = 0,
                    TotalDataSent = 0
                };
                (node.NodeHeight, node.NodeLastChecked, node.NodeLatency) = await GetNodeHeight(hubConnection);

                node.IsValidator = await GetValidatorStatus(node.Connection);
                var walletVersion = await GetWalletVersion(node.Connection);

                if (walletVersion != null)
                {
                    peer.WalletVersion = walletVersion.Substring(0,3);
                    node.WalletVersion = walletVersion.Substring(0,3);

                    Globals.Nodes.TryAdd(IPAddress, node);

                    if (Globals.Nodes.TryGetValue(IPAddress, out var currentNode))
                    {
                        currentNode.Connection = hubConnection;
                        currentNode.NodeIP = IPAddress;
                        currentNode.NodeHeight = node.NodeHeight;
                        currentNode.NodeLastChecked = node.NodeLastChecked;
                        currentNode.NodeLatency = node.NodeLatency;
                    }

                    ConsoleWriterService.OutputSameLine($"Connected to {Globals.Nodes.Count}/{Globals.MaxPeers}");
                    peer.IsOutgoing = true;
                    peer.FailCount = 0; //peer responded. Reset fail count
                    Peers.GetAll()?.UpdateSafe(peer);
                }
                else
                {
                    peer.WalletVersion = "2.1";
                    Peers.GetAll()?.UpdateSafe(peer);
                    //not on latest version. Disconnecting
                    await node.Connection.DisposeAsync();
                }                                
            }
            catch 
            {
                Globals.SkipPeers.TryAdd(peer.PeerIP, 0);
                peer.FailCount += 1;
                if (peer.FailCount > 4)
                    peer.IsOutgoing = false;
                Peers.GetAll()?.UpdateSafe(peer);
            }
            finally
            {
                ConnectLock.TryRemove(url, out _);
            }
        }

        #endregion

        #region Connect Adjudicator

        private static ConcurrentDictionary<string, bool> ConnectAdjudicatorLock = new ConcurrentDictionary<string, bool>();
        public static async Task<bool> ConnectAdjudicator(string url, string address, string time, string uName, string signature)
        {
            var IPAddress = GetPathUtility.IPFromURL(url);
            try
            {
                if (!ConnectAdjudicatorLock.TryAdd(url, true))
                    return false; 
                var hubConnection = new HubConnectionBuilder()
                .WithUrl(url, options => {
                    options.Headers.Add("address", address);
                    options.Headers.Add("time", time);
                    options.Headers.Add("uName", uName);
                    options.Headers.Add("signature", signature);
                    options.Headers.Add("walver", Globals.CLIVersion);

                })       
                .Build();


                LogUtility.Log($"Connecting to Adjudicator {IPAddress}", "ConnectAdjudicator()");                
                hubConnection.Reconnecting += (sender) =>
                {
                    //LogUtility.Log("Reconnecting to Adjudicator", "ConnectAdjudicator()");
                    //ConsoleWriterService.Output("[" + DateTime.Now.ToString() + $"] Connection to adjudicator {IPAddress} lost. Attempting to Reconnect.");
                    return Task.CompletedTask;
                };

                hubConnection.Reconnected += (sender) =>
                {
                    //LogUtility.Log("Success! Reconnected to Adjudicator", "ConnectAdjudicator()");
                    //ConsoleWriterService.Output("[" + DateTime.Now.ToString() + $"] Connection to adjudicator {IPAddress} has been restored.");
                    return Task.CompletedTask;
                };

                hubConnection.Closed += (sender) =>
                {
                    //LogUtility.Log("Closed to Adjudicator", "ConnectAdjudicator()");
                    //ConsoleWriterService.Output("[" + DateTime.Now.ToString() + $"] Connection to adjudicator {IPAddress} has been closed.");
                    return Task.CompletedTask;
                };
                
                hubConnection.On<string, string>("GetAdjMessage", async (message, data) => {
                    if (message == "task" || 
                    message == "taskResult" ||
                    message == "fortisPool" || 
                    message == "status" || 
                    message == "tx" || 
                    message == "badBlock" || 
                    message == "sendWinningBlock" ||
                    message == "disconnect" ||
                    message == "terminate" ||
                    message == "dupIP" ||
                    message == "dupAddr")
                    {
                        switch(message)
                        {
                            case "task":
                                await ValidatorProcessor_BAk.ProcessDatas(message, data, IPAddress);
                                break;
                            case "taskResult":
                                await ValidatorProcessor_BAk.ProcessDatas(message, data, IPAddress);
                                break;
                            case "sendWinningBlock":
                                await ValidatorProcessor_BAk.ProcessDatas(message, data, IPAddress);
                                break;
                            case "fortisPool":
                                if(Globals.AdjudicateAccount == null)
                                    await ValidatorProcessor_BAk.ProcessDatas(message, data, IPAddress);
                                break;
                            case "status":
                                //ConsoleWriterService.Output(data);
                                if (data == "Connected")
                                {
                                    //ValidatorLogUtility.Log("Connected to Validator Pool.", "P2PClient.ConnectAdjudicator()", true);
                                    //LogUtility.Log("Success! Connected to Adjudicator", "ConnectAdjudicator()");
                                }
                                else
                                {
                                    ValidatorLogUtility.Log($"Response from adj: {data}", "P2PClient.ConnectAdjudicator()", true);
                                }
                                break;
                            case "tx":
                                await ValidatorProcessor_BAk.ProcessDatas(message, data, IPAddress);
                                break;
                            case "badBlock":
                                //do something
                                break;
                            case "disconnect":
                                await DisconnectAdjudicators();
                                break;
                            case "terminate":
                                await ValidatorService.DoMasterNodeStop();
                                break;
                            case "dupIP":
                                Globals.DuplicateAdjIP = true;
                                break;
                            case "dupAddr":
                                Globals.DuplicateAdjAddr = true;
                                break;
                        }
                    }
                });

                await hubConnection.StartAsync(new CancellationTokenSource(8000).Token);
                if (string.IsNullOrEmpty(hubConnection.ConnectionId))
                    return false;

                var bench = Globals.AdjBench.Values.Where(x => x.IPAddress == IPAddress).FirstOrDefault();
                if (Globals.AdjNodes.TryGetValue(IPAddress, out var node))
                {
                    node.Connection = hubConnection;
                    node.IpAddress = IPAddress;
                    node.AdjudicatorConnectDate = DateTime.UtcNow;
                    node.Address = bench.RBXAddress;
                }
                else
                {
                    Globals.AdjNodes[IPAddress] = new AdjNodeInfo
                    {
                        Connection = hubConnection,
                        IpAddress = IPAddress,
                        AdjudicatorConnectDate = DateTime.UtcNow,
                        Address = bench.RBXAddress
                };
                }

                ValidatorProcessor_BAk.RandomNumberTaskV3(Globals.LastBlock.Height + 1);

                return true;
            }
            catch (Exception ex)
            {
                ValidatorLogUtility.Log($"Failed! Connecting to Adjudicator {IPAddress}: Reason - " + ex.ToString(), "ConnectAdjudicator()");
            }
            finally
            {
                ConnectAdjudicatorLock.TryRemove(url, out _);
            }

            return false;
        }

        #endregion

        #region Disconnect Adjudicator
        public static async Task DisconnectAdjudicators()
        {
            try
            {
                Globals.ValidatorAddress = "";
                foreach (var node in Globals.AdjNodes.Values)
                    if (node.Connection != null)
                        await node.Connection.DisposeAsync();                    
            }
            catch (Exception ex)
            {
                ValidatorLogUtility.Log("Failed! Did not disconnect from Adjudicator: Reason - " + ex.ToString(), "DisconnectAdjudicator()");
            }
        }


        #endregion

        #region Connect to Peers
        public static async Task<bool> ConnectToPeers()
        {
            await NodeConnector.StartNodeConnecting();
            var peerDB = Peers.GetAll();

            await DropDisconnectedPeers();            

            var SkipIPs = new HashSet<string>(Globals.Nodes.Values.Select(x => x.NodeIP.Replace(":" + Globals.Port, ""))
                .Union(Globals.BannedIPs.Keys)
                .Union(Globals.SkipPeers.Keys)
                .Union(Globals.ReportedIPs.Keys));

            if (Globals.IsTestNet)
                SkipIPs = new HashSet<string>(Globals.Nodes.Values.Select(x => x.NodeIP.Replace(":" + Globals.Port, ""))
                .Union(Globals.BannedIPs.Keys)
                .Union(Globals.SkipPeers.Keys)
                .Union(Globals.ReportedIPs.Keys));

            Random rnd = new Random();
            var newPeers = peerDB.Find(x => x.IsOutgoing == true).ToArray()
                .Where(x => !SkipIPs.Contains(x.PeerIP))
                .ToArray()
                .OrderBy(x => rnd.Next())
                .Concat(peerDB.Find(x => x.IsOutgoing == false).ToArray()
                .Where(x => !SkipIPs.Contains(x.PeerIP))
                .ToArray()
                .OrderBy(x => rnd.Next()))
                .ToArray();

            if(!newPeers.Any())
            {
                SkipIPs = new HashSet<string>(Globals.Nodes.Values.Select(x => x.NodeIP.Replace(":" + Globals.Port, ""))
                .Union(Globals.BannedIPs.Keys));

                newPeers = peerDB.Find(x => x.IsOutgoing == true).ToArray()
                .Where(x => !SkipIPs.Contains(x.PeerIP))
                .ToArray()
                .OrderBy(x => rnd.Next())
                .Concat(peerDB.Find(x => x.IsOutgoing == false).ToArray()
                .Where(x => !SkipIPs.Contains(x.PeerIP))
                .ToArray()
                .OrderBy(x => rnd.Next()))
                .ToArray();
            }

            var Diff = Globals.MaxPeers - Globals.Nodes.Count;
            newPeers.Take(Diff).ToArray().ParallelLoop(peer =>
            {
                _ = Connect(peer);
            });

            return Globals.MaxPeers != 0;         
        }

        public static async Task ManualConnectToPeers(Peers peer)
        {
            _ = Connect(peer);
        }

        public static async Task<bool> ConnectToPeersThroughFortis()
        {
            var fortisList = Globals.FortisPoolCache != "" ? JsonConvert.DeserializeObject<List<string>>(Globals.FortisPoolCache) : new List<string>();
            if(fortisList?.Count() > 0)
            {
                var peerDB = Peers.GetAll();

                await DropDisconnectedPeers();

                var fortisOnlyIPs = new HashSet<string>(fortisList.Select(x => x))
                    .Union(Globals.BannedIPs.Keys)
                    .Union(Globals.ReportedIPs.Keys);

                Random rnd = new Random();
                var newPeers = peerDB.Find(x => x.IsOutgoing == true).ToArray()
                    .Where(x => fortisOnlyIPs.Contains(x.PeerIP))
                    .ToArray()
                    .OrderBy(x => rnd.Next())
                    .Concat(peerDB.Find(x => x.IsOutgoing == false).ToArray()
                    .Where(x => fortisOnlyIPs.Contains(x.PeerIP))
                    .ToArray()
                    .OrderBy(x => rnd.Next()))
                    .ToArray();

                var Diff = Globals.MaxPeers - Globals.Nodes.Count;
                newPeers.Take(Diff).ToArray().ParallelLoop(peer =>
                {
                    _ = Connect(peer);
                });
            }

            return Globals.MaxPeers != 0;
        }
        public static async Task<bool> PingBackPeer(string peerIP)
        {
            try
            {
                var url = "http://" + peerIP + ":" + Globals.Port + "/blockchain";
                var connection = new HubConnectionBuilder().WithUrl(url).Build();
                string response = "";
                await connection.StartAsync();
                response = await connection.InvokeAsync<string>("PingBackPeer");

                if (response == "HelloBackPeer")
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                //peer did not response correctly or at all
                return false;
            }

            return false;
        }



        #endregion

        #region Send Winning Task V3

        public static async Task SendWinningTaskV3(AdjNodeInfo node, string block, long height)
        {
            var now = DateTime.Now;
            for (var i = 1; i < 4; i++)
            {
                try
                {
                    if ((DateTime.Now - now).Milliseconds > 2000)
                        return;
                    var result = await node.InvokeAsync<bool>("ReceiveWinningBlockV3", new object[] { block },
                        () => new CancellationTokenSource(3000).Token);
                    if (result)
                    {
                        node.LastWinningTaskError = false;
                        node.LastWinningTaskSentTime = DateTime.Now;
                        node.LastWinningTaskBlockHeight = height;
                        break;
                    }
                    else
                    {
                        node.LastWinningTaskError = true;
                    }
                }
                catch (Exception ex)
                {
                    node.LastTaskError = true;

                    ValidatorLogUtility.Log("Unhandled Error Sending Task. Check Error Log for more details.", "P2PClient.SendTaskAnswer()");

                    string errorMsg = string.Format("Error Sending Task - {0}. Error Message : {1}", block != null ?
                        DateTime.Now.ToString() : "No Time", ex.ToString());
                    ErrorLogUtility.LogError(errorMsg, "SendTaskAnswer(TaskAnswer taskAnswer)");
                }
            }
            if (node.LastTaskError == true)
            {
                ValidatorLogUtility.Log("Failed to send or receive back from Adjudicator 4 times. Please verify node integrity and crafted blocks.", "P2PClient.SendTaskAnswer()");
            }
        }

        #endregion

        #region Send Task Answer V3

        public static async Task SendTaskAnswerV3(string taskAnswer)
        {
            if (taskAnswer == null || Globals.TimeSyncError) //adding time sync check here.
                return;

            var tasks = new ConcurrentBag<Task>();
            Globals.AdjNodes.Values.Where(x => x.IsConnected).ToArray().ParallelLoop(x =>
            {
                tasks.Add(SendTaskAnswerV3(x, taskAnswer));
            });            

            await Task.WhenAll(tasks);
        }

        private static async Task SendTaskAnswerV3(AdjNodeInfo node, string taskAnswer)
        {
            if (node.LastSentBlockHeight == Globals.LastBlock.Height + 1)
                return;

            Random rand = new Random();
            int randNum = rand.Next(0, 3000);
            for (var i = 1; i < 4; i++)
            {
                if (i == 1)                
                    await Task.Delay(randNum);                                
                try
                {
                    var result = await node.InvokeAsync<TaskAnswerResult>("ReceiveTaskAnswerV3", new object[] { taskAnswer },
                                            () => new CancellationTokenSource(3000).Token);                    
                    if (result != null)
                    {
                        if (result.AnswerAccepted)
                        {
                            node.LastTaskError = false;
                            node.LastTaskSentTime = DateTime.Now;
                            node.LastSentBlockHeight = Globals.LastBlock.Height + 1;
                            node.LastTaskErrorCount = 0;
                            break;
                        }
                        else if(result.AnswerCode == 7)
                        {
                            node.LastTaskSentTime = DateTime.Now;
                            node.LastTaskError = false;
                            node.LastSentBlockHeight = Globals.LastBlock.Height + 1;
                            return;
                        }
                        else
                        {
                            var errorCodeDesc = await TaskAnswerCodeUtility.TaskAnswerCodeReason(result.AnswerCode);
                            if(result.AnswerCode != 6)
                                ConsoleWriterService.Output($"Task was not accpeted: From: {node.IpAddress} Error Code: {result.AnswerCode} - Reason: {errorCodeDesc} Attempt: {i}/3.");
                            ValidatorLogUtility.Log($"Task Answer was not accepted. Error Code: {result.AnswerCode} - Reason: {errorCodeDesc}", "P2PClient.SendTaskAnswer_New()");
                            node.LastTaskError = true;
   
                        }
                    }
                }
                catch (Exception ex)
                {
                    node.LastTaskError = true;

                    ValidatorLogUtility.Log("Unhandled Error Sending Task. Check Error Log for more details.", "P2PClient.SendTaskAnswer()");

                    string errorMsg = string.Format("Error Sending Task - {0}. Error Message : {1}", taskAnswer != null ?
                        taskAnswer : "No Time", ex.ToString());
                    ErrorLogUtility.LogError(errorMsg, "SendTaskAnswer(TaskAnswer taskAnswer)");
                }
            }

            if (node.LastTaskError == true)
            {
                node.LastTaskErrorCount += 1;
                ValidatorLogUtility.Log("Failed to send or receive back from Adjudicator 4 times. Please verify node integrity and crafted blocks.", "P2PClient.SendTaskAnswer()");
            }
        }

        #endregion

        #region Send TX To Adjudicators
        public static async Task SendTXToAdjudicator(Transaction tx)
        {
            if (Globals.AdjudicateAccount != null)
                return;

            var SuccessNodes = new HashSet<string>();
            int failCount = 0;
            int tryCount = 0;
            while (SuccessNodes.Count < 1 && failCount < 3 && tryCount < 5)
            {
                foreach (var node in Globals.AdjNodes.Values.Where(x => !SuccessNodes.Contains(x.Address) && x.IsConnected == true))
                {
                    try
                    {                        
                        if (await node.InvokeAsync<bool>("ReceiveTX", new object?[] { tx }, 
                            () => new CancellationTokenSource(3000).Token))
                            SuccessNodes.Add(node.Address);

                        tryCount += 1;
                    }
                    catch (Exception ex)
                    {
                        failCount += 1;
                    }
                }

                failCount += 1;
            }

            ConsoleWriterService.Output($"Adj Success Count: {SuccessNodes.Count()}");
        }

        #endregion

        #region Get Block Span

        public static async Task<long?> GetBlockSpan(long startHeight, long blockSpanBuffer, NodeInfo node)
        {
            try
            {
                var source = new CancellationTokenSource(10000);
                var blockSpan = await node.Connection.InvokeCoreAsync<long?>("SendBlockSpan", args: new object?[] { startHeight, blockSpanBuffer }, source.Token);

                if (blockSpan == null)
                    return null;

                return blockSpan;
            }
            catch 
            { 

            }
            return null;
        }

        #endregion

        #region Get Block V2

        public static async Task<List<Block>?> GetBlockList((long, long) heightSpan, NodeInfo node) //base example
        {
            var startTime = DateTime.Now;
            long blockSize = 0;

            try
            {
                var source = new CancellationTokenSource(10000);
                var blockSpan = await node.Connection.InvokeCoreAsync<string>("SendBlockList", args: new object?[] { heightSpan.Item1, heightSpan.Item2 }, source.Token);
                if (!string.IsNullOrEmpty(blockSpan))
                {
                    if(blockSpan != "0")
                    {
                        var blockSpanDecompressed = blockSpan.ToDecompress();
                        var blockSpanList = JsonConvert.DeserializeObject<List<Block>>(blockSpanDecompressed);
                        if(blockSpanList?.Count > 0)
                        {
                            return blockSpanList;
                        }
                    }
                    else
                    {
                        return null;
                    }
                }
            }
            catch(Exception ex)
            {
                
            }
            finally
            {
                Interlocked.Exchange(ref node.IsSendingBlock, 0);
                if (node != null)
                {
                    node.TotalDataSent += blockSize;
                    node.SendingBlockTime += (DateTime.Now - startTime).Milliseconds;
                }
            }

            await P2PClient.RemoveNode(node);

            return null;
        }

        #endregion

        #region Get Block
        public static async Task<Block> GetBlock(long height, NodeInfo node) //base example
        {
            //if (Interlocked.Exchange(ref node.IsSendingBlock, 1) != 0)
            //    return null;

            var startTime = DateTime.Now;
            long blockSize = 0;
            Block Block = null;
            try
            {
                var source = new CancellationTokenSource(10000);
                Block = await node.Connection.InvokeCoreAsync<Block>("SendBlock", args: new object?[] { height - 1 }, source.Token);
                if (Block != null)
                {
                    blockSize = Block.Size;
                    if (Block.Height == height)
                        return Block;
                }
            }
            catch { }
            finally
            {
                Interlocked.Exchange(ref node.IsSendingBlock, 0);
                if (node != null)
                {
                    node.TotalDataSent += blockSize;
                    node.SendingBlockTime += (DateTime.Now - startTime).Milliseconds;
                }
            }

            await P2PClient.RemoveNode(node);

            return null;
        }

        #endregion

        #region Get Height of Nodes for Timed Events

        public static async Task<(long, DateTime, int)> GetNodeHeight(HubConnection conn)
        {
            try
            {
                var startTimer = DateTime.UtcNow;
                long remoteNodeHeight = await conn.InvokeAsync<long>("SendBlockHeight");
                var endTimer = DateTime.UtcNow;
                var totalMS = (endTimer - startTimer).Milliseconds;

                return (remoteNodeHeight, startTimer, totalMS); 
            }
            catch { }
            return (-1, DateTime.UtcNow, 0);
        }

        public static async Task UpdateMethodCodes()
        {
            if (Globals.AdjudicateAccount == null)
                return;
            var Address = Globals.AdjudicateAccount.Address;
            var Height = -1L;
            while (true)
            {
                if (Height != Globals.LastBlock.Height)
                {
                    Height = Globals.LastBlock.Height;

                    Globals.Nodes.Values.ToArray().ParallelLoop(node =>
                    {
                        if (node.Address != Address && !UpdateMethodCodeAddresses.ContainsKey(node.NodeIP))
                        {
                            UpdateMethodCodeAddresses[node.NodeIP] = true;
                            _ = UpdateMethodCode(node);
                        }
                    });
                }

                await Task.Delay(1000);
            }
        }

        private static ConcurrentDictionary<string, bool> UpdateMethodCodeAddresses = new ConcurrentDictionary<string, bool>();

        private static async Task UpdateMethodCode(NodeInfo node)
        {
            while (true)
            {                
                try
                {
                    if (!node.IsConnected)
                    {
                        await Task.Delay(20);
                        continue;
                    }
                    
                    var Now = TimeUtil.GetMillisecondTime();
                    var Diff = 1000 - (int)(Now - node.LastMethodCodeTime);
                    if (Diff > 0)
                    {
                        await Task.Delay(Diff);
                        continue;
                    }

                    var state = ConsensusServer.GetState();
                    var Response = await node.Connection.InvokeCoreAsync<string>("RequestMethodCode",
                        new object[] { Globals.LastBlock.Height, state.MethodCode, state.Status == ConsensusStatus.Finalized },
                        new CancellationTokenSource(10000).Token);                         

                    if (Response != null)
                    {
                        var remoteMethodCode = Response.Split(':');
                        if (Now > node.LastMethodCodeTime)
                        {
                            lock (ConsensusServer.UpdateNodeLock)
                            {
                                node.LastMethodCodeTime = Now;
                                node.NodeHeight = long.Parse(remoteMethodCode[0]);
                                node.MethodCode = int.Parse(remoteMethodCode[1]);
                                node.IsFinalized = remoteMethodCode[2] == "1";
                            }

                            ConsensusServer.RemoveStaleCache(node);
                        }
                    }
                }
                catch (OperationCanceledException ex)
                { }
                catch (Exception ex)
                {
                    ErrorLogUtility.LogError(ex.ToString(), "UpdateMethodCodes inner catch");
                }

                await Task.Delay(20);
            }
        }

        public static async Task UpdateNodeHeights()
        {
            if (Globals.AdjudicateAccount == null)            
            {
                foreach (var node in Globals.Nodes.Values)
                    (node.NodeHeight, node.NodeLastChecked, node.NodeLatency) = await GetNodeHeight(node.Connection);
            }
        }

        #endregion

        #region Get Current Height of Nodes
        public static async Task<(bool, long)> GetCurrentHeight()
        {
            bool newHeightFound = false;
            long height = -1;

            while (!await P2PClient.ArePeersConnected())
                await Task.Delay(20);

            var myHeight = Globals.LastBlock.Height;
            await UpdateNodeHeights();

            foreach (var node in Globals.Nodes.Values)
            {
                var remoteNodeHeight = node.NodeHeight;
                if (myHeight < remoteNodeHeight)
                {
                    newHeightFound = true;
                    if (remoteNodeHeight > height)
                    {
                        height = remoteNodeHeight > height ? remoteNodeHeight : height;
                    }
                }
            }

            return (newHeightFound, height);
        }

        #endregion

        #region Connect Beacon ConnectBeacon_New
        public static async Task<bool> ConnectBeacon_New(string url, Beacons beacon, string uplReq = "n", string dwnlReq = "n")
        {
            try
            {
                var beaconRef = Globals.BeaconReference.Reference;
                if (beaconRef == null)
                {
                    return false;
                }

                var beaconHubConnection = new HubConnectionBuilder()
                .WithUrl(url, options => {
                    options.Headers.Add("beaconRef", beaconRef);
                    options.Headers.Add("walver", Globals.CLIVersion);
                    options.Headers.Add("uplReq", uplReq);
                    options.Headers.Add("dwnlReq", dwnlReq);
                })
                .WithAutomaticReconnect()
                .Build();

                LogUtility.Log("Connecting to Beacon", "ConnectBeacon()");

                var ipAddress = GetPathUtility.IPFromURL(url);
                beaconHubConnection.Reconnecting += (sender) =>
                {
                    LogUtility.Log("Reconnecting to Beacon", "ConnectBeacon()");
                    Console.WriteLine("[" + DateTime.Now.ToString() + "] Connection to Beacon lost. Attempting to Reconnect.");
                    return Task.CompletedTask;
                };

                beaconHubConnection.Reconnected += (sender) =>
                {
                    LogUtility.Log("Success! Reconnected to Beacon", "ConnectBeacon()");
                    Console.WriteLine("[" + DateTime.Now.ToString() + "] Connection to Beacon has been restored.");
                    return Task.CompletedTask;
                };

                beaconHubConnection.Closed += (sender) =>
                {
                    LogUtility.Log("Closed to Beacon", "ConnectBeacon()");
                    Console.WriteLine("[" + DateTime.Now.ToString() + "] Connection to Beacon has been closed.");
                    return Task.CompletedTask;
                };

                //Globals.AdjudicatorConnectDate = DateTime.UtcNow;

                beaconHubConnection.On<string, string>("GetBeaconData", async (message, data) => {
                    if (message == "send" ||
                    message == "receive" ||
                    message == "status" ||
                    message == "disconnect")
                    {
                        switch (message)
                        {
                            case "status":
                                Console.WriteLine(data);
                                LogUtility.Log("Success! Connected to Beacon", "ConnectBeacon()");
                                break;
                            case "send":
                                //await BeaconProcessor.ProcessData(message, data);
                                break;
                            case "receive":
                                //await BeaconProcessor.ProcessData(message, data);
                                break;
                            case "disconnect":
                                await DisconnectBeacon();
                                break;
                        }
                    }
                });

                await beaconHubConnection.StartAsync().WaitAsync(new TimeSpan(0,0,3)); // wait 3 seconds to connect. Should happen faster, but giving some room.

                if (string.IsNullOrEmpty(beaconHubConnection.ConnectionId))
                    return false;

                BeaconNodeInfo nBeacon = new BeaconNodeInfo
                {
                    Connection = beaconHubConnection,
                    Beacons = beacon,
                    IPAddress = beacon.IPAddress,
                    ConnectDate = DateTime.UtcNow,
                    LastCallDate = DateTime.UtcNow,
                    Downloading = false,
                    Uploading = false,
                };

                Globals.Beacon[beacon.IPAddress] = nBeacon;

                return true;
            }
            catch (Exception ex)
            {
                ValidatorLogUtility.Log("Failed! Connecting to Adjudicator: Reason - " + ex.ToString(), "ConnectAdjudicator()");
                return false;
            }
        }


        #endregion

        #region Connect Beacon
        public static async Task ConnectBeacon(string url, string uplReq = "n", string dwnlReq = "n")
        {
            try
            {
                var beaconRef = Globals.BeaconReference.Reference;
                if(beaconRef == null)
                {
                    throw new HubException("Cannot connect without a Beacon Reference");
                }

                hubBeaconConnection = new HubConnectionBuilder()
                .WithUrl(url, options => {
                    options.Headers.Add("beaconRef", beaconRef);
                    options.Headers.Add("walver", Globals.CLIVersion);
                    options.Headers.Add("uplReq", uplReq);
                    options.Headers.Add("dwnlReq", dwnlReq);
                })
                .WithAutomaticReconnect()
                .Build();

                LogUtility.Log("Connecting to Beacon", "ConnectBeacon()");

                var ipAddress = GetPathUtility.IPFromURL(url);
                hubBeaconConnection.Reconnecting += (sender) =>
                {
                    LogUtility.Log("Reconnecting to Beacon", "ConnectBeacon()");
                    Console.WriteLine("[" + DateTime.Now.ToString() + "] Connection to Beacon lost. Attempting to Reconnect.");
                    return Task.CompletedTask;
                };

                hubBeaconConnection.Reconnected += (sender) =>
                {
                    LogUtility.Log("Success! Reconnected to Beacon", "ConnectBeacon()");
                    Console.WriteLine("[" + DateTime.Now.ToString() + "] Connection to Beacon has been restored.");
                    return Task.CompletedTask;
                };

                hubBeaconConnection.Closed += (sender) =>
                {
                    LogUtility.Log("Closed to Beacon", "ConnectBeacon()");
                    Console.WriteLine("[" + DateTime.Now.ToString() + "] Connection to Beacon has been closed.");
                    return Task.CompletedTask;
                };

                //Globals.AdjudicatorConnectDate = DateTime.UtcNow;

                hubBeaconConnection.On<string, string>("GetBeaconData", async (message, data) => {
                    if (message == "send" ||
                    message == "receive" ||
                    message == "status" ||
                    message == "disconnect")
                    {
                        switch (message)
                        {
                            case "status":
                                Console.WriteLine(data);
                                LogUtility.Log("Success! Connected to Beacon", "ConnectBeacon()");
                                break;
                            case "send":
                                await BeaconProcessor.ProcessData(message, data);
                                break;
                            case "receive":
                                await BeaconProcessor.ProcessData(message, data);
                                break;
                            case "disconnect":
                                await DisconnectBeacon();
                                break;
                        }
                    }
                });

                await hubBeaconConnection.StartAsync();

            }
            catch (Exception ex)
            {
                ValidatorLogUtility.Log("Failed! Connecting to Adjudicator: Reason - " + ex.ToString(), "ConnectAdjudicator()");
            }
        }


        #endregion

        #region Disconnect Beacon
        public static async Task DisconnectBeacon()
        {
            try
            {
                if (hubBeaconConnection != null)
                {
                    if (IsBeaconConnected)
                    {
                        await hubBeaconConnection.DisposeAsync();
                        ConsoleWriterService.Output($"Success! Disconnected from Beacon on: {DateTime.Now}");
                    }
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError("Failed! Did not disconnect from Beacon: Reason - " + ex.ToString(), "DisconnectBeacon()");
            }
        }


        #endregion

        #region File Upload To Beacon Beacon NEW

        public static async Task<bool> BeaconUploadRequest(BeaconNodeInfo beaconNode, List<string> assets, string scUID, string nextOwnerAddress, string md5List, string preSigned = "NA")
        {
            bool result = false;
            string signature = "";
            string locatorRetString = "";
            var scState = SmartContractStateTrei.GetSmartContractState(scUID);
            var beaconRef = await BeaconReference.GetReference();

            if (beaconRef == null)
            {
                return result;
            }

            if (scState == null)
            {
                return result; // SC does not exist
            }
            else
            {
                if (preSigned != "NA")
                {
                    signature = preSigned;
                }
                else
                {
                    var account = AccountData.GetSingleAccount(scState.OwnerAddress);
                    if (account != null)
                    {
                        BigInteger b1 = BigInteger.Parse(account.GetKey, NumberStyles.AllowHexSpecifier);//converts hex private key into big int.
                        PrivateKey privateKey = new PrivateKey("secp256k1", b1);

                        signature = SignatureService.CreateSignature(scUID, privateKey, account.PublicKey);
                    }
                    else
                    {
                        return result;
                    }
                }

            }

            //send file size, beacon will reply if it is ok to send.
            var bsd = new BeaconData.BeaconSendData
            {
                CurrentOwnerAddress = scState.OwnerAddress,
                Assets = assets,
                SmartContractUID = scUID,
                Signature = signature,
                NextAssetOwnerAddress = nextOwnerAddress,
                Reference = beaconRef,
                MD5List = md5List,
            };

            try
            {
                var beaconString = beaconNode.Beacons.BeaconLocator.ToStringFromBase64();
                var beacon = JsonConvert.DeserializeObject<BeaconInfo.BeaconInfoJson>(beaconString);
                if (beacon != null)
                {
                    var url = "http://" + beacon.IPAddress + ":" + Globals.Port + "/beacon";
                    if (!beaconNode.IsConnected)
                        await ConnectBeacon_New(url, beaconNode.Beacons, "y");
                }

                if (beaconNode.IsConnected)
                {
                    if (beaconNode.Connection != null)
                    {
                        var response = await beaconNode.Connection.InvokeCoreAsync<bool>("ReceiveUploadRequest", args: new object?[] { bsd });
                        if (!response)
                        {
                            var errorMsg = string.Format("Failed to talk to beacon.");
                            ErrorLogUtility.LogError(errorMsg, "P2PClient.BeaconUploadRequest(List<BeaconInfo.BeaconInfoJson> locators, List<string> assets, string scUID) - try");
                        }

                        Globals.Beacon.TryGetValue(beaconNode.IPAddress, out var globalBeacon);
                        if(globalBeacon != null)
                        {
                            globalBeacon.Uploading= true;
                            globalBeacon.LastCallDate = DateTime.UtcNow;
                            Globals.Beacon[beaconNode.IPAddress] = globalBeacon;
                        }
                        result = true;
                    }
                }
                else
                {
                    //failed to connect. Cancel TX
                    if (beacon != null)
                    {
                        SCLogUtility.Log($"Failed to connect to beacon. Beacon Info: {beacon.Name} - {beacon.IPAddress}", "P2PClient.BeaconUploadRequest()");
                    }
                    else
                    {
                        SCLogUtility.Log($"Failed to connect to beacon. Beacon was null.", "P2PClient.BeaconUploadRequest()");
                    }

                    return result;
                }

            }
            catch (Exception ex)
            {
                var errorMsg = string.Format("Failed to send bsd to Beacon. Error Message : {0}", ex.ToString());
                ErrorLogUtility.LogError(errorMsg, "P2PClient.BeaconUploadRequest(List<BeaconInfo.BeaconInfoJson> locators, List<string> assets, string scUID) - catch");
            }


            return result;
        }

        #endregion

        #region File Download from Beacon - BeaconAccessRequest

        public static async Task<bool> BeaconDownloadRequest(List<string> locators, List<string> assets, string scUID, string preSigned = "NA")
        {
            var result = false;
            string signature = "";
            string locatorRetString = "";
            var beaconRef = await BeaconReference.GetReference();

            if (beaconRef == null)
            {
                return false;
            }

            var scState = SmartContractStateTrei.GetSmartContractState(scUID);
            if (scState == null)
            {
                return false; // SC does not exist
            }
            else
            {
                if(preSigned != "NA")
                {
                    signature = preSigned;
                }
                else
                {
                    var account = AccountData.GetSingleAccount(scState.OwnerAddress);
                    if (account == null && scState.NextOwner != null)
                        account = AccountData.GetSingleAccount(scState.NextOwner);

                    if (account != null)
                    {
                        BigInteger b1 = BigInteger.Parse(account.GetKey, NumberStyles.AllowHexSpecifier);//converts hex private key into big int.
                        PrivateKey privateKey = new PrivateKey("secp256k1", b1);

                        signature = SignatureService.CreateSignature(scUID, privateKey, account.PublicKey);
                    }
                    else
                    {
                        return false;
                    }
                }
            }

            var bdd = new BeaconData.BeaconDownloadData
            {
                Assets = assets,
                SmartContractUID = scUID,
                Signature = signature,
                Reference = beaconRef
            };

            foreach (var asset in bdd.Assets)
            {
                var path = NFTAssetFileUtility.CreateNFTAssetPath(asset, bdd.SmartContractUID);
            }

            foreach (var locator in locators)
            {
                try
                {
                    var beaconString = locator.ToStringFromBase64();
                    var beacon = JsonConvert.DeserializeObject<BeaconInfo.BeaconInfoJson>(beaconString);
                    if (beacon != null)
                    {
                        Globals.Beacons.TryGetValue(beacon.IPAddress, out var globalBeacon);
                        if (globalBeacon == null)
                        {
                            var beaconDb = Beacons.GetBeacons();
                            if (beaconDb != null)
                            {
                                var beaconCheck = beaconDb.Query().Where(x => x.IPAddress == beacon.IPAddress).FirstOrDefault();
                                if (beaconCheck == null)
                                {
                                    BeaconInfo.BeaconInfoJson beaconLoc1 = new BeaconInfo.BeaconInfoJson
                                    {
                                        IPAddress = beacon.IPAddress,
                                        Port = beacon.Port,
                                        Name = beacon.Name,
                                        BeaconUID = beacon.BeaconUID
                                    };

                                    var beaconLocJson1 = JsonConvert.SerializeObject(beaconLoc1);
                                    //Globals.Locators.TryAdd(beaconLoc1.BeaconUID, beaconLocJson1.ToBase64());
                                    Beacons beacon1 = new Beacons
                                    {
                                        IPAddress = beacon.IPAddress,
                                        Name = beacon.Name,
                                        Port = beacon.Port,
                                        BeaconUID = beacon.BeaconUID,
                                        AutoDeleteAfterDownload = true,
                                        FileCachePeriodDays = 2,
                                        IsPrivateBeacon = false,
                                        SelfBeacon = false,
                                        SelfBeaconActive = false,
                                        BeaconLocator = beaconLocJson1.ToBase64(),
                                    };

                                    beaconDb.InsertSafe(beacon1);

                                    Globals.Beacons.TryAdd(beacon1.IPAddress, beacon1);
                                }
                                else
                                {
                                    Globals.Beacons.TryAdd(beacon.IPAddress, beaconCheck);
                                }
                            }

                        }
                        var beaconNodeInfo = new BeaconNodeInfo();
                        Globals.Beacon.TryGetValue(beacon.IPAddress, out beaconNodeInfo);
                        var url = "http://" + beacon.IPAddress + ":" + Globals.Port + "/beacon";

                        if (beaconNodeInfo != null)
                        {
                            if (!beaconNodeInfo.IsConnected)
                            {
                                var connectionResult = await ConnectBeacon_New(url, beaconNodeInfo.Beacons, "n", "y");
                            }
                        }
                        else
                        {
                            Globals.Beacons.TryGetValue(beacon.IPAddress, out var beaconInfo);
                            if (beaconInfo != null)
                            {
                                var connectionResult = await ConnectBeacon_New(url, beaconInfo, "n", "y");
                                if (connectionResult)
                                {
                                    Globals.Beacon.TryGetValue(beacon.IPAddress, out beaconNodeInfo);
                                }
                            }

                        }

                        if (beaconNodeInfo?.Connection != null)
                        {
                            //Remove this. Just for testing!
                            Console.WriteLine($"Download request: {bdd.Assets} SCUID: {bdd.SmartContractUID} Signature: {bdd.Signature} Ref: {bdd.Reference}");
                            var response = await beaconNodeInfo.Connection.InvokeCoreAsync<bool>("ReceiveDownloadRequest", args: new object?[] { bdd });
                            if (response != true)
                            {
                                var errorMsg = string.Format("Failed to talk to beacon.");
                                ErrorLogUtility.LogError(errorMsg, "P2PClient.BeaconDownloadRequest() - try");
                            }
                            else
                            {

                                beaconNodeInfo.Downloading = true;
                                beaconNodeInfo.LastCallDate = DateTime.UtcNow;
                                Globals.Beacon[beaconNodeInfo.IPAddress] = beaconNodeInfo;
                                result = true;
                            }
                        }
                    }
                }
                catch(Exception ex)
                {
                    var errorMsg = string.Format("Failed to send bdd to Beacon. Error Message : {0}", ex.ToString());
                    Console.WriteLine(errorMsg);
                    ErrorLogUtility.LogError(errorMsg, "P2PClient.BeaconDownloadRequest() - catch");
                }
            }

            return result;
        }

        #endregion

        #region Beacon IsReady Flag send
        public static async Task BeaconFileIsReady(string scUID, string assetName, string locator)
        {
            try
            {
                string[] payload = { scUID, assetName };
                var payloadJson = JsonConvert.SerializeObject(payload);

                var beaconString = locator.ToStringFromBase64();
                var beacon = JsonConvert.DeserializeObject<BeaconInfo.BeaconInfoJson>(beaconString);
                if (beacon != null)
                {
                    Globals.Beacons.TryGetValue(beacon.IPAddress, out var globalBeacon);
                    if (globalBeacon == null)
                    {
                        var beaconDb = Beacons.GetBeacons();
                        if (beaconDb != null)
                        {
                            var beaconCheck = beaconDb.Query().Where(x => x.IPAddress == beacon.IPAddress).FirstOrDefault();
                            if (beaconCheck == null)
                            {
                                BeaconInfo.BeaconInfoJson beaconLoc1 = new BeaconInfo.BeaconInfoJson
                                {
                                    IPAddress = beacon.IPAddress,
                                    Port = beacon.Port,
                                    Name = beacon.Name,
                                    BeaconUID = beacon.BeaconUID
                                };

                                var beaconLocJson1 = JsonConvert.SerializeObject(beaconLoc1);
                                //Globals.Locators.TryAdd(beaconLoc1.BeaconUID, beaconLocJson1.ToBase64());
                                Beacons beacon1 = new Beacons
                                {
                                    IPAddress = beacon.IPAddress,
                                    Name = beacon.Name,
                                    Port = beacon.Port,
                                    BeaconUID = beacon.BeaconUID,
                                    AutoDeleteAfterDownload = true,
                                    FileCachePeriodDays = 2,
                                    IsPrivateBeacon = false,
                                    SelfBeacon = false,
                                    SelfBeaconActive = false,
                                    BeaconLocator = beaconLocJson1.ToBase64(),
                                };

                                beaconDb.InsertSafe(beacon1);

                                Globals.Beacons.TryAdd(beacon1.IPAddress, beacon1);
                            }
                            else
                            {
                                Globals.Beacons.TryAdd(beacon.IPAddress, beaconCheck);
                            }
                        }

                    }

                    var beaconNodeInfo = new BeaconNodeInfo();
                    Globals.Beacon.TryGetValue(beacon.IPAddress, out beaconNodeInfo);
                    var url = "http://" + beacon.IPAddress + ":" + Globals.Port + "/beacon";
                    if (beaconNodeInfo != null)
                    {
                        if (!beaconNodeInfo.IsConnected)
                        {
                            var connectionResult = await ConnectBeacon_New(url, beaconNodeInfo.Beacons, "n", "y");
                        }
                    }
                    else
                    {
                        Globals.Beacons.TryGetValue(beacon.IPAddress, out var beaconInfo);
                        if (beaconInfo != null)
                        {
                            var connectionResult = await ConnectBeacon_New(url, beaconInfo, "n", "y");
                            if (connectionResult)
                            {
                                Globals.Beacon.TryGetValue(beacon.IPAddress, out beaconNodeInfo);
                            }
                        }

                    }

                    if (beaconNodeInfo?.Connection != null)
                    {
                        var response = await beaconNodeInfo.Connection.InvokeCoreAsync<bool>("BeaconDataIsReady", args: new object?[] { payloadJson });
                        if (!response)
                        {
                            var errorMsg = string.Format("Failed to talk to beacon.");
                            SCLogUtility.Log(errorMsg, "P2PClient.BeaconFileIsReady() - try");
                        }
                    }
                }
            }
            catch(Exception ex)
            {
                ErrorLogUtility.LogError($"Unknown Error: {ex.ToString()}", "P2PClient.BeaconFileIsReady() - catch");
            }

        }

        #endregion

        #region Beacon Is File Ready
        public static async Task<bool> BeaconFileReadyCheck(string scUID, string assetName, string locator)
        {
            bool result = false;
            try
            {
                string[] payload = { scUID, assetName };
                var payloadJson = JsonConvert.SerializeObject(payload);

                var beaconString = locator.ToStringFromBase64();
                var beacon = JsonConvert.DeserializeObject<BeaconInfo.BeaconInfoJson>(beaconString);
                if (beacon != null)
                {
                    Globals.Beacons.TryGetValue(beacon.IPAddress, out var globalBeacon);
                    if (globalBeacon == null)
                    {
                        var beaconDb = Beacons.GetBeacons();
                        if (beaconDb != null)
                        {
                            var beaconCheck = beaconDb.Query().Where(x => x.IPAddress == beacon.IPAddress).FirstOrDefault();
                            if (beaconCheck == null)
                            {
                                BeaconInfo.BeaconInfoJson beaconLoc1 = new BeaconInfo.BeaconInfoJson
                                {
                                    IPAddress = beacon.IPAddress,
                                    Port = beacon.Port,
                                    Name = beacon.Name,
                                    BeaconUID = beacon.BeaconUID
                                };

                                var beaconLocJson1 = JsonConvert.SerializeObject(beaconLoc1);
                                //Globals.Locators.TryAdd(beaconLoc1.BeaconUID, beaconLocJson1.ToBase64());
                                Beacons beacon1 = new Beacons
                                {
                                    IPAddress = beacon.IPAddress,
                                    Name = beacon.Name,
                                    Port = beacon.Port,
                                    BeaconUID = beacon.BeaconUID,
                                    AutoDeleteAfterDownload = true,
                                    FileCachePeriodDays = 2,
                                    IsPrivateBeacon = false,
                                    SelfBeacon = false,
                                    SelfBeaconActive = false,
                                    BeaconLocator = beaconLocJson1.ToBase64(),
                                };

                                beaconDb.InsertSafe(beacon1);

                                Globals.Beacons.TryAdd(beacon1.IPAddress, beacon1);
                            }
                            else
                            {
                                Globals.Beacons.TryAdd(beacon.IPAddress, beaconCheck);
                            }
                        }

                    }

                    var beaconNodeInfo = new BeaconNodeInfo();
                    Globals.Beacon.TryGetValue(beacon.IPAddress, out beaconNodeInfo);
                    var url = "http://" + beacon.IPAddress + ":" + Globals.Port + "/beacon";
                    if (beaconNodeInfo != null)
                    {
                        if (!beaconNodeInfo.IsConnected)
                        {
                            var connectionResult = await ConnectBeacon_New(url, beaconNodeInfo.Beacons, "n", "y");
                        }
                    }
                    else
                    {
                        Globals.Beacons.TryGetValue(beacon.IPAddress, out var beaconInfo);
                        if (beaconInfo != null)
                        {
                            var connectionResult = await ConnectBeacon_New(url, beaconInfo, "n", "y");
                            if (connectionResult)
                            {
                                Globals.Beacon.TryGetValue(beacon.IPAddress, out beaconNodeInfo);
                            }
                        }

                    }
                    if (beaconNodeInfo?.Connection != null)
                    {
                        var response = await beaconNodeInfo.Connection.InvokeCoreAsync<bool>("BeaconIsFileReady", args: new object?[] { payloadJson });
                        return response;
                    }
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Unknown Error: {ex.ToString()}", "P2PClient.BeaconFileIsReady() - catch");
            }

            return result;

        }

        #endregion

        #region Beacon File Is Downloaded
        public static async Task<bool> BeaconFileIsDownloaded(string scUID, string assetName, string locator)
        {
            bool result = false;
            try
            {
                string[] payload = { scUID, assetName };
                var payloadJson = JsonConvert.SerializeObject(payload);

                var beaconString = locator.ToStringFromBase64();
                var beacon = JsonConvert.DeserializeObject<BeaconInfo.BeaconInfoJson>(beaconString);
                if (beacon != null)
                {
                    Globals.Beacons.TryGetValue(beacon.IPAddress, out var globalBeacon);
                    if (globalBeacon == null)
                    {
                        var beaconDb = Beacons.GetBeacons();
                        if (beaconDb != null)
                        {
                            var beaconCheck = beaconDb.Query().Where(x => x.IPAddress == beacon.IPAddress).FirstOrDefault();
                            if (beaconCheck == null)
                            {
                                BeaconInfo.BeaconInfoJson beaconLoc1 = new BeaconInfo.BeaconInfoJson
                                {
                                    IPAddress = beacon.IPAddress,
                                    Port = beacon.Port,
                                    Name = beacon.Name,
                                    BeaconUID = beacon.BeaconUID
                                };

                                var beaconLocJson1 = JsonConvert.SerializeObject(beaconLoc1);
                                //Globals.Locators.TryAdd(beaconLoc1.BeaconUID, beaconLocJson1.ToBase64());
                                Beacons beacon1 = new Beacons
                                {
                                    IPAddress = beacon.IPAddress,
                                    Name = beacon.Name,
                                    Port = beacon.Port,
                                    BeaconUID = beacon.BeaconUID,
                                    AutoDeleteAfterDownload = true,
                                    FileCachePeriodDays = 2,
                                    IsPrivateBeacon = false,
                                    SelfBeacon = false,
                                    SelfBeaconActive = false,
                                    BeaconLocator = beaconLocJson1.ToBase64(),
                                };

                                beaconDb.InsertSafe(beacon1);

                                Globals.Beacons.TryAdd(beacon1.IPAddress, beacon1);
                            }
                            else
                            {
                                Globals.Beacons.TryAdd(beacon.IPAddress, beaconCheck);
                            }
                        }

                    }

                    var beaconNodeInfo = new BeaconNodeInfo();
                    Globals.Beacon.TryGetValue(beacon.IPAddress, out beaconNodeInfo);
                    var url = "http://" + beacon.IPAddress + ":" + Globals.Port + "/beacon";
                    if (beaconNodeInfo != null)
                    {
                        if (!beaconNodeInfo.IsConnected)
                        {
                            var connectionResult = await ConnectBeacon_New(url, beaconNodeInfo.Beacons, "n", "y");
                        }
                    }
                    else
                    {
                        Globals.Beacons.TryGetValue(beacon.IPAddress, out var beaconInfo);
                        if (beaconInfo != null)
                        {
                            var connectionResult = await ConnectBeacon_New(url, beaconInfo, "n", "y");
                            if (connectionResult)
                            {
                                Globals.Beacon.TryGetValue(beacon.IPAddress, out beaconNodeInfo);
                            }
                        }

                    }

                    if (beaconNodeInfo?.Connection != null)
                    {
                        var response = await beaconNodeInfo.Connection.InvokeCoreAsync<bool>("BeaconFileIsDownloaded", args: new object?[] { payloadJson });
                        return response;
                    }
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Unknown Error: {ex.Message}", "P2PClient.BeaconFileIsDownloaded() - catch");
            }

            return result;

        }

        #endregion

        #region Get Lead Adjudicators
        public static async Task<Adjudicators?> GetLeadAdjudicator()
        {
            Adjudicators? LeadAdj = null;

            var peersConnected = await P2PClient.ArePeersConnected();

            if (!peersConnected)
            {
                //Need peers
                return null;
            }
            else
            {
                var myHeight = Globals.LastBlock.Height;

                foreach(var node in Globals.Nodes.Values)
                {
                    try
                    {
                        var leadAdjudictor = await node.InvokeAsync<Adjudicators>("SendLeadAdjudicator", Array.Empty<object>(),
                            () => new CancellationTokenSource(3000).Token, "SendLeadAdjudicator");                        

                        if (leadAdjudictor != null)
                        {
                            var adjudicators = Adjudicators.AdjudicatorData.GetAll();
                            if (adjudicators != null)
                            {
                                var lAdj = adjudicators.FindOne(x => x.IsLeadAdjuidcator == true);
                                if (lAdj == null)
                                {
                                    adjudicators.InsertSafe(leadAdjudictor);
                                    LeadAdj = leadAdjudictor;
                                }
                            }
                        }                        
                    }
                    catch (Exception ex)
                    {
                        //node is offline
                    }
                }
            }
            return LeadAdj;
        }

        #endregion

        #region Send Transactions to mempool 
        public static async Task SendTXMempool(Transaction txSend)
        {
            var peersConnected = await ArePeersConnected();

            if (!peersConnected)
            {
                //Need peers
                Console.WriteLine("Failed to broadcast Transaction. No peers are connected to you.");
                LogUtility.Log("TX failed. No Peers: " + txSend.Hash, "P2PClient.SendTXMempool()");
            }
            else
            {
                var valAdjNodes = Globals.Nodes.Values.Where(x => x.IsValidator).ToList();
                if (valAdjNodes.Count() > 0)
                {
                    var successCount = 0;
                    foreach (var node in valAdjNodes)
                    {
                        try
                        {
                            string message = Globals.AdjudicateAccount == null ? await node.InvokeAsync<string>("SendTxToMempool", new object?[] { txSend },
                                () => new CancellationTokenSource(3000).Token, "SendTxToMempool") : await node.Connection.InvokeCoreAsync
                                <string>("SendTxToMempool", new object?[] { txSend }, new CancellationTokenSource(3000).Token);

                            if (message == "ATMP")
                            {
                                //success
                                successCount += 1;
                            }
                            else if (message == "TFVP")
                            {
                                if(successCount == 0)
                                    Console.WriteLine("Transaction Failed Verification Process on remote node");
                            }
                            else
                            {
                                //already in mempool
                            }
                        }
                        catch (Exception ex)
                        {

                        }
                    }
                }
                else
                {
                    //need method to drop at least 2-3 peers to find validators in event client is not connected to any.
                    Console.WriteLine("You have no validator peers connected to you. Close wallet and attempt to reconnect.");
                    ErrorLogUtility.LogError("You have no validator peers connected to you. Close wallet and attempt to reconnect.", "P2PClient.SendTXMempool()");
                }
            }
        }

        #endregion

        #region Broadcast Blocks to Peers
        public static async Task BroadcastBlock(Block block)
        {
            var peersConnected = await P2PClient.ArePeersConnected();

            if (!peersConnected)
            {
                //Need peers
                Console.WriteLine("Failed to broadcast Transaction. No peers are connected to you.");
            }
            else
            {                
                foreach (var node in Globals.Nodes.Values)
                {
                    try
                    {                        
                        _ = node.InvokeAsync<bool>("ReceiveBlock", new object?[] { block }, () => new CancellationTokenSource(5000).Token, "ReceiveBlock");
                    }
                    catch (Exception ex)
                    {
                        //possible dead connection, or node is offline
                        Console.WriteLine("Error Sending Transaction. Please try again!");
                    }
                }
            }
        }
        #endregion

        #region Get Validator Status
        private static async Task<bool> GetValidatorStatus(HubConnection hubConnection)
        {
            var result = false;
            try
            {
                result = await hubConnection.InvokeAsync<bool>("GetValidatorStatus");
            }
            catch { }

            return result;
        }

        #endregion

        #region Get Adjudicator Status
        private static async Task<bool> GetAdjudicatorStatus(HubConnection hubConnection)
        {
            var result = false;
            try
            {
                result = await hubConnection.InvokeAsync<bool>("GetAdjudicatorStatus");
            }
            catch { }

            return result;
        }

        #endregion

        #region Get Node Wallet Version
        private static async Task<string?> GetWalletVersion(HubConnection hubConnection)
        {
            try
            {
                var result = await hubConnection.InvokeAsync<string>("GetWalletVersion");
                if (result != null)
                    return result;
            }
            catch { }

            return null;
        }

        #endregion
    }
}
