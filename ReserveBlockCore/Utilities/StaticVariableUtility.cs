﻿using ReserveBlockCore.Data;
using ReserveBlockCore.EllipticCurve;
using ReserveBlockCore.Models;
using ReserveBlockCore.P2P;
using ReserveBlockCore.Services;
using Spectre.Console;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ReserveBlockCore.Utilities
{
    public class StaticVariableUtility
    {
        public static async void PrintStaticVariables()
        {
            var staticVars = await GetStaticVars();

            Console.WriteLine(staticVars);
            
            Console.WriteLine("End.");
        }
        public static async Task<string> GetStaticVars()
        {
            var currentTime = TimeUtil.GetTime();
            var peersConnected = await P2PClient.ArePeersConnected();
            var bannedPeers = Peers.BannedPeers();
            var blockHeight = Globals.LastBlock.Height;
            var accounts = AccountData.GetAccounts();
            var localValidator = accounts.FindOne(x => x.IsValidating == true);
            var validator = localValidator != null ? localValidator.Address : "No Validator";
            var nodes = Globals.Nodes;
            var seeds = Globals.SeedDict;
            var lastBlock = Globals.LastBlock;
            var adjudicator = (Globals.AdjudicateAccount != null).ToString();
            var adjudicatorConnection = Globals.AdjNodes.Values.Any(x => x.IsConnected).ToString();
            var beaconConnection = P2PClient.IsBeaconConnected.ToString();            
            var isChainSynced = Globals.IsChainSynced.ToString();
            var peerCount = P2PServer.GetConnectedPeerCount();
            var valCount = await P2PValidatorServer.GetConnectedValCount();            
            var hdWallet = Globals.HDWallet.ToString();
            var reportedIPs = string.Join("<-->", Globals.ReportedIPs.Select(x => new { IP = x.Key, Occurrences = x.Value }));
            var mostLikelyIP = P2PClient.MostLikelyIP();
            var isWalletEncrypted = Globals.IsWalletEncrypted;            
            var beaconReference = Globals.BeaconReference.Reference;
            var dupIPs = Globals.DuplicateAdjIP.ToString();
            var dupAddr = Globals.DuplicateAdjAddr.ToString();
            var explorerData = Globals.ExplorerValDataLastSendSuccess.ToString();
            var explorerDataResponse = Globals.ExplorerValDataLastSendResponseCode;
            var explorerDataLastSent = Globals.ExplorerValDataLastSend.ToString();
            var blockLastSent = Globals.LastBlockAddedTimestamp.ToLocalDateTimeFromUnix().ToString();
            var lastDiff = Globals.BlockTimeDiff.ToString();
            var currentDiff = (currentTime - Globals.LastBlockAddedTimestamp).ToString();

            var balance = "Total Balance: " + accounts.FindAll().Sum(x => x.Balance);
            var validatorAddress = "Validator Address: " + Globals.ValidatorAddress;            
            var isBlocksDownloading = "Blocks Downloading: " + (Globals.BlocksDownloadSlim.CurrentCount == 0).ToString();
            var isChainSyncing = "Chain Sync State (True = done, false = blocks downloading): " + isChainSynced;            
            var isPeersConnecting = "Peers Connecting Startup: " + (!Globals.Nodes.Any()).ToString();
            var isStopAllTimers = "Stop all timers: " + Globals.StopAllTimers.ToString();
            var isQueueProcessing = "Queue Processing: " + (Globals.BlocksDownloadSlim.CurrentCount == 0);
            var isPeerConnected = "Peers connected: " + peersConnected.ToString();
            var peerConnectedCount = "Peers connected Count: " + Globals.Nodes.Count().ToString();
            var peerConnectedToMe = "Peers connected to you: " + peerCount.ToString();
            var blockHeightStr = "Block Height: " + blockHeight.ToString();
            var validatorStr = "Validator Address From DB: " + validator;
            var remoteLock = "Remote Lock: " + Globals.RemoteCraftLock.ToString();
            var remoteLockTime = "Remote Lock Time: " + (Globals.RemoteCraftLockTime == null ?  "NA" : Globals.RemoteCraftLockTime.Value.ToShortTimeString());
            var isResyncing = "Chain Resyncing? : " + Globals.IsResyncing.ToString();
            var isCorrupt = "Database Corruption Detected? : " + Globals.DatabaseCorruptionDetected.ToString();
            var adjConnection = "Adjudicator Connected?: " + adjudicatorConnection;
                                            
            var hdWalletText = $"HD Wallet? : {hdWallet}";
            var reportedIPText = $"Reported IPs: {reportedIPs}";
            var externalIPText = $"External IP: {mostLikelyIP}";
            var isWalletEncryptedText = $"Wallet Encrypted? {isWalletEncrypted}";
            var beaconRefText = $"Beacon Reference Id: {beaconReference}";
            var beacConnection = "Beacon Connected?: " + beaconConnection;
            var bannedPeersText = $"Banned Peer Count: {bannedPeers}";
            var dupIPText = $"Duplicate IPs found Validating? {dupIPs}";
            var dupAddrText = $"Duplicate Addresses found Validating? {dupAddr}";
            var explorerDataText = $"Explorer Last Send Success? {explorerData}";
            var explorerLastSendText = $"Explorer Last Send Date: {explorerDataLastSent}";
            var explorerLastSendResponseText = $"Explorer Last Send Date: {explorerDataResponse}";

            var blockLastSentText = $"Block last sent at: {blockLastSent}";
            var lastDiffText = $"Block last delay: {lastDiff}";
            var currentDiffText = $"Current block delay: {currentDiff}";
            var blockDiffAvg = $"Block Diff Avg: {BlockDiffService.CalculateAverage().ToString("#.##")} secs. Avg of: {Globals.BlockDiffQueue.Count()}/3456 Blocks.";

            var cliVersionText = $"CLI Version: {Globals.CLIVersion}";
            var githubVersionTag = $"GitHub Version: {Globals.GitHubVersion}";

            var timeSyncError = $"Time Sync Error? {Globals.TimeSyncError}. (If true please check system clock and ensure its working)";
            var timeSync = $"Time in Sync? {Globals.TimeInSync}";
            var lastSyncDiff = $"Last Time Sync Diff: {Globals.TimeSyncDiff}";
            var lastSyncTime = $"Last Time Sync Date: {Globals.TimeSyncLastDate}";

            var startMemory = $"Start Memory: {Globals.StartMemory}";
            var currentMemory = $"Current Memory: {Globals.CurrentMemory}";
            var systemMemory = $"System Memory: {Globals.SystemMemory}";

            var stunServerText = $"STUN Server Start: {Globals.SelfSTUNServer}";
            var stunServerPortText = $"STUN Server Port: {Globals.SelfSTUNPort}";
            var stunServerRunning = $"STUN Server Active: {Globals.STUNServerRunning}";

            var lastBlockWon = Globals.LastWonBlock != null ? $"Last Block Won: {Globals.LastWonBlock.Height}" : "No blocks reported yet.";
            var valCountText = $"Validators Connected to you: {valCount}";

            var lastBlockInfo = "Height: " + lastBlock.Height.ToString() + " - Hash: " + lastBlock.Hash + " Timestamp: " + lastBlock.Timestamp
                + " - Validator: " + lastBlock.Validator;

            var electrumxConnected = Globals.ElectrumXConnected ? "ElectrumX Connected? : Yes" : "ElectrumX Connected? : No";
            var lastElectrumXCommunication = $"ElectrumX Last Communication: {Globals.ElectrumXLastCommunication}";
            var btcAccountCheck = Globals.BTCAccountCheckRunning ? "Account Check Running? : Yes" : "Account Check Running? : No";

            StringBuilder strBld = new StringBuilder();
            strBld.AppendLine("---------------------------------------------------------------------");
            strBld.AppendLine(cliVersionText);
            strBld.AppendLine("---------------------------------------------------------------------");
            strBld.AppendLine(githubVersionTag);
            strBld.AppendLine("---------------------------------------------------------------------");
            strBld.AppendLine(validatorAddress);
            strBld.AppendLine("---------------------------------------------------------------------");
            strBld.AppendLine(systemMemory);
            strBld.AppendLine("---------------------------------------------------------------------");
            strBld.AppendLine(startMemory);
            strBld.AppendLine("---------------------------------------------------------------------");
            strBld.AppendLine(currentMemory);
            strBld.AppendLine("---------------------------------------------------------------------");
            strBld.AppendLine(hdWalletText);
            strBld.AppendLine("---------------------------------------------------------------------");
            strBld.AppendLine(beaconRefText);
            strBld.AppendLine("---------------------------------------------------------------------");
            strBld.AppendLine(beacConnection);
            strBld.AppendLine("---------------------------------------------------------------------");
            strBld.AppendLine(isWalletEncryptedText);
            strBld.AppendLine("---------------------------------------------------------------------");
            strBld.AppendLine(isCorrupt);
            strBld.AppendLine("---------------------------------------------------------------------");            
            strBld.AppendLine(isBlocksDownloading);
            strBld.AppendLine("---------------------------------------------------------------------");
            strBld.AppendLine(isChainSyncing);
            strBld.AppendLine("---------------------------------------------------------------------");
            strBld.AppendLine(balance);
            strBld.AppendLine("---------------------------------------------------------------------");
            strBld.AppendLine(isPeersConnecting);
            strBld.AppendLine("---------------------------------------------------------------------");
            strBld.AppendLine(peerConnectedToMe);
            strBld.AppendLine("---------------------------------------------------------------------");
            strBld.AppendLine(bannedPeersText);
            strBld.AppendLine("---------------------------------------------------------------------");
            strBld.AppendLine(isStopAllTimers);
            strBld.AppendLine("---------------------------------------------------------------------");
            strBld.AppendLine(isQueueProcessing);
            strBld.AppendLine("---------------------------------------------------------------------");
            strBld.AppendLine(isPeerConnected);
            strBld.AppendLine("---------------------------------------------------------------------");
            strBld.AppendLine(peerConnectedCount);
            strBld.AppendLine("---------------------------------------------------------------------");
            strBld.AppendLine(blockHeightStr);
            strBld.AppendLine("---------------------------------------------------------------------");
            strBld.AppendLine(validatorStr);
            strBld.AppendLine("---------------------------------------------------------------------");
            strBld.AppendLine(dupIPText);
            strBld.AppendLine("---------------------------------------------------------------------");
            strBld.AppendLine(dupAddrText);
            strBld.AppendLine("---------------------------------------------------------------------");
            strBld.AppendLine(remoteLock);
            strBld.AppendLine("---------------------------------------------------------------------");
            strBld.AppendLine(remoteLockTime);
            strBld.AppendLine("---------------------------------------------------------------------");
            strBld.AppendLine(isResyncing);
            strBld.AppendLine("---------------------------------------------------------------------");
            strBld.AppendLine(explorerDataText);
            strBld.AppendLine("---------------------------------------------------------------------");
            strBld.AppendLine(explorerLastSendText);
            strBld.AppendLine("---------------------------------------------------------------------");
            strBld.AppendLine(explorerLastSendResponseText);
            strBld.AppendLine("---------------------------------------------------------------------");
            strBld.AppendLine(adjConnection);
            strBld.AppendLine("---------------------------------------------------------------------");
            strBld.AppendLine(btcAccountCheck);
            strBld.AppendLine("---------------------------------------------------------------------");
            strBld.AppendLine(electrumxConnected);
            strBld.AppendLine("---------------------------------------------------------------------");
            strBld.AppendLine(lastElectrumXCommunication);
            strBld.AppendLine("---------------------------------------------------------------------");
            if (Globals.SelfSTUNServer)
            {
                strBld.AppendLine(stunServerText);
                strBld.AppendLine("---------------------------------------------------------------------");
                strBld.AppendLine(stunServerPortText);
                strBld.AppendLine("---------------------------------------------------------------------");
                strBld.AppendLine(stunServerRunning);
                strBld.AppendLine("---------------------------------------------------------------------");
            }

            if(!string.IsNullOrEmpty(Globals.ValidatorAddress))
            {
                strBld.AppendLine(lastBlockWon);
                strBld.AppendLine("---------------------------------------------------------------------");
                strBld.AppendLine(valCountText);
                strBld.AppendLine("---------------------------------------------------------------------");
            }
            strBld.AppendLine("-------------------------------Node Info-----------------------------");
            if(nodes.Count() > 0)
            {
                nodes.Values.ToList().ForEach(x => {
                    var ip = x.NodeIP;
                    var lastcheck = x.NodeLastChecked != null ? x.NodeLastChecked.Value.ToLocalTime().ToLongTimeString() : "NA";
                    var height = x.NodeHeight.ToString();
                    var latency = x.NodeLatency.ToString();

                    strBld.AppendLine("Node: " + ip + " - Last Checked: " + lastcheck + " - Height: " + height + " - Latency: " + latency);
                    strBld.AppendLine("---------------------------------------------------------------------");
                });
            }
            if(Globals.ValidatorNodes.Count() > 0)
            {
                strBld.AppendLine("--------------------------Validator Node Info------------------------");
                Globals.ValidatorNodes.Values.ToList().ForEach(x => {
                    var ip = x.NodeIP;
                    var lastcheck = x.NodeLastChecked != null ? x.NodeLastChecked.Value.ToLocalTime().ToLongTimeString() : "NA";
                    var height = x.NodeHeight.ToString();
                    var latency = x.NodeLatency.ToString();

                    strBld.AppendLine("Node: " + ip + " - Last Checked: " + lastcheck + " - Height: " + height + " - Latency: " + latency);
                    strBld.AppendLine("---------------------------------------------------------------------");
                });

            }
            if(seeds.Count() > 0)
            {
                foreach (var seed in seeds.ToList())
                {
                    strBld.AppendLine("Seed URL: " + seed.Key + " - Status: " + seed.Value);
                    strBld.AppendLine("---------------------------------------------------------------------");
                }
            }
            strBld.AppendLine("---------------------------------------------------------------------");
            strBld.AppendLine(reportedIPText);
            strBld.AppendLine("---------------------------------------------------------------------");
            strBld.AppendLine(externalIPText);
            strBld.AppendLine("---------------------------------------------------------------------");
            strBld.AppendLine("-------------------------------Block Info----------------------------");
            strBld.AppendLine(blockDiffAvg);
            strBld.AppendLine("---------------------------------------------------------------------");
            strBld.AppendLine(blockLastSentText);
            strBld.AppendLine("---------------------------------------------------------------------");
            strBld.AppendLine(lastDiffText);
            strBld.AppendLine("---------------------------------------------------------------------");
            strBld.AppendLine(currentDiffText);
            strBld.AppendLine("---------------------------------------------------------------------");
            strBld.AppendLine(lastBlockInfo);
            strBld.AppendLine("---------------------------------------------------------------------");

            strBld.AppendLine("-------------------------------Time Sync-----------------------------");
            strBld.AppendLine(timeSyncError);
            strBld.AppendLine("---------------------------------------------------------------------");
            strBld.AppendLine(timeSync);
            strBld.AppendLine("---------------------------------------------------------------------");
            strBld.AppendLine(lastSyncDiff);
            strBld.AppendLine("---------------------------------------------------------------------");
            strBld.AppendLine(lastSyncTime);
            strBld.AppendLine("---------------------------------------------------------------------");

            return strBld.ToString();
        }

        public static async Task<string> GetClientInfo()
        {
            var network = Globals.IsTestNet == true ? "TestNet" : "MainNet";
            var mostLikelyIP = P2PClient.MostLikelyIP();

            var databaseLocation = Globals.IsTestNet != true ? "Databases" : "DatabasesTestNet";
            var mainFolderPath = Globals.IsTestNet != true ? "RBX" : "RBXTest";

            var osDesc = RuntimeInformation.OSDescription;
            var processArch = RuntimeInformation.ProcessArchitecture;
            var netFramework = RuntimeInformation.FrameworkDescription;

            string path = "";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                string homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                path = homeDirectory + Path.DirectorySeparatorChar + mainFolderPath.ToLower() + Path.DirectorySeparatorChar + databaseLocation + Path.DirectorySeparatorChar;
            }
            else
            {
                if (Debugger.IsAttached)
                {
                    path = Directory.GetCurrentDirectory() + Path.DirectorySeparatorChar + "DBs" + Path.DirectorySeparatorChar + databaseLocation + Path.DirectorySeparatorChar;
                }
                else
                {
                    path = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + Path.DirectorySeparatorChar + mainFolderPath + Path.DirectorySeparatorChar + databaseLocation + Path.DirectorySeparatorChar;
                }
            }

            if (!string.IsNullOrEmpty(Globals.CustomPath))
            {
                path = Globals.CustomPath + mainFolderPath + Path.DirectorySeparatorChar + databaseLocation + Path.DirectorySeparatorChar;
            }

            var networkText = "Current Network: " + network;
            var mostLikelyIPText = "Reported IP: " + mostLikelyIP;
            var osText = "OS Description: " + osDesc;
            var processArchText = "Processor Architecture: " + processArch;
            var netFrameworkText = ".Net Core: " + netFramework;
            var pathText = "Database Folder Location: " + path;

            StringBuilder strBld = new StringBuilder();
            strBld.AppendLine(networkText);
            strBld.AppendLine("---------------------------------------------------------------------");
            strBld.AppendLine(mostLikelyIPText);
            strBld.AppendLine("---------------------------------------------------------------------");
            strBld.AppendLine(osText);
            strBld.AppendLine("---------------------------------------------------------------------");
            strBld.AppendLine(processArchText);
            strBld.AppendLine("---------------------------------------------------------------------");
            strBld.AppendLine(netFrameworkText);
            strBld.AppendLine("---------------------------------------------------------------------");
            strBld.AppendLine(pathText);
            strBld.AppendLine("---------------------------------------------------------------------");

            return strBld.ToString();
        }
    }
}
