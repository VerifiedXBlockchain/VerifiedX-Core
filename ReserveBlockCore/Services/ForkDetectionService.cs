using Newtonsoft.Json;
using ReserveBlockCore.Data;
using ReserveBlockCore.Models;
using ReserveBlockCore.Utilities;

namespace ReserveBlockCore.Services
{
    /// <summary>
    /// Wave 5: hash-based fork detection for ALL node roles (the old disabled loop was
    /// time-based and false-positive prone — this compares the hash at our COMMITTED tip
    /// height against peers, so normal production delays can never trigger it).
    ///
    /// Act conditions: a strict majority of responders reports a DIFFERENT hash at our own
    /// committed height, persisting across 2 consecutive probes. Then a bounded divergence
    /// search (≤ MAX_REORG_DEPTH) finds the fork point, the pure fork-choice rule decides,
    /// and losing means a bounded reorg via ForkRecoveryUtility.ReorgToBranchAsync.
    /// LocalWins = serve, don't reorg (peers self-correct via their own detectors).
    /// </summary>
    public static class ForkDetectionService
    {
        private const int MaxPeersToProbe = 8;
        private const int RequiredConsecutiveMismatches = 2;

        private static int _running = 0;
        private static int _mismatchStreak = 0;
        private static long _lastMismatchHeight = -1;

        /// <summary>Surfaced on the Health endpoint: None | Suspected | TooDeep.</summary>
        public static string ForkStatusText { get; private set; } = "None";

