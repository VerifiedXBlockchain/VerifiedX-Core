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
using ReserveBlockCore.Extensions;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using System.Net;

namespace ReserveBlockCore.P2P
{
    public class ConsensusClient : IAsyncDisposable, IDisposable
    {
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

        #region Consensus Code

        public enum RunType
        {
            Initial,
            Middle,
            Last
        }

        private static int ReadyToFinalize = 0;

        public static async Task<(string Address, string Message)[]> ConsensusRun(int methodCode, string message, string signature, int timeToFinalize, RunType runType)
        {
            try
            {
                Interlocked.Exchange(ref ReadyToFinalize, 0);
                ConsensusServer.UpdateState(methodCode: methodCode);
                var Address = Globals.AdjudicateAccount.Address;
                var Peers = Globals.Nodes.Values.Where(x => x.Address != Address).ToArray();
                long Height = -1;                
                int Majority = -1;
                ConcurrentDictionary<string, (string Message, string Signature)> Messages = null;                
                SendMethodCode(Peers, methodCode, false);
                var DelayTask = Task.Delay(timeToFinalize);
                while (true)
                {
                    Height = Globals.LastBlock.Height + 1;                                    
                    Peers = Globals.Nodes.Values.Where(x => x.Address != Address).ToArray();                    

                    var CurrentAddresses = Signer.CurrentSigningAddresses();
                    var NumNodes = CurrentAddresses.Count;
                    Majority = NumNodes / 2 + 1;

                    Messages = new ConcurrentDictionary<string, (string Message, string Signature)>();
                    var MessageKeysToKeep = ConsensusServer.Messages.Where(x => (x.Key.Height == Height && x.Key.MethodCode == methodCode) ||
                        (x.Key.Height == Height && x.Key.MethodCode == methodCode - 1) || (x.Key.Height == Height + 1 && x.Key.MethodCode == 0))
                        .Select(x => x.Key).ToHashSet();
                    foreach(var key in ConsensusServer.Messages.Keys.Where(x => !MessageKeysToKeep.Contains(x)))
                    {
                        ConsensusServer.Messages.TryRemove(key, out _);
                    }

                    ConsensusServer.Messages.TryAdd((Height, methodCode), Messages);
                    Messages = ConsensusServer.Messages[(Height, methodCode)];                    
                    Messages[Globals.AdjudicateAccount.Address] = (message, signature);
                                    
                    var ConsensusSource = new CancellationTokenSource(runType == RunType.Initial ? 30000 : 6000);                    
                    _ = PeerRequestLoop(methodCode, Peers, CurrentAddresses, ConsensusSource);
                    
                    var Delay = Task.Delay(2000);
                    var WaitForAddresses = AddressesToWaitFor(Height, methodCode);
                    while (Height == Globals.LastBlock.Height + 1)
                    {
                        if(!WaitForAddresses.Any(x => !Messages.ContainsKey(x)) && ((Delay.IsCompleted && runType != RunType.Initial) || Messages.Count >= Majority))                        
                            break;
                        await Task.Delay(20);
                        WaitForAddresses = AddressesToWaitFor(Height, methodCode);
                    }

                    if (Height != Globals.LastBlock.Height + 1)
                    {
                        ConsensusSource.Cancel();
                        continue;
                    }

                    if (Messages.Count >= Majority)
                    {                        
                        Interlocked.Exchange(ref ReadyToFinalize, 1);
                        ConsensusSource.Cancel();
                        break;
                    }

                    ConsensusSource.Cancel();
                    LogState("end of first loop", Height, methodCode, ConsensusStatus.Processing, Peers);
                    return null;                    
                }

                while (ReadyToFinalize != 1)
                    await Task.Delay(20);

                ConsensusServer.UpdateState(status: (int)ConsensusStatus.Finalized);                                
                var MyHash = Ecdsa.sha256(string.Join("", Messages.OrderBy(x => x.Key).Select(x => Ecdsa.sha256(x.Value.Message))));
                var Signature = SignatureService.AdjudicatorSignature(MyHash);

                var HashKeysToKeep = ConsensusServer.Hashes.Where(x => (x.Key.Height == Height && x.Key.MethodCode == methodCode) ||
                    (x.Key.Height == Height && x.Key.MethodCode == methodCode - 1) || (x.Key.Height == Height + 1 && x.Key.MethodCode == 0))
                    .Select(x => x.Key).ToHashSet();
                foreach (var key in ConsensusServer.Hashes.Keys.Where(x => !HashKeysToKeep.Contains(x)))
                {
                    ConsensusServer.Hashes.TryRemove(key, out _);
                }
                
                ConsensusServer.Hashes.TryAdd((Height, methodCode), new ConcurrentDictionary<string, (string Hash, string Signature)>());
                var Hashes = ConsensusServer.Hashes[(Height, methodCode)];
                Hashes[Globals.AdjudicateAccount.Address] = (MyHash, Signature);
                SendMethodCode(Peers, methodCode, true);
                
                var HashSource = new CancellationTokenSource(6000);
                var signers = Signer.CurrentSigningAddresses();                                                                           
                _ = PeerHashRequestLoop(methodCode, Peers, signers, HashSource);               
                
                var HashDelay = Task.Delay(1000);
                var HashAddressesToWaitFor = AddressesToWaitFor(Height, methodCode);                
                while (Height == Globals.LastBlock.Height + 1)
                {                    
                    var CurrentHashes = Hashes.ToArray();
                    var CurrentMatchAddresses = CurrentHashes.Where(x => x.Value.Hash == MyHash).Select(x => x.Key).ToArray();
                    var NumMatches = CurrentMatchAddresses.Length;
                    if (NumMatches >= Majority)
                    {
                        HashSource.Cancel();
                        while(true)
                        {
                            var Now = TimeUtil.GetMillisecondTime();
                            var AnyNodesToWaitFor = Globals.Nodes.Values.Where(x => Now - x.LastMethodCodeTime < 2000 && 
                                (x.MethodCode != 0 || x.IsFinalized) && (!x.IsFinalized || x.MethodCode < methodCode)).Any();
                            if(AnyNodesToWaitFor)
                            {
                                await Task.Delay(20);
                                continue;
                            }
                            break;
                        }
                        return Messages.Select(x => (x.Key, x.Value.Message)).ToArray();
                    }

                    if (CurrentHashes.Length - NumMatches >= Majority)
                    {
                        HashSource.Cancel();
                        LogState("hash fail", Height, methodCode, ConsensusStatus.Processing, Peers);
                        return null;
                    }

                    if (HashDelay.IsCompleted)
                    {
                        HashAddressesToWaitFor = AddressesToWaitFor(Height, methodCode);
                        var RemainingAddressCount = HashAddressesToWaitFor.Except(CurrentMatchAddresses).Count();
                        if (NumMatches + RemainingAddressCount < Majority)
                            return null;
                    }

                    await Task.Delay(20);                    
                }

                HashSource.Cancel();
            }
            catch(Exception ex)
            {
                ErrorLogUtility.LogError(ex.ToString(), "ConsensusRun");
            }
            LogState("exception thrown", Globals.LastBlock.Height + 1, methodCode, ConsensusStatus.Processing, Globals.Nodes.Values.ToArray());
            return null;
        }

