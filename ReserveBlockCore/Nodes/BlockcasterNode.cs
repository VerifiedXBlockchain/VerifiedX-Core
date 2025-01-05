using Elmah.ContentSyndication;
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
using System.Data.SqlTypes;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Reflection.Metadata;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks.Dataflow;
using System.Xml.Schema;


namespace ReserveBlockCore.Nodes
{
    public class BlockcasterNode : IHostedService, IDisposable
    {
        public static IHubContext<P2PBlockcasterServer> HubContext;
        private readonly IHubContext<P2PBlockcasterServer> _hubContext;
        private readonly IHostApplicationLifetime _appLifetime;
        private static ConcurrentBag<(string, long, string)> ValidatorApprovalBag = new ConcurrentBag<(string, long, string)>();
        const int PROOF_COLLECTION_TIME = 7000; // 7 seconds
        const int APPROVAL_WINDOW = 12000;      // 12 seconds
        const int CASTER_VOTE_WINDOW = 3000;
        const int BLOCK_REQUEST_WINDOW = 12000;  // 12 seconds
        public static ReplacementRound _currentRound;
        public static List<string> _allCasterAddresses;

        public BlockcasterNode(IHubContext<P2PBlockcasterServer> hubContext, IHostApplicationLifetime appLifetime)
        {
            _hubContext = hubContext;
            HubContext = hubContext;
            _appLifetime = appLifetime;
        }

        public async Task StartAsync(CancellationToken stoppingToken)
        {
            _ = StartConsensus();

            _ = MonitorCasters();
        }

        private static async Task MonitorCasters()
        {
            while (true && !string.IsNullOrEmpty(Globals.ValidatorAddress))
            {
                var delay = Task.Delay(new TimeSpan(0, 0, 5));
                
                if (!Globals.IsBlockCaster)
                {
                    await Task.Delay(new TimeSpan(0, 0, 30));
                    continue;
                }

                if (!Globals.BlockCasters.Any())
                {
                    //Get blockcasters if empty.
                    await ValidatorNode.GetBlockcasters();
                    await delay;
                    continue;
                }

                await PingCasters();

                var casterList = Globals.BlockCasters.ToList();

                if (casterList.Count() < Globals.MaxBlockCasters)
                {
                    await InitiateReplacement(Globals.LastBlock.Height);
                }

                await Task.Delay(10000);

            }
        }