        public static async Task CheckAsync(string caller)
        {
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
                return;
            try
            {
                if (Globals.IsResyncing || ForkRecoveryUtility.IsRecoveryInProgress
                    || SnapshotRestoreUtility.IsRestoreRunning || !Globals.IsChainSynced)
                    return;

                var myTip = Globals.LastBlock;
                if (myTip == null || myTip.Height <= 0 || string.IsNullOrEmpty(myTip.Hash))
                    return;

                var peers = SelectProbePeers();
                if (peers.Count < 2)
                {
                    ResetStreak();
                    return;
                }

                // Probe: what hash does each peer have at OUR committed tip height?
                var results = new List<(string Ip, string Hash)>();
                foreach (var ip in peers)
                {
                    var hash = await GetPeerHashAtHeightAsync(ip, myTip.Height);
                    if (!string.IsNullOrEmpty(hash) && hash != "0")
                        results.Add((ip, hash!));
                }

                if (results.Count < 2)
                {
                    ResetStreak();
                    return;
                }

                var differing = results.Where(r => r.Hash != myTip.Hash).ToList();
                if (!IsStrictMajority(differing.Count, results.Count))
                {
                    ResetStreak();
                    return;
                }

                // Majority disagreement at our committed height — require persistence.
                if (_lastMismatchHeight != myTip.Height)
                {
                    _lastMismatchHeight = myTip.Height;
                    _mismatchStreak = 1;
                }
                else
                {
                    _mismatchStreak++;
                }
                ForkStatusText = "Suspected";

                if (_mismatchStreak < RequiredConsecutiveMismatches)
                    return;

                // Persistent split confirmed. Find the divergence point against one majority peer.
                var majorityHash = differing.GroupBy(d => d.Hash).OrderByDescending(g => g.Count()).First().Key;
                var majorityPeer = differing.First(d => d.Hash == majorityHash).Ip;

                var divergenceHeight = await FindDivergenceHeightAsync(majorityPeer, myTip.Height);
                if (divergenceHeight < 0)
                {
                    ForkStatusText = "TooDeep";
                    LogUtility.Log(
                        $"[{caller}] FORK-TOO-DEEP: majority hash mismatch at h={myTip.Height} but no common ancestor within {ForkChoiceUtility.MAX_REORG_DEPTH} blocks. NO auto-reorg — operator playbook required (snapshot-anchor restore).",
                        "ForkDetection");
                    ConsoleWriterService.Output($"[ForkDetection] FORK-TOO-DEEP at height {myTip.Height} — manual resolution required (see runbook).");
                    ResetStreak(keepStatus: true);
                    return;
                }

                // Build branch summaries at the divergence height.
                var localBlock = BlockchainData.GetBlockByHeight(divergenceHeight);
                var remoteBlock = await GetPeerBlockAtHeightAsync(majorityPeer, divergenceHeight);
                if (localBlock == null || remoteBlock == null || remoteBlock.Height != divergenceHeight)
                {
                    ResetStreak();
                    return;
                }

                var local = new ForkChoiceUtility.BranchSummary
                {
                    TipHeight = myTip.Height,
                    HashAtDivergence = localBlock.Hash ?? "",
                    CertWeight = ForkChoiceUtility.CountValidCertWeight(localBlock),
                    CertRuleApplicable = ForkChoiceUtility.IsCertRuleApplicable(divergenceHeight, localBlock.Version)
                };
                var remoteTip = await GetPeerTipHeightAsync(majorityPeer) ?? myTip.Height;
                var remote = new ForkChoiceUtility.BranchSummary
                {
                    TipHeight = remoteTip,
                    HashAtDivergence = remoteBlock.Hash ?? "",
                    CertWeight = ForkChoiceUtility.CountValidCertWeight(remoteBlock),
                    CertRuleApplicable = ForkChoiceUtility.IsCertRuleApplicable(divergenceHeight, remoteBlock.Version)
                };

                var outcome = ForkChoiceUtility.CompareBranches(local, remote);
                if (outcome == ForkChoiceUtility.Outcome.LocalWins)
                {
                    LogUtility.Log(
                        $"[{caller}] FORK-CHOICE: LocalWins at divergence h={divergenceHeight} (local cert={local.CertWeight} h={local.TipHeight} hash={local.HashAtDivergence[..Math.Min(12, local.HashAtDivergence.Length)]} vs remote cert={remote.CertWeight} h={remote.TipHeight}). Serving — peers self-correct.",
                        "ForkDetection");
                    ResetStreak();
                    return;
                }

                LogUtility.Log(
                    $"[{caller}] FORK-CHOICE: RemoteWins at divergence h={divergenceHeight} — reorging to branch of {majorityPeer} (expected hash {remote.HashAtDivergence[..Math.Min(12, remote.HashAtDivergence.Length)]}…).",
                    "ForkDetection");
                ConsoleWriterService.Output($"[ForkDetection] Losing branch detected — reorging to divergence height {divergenceHeight} from {majorityPeer}.");

                var reorged = await ForkRecoveryUtility.ReorgToBranchAsync(divergenceHeight, remote.HashAtDivergence, majorityPeer, caller);
                if (reorged)
                    ResetStreak();
            }
            catch (Exception ex)
            {
                LogUtility.Log($"ForkDetection error: {ex.Message}", "ForkDetection");
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        }

        /// <summary>Pure (unit-tested): strict majority — differing responders must exceed half.</summary>
        public static bool IsStrictMajority(int differingCount, int totalResponders)
            => totalResponders > 0 && differingCount * 2 > totalResponders;

        private static void ResetStreak(bool keepStatus = false)
        {
            _mismatchStreak = 0;
            _lastMismatchHeight = -1;
            if (!keepStatus)
                ForkStatusText = "None";
        }

        private static List<string> SelectProbePeers()
        {
            return Globals.BlockCasters.ToList().Select(c => c.PeerIP)
                .Concat(Globals.ValidatorNodes.Values.Select(n => n.NodeIP))
                .Concat(Globals.Nodes.Values.Select(n => n.NodeIP))
                .Where(ip => !string.IsNullOrEmpty(ip))
                .Select(ip => ip!.Replace("::ffff:", ""))
                .Distinct()
                .Take(MaxPeersToProbe)
                .ToList();
        }

        private static async Task<string?> GetPeerHashAtHeightAsync(string ip, long height)
        {
            try
            {
                using var client = Globals.HttpClientFactory.CreateClient();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var uri = $"http://{ip}:{Globals.ValAPIPort}/valapi/validator/GetBlockHash/{height}";
                var resp = await client.GetAsync(uri, cts.Token);
                if (!resp.IsSuccessStatusCode) return null;
                var body = await resp.Content.ReadAsStringAsync();
                if (string.IsNullOrEmpty(body) || body == "0") return null;
                var parsed = JsonConvert.DeserializeAnonymousType(body, new { Hash = "", Validator = "", Height = 0L });
                return parsed?.Hash;
            }
            catch { return null; }
        }

        private static async Task<long?> GetPeerTipHeightAsync(string ip)
        {
            try
            {
                using var client = Globals.HttpClientFactory.CreateClient();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var uri = $"http://{ip}:{Globals.ValAPIPort}/valapi/validator/GetBlockHeight";
                var resp = await client.GetAsync(uri, cts.Token);
                if (!resp.IsSuccessStatusCode) return null;
                var body = (await resp.Content.ReadAsStringAsync())?.Trim().Trim('"');
                return long.TryParse(body, out var h) ? h : null;
            }
            catch { return null; }
        }

        private static async Task<Block?> GetPeerBlockAtHeightAsync(string ip, long height)
        {
            try
            {
                using var client = Globals.HttpClientFactory.CreateClient();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                var uri = $"http://{ip}:{Globals.ValAPIPort}/valapi/validator/GetBlock/{height}";
                var resp = await client.GetAsync(uri, cts.Token);
                if (!resp.IsSuccessStatusCode) return null;
                var body = await resp.Content.ReadAsStringAsync();
                if (string.IsNullOrEmpty(body) || body == "0") return null;
                return JsonConvert.DeserializeObject<Block>(body);
            }
            catch { return null; }
        }

        /// <summary>
        /// Walks downward from the tip comparing local vs peer hashes, capped at
        /// MAX_REORG_DEPTH. Returns the first divergent height (common ancestor + 1),
        /// or -1 when no common ancestor exists within the bound (FORK-TOO-DEEP).
        /// </summary>
        private static async Task<long> FindDivergenceHeightAsync(string peerIp, long tipHeight)
        {
            long divergence = -1;
            for (var h = tipHeight; h > tipHeight - ForkChoiceUtility.MAX_REORG_DEPTH && h > 0; h--)
            {
                var peerHash = await GetPeerHashAtHeightAsync(peerIp, h);
                if (string.IsNullOrEmpty(peerHash))
                    return -1; // peer can't answer — don't act on partial data
                var localHash = h == Globals.LastBlock.Height ? Globals.LastBlock.Hash : BlockchainData.GetBlockByHeight(h)?.Hash;
                if (string.IsNullOrEmpty(localHash))
                    return -1;
                if (peerHash == localHash)
                    return divergence; // common ancestor found; divergence = h+1 (tracked below)
                divergence = h;
            }
            // Bottom of the window reached while still divergent: check one more height for the ancestor.
            var ancestorHeight = tipHeight - ForkChoiceUtility.MAX_REORG_DEPTH;
            if (divergence > 0 && ancestorHeight >= 0)
            {
                var peerHash = await GetPeerHashAtHeightAsync(peerIp, ancestorHeight);
                var localHash = BlockchainData.GetBlockByHeight(ancestorHeight)?.Hash;
                if (!string.IsNullOrEmpty(peerHash) && peerHash == localHash)
                    return divergence;
            }
            return -1; // divergence deeper than the bound
        }
    }
}