        public static HashSet<string> AddressesToWaitFor(long height, int methodCode)
        {
            var Now = TimeUtil.GetMillisecondTime();
            return Globals.Nodes.Values.Where(x => Now - x.LastMethodCodeTime < 2000 && x.NodeHeight + 1 == height && (x.MethodCode == methodCode || x.MethodCode == methodCode + 1 || (x.MethodCode == methodCode - 1 && x.IsFinalized)))
                .Select(x => x.Address).ToHashSet();
        }

        public static void LogState(string place, long height, int methodCode, ConsensusStatus status, NodeInfo[] peers)
        {
            var Now = TimeUtil.GetMillisecondTime();
            var Data = peers.Where(x => Now - x.LastMethodCodeTime < 2100).Select(x => x.Address + " " + x.NodeHeight + " " + x.MethodCode + " " + x.IsFinalized ).ToArray();
            ErrorLogUtility.LogError(Now + " " + height + " " + methodCode + " " + (status == ConsensusStatus.Finalized ? 1 : 0) + " " + string.Join("|", Data), place);
        }

        public static void SendMethodCode(NodeInfo[] peers, int methodCode, bool isFinalized)
        {
            var Now = TimeUtil.GetMillisecondTime();            
            _ = Task.WhenAll(peers.Select(node =>
            {
                var Source = new CancellationTokenSource(1000);
                var SendMethodCodeFunc = () => node.InvokeAsync<bool>("SendMethodCode", args: new object?[] { Globals.LastBlock.Height, methodCode, isFinalized }, Source.Token)
                    ?? Task.FromResult(false);
                return SendMethodCodeFunc.RetryUntilSuccessOrCancel(x => x || TimeUtil.GetMillisecondTime() - Now > 2000, 100, default);
            }));
        }

