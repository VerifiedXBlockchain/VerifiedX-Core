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

namespace ReserveBlockCore.P2P
{
    public class P2PClient : IAsyncDisposable, IDisposable
    {
        #region Static Variables
        public const int MaxPeers = 6;
        public static ConcurrentDictionary<string, int> ReportedIPs = new ConcurrentDictionary<string, int>();
        public static long LastSentBlockHeight = -1;
        public static DateTime? AdjudicatorConnectDate = null;
        public static DateTime? LastTaskSentTime = null;
        public static DateTime? LastTaskResultTime = null;
        public static long LastTaskBlockHeight = 0;
        public static bool LastTaskError = false;
        #endregion

        #region HubConnection Variables        
        /// <summary>
        /// Below are reserved for adjudicators to open up communications fortis pool participation and block solving.
        /// </summary>

        private static HubConnection? hubAdjConnection1; //reserved for validators
        public static bool IsAdjConnected1 => hubAdjConnection1?.State == HubConnectionState.Connected;

        private static HubConnection? hubAdjConnection2; //reserved for validators
        public static bool IsAdjConnected2 => hubAdjConnection2?.State == HubConnectionState.Connected;

        #endregion

        #region Get Available HubConnections for Peers

        public static bool IsConnected(NodeInfo node)
        {            
            return node.Connection.State == HubConnectionState.Connected;            
        }
        private static async Task RemoveNode(NodeInfo node)
        {            
            Program.Nodes.TryRemove(node.Connection.ConnectionId, out NodeInfo test);
            await node.Connection.DisposeAsync();            
        }

        #endregion

        #region Check which HubConnections are actively connected

        public static async Task<bool> ArePeersConnected()
        {
            await DropDisconnectedPeers();
            return Program.Nodes.Any();
        }
        public static async Task DropDisconnectedPeers()
        {
            foreach (var node in Program.Nodes.Values)
            {
                if(!IsConnected(node))                
                    await RemoveNode(node);
            }
        }

        public static string MostLikelyIP()
        {
            return ReportedIPs.Count != 0 ? 
                ReportedIPs.OrderByDescending(y => y.Value).Select(y => y.Key).First() : "NA";
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
                foreach(var node in Program.Nodes.Values)
                    node.Connection.DisposeAsync().ConfigureAwait(false).GetAwaiter().GetResult();                
            }
        }

        protected virtual async ValueTask DisposeAsyncCore()
        {
            foreach (var node in Program.Nodes.Values)
                await node.Connection.DisposeAsync();
        }

        #endregion

        #region Hubconnection Connect Methods 1-6
        private static async Task<bool> Connect(string url)
        {
            try
            {
                var hubConnection = new HubConnectionBuilder()
                       .WithUrl(url, options =>
                       {

                       })
                       .WithAutomaticReconnect()
                       .Build();

                hubConnection.On<string, string>("GetMessage", async (message, data) =>
                {
                    var bob = url;
                    if (message == "tx" || message == "blk" || message == "val" || message == "IP")
                    {
                        if (message != "IP")
                        {
                            await NodeDataProcessor.ProcessData(message, data);
                        }
                        else
                        {
                            var IP = data.ToString();
                            if (ReportedIPs.TryGetValue(IP, out int Occurrences))
                                ReportedIPs[IP]++;
                            else
                                ReportedIPs[IP] = 1;                                                        
                        }
                    }

                });

                await hubConnection.StartAsync();
                if (hubConnection.ConnectionId == null)
                    return false;

                var IPAddress = url.Replace("http://", "").Replace("/blockchain", "");

                var startTimer = DateTime.UtcNow;
                long remoteNodeHeight = await hubConnection.InvokeAsync<long>("SendBlockHeight");
                var endTimer = DateTime.UtcNow;
                var totalMS = (endTimer - startTimer).Milliseconds;

                Program.Nodes.TryAdd(hubConnection.ConnectionId, new NodeInfo
                {
                    Connection = hubConnection,
                    NodeIP = IPAddress,
                    NodeHeight = remoteNodeHeight,
                    NodeLastChecked = startTimer,
                    NodeLatency = totalMS
                });

                return true;
            }
            catch { }

            return false;
        }

