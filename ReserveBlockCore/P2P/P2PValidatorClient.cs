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
using System.Reflection.Metadata.Ecma335;

namespace ReserveBlockCore.P2P
{
    public class P2PValidatorClient : P2PClient
    {
        #region HubConnection Variables        

        private static long _MaxHeight = -1;

        #endregion

        #region Remove Node from Validator Nodes
        public static async Task RemoveNode(NodeInfo node)
        {
            if(!string.IsNullOrEmpty(Globals.ValidatorAddress) && Globals.ValidatorNodes.TryRemove(node.NodeIP, out _) && node.Connection != null)
                await node.Connection.DisposeAsync();            
        }

        #endregion

        #region Check which HubConnections are actively connected

        public static async Task<bool> AreValidatorsConnected()
        {
            await DropDisconnectedValidator();
            return Globals.ValidatorNodes.Any();
        }

        public static async Task DropDisconnectedValidator()
        {
            foreach (var node in Globals.ValidatorNodes.Values)
            {
                if (!node.IsConnected)
                    await RemoveNode(node);
            }
        }

        public static string MostLikelyIP()
        {
            return Globals.ReportedIPs.Count != 0 ?
                Globals.ReportedIPs.OrderByDescending(y => y.Value).Select(y => y.Key).First() : "NA";
        }