        public static async Task PingCasters()
        {
            var casterList = Globals.BlockCasters.ToList();

            if (!casterList.Any())
                return;

            HashSet<string> removeList = new HashSet<string>();

            int retryCount = 0;
            foreach (var caster in casterList)
            {
                do
                {
                    try
                    {
                        using (var client = Globals.HttpClientFactory.CreateClient())
                        {
                            
                            var uri = $"http://{caster.PeerIP.Replace("::ffff:", "")}:{Globals.ValPort}/valapi/validator/heartbeat";

                            var response = await client.GetAsync(uri).WaitAsync(new TimeSpan(0, 0, 3));
                            await Task.Delay(100);

                            if (!response.IsSuccessStatusCode)
                            {
                                retryCount++;
                                if (retryCount == 3)
                                {
                                    removeList.Add(caster.PeerIP);
                                }
                            }
                            else
                            {
                                break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        retryCount++;
                        if (retryCount == 3)
                        {
                            removeList.Add(caster.PeerIP);
                        }
                    }
                } while (retryCount < 4);
                
            }

            if(removeList.Any())
            { 
                var nCasterList = casterList.Where(x => !removeList.Contains(x.PeerIP)).ToList();
                ConcurrentBag<Peers> nBag = new ConcurrentBag<Peers>();
                nCasterList.ForEach(x => { nBag.Add(x); });
                Globals.BlockCasters.Clear();
                Globals.BlockCasters = nBag;
            }
        }

        public static async Task<bool> InitiateReplacement(long blockHeight)
        {
            // Only start a new round if there isn't one in progress
            bool result = false;
            var casterList = Globals.BlockCasters.ToList();
            var casterIPList = casterList.Select(x => x.PeerIP).ToHashSet();
            var currentBlockCasters = Globals.NetworkValidators.Values.Where(x => casterIPList.Contains(x.IPAddress)).ToList();
            _currentRound = new ReplacementRound();

            while (_currentRound.EndTime > TimeUtil.GetTime())
            {
                if (_currentRound.CasterSeeds.Values.Any(x => x == 0))
                {
                    foreach (var caster in casterList)
                    {
                        try
                        {
                            _currentRound.CasterSeeds.TryGetValue(caster.PeerIP, out var seedPiece);
                            if (seedPiece == 0)
                            {
                                using (var client = Globals.HttpClientFactory.CreateClient())
                                {
                                    var uri = $"http://{caster.PeerIP.Replace("::ffff:", "")}:{Globals.ValPort}/valapi/validator/SendSeedPart";
                                    var response = await client.GetAsync(uri).WaitAsync(new TimeSpan(0, 0, 3));
                                    await Task.Delay(100);

                                    if (response.IsSuccessStatusCode)
                                    {
                                        var answer = await response.Content.ReadAsStringAsync();
                                        if (!string.IsNullOrEmpty(answer))
                                        {
                                            if (answer != "0")
                                            {
                                                var numAnswer = Convert.ToInt32(answer);
                                                _currentRound.CasterSeeds[caster.PeerIP] = numAnswer;
                                            }
                                        }
                                    }
                                    else 
                                    { 
                                        //round most likely finalized. Will have to wait for new one.
                                    }
                                }
                            }

                            await Task.Delay(200);
                        }
                        catch (Exception ex)
                        {
                            
                        }
                    }
                    await Task.Delay(50);
                    continue;
                }
                else
                {
                    var seedString = new StringBuilder();
                    var casterSeedsOrdered = _currentRound.CasterSeeds.Values.OrderBy(x => x);
                    foreach (var castSeed in casterSeedsOrdered)
                    {
                        seedString.Append(castSeed);
                    }
                    int seed = GenerateDeterministicSeed(currentBlockCasters, seedString.ToString());
                    Random random = new Random(seed);

                    var availableValidators = Globals.NetworkValidators.Values
                        .Except(currentBlockCasters)
                        .ToList();

                    if (availableValidators.Count == 0)
                    {
                        ConsoleWriterService.OutputVal("No available validators to select from.");
                        break;
                    }

                    int index = random.Next(availableValidators.Count);
                    var nCaster =  availableValidators[index];

                    if(nCaster == null)
                    {
                        ConsoleWriterService.OutputVal("Peers available, but nCaster was null.");
                        break;
                    }

                    bool casterConsensusReached = false;
                    _currentRound.MyChosenCaster = nCaster;

                    while (_currentRound.EndTime > TimeUtil.GetTime())
                    {
                        if(!_currentRound.NetworkValidators.Values.Any(x => x == null) && _currentRound.NetworkValidators.Count() > 1)
                        {
                            var mostFrequentValidator = _currentRound.NetworkValidators
                                .Where(kvp => kvp.Value != null) 
                                .GroupBy(kvp => kvp.Value)       // Group by NetworkValidator
                                .OrderByDescending(g => g.Count()) // Order by count in descending order
                                .FirstOrDefault()?.Key;

                            if (mostFrequentValidator != null)
                            {
                                nCaster = mostFrequentValidator;
                                casterConsensusReached = true;
                                break;
                            }
                        }

                        var postData = JsonConvert.SerializeObject(nCaster);
                        var httpContent = new StringContent(postData, Encoding.UTF8, "application/json");

                        foreach (var caster in casterList)
                        {
                            try
                            {
                                _currentRound.NetworkValidators.TryGetValue(caster.PeerIP, out var nValCheck);

                                if (nValCheck == null)
                                {
                                    using (var client = Globals.HttpClientFactory.CreateClient())
                                    {
                                        var uri = $"http://{caster.PeerIP.Replace("::ffff:", "")}:{Globals.ValPort}/valapi/validator/GetCasterVote";
                                        var response = await client.GetAsync(uri).WaitAsync(new TimeSpan(0, 0, 3));
                                        await Task.Delay(100);

                                        if (response.IsSuccessStatusCode)
                                        {
                                            var answer = await response.Content.ReadAsStringAsync();
                                            if (!string.IsNullOrEmpty(answer))
                                            {
                                                var networkVal = JsonConvert.DeserializeObject<NetworkValidator>(answer);
                                                if(networkVal != null)
                                                {
                                                    _currentRound.NetworkValidators[caster.PeerIP] =  networkVal;
                                                }
                                            }
                                        }
                                    }
                                }

                                await Task.Delay(200);
                            }
                            catch (Exception ex)
                            {
                                break;
                            }
                        }
                        await Task.Delay(1000);
                    }


                    if(casterConsensusReached)
                    {
                        var peerDB = Peers.GetAll();

                        var singleVal = peerDB.FindOne(x => x.PeerIP == nCaster.IPAddress);
                        if (singleVal != null)
                        {
                            singleVal.IsValidator = true;

                            if (singleVal.ValidatorAddress == null)
                                singleVal.ValidatorAddress = nCaster.Address;

                            if (singleVal.ValidatorPublicKey == null)
                                singleVal.ValidatorPublicKey = nCaster.PublicKey;

                            peerDB.UpdateSafe(singleVal);

                            await AddCaster(singleVal);
                        }
                        else
                        {
                            Peers nPeer = new Peers
                            {
                                IsIncoming = false,
                                IsOutgoing = true,
                                PeerIP = nCaster.IPAddress,
                                FailCount = 0,
                                IsValidator = true,
                                ValidatorAddress = nCaster.Address,
                                ValidatorPublicKey = nCaster.PublicKey,
                                WalletVersion = Globals.CLIVersion,
                            };

                            peerDB.InsertSafe(nPeer);

                            await AddCaster(nPeer);

                            result = true;
                        }
                        break;
                    }
                }
            }

            return result;
        }
        public static async Task AddCaster(Peers caster)
        {
            var casterExist = Globals.BlockCasters.Any(x => x.PeerIP == caster.PeerIP);

            if(!casterExist)
            {
                Stopwatch sw = Stopwatch.StartNew();
                while(sw.Elapsed.Seconds < 10)
                {
                    Globals.BlockCasters.Add(caster);
                    if (Globals.BlockCasters.Any(x => x.PeerIP == caster.PeerIP))
                        break;
                }
            }
        }
        public static int GenerateDeterministicSeed(List<NetworkValidator> currentNodes, string seed)
        {
            // Concatenate the public keys and the block height for deterministic input
            var sb = new StringBuilder();
            foreach (var node in currentNodes)
            {
                sb.Append(node.PublicKey);
            }
            sb.Append(seed);

            // Generate a hash of the concatenated string
            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));

                // Convert the first 4 bytes of the hash into an integer seed
                return BitConverter.ToInt32(hashBytes, 0);
            }
        }

