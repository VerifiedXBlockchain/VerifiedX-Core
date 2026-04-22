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
using System.Threading;
using System.Threading.Tasks.Dataflow;
using System.Xml.Schema;
using static ReserveBlockCore.Utilities.CasterLogUtility;


namespace ReserveBlockCore.Nodes
{
    public class BlockcasterNode : IHostedService, IDisposable
    {
        #region Variables and Instance Class
        public static IHubContext<P2PBlockcasterServer> HubContext;
        private readonly IHubContext<P2PBlockcasterServer> _hubContext;
        private readonly IHostApplicationLifetime _appLifetime;
        private static ConcurrentBag<(string, long, string)> ValidatorApprovalBag = new ConcurrentBag<(string, long, string)>();
        const int PROOF_COLLECTION_TIME = 3000; // 3 seconds — sync barrier for caster proof exchange (was 4s)
        const int APPROVAL_WINDOW = 4000;       // 4 seconds (was 8s) — reduced because VRF is deterministic
        const int CASTER_VOTE_WINDOW = 3000;    // 3 seconds per-caster HTTP timeout (was 4s)
        const int GET_APPROVAL_HTTP_TIMEOUT_MS = 2000; // per-caster HTTP; parallelized
        const int BLOCK_REQUEST_WINDOW = 8000;  // 8 seconds (was 12s)
        const int MAX_ROUND_DURATION_MS = 20000; // 20 seconds — hard cap for entire consensus round
        const int WINNER_VERIFY_TIMEOUT_MS = 5000; // 5 seconds — total time for winner verification
        const int RETRY_DELAY_MS = 300;          // 300ms — delay between round retries
        /// <summary>Replacement-round state; <see langword="volatile"/> so readers see latest reference without torn reads of the field itself.</summary>
        public static volatile ReplacementRound? _currentRound;
        public static volatile List<string>? _allCasterAddresses;
        public static CasterRoundAudit? CasterRoundAudit = null;
        static long _lastStartingOverLogTicks;
    /// <summary>Height-based deduplication: tracks the highest block height currently being processed or already accepted via ReceiveConfirmedBlock.</summary>
    private static long _acceptedHeight = -1;
    /// <summary>Timestamp (Environment.TickCount64) of the last successful block acceptance. Used for desync recovery.</summary>
    private static long _lastBlockAcceptedTick = Environment.TickCount64;
    const int DESYNC_RECOVERY_TIMEOUT_MS = 45000; // 45 seconds without a new block → bypass consensus gate
        /// <summary>Throttle for "block rejected" log messages — tracks last logged height per validator to prevent spam.</summary>
        private static readonly ConcurrentDictionary<string, long> _rejectionLogTracker = new();

    // Dynamic reference points for block delay calculation — reset after each block
    private static long ReferenceHeight = -1;
    private static long ReferenceTime = -1;

    // Readiness barrier constants
    const int READINESS_CHECK_INTERVAL_MS = 2000;
    const int READINESS_MAX_WAIT_MS = 60000; // 60 seconds max wait for peers
    const int BLOCK_HASH_AGREEMENT_TIMEOUT_MS = 5000; // 5 seconds for block hash agreement phase
    /// <summary>When message 7 arrives slightly before local hash agreement publishes <see cref="Globals.CasterApprovedBlockHashDict"/>, wait briefly.</summary>
    const int RECEIVE_AGREED_HASH_SPIN_MS = 300;
    const int WINNER_AGREEMENT_TIMEOUT_MS = 4000; // 4 seconds for mandatory winner agreement phase

    /// <summary>Tracks consecutive hash sync failures at the same height to break infinite loops.</summary>
    private static long _hashSyncFailHeight = -1;
    private static int _hashSyncFailCount = 0;
    const int HASH_SYNC_MAX_RETRIES = 3; // After this many consecutive failures at same height, skip sync and proceed

    /// <summary>Consecutive rounds where multi-caster block hash agreement failed (no commit).</summary>
    private static int _consecutiveBlockHashAgreementFailures;
    const int BLOCK_HASH_AGREEMENT_RECONCILE_THRESHOLD = 3;

    /// <summary>Consecutive failures to fetch the majority block during hash agreement (divergence risk).</summary>
    private static int _consecutiveMajorityBlockFetchFailures;
    const int BLOCK_FETCH_FAIL_HALT_THRESHOLD = 5;
    private static int _casterConsensusHalted; // 0 = running, 1 = halt until reconciliation


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

            _ = MonitorCasters();
        }

        #endregion

        #region Monitor Casters
        private static async Task MonitorCasters()
        {
            while (true && !string.IsNullOrEmpty(Globals.ValidatorAddress))
            {
                var delay = Task.Delay(new TimeSpan(0, 0, 5));

                if (!Globals.BlockCasters.Any())
                {
                    await ValidatorNode.GetBlockcasters();
                    await delay;
                    continue;
                }

                if (!Globals.IsBlockCaster)
                {
                    await Task.Delay(new TimeSpan(0, 0, 30));
                    continue;
                }

                if (!Globals.IsBootstrapMode)
                    await PingCasters();

                if (Globals.BlockCasters.Any())
                {
                    await CasterDiscoveryService.EvaluateCasterPool();
                    await CasterDiscoveryService.CheckForStall();
                }
                else if (Globals.IsBlockCaster)
                {
                    await CasterDiscoveryService.EvaluateCasterPool();
                }

                var casterList = Globals.BlockCasters.ToList();

                if (!Globals.IsBootstrapMode && casterList.Count < Globals.MaxBlockCasters)
                    await InitiateReplacement(Globals.LastBlock.Height);

                await CasterDiscoveryService.RefreshIfDueAsync();

                // Periodically audit existing casters for outdated versions.
                // This prevents deadlocks where a caster inflates the quorum but can't produce proofs.
                await CasterDiscoveryService.AuditExistingCasterVersions();

                await Task.Delay(10000);

            }
        }

        private static async Task BroadcastConsensusBlockAsync(Block block, string winnerAddress)
        {
            if (block == null)
                return;
            await ConsensusCertificateHelper.TryAttachCertificateAsync(block, winnerAddress);
            _ = Broadcast("7", JsonConvert.SerializeObject(block), "");
        }