        #endregion

        #region Connect Adjudicator
        public static async Task ConnectAdjudicator(string url, string address, string uName, string signature)
        {
            try
            {
                hubAdjConnection1 = new HubConnectionBuilder()
                .WithUrl(url, options => {
                    options.Headers.Add("address", address);
                    options.Headers.Add("uName", uName);
                    options.Headers.Add("signature", signature);
                    options.Headers.Add("walver", Program.CLIVersion);

                })
                .WithAutomaticReconnect()
                .Build();

                LogUtility.Log("Connecting to Adjudicator", "ConnectAdjudicator()");

                hubAdjConnection1.Reconnecting += (sender) =>
                {
                    LogUtility.Log("Reconnecting to Adjudicator", "ConnectAdjudicator()");
                    Console.WriteLine("[" + DateTime.Now.ToString() + "] Connection to adjudicator lost. Attempting to Reconnect.");
                    return Task.CompletedTask;
                };

                hubAdjConnection1.Reconnected += (sender) =>
                {
                    LogUtility.Log("Success! Reconnected to Adjudicator", "ConnectAdjudicator()");
                    Console.WriteLine("[" + DateTime.Now.ToString() + "] Connection to adjudicator has been restored.");
                    return Task.CompletedTask;
                };

                hubAdjConnection1.Closed += (sender) =>
                {
                    LogUtility.Log("Closed to Adjudicator", "ConnectAdjudicator()");
                    Console.WriteLine("[" + DateTime.Now.ToString() + "] Connection to adjudicator has been closed.");
                    return Task.CompletedTask;
                };

                AdjudicatorConnectDate = DateTime.UtcNow;

                hubAdjConnection1.On<string, string>("GetAdjMessage", async (message, data) => {
                    if (message == "task" || message == "taskResult" || message == "fortisPool" || message == "status" || message == "tx" || message == "badBlock")
                    {
                        switch(message)
                        {
                            case "task":
                                await ValidatorProcessor.ProcessData(message, data);
                                break;
                            case "taskResult":
                                await ValidatorProcessor.ProcessData(message, data);
                                break;
                            case "fortisPool":
                                await ValidatorProcessor.ProcessData(message, data);
                                break;
                            case "status":
                                Console.WriteLine(data);
                                ValidatorLogUtility.Log("Connected to Validator Pool", "P2PClient.ConnectAdjudicator()", true);
                                LogUtility.Log("Success! Connected to Adjudicator", "ConnectAdjudicator()");
                                break;
                            case "tx":
                                await ValidatorProcessor.ProcessData(message, data);
                                break;
                            case "badBlock":
                                //do something
                                break;
                        }
                    }
                });

                await hubAdjConnection1.StartAsync();

            }
            catch (Exception ex)
            {
                ValidatorLogUtility.Log("Failed! Connecting to Adjudicator: Reason - " + ex.Message, "ConnectAdjudicator()");
            }
        }


        #endregion

        #region Connect to Peers
        public static async Task<bool> ConnectToPeers()
        {
            await NodeConnector.StartNodeConnecting();
            var peerDB = Peers.GetAll();

            await DropDisconnectedPeers();
            var CurrentPeersIPs = new HashSet<string>(Program.Nodes.Values.Select(x => x.NodeIP));

            Random rnd = new Random();
            var newPeers = peerDB.Find(x => x.IsOutgoing == true).ToArray()
                .Where(x => !CurrentPeersIPs.Contains(x.PeerIP))
                .ToArray()
                .OrderBy(x => rnd.Next())                
                .Concat(peerDB.Find(x => x.IsOutgoing == false).ToArray()
                .Where(x => !CurrentPeersIPs.Contains(x.PeerIP))
                .ToArray()
                .OrderBy(x => rnd.Next()))                
                .ToArray();

            var NodeCount = Program.Nodes.Count;
            foreach(var peer in newPeers)
            {
                if (NodeCount == MaxPeers)
                    break;

                var url = "http://" + peer.PeerIP + ":" + Program.Port + "/blockchain";
                var conResult = await Connect(url);
                if (conResult != false)
                {
                    NodeCount++;
                    peer.IsOutgoing = true;
                    peer.FailCount = 0; //peer responded. Reset fail count
                    peerDB.UpdateSafe(peer);
                }
                else
                {
                    //peer.FailCount += 1;
                    //peerDB.UpdateSafe(peer);
                }
            }
                                 
            return NodeCount != 0;
        }