        private static async Task StartConsensus()
        {
            //start consensus run here.  
            var delay = Task.Delay(new TimeSpan(0, 0, 5));
            var EpochTime = Globals.IsTestNet ? 1731454926600L : 1674172800000L;
            var BeginBlock = Globals.IsTestNet ? Globals.V4Height : Globals.V3Height;
            var PreviousHeight = -1L;
            var BlockDelay = Task.CompletedTask;
            ConsoleWriterService.OutputVal("Booting up consensus loop");

            while (true && !string.IsNullOrEmpty(Globals.ValidatorAddress))
            {
                var casterList = Globals.BlockCasters.ToList();
                if (casterList.Any())
                {
                    if (casterList.Exists(x => x.ValidatorAddress == Globals.ValidatorAddress))
                        Globals.IsBlockCaster = true;
                    else
                        Globals.IsBlockCaster = false;
                }
                if (!Globals.IsBlockCaster)
                {
                    await Task.Delay(new TimeSpan(0, 0, 30));
                    continue;
                }

                ConsoleWriterService.OutputVal("Top of consensus loop");

                Block? block = null;

                if (!Globals.BlockCasters.Any())
                {
                    //Get blockcasters if empty.
                    await ValidatorNode.GetBlockcasters();
                    await delay;
                    continue;
                }

                try
                {
                    var Height = Globals.LastBlock.Height + 1;

                    if (Height != Globals.LastBlock.Height + 1)
                        continue;

                    if (PreviousHeight == -1L) // First time running
                    {
                        await WaitForNextConsensusRound();
                    }

                    if (PreviousHeight != Height)
                    {
                        PreviousHeight = Height;
                        await Task.WhenAll(BlockDelay, Task.Delay(1500));
                        var CurrentTime = TimeUtil.GetMillisecondTime();
                        var DelayTimeCorrection = Globals.BlockTime * (Height - BeginBlock) - (CurrentTime - EpochTime);
                        var DelayTime = Math.Min(Math.Max(Globals.BlockTime + DelayTimeCorrection, Globals.BlockTimeMin), Globals.BlockTimeMax);
                        BlockDelay = Task.Delay((int)DelayTime);
                        ConsoleWriterService.OutputVal("\r\nNext Consensus Delay: " + DelayTime + " (" + DelayTimeCorrection + ")");
                    }



                    var consensusHeader = new ConsensusHeader();
                    consensusHeader.Height = Height;
                    consensusHeader.Timestamp = TimeUtil.GetTime();
                    consensusHeader.NetworkValidatorCount = Globals.NetworkValidators.Count;

                    ValidatorApprovalBag.Clear();
                    ValidatorApprovalBag = new ConcurrentBag<(string, long, string)>();
                    //Generate Proofs for ALL vals
                    ConsoleWriterService.OutputVal($"\r\nGenerating Proofs for height: {Height}.");
                    var proofs = await ProofUtility.GenerateProofs();
                    ConsoleWriterService.OutputVal($"\r\n{proofs.Count()} Proofs Generated");
                    var winningProof = await ProofUtility.SortProofs(proofs);
                    ConsoleWriterService.OutputVal($"\r\nSorting Proofs");

                    if (winningProof != null && proofs.Count() > 1)
                    {
                        ConsoleWriterService.OutputVal($"\r\nAttempting Proof on Address: {winningProof.Address}");
                        var verificationResult = false;
                        List<string> ExcludeValList = new List<string>();
                        while (!verificationResult)
                        {
                            if (winningProof != null)
                            {
                                var nextblock = Globals.LastBlock.Height + 1;
                                Stopwatch sw = Stopwatch.StartNew();
                                var verificationResultTuple = await ProofUtility.VerifyWinnerAvailability(winningProof, nextblock);
                                sw.Stop();
                                verificationResult = verificationResultTuple.Item1;

                                Globals.NetworkValidators.TryGetValue(winningProof.Address, out var validator);

                                if (!verificationResult)
                                {
                                    if (validator != null)
                                    {
                                        validator.CheckFailCount++;
                                        validator.Latency = sw.ElapsedMilliseconds;
                                        if (validator.CheckFailCount <= 3)
                                        {
                                            Globals.NetworkValidators[winningProof.Address] = validator;
                                        }
                                        else
                                        {
                                            Globals.NetworkValidators.TryRemove(winningProof.Address, out _);
                                        }
                                    }
                                    ExcludeValList.Add(winningProof.Address);
                                    winningProof = await ProofUtility.SortProofs(
                                        proofs.Where(x => !ExcludeValList.Contains(x.Address)).ToList()
                                    );

                                    if (winningProof == null)
                                    {
                                        ExcludeValList.Clear();
                                        ExcludeValList = new List<string>();
                                    }
                                }
                                else
                                {
                                    if (validator != null)
                                    {
                                        validator.CheckFailCount = 0;
                                        validator.Latency = sw.ElapsedMilliseconds;
                                        Globals.NetworkValidators[winningProof.Address] = validator;
                                    }

                                    block = verificationResultTuple.Item2;

                                    ExcludeValList.Clear();
                                    ExcludeValList = new List<string>();
                                }

                            }
                            else
                            {
                                ExcludeValList.Clear();
                                ExcludeValList = new List<string>();
                                break;
                            }
                        }

                        if (winningProof == null)
                        {
                            ConsoleWriterService.OutputVal($"\r\nCould not connect to any nodes for winning proof. Starting over.");
                            continue;
                        }

                        ConsoleWriterService.OutputVal($"\r\nPotential Winner Found! Address: {winningProof.Address}");
                        Globals.Proofs.Add(winningProof);

                        _ = SendWinningProof(winningProof);

                        await Task.Delay(PROOF_COLLECTION_TIME);

                        var proofSnapshot = Globals.Proofs.ToList();

                        var finalizedWinnerGroup = proofSnapshot
                            .OrderBy(x => Math.Abs(x.VRFNumber))
                            .GroupBy(x => x.Address)
                            .OrderByDescending(x => x.Count())
                            .ThenBy(x => x.Min(y => Math.Abs(y.VRFNumber)))
                            .FirstOrDefault();

                        if (finalizedWinnerGroup != null)
                        {
                            var finalizedWinner = finalizedWinnerGroup.FirstOrDefault();
                            if (finalizedWinner != null)
                            {
                                ConsoleWriterService.OutputVal($"\r\nFinalized winner : {finalizedWinner.Address}");

                                if (finalizedWinner.Address != winningProof.Address)
                                    block = null;

                                var approved = false;
                                consensusHeader.WinningAddress = finalizedWinner.Address;

                                var sw = Stopwatch.StartNew();
                                List<string> CasterApprovalList = new List<string>();
                                Dictionary<string, string> CasterVoteList = new Dictionary<string, string>();

                                //INPROGRESS~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
                                //INPROGRESS~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
                                string? terminalWinner = null;

                                while (!approved && sw.ElapsedMilliseconds < APPROVAL_WINDOW)
                                {

                                    if (Globals.LastBlock.Height >= finalizedWinner.BlockHeight)
                                        break;

                                    foreach (var caster in Globals.BlockCasters)
                                    {
                                        using (var client = Globals.HttpClientFactory.CreateClient())
                                        {
                                            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(CASTER_VOTE_WINDOW));
                                            try
                                            {
                                                var valAddr = finalizedWinner.Address;
                                                var uri = $"http://{caster.PeerIP.Replace("::ffff:", "")}:{Globals.ValPort}/valapi/validator/sendapproval/{finalizedWinner.BlockHeight}/{valAddr}";
                                                var response = await client.GetAsync(uri, cts.Token);
                                                if (response.IsSuccessStatusCode)
                                                {
                                                    CasterApprovalList.Add(caster.PeerIP);
                                                    ConsoleWriterService.OutputVal($"\r\nApproval sent to address: {caster.ValidatorAddress}.");
                                                    ConsoleWriterService.OutputVal($"IP Address: {caster.PeerIP}.");
                                                    await Task.Delay(200);
                                                    continue;
                                                }
                                                else
                                                {
                                                    await Task.Delay(200);
                                                }
                                            }
                                            catch (Exception ex)
                                            {
                                                ConsoleWriterService.OutputVal($"\r\nError sending approval to address: {finalizedWinner.Address}.");
                                                ConsoleWriterService.OutputVal($"ERROR: {ex}.");
                                            }
                                        }
                                    }

                                    await Task.Delay(200);

                                    var vBag = ValidatorApprovalBag.Where(x => x.Item2 == finalizedWinner.BlockHeight).ToList();

                                    ConsoleWriterService.OutputVal($"\r\nValidator Bag Count: {vBag.Count()}.");

                                    var approvalCount = casterList.Count() <= 5 ? 3 : 4;

                                    if (vBag.Any())
                                    {
                                        foreach(var vote in vBag)
                                        {
                                            if(!CasterVoteList.ContainsKey(vote.Item1))
                                                CasterVoteList[vote.Item1] = vote.Item3;
                                        }

                                        var result = CasterVoteList.GroupBy(x => x.Value)
                                            .Where(x => x.Count() >= approvalCount)
                                            .Select(g => new { Value = g.Key, Count = g.Count() })
                                            .OrderByDescending(g => g.Count)
                                            .FirstOrDefault();

                                        if (result != null)
                                        {
                                            if (result.Count >= approvalCount)
                                            {
                                                terminalWinner = result.Value;
                                                //If our winner does not match the consensus get the one that does.
                                                if (finalizedWinner.Address != result.Value)
                                                    block = null;

                                                if(block != null)
                                                    Globals.CasterApprovedBlockHashDict[finalizedWinner.BlockHeight] = block.Hash;

                                                approved = true;
                                                break;
                                            }
                                        }
                                    }
                                }

                                ConsoleWriterService.OutputVal($"\r\nYou did not win. Looking for block.");
                                if (Globals.LastBlock.Height < finalizedWinner.BlockHeight && approved)
                                {
                                    bool blockFound = false;
                                    bool failedToReachConsensus = false;
                                    var swb = Stopwatch.StartNew();
                                    while (!blockFound && swb.ElapsedMilliseconds < BLOCK_REQUEST_WINDOW)
                                    {
                                        //This is done if non-caster wins the block
                                        if (block != null)
                                        {
                                            ConsoleWriterService.OutputVal($"Already have block. Height: {block.Height}");
                                            var IP = finalizedWinner.IPAddress;
                                            var nextHeight = Globals.LastBlock.Height + 1;
                                            var currentHeight = block.Height;

                                            //if (!BlockDownloadService.BlockDict.ContainsKey(currentHeight))
                                            //{
                                            //    ConsoleWriterService.OutputVal($"Processing Block");
                                            //    BlockDownloadService.BlockDict[currentHeight] = (block, IP);
                                            //    if (nextHeight == currentHeight)
                                            //        await BlockValidatorService.ValidateBlocks();
                                            //    if (nextHeight < currentHeight)
                                            //        await BlockDownloadService.GetAllBlocks();
                                            //}

                                            if (currentHeight < nextHeight)
                                            {
                                                blockFound = true;
                                                //_ = AddConsensusHeaderQueue(consensusHeader);
                                                if (Globals.LastBlock.Height < block.Height)
                                                    await BlockValidatorService.ValidateBlocks();

                                                if (nextHeight == currentHeight)
                                                {
                                                    ConsoleWriterService.OutputVal($"Inside block service B");
                                                    ConsoleWriterService.OutputVal($"\r\nBlock found. Broadcasting.");
                                                    _ = Broadcast("7", JsonConvert.SerializeObject(block), "");
                                                    //_ = P2PValidatorClient.BroadcastBlock(block);
                                                }

                                                if (nextHeight < currentHeight)
                                                    await BlockDownloadService.GetAllBlocks();

                                                break;
                                            }
                                        }

                                        //if (Globals.LastBlock.Height == finalizedWinner.BlockHeight)
                                        //{
                                        //    ConsoleWriterService.OutputVal($"\r\nBlock found. Broadcasting.");
                                        //    _ = Broadcast("7", JsonConvert.SerializeObject(Globals.LastBlock), "");
                                        //    //_ = P2PValidatorClient.BroadcastBlock(Globals.LastBlock);
                                        //    blockFound = true;
                                        //    break;
                                        //}

                                        //This is done if non-caster wins, and block IS NULL. We must request from other casters as at least 2/3 should have it.
                                        try
                                        {
                                            using (var client = Globals.HttpClientFactory.CreateClient())
                                            {
                                                foreach (var casters in Globals.BlockCasters)
                                                {
                                                    ConsoleWriterService.OutputVal($"Requesting block from Caster: {casters.ValidatorAddress}");
                                                    var uri = $"http://{casters.PeerIP.Replace("::ffff:", "")}:{Globals.ValPort}/valapi/validator/getblock/{finalizedWinner.BlockHeight}";
                                                    var response = await client.GetAsync(uri).WaitAsync(new TimeSpan(0, 0, 0, 0, BLOCK_REQUEST_WINDOW));
                                                    if (response != null)
                                                    {
                                                        if (response.IsSuccessStatusCode)
                                                        {
                                                            var responseBody = await response.Content.ReadAsStringAsync();
                                                            if (responseBody != null)
                                                            {
                                                                if (responseBody == "0")
                                                                {
                                                                    ConsoleWriterService.OutputVal($"Response was 0 (zero)");
                                                                    failedToReachConsensus = true;
                                                                    await Task.Delay(75);
                                                                    continue;
                                                                }

                                                                ConsoleWriterService.OutputVal($"Response had non-zero data");
                                                                block = JsonConvert.DeserializeObject<Block>(responseBody);
                                                                if (block != null)
                                                                {

                                                                    if(block.Validator != terminalWinner)
                                                                    {
                                                                        failedToReachConsensus = true;
                                                                        await Task.Delay(75);
                                                                        continue;
                                                                    }

                                                                    failedToReachConsensus = false;

                                                                    Globals.CasterApprovedBlockHashDict[block.Height] = block.Hash;

                                                                    ConsoleWriterService.OutputVal($"Block deserialized. Height: {block.Height}");
                                                                    var IP = finalizedWinner.IPAddress;
                                                                    var nextHeight = Globals.LastBlock.Height + 1;
                                                                    var currentHeight = block.Height;

                                                                    if (!BlockDownloadService.BlockDict.ContainsKey(currentHeight))
                                                                    {
                                                                        ConsoleWriterService.OutputVal($"Inside block service A");
                                                                        BlockDownloadService.BlockDict[currentHeight] = (block, IP);
                                                                        if (nextHeight == currentHeight)
                                                                            await BlockValidatorService.ValidateBlocks();
                                                                        if (nextHeight < currentHeight)
                                                                            await BlockDownloadService.GetAllBlocks();
                                                                    }

                                                                    if (currentHeight < nextHeight)
                                                                    {
                                                                        blockFound = true;
                                                                        //_ = AddConsensusHeaderQueue(consensusHeader);
                                                                        if (Globals.LastBlock.Height < block.Height)
                                                                            await BlockValidatorService.ValidateBlocks();

                                                                        if (nextHeight == currentHeight)
                                                                        {
                                                                            ConsoleWriterService.OutputVal($"Inside block service B");
                                                                            _ = Broadcast("7", JsonConvert.SerializeObject(block), "");
                                                                            //_ = P2PValidatorClient.BroadcastBlock(block);
                                                                        }

                                                                        if (nextHeight < currentHeight)
                                                                            await BlockDownloadService.GetAllBlocks();

                                                                        break;
                                                                    }
                                                                    else
                                                                    {
                                                                        ConsoleWriterService.OutputVal($"Inside block service C");
                                                                        if (!BlockDownloadService.BlockDict.ContainsKey(currentHeight))
                                                                        {
                                                                            BlockDownloadService.BlockDict[currentHeight] = (block, IP);
                                                                            if (nextHeight == currentHeight)
                                                                                await BlockValidatorService.ValidateBlocks();
                                                                            if (nextHeight < currentHeight)
                                                                                await BlockDownloadService.GetAllBlocks();
                                                                        }

                                                                        if (currentHeight < nextHeight)
                                                                        {
                                                                            ConsoleWriterService.OutputVal($"Inside block service D");
                                                                            blockFound = true;
                                                                            //_ = AddConsensusHeaderQueue(consensusHeader);
                                                                            if (Globals.LastBlock.Height < block.Height)
                                                                                await BlockValidatorService.ValidateBlocks();

                                                                            if (nextHeight == currentHeight)
                                                                            {
                                                                                ConsoleWriterService.OutputVal($"Inside block service E");
                                                                                _ = Broadcast("7", JsonConvert.SerializeObject(block), "");
                                                                                //_ = P2PValidatorClient.BroadcastBlock(block);
                                                                            }

                                                                            if (nextHeight < currentHeight)
                                                                                await BlockDownloadService.GetAllBlocks();

                                                                            break;
                                                                        }
                                                                    }
                                                                }
                                                            }
                                                        }
                                                    }

                                                    await Task.Delay(75);
                                                }
                                            }
                                        }
                                        catch (Exception ex) { }

                                        await Task.Delay(200);
                                    }

                                    if (!blockFound && failedToReachConsensus)
                                    {
                                        Globals.FailedProducerDict.TryGetValue(finalizedWinner.Address, out var failRec);
                                        if (failRec.Item1 != 0)
                                        {
                                            var currentTime = TimeUtil.GetTime(0, 0, -1);
                                            failRec.Item2 += 1;
                                            Globals.FailedProducerDict[finalizedWinner.Address] = failRec;
                                            if (failRec.Item2 >= 3)
                                            {
                                                if (currentTime > failRec.Item1)
                                                {
                                                    var exist = Globals.FailedProducers.Where(x => x == finalizedWinner.Address).FirstOrDefault();
                                                    if (exist == null)
                                                    {
                                                        if (finalizedWinner.Address != "xMpa8DxDLdC9SQPcAFBc2vqwyPsoFtrWyC" &&
                                                                finalizedWinner.Address != "xBRzJUZiXjE3hkrpzGYMSpYCHU1yPpu8cj" &&
                                                                finalizedWinner.Address != "xBRNST9oL8oW6JctcyumcafsnWCVXbzZnr")
                                                            Globals.FailedProducers.Add(finalizedWinner.Address);
                                                    }
                                                }
                                            }

                                            //Reset timer
                                            if (failRec.Item2 < 3)
                                            {
                                                if (failRec.Item1 < currentTime)
                                                {
                                                    failRec.Item1 = TimeUtil.GetTime();
                                                    failRec.Item2 = 1;
                                                    Globals.FailedProducerDict[finalizedWinner.Address] = failRec;
                                                }
                                            }
                                        }
                                        else
                                        {
                                            Globals.FailedProducerDict.TryAdd(finalizedWinner.Address, (TimeUtil.GetTime(), 1));
                                        }
                                        ConsoleWriterService.OutputVal($"\r\nValidator failed to produce block: {finalizedWinner.Address}");
                                        if (finalizedWinner.Address != "xMpa8DxDLdC9SQPcAFBc2vqwyPsoFtrWyC" &&
                                            finalizedWinner.Address != "xBRzJUZiXjE3hkrpzGYMSpYCHU1yPpu8cj" && 
                                            finalizedWinner.Address != "xBRNST9oL8oW6JctcyumcafsnWCVXbzZnr")
                                            ProofUtility.AddFailedProducer(finalizedWinner.Address);

                                        //have to add immediately if this happens.
                                        if (failedToReachConsensus)
                                        {
                                            if (finalizedWinner.Address != "xMpa8DxDLdC9SQPcAFBc2vqwyPsoFtrWyC" &&
                                            finalizedWinner.Address != "xBRzJUZiXjE3hkrpzGYMSpYCHU1yPpu8cj" &&
                                            finalizedWinner.Address != "xBRNST9oL8oW6JctcyumcafsnWCVXbzZnr")
                                                Globals.FailedProducers.Add(finalizedWinner.Address);
                                            ConsoleWriterService.OutputVal($"\r\nAddress: {finalizedWinner.Address} added to failed producers. (Globals.FailedProducers)");
                                        }

                                    }
                                    else
                                    {
                                        if (!failedToReachConsensus)
                                        {
                                            var winnerFound = Globals.ProducerDict.TryGetValue(finalizedWinner.Address, out var winningCount);
                                            if (winnerFound)
                                            {
                                                Globals.ProducerDict[finalizedWinner.Address] = winningCount + 1;
                                            }
                                            else
                                            {
                                                Globals.ProducerDict.TryAdd(finalizedWinner.Address, 1);
                                            }
                                        }
                                        else
                                        {
                                            Globals.FailedProducerDict.TryGetValue(finalizedWinner.Address, out var failRec);
                                            if (failRec.Item1 != 0)
                                            {
                                                var currentTime = TimeUtil.GetTime(0, 0, -1);
                                                failRec.Item2 += 1;
                                                Globals.FailedProducerDict[finalizedWinner.Address] = failRec;
                                                if (failRec.Item2 >= 30)
                                                {
                                                    if (currentTime > failRec.Item1)
                                                    {
                                                        var exist = Globals.FailedProducers.Where(x => x == finalizedWinner.Address).FirstOrDefault();
                                                        if (exist == null)
                                                        {
                                                            if (finalizedWinner.Address != "xMpa8DxDLdC9SQPcAFBc2vqwyPsoFtrWyC" &&
                                                                finalizedWinner.Address != "xBRzJUZiXjE3hkrpzGYMSpYCHU1yPpu8cj" &&
                                                                finalizedWinner.Address != "xBRNST9oL8oW6JctcyumcafsnWCVXbzZnr")
                                                                    Globals.FailedProducers.Add(finalizedWinner.Address);
                                                        }
                                                            
                                                    }
                                                }

                                                //Reset timer
                                                if (failRec.Item2 < 30)
                                                {
                                                    if (failRec.Item1 < currentTime)
                                                    {
                                                        failRec.Item1 = TimeUtil.GetTime();
                                                        failRec.Item2 = 1;
                                                        Globals.FailedProducerDict[finalizedWinner.Address] = failRec;
                                                    }
                                                }
                                            }
                                            else
                                            {
                                                Globals.FailedProducerDict.TryAdd(finalizedWinner.Address, (TimeUtil.GetTime(), 1));
                                            }
                                            ConsoleWriterService.OutputVal($"\r\n2-Validator failed to produce block: {finalizedWinner.Address}");
                                            if (finalizedWinner.Address != "xMpa8DxDLdC9SQPcAFBc2vqwyPsoFtrWyC" &&
                                            finalizedWinner.Address != "xBRzJUZiXjE3hkrpzGYMSpYCHU1yPpu8cj" &&
                                            finalizedWinner.Address != "xBRNST9oL8oW6JctcyumcafsnWCVXbzZnr")
                                                ProofUtility.AddFailedProducer(finalizedWinner.Address);
                                        }
                                    }
                                }
                            }
                        }
                    }
                    
                    ConsoleWriterService.OutputVal($"\r\nStarting over.");
                    Globals.Proofs.Clear();
                    Globals.Proofs = new ConcurrentBag<Proof>();
                    await Task.Delay(50);
                }
                catch (Exception ex)
                {
                }
            }
        }

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

        #region Broadcast
        public static async Task Broadcast(string messageType, string data, string method = "")
        {
            await HubContext.Clients.All.SendAsync("GetCasterMessage", messageType, data);

            if (method == "") return;

            if (!Globals.BlockCasterNodes.Any()) return;

            var valNodeList = Globals.BlockCasterNodes.Values.Where(x => x.IsConnected).ToList();

            if (valNodeList == null || valNodeList.Count() == 0) return;

            foreach (var val in valNodeList)
            {
                var source = new CancellationTokenSource(2000);
                await val.Connection.InvokeCoreAsync(method, args: new object?[] { data }, source.Token);
            }
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

        #region Send Winning Proof Backup Method
        public static async Task SendWinningProof(Proof proof)
        {
            // Create a CancellationTokenSource with a timeout of 5 seconds
            var validators = Globals.BlockCasterNodes.Values.ToList();

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
                            var uri = $"http://{validator.NodeIP.Replace("::ffff:", "")}:{Globals.ValPort}/valapi/validator/ReceiveWinningProof";
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


        #region Get Approval

        public static async Task GetApproval(string? ip, long blockHeight, string validatorAddress)
        {
            if (ip == null)
                return;

            ip = ip.Replace("::ffff:", "");

            if (!Globals.BlockCasters.Any(x => x.PeerIP == ip))
                return;

            var alreadyApproved = ValidatorApprovalBag.Where(x => x.Item1 == ip).ToList();
            if (alreadyApproved.Any())
                return;

            ValidatorApprovalBag.Add((ip.Replace("::ffff:", ""), blockHeight, validatorAddress));
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
