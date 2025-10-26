using ReserveBlockCore.Extensions;
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
using System.Reflection.Metadata;

namespace ReserveBlockCore.P2P
{    
    public class ConsensusClient : IAsyncDisposable, IDisposable
    {
        public const int HeartBeatTimeout = 6000;
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
            // All disposal logic has been moved to DisposeAsyncCore to avoid sync-over-async deadlocks.
            // For proper resource cleanup, always prefer DisposeAsync() over Dispose().
        }

        protected virtual async ValueTask DisposeAsyncCore()
        {
            const int timeoutSeconds = 30;
            using var disposalCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            var disposalTasks = new List<Task>();

            foreach (var node in Globals.Nodes.Values)
            {
                if (node.Connection != null)
                {
                    disposalTasks.Add(DisposeConsensusNodeConnectionSafely(node, disposalCts.Token));
                }
            }

            if (disposalTasks.Count > 0)
            {
                try
                {
                    await Task.WhenAll(disposalTasks).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Log timeout but continue - graceful degradation
                    LogUtility.Log($"Consensus node disposal timed out after {timeoutSeconds} seconds, some connections may not be properly closed", "ConsensusClient.DisposeAsyncCore");
                }
                catch (Exception ex)
                {
                    // Log general errors but don't throw to ensure disposal completes
                    ErrorLogUtility.LogError($"Error during consensus node disposal: {ex}", "ConsensusClient.DisposeAsyncCore");
                }
            }
        }

        private static async Task DisposeConsensusNodeConnectionSafely(NodeInfo node, CancellationToken cancellationToken)
        {
            try
            {
                await node.Connection.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Log but don't throw - continue disposing other nodes
                ErrorLogUtility.LogError($"Error disposing consensus connection for node {node.NodeIP}: {ex.Message}", "ConsensusClient.DisposeConsensusNodeConnectionSafely");
            }
        }

        #endregion

        #region Consensus Code

        public enum RunType
        {
            Initial,
            Middle,
            Last
        }

        public static HashSet<string> AddressesToWaitFor(long height, int methodCode, int wait)
        {
            var Now = TimeUtil.GetMillisecondTime();            
            return Globals.Nodes.Values.Where(x => Now - x.LastMethodCodeTime < wait && ((x.NodeHeight + 2 == height && methodCode == 0) ||
                (x.NodeHeight + 1 == height && (x.MethodCode == methodCode || (x.MethodCode == methodCode - 1 && x.IsFinalized)))))
                .Select(x => x.Address).ToHashSet();
        }

        public static HashSet<string> HashAddressesToWaitFor(long height, int methodCode, int wait)
        {
            var Now = TimeUtil.GetMillisecondTime();
            return Globals.Nodes.Values.Where(x => Now - x.LastMethodCodeTime < wait && ((x.NodeHeight + 2 == height && methodCode == 0) || 
                (x.NodeHeight + 1 == height && (x.MethodCode == methodCode || (x.MethodCode == methodCode + 1 && !x.IsFinalized)))))
                .Select(x => x.Address).ToHashSet();
        }

        public static string[] RotateFrom(string[] arr, string elem)
        {
            var Index = arr.Select((x, i) => (x, i)).Where(x => x.x == elem).Select(x => (int?)x.i).FirstOrDefault() ?? -1;
            if (Index == -1)
                return arr;
            return arr.Skip(Index).Concat(arr.Take(Index)).ToArray();
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
                
                hubConnection.Reconnecting += (sender) =>
                {
                    LogUtility.Log("Reconnecting to Adjudicator", "ConnectConsensusNode()");
                    Console.WriteLine("[" + DateTime.Now.ToString() + $"] Connection to consensus node {IPAddress} lost. Attempting to Reconnect.");
                    return Task.CompletedTask;
                };

                hubConnection.Reconnected += (sender) =>
                {
                    LogUtility.Log("Success! Reconnected to Adjudicator", "ConnectConsensusNode()");
                    Console.WriteLine("[" + DateTime.Now.ToString() + $"] Connection to consensus node {IPAddress} has been restored.");
                    return Task.CompletedTask;
                };

                hubConnection.Closed += (sender) =>
                {                    
                    return Task.CompletedTask;
                };

                await hubConnection.StartAsync(new CancellationTokenSource(8000).Token);

                var node = Globals.Nodes[IPAddress];
                (node.NodeHeight, node.NodeLastChecked, node.NodeLatency) = await P2PClient.GetNodeHeight(hubConnection);
                Globals.Nodes[IPAddress].Connection = hubConnection;

                return true;
            }
            catch (Exception ex)
            {
                ValidatorLogUtility.Log($"Failed! Connecting to consensus node {IPAddress}: Reason - " + ex.ToString(), "ConnectAdjudicator()");
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
            long blockSize = 0;
            Block Block = null;
            try
            {
                var source = new CancellationTokenSource(HeartBeatTimeout);
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
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Unknown Error: {ex.ToString()}", "ConsensusClient.GetBlock()");
            }

            return false;
        }

        public static async Task<long> GetNodeHeight(NodeInfo node)
        {
            try
            {
                if (!node.IsConnected)
                    return default;
                using (var Source = new CancellationTokenSource(HeartBeatTimeout))
                    return await node.Connection.InvokeAsync<long>("SendBlockHeight", Source.Token);
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Unknown Error: {ex.ToString()}", "ConsensusClient.GetNodeHeight()");
            }
            return default;
        }
    }
}