        /// <summary>One caster HTTP poll; used in parallel so N casters finish in ~one timeout instead of N×6s.</summary>
        private static async Task PollCasterGetApprovalAsync(Peers caster, Proof finalizedWinner)
        {
            if (string.IsNullOrEmpty(caster.PeerIP))
                return;
            using var client = Globals.HttpClientFactory.CreateClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(GET_APPROVAL_HTTP_TIMEOUT_MS));
            try
            {
                var uri = $"http://{caster.PeerIP.Replace("::ffff:", "")}:{Globals.ValAPIPort}/valapi/validator/getapproval/{finalizedWinner.BlockHeight}";
                var response = await client.GetAsync(uri, cts.Token);
                if (!response.IsSuccessStatusCode)
                    return;
                var responseJson = await response.Content.ReadAsStringAsync();
                if (responseJson == null)
                    return;
                if (responseJson == "1")
                {
                    await Task.Delay(200);
                    for (var count = 0; count < 3; count++)
                    {
                        response = await client.GetAsync(uri, cts.Token);
                        responseJson = await response.Content.ReadAsStringAsync();
                        if (responseJson == "0" || responseJson != "1")
                            await Task.Delay(200);
                        else
                            break;
                    }
                }
                if (responseJson != "0" && responseJson != "1")
                {
                    var remoteCasterRound = JsonConvert.DeserializeObject<CasterRound>(responseJson);
                    if (remoteCasterRound != null)
                        await GetApproval(caster.PeerIP, finalizedWinner.BlockHeight, remoteCasterRound.Validator);
                }
            }
            catch (Exception ex)
            {
                if (Globals.OptionalLogging)
                    ErrorLogUtility.LogError($"PollCasterGetApproval {caster.PeerIP}: {ex.Message}", "BlockcasterNode");
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

            foreach (var caster in casterList)
            {
                int retryCount = 0;
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
                Globals.BlockCasters = nBag;
                Globals.SyncKnownCastersFromBlockCasters();

                foreach (var removedIP in removeList)
                {
                    var removedCaster = casterList.FirstOrDefault(c => c.PeerIP == removedIP);
                    var removedAddr = removedCaster?.ValidatorAddress ?? "unknown";
                    ConsoleWriterService.OutputVal($"[PingCasters] Removed offline caster {removedAddr} ({removedIP})");
                    _ = CasterDiscoveryService.OnCasterRemoved(removedAddr);
                }
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

                    // Version gate: reject candidates on outdated major versions.
                    // This was missing and allowed outdated nodes to become casters,
                    // inflating the quorum while being unable to participate properly.
                    if (!string.IsNullOrEmpty(nCaster.IPAddress))
                    {
                        var ip = nCaster.IPAddress.Replace("::ffff:", "");
                        if (!await CasterDiscoveryService.CheckCandidateVersion(ip, nCaster.Address))
                        {
                            ConsoleWriterService.OutputVal($"[InitiateReplacement] Candidate {nCaster.Address} failed version check. Skipping.");
                            break;
                        }
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
            var PreviousHeight = -1L;
            var BlockDelay = Task.CompletedTask;
            ConsoleWriterService.OutputVal("Booting up consensus loop");
            CasterLogUtility.Clear();
            CasterLogUtility.Log($"=== Consensus loop starting. Validator={Globals.ValidatorAddress} ===", "BOOT");

            while (true && !string.IsNullOrEmpty(Globals.ValidatorAddress))
            {
                if (!Globals.BlockCasters.Any())
                {
                    await ValidatorNode.GetBlockcasters();
                    await delay;
                    continue;
                }

                var casterList = Globals.BlockCasters.ToList();
                if (casterList.Exists(x => x.ValidatorAddress == Globals.ValidatorAddress))
                    Globals.IsBlockCaster = true;
                else
                    Globals.IsBlockCaster = false;

                if (!Globals.IsBlockCaster)
                {
                    await Task.Delay(new TimeSpan(0, 0, 30));
                    continue;
                }

                ConsoleWriterService.OutputVal("Top of consensus loop");

                Block? block = null;

                try
                {
                    var Height = Globals.LastBlock.Height + 1;

                    if (Height != Globals.LastBlock.Height + 1)
                        continue;

                    if (PreviousHeight == -1L) // First time running
                    {
                        await WaitForNextConsensusRound();
                    }

                    if (CasterRoundAudit == null || CasterRoundAudit.BlockHeight < Height)
                    {
                        CasterRoundAudit = new CasterRoundAudit(Height);
                        Console.Clear();
                    }
                    else if (CasterRoundAudit.BlockHeight == Height)
                    {
                        CasterRoundAudit.AddStep($"Retry at height {Height}…", false);
                    }

                    var roundSw = Stopwatch.StartNew(); // Track total round time
                    CasterLogUtility.Log($"--- ROUND START height={Height} lastBlock={Globals.LastBlock.Height} lastHash={Globals.LastBlock.Hash?[..Math.Min(16, Globals.LastBlock.Hash?.Length ?? 0)]} ---", "ROUND");

                    if (Volatile.Read(ref _casterConsensusHalted) != 0)
                    {
                        await SyncBlockHashWithPeersAsync();
                        await SyncHeightWithPeersAsync();
                        try { await BlockDownloadService.GetAllBlocks(); } catch { }
                        Interlocked.Exchange(ref _casterConsensusHalted, 0);
                        await Task.Delay(Math.Max(2000, Globals.BlockTime / 4));
                    }

                    // FIX 1 (CRITICAL): Block hash sync before VRF computation.
                    // If our LastBlock.Hash differs from peer casters, VRF seeds diverge and
                    // casters pick different winners → fork. Sync BEFORE generating proofs.
                    await SyncBlockHashWithPeersAsync();

                    if (PreviousHeight != Height)
                    {
                        PreviousHeight = Height;
                        await Task.WhenAll(BlockDelay, Task.Delay(1500));
                        
                        // Adaptive timing: correct for drift so blocks stay near target interval
                        if (ReferenceHeight == -1)
                        {
                            ReferenceHeight = Globals.LastBlock.Height;
                            ReferenceTime = TimeUtil.GetMillisecondTime();
                        }
                        
                        var CurrentTime = TimeUtil.GetMillisecondTime();
                        var DelayTimeCorrection = Globals.BlockTime * (Height - ReferenceHeight) - (CurrentTime - ReferenceTime);
                        var DelayTime = Math.Min(Math.Max(Globals.BlockTime + DelayTimeCorrection, Globals.BlockTimeMin), Globals.BlockTimeMax);
                        BlockDelay = Task.Delay((int)DelayTime);

                        CasterRoundAudit.AddStep("Next Consensus Delay: " + DelayTime + " (" + DelayTimeCorrection + ")", true);
                    }

                    _ = CleanupApprovedCasterBlocks();

                    await ValidatorSnapshotService.RefreshSnapshotIfNeededAsync(Height);

                    ValidatorApprovalBag.Clear();
                    ValidatorApprovalBag = new ConcurrentBag<(string, long, string)>();
                    //Generate Proofs for ALL vals
                    CasterRoundAudit.AddStep($"Generating Proofs for height: {Height}.", true);
                    //ConsoleWriterService.OutputVal($"\r\nGenerating Proofs for height: {Height}.");
                    var proofGenSw = Stopwatch.StartNew();
                    var casterProofs = await ProofUtility.GenerateCasterProofs();
                    var proofs = await ProofUtility.GenerateProofs();
                    proofGenSw.Stop();
                    CasterLogUtility.Log($"ProofGen: {proofGenSw.ElapsedMilliseconds}ms, casterProofs={casterProofs.Count}, allProofs={proofs.Count}", "PROOFS");
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

                    // Always store our winning caster proof in CasterRoundDict so other casters can fetch it via HTTP
                    // (SendWinningProof endpoint reads from CasterRoundDict)
                    if (winningCasterProof != null)
                    {
                        var earlyRound = Globals.CasterRoundDict[Height];
                        if (earlyRound != null)
                        {
                            var compareEarlyRound = earlyRound;
                            earlyRound.Proof = winningCasterProof;
                            while (!Globals.CasterRoundDict.TryUpdate(Height, earlyRound, compareEarlyRound)) ;
                        }
                    }

                    if (winningCasterProof != null && casterProofs.Count() > 0)
                    {
                        CasterLogUtility.Log($"Winner candidate: {winningCasterProof.Address} VRF={winningCasterProof.VRFNumber}", "VERIFY");
                        CasterRoundAudit.AddStep($"Attempting Proof on Address: {winningCasterProof.Address} (casterProofs: {casterProofs.Count()})", true);
                        var verificationResult = false;
                        List<string> ExcludeValList = new List<string>();
                        var verifySw = Stopwatch.StartNew();
                        int verifyAttempts = 0;
                        while (!verificationResult && verifySw.ElapsedMilliseconds < WINNER_VERIFY_TIMEOUT_MS)
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

                        verifySw.Stop();
                        CasterLogUtility.Log($"Verify done: {verifySw.ElapsedMilliseconds}ms, attempts={verifyAttempts}, winner={winningCasterProof?.Address ?? "NULL"}", "VERIFY");

                        if (winningCasterProof == null)
                        {
                            if (CasterRoundAudit != null)
                                CasterRoundAudit.AddStep("Could not verify winning caster; retrying…", false);
                            CasterLogUtility.Log($"ROUND FAILED: no verified winner after {roundSw.ElapsedMilliseconds}ms", "ROUND");
                            CasterLogUtility.Flush();
                            await Task.Delay(RETRY_DELAY_MS);
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

                        // Inject our own proof directly (avoids self-HTTP which can fail behind NAT)
                        var selfIP = Globals.BlockCasters.FirstOrDefault(c => c.ValidatorAddress == Globals.ValidatorAddress)?.PeerIP;
                        if (selfIP != null && winningCasterProof != null)
                            Globals.CasterProofDict.TryAdd(selfIP, winningCasterProof);

                        // FIX 4: Clamp requiredProofs to actual unique caster ADDRESSES (not casterList.Count
                        // which can include phantom entries from replacement logic).
                        var uniqueCasterAddresses = casterList
                            .Where(c => !string.IsNullOrEmpty(c.ValidatorAddress))
                            .Select(c => c.ValidatorAddress)
                            .Distinct()
                            .Count();
                        var effectiveCasterCount = Math.Max(uniqueCasterAddresses, 1);
                        var requiredProofs = Math.Max(2, effectiveCasterCount / 2 + 1); // majority quorum
                        CasterLogUtility.Log($"ProofExchange: need {requiredProofs}/{casterList.Count} proofs, have {Globals.CasterProofDict.Count()} (self-injected)", "EXCHANGE");
                        var swProofCollectionTime = Stopwatch.StartNew();
                        while (swProofCollectionTime.ElapsedMilliseconds <= PROOF_COLLECTION_TIME)
                        {
                            // FIX 3: Only pull proofs via HTTP (GetWinningProof).
                            // SendWinningProof uses BlockCasterNodes (SignalR) which is always empty — removed.
                            await GetWinningProof(winningCasterProof);
                            
                            // Early exit if we have enough proofs
                            if (Globals.CasterProofDict.Count() >= requiredProofs)
                                break;
                            
                            await Task.Delay(500);
                        }
                        swProofCollectionTime.Stop();

                        CasterLogUtility.Log($"ProofExchange done: {swProofCollectionTime.ElapsedMilliseconds}ms, collected={Globals.CasterProofDict.Count()}/{requiredProofs}", "EXCHANGE");

                        if (Globals.CasterProofDict.Count() < requiredProofs)
                        {
                            if (CasterRoundAudit != null)
                                CasterRoundAudit.AddStep($"Caster P2P proofs {Globals.CasterProofDict.Count()}/{requiredProofs}; retrying…", false);
                            CasterLogUtility.Log($"ROUND FAILED: insufficient proofs after {roundSw.ElapsedMilliseconds}ms", "ROUND");
                            CasterLogUtility.Flush();
                            await Task.Delay(RETRY_DELAY_MS);
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
                                CasterLogUtility.Log($"Finalized winner: {finalizedWinner.Address} VRF={finalizedWinner.VRFNumber} proofCount={proofSnapshot.Count}", "WINNER");
                                CasterRoundAudit.AddStep($"Finalized winner : {finalizedWinner.Address}", true);

                                // CASTER-CONSENSUS-FIX: Mandatory winner agreement phase.
                                // Instead of skipping approval entirely (which caused desync when VRF inputs differed),
                                // exchange winner votes with all peer casters and require supermajority agreement.
                                // This guarantees all casters craft/fetch a block for the SAME validator.
                                var agreedWinner = await ReachWinnerAgreementAsync(Height, finalizedWinner.Address);
                                if (agreedWinner == null)
                                {
                                    CasterRoundAudit?.AddStep($"Winner agreement FAILED — no supermajority. Retrying round.", false);
                                    CasterLogUtility.Log($"ROUND FAILED: winner agreement failed after {roundSw.ElapsedMilliseconds}ms", "ROUND");
                                    CasterLogUtility.Flush();
                                    await Task.Delay(RETRY_DELAY_MS);
                                    continue;
                                }

                                var terminalWinner = agreedWinner;
                                var approved = true;

                                // Update CasterRoundDict with the agreed winner
                                {
                                    var casterRound = Globals.CasterRoundDict.GetOrAdd(finalizedWinner.BlockHeight, new CasterRound { BlockHeight = finalizedWinner.BlockHeight });
                                    var compareRound = casterRound;
                                    casterRound.Validator = terminalWinner;
                                    casterRound.BlockHeight = finalizedWinner.BlockHeight;
                                    casterRound.ProgressRound();
                                    Globals.CasterRoundDict.TryUpdate(finalizedWinner.BlockHeight, casterRound, compareRound);
                                }

                                CasterLogUtility.Log($"Winner AGREED: {terminalWinner} (local candidate was {finalizedWinner.Address})", "AGREEMENT");
                                CasterRoundAudit.AddStep($"Winner agreed: {terminalWinner}", true);

                                CasterLogUtility.Log($"Block fetch phase: iAmWinner={terminalWinner == Globals.ValidatorAddress}, winner={terminalWinner}", "BLOCKFETCH");
                                if (Globals.LastBlock.Height < finalizedWinner.BlockHeight && approved)
                                {
                                    bool blockFound = false;
                                    bool failedToReachConsensus = false;
                                    var swb = Stopwatch.StartNew();
                                    while (!blockFound && swb.ElapsedMilliseconds < BLOCK_REQUEST_WINDOW)
                                    {
                                        if (terminalWinner == Globals.ValidatorAddress)
                                        {
                                            // FIX: Craft the block exactly ONCE when we are the winning validator.
                                            // Previously this looped through random validators, each independently crafting
                                            // a different block (different timestamp/txs = different hash).
                                            // Now we use Globals.NextValidatorBlock (pre-crafted) or request from ONE source only.
                                            
                                            // First check CasterRoundDict — another caster may have already stored the block
                                            if (Globals.CasterRoundDict.TryGetValue(Height, out var existingRound) && existingRound?.Block != null)
                                            {
                                                block = existingRound.Block;
                                                blockFound = true;
                                            }
                                            
                                            // Try Globals.NextValidatorBlock (pre-crafted by this validator)
                                            if (!blockFound)
                                            {
                                                var preBlock = Globals.NextValidatorBlock;
                                                if (preBlock != null && preBlock.Height == Height)
                                                {
                                                    block = preBlock;
                                                    blockFound = true;
                                                }
                                            }
                                            
                                            // If still no block, request from exactly ONE network validator to craft it
                                            if (!blockFound)
                                            {
                                                var validators = Globals.NetworkValidators.Values.ToList();
                                                var excludeVals = new List<string>();
                                                
                                                while (!blockFound && swb.ElapsedMilliseconds < BLOCK_REQUEST_WINDOW)
                                                {
                                                    var rnd = new Random();
                                                    var randomizedValidator = validators
                                                        .Where(x => !excludeVals.Contains(x.IPAddress))
                                                        .OrderBy(x => rnd.Next())
                                                        .ToList()
                                                        .FirstOrDefault();
                                                    
                                                    if (randomizedValidator == null)
                                                    {
                                                        // No validators available — use pre-crafted block as last resort
                                                        block = Globals.NextValidatorBlock;
                                                        if (block != null)
                                                            blockFound = true;
                                                        break;
                                                    }
                                                    
                                                    var verificationResultTuple = await ProofUtility.VerifyValAvailability(randomizedValidator.IPAddress, randomizedValidator.Address, Height);
                                                    verificationResult = verificationResultTuple.Item1;
                                                    if (!verificationResult || verificationResultTuple.Item2 == null)
                                                    {
                                                        excludeVals.Add(randomizedValidator.IPAddress);
                                                        continue;
                                                    }
                                                    
                                                    block = verificationResultTuple.Item2;
                                                    blockFound = true;
                                                    break; // Use THIS block — don't try other validators
                                                }
                                            }
                                            
                                            // Store the single crafted block in CasterRoundDict so all casters share the same version
                                            if (blockFound && block != null)
                                            {
                                                round = Globals.CasterRoundDict.GetOrAdd(block.Height, new CasterRound { BlockHeight = block.Height });
                                                var compareRound = round;
                                                round.Block = block;
                                                round.Validator = Globals.ValidatorAddress;
                                                Globals.CasterRoundDict.TryUpdate(finalizedWinner.BlockHeight, round, compareRound);
                                            }

                                            if(blockFound)
                                            {
                                                // Staged only in CasterRoundDict above — commit/broadcast runs after VerifyBlockHashAgreementAsync.
                                                CasterRoundAudit.AddStep($"Staged winning caster block at height {block.Height}; awaiting caster hash agreement before commit.", true);
                                            }

                                            
                                        }
                                        else
                                        {
                                            // FIX 4: Fetch block directly from each caster via RequestBlock endpoint.
                                            // FetchBlockWithRedundantCasterAgreementAsync required supermajority agreement
                                            // but non-winning casters don't have the block yet, causing it to always fail.
                                            CasterRoundAudit.AddStep($"Requesting block from peer casters directly.", true);
                                            foreach (var caster in Globals.BlockCasters.ToList())
                                            {
                                                try
                                                {
                                                    block = await CasterBlockFetch.TryFetchBlockAsync(caster, finalizedWinner.BlockHeight, terminalWinner);
                                                    if (block != null && block.Validator == terminalWinner)
                                                    {
                                                        failedToReachConsensus = false;
                                                        blockFound = true;

                                                        round = Globals.CasterRoundDict.GetOrAdd(block.Height, new CasterRound { BlockHeight = block.Height });
                                                        var compareRound = round;
                                                        round.Block = block;
                                                        round.Validator = block.Validator;
                                                        Globals.CasterRoundDict.TryUpdate(finalizedWinner.BlockHeight, round, compareRound);

                                                        CasterRoundAudit.AddStep($"Block fetched from {caster.PeerIP}. Height: {block.Height} (staged; commit after hash agreement).", true);

                                                        break;
                                                    }
                                                    else
                                                    {
                                                        failedToReachConsensus = true;
                                                    }
                                                }
                                                catch { failedToReachConsensus = true; }
                                                await Task.Delay(75);
                                            }
                                        }
                                        /////////////////////////////////////
                                        //This is done if non-caster wins the block

                                        await Task.Delay(200);
                                    }

                                    CasterLogUtility.Log($"BlockFetch done: {swb.ElapsedMilliseconds}ms, found={blockFound}, failed={failedToReachConsensus}", "BLOCKFETCH");

                                    // CASTER-SYNC-FIX: Block hash agreement phase — verify all casters have the same block
                                    if (blockFound && block != null)
                                    {
                                        CasterRoundAudit.AddStep($"[BlockHashAgreement] Verifying block hash agreement with peer casters…", true);
                                        var agreedBlock = await VerifyBlockHashAgreementAsync(block, Height, terminalWinner);
                                        if (agreedBlock != null)
                                        {
                                            block = agreedBlock;
                                            var producerIp = finalizedWinner.IPAddress;
                                            var winnerCraftedLayout = terminalWinner == Globals.ValidatorAddress;
                                            await CommitCasterBlockPostAgreementAsync(block, terminalWinner, producerIp, winnerCraftedLayout);
                                        }
                                        else
                                            await OnBlockHashAgreementRoundRejectedAsync(Height, terminalWinner);
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        var cc = casterProofs?.Count() ?? 0;
                        if (CasterRoundAudit != null)
                            CasterRoundAudit.AddStep($"No caster proofs generated (have {cc}). Check peer public keys / balances / val API / firewall.", false);
                        await Task.Delay(Math.Max(3000, Globals.BlockTime / 4));
                        continue;
                    }

                    CasterLogUtility.Log($"--- ROUND END height={Height} totalTime={roundSw.ElapsedMilliseconds}ms lastBlock={Globals.LastBlock.Height} ---", "ROUND");
                    CasterLogUtility.Flush();

                    if (Environment.TickCount64 - _lastStartingOverLogTicks >= 15_000)
                    {
                        ConsoleWriterService.OutputVal("\r\nStarting over.");
                        _lastStartingOverLogTicks = Environment.TickCount64;
                    }
                    Globals.Proofs.Clear();
                    Globals.Proofs = new ConcurrentBag<Proof>();
                    await Task.Delay(50);
                }
                catch (Exception ex)
                {
                }
            }
        }

        #region Block Hash Sync

        /// <summary>
        /// FIX 1 (CRITICAL): Before each round, verify our LastBlock.Hash matches peer casters.
        /// If hashes diverge, VRF seeds diverge → different winners → fork.
        /// Fetches the correct block from the majority if we're the outlier.
        /// </summary>
        private static async Task SyncBlockHashWithPeersAsync()
        {
            try
            {
                var myHeight = Globals.LastBlock.Height;
                var myHash = Globals.LastBlock.Hash;
                var casters = Globals.BlockCasters.ToList()
                    .Where(c => !string.IsNullOrEmpty(c.PeerIP) && c.ValidatorAddress != Globals.ValidatorAddress)
                    .ToList();
                
                if (casters.Count == 0) return;

                // FIX 2: Escape hatch — if we've failed to sync at the same height too many times,
                // skip and proceed. This breaks the infinite HASHSYNC MISMATCH loop caused by
                // stale CasterRoundDict data (now fixed in GetBlockHash endpoint).
                if (_hashSyncFailHeight == myHeight && _hashSyncFailCount >= HASH_SYNC_MAX_RETRIES)
                {
                    CasterLogUtility.Log($"BlockHashSync: SKIP — {_hashSyncFailCount} consecutive failures at height {myHeight}. Proceeding with consensus.", "HASHSYNC");
                    return;
                }

                var peerTasks = casters.Select(async caster =>
                {
                    try
                    {
                        using var client = Globals.HttpClientFactory.CreateClient();
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                        var uri = $"http://{caster.PeerIP!.Replace("::ffff:", "")}:{Globals.ValAPIPort}/valapi/validator/GetBlockHash/{myHeight}";
                        var resp = await client.GetAsync(uri, cts.Token);
                        if (resp.IsSuccessStatusCode)
                        {
                            var body = await resp.Content.ReadAsStringAsync();
                            if (!string.IsNullOrEmpty(body) && body != "0")
                            {
                                var peerResult = JsonConvert.DeserializeAnonymousType(body, new { Hash = "", Validator = "", Height = 0L });
                                if (peerResult != null && peerResult.Height == myHeight && !string.IsNullOrEmpty(peerResult.Hash))
                                    return (hash: peerResult.Hash, ip: caster.PeerIP!);
                            }
                        }
                    }
                    catch { }
                    return (hash: (string?)null, ip: "");
                }).ToList();

                var results = await Task.WhenAll(peerTasks);
                
                var hashVotes = new Dictionary<string, int>();
                if (!string.IsNullOrEmpty(myHash))
                    hashVotes[myHash] = 1;
                
                var hashToIP = new Dictionary<string, string>();
                foreach (var r in results)
                {
                    if (r.hash != null)
                    {
                        if (!hashVotes.ContainsKey(r.hash))
                            hashVotes[r.hash] = 0;
                        hashVotes[r.hash]++;
                        if (!hashToIP.ContainsKey(r.hash))
                            hashToIP[r.hash] = r.ip;
                    }
                }

                if (hashVotes.Count <= 1)
                {
                    // All agree or no responses — reset failure counter
                    _hashSyncFailHeight = -1;
                    _hashSyncFailCount = 0;
                    return;
                }

                var majority = hashVotes.OrderByDescending(kv => kv.Value).First();
                
                if (majority.Key == myHash)
                {
                    CasterLogUtility.Log($"BlockHashSync: OK — all agree on {myHash?[..Math.Min(16, myHash?.Length ?? 0)]}", "HASHSYNC");
                    // Reset failure counter on success
                    _hashSyncFailHeight = -1;
                    _hashSyncFailCount = 0;
                    return;
                }

                CasterLogUtility.Log($"BlockHashSync: MISMATCH! ours={myHash?[..Math.Min(16, myHash?.Length ?? 0)]} majority={majority.Key[..Math.Min(16, majority.Key.Length)]} votes={majority.Value}", "HASHSYNC");

                bool syncSucceeded = false;
                if (hashToIP.TryGetValue(majority.Key, out var sourceIP))
                {
                    try
                    {
                        using var client = Globals.HttpClientFactory.CreateClient();
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        var uri = $"http://{sourceIP.Replace("::ffff:", "")}:{Globals.ValAPIPort}/valapi/validator/GetBlock/{myHeight}";
                        var resp = await client.GetAsync(uri, cts.Token);
                        if (resp.IsSuccessStatusCode)
                        {
                            var json = await resp.Content.ReadAsStringAsync();
                            if (!string.IsNullOrEmpty(json) && json != "0")
                            {
                                var correctBlock = JsonConvert.DeserializeObject<Block>(json);
                                if (correctBlock != null && correctBlock.Hash == majority.Key)
                                {
                                    var result = await BlockValidatorService.ValidateBlock(correctBlock, true, false, false, true);
                                    if (result)
                                    {
                                        CasterLogUtility.Log($"BlockHashSync: Applied majority block. New hash={Globals.LastBlock.Hash?[..Math.Min(16, Globals.LastBlock.Hash?.Length ?? 0)]}", "HASHSYNC");
                                        syncSucceeded = true;
                                    }
                                    else
                                        CasterLogUtility.Log($"BlockHashSync: Majority block validation FAILED.", "HASHSYNC");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        CasterLogUtility.Log($"BlockHashSync: Error fetching majority block: {ex.Message}", "HASHSYNC");
                    }
                }

                // FIX 2: Track consecutive failures to enable escape hatch
                if (!syncSucceeded)
                {
                    if (_hashSyncFailHeight == myHeight)
                    {
                        _hashSyncFailCount++;
                        CasterLogUtility.Log($"BlockHashSync: Sync failed {_hashSyncFailCount}/{HASH_SYNC_MAX_RETRIES} at height {myHeight}", "HASHSYNC");
                    }
                    else
                    {
                        _hashSyncFailHeight = myHeight;
                        _hashSyncFailCount = 1;
                    }
                }
                else
                {
                    _hashSyncFailHeight = -1;
                    _hashSyncFailCount = 0;
                }
            }
            catch (Exception ex)
            {
                CasterLogUtility.Log($"BlockHashSync: Exception: {ex.Message}", "HASHSYNC");
            }
        }

        #endregion

        #region Height Sync

        /// <summary>
        /// Queries peer casters' block heights and fetches/applies any missing blocks
        /// so this caster is at the same height before starting consensus.
        /// </summary>
        private static async Task SyncHeightWithPeersAsync()
        {
            try
            {
                var myHeight = Globals.LastBlock.Height;
                var casters = Globals.BlockCasters.ToList();
                if (casters.Count == 0) return;

                long maxPeerHeight = myHeight;
                string? bestPeerIP = null;

                // Query all peer casters for their height in parallel
                var tasks = casters
                    .Where(c => !string.IsNullOrEmpty(c.PeerIP))
                    .Select(async caster =>
                    {
                        try
                        {
                            using var client = Globals.HttpClientFactory.CreateClient();
                            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                            var uri = $"http://{caster.PeerIP!.Replace("::ffff:", "")}:{Globals.ValAPIPort}/valapi/validator/GetBlockHeight";
                            var resp = await client.GetAsync(uri, cts.Token);
                            if (resp.IsSuccessStatusCode)
                            {
                                var body = await resp.Content.ReadAsStringAsync();
                                if (long.TryParse(body, out var peerHeight))
                                    return (ip: caster.PeerIP!, height: peerHeight);
                            }
                        }
                        catch { }
                        return (ip: "", height: 0L);
                    })
                    .ToList();

                var results = await Task.WhenAll(tasks);
                foreach (var r in results)
                {
                    if (r.height > maxPeerHeight)
                    {
                        maxPeerHeight = r.height;
                        bestPeerIP = r.ip;
                    }
                }

                // If we're behind, fetch and apply missing blocks
                if (maxPeerHeight > myHeight && bestPeerIP != null)
                {
                    ConsoleWriterService.OutputVal($"\r\n[HeightSync] Behind by {maxPeerHeight - myHeight} block(s). Catching up from {bestPeerIP}...");
                    for (long h = myHeight + 1; h <= maxPeerHeight; h++)
                    {
                        try
                        {
                            using var client = Globals.HttpClientFactory.CreateClient();
                            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                            var uri = $"http://{bestPeerIP.Replace("::ffff:", "")}:{Globals.ValAPIPort}/valapi/validator/GetBlock/{h}";
                            var resp = await client.GetAsync(uri, cts.Token);
                            if (resp.IsSuccessStatusCode)
                            {
                                var json = await resp.Content.ReadAsStringAsync();
                                if (!string.IsNullOrEmpty(json) && json != "0")
                                {
                                    var block = Newtonsoft.Json.JsonConvert.DeserializeObject<Block>(json);
                                    if (block != null)
                                    {
                                        var result = await BlockValidatorService.ValidateBlock(block, true, false, false, true);
                                        if (result)
                                        {
                                            ConsoleWriterService.OutputVal($"[HeightSync] Applied block {h}.");
                                        }
                                        else
                                        {
                                            ConsoleWriterService.OutputVal($"[HeightSync] Block {h} validation failed. Stopping catch-up.");
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            ConsoleWriterService.OutputVal($"[HeightSync] Error fetching block {h}: {ex.Message}");
                            break;
                        }
                    }
                }
            }
            catch { /* best-effort sync */ }
        }

        #endregion

        #region Start Consensus
        private static async Task StartConsensus()
        {
            //start consensus run here.  
            var delay = Task.Delay(new TimeSpan(0, 0, 5));
            var PreviousHeight = -1L;
            var BlockDelay = Task.CompletedTask;
            ConsoleWriterService.OutputVal("Booting up consensus loop");

            while (true && !string.IsNullOrEmpty(Globals.ValidatorAddress))
            {
                if (!Globals.BlockCasters.Any())
                {
                    await ValidatorNode.GetBlockcasters();
                    await delay;
                    continue;
                }

                var casterList = Globals.BlockCasters.ToList();
                if (casterList.Exists(x => x.ValidatorAddress == Globals.ValidatorAddress))
                    Globals.IsBlockCaster = true;
                else
                    Globals.IsBlockCaster = false;

                if (!Globals.IsBlockCaster)
                {
                    await Task.Delay(new TimeSpan(0, 0, 30));
                    continue;
                }

                ConsoleWriterService.OutputVal("Top of consensus loop");

                Block? block = null;

                try
                {
                    var Height = Globals.LastBlock.Height + 1;

                    if (Height != Globals.LastBlock.Height + 1)
                        continue;

                    if (PreviousHeight == -1L) // First time running
                    {
                        await WaitForNextConsensusRound();
                    }

                    if (CasterRoundAudit == null || CasterRoundAudit.BlockHeight < Height)
                    {
                        CasterRoundAudit = new CasterRoundAudit(Height);
                        Console.Clear();
                    }
                    else if (CasterRoundAudit.BlockHeight == Height)
                    {
                        CasterRoundAudit.AddStep($"Retry at height {Height}…", false);
                    }

                    if (PreviousHeight != Height)
                    {
                        PreviousHeight = Height;
                        await Task.WhenAll(BlockDelay, Task.Delay(1500));
                        
                        // Initialize reference point on first run
                        if (ReferenceHeight == -1)
                        {
                            ReferenceHeight = Globals.LastBlock.Height;
                            ReferenceTime = TimeUtil.GetMillisecondTime();
                        }
                        
                        var CurrentTime = TimeUtil.GetMillisecondTime();
                        var DelayTimeCorrection = Globals.BlockTime * (Height - ReferenceHeight) - (CurrentTime - ReferenceTime);
                        var DelayTime = Math.Min(Math.Max(Globals.BlockTime + DelayTimeCorrection, Globals.BlockTimeMin), Globals.BlockTimeMax);
                        BlockDelay = Task.Delay((int)DelayTime);

                        CasterRoundAudit.AddStep("Next Consensus Delay: " + DelayTime + " (" + DelayTimeCorrection + ")", true);
                        //ConsoleWriterService.OutputVal("\r\nNext Consensus Delay: " + DelayTime + " (" + DelayTimeCorrection + ")");
                    }

                    _ = CleanupApprovedCasterBlocks();

                    await ValidatorSnapshotService.RefreshSnapshotIfNeededAsync(Height);

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
                            if (CasterRoundAudit != null)
                                CasterRoundAudit.AddStep("Could not verify winning caster; retrying…", false);
                            await Task.Delay(2000);
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
                                    var castersSnapB = Globals.BlockCasters.ToList();
                                    await Task.WhenAll(castersSnapB.Select(c => PollCasterGetApprovalAsync(c, finalizedWinner)));

                                    await Task.Delay(100);

                                    var vBag = ValidatorApprovalBag.Where(x => x.Item2 == finalizedWinner.BlockHeight).ToList();

                                    CasterRoundAudit.AddStep($"Validator Bag Count: {vBag.Count()}.", false);
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
                                                    // Agreed hash is registered in CommitCasterBlockPostAgreementAsync after hash agreement.
                                                }

                                                CasterRoundAudit.AddStep($"Bag was approved. Moving to next block.", true);
                                                //ConsoleWriterService.OutputVal($"\r\nBag was approved. Moving to next block.");

                                                approved = true;
                                                break;
                                            }
                                        }
                                        else
                                        {
                                            var tieSc = proofSnapshot
                                                .Where(x => x.BlockHeight == Height)
                                                .OrderBy(x => x.VRFNumber)
                                                .ThenBy(x => x.ProofHash, StringComparer.Ordinal)
                                                .ThenBy(x => x.Address, StringComparer.Ordinal)
                                                .FirstOrDefault();
                                            if (tieSc != null)
                                            {
                                                terminalWinner = tieSc.Address;
                                                CasterRoundAudit.AddStep($"Bag had no majority vote; tiebreak winner: {terminalWinner}", true);
                                                approved = true;
                                                break;
                                            }
                                            if (Globals.OptionalLogging)
                                                ConsoleWriterService.OutputVal("\r\n Bag failed. No Result was found.");
                                            await Task.Delay(200);
                                        }
                                    }
                                }

                                if (!approved)
                                {
                                    terminalWinner = finalizedWinner.Address;
                                    approved = true;
                                    CasterRoundAudit.AddStep($"Approval inconclusive; using finalized proof winner: {terminalWinner}", true);
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
                                            CasterRoundAudit.AddStep($"Already have block. Height: {block.Height} (staged; commit after hash agreement).", true);
                                            var nextHeight = Globals.LastBlock.Height + 1;
                                            var currentHeight = block.Height;

                                            if (currentHeight < nextHeight)
                                            {
                                                blockFound = true;
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
                                            foreach (var casters in Globals.BlockCasters)
                                            {
                                                block = await CasterBlockFetch.TryFetchBlockAsync(casters, finalizedWinner.BlockHeight, terminalWinner);
                                                if (block == null)
                                                {
                                                    failedToReachConsensus = true;
                                                    await Task.Delay(75);
                                                    continue;
                                                }

                                                                if(block.Validator != terminalWinner)
                                                                    {
                                                                        failedToReachConsensus = true;
                                                                        await Task.Delay(75);
                                                                        continue;
                                                                    }

                                                                    failedToReachConsensus = false;

                                                                    while (true)
                                                                    {
                                                                        round = Globals.CasterRoundDict.GetOrAdd(block.Height, new CasterRound { BlockHeight = block.Height });
                                                                        var compareInner = round;
                                                                        round.Block = block;
                                                                        round.Validator = block.Validator;
                                                                        if (Globals.CasterRoundDict.TryUpdate(finalizedWinner.BlockHeight, round, compareInner))
                                                                            break;
                                                                    }

                                                                    CasterRoundAudit.AddStep($"Block deserialized. Height: {block.Height} (staged; commit after hash agreement).", true);
                                                                    blockFound = true;
                                                                    break;

                                                await Task.Delay(75);
                                            }
                                        }
                                        catch (Exception ex) { }

                                        await Task.Delay(200);
                                    }

                                    CasterLogUtility.Log($"BlockFetch done (StartConsensus): found={blockFound}, failed={failedToReachConsensus}", "BLOCKFETCH");
                                    if (blockFound && block != null && !string.IsNullOrEmpty(terminalWinner))
                                    {
                                        var agreeHeight = finalizedWinner.BlockHeight;
                                        CasterRoundAudit.AddStep($"[BlockHashAgreement] Verifying block hash agreement with peer casters…", true);
                                        var agreedBlockSc = await VerifyBlockHashAgreementAsync(block, agreeHeight, terminalWinner);
                                        if (agreedBlockSc != null)
                                        {
                                            block = agreedBlockSc;
                                            await CommitCasterBlockPostAgreementAsync(block, terminalWinner, finalizedWinner.IPAddress, false);
                                        }
                                        else
                                            await OnBlockHashAgreementRoundRejectedAsync(agreeHeight, terminalWinner);
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
                        var cc = casterProofs?.Count() ?? 0;
                        if (CasterRoundAudit != null)
                            CasterRoundAudit.AddStep($"Need ≥3 caster proofs (have {cc}). Augmented from BlockCasters + chain; check balances / val API / firewall.", false);
                        await Task.Delay(Math.Max(3000, Globals.BlockTime / 4));
                        continue;
                    }

                    if (Environment.TickCount64 - _lastStartingOverLogTicks >= 15_000)
                    {
                        ConsoleWriterService.OutputVal("\r\nStarting over.");
                        _lastStartingOverLogTicks = Environment.TickCount64;
                    }
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
            P2P.P2PClient.TryAutoUpdateReportedIP();
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
                // Height-based deduplication: prevent processing competing blocks at the same height.
                // Uses Interlocked.CompareExchange so only the first block at a given height proceeds.
                var currentAccepted = Interlocked.Read(ref _acceptedHeight);
                if (nextBlock.Height <= currentAccepted)
                    return; // Another block at this height is already being processed or was accepted

                // Try to claim this height — only one thread wins
                var prev = Interlocked.CompareExchange(ref _acceptedHeight, nextBlock.Height, currentAccepted);
                if (prev != currentAccepted)
                    return; // Lost the race — another thread claimed it

                // Consensus gate: verify this block's validator matches what consensus determined.
                // If CasterRoundDict has a consensus entry for this height, the block's validator must match.
                // DESYNC-FIX: Bypass the consensus gate if we've been stuck for too long (desync recovery).
                var timeSinceLastBlock = Environment.TickCount64 - Interlocked.Read(ref _lastBlockAcceptedTick);
                bool desyncRecoveryMode = timeSinceLastBlock > DESYNC_RECOVERY_TIMEOUT_MS;

                if (!desyncRecoveryMode && Globals.CasterRoundDict.TryGetValue(nextBlock.Height, out var consensusRound))
                {
                    if (!string.IsNullOrEmpty(consensusRound?.Validator) && nextBlock.Validator != consensusRound.Validator)
                    {
                        // Throttled rejection log — only log once per validator per height
                        var logKey = $"{nextBlock.Validator}";
                        _rejectionLogTracker.TryGetValue(logKey, out var lastLoggedHeight);
                        if (lastLoggedHeight < nextBlock.Height)
                        {
                            _rejectionLogTracker[logKey] = nextBlock.Height;
                            ConsoleWriterService.OutputVal($"[Consensus Gate] Block at height {nextBlock.Height} from {nextBlock.Validator} rejected — consensus chose {consensusRound.Validator}");
                        }
                        // Reset _acceptedHeight so the correct block can be processed
                        Interlocked.CompareExchange(ref _acceptedHeight, currentAccepted, nextBlock.Height);
                        return;
                    }
                }
                else if (desyncRecoveryMode)
                {
                    ConsoleWriterService.OutputVal($"[Desync Recovery] Stuck for {timeSinceLastBlock}ms — bypassing consensus gate for block {nextBlock.Height} from {nextBlock.Validator}");
                }

                // Supermajority hash is registered in VerifyBlockHashAgreementAsync as soon as it is known (before local commit).
                // Block casters must not accept message-7 for the next height without that gate (brief spin covers races with local agreement).
                string? agreedHashForGate = null;
                Globals.CasterApprovedBlockHashDict.TryGetValue(nextBlock.Height, out agreedHashForGate);

                if (!desyncRecoveryMode && Globals.IsBlockCaster && nextBlock.Height == lastBlockHeight + 1
                    && string.IsNullOrEmpty(agreedHashForGate))
                {
                    var spin = Stopwatch.StartNew();
                    while (spin.ElapsedMilliseconds < RECEIVE_AGREED_HASH_SPIN_MS && string.IsNullOrEmpty(agreedHashForGate))
                    {
                        if (Globals.CasterApprovedBlockHashDict.TryGetValue(nextBlock.Height, out var h) && !string.IsNullOrEmpty(h))
                        {
                            agreedHashForGate = h;
                            break;
                        }
                        await Task.Delay(10);
                    }
                }

                if (!desyncRecoveryMode && Globals.IsBlockCaster && nextBlock.Height == lastBlockHeight + 1
                    && string.IsNullOrEmpty(agreedHashForGate))
                {
                    ConsoleWriterService.OutputVal($"[Consensus Gate] Rejecting height {nextBlock.Height} — no caster-agreed hash yet (message 7 before agreement or wrong fork).");
                    Interlocked.CompareExchange(ref _acceptedHeight, currentAccepted, nextBlock.Height);
                    return;
                }

                if (!desyncRecoveryMode && !string.IsNullOrEmpty(agreedHashForGate) && nextBlock.Hash != agreedHashForGate)
                {
                    ConsoleWriterService.OutputVal($"[Consensus Gate] Block at height {nextBlock.Height} hash mismatch — expected {agreedHashForGate[..Math.Min(12, agreedHashForGate.Length)]}… got {nextBlock.Hash?[..Math.Min(12, nextBlock.Hash?.Length ?? 0)]}…");
                    Interlocked.CompareExchange(ref _acceptedHeight, currentAccepted, nextBlock.Height);
                    return;
                }

                if (nextBlock.Height != Globals.LastBlock.Height + 1)
                {
                    ConsoleWriterService.OutputVal($"[Consensus Gate] Rejecting height {nextBlock.Height} — next expected is {Globals.LastBlock.Height + 1}.");
                    Interlocked.CompareExchange(ref _acceptedHeight, currentAccepted, nextBlock.Height);
                    return;
                }

                var result = await BlockValidatorService.ValidateBlock(nextBlock, true, false, false, true);
                if (result)
                {
                    // DESYNC-FIX: Update last block accepted timestamp for desync recovery tracking
                    Interlocked.Exchange(ref _lastBlockAcceptedTick, Environment.TickCount64);

                    // FIX 3: Update CasterRoundDict with the COMMITTED block so GetBlockHash
                    // returns the correct hash instead of stale pre-agreement data.
                    if (Globals.CasterRoundDict.TryGetValue(nextBlock.Height, out var committedRound))
                    {
                        var compareRound = committedRound;
                        committedRound.Block = nextBlock;
                        committedRound.Validator = nextBlock.Validator;
                        Globals.CasterRoundDict.TryUpdate(nextBlock.Height, committedRound, compareRound);
                    }

                    // FIX 3b: Reset hash sync failure counter since we successfully committed a block
                    _hashSyncFailHeight = -1;
                    _hashSyncFailCount = 0;

                    if (nextBlock.Height > lastBlockHeight)
                    {
                        _ = P2PValidatorClient.BroadcastBlock(nextBlock, false);
                    }
                }
                else
                {
                    // Validation failed — reset _acceptedHeight so the correct block can try
                    Interlocked.CompareExchange(ref _acceptedHeight, currentAccepted, nextBlock.Height);
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
                                    TransactionData.ReleasePrivateMempoolNullifiersForTx(transaction.Hash);
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

            // CASTER-SYNC-FIX: Startup readiness barrier — ensure all casters are at same height before first round
            await WaitForCasterReadiness();
        }

        /// <summary>
        /// Startup readiness barrier: queries peer casters to ensure a supermajority are online
        /// and at the same block height before allowing consensus to begin.
        /// This prevents a slow-booting caster from generating proofs independently and picking
        /// a different winner than its peers.
        /// </summary>
        private static async Task WaitForCasterReadiness()
        {
            ConsoleWriterService.OutputVal("[ReadinessBarrier] Waiting for peer casters to be ready...");
            var sw = Stopwatch.StartNew();

            while (sw.ElapsedMilliseconds < READINESS_MAX_WAIT_MS)
            {
                // First, sync our height with peers in case we're behind
                await SyncHeightWithPeersAsync();

                var myHeight = Globals.LastBlock.Height;
                var casters = Globals.BlockCasters.ToList();
                if (casters.Count <= 1)
                {
                    ConsoleWriterService.OutputVal("[ReadinessBarrier] Only 1 caster (self); proceeding.");
                    break;
                }

                var requiredReady = Math.Max(2, casters.Count / 2 + 1); // supermajority
                int readyCount = 1; // count ourselves
                int matchingHeightCount = 1; // count ourselves

                var peerTasks = casters
                    .Where(c => !string.IsNullOrEmpty(c.PeerIP) && c.ValidatorAddress != Globals.ValidatorAddress)
                    .Select(async caster =>
                    {
                        try
                        {
                            using var client = Globals.HttpClientFactory.CreateClient();
                            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                            var uri = $"http://{caster.PeerIP!.Replace("::ffff:", "")}:{Globals.ValAPIPort}/valapi/validator/CasterReadyCheck/{myHeight}";
                            var resp = await client.GetAsync(uri, cts.Token);
                            if (resp.IsSuccessStatusCode)
                            {
                                var body = await resp.Content.ReadAsStringAsync();
                                if (!string.IsNullOrEmpty(body) && body != "0")
                                {
                                    var peerStatus = JsonConvert.DeserializeAnonymousType(body, new { Height = 0L, Ready = false, Address = "" });
                                    if (peerStatus != null)
                                        return (ready: peerStatus.Ready, height: peerStatus.Height);
                                }
                            }
                        }
                        catch { }
                        return (ready: false, height: -1L);
                    })
                    .ToList();

                var results = await Task.WhenAll(peerTasks);
                foreach (var r in results)
                {
                    if (r.ready) readyCount++;
                    if (r.height == myHeight) matchingHeightCount++;
                }

                ConsoleWriterService.OutputVal($"[ReadinessBarrier] Ready: {readyCount}/{casters.Count}, Height-matched: {matchingHeightCount}/{casters.Count} (need {requiredReady})");

                if (readyCount >= requiredReady && matchingHeightCount >= requiredReady)
                {
                    ConsoleWriterService.OutputVal("[ReadinessBarrier] Supermajority of casters ready and height-synced. Starting consensus.");
                    break;
                }

                await Task.Delay(READINESS_CHECK_INTERVAL_MS);
            }

            if (sw.ElapsedMilliseconds >= READINESS_MAX_WAIT_MS)
            {
                ConsoleWriterService.OutputVal("[ReadinessBarrier] WARNING: Timed out waiting for peer readiness. Proceeding anyway — peers may be unavailable.");
            }
        }

        /// <summary>
        /// CASTER-CONSENSUS-FIX: Mandatory winner agreement phase.
        /// After each caster independently picks a winner from its proof snapshot,
        /// exchange winner votes with all peer casters. Only proceed if a supermajority
        /// agrees on the same winner. This prevents the boot desync scenario where a slow
        /// caster picks a different winner than its peers.
        /// Returns the agreed winner address, or null if no agreement was reached.
        /// </summary>
        private static async Task<string?> ReachWinnerAgreementAsync(long height, string myChosenWinner)
        {
            var casters = Globals.BlockCasters.ToList();
            if (casters.Count <= 1)
                return myChosenWinner; // Only one caster, no agreement needed

            var requiredAgreement = Math.Max(2, casters.Count / 2 + 1);

            // Store our own vote
            var votesForHeight = Globals.CasterWinnerVoteDict.GetOrAdd(height, _ => new ConcurrentDictionary<string, string>());
            votesForHeight[Globals.ValidatorAddress] = myChosenWinner;

            // Also store in CasterRoundDict so the endpoint can read it
            if (Globals.CasterRoundDict.TryGetValue(height, out var currentRound) && currentRound != null)
            {
                currentRound.Validator = myChosenWinner;
            }

            var sw = Stopwatch.StartNew();
            string? agreedWinner = null;

            while (sw.ElapsedMilliseconds < WINNER_AGREEMENT_TIMEOUT_MS)
            {
                // Exchange votes with all peer casters in parallel
                var peerTasks = casters
                    .Where(c => !string.IsNullOrEmpty(c.PeerIP) && c.ValidatorAddress != Globals.ValidatorAddress)
                    .Select(async caster =>
                    {
                        try
                        {
                            using var client = Globals.HttpClientFactory.CreateClient();
                            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                            var uri = $"http://{caster.PeerIP!.Replace("::ffff:", "")}:{Globals.ValAPIPort}/valapi/validator/ExchangeWinnerVote";
                            var voteReq = new WinnerVoteRequest
                            {
                                BlockHeight = height,
                                VoterAddress = Globals.ValidatorAddress,
                                WinnerAddress = myChosenWinner
                            };
                            using var content = new StringContent(
                                JsonConvert.SerializeObject(voteReq),
                                Encoding.UTF8,
                                "application/json");
                            var resp = await client.PostAsync(uri, content, cts.Token);
                            if (resp.IsSuccessStatusCode)
                            {
                                var body = await resp.Content.ReadAsStringAsync();
                                if (!string.IsNullOrEmpty(body) && body != "0")
                                {
                                    var voteResp = JsonConvert.DeserializeAnonymousType(body, new { BlockHeight = 0L, Votes = new Dictionary<string, string>() });
                                    if (voteResp?.Votes != null)
                                    {
                                        // Merge remote votes into our local dict
                                        foreach (var kv in voteResp.Votes)
                                        {
                                            votesForHeight.TryAdd(kv.Key, kv.Value);
                                        }
                                    }
                                }
                            }
                        }
                        catch { }
                    })
                    .ToList();

                await Task.WhenAll(peerTasks);

                // Check for supermajority agreement
                var voteGroups = votesForHeight.Values
                    .GroupBy(v => v)
                    .OrderByDescending(g => g.Count())
                    .ToList();

                if (voteGroups.Any())
                {
                    var best = voteGroups.First();
                    CasterLogUtility.Log($"WinnerAgreement: votes={votesForHeight.Count}/{casters.Count}, best={best.Key} count={best.Count()}, need={requiredAgreement}", "AGREEMENT");

                    if (best.Count() >= requiredAgreement)
                    {
                        agreedWinner = best.Key;
                        CasterLogUtility.Log($"WinnerAgreement: AGREED on {agreedWinner} ({best.Count()}/{casters.Count})", "AGREEMENT");
                        break;
                    }
                }

                await Task.Delay(500);
            }

            sw.Stop();

            if (agreedWinner == null)
            {
                CasterLogUtility.Log($"WinnerAgreement: FAILED after {sw.ElapsedMilliseconds}ms. Votes: {string.Join(", ", votesForHeight.Select(kv => $"{kv.Key[..Math.Min(8, kv.Key.Length)]}→{kv.Value[..Math.Min(8, kv.Value.Length)]}"))}", "AGREEMENT");
            }

            // Cleanup old vote entries
            var oldKeys = Globals.CasterWinnerVoteDict.Keys.Where(k => k < height - 10).ToList();
            foreach (var k in oldKeys)
                Globals.CasterWinnerVoteDict.TryRemove(k, out _);

            return agreedWinner;
        }

        /// <summary>
        /// Publishes the supermajority-agreed block hash as soon as it is known so <see cref="ReceiveConfirmedBlock"/>
        /// can reject mismatched message-7 traffic before local commit completes. Cleared when agreement fails or a new attempt starts.
        /// </summary>
        private static void RegisterPendingCasterBlockHash(long height, string hash)
        {
            if (string.IsNullOrEmpty(hash)) return;
            while (true)
            {
                if (Globals.CasterApprovedBlockHashDict.TryGetValue(height, out var cur))
                {
                    if (cur == hash) break;
                    if (Globals.CasterApprovedBlockHashDict.TryUpdate(height, hash, cur)) break;
                }
                else if (Globals.CasterApprovedBlockHashDict.TryAdd(height, hash))
                    break;
            }
        }

        private static void ClearPendingCasterBlockHash(long height)
        {
            Globals.CasterApprovedBlockHashDict.TryRemove(height, out _);
        }

        /// <summary>
        /// Persists agreed hash for <see cref="ReceiveConfirmedBlock"/> validation, updates <see cref="Globals.CasterRoundDict"/>,
        /// then applies the same chain/broadcast sequencing as the pre-fix paths (winner vs peer-fetch layouts differ).
        /// Must run only after successful multi-caster hash agreement.
        /// </summary>
        private static async Task CommitCasterBlockPostAgreementAsync(Block block, string terminalWinner, string producerIp, bool winnerCraftedLayout)
        {
            if (block == null || string.IsNullOrEmpty(producerIp))
                return;

            while (true)
            {
                if (Globals.CasterApprovedBlockHashDict.TryGetValue(block.Height, out var cur))
                {
                    if (cur == block.Hash)
                        break;
                    if (Globals.CasterApprovedBlockHashDict.TryUpdate(block.Height, block.Hash, cur))
                        break;
                }
                else if (Globals.CasterApprovedBlockHashDict.TryAdd(block.Height, block.Hash))
                    break;
            }

            if (Globals.CasterRoundDict.TryGetValue(block.Height, out var round))
            {
                var compareRound = round;
                round.Block = block;
                round.Validator = terminalWinner;
                Globals.CasterRoundDict.TryUpdate(block.Height, round, compareRound);
            }

            var nextHeight = Globals.LastBlock.Height + 1;
            var currentHeight = block.Height;

            if (!BlockDownloadService.BlockDict.ContainsKey(currentHeight))
            {
                BlockDownloadService.BlockDict.AddOrUpdate(
                    currentHeight,
                    new List<(Block, string)> { (block, producerIp) },
                    (key, existingList) =>
                    {
                        existingList.Add((block, producerIp));
                        return existingList;
                    });
                if (nextHeight == currentHeight)
                    await BlockValidatorService.ValidateBlocks();
                if (nextHeight < currentHeight)
                    await BlockDownloadService.GetAllBlocks();
            }

            if (winnerCraftedLayout)
            {
                if (currentHeight < nextHeight)
                {
                    if (Globals.LastBlock.Height < block.Height)
                        await BlockValidatorService.ValidateBlocks();

                    if (nextHeight == currentHeight)
                    {
                        CasterRoundAudit?.AddStep($"Block found. Broadcasting.", true);
                        await BroadcastConsensusBlockAsync(block, terminalWinner);
                    }

                    if (nextHeight < currentHeight)
                        await BlockDownloadService.GetAllBlocks();
                }
            }
            else
            {
                if (nextHeight == currentHeight)
                    await BroadcastConsensusBlockAsync(block, terminalWinner);
            }

            _consecutiveBlockHashAgreementFailures = 0;
            _consecutiveMajorityBlockFetchFailures = 0;
            Interlocked.Exchange(ref _casterConsensusHalted, 0);
        }

        private static async Task OnBlockHashAgreementRoundRejectedAsync(long height, string? terminalWinner)
        {
            ClearPendingCasterBlockHash(height);
            _consecutiveBlockHashAgreementFailures++;
            CasterLogUtility.Log($"BlockHashAgreement: round rejected at height {height} (streak {_consecutiveBlockHashAgreementFailures}).", "AGREEMENT");

            if (Globals.LastBlock.Height >= height)
            {
                CasterLogUtility.Log($"BlockHashAgreement: safety-net — tip already at ≥{height}; reconciling with peers.", "AGREEMENT");
                await SyncBlockHashWithPeersAsync();
                await SyncHeightWithPeersAsync();
                try { await BlockDownloadService.GetAllBlocks(); } catch { }
            }

            if (_consecutiveBlockHashAgreementFailures >= BLOCK_HASH_AGREEMENT_RECONCILE_THRESHOLD)
            {
                _consecutiveBlockHashAgreementFailures = 0;
                CasterRoundAudit?.AddStep($"[BlockHashAgreement] {BLOCK_HASH_AGREEMENT_RECONCILE_THRESHOLD}+ consecutive failures — forced chain reconciliation.", false);
                await SyncBlockHashWithPeersAsync();
                await SyncHeightWithPeersAsync();
                try { await BlockDownloadService.GetAllBlocks(); } catch { }
            }
        }

        private static async Task HaltConsensusForFetchFailuresAndReconcileAsync()
        {
            if (Interlocked.Exchange(ref _casterConsensusHalted, 1) != 0)
                return;
            CasterRoundAudit?.AddStep($"[BlockFetch] Majority block fetch failures ≥{BLOCK_FETCH_FAIL_HALT_THRESHOLD} — halting caster commit path and reconciling.", false);
            ConsoleWriterService.OutputVal($"[CasterConsensus] Halted due to repeated majority block fetch failures; syncing with peers.");
            await SyncBlockHashWithPeersAsync();
            await SyncHeightWithPeersAsync();
            try { await BlockDownloadService.GetAllBlocks(); } catch { }
            // Leave _casterConsensusHalted == 1 until the next casting round clears it after sync (see StartCastingRounds).
        }

        /// <summary>
        /// Block hash agreement phase: after fetching/crafting a block, exchange hashes with peer casters
        /// to ensure all casters have the SAME block before committing.
        /// Returns the agreed-upon block, or null if agreement could not be reached.
        /// </summary>
        private static async Task<Block?> VerifyBlockHashAgreementAsync(Block block, long height, string terminalWinner)
        {
            var casters = Globals.BlockCasters.ToList();
            if (casters.Count <= 1)
            {
                // Single caster: still publish expected hash before commit so ReceiveConfirmedBlock cannot accept a different next block first.
                if (block != null && !string.IsNullOrEmpty(block.Hash))
                    RegisterPendingCasterBlockHash(height, block.Hash);
                return block;
            }

            // New multi-caster attempt for this height — drop any stale pending hash from a prior failed round.
            ClearPendingCasterBlockHash(height);

            var requiredAgreement = Math.Max(2, casters.Count / 2 + 1);
            var myHash = block.Hash;
            int agreementCount = 1; // count ourselves
            string? majorityHash = null;
            var hashVotes = new Dictionary<string, int> { { myHash, 1 } };
            var hashToSource = new Dictionary<string, string>(); // hash -> peer IP for fetching

            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < BLOCK_HASH_AGREEMENT_TIMEOUT_MS)
            {
                var peerTasks = casters
                    .Where(c => !string.IsNullOrEmpty(c.PeerIP) && c.ValidatorAddress != Globals.ValidatorAddress)
                    .Select(async caster =>
                    {
                        try
                        {
                            using var client = Globals.HttpClientFactory.CreateClient();
                            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                            var uri = $"http://{caster.PeerIP!.Replace("::ffff:", "")}:{Globals.ValAPIPort}/valapi/validator/GetBlockHash/{height}";
                            var resp = await client.GetAsync(uri, cts.Token);
                            if (resp.IsSuccessStatusCode)
                            {
                                var body = await resp.Content.ReadAsStringAsync();
                                if (!string.IsNullOrEmpty(body) && body != "0")
                                {
                                    var peerResult = JsonConvert.DeserializeAnonymousType(body, new { Hash = "", Validator = "", Height = 0L });
                                    if (peerResult != null && !string.IsNullOrEmpty(peerResult.Hash))
                                        return (hash: peerResult.Hash, ip: caster.PeerIP!);
                                }
                            }
                        }
                        catch { }
                        return (hash: (string?)null, ip: "");
                    })
                    .ToList();

                var results = await Task.WhenAll(peerTasks);
                hashVotes.Clear();
                hashVotes[myHash] = 1;
                hashToSource.Clear();

                foreach (var r in results)
                {
                    if (r.hash != null)
                    {
                        if (!hashVotes.ContainsKey(r.hash))
                            hashVotes[r.hash] = 0;
                        hashVotes[r.hash]++;
                        if (!hashToSource.ContainsKey(r.hash))
                            hashToSource[r.hash] = r.ip;
                    }
                }

                // Check for agreement
                var best = hashVotes.OrderByDescending(kv => kv.Value).First();
                if (best.Value >= requiredAgreement)
                {
                    majorityHash = best.Key;
                    agreementCount = best.Value;
                    // Publish immediately so message-7 handlers can enforce this hash before local commit runs.
                    RegisterPendingCasterBlockHash(height, majorityHash);
                    break;
                }

                await Task.Delay(500);
            }

            if (majorityHash == null)
            {
                CasterRoundAudit?.AddStep($"[BlockHashAgreement] No supermajority hash agreement reached for height {height}. REJECTING round.", false);
                CasterLogUtility.Log($"BlockHashAgreement: FAILED — no supermajority. Votes: {string.Join(", ", hashVotes.Select(kv => $"{kv.Key[..Math.Min(8, kv.Key.Length)]}={kv.Value}"))}", "AGREEMENT");
                // CASTER-CONSENSUS-FIX: Return null to force a round retry instead of committing a potentially divergent block
                return null;
            }

            CasterRoundAudit?.AddStep($"[BlockHashAgreement] Agreement: {agreementCount}/{casters.Count} on hash {majorityHash[..Math.Min(8, majorityHash.Length)]}…", true);

            // If our hash matches, we're good
            if (myHash == majorityHash)
            {
                _consecutiveMajorityBlockFetchFailures = 0;
                return block;
            }

            // Our hash differs from majority — fetch the correct block from a peer that has it
            ConsoleWriterService.OutputVal($"[BlockHashAgreement] Our block hash differs from majority. Fetching correct block from peer.");
            if (hashToSource.TryGetValue(majorityHash, out var sourceIP))
            {
                var peerBlock = await CasterBlockFetch.TryFetchBlockAsync(
                    new Peers { PeerIP = sourceIP },
                    height,
                    terminalWinner);
                if (peerBlock != null && peerBlock.Hash == majorityHash)
                {
                    CasterRoundAudit?.AddStep($"[BlockHashAgreement] Replaced local block with majority block from {sourceIP}.", true);
                    _consecutiveMajorityBlockFetchFailures = 0;
                    return peerBlock;
                }
            }

            _consecutiveMajorityBlockFetchFailures++;
            CasterRoundAudit?.AddStep($"[BlockHashAgreement] Could not fetch majority block — rejecting round (fetch streak {_consecutiveMajorityBlockFetchFailures}).", false);
            CasterLogUtility.Log($"BlockHashAgreement: majority fetch FAILED streak={_consecutiveMajorityBlockFetchFailures}", "AGREEMENT");
            if (_consecutiveMajorityBlockFetchFailures >= BLOCK_FETCH_FAIL_HALT_THRESHOLD)
                await HaltConsensusForFetchFailuresAndReconcileAsync();
            ClearPendingCasterBlockHash(height);
            return null;
        }

        #endregion

        #region Get Winning Proof Backup Method
        public static async Task GetWinningProof(Proof proof)
        {
            var validators = Globals.BlockCasters.ToList();

            if (!validators.Any())
                return;

            try
            {
                // Parallel fetch from all casters simultaneously
                var tasks = validators.Select(async validator =>
                {
                    try
                    {
                        using var client = Globals.HttpClientFactory.CreateClient();
                        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(CASTER_VOTE_WINDOW));
                        var uri = $"http://{validator.PeerIP.Replace("::ffff:", "")}:{Globals.ValAPIPort}/valapi/validator/SendWinningProof/{proof.BlockHeight}";
                        var response = await client.GetAsync(uri, cts.Token);
                        if (response.IsSuccessStatusCode)
                        {
                            var responseJson = await response.Content.ReadAsStringAsync();
                            if (responseJson != null && responseJson != "0")
                            {
                                var remoteCasterProof = JsonConvert.DeserializeObject<Proof>(responseJson);
                                if (remoteCasterProof != null && remoteCasterProof.VerifyProof())
                                {
                                    Globals.CasterProofDict.TryAdd(validator.PeerIP, remoteCasterProof);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        if (Globals.OptionalLogging)
                            ErrorLogUtility.LogError($"Error getting proof from {validator.PeerIP}: {ex.Message}", "BlockcasterNode.GetWinningProof()");
                    }
                });

                await Task.WhenAll(tasks);
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error in proof collection: {ex.Message}", "BlockcasterNode.GetWinningProof()");
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