        //This will need work
        public static async Task DropLowBandwidthPeers()
        {
            if (Globals.AdjudicateAccount != null)
                return;

            await DropDisconnectedValidator();

            var PeersWithSamples = Globals.ValidatorNodes.Where(x => x.Value.SendingBlockTime > 60000)
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
                foreach(var node in Globals.ValidatorNodes.Values)
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

        #region Connect

        private static ConcurrentDictionary<string, bool> ConnectLock = new ConcurrentDictionary<string, bool>();
        private static async Task Connect(Peers peer)
        {
            var url = "http://" + peer.PeerIP.Replace("::ffff:", "") + ":" + Globals.ValPort + "/consensus";
            try
            {
                if (!ConnectLock.TryAdd(url, true))
                    return;

                var account = AccountData.GetLocalValidator();
                var validators = Validators.Validator.GetAll();
                var validator = validators.FindOne(x => x.Address == account.Address);
                if (validator == null)
                    return;

                var time = TimeUtil.GetTime().ToString();
                var signature = SignatureService.ValidatorSignature(validator.Address + ":" + time + ":" + account.PublicKey);

                var hubConnection = new HubConnectionBuilder()
                       .WithUrl(url, options =>
                       {
                           options.Headers.Add("address", validator.Address);
                           options.Headers.Add("time", time);
                           options.Headers.Add("uName", validator.UniqueName);
                           options.Headers.Add("signature", signature);
                           options.Headers.Add("walver", Globals.CLIVersion);
                           options.Headers.Add("publicKey", account.PublicKey);
                           options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.WebSockets;
                           options.SkipNegotiation = false; // Important: Allow negotiation
                           options.WebSocketConfiguration = conf => {
                               conf.RemoteCertificateValidationCallback = (sender, cert, chain, errors) => true;
                           };
                       })                       
                       .Build();

                var IPAddress = GetPathUtility.IPFromURL(url);
                hubConnection.On<string, string>("GetValMessage", async (message, data) =>
                {
                    _ = ValidatorNode.ProcessData(message, data, IPAddress);
                });

                await hubConnection.StartAsync(new CancellationTokenSource(8000).Token);
                if (hubConnection.ConnectionId == null)
                {
                    Globals.SkipValPeers.TryAdd(peer.PeerIP, 0);
                    peer.FailCount += 1;
                    if (peer.FailCount > 600)
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

                    Globals.ValidatorNodes.TryAdd(IPAddress, node);

                    if (Globals.ValidatorNodes.TryGetValue(IPAddress, out var currentNode))
                    {
                        currentNode.Connection = hubConnection;
                        currentNode.NodeIP = IPAddress;
                        currentNode.NodeHeight = node.NodeHeight;
                        currentNode.NodeLastChecked = node.NodeLastChecked;
                        currentNode.NodeLatency = node.NodeLatency;
                    }

                    ConsoleWriterService.OutputSameLine($"Connected to Validators {Globals.ValidatorNodes.Count}/{Globals.MaxValPeers}");
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
                Globals.SkipValPeers.TryAdd(peer.PeerIP, 0);
                peer.FailCount += 1;
                if (peer.FailCount > 600)
                    peer.IsOutgoing = false;
                Peers.GetAll()?.UpdateSafe(peer);
            }
            finally
            {
                ConnectLock.TryRemove(url, out _);
            }
        }

        #endregion


        #region Connect to Validators
        public static async Task<bool> ConnectToValidators()
        {
            await NodeConnector.StartNodeConnecting(); //TODO: update this for validator peers!
            var peerDB = Peers.GetAll();

            await DropDisconnectedValidator();

            //Force unban quicker
            await BanService.RunUnban();

            var SkipIPs = new HashSet<string>(Globals.ValidatorNodes.Values.Select(x => x.NodeIP.Replace(":" + Globals.Port, ""))
                .Union(Globals.BannedIPs.Keys)
                .Union(Globals.SkipValPeers.Keys)
                .Union(Globals.ReportedIPs.Keys));

            var connectedNodes = Globals.ValidatorNodes.Values.Where(x => x.IsConnected).ToArray();

            foreach (var validator in connectedNodes)
            {
                SkipIPs.Add(validator.NodeIP);
            }

            if (Globals.ValidatorAddress == "xMpa8DxDLdC9SQPcAFBc2vqwyPsoFtrWyC")
            {
                SkipIPs.Add("66.94.124.2");
            }

            Random rnd = new Random();
            var newPeers = peerDB.Find(x => x.IsValidator).ToArray()
                .Where(x => !SkipIPs.Contains(x.PeerIP))
                .ToArray()
                .OrderBy(x => rnd.Next())
                .ThenBy(x => x.FailCount)
                .ToArray();

            if(!newPeers.Any())
            {
                //clear out skipped peers to try again
                Globals.SkipValPeers.Clear();

                SkipIPs = new HashSet<string>(Globals.ValidatorNodes.Values.Select(x => x.NodeIP.Replace(":" + Globals.Port, ""))
                .Union(Globals.BannedIPs.Keys)
                .Union(Globals.SkipValPeers.Keys)
                .Union(Globals.ReportedIPs.Keys));

                connectedNodes = Globals.ValidatorNodes.Values.Where(x => x.IsConnected).ToArray();

                foreach (var validator in connectedNodes)
                {
                    SkipIPs.Add(validator.NodeIP);
                }

                newPeers = peerDB.Find(x => x.IsValidator).ToArray()
                .Where(x => !SkipIPs.Contains(x.PeerIP))
                .ToArray()
                .OrderBy(x => rnd.Next())
                .ThenBy(x => x.FailCount)
                .ToArray();
            }

            var Diff = Globals.MaxValPeers - Globals.ValidatorNodes.Count;
            newPeers.Take(Diff).ToArray().ParallelLoop(peer =>
            {
                _ = Connect(peer);
            });

            return Globals.MaxValPeers != 0;         
        }

        public static async Task ManualConnectToVal(Peers peer)
        {
            _ = Connect(peer);
        }

        #endregion

        #region Disconnect Validators
        public static async Task DisconnectValidators()
        {
            try
            {
                Globals.ValidatorAddress = "";
                Globals.ValidatorPublicKey = "";
                foreach (var node in Globals.ValidatorNodes.Values)
                    if (node.Connection != null)
                        await node.Connection.DisposeAsync();

                if(Globals.NetworkValidators.Count() > 0)
                {
                    //foreach (var val in Globals.NetworkValidators.Values)
                    //{
                    //    if (val.Context != null)
                    //        val.Context.Abort();
                    //}
                }
            }
            catch (Exception ex)
            {
                ValidatorLogUtility.Log("Failed! Did not disconnect from Adjudicator: Reason - " + ex.ToString(), "DisconnectAdjudicator()");
            }
        }


        #endregion

        #region Get Height of Nodes for Timed Events

        public static async Task<(long, DateTime, int)> GetNodeHeight(HubConnection conn)
        {
            try
            {
                var startTimer = DateTime.UtcNow;
                long remoteNodeHeight = await conn.InvokeAsync<long>("SendBlockHeightForVals");
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

                    Globals.ValidatorNodes.Values.ToArray().ParallelLoop(node =>
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
            foreach (var node in Globals.ValidatorNodes.Values)
                (node.NodeHeight, node.NodeLastChecked, node.NodeLatency) = await GetNodeHeight(node.Connection);   
        }

        #endregion

        #region Get Current Height of Nodes
        public static async Task<(bool, long)> GetCurrentHeight()
        {
            bool newHeightFound = false;
            long height = -1;

            while (!await AreValidatorsConnected())
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

        #region Send Transactions to mempool 
        public static async Task SendTXMempool(Transaction txSend)
        {
            var peersConnected = await AreValidatorsConnected();

            if (!peersConnected)
            {
                //Need peers
                Console.WriteLine("Failed to broadcast Transaction. No peers are connected to you.");
                LogUtility.Log("TX failed. No Peers: " + txSend.Hash, "P2PClient.SendTXMempool()");
            }
            else
            {
                try
                {
                    var txJson = JsonConvert.SerializeObject(txSend);
                    _ = ValidatorNode.Broadcast("7777", txJson, "SendTxToMempoolVals");
                    //_ = ValidatorNode.Broadcast("7777", txJson, "SendTxToMempoolVals");
                }
                catch (Exception ex)
                {

                }
            }
        }

        #endregion

        #region Broadcast Blocks to Peers
        public static async Task BroadcastBlock(Block block, bool isQueueBlock = false)
        {
            var peersConnected = await AreValidatorsConnected();

            if (!peersConnected)
                return;

            else
            {
                foreach (var node in Globals.ValidatorNodes.Values)
                {
                    try
                    {
                        var source = new CancellationTokenSource(5000);
                        _ = node.Connection.InvokeCoreAsync<bool>("ReceiveBlockVal", new object?[] { block }, source.Token);                      
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

        #region Broadcast Blocks to Peers
        public static async Task FailedToReachConsensus(List<string> failedProducers)
        {
            var peersConnected = await AreValidatorsConnected();

            if (!peersConnected)
                return;

            else
            {
                foreach (var node in Globals.ValidatorNodes.Values)
                {
                    try
                    {
                        var source = new CancellationTokenSource(5000);
                        _ = node.Connection.InvokeCoreAsync<bool>("FailedToReachConsensus", new object?[] { failedProducers }, source.Token);
                    }
                    catch (Exception ex)
                    {
                        //possible dead connection, or node is offline
                        Console.WriteLine("Error Sending FailedToReachConsensus. Please try again!");
                    }
                }
            }
        }
        #endregion

        #region Request Active Validators

        public static async Task RequestActiveValidators()
        {
            bool waitForVals = true;
            while (waitForVals)
            {
                var delay = Task.Delay(new TimeSpan(0, 0, 5));
                var valNodeListCheck = Globals.ValidatorNodes.Values.Where(x => x.IsConnected).ToList();

                if (valNodeListCheck.Count() == 0)
                {
                    await delay;
                    continue;
                }

                var valNodeList = Globals.ValidatorNodes.Values.Where(x => x.IsConnected).ToList();

                foreach (var validator in valNodeList)
                {
                    try
                    {
                        var source = new CancellationTokenSource(2000);
                        string activeValJson = await validator.Connection.InvokeAsync<string>("SendActiveVals", source.Token);

                        if (activeValJson != null && activeValJson != "0")
                        {
                            var activeVals = JsonConvert.DeserializeObject<List<NetworkValidator>>(activeValJson.ToDecompress().ToStringFromBase64());
                            if (activeVals != null)
                            {
                                var peerDB = Peers.GetAll();

                                foreach (var val in activeVals)
                                {
                                    var addResult = await NetworkValidator.AddValidatorToPool(val);

                                    if (!addResult)
                                        continue;

                                    var singleVal = peerDB.FindOne(x => x.PeerIP == val.IPAddress);
                                    if (singleVal != null)
                                    {
                                        singleVal.IsValidator = true;

                                        if(singleVal.ValidatorAddress == null)
                                            singleVal.ValidatorAddress = val.Address;

                                        if(singleVal.ValidatorPublicKey == null)
                                            singleVal.ValidatorPublicKey = val.PublicKey;

                                        peerDB.UpdateSafe(singleVal);
                                    }
                                    else
                                    {
                                        Peers nPeer = new Peers
                                        {
                                            IsIncoming = false,
                                            IsOutgoing = true,
                                            PeerIP = val.IPAddress,
                                            FailCount = 0,
                                            IsValidator = true,
                                            ValidatorAddress = val.Address,
                                            ValidatorPublicKey = val.PublicKey,
                                            WalletVersion = Globals.CLIVersion,
                                        };

                                        peerDB.InsertSafe(nPeer);
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {

                    }
                }

                waitForVals = false;
            }
        }

        #endregion

        #region Request a Queued Block
        public static async Task RequestQueuedBlock(long height)
        {
            var valNodeList = Globals.ValidatorNodes.Values.Where(x => x.IsConnected).ToList();

            if (valNodeList.Count() == 0)
            {
                return;
            }

            foreach (var val in valNodeList)
            {
                try
                {
                    var source = new CancellationTokenSource(2000);
                    var qBlock = await val.Connection.InvokeCoreAsync<Block>("SendQueuedBlock", args: new object?[] { height }, source.Token);
                    if (qBlock != null)
                    {
                        var result = await BlockValidatorService.ValidateBlock(qBlock, false, false, true);
                        
                        if(result)
                        {
                            if (Globals.NetworkBlockQueue.TryAdd(qBlock.Height, qBlock))
                            {
                                _ = BroadcastBlock(qBlock, true);
                                break;
                            }
                        }
                    }
                }
                catch(Exception ex) { }
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

        #region Request Current Winner List
        public static async Task RequestCurrentWinners()
        {
            var valNodeList = Globals.ValidatorNodes.Values.Where(x => x.IsConnected).ToList();

            if (valNodeList.Count() == 0)
            {
                return;
            }

            foreach (var val in valNodeList)
            {
                try
                {
                    var source = new CancellationTokenSource(2000);
                    var winnerProofList = await val.Connection.InvokeAsync<string>("GetWinningProofList", source.Token);
                    if (winnerProofList != null)
                    {
                        if (winnerProofList != "0")
                        {
                            var proofList = JsonConvert.DeserializeObject<List<Proof>>(winnerProofList);
                            if (proofList != null)
                                await ProofUtility.SortProofs(proofList, true);
                        }
                    }
                }
                catch { }
            }
        }

        #endregion

        #region Send Current Winner List

        public static async Task<string> SendCurrentWinners()
        {
            try
            {
                var valNodeList = Globals.ValidatorNodes.Values.Where(x => x.IsConnected).ToList();

                if (valNodeList.Count() == 0)
                {
                    return "0";
                }

                List<Proof> winningProofs = new List<Proof>();
                for (int i = 1; i < 5; i++)
                {
                    var nextBlock = Globals.LastBlock.Height + i;
                    if (!Globals.FinalizedWinner.TryGetValue(nextBlock, out _))
                    {
                        if (Globals.WinningProofs.TryGetValue(nextBlock, out var proof))
                        {
                            winningProofs.Add(proof);
                        }
                    }
                }

                var proofsJson = JsonConvert.SerializeObject(winningProofs);

                foreach (var val in valNodeList)
                {
                    try
                    {
                        var source = new CancellationTokenSource(2000);
                        _ = val.Connection.InvokeCoreAsync("SendWinningProofList", args: new object?[] { proofsJson }, source.Token);
                    }
                    catch (Exception ex) { }
                }

                return proofsJson;
            }
            catch
            { }

            return "0";
        }

        #endregion

        #region Request Finalized Winner List
        public static async Task RequestFinalizedWinners()
        {
            var valNodeList = Globals.ValidatorNodes.Values.Where(x => x.IsConnected).ToList();

            if (valNodeList.Count() == 0)
            {
                return;
            }

            foreach (var val in valNodeList)
            {
                try
                {
                    var source = new CancellationTokenSource(2000);
                    var finalizedWinnerList = await val.Connection.InvokeAsync<string>("GetFinalizedWinnersList", source.Token);
                    if (finalizedWinnerList != null)
                    {
                        if (finalizedWinnerList != "0")
                        {
                            var fList = JsonConvert.DeserializeObject<ConcurrentDictionary<long, string>>(finalizedWinnerList);
                            if (fList != null)
                                Globals.FinalizedWinner = fList;
                        }
                    }
                }
                catch { }
            }
        }

        #endregion

        #region Request Locked Winner
        public static async Task<Dictionary<string, int>> RequestLockedWinner(long height)
        {
            Dictionary<string, int> LockedWinnerDict = new Dictionary<string, int>();
            var valNodeList = Globals.ValidatorNodes.Values.Where(x => x.IsConnected).ToList();

            if (valNodeList.Count() == 0)
            {
                return LockedWinnerDict;
            }

            foreach (var val in valNodeList)
            {
                try
                {
                    var source = new CancellationTokenSource(2000);
                    var lockedWinner = await val.Connection.InvokeCoreAsync<string>("SendLockedWinner", args: new object?[] { height }, source.Token);
                    if (lockedWinner != null)
                    {
                        if (lockedWinner != "0")
                        {
                            if(LockedWinnerDict.ContainsKey(lockedWinner))
                            {
                                LockedWinnerDict[lockedWinner]++;
                            }
                            else
                            {
                                LockedWinnerDict.Add(lockedWinner, 1);
                            }
                        }
                    }
                }
                catch { }
            }

            return LockedWinnerDict;
        }

        #endregion
    }
}