        public static async Task PeerRequestLoop(int methodCode, NodeInfo[] peers, HashSet<string> addresses, CancellationTokenSource cts)
        {
            var messages = ConsensusServer.Messages[(Globals.LastBlock.Height + 1, methodCode)];
            var rnd = new Random();
            var taskDict = new ConcurrentDictionary<string, Task<string>>();
            var MissingAddresses = addresses.Except(messages.Select(x => x.Key)).OrderBy(x => rnd.Next()).ToArray();
            var SentMessageToPeerSet = new HashSet<string>();
            var MyMessage = messages[Globals.AdjudicateAccount.Address];
            var ToSend = MyMessage.Message.Replace(":", "::") + ";:;" + MyMessage.Signature;

            do
            {
                try
                {
                    var RecentPeers = peers.Where(x => x.IsConnected && x.NodeHeight == Globals.LastBlock.Height &&
                        x.MethodCode == methodCode && !taskDict.ContainsKey(x.NodeIP)).ToArray();
                    
                    var Source = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, new CancellationTokenSource(1000).Token);
                    for (var i = 0; i < RecentPeers.Length; i++)
                    {
                        var peer = RecentPeers[i];
                        var MessageToSend = SentMessageToPeerSet.Contains(peer.NodeIP) ? null : ToSend;
                        taskDict[peer.NodeIP] = peer.InvokeAsync<string>("Message", args: new object?[] { Globals.LastBlock.Height + 1, methodCode, MissingAddresses.Rotate(i * MissingAddresses.Length / RecentPeers.Length), MessageToSend }, Source.Token);
                    }                    
                    
                    if(!taskDict.Any())
                    {
                        await Task.Delay(10);
                        continue;
                    }

                    await Task.WhenAny(taskDict.Values);

                    var CompletedTasks = taskDict.Where(x => x.Value.IsCompleted).ToArray();
                    foreach(var completedTask in CompletedTasks)
                    {
                        try
                        {
                            var Response = await completedTask.Value;
                            if (Response != null)
                            {
                                var arr = Response.Split(";:;");
                                var (address, message, signature) = (arr[0], arr[1].Replace("::", ":"), arr[2]);
                                if (MissingAddresses.Contains(address) && SignatureService.VerifySignature(address, message, signature))
                                    messages[address] = (message, signature);
                                MissingAddresses = addresses.Except(messages.Select(x => x.Key)).OrderBy(x => rnd.Next()).ToArray();
                            }
                        }
                        catch(TaskCanceledException ex)
                        { }
                        catch(Exception ex)
                        {
                            ErrorLogUtility.LogError(ex.ToString(), "PeerRequestLoop inner catch");
                        }

                        taskDict.TryRemove(completedTask.Key, out _);
                        SentMessageToPeerSet.Add(completedTask.Key);
                    }                    
                }
                catch(Exception ex)
                {
                    ErrorLogUtility.LogError(ex.ToString(), "PeerRequestLoop outer catch");
                }

            await Task.Delay(10);
            } while (!cts.IsCancellationRequested && MissingAddresses.Any());

            await cts.Token.WhenCanceled();
            Interlocked.Exchange(ref ReadyToFinalize, 1);
        }

