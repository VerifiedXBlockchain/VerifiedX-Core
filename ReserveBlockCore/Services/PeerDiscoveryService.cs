using ReserveBlockCore.Data;
using ReserveBlockCore.Models;
using ReserveBlockCore.P2P;
using ReserveBlockCore.Utilities;
using Microsoft.AspNetCore.SignalR.Client;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;

namespace ReserveBlockCore.Services
{
    /// <summary>
    /// Peer discovery service for non-validator nodes.
    /// Scans the local blockchain for validator REGISTER/HEARTBEAT transactions
    /// to extract validator IP addresses and populate the Peers DB.
    /// 
    /// Problem: Non-validator nodes could only connect to the 3 hardcoded bootstrap IPs.
    /// There was no peer exchange/gossip protocol and no chain-based discovery.
    /// 
    /// Solution:
    ///   1. Scan local blocks (no IsChainSynced gate) for validator TXs → extract IPs → add to Peers DB
    ///   2. Run again after chain sync completes to get the most up-to-date validators
    ///   3. Peer gossip as fallback (request peer lists from connected nodes)
    ///   4. Periodically clear SkipPeers so temporarily-offline nodes can be retried
    /// </summary>
    public static class PeerDiscoveryService
    {
        private static long _lastChainDiscoveryHeight = -1;
        private static long _lastSkipPeersClearTick = 0;
        private static bool _postSyncDiscoveryDone = false;

        /// <summary>How many blocks to scan backwards for validator TXs.</summary>
        private const int SCAN_DEPTH = 1000;

        /// <summary>How often to re-run chain-based discovery (in blocks) after initial run.</summary>
        private const int CHAIN_DISCOVERY_INTERVAL_BLOCKS = 500;

        /// <summary>How often to clear SkipPeers (in milliseconds) — 5 minutes.</summary>
        private const long SKIP_PEERS_CLEAR_INTERVAL_MS = 300_000;

        /// <summary>
        /// Primary discovery: Scan the local blockchain directly for validator
        /// REGISTER and HEARTBEAT transactions to extract IP addresses.
        /// 
        /// NO IsChainSynced gate — works immediately with whatever blocks are
        /// available locally. Even if only partially synced (e.g., 100 blocks behind),
        /// this will find validators from recent blocks.
        /// 
        /// Should be called again after sync completes to get the most up-to-date data.
        /// 
        /// Returns the number of new peers added.
        /// </summary>
        public static int DiscoverPeersFromChain()
        {
            var currentHeight = Globals.LastBlock?.Height ?? 0;
            if (currentHeight <= 0)
                return 0;

            // Height gate: don't re-scan if we haven't moved enough blocks since last scan
            if (_lastChainDiscoveryHeight > 0 &&
                (currentHeight - _lastChainDiscoveryHeight) < CHAIN_DISCOVERY_INTERVAL_BLOCKS)
                return 0;

            _lastChainDiscoveryHeight = currentHeight;

            try
            {
                var scanFrom = Math.Max(0, currentHeight - SCAN_DEPTH);
                var blockChain = BlockchainData.GetBlocks();
                var peerDB = Peers.GetAll();
                int added = 0;

                LogUtility.Log(
                    $"PeerDiscovery: Scanning blocks {scanFrom}→{currentHeight} for validator TXs",
                    "PeerDiscoveryService.DiscoverPeersFromChain");

                for (long h = scanFrom; h <= currentHeight; h++)
                {
                    try
                    {
                        var block = blockChain.Query().Where(x => x.Height == h).FirstOrDefault();
                        if (block?.Transactions == null || block.Transactions.Count <= 1)
                            continue;

                        foreach (var tx in block.Transactions)
                        {
                            // Look for validator registration and heartbeat transactions
                            if (tx.TransactionType != TransactionType.VBTC_V2_VALIDATOR_HEARTBEAT &&
                                tx.TransactionType != TransactionType.VBTC_V2_VALIDATOR_REGISTER)
                                continue;

                            try
                            {
                                if (string.IsNullOrEmpty(tx.Data))
                                    continue;

                                var txData = JObject.Parse(tx.Data);
                                var validatorAddress = txData["ValidatorAddress"]?.ToString();
                                var ipAddress = txData["IPAddress"]?.ToString();

                                if (string.IsNullOrEmpty(validatorAddress) || string.IsNullOrEmpty(ipAddress))
                                    continue;

                                var cleanIP = ipAddress.Replace("::ffff:", "");
                                if (string.IsNullOrEmpty(cleanIP))
                                    continue;

                                // Validate IP format
                                if (!System.Net.IPAddress.TryParse(cleanIP, out _))
                                    continue;

                                // Skip private/loopback IPs
                                if (P2PClient.IsPrivateIP(cleanIP))
                                    continue;

                                // Check if already in Peers DB
                                var existingPeer = peerDB.FindOne(x => x.PeerIP == cleanIP);
                                if (existingPeer != null)
                                {
                                    // Reset fail count for known on-chain validators
                                    if (existingPeer.FailCount > 0 || !existingPeer.IsOutgoing)
                                    {
                                        existingPeer.FailCount = 0;
                                        existingPeer.IsOutgoing = true;
                                        existingPeer.IsValidator = true;
                                        if (string.IsNullOrEmpty(existingPeer.ValidatorAddress))
                                            existingPeer.ValidatorAddress = validatorAddress;
                                        peerDB.UpdateSafe(existingPeer);
                                    }
                                    continue;
                                }

                                // Add new peer from on-chain data
                                var frostPublicKey = txData["FrostPublicKey"]?.ToString() ?? "";
                                var newPeer = new Peers
                                {
                                    IsIncoming = false,
                                    IsOutgoing = true,
                                    PeerIP = cleanIP,
                                    FailCount = 0,
                                    IsValidator = true,
                                    ValidatorAddress = validatorAddress,
                                    ValidatorPublicKey = frostPublicKey,
                                    WalletVersion = Globals.CLIVersion,
                                };

                                peerDB.InsertSafe(newPeer);
                                added++;
                            }
                            catch { /* skip malformed TX data */ }
                        }
                    }
                    catch { /* skip unreadable block */ }
                }

                if (added > 0)
                {
                    LogUtility.Log(
                        $"PeerDiscovery: Added {added} new peers from chain scan (blocks {scanFrom}→{currentHeight})",
                        "PeerDiscoveryService.DiscoverPeersFromChain");
                    ConsoleWriterService.Output(
                        $"[PeerDiscovery] Discovered {added} new peers from on-chain validator data");
                }
                else
                {
                    LogUtility.Log(
                        $"PeerDiscovery: Chain scan found 0 new peers (blocks {scanFrom}→{currentHeight})",
                        "PeerDiscoveryService.DiscoverPeersFromChain");
                }

                return added;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError(
                    $"PeerDiscovery chain scan error: {ex.Message}",
                    "PeerDiscoveryService.DiscoverPeersFromChain");
                return 0;
            }
        }

