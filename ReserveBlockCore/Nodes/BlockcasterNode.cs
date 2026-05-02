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
        const int PROOF_COLLECTION_TIME = 6000; // 6 seconds — sync barrier for caster proof exchange (was 3s; increased to handle newly-joined caster timing skew)
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

    // Dynamic reference points for block delay calculation — epoch-based (reset every N blocks)
    private static long ReferenceHeight = -1;
    private static long ReferenceTime = -1;
    const int EPOCH_SIZE = 10; // Reset timing reference every N blocks; allows drift correction to accumulate

    /// <summary>Tracks BlockCasters.Count from the previous round to detect when a new caster joins.</summary>
    private static int _previousCasterCount = -1;

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

    /// <summary>DETERMINISTIC-CONSENSUS: Tracks consecutive winner agreement failures at the same height for deadlock safety net.</summary>
    private static long _winnerAgreementFailHeight = -1;
    private static int _winnerAgreementFailCount = 0;
    const int WINNER_AGREEMENT_DEADLOCK_THRESHOLD = 5; // After this many failures at same height, use deterministic tiebreaker

    /// <summary>
    /// CONSENSUS-V2 (Fix #6): Size-tiered deadlock thresholds. With only 2 casters,
    /// supermajority requires both to agree — there is zero tolerance for a single
    /// network hiccup. Waiting 5 rounds before tiebreaking on a 2-caster bootstrap
    /// can stall block production for 30+ seconds. Tier the threshold by pool size:
    ///   2 casters → 2 fails before tiebreak (≈10s stall ceiling)
    ///   3-4 casters → 3 fails (still tight quorum)
    ///   5+ casters → original 5 fails (resilient, prefer organic agreement)
    /// </summary>
    internal static int GetWinnerAgreementDeadlockThreshold(int casterCount)
    {
        if (casterCount <= 2) return 2;
        if (casterCount <= 4) return 3;
        return WINNER_AGREEMENT_DEADLOCK_THRESHOLD;
    }

    /// <summary>
    /// CONSENSUS-V2 (Fix #6): Same size-tiered scaling for forced chain reconciliation
    /// after consecutive block-hash agreement failures. Bootstrap pools should reconcile
    /// faster (any disagreement at 2 casters is total disagreement).
    /// </summary>
    internal static int GetBlockHashAgreementReconcileThreshold(int casterCount)
    {
        if (casterCount <= 2) return 2;
        return BLOCK_HASH_AGREEMENT_RECONCILE_THRESHOLD;
    }

    /// <summary>DETERMINISTIC-CONSENSUS: Tracks last height at which validator list sync was performed.</summary>
    private static long _lastValidatorListSyncHeight = 0;
    /// <summary>
    /// CONSENSUS-V2 (Fix #4): Tightened from 50 → 10 blocks. With 30+ validators we can't afford to
    /// wait ~5 minutes for two casters' NetworkValidators sets to converge. The sync is cheap
    /// (one tiny POST per peer) so amortized cost is negligible at 10-block cadence.
    /// </summary>
    const int VALIDATOR_LIST_SYNC_INTERVAL = 10;


    /// <summary>Consecutive rounds where multi-caster block hash agreement failed (no commit).</summary>
    private static int _consecutiveBlockHashAgreementFailures;
    const int BLOCK_HASH_AGREEMENT_RECONCILE_THRESHOLD = 3;

    /// <summary>Consecutive failures to fetch the majority block during hash agreement (divergence risk).</summary>
    private static int _consecutiveMajorityBlockFetchFailures;
    const int BLOCK_FETCH_FAIL_HALT_THRESHOLD = 5;
    private static int _casterConsensusHalted; // 0 = running, 1 = halt until reconciliation

    /// <summary>Tracks consecutive block-fetch failures per winner address at a given height.
    /// After WINNER_SKIP_THRESHOLD consecutive failures for the same winner at the same height,
    /// that winner is excluded from proof sorting so the next VRF candidate can be tried.</summary>
    private static readonly ConcurrentDictionary<string, (long height, int failCount)> _winnerFetchFailures = new();
    const int WINNER_SKIP_THRESHOLD = 3; // Skip winner after this many consecutive block-fetch failures at same height

    /// <summary>FIX D2: Tracks cumulative winner verification failures across rounds.
    /// (totalFails, excludeUntilHeight). Escalating exclusion: 3-5→10 blocks, 6-9→50 blocks, 10+→evict from NetworkValidators.</summary>
    private static readonly ConcurrentDictionary<string, (int totalFails, long excludeUntilHeight)> _winnerCumulativeFailures = new();
    /// <summary>FIX D1: Whether fresh startup detection has run (once per process lifetime).</summary>
    private static bool _freshStartupChecked = false;
    const long FRESH_STARTUP_THRESHOLD_SECONDS = 300; // 5 minutes

    /// <summary>FORK-RECOVERY: Tracks consecutive rounds where lastBlock height did not advance.
    /// When this exceeds FORK_RECOVERY_THRESHOLD and hash sync has already failed, triggers
    /// automatic rollback + resync to heal a node stuck on a minority-fork block.</summary>
    private static long _forkStuckHeight = -1;
    private static int _forkStuckRounds = 0;
    private static int _forkRecoveryInProgress; // 0 = idle, 1 = running (interlocked)
    const int FORK_RECOVERY_THRESHOLD = 5; // After this many stuck rounds, trigger self-healing

    /// <summary>FIX D3: Clears all winner failure tracking for a given address.
    /// Called when a heartbeat TX is detected, indicating the validator restarted.</summary>
    public static void ClearWinnerFailures(string address)
    {
        if (string.IsNullOrEmpty(address)) return;
        _winnerFetchFailures.TryRemove(address, out _);
        _winnerCumulativeFailures.TryRemove(address, out _);
        CasterLogUtility.Log($"ClearWinnerFailures: cleared all failure tracking for {address} (heartbeat/block success)", "PROOFS");
    }


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
                    CasterLogUtility.Log(
                        $"MonitorTick BlockCasters empty → calling GetBlockcasters(). Self={Globals.ValidatorAddress} IsBlockCaster={Globals.IsBlockCaster}",
                        "CasterFlow");
                    await ValidatorNode.GetBlockcasters();
                    await delay;
                    continue;
                }

                // Per-tick snapshot so we can always see the caster pool state the monitor
                // is reasoning about. Critical for diagnosing "stuck as validator, never promoted"
                // and "promoted but never solving blocks" scenarios.
                {
                    var addrs = string.Join(",", Globals.BlockCasters.Select(c => c.ValidatorAddress ?? "?"));
                    var selfInList = Globals.BlockCasters.Any(c => c.ValidatorAddress == Globals.ValidatorAddress);
                    CasterLogUtility.Log(
                        $"MonitorTick | Self={Globals.ValidatorAddress} | IsBlockCaster={Globals.IsBlockCaster} | " +
                        $"self-in-BlockCasters={selfInList} | BlockCasters.Count={Globals.BlockCasters.Count} addrs=[{addrs}] | " +
                        $"NetworkValidators.Count={Globals.NetworkValidators.Count} | IsBootstrapMode={Globals.IsBootstrapMode} | height={Globals.LastBlock.Height}",
                        "CasterFlow");
                }

                if (!Globals.IsBlockCaster)
                {
                    // FIX 4: Self-recovery heartbeat — poll a peer caster's /GetCasters endpoint.
                    // If our address appears in their caster list, we were promoted but missed the notification.
                    // Flip IsBlockCaster=true and merge the caster list so we can begin consensus.
                    var selfRecovered = false;
                    try
                    {
                        var peers = Globals.BlockCasters.ToList()
                            .Where(c => !string.IsNullOrEmpty(c.PeerIP) && c.ValidatorAddress != Globals.ValidatorAddress)
                            .Take(2)
                            .ToList();
                        if (peers.Any())
                        {
                            using var hc = Globals.HttpClientFactory.CreateClient();
                            hc.Timeout = TimeSpan.FromSeconds(5);
                            foreach (var peer in peers)
                            {
                                try
                                {
                                    var ip = peer.PeerIP!.Replace("::ffff:", "");
                                    var url = $"http://{ip}:{Globals.ValAPIPort}/valapi/validator/GetCasters";
                                    var resp = await hc.GetStringAsync(url);
                                    if (!string.IsNullOrEmpty(resp) && resp.Contains(Globals.ValidatorAddress!))
                                    {
                                        CasterLogUtility.Log(
                                            $"SelfRecovery: peer {ip} reports us in caster list — flipping IsBlockCaster=true",
                                            "CasterFlow");
                                        Globals.IsBlockCaster = true;
                                        selfRecovered = true;
                                        // Merge peer's caster list into ours
                                        var parsed = Newtonsoft.Json.JsonConvert.DeserializeObject<dynamic>(resp);
                                        if (parsed?.Casters != null)
                                        {
                                            foreach (var c in parsed.Casters)
                                            {
                                                string addr = c.Address?.ToString() ?? "";
                                                string pip = c.PeerIP?.ToString() ?? "";
                                                string pk = c.PublicKey?.ToString() ?? "";
                                                if (!string.IsNullOrEmpty(addr) && !Globals.BlockCasters.Any(x => x.ValidatorAddress == addr))
                                                {
                                                    Globals.BlockCasters.Add(new Peers { ValidatorAddress = addr, PeerIP = pip, ValidatorPublicKey = pk });
                                                }
                                            }
                                        }
                                        // FIX: Hydrate NetworkValidator entries for ALL casters (including self)
                                        // after SelfRecovery merges the caster list. Without this, the newly-
                                        // promoted node's own entry (and any other freshly-discovered casters)
                                        // may have IsFullyTrusted=false or LastSeen=0 in NetworkValidators,
                                        // causing GenerateProofsFromNetworkValidatorsLegacy() to filter them
                                        // out of allProofs — preventing them from ever winning blocks.
                                        var srNow = TimeUtil.GetTime();
                                        foreach (var caster in Globals.BlockCasters.ToList())
                                        {
                                            if (string.IsNullOrEmpty(caster.ValidatorAddress))
                                                continue;
                                            if (Globals.NetworkValidators.TryGetValue(caster.ValidatorAddress, out var nv))
                                            {
                                                nv.IsFullyTrusted = true;
                                                nv.LastSeen = srNow;
                                                nv.CheckFailCount = 0;
                                                Globals.NetworkValidators[caster.ValidatorAddress] = nv;
                                            }
                                            else if (!string.IsNullOrEmpty(caster.PeerIP))
                                            {
                                                Globals.NetworkValidators[caster.ValidatorAddress] = new Models.NetworkValidator
                                                {
                                                    Address = caster.ValidatorAddress,
                                                    IPAddress = (caster.PeerIP ?? "").Replace("::ffff:", ""),
                                                    PublicKey = caster.ValidatorPublicKey ?? "",
                                                    IsFullyTrusted = true,
                                                    LastSeen = srNow,
                                                    CheckFailCount = 0,
                                                    FirstSeenAtHeight = Globals.LastBlock?.Height ?? 0,
                                                    FirstAdvertised = srNow,
                                                };
                                            }
                                        }
                                        Utilities.ProofUtility.ClearProofGenerationCache();
                                        CasterLogUtility.Log(
                                            $"SelfRecovery: hydrated {Globals.BlockCasters.Count} casters in NetworkValidators and cleared proof cache",
                                            "CasterFlow");

                                        break;
                                    }
                                }
                                catch { /* peer unreachable, try next */ }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        CasterLogUtility.Log($"SelfRecovery heartbeat error: {ex.Message}", "CasterFlow");
                    }

                    if (!selfRecovered)
                    {
                        CasterLogUtility.Log(
                            $"MonitorTick SKIP promotion-related work — IsBlockCaster=false. Will retry in 10s.",
                            "CasterFlow");
                        await Task.Delay(new TimeSpan(0, 0, 10));
                        continue;
                    }
                    // If self-recovered, fall through to normal caster work
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
        /// <summary>Throttle the "PingCasters start/end" summary logs to roughly one per 30s so we don't flood the log.</summary>
        private static long _lastPingCastersLogTick;

        public static async Task PingCasters()
        {
            var casterList = Globals.BlockCasters.ToList();

            if (!casterList.Any())
                return;

            var shouldLog = (Environment.TickCount64 - _lastPingCastersLogTick) > 30_000;
            if (shouldLog)
            {
                _lastPingCastersLogTick = Environment.TickCount64;
                CasterLogUtility.Log(
                    $"PingCasters start — {casterList.Count} peers in list (self skipped). " +
                    $"Self={Globals.ValidatorAddress} addrs=[{string.Join(",", casterList.Select(c => c.ValidatorAddress ?? "?"))}]",
                    "CasterFlow");
            }

            HashSet<string> removeList = new HashSet<string>();


            foreach (var caster in casterList)
            {
                // Never ping ourselves. The loopback from our external PeerIP often fails
                // due to NAT hairpin being disabled on home/NAT'd setups, which would
                // falsely evict this node from its own BlockCasters list immediately after
                // promotion. That flip-flops Globals.IsBlockCaster back to false in
                // StartCastingRounds and breaks consensus participation until restart.
                if (!string.IsNullOrEmpty(caster.ValidatorAddress)
                    && caster.ValidatorAddress == Globals.ValidatorAddress)
                    continue;

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

                CasterLogUtility.Log(
                    $"PingCasters EVICTED {removeList.Count} offline peer(s): [{string.Join(",", removeList)}]. " +
                    $"BlockCasters.Count now {Globals.BlockCasters.Count}",
                    "CasterFlow");

                foreach (var removedIP in removeList)
                {
                    var removedCaster = casterList.FirstOrDefault(c => c.PeerIP == removedIP);
                    var removedAddr = removedCaster?.ValidatorAddress ?? "unknown";
                    ConsoleWriterService.OutputValCaster($"[PingCasters] Removed offline caster {removedAddr} ({removedIP})");
                    _ = CasterDiscoveryService.OnCasterRemoved(removedAddr);
                }
            }
            else if (shouldLog)
            {
                CasterLogUtility.Log($"PingCasters end — no evictions", "CasterFlow");
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
                        ConsoleWriterService.OutputValCaster("No available validators to select from.");
                        break;
                    }

                    int index = random.Next(availableValidators.Count);
                    var nCaster =  availableValidators[index];

                    if(nCaster == null)
                    {
                        ConsoleWriterService.OutputValCaster("Peers available, but nCaster was null.");
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
                            ConsoleWriterService.OutputValCaster($"[InitiateReplacement] Candidate {nCaster.Address} failed version check. Skipping.");
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
            // ROUND-SYNC-FIX: Compute initial BlockDelay from last block timestamp instead of Task.CompletedTask.
            // Using CompletedTask caused the new caster's first round to fire ~1.5s after join while bootstrap
            // casters were still mid-round with full block-time delays, creating a permanent phase offset.
            var BlockDelay = ComputeInitialBlockDelay();
            ConsoleWriterService.OutputValCaster("Booting up consensus loop");
            CasterLogUtility.Clear();
            CasterLogUtility.Log($"=== Consensus loop starting. Validator={Globals.ValidatorAddress} ===", "BOOT");

            // FIX D1: Fresh startup detection — if the last block is older than 5 minutes,
            // clear NetworkValidators so bootstrap casters start with only themselves.
            // New validators will populate as they connect via P2P.
            if (!_freshStartupChecked)
            {
                _freshStartupChecked = true;
                var lastBlockAge = TimeUtil.GetTime() - Globals.LastBlock.Timestamp;
                if (lastBlockAge > FRESH_STARTUP_THRESHOLD_SECONDS)
                {
                    var staleCount = Globals.NetworkValidators.Count;
                    Globals.NetworkValidators.Clear();
                    CasterLogUtility.Log(
                        $"FRESH-STARTUP: Last block is {lastBlockAge}s old (>{FRESH_STARTUP_THRESHOLD_SECONDS}s). " +
                        $"Cleared {staleCount} stale NetworkValidators. Validators will repopulate via P2P.",
                        "BOOT");
                    ConsoleWriterService.OutputValCaster(
                        $"[FRESH-STARTUP] Cleared {staleCount} stale validators. Last block age: {lastBlockAge}s.");

                    // FIX D1b: Re-seed bootstrap casters (including self) into NetworkValidators
                    // so they can generate proofs for themselves and be selected as winners.
                    //
                    // CRITICAL: Must populate PublicKey from Peers.ValidatorPublicKey. The legacy proof
                    // generation path (GenerateProofsFromNetworkValidatorsLegacy) requires PublicKey to be
                    // non-null — without it the foreach skips the entry, allProofs comes back empty, and
                    // the consensus loop falls through silently with no winner candidate ever produced.
                    // FirstSeenAtHeight is set to 0 so genesis-trusted bootstrap casters bypass any
                    // height-based maturity gate that compares against `LastBlock.Height`.
                    int reseedSkippedNoPubKey = 0;
                    foreach (var caster in Globals.BlockCasters.ToList())
                    {
                        if (!string.IsNullOrEmpty(caster.ValidatorAddress) && !string.IsNullOrEmpty(caster.PeerIP))
                        {
                            if (string.IsNullOrEmpty(caster.ValidatorPublicKey))
                            {
                                reseedSkippedNoPubKey++;
                                CasterLogUtility.Log(
                                    $"FRESH-STARTUP: skip re-seed for {caster.ValidatorAddress} @ {caster.PeerIP} — Peers entry has no ValidatorPublicKey",
                                    "BOOT");
                                continue;
                            }

                            var nv = new NetworkValidator
                            {
                                Address = caster.ValidatorAddress,
                                PublicKey = caster.ValidatorPublicKey,
                                IPAddress = caster.PeerIP,
                                IsFullyTrusted = true,
                                LastSeen = TimeUtil.GetTime(),
                                FirstSeenAtHeight = 0,
                                CheckFailCount = 0,
                            };
                            Globals.NetworkValidators.TryAdd(caster.ValidatorAddress, nv);
                        }
                    }
                    // FIX E: Set gossip cooldown to prevent P2P gossip from re-adding stale validators
                    Globals.GossipCooldownUntil = TimeUtil.GetTime() + 60;
                    CasterLogUtility.Log(
                        $"FRESH-STARTUP: Re-seeded {Globals.NetworkValidators.Count} bootstrap casters into NetworkValidators. Gossip cooldown until {Globals.GossipCooldownUntil}.",
                        "BOOT");
                }
                else
                {
                    CasterLogUtility.Log(
                        $"FRESH-STARTUP: Last block is {lastBlockAge}s old (<={FRESH_STARTUP_THRESHOLD_SECONDS}s). " +
                        $"Keeping {Globals.NetworkValidators.Count} NetworkValidators.",
                        "BOOT");
                }
            }

            while (true && !string.IsNullOrEmpty(Globals.ValidatorAddress))
            {
                if (!Globals.BlockCasters.Any())
                {
                    await ValidatorNode.GetBlockcasters();
                    await delay;
                    continue;
                }

                var casterList = Globals.BlockCasters.ToList();
                var wasCaster = Globals.IsBlockCaster;
                var selfInList = casterList.Exists(x => x.ValidatorAddress == Globals.ValidatorAddress);
                Globals.IsBlockCaster = selfInList;

                if (wasCaster && !selfInList)
                {
                    // Loud diagnostic: something evicted us from our own BlockCasters between
                    // loop iterations. Historically this was PingCasters failing the self-loopback
                    // HTTP on nodes behind NAT, which has since been fixed to skip self, but keeping
                    // this log makes future regressions immediately obvious.
                    CasterLogUtility.Log(
                        $"IsBlockCaster flipped FALSE — self ({Globals.ValidatorAddress}) missing from BlockCasters. " +
                        $"Count={casterList.Count} Addresses=[{string.Join(",", casterList.Select(c => c.ValidatorAddress))}]",
                        "SELF-DEMOTE");
                    ConsoleWriterService.OutputValCaster(
                        $"[SELF-DEMOTE] This node was unexpectedly removed from its own BlockCasters list. " +
                        $"Count={casterList.Count}. Will re-evaluate next cycle.");
                }

                if (!Globals.IsBlockCaster)
                {
                    // Throttled diagnostic: this is the path validators take while they're waiting
                    // to be promoted by a caster. Logging each 30s tick makes it obvious whether:
                    //   (a) the node is being skipped entirely (wrong ValidatorAddress / not fully trusted)
                    //   (b) the caster pool is full (no slots for promotion)
                    //   (c) the promotion request came in but was rejected (see HandlePromotion logs)
                    var addrs = string.Join(",", casterList.Select(c => c.ValidatorAddress ?? "?"));
                    CasterLogUtility.Log(
                        $"ConsensusLoop WAITING (not a caster). Self={Globals.ValidatorAddress} " +
                        $"self-in-BlockCasters=false | BlockCasters.Count={casterList.Count} addrs=[{addrs}] | " +
                        $"NetworkValidators.Count={Globals.NetworkValidators.Count} | Retrying in 10s…",
                        "CasterFlow");
                    await Task.Delay(new TimeSpan(0, 0, 10));
                    continue;
                }

                ConsoleWriterService.OutputValCaster("Top of consensus loop");
                CasterLogUtility.Log(
                    $"ConsensusLoop RUNNING as caster. Self={Globals.ValidatorAddress} " +
                    $"BlockCasters.Count={casterList.Count} height={Globals.LastBlock.Height}",
                    "CasterFlow");

                // ROUND-SYNC-FIX: Detect when caster pool changes (new caster joined or one was evicted).
                // When this happens, re-sync height with peers and reset timing references so all casters
                // start the next round from a common baseline.
                if (_previousCasterCount != -1 && _previousCasterCount != casterList.Count)
                {
                    CasterLogUtility.Log(
                        $"CASTER-POOL-CHANGE: count {_previousCasterCount}→{casterList.Count}. Re-syncing height and resetting timing.",
                        "ROUND-SYNC");
                    await SyncHeightWithPeersAsync();
                    ResetRoundTiming(Globals.LastBlock.Height, force: true);
                }
                _previousCasterCount = casterList.Count;


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
                    CasterLogUtility.Log($"--- ROUND START height={Height} lastBlock={Globals.LastBlock.Height} lastHash={Globals.LastBlock.Hash?[..Math.Min(16, Globals.LastBlock.Hash?.Length ?? 0)]} refH={ReferenceHeight} refT={ReferenceTime} casters={casterList.Count} ---", "ROUND");


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
                        await Task.WhenAll(BlockDelay, Task.Delay(1000));
                        
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

                    // DETERMINISTIC-CONSENSUS: Periodic validator list sync between casters
                    await SyncValidatorListsWithPeersAsync(Height);

                    ValidatorApprovalBag.Clear();
                    ValidatorApprovalBag = new ConcurrentBag<(string, long, string)>();
                    //Generate Proofs for ALL vals
                    CasterRoundAudit.AddStep($"Generating Proofs for height: {Height}.", true);
                    //ConsoleWriterService.OutputVal($"\r\nGenerating Proofs for height: {Height}.");
                    CasterLogUtility.Log($"[PHASE] PROOF-GEN entering at +{roundSw.ElapsedMilliseconds}ms", "PHASE");
                    var proofGenSw = Stopwatch.StartNew();
                    var casterProofs = await ProofUtility.GenerateCasterProofs();
                    var proofs = await ProofUtility.GenerateProofs();
                    proofGenSw.Stop();
                    CasterLogUtility.Log($"ProofGen: {proofGenSw.ElapsedMilliseconds}ms, casterProofs={casterProofs.Count}, allProofs={proofs.Count}", "PROOFS");
                    CasterRoundAudit.AddStep($"{proofs.Count()} Proofs Generated", true);

                    // DETERMINISTIC-CONSENSUS: Local pre-filtering (WINNER-SKIP, BOOTSTRAP-FILTER) REMOVED.
                    // These used divergent local state (_winnerFetchFailures, IsBootstrapMode, NetworkValidators)
                    // causing different casters to pick different VRF winners → permanent vote deadlocks.
                    // All casters now use the SAME unfiltered proof set for deterministic winner selection.
                    var filteredProofs = proofs;
                    // Note: skippedAddresses kept as empty set for compatibility with proofSnapshot below
                    var skippedAddresses = new HashSet<string>();

                    // FIX B: Use ALL validator proofs for winner selection (not just caster proofs).
                    var winningCasterProof = await ProofUtility.SortProofs(filteredProofs);
                    var winningProof = winningCasterProof;
                    CasterRoundAudit.AddStep($"Sorting Proofs (deterministic, no local filtering)", true);
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

                    // FIX: Make the empty-proof-set fall-through explicit. Previously when allProofs was 0
                    // (e.g. NetworkValidators re-seed missing PublicKey), the loop body silently fell through,
                    // looped back to the next round, and the chain stalled with no diagnostic beyond the
                    // ProofGen line. Log loud + skip-with-delay so operators see it within seconds.
                    if (filteredProofs.Count == 0)
                    {
                        CasterLogUtility.Log(
                            $"ROUND ABORT: empty proof set (allProofs=0, casterProofs={casterProofs.Count}, " +
                            $"NetworkValidators={Globals.NetworkValidators.Count}, BlockCasters={Globals.BlockCasters.Count}). " +
                            $"Likely a re-seed/PublicKey/state-balance issue — check PROOFS-DIAG and BOOT logs.",
                            "ROUND");
                        if (CasterRoundAudit != null)
                            CasterRoundAudit.AddStep($"No validator proofs generated this round (allProofs=0). Retrying.", false);
                        CasterLogUtility.Flush();
                        ProofUtility.ClearProofGenerationCache();
                        await Task.Delay(RETRY_DELAY_MS);
                        continue;
                    }

                    if (winningCasterProof != null && filteredProofs.Count > 0)
                    {
                        CasterLogUtility.Log($"Winner candidate: {winningCasterProof.Address} VRF={winningCasterProof.VRFNumber}", "VERIFY");
                        CasterRoundAudit.AddStep($"Attempting Proof on Address: {winningCasterProof.Address} (allProofs: {filteredProofs.Count})", true);
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
                                        // FIX F: Bump by 3 instead of 1 so the CheckFailCount <= 3 filter
                                        // in proof generation kicks in after just ONE VersionGate timeout.
                                        validator.CheckFailCount += 3;
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
                                    // FIX B: Re-select from all validator proofs, not just caster proofs
                                    winningCasterProof = await ProofUtility.SortProofs(filteredProofs
                                        .Where(x => !ExcludeValList.Contains(x.Address)).ToList()
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
                        CasterLogUtility.Log($"[PHASE] PROOF-EXCHANGE entering at +{roundSw.ElapsedMilliseconds}ms, need {requiredProofs}/{casterList.Count} proofs, have {Globals.CasterProofDict.Count()} (self-injected)", "PHASE");
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
                            ProofUtility.ClearProofGenerationCache();
                            await Task.Delay(RETRY_DELAY_MS);
                            continue;
                        }

                        foreach (var proofItem in Globals.CasterProofDict)
                        {
                            Globals.Proofs.Add(proofItem.Value);
                        }

                        // FIX B: Apply winner exclusions to proofSnapshot — prevents peer proofs
                        // from overriding local exclusions during winner agreement.
                        var proofSnapshot = Globals.Proofs
                            .Where(x => x.BlockHeight == Height && !skippedAddresses.Contains(x.Address))
                            .ToList();

                        // CONSENSUS-V2 (Fix #5): Proof-set commitment exchange.
                        // After proofs converge across casters, broadcast a hash over the sorted
                        // proof-address list and wait briefly for supermajority agreement on the
                        // commitment hash. If reached, restrict the winner-selection input to the
                        // agreed address set so all casters sort and pick from the SAME proofs —
                        // even if their local proof bags differ at the margins (a major source of
                        // proof-set divergence under load).  No agreement (timeout / minority) →
                        // we keep the local snapshot unchanged so the existing fallbacks
                        // (ReachWinnerAgreementAsync + size-tiered tiebreak) still drive convergence.
                        try
                        {
                            var localCommit = BuildLocalProofSetCommitment(Height, proofSnapshot);
                            var agreedAddresses = await ReachProofSetAgreementAsync(Height, localCommit);
                            if (agreedAddresses != null && agreedAddresses.Count > 0)
                            {
                                var agreedSet = new HashSet<string>(agreedAddresses, StringComparer.Ordinal);
                                var beforeCount = proofSnapshot.Count;
                                var filtered = proofSnapshot
                                    .Where(p => p != null && p.Address != null && agreedSet.Contains(p.Address))
                                    .ToList();
                                // Only adopt the filtered set if it preserves enough signal to pick a winner;
                                // an unexpected empty intersection means our local proof bag has diverged badly
                                // from the agreed set — better to fall back to the local snapshot than to crash.
                                if (filtered.Count > 0)
                                {
                                    proofSnapshot = filtered;
                                    CasterLogUtility.Log(
                                        $"[CONSENSUS-V2] ProofSetAgreement applied: filtered {beforeCount}→{proofSnapshot.Count} (agreed set size={agreedSet.Count})",
                                        "AGREEMENT");
                                }
                                else
                                {
                                    CasterLogUtility.Log(
                                        $"[CONSENSUS-V2] ProofSetAgreement: agreed set ({agreedSet.Count}) had no overlap with local proofs ({beforeCount}) — keeping local snapshot",
                                        "AGREEMENT");
                                }
                            }
                        }
                        catch (Exception psEx)
                        {
                            // Never let agreement crash the round — log and proceed with the local snapshot.
                            CasterLogUtility.Log($"[CONSENSUS-V2] ProofSetAgreement EXCEPTION: {psEx.Message} — falling back to local snapshot", "AGREEMENT");
                        }

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
                                CasterLogUtility.Log($"[PHASE] WINNER-AGREEMENT entering at +{roundSw.ElapsedMilliseconds}ms, candidate={finalizedWinner.Address} VRF={finalizedWinner.VRFNumber} proofCount={proofSnapshot.Count}", "PHASE");
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
                                    ProofUtility.ClearProofGenerationCache();
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

                                CasterLogUtility.Log($"[PHASE] BLOCK-FETCH entering at +{roundSw.ElapsedMilliseconds}ms, iAmWinner={terminalWinner == Globals.ValidatorAddress}, winner={terminalWinner}", "PHASE");
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
                                            // FIX: First try calling the winning VALIDATOR directly via VerifyBlock.
                                            // This is the intended design: casters should always fetch blocks from validators.
                                            // The winning validator pre-crafts blocks in GenerateValidBlock() and serves them
                                            // via the VerifyBlock endpoint when a caster requests the matching height.
                                            
                                            // Find the winning validator's IP from proofs or NetworkValidators
                                            string? winnerIP = null;
                                            var winnerProof = proofs.FirstOrDefault(p => p.Address == terminalWinner);
                                            if (winnerProof != null && !string.IsNullOrEmpty(winnerProof.IPAddress))
                                            {
                                                winnerIP = winnerProof.IPAddress.Replace("::ffff:", "");
                                            }
                                            else if (Globals.NetworkValidators.TryGetValue(terminalWinner, out var nv) && !string.IsNullOrEmpty(nv.IPAddress))
                                            {
                                                winnerIP = nv.IPAddress.Replace("::ffff:", "");
                                            }
                                            
                                            if (!string.IsNullOrEmpty(winnerIP))
                                            {
                                                CasterRoundAudit.AddStep($"Calling winning validator directly at {winnerIP} via VerifyBlock.", true);
                                                CasterLogUtility.Log($"Direct validator fetch: calling {terminalWinner} at {winnerIP}", "BLOCKFETCH");
                                                try
                                                {
                                                    var verifyResult = await ProofUtility.VerifyValAvailability(winnerIP, terminalWinner, finalizedWinner.BlockHeight);
                                                    if (verifyResult.Item1 && verifyResult.Item2 != null)
                                                    {
                                                        block = verifyResult.Item2;
                                                        blockFound = true;
                                                        failedToReachConsensus = false;
                                                        
                                                        round = Globals.CasterRoundDict.GetOrAdd(block.Height, new CasterRound { BlockHeight = block.Height });
                                                        var compareRound = round;
                                                        round.Block = block;
                                                        round.Validator = block.Validator;
                                                        Globals.CasterRoundDict.TryUpdate(finalizedWinner.BlockHeight, round, compareRound);
                                                        
                                                        CasterRoundAudit.AddStep($"Block fetched directly from validator {winnerIP}. Height: {block.Height} (staged; commit after hash agreement).", true);
                                                        CasterLogUtility.Log($"Direct validator fetch SUCCESS: block {block.Height} from {winnerIP}", "BLOCKFETCH");
                                                    }
                                                    else
                                                    {
                                                        CasterLogUtility.Log($"Direct validator fetch FAILED: {terminalWinner} at {winnerIP} returned no block", "BLOCKFETCH");
                                                    }
                                                }
                                                catch (Exception ex)
                                                {
                                                    CasterLogUtility.Log($"Direct validator fetch ERROR: {terminalWinner} at {winnerIP}: {ex.Message}", "BLOCKFETCH");
                                                }
                                            }
                                            else
                                            {
                                                CasterLogUtility.Log($"Direct validator fetch SKIPPED: no IP found for winner {terminalWinner}", "BLOCKFETCH");
                                            }
                                            
                                            // Fallback: try peer casters if direct validator call failed
                                            if (!blockFound)
                                            {
                                                CasterRoundAudit.AddStep($"Direct validator fetch failed. Trying peer casters as fallback.", true);
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

                                                            CasterRoundAudit.AddStep($"Block fetched from caster {caster.PeerIP}. Height: {block.Height} (staged; commit after hash agreement).", true);

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
                                        }
                                        /////////////////////////////////////
                                        //This is done if non-caster wins the block

                                        await Task.Delay(200);
                                    }

                                    CasterLogUtility.Log($"BlockFetch done: {swb.ElapsedMilliseconds}ms, found={blockFound}, failed={failedToReachConsensus}", "BLOCKFETCH");

                                    // WINNER-SKIP: Track block-fetch failures per winner address at this height
                                    if (!blockFound && !string.IsNullOrEmpty(terminalWinner))
                                    {
                                        _winnerFetchFailures.AddOrUpdate(
                                            terminalWinner,
                                            (Height, 1),
                                            (key, existing) => existing.height == Height
                                                ? (Height, existing.failCount + 1)
                                                : (Height, 1));
                                        var newCount = _winnerFetchFailures.TryGetValue(terminalWinner, out var fc) ? fc.failCount : 0;
                                        CasterLogUtility.Log($"WINNER-SKIP: BlockFetch failed for {terminalWinner} at height {Height} (streak {newCount}/{WINNER_SKIP_THRESHOLD})", "BLOCKFETCH");

                                        // FIX D2: Track cumulative failures across rounds with escalating exclusion
                                        var cumulative = _winnerCumulativeFailures.AddOrUpdate(
                                            terminalWinner,
                                            (1, 0L),
                                            (_, prev) => (prev.totalFails + 1, prev.excludeUntilHeight));
                                        int totalFails = cumulative.totalFails;
                                        long excludeUntil = 0;
                                        if (totalFails >= 10)
                                        {
                                            // Evict from NetworkValidators entirely
                                            Globals.NetworkValidators.TryRemove(terminalWinner, out _);
                                            _winnerCumulativeFailures.TryRemove(terminalWinner, out _);
                                            CasterLogUtility.Log($"WINNER-EVICT: {terminalWinner} evicted from NetworkValidators after {totalFails} cumulative failures", "PROOFS");
                                        }
                                        else if (totalFails >= 6)
                                        {
                                            excludeUntil = Height + 50;
                                            _winnerCumulativeFailures[terminalWinner] = (totalFails, excludeUntil);
                                            CasterLogUtility.Log($"WINNER-EXCLUDE: {terminalWinner} excluded for 50 blocks (until {excludeUntil}, fails={totalFails})", "PROOFS");
                                        }
                                        else if (totalFails >= 3)
                                        {
                                            excludeUntil = Height + 10;
                                            _winnerCumulativeFailures[terminalWinner] = (totalFails, excludeUntil);
                                            CasterLogUtility.Log($"WINNER-EXCLUDE: {terminalWinner} excluded for 10 blocks (until {excludeUntil}, fails={totalFails})", "PROOFS");
                                        }
                                    }
                                    else if (blockFound)
                                    {
                                        // Success — clear failure tracking for this winner and all stale entries
                                        if (!string.IsNullOrEmpty(terminalWinner))
                                        {
                                            _winnerFetchFailures.TryRemove(terminalWinner, out _);
                                            // FIX D2: Clear cumulative failures on success
                                            ClearWinnerFailures(terminalWinner);
                                        }
                                        // Clear entries from old heights
                                        foreach (var key in _winnerFetchFailures.Keys.ToList())
                                        {
                                            if (_winnerFetchFailures.TryGetValue(key, out var val) && val.height < Height)
                                                _winnerFetchFailures.TryRemove(key, out _);
                                        }
                                    }

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

                    CasterLogUtility.Log($"--- ROUND END height={Height} totalTime={roundSw.ElapsedMilliseconds}ms lastBlock={Globals.LastBlock.Height} refH={ReferenceHeight} ---", "ROUND");
                    CasterLogUtility.Flush();

                    // FORK-RECOVERY: Track consecutive rounds where lastBlock height doesn't advance.
                    // If we're stuck at the same height for FORK_RECOVERY_THRESHOLD rounds AND hash sync
                    // has already given up (indicating we're on a minority fork), trigger automatic
                    // rollback + resync to self-heal.
                    {
                        var currentLastBlockHeight = Globals.LastBlock.Height;
                        if (_forkStuckHeight == currentLastBlockHeight)
                        {
                            _forkStuckRounds++;
                            // Only trigger recovery if hash sync has also failed (confirming we're on a wrong block)
                            bool hashSyncGaveUp = _hashSyncFailHeight == currentLastBlockHeight && _hashSyncFailCount >= HASH_SYNC_MAX_RETRIES;
                            if (_forkStuckRounds >= FORK_RECOVERY_THRESHOLD && hashSyncGaveUp)
                            {
                                CasterLogUtility.Log(
                                    $"FORK-RECOVERY: Detected {_forkStuckRounds} rounds stuck at height {currentLastBlockHeight} " +
                                    $"with hash sync failed ({_hashSyncFailCount} failures). Triggering self-heal.",
                                    "FORK-RECOVERY");
                                await ForkRecoveryAsync(currentLastBlockHeight);
                                // After recovery, continue to next round iteration
                                continue;
                            }
                            else if (_forkStuckRounds % 5 == 0)
                            {
                                CasterLogUtility.Log(
                                    $"FORK-STUCK: {_forkStuckRounds} rounds at height {currentLastBlockHeight}. " +
                                    $"hashSyncGaveUp={hashSyncGaveUp} (failCount={_hashSyncFailCount}/{HASH_SYNC_MAX_RETRIES})",
                                    "FORK-RECOVERY");
                            }
                        }
                        else
                        {
                            // Height advanced — reset stuck tracking
                            _forkStuckHeight = currentLastBlockHeight;
                            _forkStuckRounds = 0;
                        }
                    }

                    if (Environment.TickCount64 - _lastStartingOverLogTicks >= 15_000)
                    {
                        ConsoleWriterService.OutputValCaster("\r\nStarting over.");
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
                    ConsoleWriterService.OutputValCaster($"\r\n[HeightSync] Behind by {maxPeerHeight - myHeight} block(s). Catching up from {bestPeerIP}...");
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
                                            ConsoleWriterService.OutputValCaster($"[HeightSync] Applied block {h}.");
                                        }
                                        else
                                        {
                                            ConsoleWriterService.OutputValCaster($"[HeightSync] Block {h} validation failed. Stopping catch-up.");
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            ConsoleWriterService.OutputValCaster($"[HeightSync] Error fetching block {h}: {ex.Message}");
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
            ConsoleWriterService.OutputValCaster("Booting up consensus loop");

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

                ConsoleWriterService.OutputValCaster("Top of consensus loop");

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
                        await Task.WhenAll(BlockDelay, Task.Delay(1000));
                        
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
                                        // FIX F: Bump by 3 instead of 1 (legacy path)
                                        validator.CheckFailCount += 3;
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
                                                ConsoleWriterService.OutputValCaster("\r\n Bag failed. No Result was found.");
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
                        ConsoleWriterService.OutputValCaster("\r\nStarting over.");
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
                            ConsoleWriterService.OutputValCaster($"[Consensus Gate] Block at height {nextBlock.Height} from {nextBlock.Validator} rejected — consensus chose {consensusRound.Validator}");
                        }
                        // Reset _acceptedHeight so the correct block can be processed
                        Interlocked.CompareExchange(ref _acceptedHeight, currentAccepted, nextBlock.Height);
                        return;
                    }
                }
                else if (desyncRecoveryMode)
                {
                    ConsoleWriterService.OutputValCaster($"[Desync Recovery] Stuck for {timeSinceLastBlock}ms — bypassing consensus gate for block {nextBlock.Height} from {nextBlock.Validator}");
                }

                // ── Agreed-hash gate (4-case logic) ──────────────────────────────
                // Case 1: Agreed hash exists AND matches → accept (fall through)
                // Case 2: Agreed hash exists AND differs → reject (actual fork)
                // Case 3: No agreed hash + no CasterRoundDict entry → accept (we haven't started consensus for this height)
                // Case 4: No agreed hash + CasterRoundDict entry exists → spin-wait briefly, then accept (mid-round)
                string? agreedHashForGate = null;
                Globals.CasterApprovedBlockHashDict.TryGetValue(nextBlock.Height, out agreedHashForGate);

                bool hasCasterRoundEntry = Globals.CasterRoundDict.ContainsKey(nextBlock.Height);

                if (!desyncRecoveryMode && Globals.IsBlockCaster && nextBlock.Height == lastBlockHeight + 1
                    && string.IsNullOrEmpty(agreedHashForGate) && hasCasterRoundEntry)
                {
                    // Case 4: We're mid-round for this height — spin-wait briefly for the agreement to land
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
                    if (string.IsNullOrEmpty(agreedHashForGate))
                    {
                        ConsoleWriterService.OutputValCaster($"[Consensus Gate] Mid-round accept: height {nextBlock.Height} — CasterRoundDict entry exists but agreement not yet resolved after {spin.ElapsedMilliseconds}ms spin.");
                    }
                }
                else if (!desyncRecoveryMode && Globals.IsBlockCaster && nextBlock.Height == lastBlockHeight + 1
                    && string.IsNullOrEmpty(agreedHashForGate) && !hasCasterRoundEntry)
                {
                    // Case 3: No CasterRoundDict entry — we haven't started consensus for this height, trust peer broadcast
                    ConsoleWriterService.OutputValCaster($"[Consensus Gate] No-round accept: height {nextBlock.Height} — no CasterRoundDict entry, trusting peer broadcast.");
                }

                // Case 2: Agreed hash exists but doesn't match → reject (actual fork)
                if (!desyncRecoveryMode && !string.IsNullOrEmpty(agreedHashForGate) && nextBlock.Hash != agreedHashForGate)
                {
                    ConsoleWriterService.OutputValCaster($"[Consensus Gate] Block at height {nextBlock.Height} hash mismatch — expected {agreedHashForGate[..Math.Min(12, agreedHashForGate.Length)]}… got {nextBlock.Hash?[..Math.Min(12, nextBlock.Hash?.Length ?? 0)]}…");
                    Interlocked.CompareExchange(ref _acceptedHeight, currentAccepted, nextBlock.Height);
                    return;
                }
                // Case 1: Agreed hash matches (or no hash at all after cases 3/4) → fall through to accept

                if (nextBlock.Height != Globals.LastBlock.Height + 1)
                {
                    ConsoleWriterService.OutputValCaster($"[Consensus Gate] Rejecting height {nextBlock.Height} — next expected is {Globals.LastBlock.Height + 1}.");
                    Interlocked.CompareExchange(ref _acceptedHeight, currentAccepted, nextBlock.Height);
                    return;
                }

                var result = await BlockValidatorService.ValidateBlock(nextBlock, true, false, false, true);
                if (result)
                {
                    // DESYNC-FIX: Update last block accepted timestamp for desync recovery tracking
                    Interlocked.Exchange(ref _lastBlockAcceptedTick, Environment.TickCount64);

                    // ROUND-SYNC-FIX: Reset timing references when a block is committed via message-7.
                    // This anchors all casters' round timing to the same event (block commit), preventing
                    // the new caster from racing ahead when it receives the broadcast before the producers
                    // have finished their own commit.
                    ResetRoundTiming(nextBlock.Height);

                    // Auto-promote block producer to fully trusted — if this validator
                    // produced a block that passed full validation, it is definitively legitimate.
                    if (!string.IsNullOrEmpty(nextBlock.Validator))
                    {
                        NetworkValidator.PromoteBlockProducer(nextBlock.Validator);
                    }

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
            ConsoleWriterService.OutputValCaster("[ReadinessBarrier] Waiting for peer casters to be ready...");
            var sw = Stopwatch.StartNew();

            while (sw.ElapsedMilliseconds < READINESS_MAX_WAIT_MS)
            {
                // First, sync our height with peers in case we're behind
                await SyncHeightWithPeersAsync();

                var myHeight = Globals.LastBlock.Height;
                var casters = Globals.BlockCasters.ToList();
                if (casters.Count <= 1)
                {
                    ConsoleWriterService.OutputValCaster("[ReadinessBarrier] Only 1 caster (self); proceeding.");
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

                ConsoleWriterService.OutputValCaster($"[ReadinessBarrier] Ready: {readyCount}/{casters.Count}, Height-matched: {matchingHeightCount}/{casters.Count} (need {requiredReady})");

                if (readyCount >= requiredReady && matchingHeightCount >= requiredReady)
                {
                    ConsoleWriterService.OutputValCaster("[ReadinessBarrier] Supermajority of casters ready and height-synced. Starting consensus.");
                    break;
                }

                await Task.Delay(READINESS_CHECK_INTERVAL_MS);
            }

            if (sw.ElapsedMilliseconds >= READINESS_MAX_WAIT_MS)
            {
                ConsoleWriterService.OutputValCaster("[ReadinessBarrier] WARNING: Timed out waiting for peer readiness. Proceeding anyway — peers may be unavailable.");
            }
        }

        /// <summary>
        /// CASTER-CONSENSUS-FIX: Mandatory winner agreement phase.
        /// After each caster independently picks a winner from its proof snapshot,
        /// exchange winner votes with all peer casters. Only proceed if a supermajority
        /// agrees on the same winner. This prevents the boot desync scenario where a slow
        /// caster picks a different winner than its peers.
        /// DETERMINISTIC-CONSENSUS: Includes deadlock safety net — after WINNER_AGREEMENT_DEADLOCK_THRESHOLD
        /// consecutive failures at the same height, uses deterministic tiebreaker (lowest VRF from all votes).
        /// Returns the agreed winner address, or null if no agreement was reached.
        /// </summary>
        private static async Task<string?> ReachWinnerAgreementAsync(long height, string myChosenWinner)
        {
            var casters = Globals.BlockCasters.ToList();
            if (casters.Count <= 1)
                return myChosenWinner; // Only one caster, no agreement needed

            var requiredAgreement = Math.Max(2, casters.Count / 2 + 1);

            // DETERMINISTIC-CONSENSUS: Check for deadlock safety net
            if (_winnerAgreementFailHeight == height)
            {
                _winnerAgreementFailCount++;
                // CONSENSUS-V2 (Fix #6): Use size-tiered threshold so 2-caster bootstrap
                // doesn't have to wait 5 rounds (~30s) to tiebreak.
                var winnerDeadlockThreshold = GetWinnerAgreementDeadlockThreshold(casters.Count);
                if (_winnerAgreementFailCount >= winnerDeadlockThreshold)
                {
                    // Deadlock detected! Use deterministic tiebreaker: sort all known votes lexicographically
                    // and pick the lowest winner address. All casters will converge on the same choice.
                    var allVotes = Globals.CasterWinnerVoteDict.TryGetValue(height, out var existingVotes)
                        ? existingVotes.Values.Distinct().OrderBy(v => v, StringComparer.Ordinal).ToList()
                        : new List<string> { myChosenWinner };
                    var tiebreakWinner = allVotes.FirstOrDefault() ?? myChosenWinner;
                    CasterLogUtility.Log(
                        $"DEADLOCK-SAFETY: {_winnerAgreementFailCount} failures at height {height}. " +
                        $"Deterministic tiebreak → {tiebreakWinner} (from {allVotes.Count} candidates: [{string.Join(",", allVotes.Select(v => v[..Math.Min(8, v.Length)]))}])",
                        "AGREEMENT");
                    _winnerAgreementFailCount = 0;
                    _winnerAgreementFailHeight = -1;
                    return tiebreakWinner;
                }
            }
            else
            {
                _winnerAgreementFailHeight = height;
                _winnerAgreementFailCount = 1;
            }

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
                                WinnerAddress = myChosenWinner,
                                ExcludedAddresses = new List<string>() // DETERMINISTIC-CONSENSUS: No local exclusions (removed WINNER-SKIP)
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

            if (agreedWinner != null)
            {
                // Reset failure tracking on success
                _winnerAgreementFailHeight = -1;
                _winnerAgreementFailCount = 0;
            }
            else
            {
                CasterLogUtility.Log($"WinnerAgreement: FAILED after {sw.ElapsedMilliseconds}ms (streak {_winnerAgreementFailCount}/{WINNER_AGREEMENT_DEADLOCK_THRESHOLD} at height {height}). Votes: {string.Join(", ", votesForHeight.Select(kv => $"{kv.Key[..Math.Min(8, kv.Key.Length)]}→{kv.Value[..Math.Min(8, kv.Value.Length)]}"))}", "AGREEMENT");
            }

            // Cleanup old vote entries
            var oldKeys = Globals.CasterWinnerVoteDict.Keys.Where(k => k < height - 10).ToList();
            foreach (var k in oldKeys)
                Globals.CasterWinnerVoteDict.TryRemove(k, out _);

            // DETERMINISTIC-CONSENSUS: Cleanup old excluded address entries
            var oldExclKeys = Globals.CasterExcludedAddressDict.Keys.Where(k => k < height - 10).ToList();
            foreach (var k in oldExclKeys)
                Globals.CasterExcludedAddressDict.TryRemove(k, out _);

            return agreedWinner;
        }

        #region CONSENSUS-V2 Fix #5 — Proof-set commitment exchange

        /// <summary>How long to wait for proof-set commitment supermajority before falling through.</summary>
        const int PROOF_SET_AGREEMENT_TIMEOUT_MS = 3000;

        /// <summary>
        /// CONSENSUS-V2 (Fix #5): Computes the canonical commitment hash over a sorted list
        /// of proof addresses. Lower-case hex SHA-256 of <c>"|".Join(addresses)</c>.
        /// Public+static so the controller endpoint and tests can re-compute it.
        /// </summary>
        public static string ComputeProofSetCommitmentHash(IEnumerable<string> sortedAddresses)
        {
            var joined = string.Join("|", sortedAddresses ?? Enumerable.Empty<string>());
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(joined));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        /// <summary>
        /// CONSENSUS-V2 (Fix #5): Builds a <see cref="ProofSetCommitment"/> from this caster's
        /// local proof set for the given height. Addresses are de-duplicated and sorted with
        /// <see cref="StringComparer.Ordinal"/> so every caster computes the same commitment
        /// when their proof inputs match.
        /// </summary>
        public static Models.ProofSetCommitment BuildLocalProofSetCommitment(long height, IEnumerable<Proof> proofs)
        {
            var sortedAddresses = (proofs ?? Enumerable.Empty<Proof>())
                .Where(p => p != null && p.BlockHeight == height && !string.IsNullOrEmpty(p.Address))
                .Select(p => p.Address!)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(a => a, StringComparer.Ordinal)
                .ToList();
            return new Models.ProofSetCommitment
            {
                BlockHeight = height,
                CasterAddress = Globals.ValidatorAddress ?? "",
                ProofAddressesSorted = sortedAddresses,
                CommitmentHash = ComputeProofSetCommitmentHash(sortedAddresses),
            };
        }

        /// <summary>
        /// CONSENSUS-V2 (Fix #5): Reach supermajority agreement on the proof-set hash for a
        /// given block height. Each caster broadcasts its <see cref="ProofSetCommitment"/>;
        /// receivers tally by <see cref="ProofSetCommitment.CommitmentHash"/>. If a single
        /// hash group has supermajority count, we adopt that group's sorted address list as
        /// the canonical proof-address set. If no supermajority emerges before the timeout,
        /// returns <see langword="null"/> and the caller falls back to local-snapshot semantics
        /// (preserves current behavior so a Phase-2 regression cannot deadlock production).
        /// </summary>
        public static async Task<List<string>?> ReachProofSetAgreementAsync(
            long height,
            Models.ProofSetCommitment myCommitment)
        {
            if (myCommitment == null)
                return null;

            var casters = Globals.BlockCasters.ToList();
            if (casters.Count <= 1)
                return myCommitment.ProofAddressesSorted; // single caster — trivially in agreement

            var requiredAgreement = Math.Max(2, casters.Count / 2 + 1);

            var commitsForHeight = Globals.CasterProofSetCommitDict
                .GetOrAdd(height, _ => new ConcurrentDictionary<string, Models.ProofSetCommitment>());
            commitsForHeight[Globals.ValidatorAddress ?? ""] = myCommitment;

            var sw = Stopwatch.StartNew();
            // Cache hash → sorted address list so we don't have to track group-membership
            // separately when picking the winning hash.
            var hashToAddresses = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                [myCommitment.CommitmentHash] = myCommitment.ProofAddressesSorted ?? new List<string>()
            };

            string? winningHash = null;
            int winningCount = 0;

            while (sw.ElapsedMilliseconds < PROOF_SET_AGREEMENT_TIMEOUT_MS)
            {
                var peerTasks = casters
                    .Where(c => !string.IsNullOrEmpty(c.PeerIP) && c.ValidatorAddress != Globals.ValidatorAddress)
                    .Select(async caster =>
                    {
                        try
                        {
                            using var client = Globals.HttpClientFactory.CreateClient();
                            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                            var uri = $"http://{caster.PeerIP!.Replace("::ffff:", "")}:{Globals.ValAPIPort}/valapi/validator/ExchangeProofSet";
                            using var content = new StringContent(
                                JsonConvert.SerializeObject(myCommitment),
                                Encoding.UTF8,
                                "application/json");
                            var resp = await client.PostAsync(uri, content, cts.Token).ConfigureAwait(false);
                            if (!resp.IsSuccessStatusCode) return;
                            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                            if (string.IsNullOrEmpty(body) || body == "0") return;
                            var psResp = JsonConvert.DeserializeObject<Models.ProofSetExchangeResponse>(body);
                            if (psResp?.Commitments == null) return;
                            foreach (var kv in psResp.Commitments)
                            {
                                if (kv.Value == null) continue;
                                if (kv.Value.BlockHeight != height) continue;
                                if (string.IsNullOrEmpty(kv.Value.CasterAddress)) continue;
                                // Re-verify the commitment hash so a bad peer can't poison our tally.
                                var recomputed = ComputeProofSetCommitmentHash(kv.Value.ProofAddressesSorted ?? new List<string>());
                                if (!string.Equals(recomputed, kv.Value.CommitmentHash, StringComparison.Ordinal))
                                    continue;
                                commitsForHeight[kv.Value.CasterAddress] = kv.Value;
                            }
                        }
                        catch { /* best-effort */ }
                    })
                    .ToList();

                await Task.WhenAll(peerTasks).ConfigureAwait(false);

                hashToAddresses[myCommitment.CommitmentHash] = myCommitment.ProofAddressesSorted ?? new List<string>();
                foreach (var c in commitsForHeight.Values)
                {
                    if (c == null || string.IsNullOrEmpty(c.CommitmentHash)) continue;
                    if (!hashToAddresses.ContainsKey(c.CommitmentHash))
                        hashToAddresses[c.CommitmentHash] = c.ProofAddressesSorted ?? new List<string>();
                }

                var byHash = commitsForHeight.Values
                    .Where(c => c != null && !string.IsNullOrEmpty(c.CommitmentHash))
                    .GroupBy(c => c.CommitmentHash, StringComparer.Ordinal)
                    .OrderByDescending(g => g.Count())
                    .ToList();

                if (byHash.Count > 0)
                {
                    var best = byHash.First();
                    winningHash = best.Key;
                    winningCount = best.Count();

                    CasterLogUtility.Log(
                        $"[CONSENSUS-V2] ProofSetAgreement: votes={commitsForHeight.Count}/{casters.Count} bestHash={winningHash[..Math.Min(10, winningHash.Length)]}… count={winningCount} need={requiredAgreement}",
                        "AGREEMENT");

                    if (winningCount >= requiredAgreement)
                        break;
                }

                await Task.Delay(250).ConfigureAwait(false);
            }

            // Cleanup older heights regardless of outcome.
            var oldKeys = Globals.CasterProofSetCommitDict.Keys.Where(k => k < height - 10).ToList();
            foreach (var k in oldKeys)
                Globals.CasterProofSetCommitDict.TryRemove(k, out _);

            if (winningHash == null || winningCount < requiredAgreement)
            {
                CasterLogUtility.Log(
                    $"[CONSENSUS-V2] ProofSetAgreement: TIMEOUT after {sw.ElapsedMilliseconds}ms — no supermajority. " +
                    $"Falling back to local snapshot (votes={commitsForHeight.Count}, need={requiredAgreement}).",
                    "AGREEMENT");
                return null;
            }

            if (hashToAddresses.TryGetValue(winningHash, out var winningAddresses))
            {
                CasterLogUtility.Log(
                    $"[CONSENSUS-V2] ProofSetAgreement: AGREED hash={winningHash[..Math.Min(10, winningHash.Length)]}… size={winningAddresses.Count} ({winningCount}/{casters.Count})",
                    "AGREEMENT");
                return winningAddresses;
            }
            return null;
        }

        #endregion

        /// <summary>
        /// CONSENSUS-V2 (Fix #4): Periodic validator list sync between casters.
        /// Exchanges full <see cref="ValidatorListEntry"/> records (Address + IP + PublicKey + FirstSeenAtHeight + LastSeen)
        /// so peers can actually materialize a fully-formed <see cref="NetworkValidator"/> for any
        /// missing entry — not just identify gaps. Each merged entry is liveness-gated and version-checked
        /// before joining <see cref="Globals.NetworkValidators"/> as fully trusted.
        /// Public so the post-promotion path can trigger an out-of-band sync immediately after a successful
        /// caster promotion (Fix #4 also tightens cadence from 50→10 blocks for steady-state convergence).
        /// </summary>
        /// <summary>
        /// CONSENSUS-V2 (Fix #4): Maximum number of new validator entries this node will merge
        /// in a single sync round across ALL peer responses. Prevents an HTTP storm of liveness
        /// checks if a freshly-joined caster receives a 100-validator list from every peer at
        /// once. Excess entries are silently deferred to the next 10-block sync tick.
        /// </summary>
        const int VALIDATOR_LIST_SYNC_MERGE_CAP = 25;

        public static async Task SyncValidatorListsWithPeersAsync(long currentHeight, bool force = false)
        {
            if (!force && currentHeight - _lastValidatorListSyncHeight < VALIDATOR_LIST_SYNC_INTERVAL)
                return;

            _lastValidatorListSyncHeight = currentHeight;

            var casters = Globals.BlockCasters.ToList()
                .Where(c => !string.IsNullOrEmpty(c.PeerIP) && c.ValidatorAddress != Globals.ValidatorAddress)
                .ToList();

            if (!casters.Any()) return;

            // Build full per-validator entries from our local registry — only IsFullyTrusted entries
            // with a non-empty IP, since those are the only ones a peer can usefully merge.
            var myEntries = Globals.NetworkValidators.Values
                .Where(v => v != null
                            && v.IsFullyTrusted
                            && !string.IsNullOrEmpty(v.Address)
                            && !string.IsNullOrEmpty(v.IPAddress))
                .Select(v => new ValidatorListEntry
                {
                    Address = v.Address,
                    IPAddress = v.IPAddress,
                    PublicKey = v.PublicKey ?? "",
                    FirstSeenAtHeight = v.FirstSeenAtHeight,
                    LastSeen = v.LastSeen
                })
                .ToList();

            CasterLogUtility.Log(
                $"VALLIST-SYNC: Starting sync at height {currentHeight} (force={force}). My trusted validators: {myEntries.Count} / total {Globals.NetworkValidators.Count}",
                "CONSENSUS");

            int totalMerged = 0;
            int totalRejected = 0;
            int totalDeferred = 0;

            var syncTasks = casters.Select(async caster =>
            {
                try
                {
                    using var client = Globals.HttpClientFactory.CreateClient();
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    var uri = $"http://{caster.PeerIP!.Replace("::ffff:", "")}:{Globals.ValAPIPort}/valapi/validator/ExchangeValidatorList";
                    var req = new ValidatorListExchangeRequest
                    {
                        BlockHeight = currentHeight,
                        CasterAddress = Globals.ValidatorAddress ?? "",
                        Validators = myEntries
                    };
                    using var content = new StringContent(
                        JsonConvert.SerializeObject(req),
                        Encoding.UTF8,
                        "application/json");
                    var resp = await client.PostAsync(uri, content, cts.Token);
                    if (!resp.IsSuccessStatusCode)
                        return;

                    var body = await resp.Content.ReadAsStringAsync();
                    if (string.IsNullOrEmpty(body) || body == "0")
                        return;

                    var listResp = JsonConvert.DeserializeObject<ValidatorListExchangeResponse>(body);
                    if (listResp?.Validators == null || listResp.Validators.Count == 0)
                        return;

                    int peerMerged = 0;
                    int peerRejected = 0;
                    int peerDeferred = 0;
                    foreach (var entry in listResp.Validators)
                    {
                        if (entry == null
                            || string.IsNullOrEmpty(entry.Address)
                            || string.IsNullOrEmpty(entry.IPAddress))
                            continue;

                        // Skip self and anything already known.
                        if (entry.Address == Globals.ValidatorAddress)
                            continue;
                        if (Globals.NetworkValidators.ContainsKey(entry.Address))
                            continue;

                        // CONSENSUS-V2 (Fix #4): Per-round merge cap. Once we've merged
                        // VALIDATOR_LIST_SYNC_MERGE_CAP entries this round, defer the rest to the next
                        // sync tick. We still drain the response (no early break) so the HTTP
                        // socket closes cleanly and we get an accurate "deferred" count for logs.
                        if (Volatile.Read(ref totalMerged) >= VALIDATOR_LIST_SYNC_MERGE_CAP)
                        {
                            Interlocked.Increment(ref peerDeferred);
                            continue;
                        }

                        // Liveness + version gate before merging — never trust a peer's word alone.
                        bool live;
                        try { live = await NetworkValidator.CheckValidatorLiveness(entry.IPAddress); }
                        catch { live = false; }

                        if (!live)
                        {
                            Interlocked.Increment(ref peerRejected);
                            continue;
                        }

                        // Re-check the cap AFTER the (slow) liveness call — another peer task could
                        // have crossed the threshold while we were awaiting.
                        if (Volatile.Read(ref totalMerged) >= VALIDATOR_LIST_SYNC_MERGE_CAP)
                        {
                            Interlocked.Increment(ref peerDeferred);
                            continue;
                        }

                        var nv = new NetworkValidator
                        {
                            Address = entry.Address,
                            IPAddress = entry.IPAddress,
                            PublicKey = entry.PublicKey ?? "",
                            IsFullyTrusted = true,
                            LastSeen = TimeUtil.GetTime(),
                            FirstSeenAtHeight = entry.FirstSeenAtHeight > 0 ? entry.FirstSeenAtHeight : currentHeight,
                            CheckFailCount = 0,
                        };
                        if (Globals.NetworkValidators.TryAdd(entry.Address, nv))
                        {
                            Interlocked.Increment(ref peerMerged);
                            Interlocked.Increment(ref totalMerged);
                        }
                    }

                    if (peerMerged > 0 || peerRejected > 0 || peerDeferred > 0)
                        CasterLogUtility.Log(
                            $"VALLIST-SYNC: Peer {caster.PeerIP} → merged={peerMerged} rejected={peerRejected} deferred={peerDeferred} (peer reported {listResp.Validators.Count} entries)",
                            "CONSENSUS");

                    Interlocked.Add(ref totalRejected, peerRejected);
                    Interlocked.Add(ref totalDeferred, peerDeferred);
                }
                catch (Exception ex)
                {
                    CasterLogUtility.Log($"VALLIST-SYNC: Peer {caster.PeerIP} ERROR: {ex.Message}", "CONSENSUS");
                }
            }).ToList();

            await Task.WhenAll(syncTasks);

            if (totalDeferred > 0)
                CasterLogUtility.Log(
                    $"[CONSENSUS-V2] VALLIST-SYNC capped at {VALIDATOR_LIST_SYNC_MERGE_CAP}/{totalMerged + totalDeferred} merged this round — {totalDeferred} entries deferred to next round",
                    "CONSENSUS");

            CasterLogUtility.Log(
                $"VALLIST-SYNC: SUMMARY at height {currentHeight} → casters={casters.Count} merged={totalMerged} rejected={totalRejected} deferred={totalDeferred} myCount(after)={Globals.NetworkValidators.Count}",
                "CONSENSUS");
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
            // ROUND-SYNC-FIX: Reset timing after local block commit so this caster's
            // next round starts from the same baseline as peers who receive the broadcast.
            ResetRoundTiming(block.Height);

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

            // CONSENSUS-V2 (Fix #6): Size-tiered threshold (2 casters → reconcile after 2 fails).
            var blockHashReconcileThreshold = GetBlockHashAgreementReconcileThreshold(Globals.BlockCasters.Count);
            if (_consecutiveBlockHashAgreementFailures >= blockHashReconcileThreshold)
            {
                _consecutiveBlockHashAgreementFailures = 0;
                CasterRoundAudit?.AddStep($"[BlockHashAgreement] {BLOCK_HASH_AGREEMENT_RECONCILE_THRESHOLD}+ consecutive failures — forced chain reconciliation.", false);
                await SyncBlockHashWithPeersAsync();
                await SyncHeightWithPeersAsync();
                try { await BlockDownloadService.GetAllBlocks(); } catch { }
            }
        }

        /// <summary>
        /// FORK-RECOVERY: Self-healing mechanism for nodes stuck on a minority-fork block.
        /// When a node has been stuck at the same height for FORK_RECOVERY_THRESHOLD rounds
        /// (and BlockHashSync has already failed), this method:
        /// 1. Self-demotes from caster pool temporarily
        /// 2. Rolls back the bad block using BlockRollbackUtility
        /// 3. Re-downloads the correct block from peers
        /// 4. Resets all failure counters
        /// 5. Returns to normal consensus participation
        /// This requires NO human intervention — the node heals itself automatically.
        /// </summary>
        private static async Task ForkRecoveryAsync(long stuckHeight)
        {
            // Prevent concurrent recovery attempts
            if (Interlocked.CompareExchange(ref _forkRecoveryInProgress, 1, 0) != 0)
                return;

            try
            {
                CasterLogUtility.Log(
                    $"FORK-RECOVERY: Initiating self-heal at height {stuckHeight}. " +
                    $"Stuck for {_forkStuckRounds} rounds. Hash sync failed {_hashSyncFailCount} times.",
                    "FORK-RECOVERY");
                ConsoleWriterService.OutputValCaster(
                    $"[FORK-RECOVERY] Detected {_forkStuckRounds} rounds stuck at height {stuckHeight}. " +
                    $"Initiating automatic rollback + resync...");

                // Step 1: Halt consensus so we don't keep poisoning the network
                Interlocked.Exchange(ref _casterConsensusHalted, 1);

                // Step 2: Roll back the bad block
                CasterLogUtility.Log($"FORK-RECOVERY: Rolling back 1 block from height {stuckHeight}...", "FORK-RECOVERY");
                var rollbackResult = await BlockRollbackUtility.RollbackBlocks(1);
                if (rollbackResult)
                {
                    CasterLogUtility.Log(
                        $"FORK-RECOVERY: Rollback succeeded. New lastBlock height={Globals.LastBlock.Height} hash={Globals.LastBlock.Hash?[..Math.Min(16, Globals.LastBlock.Hash?.Length ?? 0)]}",
                        "FORK-RECOVERY");
                }
                else
                {
                    CasterLogUtility.Log($"FORK-RECOVERY: Rollback returned false — chain may need deeper repair.", "FORK-RECOVERY");
                }

                // Step 3: Sync height with peers and download the correct block
                CasterLogUtility.Log($"FORK-RECOVERY: Syncing height with peers...", "FORK-RECOVERY");
                await SyncHeightWithPeersAsync();

                CasterLogUtility.Log($"FORK-RECOVERY: Downloading blocks from peers...", "FORK-RECOVERY");
                try { await BlockDownloadService.GetAllBlocks(); } catch { }

                // Step 4: Verify the chain hash now matches peers
                CasterLogUtility.Log($"FORK-RECOVERY: Verifying chain hash after resync...", "FORK-RECOVERY");
                await SyncBlockHashWithPeersAsync();

                // Step 5: Reset ALL failure counters
                _hashSyncFailHeight = -1;
                _hashSyncFailCount = 0;
                _forkStuckHeight = -1;
                _forkStuckRounds = 0;
                _consecutiveBlockHashAgreementFailures = 0;
                _consecutiveMajorityBlockFetchFailures = 0;
                _winnerFetchFailures.Clear();
                _winnerCumulativeFailures.Clear();
                _winnerAgreementFailHeight = -1;
                _winnerAgreementFailCount = 0;

                // Step 6: Reset timing references so consensus restarts cleanly
                ResetRoundTiming(Globals.LastBlock.Height, force: true);

                // Step 7: Clear the halt flag so consensus resumes
                Interlocked.Exchange(ref _casterConsensusHalted, 0);

                // Clear proof generation cache so stale data doesn't persist
                ProofUtility.ClearProofGenerationCache();

                CasterLogUtility.Log(
                    $"FORK-RECOVERY: Self-heal COMPLETE. New lastBlock height={Globals.LastBlock.Height} " +
                    $"hash={Globals.LastBlock.Hash?[..Math.Min(16, Globals.LastBlock.Hash?.Length ?? 0)]}. " +
                    $"Resuming normal consensus.",
                    "FORK-RECOVERY");
                ConsoleWriterService.OutputValCaster(
                    $"[FORK-RECOVERY] Self-heal complete. Now at height {Globals.LastBlock.Height}. Resuming consensus.");
            }
            catch (Exception ex)
            {
                CasterLogUtility.Log($"FORK-RECOVERY: Exception during recovery: {ex.Message}", "FORK-RECOVERY");
                // Even on failure, reset the halt flag so the node doesn't stay permanently halted
                Interlocked.Exchange(ref _casterConsensusHalted, 0);
            }
            finally
            {
                Interlocked.Exchange(ref _forkRecoveryInProgress, 0);
            }
        }

        private static async Task HaltConsensusForFetchFailuresAndReconcileAsync()
        {
            if (Interlocked.Exchange(ref _casterConsensusHalted, 1) != 0)
                return;
            CasterRoundAudit?.AddStep($"[BlockFetch] Majority block fetch failures ≥{BLOCK_FETCH_FAIL_HALT_THRESHOLD} — halting caster commit path and reconciling.", false);
            ConsoleWriterService.OutputValCaster($"[CasterConsensus] Halted due to repeated majority block fetch failures; syncing with peers.");
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
            ConsoleWriterService.OutputValCaster($"[BlockHashAgreement] Our block hash differs from majority. Fetching correct block from peer.");
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
                    // Skip self — we already self-injected our own proof
                    if (validator.ValidatorAddress == Globals.ValidatorAddress)
                        return;

                    // Skip if we already have this peer's proof
                    if (Globals.CasterProofDict.ContainsKey(validator.PeerIP))
                        return;

                    try
                    {
                        using var client = Globals.HttpClientFactory.CreateClient();
                        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(CASTER_VOTE_WINDOW));
                        var cleanIP = validator.PeerIP.Replace("::ffff:", "");
                        var uri = $"http://{cleanIP}:{Globals.ValAPIPort}/valapi/validator/SendWinningProof/{proof.BlockHeight}";
                        CasterLogUtility.Log($"ProofFetch → {cleanIP} height={proof.BlockHeight}", "PROOFDIAG");
                        var response = await client.GetAsync(uri, cts.Token);
                        var responseJson = await response.Content.ReadAsStringAsync();
                        CasterLogUtility.Log($"ProofFetch ← {cleanIP} status={response.StatusCode} body={responseJson?.Substring(0, Math.Min(responseJson?.Length ?? 0, 120))}", "PROOFDIAG");
                        if (response.IsSuccessStatusCode)
                        {
                            if (responseJson != null && responseJson != "0" && responseJson != "\"0\"")
                            {
                                var remoteCasterProof = JsonConvert.DeserializeObject<Proof>(responseJson);
                                if (remoteCasterProof != null && remoteCasterProof.VerifyProof())
                                {
                                    Globals.CasterProofDict.TryAdd(validator.PeerIP, remoteCasterProof);
                                    CasterLogUtility.Log($"ProofFetch ACCEPTED from {cleanIP} addr={remoteCasterProof.Address} VRF={remoteCasterProof.VRFNumber}", "PROOFDIAG");
                                }
                                else
                                {
                                    CasterLogUtility.Log($"ProofFetch VERIFY-FAIL from {cleanIP}", "PROOFDIAG");
                                }
                            }
                        }
                    }
                    catch (TaskCanceledException)
                    {
                        CasterLogUtility.Log($"ProofFetch TIMEOUT from {validator.PeerIP.Replace("::ffff:", "")} after {CASTER_VOTE_WINDOW}ms", "PROOFDIAG");
                    }
                    catch (Exception ex)
                    {
                        CasterLogUtility.Log($"ProofFetch ERROR from {validator.PeerIP.Replace("::ffff:", "")}: {ex.Message}", "PROOFDIAG");
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
                    ConsoleWriterService.OutputValCaster($"Warning: Could not remove block {block.Key} from CasterApprovedBlockHashDict");
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
                    ConsoleWriterService.OutputValCaster($"Warning: Could not remove round {round.Key} from CasterRoundDict");
                }
            }
        }

        #endregion

        #region Round Timing Helpers

        /// <summary>
        /// ROUND-SYNC-FIX: Resets the adaptive timing reference points so all casters
        /// that commit the same block at roughly the same time will compute similar
        /// BlockDelay values for the next round. Called on block commit (both local
        /// crafting via CommitCasterBlockPostAgreementAsync and remote receipt via
        /// ReceiveConfirmedBlock message-7).
        /// </summary>
        private static void ResetRoundTiming(long committedHeight, bool force = false)
        {
            // EPOCH-TIMING: Only reset the reference point at epoch boundaries (every EPOCH_SIZE blocks)
            // or when forced (caster pool changes, desync recovery). This lets the adaptive timing
            // accumulate drift corrections across multiple blocks instead of losing them every block.
            // Example: if block N took 15s (3s over), the correction for block N+1 will be -3s,
            // shortening its delay to compensate. Over an epoch, the average converges to BlockTime.
            if (!force && ReferenceHeight != -1 && (committedHeight - ReferenceHeight) < EPOCH_SIZE)
            {
                CasterLogUtility.Log(
                    $"ROUND-SYNC: Epoch hold — refH={ReferenceHeight} committed={committedHeight} (epoch resets at {ReferenceHeight + EPOCH_SIZE})",
                    "ROUND-SYNC");
                return;
            }

            ReferenceHeight = committedHeight;
            ReferenceTime = TimeUtil.GetMillisecondTime();
            CasterLogUtility.Log(
                $"ROUND-SYNC: Epoch RESET timing references — refH={ReferenceHeight} refT={ReferenceTime} force={force}",
                "ROUND-SYNC");
        }

        /// <summary>
        /// ROUND-SYNC-FIX: Computes an initial BlockDelay based on the last block's
        /// timestamp instead of returning Task.CompletedTask. This prevents a newly-joined
        /// caster from racing ahead on its first round while existing casters are still
        /// mid-round with full block-time delays.
        /// </summary>
        private static Task ComputeInitialBlockDelay()
        {
            var currentTime = TimeUtil.GetMillisecondTime();
            var lastBlockTime = Globals.LastBlock.Timestamp;
            var elapsed = currentTime - lastBlockTime;
            var remaining = Globals.BlockTime - elapsed;

            if (remaining > 0)
            {
                CasterLogUtility.Log(
                    $"ROUND-SYNC: Initial BlockDelay={remaining}ms (elapsed={elapsed}ms since last block)",
                    "ROUND-SYNC");
                return Task.Delay((int)Math.Min(remaining, Globals.BlockTimeMax));
            }

            CasterLogUtility.Log(
                $"ROUND-SYNC: Initial BlockDelay=0ms (elapsed={elapsed}ms >= BlockTime={Globals.BlockTime}ms)",
                "ROUND-SYNC");
            return Task.CompletedTask;
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
