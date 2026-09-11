using Newtonsoft.Json;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Services
{
    /// <summary>
    /// Wave 5 + STALL-RESOLVE: hash-based fork detection AND stall-driven fork resolution for
    /// ALL node roles. Compares the hash at our COMMITTED tip height against peers, so normal
    /// production delays can never trigger it.
    ///
    /// Normal act conditions (unchanged): a strict majority of responders reports a DIFFERENT
    /// hash at our committed height across 2 consecutive probes → find divergence → fork-choice
    /// rule → losing means reorg.
    ///
    /// STALL-RESOLVE additions (testnet Sep 2026: four validators stranded on a dead branch at
    /// 912,064 for ~2 days):
    ///  • Stall trigger — tip unchanged for STALL_SECONDS while peers are ahead. When stalled the
    ///    persistence requirement is waived, the probe fans out to EVERY known peer, and the vote
    ///    is caster-weighted (casters produce the chain; a caster majority is decisive on its own).
    ///  • Unbounded divergence search — linear for the shallow window, then binary search using
    ///    GetBlockHash (which serves any committed height). No more FORK-TOO-DEEP dead end.
    ///  • Depth-based action — ≤ MAX_REORG_DEPTH: bounded reorg. Deeper: anchor-verified snapshot
    ///    restore below the divergence + redownload (ForkRecoveryUtility.DeepReorgAsync). Both
    ///    fetch the winning block through the now-unwindowed GetBlock, and both download ONLY from
    ///    peers holding the majority hash (RecoverySourcePolicy) so fellow stranded nodes cannot
    ///    re-serve the dead branch.
    ///  • Stall override — a stalled node whose branch is more than a reorg window behind a
    ///    confirmed majority follows the majority even if the pure rule says LocalWins.
    ///  • Never terminal — every failed attempt schedules the next on a backoff (10/30/60/120s).
    ///  • Self-reporting — SyncState (Healthy | Stalled | Recovering) feeds ProducerReady, the
    ///    Health endpoint, the explorer notifier (goes silent) and the vBTC heartbeat loop (pauses).
    ///
    /// Every adopted block still goes through BlockValidatorService.ValidateBlock — full header,
    /// proof, signature and per-transaction consensus rules — exactly like a live block.
    /// </summary>
    public static class ForkDetectionService
    {
        private const int RequiredConsecutiveMismatches = 2;

        private static int _running = 0;
        private static int _mismatchStreak = 0;
        private static long _lastMismatchHeight = -1;

        // ── Stall tracking ────────────────────────────────────────────────────
        private static long _observedTipHeight = -1;
        private static string _observedTipHash = "";
        private static DateTime _observedTipSinceUtc = DateTime.UtcNow;
        private static long _stalledAtHeight = -1;
        private static DateTime _nextAttemptUtc = DateTime.MinValue;
        private static int _attempts = 0;

        /// <summary>Surfaced on the Health endpoint: None | Suspected | Resolving | Unresolved.</summary>
        public static string ForkStatusText { get; private set; } = "None";

        /// <summary>Healthy | Stalled | Recovering. Stalled/Recovering = IsStalled.</summary>
        public static string SyncState { get; private set; } = "Healthy";
        public static bool IsStalled => SyncState != "Healthy";
        public static long StallSeconds => IsStalled ? (long)(DateTime.UtcNow - _observedTipSinceUtc).TotalSeconds : 0;
        public static long StalledAtHeight => IsStalled ? _stalledAtHeight : -1;
        public static long KnownDivergenceHeight { get; private set; } = -1;
        public static int RecoveryAttempts => _attempts;
        public static DateTime? LastRecoveryAttemptUtc { get; private set; }
        public static string LastRecoveryResult { get; private set; } = "";

        public static async Task CheckAsync(string caller)
        {
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
                return;
            try
            {
                var myTip = Globals.LastBlock;
                if (myTip == null || myTip.Height <= 0 || string.IsNullOrEmpty(myTip.Hash))
                    return;

                // Tip bookkeeping runs before the writer guards so the stall clock and the
                // "recovered" transition are observed even while a recovery is finishing.
                TrackTip(myTip);

                if (Globals.IsResyncing || ForkRecoveryUtility.IsRecoveryInProgress
                    || SnapshotRestoreUtility.IsRestoreRunning)
                {
                    LogIfBlockedWhileStuck(myTip);
                    return;
                }

                // STARTUP: IsChainSynced only flips true when the startup download loop finishes,
                // and on a stranded node it never finishes (every block fails PREVHASH). So the
                // unsynced guard is waived once the tip has been still for STALL_SECONDS — a node
                // that is genuinely syncing keeps moving and never meets that. This is what makes
                // "upgrade the stuck node and restart it" sufficient on its own.
                if (!Globals.IsChainSynced && (DateTime.UtcNow - _observedTipSinceUtc).TotalSeconds < ForkResolutionUtility.STALL_SECONDS)
                {
                    LogIfBlockedWhileStuck(myTip);
                    return;
                }

                var peerMaxHeight = await PeerMaxHeightAsync(myTip);
                var stalled = EvaluateStall(myTip, peerMaxHeight);

                var peers = SelectProbePeers();
                if (peers.Count < 2)
                {
                    ResetStreak();
                    return;
                }

                // Probe: what hash does each peer have at OUR committed tip height?
                var results = await ProbeAsync(peers, myTip.Height);
                if (results.Count < 2)
                {
                    ResetStreak();
                    return;
                }

                var verdict = ForkResolutionUtility.ComputeMajority(results, myTip.Hash);
                if (!verdict.IsMinority)
                {
                    // Majority holds our hash: not forked. If stalled we are merely behind —
                    // the height-check loops' GetAllBlocks handles that; state stays visible.
                    ResetStreak();
                    return;
                }

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

                // Persistence is required in the normal (not stalled) case to stay immune to
                // transient views. A stalled node has already waited STALL_SECONDS — act now,
                // subject only to the retry backoff.
                if (!stalled && _mismatchStreak < RequiredConsecutiveMismatches)
                    return;
                if (stalled && DateTime.UtcNow < _nextAttemptUtc)
                    return;

                await ResolveAsync(caller, myTip, verdict, stalled);
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

        // ── Resolution ────────────────────────────────────────────────────────

        private static async Task ResolveAsync(string caller, Block myTip, ForkResolutionUtility.MajorityVerdict verdict, bool stalled)
        {
            _attempts++;
            LastRecoveryAttemptUtc = DateTime.UtcNow;
            _nextAttemptUtc = DateTime.UtcNow.AddSeconds(ForkResolutionUtility.NextBackoffSeconds(_attempts));
            if (stalled)
                SyncState = "Recovering";
            ForkStatusText = "Resolving";

            // Prefer a caster as the reference peer; casters are the producers of the majority branch.
            var casterIps = CasterIps();
            var majorityPeer = verdict.MajorityPeers.OrderByDescending(ip => casterIps.Contains(ip)).First();

            LogUtility.Log(
                $"[{caller}] FORK-RESOLVE: attempt {_attempts} — majority ({verdict.DecidedBy}: {verdict.Differing}/{verdict.Responders}) holds {Short(verdict.MajorityHash)} at h={myTip.Height}, we hold {Short(myTip.Hash)}. stalled={stalled} reference={majorityPeer}.",
                "ForkDetection");

            var divergenceHeight = await ForkResolutionUtility.FindDivergenceHeightAsync(
                h => GetPeerHashAtHeightAsync(majorityPeer, h),
                LocalHashAt,
                myTip.Height);

            if (divergenceHeight < 0)
            {
                ForkStatusText = "Unresolved";
                LastRecoveryResult = "divergence-not-found";
                LogUtility.Log($"[{caller}] FORK-RESOLVE: could not locate a common ancestor with {majorityPeer} (peer unresponsive or foreign chain). Retrying in {ForkResolutionUtility.NextBackoffSeconds(_attempts)}s.", "ForkDetection");
                ResetStreak(keepStatus: true);
                return;
            }
            KnownDivergenceHeight = divergenceHeight;
            var depth = myTip.Height - divergenceHeight + 1;

            // Source policy goes up BEFORE the block fetch: it dials the majority peers over P2P
            // (a stranded node often has no P2P link to any of them) and quarantines peers that
            // hold our losing hash, so the P2P fallback fetch and the redownload only ever talk
            // to the majority. Cleared on every exit below.
            RecoverySourcePolicy.Set(verdict.MajorityPeers, verdict.AgreeingPeers);
            try
            {
                await RecoverySourcePolicy.EnsureSourcesConnectedAsync();

                var localBlock = BlockchainData.GetBlockByHeight(divergenceHeight);
                var remoteHashAtDivergence = await GetPeerHashAtHeightAsync(majorityPeer, divergenceHeight);
                var remoteBlock = await ForkRecoveryUtility.FetchCommittedBlockAsync(majorityPeer, divergenceHeight);
                if (localBlock == null || remoteBlock == null || remoteBlock.Height != divergenceHeight
                    || string.IsNullOrEmpty(remoteBlock.Hash) || remoteBlock.Hash != remoteHashAtDivergence)
                {
                    ForkStatusText = "Unresolved";
                    LastRecoveryResult = "remote-block-unavailable";
                    LogUtility.Log($"[{caller}] FORK-RESOLVE: could not obtain a block at divergence h={divergenceHeight} matching {majorityPeer}'s hash {Short(remoteHashAtDivergence)} (depth {depth}; API + P2P both tried). Retrying in {ForkResolutionUtility.NextBackoffSeconds(_attempts)}s.", "ForkDetection");
                    ResetStreak(keepStatus: true);
                    return;
                }

                await ResolveWithBlocksAsync(caller, myTip, verdict, stalled, majorityPeer, divergenceHeight, depth, localBlock, remoteBlock);
            }
            finally
            {
                RecoverySourcePolicy.Clear();
            }
        }

        private static async Task ResolveWithBlocksAsync(string caller, Block myTip, ForkResolutionUtility.MajorityVerdict verdict, bool stalled,
            string majorityPeer, long divergenceHeight, long depth, Block localBlock, Block remoteBlock)
        {
            var remoteTip = await GetPeerTipHeightAsync(majorityPeer) ?? myTip.Height;
            var local = new ForkChoiceUtility.BranchSummary
            {
                TipHeight = myTip.Height,
                HashAtDivergence = localBlock.Hash ?? "",
                CertWeight = ForkChoiceUtility.CountValidCertWeight(localBlock),
                CertRuleApplicable = ForkChoiceUtility.IsCertRuleApplicable(divergenceHeight, localBlock.Version)
            };
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
                if (!ForkResolutionUtility.ShouldOverrideLocalWin(stalled, myTip.Height, remoteTip))
                {
                    LogUtility.Log(
                        $"[{caller}] FORK-CHOICE: LocalWins at divergence h={divergenceHeight} (local cert={local.CertWeight} h={local.TipHeight} hash={Short(local.HashAtDivergence)} vs remote cert={remote.CertWeight} h={remote.TipHeight}). Serving — peers self-correct.",
                        "ForkDetection");
                    LastRecoveryResult = "local-wins";
                    ResetStreak();
                    return;
                }
                LogUtility.Log(
                    $"[{caller}] STALL-OVERRIDE: rule says LocalWins at h={divergenceHeight} but this node is stalled at {myTip.Height} while the majority is at {remoteTip}. A branch nobody extends is dead — following the majority.",
                    "ForkDetection");
            }

            LogUtility.Log(
                $"[{caller}] FORK-CHOICE: RemoteWins at divergence h={divergenceHeight} (depth {depth}) — adopting branch of {majorityPeer} (expected {Short(remote.HashAtDivergence)}). Sources: {verdict.MajorityPeers.Count} allowed, {verdict.AgreeingPeers.Count} quarantined.",
                "ForkDetection");
            ConsoleWriterService.Output($"[ForkDetection] Losing branch detected — adopting majority branch from divergence height {divergenceHeight} (depth {depth}).");

            // RecoverySourcePolicy is already active (set by the caller, cleared by the caller).
            var ok = depth <= ForkChoiceUtility.MAX_REORG_DEPTH
                ? await ForkRecoveryUtility.ReorgToBranchAsync(divergenceHeight, remote.HashAtDivergence, majorityPeer, caller)
                : await ForkRecoveryUtility.DeepReorgAsync(divergenceHeight, remote.HashAtDivergence, majorityPeer, caller);

            LastRecoveryResult = ok ? "reorg-complete" : "reorg-incomplete";
            if (ok)
            {
                ForkRecoveryUtility.ResetEscalation();
                ResetStreak();
                ForkStatusText = "None";
                // SyncState flips to Healthy in TrackTip once the tip has moved past the stall point.
            }
            else
            {
                ForkStatusText = "Unresolved";
                LogUtility.Log($"[{caller}] FORK-RESOLVE: attempt {_attempts} did not adopt the majority branch. Retrying in {ForkResolutionUtility.NextBackoffSeconds(_attempts)}s.", "ForkDetection");
            }
        }

        // ── Stall bookkeeping ─────────────────────────────────────────────────

        private static void TrackTip(Block myTip)
        {
            if (myTip.Height != _observedTipHeight || !string.Equals(myTip.Hash, _observedTipHash, StringComparison.Ordinal))
            {
                var advancedPastStall = IsStalled && myTip.Height > _stalledAtHeight;
                _observedTipHeight = myTip.Height;
                _observedTipHash = myTip.Hash ?? "";
                _observedTipSinceUtc = DateTime.UtcNow;
                if (advancedPastStall)
                    MarkHealthy(myTip.Height);
            }
        }

        private static bool EvaluateStall(Block myTip, long peerMaxHeight)
        {
            if (IsStalled)
                return true;
            var stalledFor = (DateTime.UtcNow - _observedTipSinceUtc).TotalSeconds;
            if (stalledFor < ForkResolutionUtility.STALL_SECONDS)
                return false;
            if (peerMaxHeight < myTip.Height + ForkResolutionUtility.STALL_MIN_PEER_LEAD)
                return false;

            SyncState = "Stalled";
            _stalledAtHeight = myTip.Height;
            _attempts = 0;
            _nextAttemptUtc = DateTime.MinValue;
            LastRecoveryResult = "";
            LogUtility.Log($"SYNC-STALL: tip {myTip.Height} ({Short(myTip.Hash)}) unchanged for {(int)stalledFor}s while peers report {peerMaxHeight}. Entering stall resolution; producer readiness, explorer check-ins and heartbeats are suspended.", "ForkDetection");
            ConsoleWriterService.Output($"[Sync] STALLED at height {myTip.Height} for {(int)stalledFor}s — peers are at {peerMaxHeight}. Resolving automatically…");
            return true;
        }

        private static void MarkHealthy(long height)
        {
            var wasState = SyncState;
            SyncState = "Healthy";
            _stalledAtHeight = -1;
            _attempts = 0;
            _nextAttemptUtc = DateTime.MinValue;
            KnownDivergenceHeight = -1;
            if (ForkStatusText == "Resolving" || ForkStatusText == "Unresolved")
                ForkStatusText = "None";
            LogUtility.Log($"SYNC-RECOVERED: chain advancing again at height {height} (was {wasState}). Producer readiness, explorer check-ins and heartbeats resume.", "ForkDetection");
            ConsoleWriterService.Output($"[Sync] Recovered — chain advancing again at height {height}.");
        }

        private static DateTime _lastBlockedLogUtc = DateTime.MinValue;

        /// <summary>
        /// The writer guards are correct, but a node whose tip has not moved for a long time
        /// while they stay set is worth a loud, rate-limited log — that is exactly how a hung
        /// recovery (e.g. waiting on the download semaphore) used to hide for days.
        /// </summary>
        private static void LogIfBlockedWhileStuck(Block myTip)
        {
            var unchangedFor = (DateTime.UtcNow - _observedTipSinceUtc).TotalSeconds;
            if (unchangedFor < ForkResolutionUtility.STALL_SECONDS * 4)
                return;
            if ((DateTime.UtcNow - _lastBlockedLogUtc).TotalMinutes < 5)
                return;
            _lastBlockedLogUtc = DateTime.UtcNow;
            LogUtility.Log(
                $"SYNC-STALL-BLOCKED: tip {myTip.Height} unchanged for {(int)unchangedFor}s but fork detection is held off by " +
                $"IsResyncing={Globals.IsResyncing} RecoveryInProgress={ForkRecoveryUtility.IsRecoveryInProgress} RestoreRunning={SnapshotRestoreUtility.IsRestoreRunning} IsChainSynced={Globals.IsChainSynced}.",
                "ForkDetection");
        }

        /// <summary>
        /// Highest height any known peer reports. Node tables are the cheap source; when the tip
        /// has been still for STALL_SECONDS and the tables show no lead (a stranded node whose
        /// only P2P links are fellow stranded nodes sees exactly that), ask the casters directly.
        /// </summary>
        private static async Task<long> PeerMaxHeightAsync(Block myTip)
        {
            long max = -1;
            foreach (var n in Globals.Nodes.Values) if (n.NodeHeight > max) max = n.NodeHeight;
            foreach (var n in Globals.ValidatorNodes.Values) if (n.NodeHeight > max) max = n.NodeHeight;
            foreach (var n in Globals.BlockCasterNodes.Values) if (n.NodeHeight > max) max = n.NodeHeight;

            var stillFor = (DateTime.UtcNow - _observedTipSinceUtc).TotalSeconds;
            if (!IsStalled && stillFor >= ForkResolutionUtility.STALL_SECONDS && max < myTip.Height + ForkResolutionUtility.STALL_MIN_PEER_LEAD)
            {
                var casters = CasterIps().Take(ForkResolutionUtility.MAX_PROBE_PEERS).ToList();
                if (casters.Count > 0)
                {
                    var tips = await Task.WhenAll(casters.Select(GetPeerTipHeightAsync));
                    foreach (var t in tips) if (t.HasValue && t.Value > max) max = t.Value;
                }
            }
            return max;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>Pure (unit-tested): strict majority — differing responders must exceed half.</summary>
        public static bool IsStrictMajority(int differingCount, int totalResponders)
            => ForkResolutionUtility.IsStrictMajority(differingCount, totalResponders);

        private static void ResetStreak(bool keepStatus = false)
        {
            _mismatchStreak = 0;
            _lastMismatchHeight = -1;
            if (!keepStatus)
                ForkStatusText = "None";
        }

        private static string Short(string? hash) => string.IsNullOrEmpty(hash) ? "" : hash[..Math.Min(12, hash.Length)];

        private static string? LocalHashAt(long h)
            => h == Globals.LastBlock.Height ? Globals.LastBlock.Hash : BlockchainData.GetBlockByHeight(h)?.Hash;

        private static HashSet<string> CasterIps()
            => Globals.BlockCasters.ToList()
                .Select(c => RecoverySourcePolicy.Normalize(c.PeerIP))
                .Where(ip => ip.Length > 0)
                .ToHashSet();

        /// <summary>
        /// STALL-RESOLVE: every known peer, casters first, capped at MAX_PROBE_PEERS. The old
        /// cap of 8 could be filled entirely by fellow stranded peers.
        /// </summary>
        private static List<(string Ip, bool IsCaster)> SelectProbePeers()
        {
            var casters = CasterIps();
            return casters.Select(ip => (Ip: ip, IsCaster: true))
                .Concat(Globals.BlockCasterNodes.Values.Select(n => (Ip: RecoverySourcePolicy.Normalize(n.NodeIP), IsCaster: true)))
                .Concat(Globals.ValidatorNodes.Values.Select(n => (Ip: RecoverySourcePolicy.Normalize(n.NodeIP), IsCaster: casters.Contains(RecoverySourcePolicy.Normalize(n.NodeIP)))))
                .Concat(Globals.Nodes.Values.Select(n => (Ip: RecoverySourcePolicy.Normalize(n.NodeIP), IsCaster: casters.Contains(RecoverySourcePolicy.Normalize(n.NodeIP)))))
                .Where(p => p.Ip.Length > 0)
                .GroupBy(p => p.Ip)
                .Select(g => (Ip: g.Key, IsCaster: g.Any(p => p.IsCaster)))
                .OrderByDescending(p => p.IsCaster)
                .Take(ForkResolutionUtility.MAX_PROBE_PEERS)
                .ToList();
        }

        private static async Task<List<ForkResolutionUtility.ProbeResult>> ProbeAsync(List<(string Ip, bool IsCaster)> peers, long height)
        {
            var tasks = peers.Select(async p =>
            {
                var hash = await GetPeerHashAtHeightAsync(p.Ip, height);
                return new ForkResolutionUtility.ProbeResult { Ip = p.Ip, Hash = hash ?? "", IsCaster = p.IsCaster };
            }).ToList();
            var all = await Task.WhenAll(tasks);
            return all.Where(r => !string.IsNullOrEmpty(r.Hash) && r.Hash != "0").ToList();
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

    }
}