        public static async Task<bool> PingBackPeer(string peerIP)
        {
            try
            {
                var url = "http://" + peerIP + ":" + Program.Port + "/blockchain";
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

        #region Send Task Answer
        public static async Task SendTaskAnswer(TaskAnswer taskAnswer)
        {
            var adjudicatorConnected = IsAdjConnected1;
            if(adjudicatorConnected)
            {
                try
                {
                    if(taskAnswer.Block.Height == Program.BlockHeight + 1)
                    {
                        if (hubAdjConnection1 != null)
                        {
                            var result = await hubAdjConnection1.InvokeCoreAsync<bool>("ReceiveTaskAnswer", args: new object?[] { taskAnswer });
                            if (result)
                            {
                                LastTaskError = false;
                                LastTaskSentTime = DateTime.Now;
                                LastSentBlockHeight = taskAnswer.Block.Height;
                            }
                            else
                            {
                                LastTaskError = true;
                                ValidatorLogUtility.Log("Block passed validation, but received a false result from adjudicator and failed.", "P2PClient.SendTaskAnswer()");
                            }
                        }
                    }
                }
                catch(Exception ex)
                {
                    LastTaskError = true;

                    ValidatorLogUtility.Log("Unhandled Error Sending Task. Check Error Log for more details.", "P2PClient.SendTaskAnswer()");

                    string errorMsg = string.Format("Error Sending Task - {0}. Error Message : {1}", taskAnswer != null ? 
                        taskAnswer.SubmitTime.ToString() : "No Time", ex.Message);
                    ErrorLogUtility.LogError(errorMsg, "SendTaskAnswer(TaskAnswer taskAnswer)");
                }
            }
            else
            {
                //reconnect and then send
            }
        }

        #endregion

        #region Send TX To Adjudicators
        public static async Task SendTXToAdjudicator(Transaction tx)
        {
            var adjudicatorConnected = IsAdjConnected1;
            if (adjudicatorConnected == true && hubAdjConnection1 != null)
            {
                try
                {
                    var result = await hubAdjConnection1.InvokeCoreAsync<bool>("ReceiveTX", args: new object?[] { tx });
                }
                catch (Exception ex)
                {

                }
            }
            else
            {
                //temporary connection to an adj to send transaction to get broadcasted to global pool
                SendTXToAdj(tx);
            }

            var adjudicator2Connected = IsAdjConnected2;
            if (adjudicator2Connected == true && hubAdjConnection2 != null)
            {
                try
                {
                    var result = await hubAdjConnection2.InvokeCoreAsync<bool>("ReceiveTX", args: new object?[] { tx });
                }
                catch (Exception ex)
                {

                }
            }
            else
            {
                //temporary connection to an adj to send transaction to get broadcasted to global pool
                SendTXToAdj(tx);
            }
        }


        //This method will need to eventually be modified when the adj is a multi-pool and not a singular-pool
        private static async void SendTXToAdj(Transaction trx)
        {
            try
            {
                var adjudicator = Adjudicators.AdjudicatorData.GetLeadAdjudicator();
                if (adjudicator != null)
                {
                    var url = "http://" + adjudicator.NodeIP + ":" + Program.Port + "/adjudicator";
                    var _tempHubConnection = new HubConnectionBuilder().WithUrl(url).Build();
                    var alive = _tempHubConnection.StartAsync();
                    var response = await _tempHubConnection.InvokeCoreAsync<bool>("ReceiveTX", args: new object?[] { trx });
                    if(response != true)
                    {
                        var errorMsg = string.Format("Failed to send TX to Adjudicator.");
                        ErrorLogUtility.LogError(errorMsg, "P2PClient.SendTXToAdj(Transaction trx) - try");
                        try { await _tempHubConnection.StopAsync(); }
                        finally
                        {
                            await _tempHubConnection.DisposeAsync();
                        }
                    }
                    else
                    {
                        try { await _tempHubConnection.StopAsync(); }
                        finally
                        {
                            await _tempHubConnection.DisposeAsync();
                        }
                    }
                }
            }
            catch(Exception ex)
            {
                var errorMsg = string.Format("Failed to send TX to Adjudicator. Error Message : {0}", ex.Message);
                ErrorLogUtility.LogError(errorMsg, "P2PClient.SendTXToAdj(Transaction trx) - catch");
            }
        }

        #endregion

        #region Get Block
        public static async Task<List<Block>> GetBlock() //base example
        {
            var currentBlock = Program.BlockHeight != -1 ? Program.LastBlock.Height : -1; //-1 means fresh client with no blocks
            var nBlock = new Block();
            List<Block> blocks = new List<Block>();
            var peersConnected = await ArePeersConnected();

            if (!peersConnected)
            {
                //Need peers
                return blocks;
            }
            else
            {
                foreach(var node in Program.Nodes.Values)
                {
                    try
                    {
                        nBlock = await node.Connection.InvokeCoreAsync<Block>("SendBlock", args: new object?[] { currentBlock });
                        if (nBlock != null && !blocks.Exists(x => x.Height == nBlock.Height))
                        {
                            blocks.Add(nBlock);
                            currentBlock++;
                        }
                    }
                    catch (Exception ex)
                    {
                        //possible dead connection, or node is offline
                    }
                }

                return blocks;

            }

        }

        #endregion

        #region Get Height of Nodes for Timed Events

        public static async Task<(long, DateTime, int)> GetNodeHeight(NodeInfo node)
        {
            try
            {
                var startTimer = DateTime.UtcNow;
                long remoteNodeHeight = await node.Connection.InvokeAsync<long>("SendBlockHeight");
                var endTimer = DateTime.UtcNow;
                var totalMS = (endTimer - startTimer).Milliseconds;

                return (remoteNodeHeight, startTimer, totalMS); ;
            }
            catch { }
            return default;
        }
        public static async Task UpdateNodeHeights()
        {
            foreach (var node in Program.Nodes.Values)                
                (node.NodeHeight, node.NodeLastChecked, node.NodeLatency) = await GetNodeHeight(node);           
        }

        #endregion

        #region Get Current Height of Nodes
        public static async Task<(bool, long)> GetCurrentHeight()
        {
            bool newHeightFound = false;
            long height = 0;

            var peersConnected = await P2PClient.ArePeersConnected();

            if (!peersConnected)
            {                
                return (newHeightFound, height);
            }
            else
            {
                if(Program.BlockHeight == -1)
                {
                    return (true, -1);
                }

                long myHeight = Program.BlockHeight;

                await UpdateNodeHeights();

                foreach (var node in Program.Nodes.Values)
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
            }
            return (newHeightFound, height);
        }

        #endregion

        #region File Upload To Beacon Beacon

        public static async Task<string> BeaconUploadRequest(List<string> locators, List<string> assets, string scUID, string nextOwnerAddress, string preSigned = "NA")
        {
            var result = "Fail";
            string signature = "";
            string locatorRetString = "";
            var scState = SmartContractStateTrei.GetSmartContractState(scUID);
            if(scState == null)
            {
                return "Fail"; // SC does not exist
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
                    if (account != null)
                    {
                        BigInteger b1 = BigInteger.Parse(account.PrivateKey, NumberStyles.AllowHexSpecifier);//converts hex private key into big int.
                        PrivateKey privateKey = new PrivateKey("secp256k1", b1);

                        signature = SignatureService.CreateSignature(scUID, privateKey, account.PublicKey);
                    }
                    else
                    {
                        return "Fail";
                    }
                }
                
            }

            //send file size, beacon will reply if it is ok to send.
            var bsd = new BeaconData.BeaconSendData {
                Assets = assets,
                SmartContractUID = scUID,
                Signature = signature,
                NextAssetOwnerAddress = nextOwnerAddress
            };
            foreach(var locator in locators)
            {
                try
                {
                    var beaconString = locator.ToStringFromBase64();
                    var beacon = JsonConvert.DeserializeObject<BeaconInfo.BeaconInfoJson>(beaconString);

                    var url = "http://" + beacon.IPAddress + ":" + Program.Port + "/blockchain";
                    var _tempHubConnection = new HubConnectionBuilder().WithUrl(url).Build();
                    var alive = _tempHubConnection.StartAsync();
                    var response = await _tempHubConnection.InvokeCoreAsync<bool>("ReceiveUploadRequest", args: new object?[] { bsd });
                    if (response != true)
                    {
                        var errorMsg = string.Format("Failed to talk to beacon.");
                        ErrorLogUtility.LogError(errorMsg, "P2PClient.BeaconUploadRequest(List<BeaconInfo.BeaconInfoJson> locators, List<string> assets, string scUID) - try");
                        try { await _tempHubConnection.StopAsync(); }
                        finally
                        {
                            await _tempHubConnection.DisposeAsync();
                        }
                    }
                    else
                    {
                        NFTLogUtility.Log($"Beacon response was true.", "P2PClient.BeaconUploadRequest()");
                        try { await _tempHubConnection.StopAsync(); }
                        finally
                        {
                            await _tempHubConnection.DisposeAsync();
                        }
                        if(locatorRetString == "")
                        {
                            foreach(var asset in bsd.Assets)
                            {
                                NFTLogUtility.Log($"Preparing file to send. Sending {asset} for smart contract {bsd.SmartContractUID}", "P2PClient.BeaconUploadRequest()");
                                var path = NFTAssetFileUtility.NFTAssetPath(asset, bsd.SmartContractUID);
                                NFTLogUtility.Log($"Path for asset {assets} : {path}", "P2PClient.BeaconUploadRequest()");
                                NFTLogUtility.Log($"Beacon IP {beacon.IPAddress} : Beacon Port {beacon.Port}", "P2PClient.BeaconUploadRequest()");
                                BeaconResponse rsp = BeaconClient.Send(path, beacon.IPAddress, beacon.Port);
                                if (rsp.Status == 1)
                                {
                                    //success
                                    NFTLogUtility.Log($"Success sending asset: {asset}", "P2PClient.BeaconUploadRequest()");
                                }
                                else
                                {
                                    NFTLogUtility.Log($"NFT Send for assets -> {asset} <- failed.", "SCV1Controller.TransferNFT()");
                                }
                            }
                            
                            locatorRetString = locator;
                        }
                        else
                        {
                            locatorRetString = locatorRetString + "," + locator;
                        }

                    }
                }
                catch (Exception ex)
                {
                    var errorMsg = string.Format("Failed to send bsd to Beacon. Error Message : {0}", ex.Message);
                    ErrorLogUtility.LogError(errorMsg, "P2PClient.BeaconUploadRequest(List<BeaconInfo.BeaconInfoJson> locators, List<string> assets, string scUID) - catch");
                }
            }
            result = locatorRetString;
            return result;
        }

        #endregion

        #region File Download from Beacon - BeaconAccessRequest

        public static async Task<bool> BeaconDownloadRequest(List<string> locators, List<string> assets, string scUID, string preSigned = "NA")
        {
            var result = false;
            string signature = "";
            string locatorRetString = "";
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
                    if (account != null)
                    {
                        BigInteger b1 = BigInteger.Parse(account.PrivateKey, NumberStyles.AllowHexSpecifier);//converts hex private key into big int.
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
            };

            foreach (var locator in locators)
            {
                try
                {
                    var beaconString = locator.ToStringFromBase64();
                    var beacon = JsonConvert.DeserializeObject<BeaconInfo.BeaconInfoJson>(beaconString);

                    var url = "http://" + beacon.IPAddress + ":" + Program.Port + "/blockchain";
                    var _tempHubConnection = new HubConnectionBuilder().WithUrl(url).Build();
                    var alive = _tempHubConnection.StartAsync();

                    var response = await _tempHubConnection.InvokeCoreAsync<bool>("ReceiveDownloadRequest", args: new object?[] { bdd });
                    if (response != true)
                    {
                        var errorMsg = string.Format("Failed to talk to beacon.");
                        ErrorLogUtility.LogError(errorMsg, "P2PClient.BeaconUploadRequest(List<BeaconInfo.BeaconInfoJson> locators, List<string> assets, string scUID) - try");
                        try { await _tempHubConnection.StopAsync(); }
                        finally
                        {
                            await _tempHubConnection.DisposeAsync();
                        }
                    }
                    else
                    {
                        try { await _tempHubConnection.StopAsync(); }
                        finally
                        {
                            await _tempHubConnection.DisposeAsync();
                        }

                        int failCount = 0;
                        foreach (var asset in bdd.Assets)
                        {
                            var path = NFTAssetFileUtility.CreateNFTAssetPath(asset, bdd.SmartContractUID);
                            BeaconResponse rsp = BeaconClient.Receive(asset, beacon.IPAddress, beacon.Port, scUID);
                            if (rsp.Status == 1)
                            {
                                //success
                            }
                            else
                            {
                                failCount += 1;
                            }
                        }

                        if(failCount == 0)
                        {
                            result = true;
                            break;
                        }
                    }
                }
                catch(Exception ex)
                {
                    var errorMsg = string.Format("Failed to send bdd to Beacon. Error Message : {0}", ex.Message);
                    ErrorLogUtility.LogError(errorMsg, "P2PClient.BeaconDownloadRequest() - catch");
                }
            }

            return result;
        }

        #endregion

        #region Get Beacon Status of Nodes
        public static async Task<List<string>> GetBeacons()
        {
            List<string> BeaconList = new List<string>();

            var peersConnected = await ArePeersConnected();

            int foundBeaconCount = 0;

            if (!peersConnected)
            {
                //Need peers
                ErrorLogUtility.LogError("You are not connected to any nodes", "P2PClient.GetBeacons()");
                NFTLogUtility.Log("You are not connected to any nodes", "P2PClient.GetBeacons()");
                return BeaconList;
            }
            else
            {
                foreach(var node in Program.Nodes.Values)
                {
                    string beaconInfo = await node.Connection.InvokeAsync<string>("SendBeaconInfo");
                    if (beaconInfo != "NA")
                    {
                        NFTLogUtility.Log("Beacon Found on hub " + node.NodeIP, "P2PClient.GetBeacons()");
                        BeaconList.Add(beaconInfo);
                        foundBeaconCount++;
                    }
                }

                if(foundBeaconCount == 0)
                {
                    NFTLogUtility.Log("Zero beacons found. Adding bootstrap.", "SCV1Controller.TransferNFT()");
                    BeaconList = Program.Locators;
                    BeaconList.ForEach(x => { NFTLogUtility.Log($"Bootstrap Beacons {x}", "P2PClient.GetBeacons()"); });
                }

            }
            return BeaconList;
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
                if (Program.BlockHeight == -1)
                {
                    return null;
                }

                long myHeight = Program.BlockHeight;

                foreach(var node in Program.Nodes.Values)
                {
                    try
                    {
                        var leadAdjudictor = await node.Connection.InvokeAsync<Adjudicators?>("SendLeadAdjudicator");

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
        public static async void SendTXMempool(Transaction txSend)
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
                foreach (var node in Program.Nodes.Values)
                {
                    try
                    {
                        string message = await node.Connection.InvokeCoreAsync<string>("SendTxToMempool", args: new object?[] { txSend });

                        if (message == "ATMP")
                        {
                            //success
                        }
                        else if (message == "TFVP")
                        {
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
                foreach(var node in Program.Nodes.Values)
                {
                    try
                    {                        
                        await node.Connection.InvokeCoreAsync<string>("ReceiveBlock", args: new object?[] { block });
                        
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
    }
}