        /// <summary>
        /// Called after chain sync completes to re-run discovery with the most
        /// up-to-date blocks. Forces a re-scan regardless of the height gate.
        /// </summary>
        public static void RunPostSyncDiscovery()
        {
            if (_postSyncDiscoveryDone)
                return;

            _postSyncDiscoveryDone = true;
            // Reset height gate so the next DiscoverPeersFromChain call runs immediately
            _lastChainDiscoveryHeight = -1;

            var added = DiscoverPeersFromChain();
            LogUtility.Log(
                $"PeerDiscovery: Post-sync discovery completed. Added {added} peers.",
                "PeerDiscoveryService.RunPostSyncDiscovery");
        }

        /// <summary>
        /// Fallback discovery: Request peer lists from currently connected nodes.
        /// Only called when chain-based discovery hasn't provided enough peers.
        /// 
        /// Asks each connected node for its peer list via the SendPeers SignalR method,
        /// and adds any new IPs to the local Peers database.
        /// 
        /// Returns the number of new peers added.
        /// </summary>
        public static async Task<int> RequestPeersFromConnectedNodes()
        {
            if (!Globals.Nodes.Any())
                return 0;

            int totalAdded = 0;
            var peerDB = Peers.GetAll();

            // Ask up to 3 connected nodes for their peer lists
            var nodesToAsk = Globals.Nodes.Values
                .Where(n => n.IsConnected && n.Connection?.State == HubConnectionState.Connected)
                .Take(3)
                .ToList();

            foreach (var node in nodesToAsk)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    var peersJson = await node.Connection.InvokeCoreAsync<string>(
                        "SendPeers", args: Array.Empty<object?>(), cts.Token);

                    if (string.IsNullOrEmpty(peersJson))
                        continue;

                    var peerIPs = JsonConvert.DeserializeObject<List<string>>(peersJson);
                    if (peerIPs == null || peerIPs.Count == 0)
                        continue;

                    foreach (var ip in peerIPs)
                    {
                        if (string.IsNullOrEmpty(ip))
                            continue;

                        var cleanIP = ip.Replace("::ffff:", "");
                        if (string.IsNullOrEmpty(cleanIP) || P2PClient.IsPrivateIP(cleanIP))
                            continue;

                        // Skip if banned
                        if (Globals.BannedIPs.ContainsKey(cleanIP))
                            continue;

                        // Skip if already in DB
                        var exists = peerDB.FindOne(x => x.PeerIP == cleanIP);
                        if (exists != null)
                            continue;

                        var newPeer = new Peers
                        {
                            IsIncoming = false,
                            IsOutgoing = true,
                            PeerIP = cleanIP,
                            FailCount = 0,
                        };

                        peerDB.InsertSafe(newPeer);
                        totalAdded++;
                    }
                }
                catch (Exception ex)
                {
                    // Peer may not support SendPeers (older version) — skip silently
                    if (Globals.OptionalLogging)
                    {
                        ErrorLogUtility.LogError(
                            $"PeerDiscovery gossip error from {node.NodeIP}: {ex.Message}",
                            "PeerDiscoveryService.RequestPeersFromConnectedNodes");
                    }
                }
            }

            if (totalAdded > 0)
            {
                LogUtility.Log(
                    $"PeerDiscovery: Gossip added {totalAdded} new peers from {nodesToAsk.Count} connected nodes",
                    "PeerDiscoveryService.RequestPeersFromConnectedNodes");
            }

            return totalAdded;
        }

        /// <summary>
        /// Periodically clears the SkipPeers dictionary so that temporarily-offline
        /// nodes can be retried. Called from the connection loop.
        /// 
        /// Without this, a node that fails to connect once during startup gets permanently
        /// skipped for the entire session (SkipPeers was only cleared during block download).
        /// </summary>
        public static void ClearSkipPeersIfDue()
        {
            var now = Environment.TickCount64;
            if (now - _lastSkipPeersClearTick >= SKIP_PEERS_CLEAR_INTERVAL_MS)
            {
                _lastSkipPeersClearTick = now;
                var count = Globals.SkipPeers.Count;
                if (count > 0)
                {
                    Globals.SkipPeers.Clear();
                    LogUtility.Log(
                        $"PeerDiscovery: Cleared {count} entries from SkipPeers (periodic reset)",
                        "PeerDiscoveryService.ClearSkipPeersIfDue");
                }
            }
        }
    }
}