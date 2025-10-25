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
        #region Variables and Instance Class
        public static IHubContext<P2PBlockcasterServer> HubContext;
        private readonly IHubContext<P2PBlockcasterServer> _hubContext;
        private readonly IHostApplicationLifetime _appLifetime;
        private static ConcurrentBag<(string, long, string)> ValidatorApprovalBag = new ConcurrentBag<(string, long, string)>();
        const int PROOF_COLLECTION_TIME = 6000; // 6 seconds
        const int APPROVAL_WINDOW = 12000;      // 12 seconds
        const int CASTER_VOTE_WINDOW = 6000;    // 6 seconds
        const int BLOCK_REQUEST_WINDOW = 12000;  // 12 seconds
        public static ReplacementRound _currentRound;
        public static List<string> _allCasterAddresses;
        public static CasterRoundAudit? CasterRoundAudit = null;


        public BlockcasterNode(IHubContext<P2PBlockcasterServer> hubContext, IHostApplicationLifetime appLifetime)
        {
            _hubContext = hubContext;
            HubContext = hubContext;
            _appLifetime = appLifetime;
        }

        #endregion

        #region Start Async Methods

        public async Task StartAsync(CancellationToken stoppingToken)
        {
            _ = StartCastingRounds();

            //_ = MonitorCasters();
        }

        #endregion

        #region Monitor Casters
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

                //Add back once network is stable after mainnet release.
                //Opens casters up to general public. For now casters will always be online.
                //await PingCasters();

                var casterList = Globals.BlockCasters.ToList();

                if (casterList.Count() < Globals.MaxBlockCasters)
                {
                    //Add back once network is stable after mainnet release.
                    //Opens casters up to general public.
                    //await InitiateReplacement(Globals.LastBlock.Height);
                }

                await Task.Delay(10000);

            }
        }

        #endregion

        #region Ping Casters
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
                            
                            var uri = $"http://{caster.PeerIP.Replace("::ffff:", "")}:{Globals.ValAPIPort}/valapi/validator/heartbeat";

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

        #endregion

        #region Initiate Replacement Casters
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
                                    var uri = $"http://{caster.PeerIP.Replace("::ffff:", "")}:{Globals.ValAPIPort}/valapi/validator/SendSeedPart";
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
                                        var uri = $"http://{caster.PeerIP.Replace("::ffff:", "")}:{Globals.ValAPIPort}/valapi/validator/GetCasterVote";
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

        #endregion

        #region Add Casters
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

                    //Adding delay to prevent infinite loop and cpu throttling
                    await Task.Delay(10);
                }
            }
        }

        #endregion

        #region Generated Deterministic Seed
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

        #endregion

        private static async Task StartCastingRounds()
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

                    if (CasterRoundAudit == null)
                    {
                        CasterRoundAudit = new CasterRoundAudit(Height);
                        Console.Clear();
                    }
                    else
                    {
                        Console.Clear();
                        if (CasterRoundAudit.BlockHeight < Height)
                        {
                            CasterRoundAudit = new CasterRoundAudit(Height);
                        }
                        else
                        {
                            CasterRoundAudit.AddCycle();//bad we don't want this.
                        }
                    }

                    if (PreviousHeight != Height)
                    {
                        PreviousHeight = Height;
                        await Task.WhenAll(BlockDelay, Task.Delay(1500));
                        var CurrentTime = TimeUtil.GetMillisecondTime();
                        var DelayTimeCorrection = Globals.BlockTime * (Height - BeginBlock) - (CurrentTime - EpochTime);
                        var DelayTime = Math.Min(Math.Max(Globals.BlockTime + DelayTimeCorrection, Globals.BlockTimeMin), Globals.BlockTimeMax);
                        BlockDelay = Task.Delay((int)DelayTime);

                        CasterRoundAudit.AddStep("Next Consensus Delay: " + DelayTime + " (" + DelayTimeCorrection + ")", true);
                        //ConsoleWriterService.OutputVal("\r\nNext Consensus Delay: " + DelayTime + " (" + DelayTimeCorrection + ")");
                    }

                    _ = CleanupApprovedCasterBlocks();

                    ValidatorApprovalBag.Clear();
                    ValidatorApprovalBag = new ConcurrentBag<(string, long, string)>();
                    //Generate Proofs for ALL vals
                    CasterRoundAudit.AddStep($"Generating Proofs for height: {Height}.", true);
                    //ConsoleWriterService.OutputVal($"\r\nGenerating Proofs for height: {Height}.");
                    var casterProofs = await ProofUtility.GenerateCasterProofs();
                    var proofs = await ProofUtility.GenerateProofs();
                    CasterRoundAudit.AddStep($"{proofs.Count()} Proofs Generated", true);
                    //ConsoleWriterService.OutputVal($"\r\n{proofs.Count()} Proofs Generated");
                    var winningCasterProof = await ProofUtility.SortProofs(casterProofs);
                    var winningProof = await ProofUtility.SortProofs(proofs);
                    CasterRoundAudit.AddStep($"Sorting Proofs", true);
                    //ConsoleWriterService.OutputVal($"\r\nSorting Proofs");

                    if (!Globals.CasterRoundDict.ContainsKey(Height))
                    {
                        var casterRound = new CasterRound
                        {
                            BlockHeight = Height,
                        };

                        Globals.CasterRoundDict[Height] = casterRound;
                    }

                    if (winningCasterProof != null && casterProofs.Count() > 2)
                    {
                        CasterRoundAudit.AddStep($"Attempting Proof on Address: {winningCasterProof.Address}", true);
                        //ConsoleWriterService.OutputVal($"\r\nAttempting Proof on Address: {winningProof.Address}");
                        var verificationResult = false;
                        List<string> ExcludeValList = new List<string>();
                        while (!verificationResult)
                        {
                            if (winningCasterProof != null)
                            {
                                var nextblock = Globals.LastBlock.Height + 1;
                                Stopwatch sw = Stopwatch.StartNew();
                                var verificationResultTuple = await ProofUtility.VerifyWinnerAvailability(winningCasterProof, nextblock);
                                sw.Stop();
                                await Task.Delay(100);
                                verificationResult = verificationResultTuple.Item1;

                                Globals.NetworkValidators.TryGetValue(winningCasterProof.Address, out var validator);

                                if (!verificationResult)
                                {
                                    if (validator != null)
                                    {
                                        validator.CheckFailCount++;
                                        validator.Latency = sw.ElapsedMilliseconds;
                                        if (validator.CheckFailCount <= 3)
                                        {
                                            NetworkValidator.UpdateLastSeen(winningCasterProof.Address); // HAL-26 Fix: Track validator activity
                                            Globals.NetworkValidators[winningCasterProof.Address] = validator;
                                        }
                                        else
                                        {
                                            //Globals.NetworkValidators.TryRemove(winningProof.Address, out _);
                                        }
                                    }
                                    ExcludeValList.Add(winningCasterProof.Address);
                                    winningCasterProof = await ProofUtility.SortProofs(casterProofs
                                        .Where(x => !ExcludeValList.Contains(x.Address)).ToList()
                                    //winningProof = await ProofUtility.SortProofs(
                                    //    proofs.Where(x => !ExcludeValList.Contains(x.Address)).ToList()
                                    );

                                    if (winningCasterProof == null)
                                    {
                                        ExcludeValList.Clear();
                                        ExcludeValList = new List<string>();
                                        break;
                                    }
                                }
                                else
                                {
                                    if (validator != null)
                                    {
                                        validator.CheckFailCount = 0;
                                        validator.Latency = sw.ElapsedMilliseconds;
                                        NetworkValidator.UpdateLastSeen(winningCasterProof.Address); // HAL-26 Fix: Track validator activity
                                        Globals.NetworkValidators[winningCasterProof.Address] = validator;
                                    }

                                    ExcludeValList.Clear();
                                    ExcludeValList = new List<string>();
                                }
                                await Task.Delay(100);
                            }
                            else
                            {
                                ExcludeValList.Clear();
                                ExcludeValList = new List<string>();
                                await Task.Delay(100);
                                break;
                            }
                        }

                        if (winningCasterProof == null)
                        {
                            ConsoleWriterService.OutputVal($"\r\nCould not connect to any nodes for winning proof. Starting over.");
                            continue;
                        }

                        CasterRoundAudit.AddStep($"Potential Winner Found! Address: {winningCasterProof.Address}", true);
                        //ConsoleWriterService.OutputVal($"\r\nPotential Winner Found! Address: {winningProof.Address}");
                        //Globals.Proofs.Add(winningProof);

                        var round = Globals.CasterRoundDict[Height];
                        if (round != null)
                        {
                            var compareRound = round;
                            round.Proof = winningCasterProof;
                            while (!Globals.CasterRoundDict.TryUpdate(Height, round, compareRound)) ;
                        }

                        //_ = GetWinningProof(winningProof);
                        Globals.CasterProofDict.Clear();
                        Globals.Proofs.Clear();
                        Globals.CasterProofDict = new ConcurrentDictionary<string, Proof>();
                        Globals.Proofs = new ConcurrentBag<Proof>();

                        var swProofCollectionTime = Stopwatch.StartNew();
                        while (swProofCollectionTime.ElapsedMilliseconds <= PROOF_COLLECTION_TIME)
                        {
                            _ = GetWinningProof(winningCasterProof);
                            await Task.Delay(1000);
                            _ = SendWinningProof(winningCasterProof);
                            await Task.Delay(500);


                        }

                        swProofCollectionTime.Stop();
                        //await Task.Delay(PROOF_COLLECTION_TIME);

                        if (Globals.CasterProofDict.Count() < 3)
                        {
                            continue;
                        }

                        foreach (var proofItem in Globals.CasterProofDict)
                        {
                            Globals.Proofs.Add(proofItem.Value);
                        }

                        var proofSnapshot = Globals.Proofs.Where(x => x.BlockHeight == Height).ToList();

                        CasterRoundAudit.AddStep($"Total Proofs Collection: {proofSnapshot.Count()}", true);

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
                                CasterRoundAudit.AddStep($"Finalized winner : {finalizedWinner.Address}", true);
                                //ConsoleWriterService.OutputVal($"\r\nFinalized winner : {finalizedWinner.Address}");

                                var approved = false;

                                var sw = Stopwatch.StartNew();
                                List<string> CasterApprovalList = new List<string>();
                                Dictionary<string, string> CasterVoteList = new Dictionary<string, string>();

                                string? terminalWinner = null;

                                while (!approved && sw.ElapsedMilliseconds < APPROVAL_WINDOW)
                                {

                                    if (Globals.LastBlock.Height >= finalizedWinner.BlockHeight)
                                        break;

                                    if (!Globals.CasterRoundDict.ContainsKey(finalizedWinner.BlockHeight))
                                    {
                                        var casterRound = new CasterRound
                                        {
                                            BlockHeight = finalizedWinner.BlockHeight,
                                            Validator = finalizedWinner.Address
                                        };

                                        casterRound.ProgressRound();

                                        Globals.CasterRoundDict[finalizedWinner.BlockHeight] = casterRound;
                                    }
                                    else
                                    {
                                        round = Globals.CasterRoundDict[finalizedWinner.BlockHeight];
                                        round.ProgressRound();
                                        if (round.RoundStale())
                                        {
                                            var casterRound = new CasterRound
                                            {
                                                BlockHeight = finalizedWinner.BlockHeight,
                                                Validator = finalizedWinner.Address
                                            };
                                            Globals.CasterRoundDict[finalizedWinner.BlockHeight] = casterRound;
                                        }
                                        else
                                        {
                                            var compareRound = round;
                                            round.Validator = finalizedWinner.Address;
                                            round.BlockHeight = finalizedWinner.BlockHeight;
                                            while (!Globals.CasterRoundDict.TryUpdate(round.BlockHeight, round, compareRound))
                                            {
                                                await Task.Delay(75);
                                            }
                                        }
                                    }
                                    //Caster dict null value here potentially due to it being to fast.
                                    foreach (var caster in Globals.BlockCasters)
                                    {
                                        using (var client = Globals.HttpClientFactory.CreateClient())
                                        {
                                            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(CASTER_VOTE_WINDOW));
                                            try
                                            {
                                                var valAddr = finalizedWinner.Address;
                                                var uri = $"http://{caster.PeerIP.Replace("::ffff:", "")}:{Globals.ValAPIPort}/valapi/validator/getapproval/{finalizedWinner.BlockHeight}";
                                                var response = await client.GetAsync(uri, cts.Token);
                                                if (response.IsSuccessStatusCode)
                                                {
                                                    var responseJson = await response.Content.ReadAsStringAsync();
                                                    if (responseJson != null)
                                                    {
                                                        if (responseJson == "1")
                                                        {
                                                            await Task.Delay(500);
                                                            int count = 0;
                                                            while (count < 3)
                                                            {
                                                                response = await client.GetAsync(uri, cts.Token);
                                                                responseJson = await response.Content.ReadAsStringAsync();
                                                                if (responseJson == "0" || responseJson != "1")
                                                                {
                                                                    await Task.Delay(500);
                                                                    count++;
                                                                    continue;
                                                                }
                                                                else
                                                                {
                                                                    break;
                                                                }
                                                            }
                                                        }
                                                        if (responseJson != "0" && responseJson != "1")
                                                        {
                                                            var remoteCasterRound = JsonConvert.DeserializeObject<CasterRound>(responseJson);
                                                            if (remoteCasterRound != null)
                                                            {
                                                                await GetApproval(caster.PeerIP, finalizedWinner.BlockHeight, remoteCasterRound.Validator);
                                                                CasterApprovalList.Add(caster.PeerIP);
                                                                //ConsoleWriterService.OutputVal($"\r\nApproval sent to address: {caster.ValidatorAddress}.");
                                                                //ConsoleWriterService.OutputVal($"IP Address: {caster.PeerIP}.");
                                                                await Task.Delay(200);
                                                                continue;
                                                            }
                                                        }
                                                    }
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

                                    CasterRoundAudit.AddStep($"Validator Bag Count: {vBag.Count()}.", true);
                                    //ConsoleWriterService.OutputVal($"\r\nValidator Bag Count: {vBag.Count()}.");

                                    var approvalCount = casterList.Count() <= 5 ? 3 : 4;

                                    if (vBag.Any() && !approved)
                                    {
                                        foreach (var vote in vBag)
                                        {
                                            if (!CasterVoteList.ContainsKey(vote.Item1))
                                                CasterVoteList[vote.Item1] = vote.Item3;
                                        }

                                        //Issue is here one of them is still null... Figure out WHY!
                                        //Look above. Could be its asking the first one and it hasn't populated its dictionary yet. 
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

                                                CasterRoundAudit.AddStep($"Bag was approved. Moving to next block.", true);
                                                //ConsoleWriterService.OutputVal($"\r\nBag was approved. Moving to next block.");

                                                approved = true;
                                                break;
                                            }
                                        }
                                        else
                                        {
                                            terminalWinner = "RH9XAP3omXvk7P6Xe9fQ1C6nZQ1adJw2ZG"; //Mainnet
                                            if(Globals.IsTestNet)
                                                terminalWinner = "xBRzJUZiXjE3hkrpzGYMSpYCHU1yPpu8cj";
                                            ConsoleWriterService.OutputVal($"\r\n Bag failed. No Result was found.");
                                            CasterRoundAudit.AddStep($"Bag was approved. Moving to next block.", true);
                                            //ConsoleWriterService.OutputVal($"\r\nBag was approved. Moving to next block.");

                                            approved = true;
                                            break;
                                        }
                                    }
                                }

                                terminalWinner = Height % 2 == 0 ? "RFoKrASMr19mg8S71Lf1F2suzxahG5Yj4N" : "RH9XAP3omXvk7P6Xe9fQ1C6nZQ1adJw2ZG";

                                if (Globals.IsTestNet)
                                    terminalWinner = Height % 2 == 0 ? "xBRzJUZiXjE3hkrpzGYMSpYCHU1yPpu8cj" : "xMpa8DxDLdC9SQPcAFBc2vqwyPsoFtrWyC";

                                CasterRoundAudit.AddStep($"Stabilization Code. Moving on.", true);
                                approved = true;
                                
                                //ConsoleWriterService.OutputVal($"\r\nYou did not win. Looking for block.");
                                if (Globals.LastBlock.Height < finalizedWinner.BlockHeight && approved)
                                {
                                    bool blockFound = false;
                                    bool failedToReachConsensus = false;
                                    var swb = Stopwatch.StartNew();
                                    while (!blockFound && swb.ElapsedMilliseconds < BLOCK_REQUEST_WINDOW)
                                    {
                                        if (terminalWinner == Globals.ValidatorAddress)
                                        {
                                            //request block from random network val.
                                            var validators = Globals.NetworkValidators.Values.ToList();
                                            var excludeVals = new List<string>();

                                            while(!blockFound)
                                            {
                                                var rnd = new Random();
                                                var randomizedValidator = validators
                                                    .Where(x => !excludeVals.Contains(x.IPAddress))
                                                    .OrderBy(x => rnd.Next())
                                                    .ToList()
                                                    .FirstOrDefault();

                                                if(randomizedValidator == null)
                                                {
                                                    block = Globals.NextValidatorBlock;
                                                    blockFound = true;
                                                    round = Globals.CasterRoundDict[block.Height];
                                                    if (round != null)
                                                    {
                                                        var compareRound = round;
                                                        round.Block = block;
                                                        round.Validator = Globals.ValidatorAddress;
                                                        while (!Globals.CasterRoundDict.TryUpdate(finalizedWinner.BlockHeight, round, compareRound))
                                                        {
                                                            await Task.Delay(75);
                                                        }
                                                    }
                                                    break;
                                                }
                                                else
                                                {
                                                    var verificationResultTuple = await ProofUtility.VerifyValAvailability(randomizedValidator.IPAddress, randomizedValidator.Address, Height);
                                                    verificationResult = verificationResultTuple.Item1;
                                                    if(!verificationResult)
                                                    {
                                                        excludeVals.Add(randomizedValidator.IPAddress);
                                                        continue;
                                                    }

                                                    if(verificationResultTuple.Item2 == null)
                                                    {
                                                        excludeVals.Add(randomizedValidator.IPAddress);
                                                        continue;
                                                    }

                                                    block = verificationResultTuple.Item2;
                                                    blockFound = true;
                                                    round = Globals.CasterRoundDict[block.Height];
                                                    if (round != null)
                                                    {
                                                        var compareRound = round;
                                                        round.Block = block;
                                                        round.Validator = randomizedValidator.Address;
                                                        while (!Globals.CasterRoundDict.TryUpdate(finalizedWinner.BlockHeight, round, compareRound))
                                                        {
                                                            await Task.Delay(75);
                                                        }
                                                    }
                                                    break;
                                                }
                                            }

                                            if(blockFound)
                                            {
                                                CasterRoundAudit.AddStep($"Already have block. Height: {block.Height}", true);
                                                //ConsoleWriterService.OutputVal($"Already have block. Height: {block.Height}");
                                                var IP = finalizedWinner.IPAddress;
                                                var nextHeight = Globals.LastBlock.Height + 1;
                                                var currentHeight = block.Height;

                                                if (!BlockDownloadService.BlockDict.ContainsKey(currentHeight))
                                                {
                                                    CasterRoundAudit.AddStep($"Processing Block.", true);
                                                    //ConsoleWriterService.OutputVal($"Processing Block");
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
                                                        CasterRoundAudit.AddStep($"Block found. Broadcasting.", true);
                                                        //ConsoleWriterService.OutputVal($"Inside block service B");
                                                        //ConsoleWriterService.OutputVal($"\r\nBlock found. Broadcasting.");
                                                        _ = Broadcast("7", JsonConvert.SerializeObject(block), "");
                                                        //_ = P2PValidatorClient.BroadcastBlock(block);
                                                    }

                                                    if (nextHeight < currentHeight)
                                                        await BlockDownloadService.GetAllBlocks();

                                                    break;
                                                }
                                            }

                                            
                                        }
                                        else
                                        {
                                            //wait and request block or wait for it to show up. 
                                            int attempts = 0;
                                            while (attempts < 3)
                                            {
                                                try
                                                {
                                                    attempts++;
                                                    CasterRoundAudit.AddStep($"Requesting block from Casters.", true);
                                                    using (var client = Globals.HttpClientFactory.CreateClient())
                                                    {
                                                        var casters = Globals.BlockCasters.Where(x => x.ValidatorAddress == terminalWinner).FirstOrDefault();
                                                        if (casters == null)
                                                            break;
                                                        //ConsoleWriterService.OutputVal($"Requesting block from Caster: {casters.ValidatorAddress}");
                                                        var uri = $"http://{casters.PeerIP.Replace("::ffff:", "")}:{Globals.ValAPIPort}/valapi/validator/getblock/{finalizedWinner.BlockHeight}";
                                                        var response = await client.GetAsync(uri).WaitAsync(new TimeSpan(0, 0, 5));
                                                        if (response != null)
                                                        {
                                                            if (response.IsSuccessStatusCode)
                                                            {
                                                                var responseBody = await response.Content.ReadAsStringAsync();
                                                                if (responseBody != null)
                                                                {
                                                                    if (responseBody == "0")
                                                                    {
                                                                        //ConsoleWriterService.OutputVal($"Response was 0 (zero)");
                                                                        failedToReachConsensus = true;
                                                                        await Task.Delay(75);
                                                                        continue;
                                                                    }

                                                                    //ConsoleWriterService.OutputVal($"Response had non-zero data");
                                                                    block = JsonConvert.DeserializeObject<Block>(responseBody);
                                                                    if (block != null)
                                                                    {
                                                                        failedToReachConsensus = false;
                                                                        blockFound = true;

                                                                        round = Globals.CasterRoundDict[block.Height];
                                                                        if (round != null)
                                                                        {
                                                                            var compareRound = round;
                                                                            round.Block = block;
                                                                            round.Validator = block.Validator;
                                                                            while (!Globals.CasterRoundDict.TryUpdate(finalizedWinner.BlockHeight, round, compareRound)) ;
                                                                        }

                                                                        CasterRoundAudit.AddStep($"Block deserialized. Height: {block.Height}", true);
                                                                        //ConsoleWriterService.OutputVal($"Block deserialized. Height: {block.Height}");
                                                                        var IP = finalizedWinner.IPAddress;
                                                                        var nextHeight = Globals.LastBlock.Height + 1;
                                                                        var currentHeight = block.Height;

                                                                        if (!BlockDownloadService.BlockDict.ContainsKey(currentHeight))
                                                                        {
                                                                            //ConsoleWriterService.OutputVal($"Inside block service A");
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
                                                                                //ConsoleWriterService.OutputVal($"Inside block service B");
                                                                                _ = Broadcast("7", JsonConvert.SerializeObject(block), "");
                                                                                //_ = P2PValidatorClient.BroadcastBlock(block);
                                                                            }

                                                                            if (nextHeight < currentHeight)
                                                                                await BlockDownloadService.GetAllBlocks();

                                                                            break;
                                                                        }
                                                                        else
                                                                        {
                                                                            //ConsoleWriterService.OutputVal($"Inside block service C");
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
                                                                                //ConsoleWriterService.OutputVal($"Inside block service D");
                                                                                blockFound = true;
                                                                                //_ = AddConsensusHeaderQueue(consensusHeader);
                                                                                if (Globals.LastBlock.Height < block.Height)
                                                                                    await BlockValidatorService.ValidateBlocks();

                                                                                if (nextHeight == currentHeight)
                                                                                {
                                                                                    //ConsoleWriterService.OutputVal($"Inside block service E");
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
                                                catch { }
                                            }
                                            
                                        }
                                        /////////////////////////////////////
                                        //This is done if non-caster wins the block

                                        await Task.Delay(200);
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        CasterRoundAudit = null;//round was starting. 
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

        #region Start Consensus
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

                    if(CasterRoundAudit == null)
                    {
                        CasterRoundAudit = new CasterRoundAudit(Height);
                        Console.Clear();
                    }
                    else
                    {
                        Console.Clear();
                        if (CasterRoundAudit.BlockHeight < Height)
                        {
                            CasterRoundAudit = new CasterRoundAudit(Height);
                        }
                        else
                        {
                            CasterRoundAudit.AddCycle();//bad we don't want this.
                        }
                    }

                    if (PreviousHeight != Height)
                    {
                        PreviousHeight = Height;
                        await Task.WhenAll(BlockDelay, Task.Delay(1500));
                        var CurrentTime = TimeUtil.GetMillisecondTime();
                        var DelayTimeCorrection = Globals.BlockTime * (Height - BeginBlock) - (CurrentTime - EpochTime);
                        var DelayTime = Math.Min(Math.Max(Globals.BlockTime + DelayTimeCorrection, Globals.BlockTimeMin), Globals.BlockTimeMax);
                        BlockDelay = Task.Delay((int)DelayTime);

                        CasterRoundAudit.AddStep("Next Consensus Delay: " + DelayTime + " (" + DelayTimeCorrection + ")", true);
                        //ConsoleWriterService.OutputVal("\r\nNext Consensus Delay: " + DelayTime + " (" + DelayTimeCorrection + ")");
                    }

                    _ = CleanupApprovedCasterBlocks();

                    ValidatorApprovalBag.Clear();
                    ValidatorApprovalBag = new ConcurrentBag<(string, long, string)>();
                    //Generate Proofs for ALL vals
                    CasterRoundAudit.AddStep($"Generating Proofs for height: {Height}.", true);
                    //ConsoleWriterService.OutputVal($"\r\nGenerating Proofs for height: {Height}.");
                    var casterProofs = await ProofUtility.GenerateCasterProofs();
                    var proofs = await ProofUtility.GenerateProofs();
                    CasterRoundAudit.AddStep($"{proofs.Count()} Proofs Generated", true);
                    //ConsoleWriterService.OutputVal($"\r\n{proofs.Count()} Proofs Generated");
                    var winningCasterProof = await ProofUtility.SortProofs(casterProofs);
                    var winningProof = await ProofUtility.SortProofs(proofs);
                    CasterRoundAudit.AddStep($"Sorting Proofs", true);
                    //ConsoleWriterService.OutputVal($"\r\nSorting Proofs");

                    if (!Globals.CasterRoundDict.ContainsKey(Height))
                    {
                        var casterRound = new CasterRound
                        {
                            BlockHeight = Height,
                        };

                        Globals.CasterRoundDict[Height] = casterRound;
                    }

                    if (winningCasterProof != null && proofs.Count() > 1)
                    {
                        CasterRoundAudit.AddStep($"Attempting Proof on Address: {winningCasterProof.Address}", true);
                        //ConsoleWriterService.OutputVal($"\r\nAttempting Proof on Address: {winningProof.Address}");
                        var verificationResult = false;
                        List<string> ExcludeValList = new List<string>();
                        while (!verificationResult)
                        {
                            if (winningCasterProof != null)
                            {
                                var nextblock = Globals.LastBlock.Height + 1;
                                Stopwatch sw = Stopwatch.StartNew();
                                var verificationResultTuple = await ProofUtility.VerifyWinnerAvailability(winningCasterProof, nextblock);
                                sw.Stop();
                                verificationResult = verificationResultTuple.Item1;

                                Globals.NetworkValidators.TryGetValue(winningCasterProof.Address, out var validator);

                                if (!verificationResult)
                                {
                                    if (validator != null)
                                    {
                                        validator.CheckFailCount++;
                                        validator.Latency = sw.ElapsedMilliseconds;
                                        if (validator.CheckFailCount <= 3)
                                        {
                                            Globals.NetworkValidators[winningCasterProof.Address] = validator;
                                        }
                                        else
                                        {
                                            //Globals.NetworkValidators.TryRemove(winningProof.Address, out _);
                                        }
                                    }
                                    ExcludeValList.Add(winningCasterProof.Address);
                                    winningCasterProof = await ProofUtility.SortProofs(casterProofs
                                        .Where(x => !ExcludeValList.Contains(x.Address)).ToList()
                                    //winningProof = await ProofUtility.SortProofs(
                                    //    proofs.Where(x => !ExcludeValList.Contains(x.Address)).ToList()
                                    );

                                    if (winningCasterProof == null)
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
                                        Globals.NetworkValidators[winningCasterProof.Address] = validator;
                                    }

                                    //No longer getting block at this time. Deprecate this block process later!
                                    //block = verificationResultTuple.Item2;

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

                        if (winningCasterProof == null)
                        {
                            ConsoleWriterService.OutputVal($"\r\nCould not connect to any nodes for winning proof. Starting over.");
                            continue;
                        }

                        CasterRoundAudit.AddStep($"Potential Winner Found! Address: {winningCasterProof.Address}", true);
                        //ConsoleWriterService.OutputVal($"\r\nPotential Winner Found! Address: {winningProof.Address}");
                        //Globals.Proofs.Add(winningProof);

                        var round = Globals.CasterRoundDict[Height];
                        if (round != null)
                        {
                            var compareRound = round;
                            round.Proof = winningProof;
                            while (!Globals.CasterRoundDict.TryUpdate(Height, round, compareRound)) ;
                        }

                        //_ = GetWinningProof(winningProof);
                        Globals.CasterProofDict.Clear();
                        Globals.Proofs.Clear();
                        Globals.CasterProofDict = new ConcurrentDictionary<string, Proof>();
                        Globals.Proofs = new ConcurrentBag<Proof>();

                        var swProofCollectionTime = Stopwatch.StartNew();
                        while(swProofCollectionTime.ElapsedMilliseconds <= PROOF_COLLECTION_TIME)
                        {
                            _ = GetWinningProof(winningCasterProof);
                            _ = SendWinningProof(winningCasterProof);

                            await Task.Delay(1000);
                        }

                        swProofCollectionTime.Stop();
                        //await Task.Delay(PROOF_COLLECTION_TIME);

                        if (Globals.CasterProofDict.Count() < casterList.Count())
                        {
                            continue;
                        }

                        foreach (var proofItem in Globals.CasterProofDict)
                        {
                            Globals.Proofs.Add(proofItem.Value);
                        }

                        var proofSnapshot = Globals.Proofs.Where(x => x.BlockHeight == Height).ToList();

                        CasterRoundAudit.AddStep($"Total Proofs Collection: {proofSnapshot.Count()}", true);

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
                                CasterRoundAudit.AddStep($"Finalized winner : {finalizedWinner.Address}", true);
                                //ConsoleWriterService.OutputVal($"\r\nFinalized winner : {finalizedWinner.Address}");


                                if (finalizedWinner.Address != winningProof.Address)
                                    block = null;

                                var approved = false;

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

                                    if(!Globals.CasterRoundDict.ContainsKey(finalizedWinner.BlockHeight))
                                    {
                                        var casterRound = new CasterRound
                                        {
                                            BlockHeight = finalizedWinner.BlockHeight,
                                            Validator = finalizedWinner.Address
                                        };

                                        casterRound.ProgressRound();

                                        Globals.CasterRoundDict[finalizedWinner.BlockHeight] = casterRound;
                                    }
                                    else
                                    {
                                        round = Globals.CasterRoundDict[finalizedWinner.BlockHeight];
                                        round.ProgressRound();
                                        if(round.RoundStale())
                                        {
                                            var casterRound = new CasterRound
                                            {
                                                BlockHeight = finalizedWinner.BlockHeight,
                                                Validator = finalizedWinner.Address
                                            };
                                            Globals.CasterRoundDict[finalizedWinner.BlockHeight] = casterRound;
                                        }
                                        else
                                        {
                                            var compareRound = round;
                                            round.Validator = finalizedWinner.Address;
                                            round.BlockHeight = finalizedWinner.BlockHeight;
                                            while(!Globals.CasterRoundDict.TryUpdate(round.BlockHeight, round, compareRound))
                                            {
                                                await Task.Delay(75);
                                            }
                                        }
                                    }
                                    //Caster dict null value here potentially due to it being to fast.
                                    foreach (var caster in Globals.BlockCasters)
                                    {
                                        using (var client = Globals.HttpClientFactory.CreateClient())
                                        {
                                            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(CASTER_VOTE_WINDOW));
                                            try
                                            {
                                                var valAddr = finalizedWinner.Address;
                                                var uri = $"http://{caster.PeerIP.Replace("::ffff:", "")}:{Globals.ValAPIPort}/valapi/validator/getapproval/{finalizedWinner.BlockHeight}";
                                                var response = await client.GetAsync(uri, cts.Token);
                                                if (response.IsSuccessStatusCode)
                                                {
                                                    var responseJson = await response.Content.ReadAsStringAsync();
                                                    if (responseJson != null)
                                                    {
                                                        if(responseJson == "1")
                                                        {
                                                            await Task.Delay(500);
                                                            int count = 0;
                                                            while(count < 3)
                                                            {
                                                                response = await client.GetAsync(uri, cts.Token);
                                                                responseJson = await response.Content.ReadAsStringAsync();
                                                                if (responseJson == "0" || responseJson != "1")
                                                                {
                                                                    await Task.Delay(500);
                                                                    count++;
                                                                    continue;
                                                                }
                                                                else
                                                                {
                                                                    break;
                                                                }
                                                            }
                                                        }
                                                        if(responseJson != "0" && responseJson != "1")
                                                        {
                                                            var remoteCasterRound = JsonConvert.DeserializeObject<CasterRound>(responseJson);
                                                            if(remoteCasterRound != null)
                                                            {
                                                                await GetApproval(caster.PeerIP, finalizedWinner.BlockHeight, remoteCasterRound.Validator);
                                                                CasterApprovalList.Add(caster.PeerIP);
                                                                //ConsoleWriterService.OutputVal($"\r\nApproval sent to address: {caster.ValidatorAddress}.");
                                                                //ConsoleWriterService.OutputVal($"IP Address: {caster.PeerIP}.");
                                                                await Task.Delay(200);
                                                                continue;
                                                            }
                                                        }
                                                    }
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

                                    CasterRoundAudit.AddStep($"Validator Bag Count: {vBag.Count()}.", true);
                                    //ConsoleWriterService.OutputVal($"\r\nValidator Bag Count: {vBag.Count()}.");

                                    var approvalCount = casterList.Count() <= 5 ? 3 : 4;

                                    if (vBag.Any() && !approved)
                                    {
                                        foreach(var vote in vBag)
                                        {
                                            if(!CasterVoteList.ContainsKey(vote.Item1))
                                                CasterVoteList[vote.Item1] = vote.Item3;
                                        }
                                        
                                        //Issue is here one of them is still null... Figure out WHY!
                                        //Look above. Could be its asking the first one and it hasn't populated its dictionary yet. 
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
                                                {
                                                    round = Globals.CasterRoundDict[block.Height];
                                                    if (round != null)
                                                    {
                                                        var compareRound = round;
                                                        round.Block = block;
                                                        while(!Globals.CasterRoundDict.TryUpdate(finalizedWinner.BlockHeight, round, compareRound));
                                                    }

                                                    Globals.CasterApprovedBlockHashDict.TryGetValue(block.Height, out var currentVal);
                                                    if (currentVal != null)
                                                        while (!Globals.CasterApprovedBlockHashDict.TryUpdate(block.Height, block.Hash, currentVal));
                                                    else
                                                        while (!Globals.CasterApprovedBlockHashDict.TryAdd(block.Height, block.Hash));
                                                }

                                                CasterRoundAudit.AddStep($"Bag was approved. Moving to next block.", true);
                                                //ConsoleWriterService.OutputVal($"\r\nBag was approved. Moving to next block.");

                                                approved = true;
                                                break;
                                            }
                                        }
                                        else
                                        {
                                            ConsoleWriterService.OutputVal($"\r\n Bag failed. No Result was found.");
                                        }
                                    }
                                }

                                CasterRoundAudit.AddStep($"You did not win. Looking for block.", true);
                                //ConsoleWriterService.OutputVal($"\r\nYou did not win. Looking for block.");
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
                                            CasterRoundAudit.AddStep($"Already have block. Height: {block.Height}", true);
                                            //ConsoleWriterService.OutputVal($"Already have block. Height: {block.Height}");
                                            var IP = finalizedWinner.IPAddress;
                                            var nextHeight = Globals.LastBlock.Height + 1;
                                            var currentHeight = block.Height;

                                            if (!BlockDownloadService.BlockDict.ContainsKey(currentHeight))
                                            {
                                                CasterRoundAudit.AddStep($"Processing Block.", true);
                                                //ConsoleWriterService.OutputVal($"Processing Block");
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
                                                    CasterRoundAudit.AddStep($"Block found. Broadcasting.", true);
                                                    //ConsoleWriterService.OutputVal($"Inside block service B");
                                                    //ConsoleWriterService.OutputVal($"\r\nBlock found. Broadcasting.");
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
                                            CasterRoundAudit.AddStep($"Requesting block from Casters.", true);
                                            using (var client = Globals.HttpClientFactory.CreateClient())
                                            {
                                                foreach (var casters in Globals.BlockCasters)
                                                {
                                                    //ConsoleWriterService.OutputVal($"Requesting block from Caster: {casters.ValidatorAddress}");
                                                    var uri = $"http://{casters.PeerIP.Replace("::ffff:", "")}:{Globals.ValAPIPort}/valapi/validator/getblock/{finalizedWinner.BlockHeight}";
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
                                                                    //ConsoleWriterService.OutputVal($"Response was 0 (zero)");
                                                                    failedToReachConsensus = true;
                                                                    await Task.Delay(75);
                                                                    continue;
                                                                }

                                                                //ConsoleWriterService.OutputVal($"Response had non-zero data");
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

                                                                    Globals.CasterApprovedBlockHashDict.TryGetValue(block.Height, out var currentVal);
                                                                    if(currentVal != null)
                                                                        while(!Globals.CasterApprovedBlockHashDict.TryUpdate(block.Height, block.Hash, currentVal));
                                                                    else
                                                                        while (!Globals.CasterApprovedBlockHashDict.TryAdd(block.Height, block.Hash));

                                                                    round = Globals.CasterRoundDict[block.Height];
                                                                    if (round != null)
                                                                    {
                                                                        var compareRound = round;
                                                                        round.Block = block;
                                                                        while (!Globals.CasterRoundDict.TryUpdate(finalizedWinner.BlockHeight, round, compareRound));
                                                                    }

                                                                    CasterRoundAudit.AddStep($"Block deserialized. Height: {block.Height}", true);
                                                                    //ConsoleWriterService.OutputVal($"Block deserialized. Height: {block.Height}");
                                                                    var IP = finalizedWinner.IPAddress;
                                                                    var nextHeight = Globals.LastBlock.Height + 1;
                                                                    var currentHeight = block.Height;

                                                                    if (!BlockDownloadService.BlockDict.ContainsKey(currentHeight))
                                                                    {
                                                                        //ConsoleWriterService.OutputVal($"Inside block service A");
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
                                                                            //ConsoleWriterService.OutputVal($"Inside block service B");
                                                                            _ = Broadcast("7", JsonConvert.SerializeObject(block), "");
                                                                            //_ = P2PValidatorClient.BroadcastBlock(block);
                                                                        }

                                                                        if (nextHeight < currentHeight)
                                                                            await BlockDownloadService.GetAllBlocks();

                                                                        break;
                                                                    }
                                                                    else
                                                                    {
                                                                        //ConsoleWriterService.OutputVal($"Inside block service C");
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
                                                                            //ConsoleWriterService.OutputVal($"Inside block service D");
                                                                            blockFound = true;
                                                                            //_ = AddConsensusHeaderQueue(consensusHeader);
                                                                            if (Globals.LastBlock.Height < block.Height)
                                                                                await BlockValidatorService.ValidateBlocks();

                                                                            if (nextHeight == currentHeight)
                                                                            {
                                                                                //ConsoleWriterService.OutputVal($"Inside block service E");
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
                                        CasterRoundAudit.AddStep($"Validator failed to produce block: {finalizedWinner.Address}", true);
                                        //ConsoleWriterService.OutputVal($"\r\nValidator failed to produce block: {finalizedWinner.Address}");
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

                                            CasterRoundAudit.AddStep($"Address: {finalizedWinner.Address} added to failed producers. (Globals.FailedProducers)", true);
                                            //ConsoleWriterService.OutputVal($"\r\nAddress: {finalizedWinner.Address} added to failed producers. (Globals.FailedProducers)");
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

                                            CasterRoundAudit.AddStep($"2-Validator failed to produce block: {finalizedWinner.Address}", true);
                                            //ConsoleWriterService.OutputVal($"\r\n2-Validator failed to produce block: {finalizedWinner.Address}");
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
                    else
                    {
                        CasterRoundAudit = null;//round was starting. 
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

        #endregion

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

        #region Get Winning Proof Backup Method
        public static async Task GetWinningProof(Proof proof)
        {
            // Create a CancellationTokenSource with a timeout of 5 seconds
            var validators = Globals.BlockCasters.ToList();

            try
            {
                var rnd = new Random();
                var randomizedValidators = validators
                    .OrderBy(x => rnd.Next())
                    .ToList();

                if (!randomizedValidators.Any())
                    return;

                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds <= PROOF_COLLECTION_TIME)
                {
                    try
                    {
                        foreach(var validator in randomizedValidators)
                        {
                            using (var client = Globals.HttpClientFactory.CreateClient())
                            {
                                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(CASTER_VOTE_WINDOW));
                                try
                                {
                                    var uri = $"http://{validator.PeerIP.Replace("::ffff:", "")}:{Globals.ValAPIPort}/valapi/validator/SendWinningProof/{proof.BlockHeight}";
                                    var response = await client.GetAsync(uri, cts.Token);
                                    await Task.Delay(200);
                                    if (response.IsSuccessStatusCode)
                                    {
                                        var responseJson = await response.Content.ReadAsStringAsync();
                                        if (responseJson != null)
                                        {
                                            if (responseJson != "0")
                                            {
                                                var remoteCasterProof = JsonConvert.DeserializeObject<Proof>(responseJson);
                                                if (remoteCasterProof != null)
                                                {
                                                    if (remoteCasterProof.VerifyProof())
                                                    {
                                                        if (!Globals.CasterProofDict.ContainsKey(validator.PeerIP))
                                                        {
                                                            while (!Globals.CasterProofDict.TryAdd(validator.PeerIP, remoteCasterProof))
                                                            {
                                                                await Task.Delay(75);
                                                            }
                                                        }

                                                    }
                                                    await Task.Delay(200);
                                                }
                                            }
                                        }
                                    }
                                    else
                                    {
                                        await Task.Delay(200);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    //ConsoleWriterService.OutputVal($"\r\nError getting proof from address: {validator.PeerIP}.");
                                    //ConsoleWriterService.OutputVal($"ERROR: {ex}.");
                                }
                            }
                        }
                    }
                    catch (Exception ex) { }
                }

                sw.Stop();
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error in proof distribution: {ex.Message}", "ValidatorNode.SendWinningProof()");
            }
        }
        #endregion

        #region Send Winning Proof 
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

                foreach (var validator in randomizedValidators)
                {
                    if (sw.ElapsedMilliseconds >= PROOF_COLLECTION_TIME)
                    {
                        // Stop processing if cancellation is requested
                        sw.Stop();
                        return;
                    }

                    try
                    {
                        using (var client = Globals.HttpClientFactory.CreateClient())
                        {
                            // Create a request-specific CancellationTokenSource with a 1-second timeout
                            var uri = $"http://{validator.NodeIP.Replace("::ffff:", "")}:{Globals.ValAPIPort}/valapi/validator/ReceiveWinningProof";
                            await client.PostAsync(uri, httpContent).WaitAsync(new TimeSpan(0, 0, 3));
                            await Task.Delay(100);
                        }

                    }
                    catch (Exception ex)
                    {
                        // Log or handle the exception if needed
                    }
                }

                randomizedValidators.ParallelLoop(async validator =>
                {
                    
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

        #region CleanupApprovedCasterBlocks
        public static async Task CleanupApprovedCasterBlocks()
        {
            var blockPoint = Globals.LastBlock.Height - 50;
            var blocksToRemove = Globals.CasterApprovedBlockHashDict.Where(x => x.Key <= blockPoint).ToList();
            foreach (var block in blocksToRemove)
            {
                int retryCount = 0;
                while (!Globals.CasterApprovedBlockHashDict.TryRemove(block.Key, out var _) && retryCount < 5)
                {
                    retryCount++;
                    await Task.Delay(50);
                }

                // Log if removal consistently fails
                if (retryCount >= 5)
                {
                    ConsoleWriterService.OutputVal($"Warning: Could not remove block {block.Key} from CasterApprovedBlockHashDict");
                }
            }

            var roundsToRemove = Globals.CasterRoundDict.Where(x => x.Key <= blockPoint).ToList();
            foreach (var round in roundsToRemove)
            {
                int retryCount = 0;
                while (!Globals.CasterRoundDict.TryRemove(round.Key, out var _) && retryCount < 5)
                {
                    retryCount++;
                    await Task.Delay(50);
                }

                // Log if removal consistently fails
                if (retryCount >= 5)
                {
                    ConsoleWriterService.OutputVal($"Warning: Could not remove round {round.Key} from CasterRoundDict");
                }
            }
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