        public static async Task PeerHashRequestLoop(int methodCode, NodeInfo[] peers, HashSet<string> addresses, CancellationTokenSource cts)
        {
            var hashes = ConsensusServer.Hashes[(Globals.LastBlock.Height + 1, methodCode)];
            var rnd = new Random();
            var taskDict = new ConcurrentDictionary<string, Task<string>>();
            var MissingAddresses = addresses.Except(hashes.Select(x => x.Key)).OrderBy(x => rnd.Next()).ToArray();
            var SentHashToPeerSet = new HashSet<string>();
            var MyHash = hashes[Globals.AdjudicateAccount.Address];
            var ToSend = MyHash.Hash + ":" + MyHash.Signature;

            do
            {
                try
                {
                    var RecentPeers = peers.Where(x => x.IsConnected && x.NodeHeight == Globals.LastBlock.Height &&
                        x.MethodCode == methodCode && !taskDict.ContainsKey(x.NodeIP)).ToArray();

                    var Source = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, new CancellationTokenSource(1000).Token);
                    for (var i = 0; i < RecentPeers.Length; i++)
                    {
                        var peer = RecentPeers[i];
                        var HashToSend = SentHashToPeerSet.Contains(peer.NodeIP) ? null : ToSend;
                        taskDict[peer.NodeIP] = peer.InvokeAsync<string>("Hash", args: new object?[] { Globals.LastBlock.Height + 1, methodCode, MissingAddresses.Rotate(i * MissingAddresses.Length / RecentPeers.Length), HashToSend }, Source.Token);
                    }

                    if (!taskDict.Any())
                    {
                        await Task.Delay(10);
                        continue;
                    }

                    await Task.WhenAny(taskDict.Values);

                    var CompletedTasks = taskDict.Where(x => x.Value.IsCompleted).ToArray();
                    foreach (var completedTask in CompletedTasks)
                    {
                        try
                        {
                            var Response = await completedTask.Value;
                            if (Response != null)
                            {
                                var arr = Response.Split(":");
                                var (address, hash, signature) = (arr[0], arr[1], arr[2]);
                                if (MissingAddresses.Contains(address) && SignatureService.VerifySignature(address, hash, signature))
                                    hashes[address] = (hash, signature);
                                MissingAddresses = addresses.Except(hashes.Select(x => x.Key)).OrderBy(x => rnd.Next()).ToArray();
                            }
                        }
                        catch (TaskCanceledException ex)
                        { }
                        catch (Exception ex)
                        {
                            ErrorLogUtility.LogError(ex.ToString(), "PeerHashRequestLoop inner catch");
                        }

                        taskDict.TryRemove(completedTask.Key, out _);
                        SentHashToPeerSet.Add(completedTask.Key);
                    }                    
                }
                catch (Exception ex)
                {
                    ErrorLogUtility.LogError(ex.ToString(), "PeerHashRequestLoop outer catch");
                }

                await Task.Delay(10);
            } while (!cts.IsCancellationRequested && MissingAddresses.Any());            
        }

        #endregion


        #region Connect Adjudicator
        public static ConcurrentDictionary<string, bool> IsConnectingDict = new ConcurrentDictionary<string, bool>();
        public static async Task<bool> ConnectConsensusNode(string url, string address, string time, string uName, string signature)
        {
            var IPAddress = GetPathUtility.IPFromURL(url);
            try
            {               
                if (!IsConnectingDict.TryAdd(IPAddress, true))
                    return Globals.Nodes[IPAddress].IsConnected;

                var hubConnection = new HubConnectionBuilder()
                .WithUrl(url, options => {
                    options.Headers.Add("address", address);
                    options.Headers.Add("time", time);
                    options.Headers.Add("uName", uName);
                    options.Headers.Add("signature", signature);
                    options.Headers.Add("walver", Globals.CLIVersion);
                })                
                .Build();

                LogUtility.Log("Connecting to Consensus Node", "ConnectConsensusNode()");

                
                hubConnection.Reconnecting += (sender) =>
                {
                    LogUtility.Log("Reconnecting to Adjudicator", "ConnectConsensusNode()");
                    Console.WriteLine("[" + DateTime.Now.ToString() + "] Connection to adjudicator lost. Attempting to Reconnect.");
                    return Task.CompletedTask;
                };

                hubConnection.Reconnected += (sender) =>
                {
                    LogUtility.Log("Success! Reconnected to Adjudicator", "ConnectConsensusNode()");
                    Console.WriteLine("[" + DateTime.Now.ToString() + "] Connection to adjudicator has been restored.");
                    return Task.CompletedTask;
                };

                hubConnection.Closed += (sender) =>
                {
                    LogUtility.Log("Closed to Adjudicator", "ConnectConsensusNode()");
                    Console.WriteLine("[" + DateTime.Now.ToString() + "] Connection to adjudicator has been closed.");
                    return Task.CompletedTask;
                };                

                await hubConnection.StartAsync().WaitAsync(new TimeSpan(0, 0, 8));

                var node = Globals.Nodes[IPAddress];
                (node.NodeHeight, node.NodeLastChecked, node.NodeLatency) = await P2PClient.GetNodeHeight(hubConnection);
                Globals.Nodes[IPAddress].Connection = hubConnection;

                return true;
            }
            catch (Exception ex)
            {
                ValidatorLogUtility.Log("Failed! Connecting to Adjudicator: Reason - " + ex.ToString(), "ConnectAdjudicator()");
            }
            finally
            {
                IsConnectingDict.TryRemove(IPAddress, out _);
            }

            return false;
        }

        #endregion

        public static async Task<bool> GetBlock(long height, NodeInfo node)
        {
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
                    {
                        BlockDownloadService.BlockDict[height] = (Block, node.NodeIP);
                        return true;
                    }                        
                }
            }
            catch { }

            return false;
        }

        public static async Task<long> GetNodeHeight(NodeInfo node)
        {
            try
            {
                if (!node.IsConnected)
                    return default;
                using (var Source = new CancellationTokenSource(2000))
                    return await node.Connection.InvokeAsync<long>("SendBlockHeight", Source.Token);
            }
            catch { }
            return default;
        }
    }
}
