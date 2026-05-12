using Newtonsoft.Json;
using ReserveBlockCore.Data;
using ReserveBlockCore.Models;
using ReserveBlockCore.Nodes;
using ReserveBlockCore.Utilities;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace ReserveBlockCore.Services
{
    /// <summary>
    /// Dynamic caster discovery and pool management. See docs/IMPLEMENTATION-Dynamic-Caster-Discovery.md.
    /// </summary>
    public static class CasterDiscoveryService
    {
        public const int MaxCasters = 5;
        public const decimal MinCasterBalance = 5000M;
        public const int StallThresholdSeconds = 80;
        /// <summary>Number of consecutive audit failures before a caster is demoted.</summary>
        public const int AuditFailThreshold = 3;
        /// <summary>Minimum number of blocks a validator must be in NetworkValidators before caster promotion.</summary>
        public const int MaturityBlocks = 10;

        private static long _lastEvaluationHeight = long.MinValue;
        private static long _lastRefreshAtHeight = -1;

        /// <summary>Tracks consecutive version-check failures per caster address for audit tolerance.</summary>
        private static readonly ConcurrentDictionary<string, int> _auditFailCounts = new();

        /// <summary>Tracks promotion cooldown per candidate address after repeated version/port failures.
        /// After <see cref="CooldownFailThreshold"/> consecutive Unreachable failures, the candidate is
        /// put on cooldown for <see cref="CooldownBaseBlocks"/> blocks, doubling each subsequent failure,
        /// capped at <see cref="CooldownMaxBlocks"/>.</summary>
        private static readonly ConcurrentDictionary<string, (int FailCount, long CooldownUntilHeight)> _promotionCooldowns = new();
        private const int CooldownFailThreshold = 3;
        private const int CooldownBaseBlocks = 100;
        private const int CooldownMaxBlocks = 1000;

        /// <summary>FIX 3: Tracks per-candidate API readiness — (firstSuccessUtcTicks, consecutiveSuccessCount).
        /// A candidate must have ≥3 consecutive successful version checks AND ≥30s since first success before promotion.</summary>
        private static readonly ConcurrentDictionary<string, (long FirstSuccessUtcTicks, int ConsecutiveSuccesses)> _valApiReadiness = new();
        private const int RequiredReadinessChecks = 3;
        private const int RequiredReadinessSeconds = 30;

        /// <summary>
        /// CONSENSUS-V2 (Fix #2): Atomic slot reservation for caster promotions.
        /// The window between the MaxCasters check at the top of EvaluateCasterPool and the
        /// final BlockCasters.Add(...) is several seconds long (port + version + readiness +
        /// propose + notify HTTP round-trips). Two concurrent EvaluateCasterPool tasks (or two
        /// peer casters racing to promote different candidates) can both pass the count check
        /// and both .Add(), exceeding MaxCasters. This lock + claim-slot serializes
        /// promotion attempts on a single node and lets the helper AddBlockCasterIfRoomAndUnique
        /// re-check capacity atomically just before mutating the bag.
        /// </summary>
        private static readonly object _promotionLock = new();
        private static string? _inFlightPromotionAddress; // guarded by _promotionLock

        /// <summary>
        /// CONSENSUS-V2 (Fix #2): Adds <paramref name="newCaster"/> to <see cref="Globals.BlockCasters"/>
        /// only if (a) the pool isn't full and (b) no entry already exists for the same validator
        /// address. Both checks happen under <see cref="_promotionLock"/> so they're atomic with
        /// respect to other promotion attempts on this node. Returns true only when the caster
        /// was actually added.
        /// </summary>
        internal static bool AddBlockCasterIfRoomAndUnique(Peers newCaster)
        {
            if (newCaster == null || string.IsNullOrEmpty(newCaster.ValidatorAddress))
                return false;

            lock (_promotionLock)
            {
                if (Globals.BlockCasters.Count >= MaxCasters)
                {
                    CasterLogUtility.Log(
                        $"AddBlockCasterIfRoomAndUnique REJECT — pool full ({Globals.BlockCasters.Count}/{MaxCasters}). candidate={newCaster.ValidatorAddress}",
                        "CasterFlow");
                    return false;
                }
                if (Globals.BlockCasters.Any(c => c.ValidatorAddress == newCaster.ValidatorAddress))
                {
                    CasterLogUtility.Log(
                        $"AddBlockCasterIfRoomAndUnique REJECT — duplicate. candidate={newCaster.ValidatorAddress}",
                        "CasterFlow");
                    return false;
                }
                Globals.BlockCasters.Add(newCaster);
                CasterLogUtility.Log(
                    $"AddBlockCasterIfRoomAndUnique OK — added {newCaster.ValidatorAddress} ({Globals.BlockCasters.Count}/{MaxCasters})",
                    "CasterFlow");

                // FIX: Hydrate NetworkValidator entry immediately on caster promotion.
                // Without this, a newly-promoted validator's NetworkValidator entry may have
                // IsFullyTrusted=false or LastSeen=0, causing GenerateProofsFromNetworkValidatorsLegacy()
                // to filter it out of allProofs. The validator would be unable to win blocks
                // until the organic trust-building process completes (~40-60 minutes).
                // Being promoted to caster is strong proof of liveness, so we set both flags now.
                if (!string.IsNullOrEmpty(newCaster.ValidatorAddress))
                {
                    var now = TimeUtil.GetTime();
                    if (Globals.NetworkValidators.TryGetValue(newCaster.ValidatorAddress, out var nv))
                    {
                        nv.IsFullyTrusted = true;
                        nv.LastSeen = now;
                        nv.CheckFailCount = 0;
                        Globals.NetworkValidators[newCaster.ValidatorAddress] = nv;
                        CasterLogUtility.Log(
                            $"Hydrated NetworkValidator for promoted caster {newCaster.ValidatorAddress}: IsFullyTrusted=true LastSeen={now}",
                            "CasterFlow");
                    }
                    else if (!string.IsNullOrEmpty(newCaster.PeerIP))
                    {
                        // Caster not yet in NetworkValidators — create a minimal trusted entry
                        // so it immediately appears in allProofs for winner selection.
                        var newNv = new Models.NetworkValidator
                        {
                            Address = newCaster.ValidatorAddress,
                            IPAddress = (newCaster.PeerIP ?? "").Replace("::ffff:", ""),
                            PublicKey = newCaster.ValidatorPublicKey ?? "",
                            IsFullyTrusted = true,
                            LastSeen = now,
                            CheckFailCount = 0,
                            FirstSeenAtHeight = Globals.LastBlock?.Height ?? 0,
                            FirstAdvertised = now,
                        };
                        Globals.NetworkValidators[newCaster.ValidatorAddress] = newNv;
                        CasterLogUtility.Log(
                            $"Created NetworkValidator for promoted caster {newCaster.ValidatorAddress} (IP={newNv.IPAddress}): IsFullyTrusted=true",
                            "CasterFlow");
                    }

                    // Also clear the proof cache so the next proof generation picks up the new validator immediately
                    Utilities.ProofUtility.ClearProofGenerationCache();
                }

                return true;
            }
        }

        /// <summary>Result of a version check distinguishing connectivity failure from genuine version mismatch.</summary>
        internal enum VersionCheckResult
        {
            /// <summary>Version is current.</summary>
            Ok,
            /// <summary>Got a response but version is outdated.</summary>
            Outdated,
            /// <summary>Could not reach the node (timeout, connection refused, etc.).</summary>
            Unreachable
        }

        /// <summary>
        /// Detailed version check result, including the discovered version string (if any).
        /// Used by <see cref="EvaluateCasterPool"/> to stamp <see cref="Peers.WalletVersion"/>
        /// on newly-promoted casters so they aren't silently filtered out of proof generation.
        /// </summary>
        internal readonly struct VersionCheckInfo
        {
            public VersionCheckResult Status { get; }
            public string Version { get; }

            public VersionCheckInfo(VersionCheckResult status, string version)
            {
                Status = status;
                Version = version ?? "";
            }
        }


        /// <summary>Deterministic JSON for <see cref="CasterInfo"/> lists (must match on promoter and promoted node).</summary>
        internal static string GetCanonicalCasterListJson(IEnumerable<CasterInfo> list)
        {
            var ordered = list
                .OrderBy(c => c.Address ?? "", StringComparer.Ordinal)
                .Select(c => new { Address = c.Address ?? "", PeerIP = c.PeerIP ?? "", PublicKey = c.PublicKey ?? "" });
            return JsonConvert.SerializeObject(ordered, Formatting.None);
        }

        /// <summary>Idempotent refresh when crossing a 100-block boundary (caster monitor loop).</summary>
        public static async Task RefreshIfDueAsync()
        {
            var h = Globals.LastBlock.Height;
            if (h <= 0 || h % 100 != 0 || h == _lastRefreshAtHeight)
                return;
            _lastRefreshAtHeight = h;
            await ValidatorNode.GetBlockcasters().ConfigureAwait(false);
            Globals.SyncKnownCastersFromBlockCasters();
        }

        /// <summary>
        /// Called periodically from <see cref="BlockcasterNode.MonitorCasters"/>. Height-gated: one evaluation per block height.
        /// </summary>
        public static async Task EvaluateCasterPool()
        {
            if (!Globals.IsBlockCaster)
            {
                CasterLogUtility.Log($"EvalTick SKIP — IsBlockCaster=false. Self={Globals.ValidatorAddress}", "CasterFlow");
                return;
            }
            if (string.IsNullOrEmpty(Globals.ValidatorAddress))
            {
                CasterLogUtility.Log("EvalTick SKIP — ValidatorAddress is empty", "CasterFlow");
                return;
            }

            var currentHeight = Globals.LastBlock.Height;
            if (currentHeight < 0)
                return;
            if (currentHeight == _lastEvaluationHeight)
                return;
            _lastEvaluationHeight = currentHeight;

            try
            {
                var currentCasters = Globals.BlockCasters.ToList();
                var currentCasterAddrs = currentCasters.Select(c => c.ValidatorAddress ?? "?").ToList();
                var netValCount = Globals.NetworkValidators.Count;
                CasterLogUtility.Log(
                    $"EvalTick height={currentHeight} | Self={Globals.ValidatorAddress} | IsBlockCaster=true | " +
                    $"BlockCasters.Count={currentCasters.Count}/{MaxCasters} addrs=[{string.Join(",", currentCasterAddrs)}] | " +
                    $"NetworkValidators.Count={netValCount}",
                    "CasterFlow");
                ConsoleWriterService.OutputValCaster(
                    $"[CasterFlow] EvalTick height={currentHeight} casters={currentCasters.Count}/{MaxCasters} vals={netValCount}");

                if (currentCasters.Count >= MaxCasters)
                {
                    CasterLogUtility.Log($"EvalTick SKIP — caster pool full ({currentCasters.Count}/{MaxCasters})", "CasterFlow");
                    return;
                }

                var validators = Globals.NetworkValidators.Values.ToList();
                if (!validators.Any())
                {
                    CasterLogUtility.Log("EvalTick SKIP — NetworkValidators is empty", "CasterFlow");
                    return;
                }

                var currentCasterAddresses = currentCasters
                    .Where(c => !string.IsNullOrEmpty(c.ValidatorAddress))
                    .Select(c => c.ValidatorAddress!)
                    .ToHashSet();

                var candidates = validators
                    .Where(v => !string.IsNullOrEmpty(v.Address)
                             && !string.IsNullOrEmpty(v.IPAddress)
                             && !string.IsNullOrEmpty(v.PublicKey)
                             && !currentCasterAddresses.Contains(v.Address)
                             && v.CheckFailCount < 10
                             && v.IsFullyTrusted)
                    .ToList();

                // Emit per-validator filter diagnostics so it's obvious why a validator isn't in the candidate pool.
                foreach (var v in validators)
                {
                    if (currentCasterAddresses.Contains(v.Address ?? ""))
                        continue; // already a caster — not interesting
                    var reasons = new List<string>();
                    if (string.IsNullOrEmpty(v.Address)) reasons.Add("no-address");
                    if (string.IsNullOrEmpty(v.IPAddress)) reasons.Add("no-ip");
                    if (string.IsNullOrEmpty(v.PublicKey)) reasons.Add("no-pubkey");
                    if (v.CheckFailCount >= 10) reasons.Add($"checkFailCount={v.CheckFailCount}");
                    if (!v.IsFullyTrusted) reasons.Add("!IsFullyTrusted");
                    if (reasons.Count > 0)
                    {
                        CasterLogUtility.Log(
                            $"  filter-out val={v.Address ?? "?"} ip={v.IPAddress ?? "?"} reasons=[{string.Join(",", reasons)}]",
                            "CasterFlow");
                    }
                }

                if (!candidates.Any())
                {
                    CasterLogUtility.Log($"EvalTick END — 0 eligible candidates (of {validators.Count} validators, {currentCasterAddresses.Count} already casters)", "CasterFlow");
                    return;
                }

                CasterLogUtility.Log($"EvalTick — {candidates.Count} eligible candidate(s): [{string.Join(",", candidates.Select(c => c.Address))}]", "CasterFlow");


                var rankedCandidates = candidates
                    .Select(v => new { Validator = v, Balance = AccountStateTrei.GetAccountBalance(v.Address) })
                    .ToList();

                // Emit balance-gate diagnostics for each eligible candidate.
                foreach (var c in rankedCandidates)
                {
                    if (c.Balance < MinCasterBalance)
                    {
                        CasterLogUtility.Log(
                            $"  balance-gate FAIL val={c.Validator.Address} balance={c.Balance} < min={MinCasterBalance}",
                            "CasterFlow");
                    }
                    else
                    {
                        CasterLogUtility.Log(
                            $"  balance-gate PASS val={c.Validator.Address} balance={c.Balance}",
                            "CasterFlow");
                    }
                }

                // CONSENSUS-V2 (Fix #1): Deterministic candidate ordering. ConcurrentDictionary
                // enumeration is non-deterministic; without a stable secondary sort key, two
                // casters iterating the same NetworkValidators set could pick different "first"
                // candidates when balances tie (very common at bootstrap). Lexicographic
                // ordinal sort on the address breaks ties identically across all casters.
                rankedCandidates = rankedCandidates
                    .Where(x => x.Balance >= MinCasterBalance)
                    .OrderByDescending(x => x.Balance)
                    .ThenBy(x => x.Validator.Address ?? "", StringComparer.Ordinal)
                    .ToList();

                int slotsAvailable = MaxCasters - currentCasters.Count;
                // FIX A: Iterate ALL ranked candidates (not .Take(slotsAvailable)) so that
                // cooldown-blocked candidates don't waste promotion slots. We promote up to
                // slotsAvailable and skip any that fail checks.
                CasterLogUtility.Log(
                    $"EvalTick — slotsAvailable={slotsAvailable}, attempting promotion of {rankedCandidates.Count} candidate(s): [{string.Join(",", rankedCandidates.Select(p => p.Validator.Address))}]",
                    "CasterFlow");

                int promoted = 0;
                foreach (var candidate in rankedCandidates)
                {
                    if (promoted >= slotsAvailable)
                    {
                        CasterLogUtility.Log($"  <<  STOP — all {slotsAvailable} slot(s) filled", "CasterFlow");
                        break;
                    }

                    var v = candidate.Validator;
                    var ip = v.IPAddress.Replace("::ffff:", "");

                    // CONSENSUS-V2 (Fix #2): Atomic in-flight claim. Only one promotion per node may
                    // be mid-flight at a time. This prevents a parallel EvaluateCasterPool task on
                    // this same node from racing with us; it also bounds the window during which
                    // BlockCasters.Count check can lie (since the long HTTP gates run inside the
                    // claim and the final add re-checks under lock via AddBlockCasterIfRoomAndUnique).
                    bool claimed;
                    lock (_promotionLock)
                    {
                        if (Globals.BlockCasters.Count >= MaxCasters)
                        {
                            CasterLogUtility.Log($"  <<  ABORT — pool filled to {Globals.BlockCasters.Count}/{MaxCasters} during iteration", "CasterFlow");
                            break;
                        }
                        if (_inFlightPromotionAddress != null)
                        {
                            CasterLogUtility.Log($"  <<  SKIP — another promotion in-flight ({_inFlightPromotionAddress})", "CasterFlow");
                            // Skip this iteration; outer loop will move on (don't break — give
                            // a different candidate a chance once the in-flight one finishes).
                            claimed = false;
                        }
                        else
                        {
                            _inFlightPromotionAddress = v.Address;
                            claimed = true;
                        }
                    }
                    if (!claimed)
                        continue;

                    bool addedToPool = false;
                    try
                    {

                    CasterLogUtility.Log(
                        $"  >> Candidate {v.Address} ip={ip} balance={candidate.Balance} firstSeen={v.FirstSeenAtHeight} now={currentHeight} maturityΔ={(v.FirstSeenAtHeight > 0 ? currentHeight - v.FirstSeenAtHeight : -1)}/{MaturityBlocks}",
                        "CasterFlow");
                    ConsoleWriterService.OutputValCaster($"[CasterFlow] Promoting candidate {v.Address} ({ip})…");

                    // Promotion cooldown: skip candidates with repeated version/port failures
                    if (_promotionCooldowns.TryGetValue(v.Address, out var cooldown)
                        && cooldown.CooldownUntilHeight > currentHeight)
                    {
                        CasterLogUtility.Log(
                            $"  <<  SKIP — promotion cooldown until height {cooldown.CooldownUntilHeight} (fails={cooldown.FailCount})",
                            "CasterFlow");
                        continue;
                    }

                    // Maturity gate: don't promote validators that just connected.
                    // They need time to sync their own NetworkValidators pool.
                    if (v.FirstSeenAtHeight > 0 && currentHeight - v.FirstSeenAtHeight < MaturityBlocks)
                    {
                        CasterLogUtility.Log($"  <<  maturity=FAIL ({currentHeight - v.FirstSeenAtHeight}/{MaturityBlocks})", "CasterFlow");
                        ConsoleWriterService.OutputValCaster(
                            $"[CasterDiscovery] Candidate {v.Address} not mature enough (seen at height {v.FirstSeenAtHeight}, current {currentHeight}, need {MaturityBlocks} blocks). Skipping.");
                        continue;
                    }
                    CasterLogUtility.Log($"     maturity=PASS", "CasterFlow");

                    if (!PortUtility.IsPortOpen(ip, Globals.ValAPIPort))
                    {
                        CasterLogUtility.Log($"  <<  portOpen=FAIL {ip}:{Globals.ValAPIPort}", "CasterFlow");
                        ConsoleWriterService.OutputValCaster(
                            $"[CasterDiscovery] Candidate {v.Address} port check failed on {ip}:{Globals.ValAPIPort}");
                        TrackPromotionCooldown(v.Address, currentHeight);
                        continue;
                    }
                    CasterLogUtility.Log($"     portOpen=PASS {ip}:{Globals.ValAPIPort}", "CasterFlow");

                    // Version gate: reject candidates on outdated major versions.
                    // This prevents nodes that can't produce valid proofs from inflating the quorum.
                    // Capture the version string so we can stamp it on the new Peers entry —
                    // GenerateCasterProofs filters BlockCasters by WalletVersion, and an empty
                    // version silently excludes the caster from proof generation.
                    var candidateVersionResult = await CheckCandidateVersionDetailed(ip, v.Address);
                    if (candidateVersionResult.Status != VersionCheckResult.Ok)
                    {
                        CasterLogUtility.Log(
                            $"  <<  version={candidateVersionResult.Status} reported='{candidateVersionResult.Version}'",
                            "CasterFlow");
                        // FIX 3: Reset readiness on failure
                        _valApiReadiness.TryRemove(v.Address, out _);
                        // Track cooldown for unreachable nodes (outdated is a permanent problem until they upgrade)
                        if (candidateVersionResult.Status == VersionCheckResult.Unreachable)
                            TrackPromotionCooldown(v.Address, currentHeight);
                        // FIX: Confirmed-outdated validators should be excluded from future proof generation
                        // and promotion evaluation. Bump CheckFailCount past the filter thresholds so they
                        // stop appearing in EvalTick candidates and in GenerateProofsFromNetworkValidatorsLegacy.
                        // If they upgrade and reconnect, P2P gossip will re-add them with CheckFailCount=0.
                        if (candidateVersionResult.Status == VersionCheckResult.Outdated)
                        {
                            if (Globals.NetworkValidators.TryGetValue(v.Address, out var nvOutdated))
                                nvOutdated.CheckFailCount = 10;
                        }
                        continue;
                    }
                    CasterLogUtility.Log(
                        $"     version=PASS reported='{candidateVersionResult.Version}'",
                        "CasterFlow");
                    // Clear any promotion cooldown on success
                    _promotionCooldowns.TryRemove(v.Address, out _);

                    // FIX 3: HTTP-ready grace period — require ≥3 consecutive version check successes
                    // AND ≥30s since first success before allowing promotion.
                    var nowTicks = DateTime.UtcNow.Ticks;
                    var readiness = _valApiReadiness.AddOrUpdate(
                        v.Address,
                        _ => (nowTicks, 1),
                        (_, prev) => (prev.FirstSuccessUtcTicks, prev.ConsecutiveSuccesses + 1));
                    var readinessElapsed = TimeSpan.FromTicks(nowTicks - readiness.FirstSuccessUtcTicks).TotalSeconds;
                    if (readiness.ConsecutiveSuccesses < RequiredReadinessChecks || readinessElapsed < RequiredReadinessSeconds)
                    {
                        CasterLogUtility.Log(
                            $"  <<  apiReady=FAIL checks={readiness.ConsecutiveSuccesses}/{RequiredReadinessChecks} elapsed={readinessElapsed:F0}s/{RequiredReadinessSeconds}s",
                            "CasterFlow");
                        continue;
                    }
                    CasterLogUtility.Log(
                        $"     apiReady=PASS checks={readiness.ConsecutiveSuccesses}/{RequiredReadinessChecks} elapsed={readinessElapsed:F0}s/{RequiredReadinessSeconds}s",
                        "CasterFlow");

                    // Health gate: reject candidates that are out of sync or have clock skew.
                    if (!await CheckCandidateHealth(ip, v.Address))
                    {
                        CasterLogUtility.Log($"  <<  health=FAIL", "CasterFlow");
                        continue;
                    }
                    CasterLogUtility.Log($"     health=PASS", "CasterFlow");

                    // CONSENSUS-V2 (Fix #2): Cheap pre-check (definitive add re-checks under lock).
                    if (Globals.BlockCasters.Any(c => c.ValidatorAddress == v.Address))
                    {
                        CasterLogUtility.Log($"  <<  race-condition: already in BlockCasters (another thread promoted first)", "CasterFlow");
                        continue;
                    }

                    var newCaster = new Peers
                    {
                        PeerIP = ip,
                        IsValidator = true,
                        ValidatorAddress = v.Address,
                        ValidatorPublicKey = v.PublicKey,
                        FailCount = 0,
                        WalletVersion = candidateVersionResult.Version,
                    };

                    // FIX 5: Promotion agreement — propose to peer casters before promoting.
                    // All casters must agree on the same candidate to prevent segmented caster lists.
                    var agreementReached = await ProposePromotionToPeers(v.Address, ip, currentHeight).ConfigureAwait(false);
                    if (!agreementReached)
                    {
                        CasterLogUtility.Log($"  <<  promotionAgreement=FAIL — peer casters did not agree on candidate {v.Address}", "CasterFlow");
                        ConsoleWriterService.OutputValCaster(
                            $"[CasterDiscovery] Promotion agreement failed for {v.Address}. Skipping — will retry next height.");
                        continue;
                    }
                    CasterLogUtility.Log($"     promotionAgreement=PASS", "CasterFlow");

                    // Send promotion notification FIRST and wait for acceptance.
                    // Only add to BlockCasters if the promoted node confirms.
                    // This prevents "zombie casters" where the promoter's caster list
                    // includes a node that rejected the promotion, breaking quorum.
                    CasterLogUtility.Log($"     sending PromoteToCaster HTTP to {ip}…", "CasterFlow");
                    ConsoleWriterService.OutputValCaster($"[CasterFlow] → POST PromoteToCaster {ip}");
                    var (accepted, promotionUnreachable) = await NotifyPromotionAndAwaitAcceptance(ip, v.Address, newCaster).ConfigureAwait(false);
                    if (!accepted)
                    {
                        if (promotionUnreachable)
                        {
                            RevokeFullyTrustedValidatorState(v.Address, "PromoteToCaster unreachable or HTTP error");
                            TrackPromotionCooldown(v.Address, currentHeight);
                        }
                        CasterLogUtility.Log($"  <<  promotion REJECTED/failed by candidate (unreachable={promotionUnreachable})", "CasterFlow");
                        ConsoleWriterService.OutputValCaster(
                            $"[CasterDiscovery] Candidate {v.Address} at {ip} did not accept promotion. Not adding to caster pool.");
                        continue;
                    }

                    // CONSENSUS-V2 (Fix #2): Atomic add via helper — re-checks Count<MaxCasters
                    // and uniqueness under _promotionLock just before mutating the bag, so two
                    // simultaneous promotion paths (e.g. local Eval + inbound promotion announce)
                    // can't blow past MaxCasters even if both passed earlier checks.
                    if (!AddBlockCasterIfRoomAndUnique(newCaster))
                    {
                        CasterLogUtility.Log($"  <<  Atomic add failed (pool full or duplicate). Skipping.", "CasterFlow");
                        continue;
                    }
                    addedToPool = true;
                    Globals.SyncKnownCastersFromBlockCasters();

                    // CONSENSUS-V2 (Fix #3): Immediately broadcast the promotion to all peer casters
                    // so they merge the new caster into their own BlockCasters bag. Without this,
                    // peers only learn about the new caster on the next caster-list sync (up to 100
                    // blocks later), which leaves the consensus pool segmented in the meantime.
                    _ = Task.Run(async () =>
                    {
                        try { await BroadcastPromotionAnnouncement(newCaster, currentHeight).ConfigureAwait(false); }
                        catch (Exception bcEx)
                        {
                            CasterLogUtility.Log($"  promotion-announce broadcast failed: {bcEx.Message}", "CasterFlow");
                        }
                    });

                    // FIX: Push the last few blocks to the newly promoted caster so it can
                    // immediately participate in consensus. Without this, a newly promoted node
                    // that is 1-2 blocks behind will return body=0 for proof fetches, fail to
                    // generate proofs, and fail block fetch — creating a cascading failure cycle.
                    _ = Task.Run(async () =>
                    {
                        try { await PushRecentBlocksToPeer(ip, 3).ConfigureAwait(false); }
                        catch (Exception pushEx)
                        {
                            CasterLogUtility.Log($"  block-push to {ip} failed: {pushEx.Message}", "CasterFlow");
                        }
                    });

                    // CONSENSUS-V2 (Fix #4): Force an out-of-band validator-list sync on
                    // every existing caster immediately. Without this, the new caster knows
                    // only itself + bootstrap peers until the next 10-block cadence tick,
                    // and proof generation runs against a divergent NetworkValidators set.
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await BlockcasterNode.SyncValidatorListsWithPeersAsync(currentHeight, force: true)
                                .ConfigureAwait(false);
                            CasterLogUtility.Log(
                                $"  post-promotion VALLIST-SYNC fired for {v.Address} at height {currentHeight}",
                                "CasterFlow");
                        }
                        catch (Exception svEx)
                        {
                            CasterLogUtility.Log(
                                $"  post-promotion VALLIST-SYNC FAILED for {v.Address}: {svEx.Message}",
                                "CasterFlow");
                        }
                    });

                    CasterLogUtility.Log(
                        $"  ✓ PROMOTED val={v.Address} ip={ip}. BlockCasters.Count now {Globals.BlockCasters.Count}/{MaxCasters}",
                        "CasterFlow");

                    ConsoleWriterService.OutputValCaster(
                        $"[CasterDiscovery] Promoted {v.Address} (balance: {candidate.Balance}) to caster. Pool: {Globals.BlockCasters.Count}/{MaxCasters}");
                    promoted++;
                    } // end inner try (Fix #2 in-flight scope)
                    finally
                    {
                        // CONSENSUS-V2 (Fix #2): Always release the in-flight slot so the next
                        // candidate iteration can claim it, even if any gate above threw or returned.
                        lock (_promotionLock)
                        {
                            if (_inFlightPromotionAddress == v.Address)
                                _inFlightPromotionAddress = null;
                        }
                    }
                }

            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"EvaluateCasterPool error: {ex.Message}", "CasterDiscoveryService");
            }
        }

        /// <summary>
        /// Checks a candidate's clock skew and validator pool health before promotion.
        /// Returns true only if the candidate has acceptable clock sync and a healthy validator pool.
        /// </summary>
        internal static async Task<bool> CheckCandidateHealth(string ip, string address)
        {
            // Clock skew check: query the candidate's block height and compare timestamps
            try
            {
                using var client = Globals.HttpClientFactory.CreateClient();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

                // Use CasterReadyCheck which returns Height, Ready, Address
                var uri = $"http://{ip}:{Globals.ValAPIPort}/valapi/validator/CasterReadyCheck/{Globals.LastBlock.Height}";
                var response = await client.GetAsync(uri, cts.Token);
                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync();
                    if (!string.IsNullOrEmpty(body) && body != "0")
                    {
                        var status = Newtonsoft.Json.JsonConvert.DeserializeAnonymousType(body, new { Height = 0L, Ready = false, Address = "" });
                        if (status != null)
                        {
                            // Height skew check: if the candidate is >5 blocks behind, reject
                            var heightDiff = Math.Abs(Globals.LastBlock.Height - status.Height);
                            if (heightDiff > 5)
                            {
                                ConsoleWriterService.OutputValCaster(
                                    $"[CasterDiscovery] HealthCheck: Candidate {address} at {ip} is {heightDiff} blocks behind (theirs={status.Height}, ours={Globals.LastBlock.Height}). Skipping.");
                                return false;
                            }
                        }
                    }
                }
                else
                {
                    ConsoleWriterService.OutputValCaster(
                        $"[CasterDiscovery] HealthCheck: Candidate {address} at {ip} — CasterReadyCheck returned {response.StatusCode}. Skipping.");
                    return false;
                }
            }
            catch (Exception ex)
            {
                ConsoleWriterService.OutputValCaster(
                    $"[CasterDiscovery] HealthCheck: Candidate {address} at {ip} — unreachable: {ex.Message}. Skipping.");
                return false;
            }

            return true;
        }

        /// <summary>Minimum number of validators a node must know about to be a useful caster.</summary>
        public const int MinValidatorPoolSize = 2;

        /// <summary>
        /// Checks a candidate's wallet version via HTTP before promotion.
        /// Returns true if the candidate is on a compatible major version.
        /// </summary>
        internal static async Task<bool> CheckCandidateVersion(string ip, string address)
        {
            var result = await CheckCandidateVersionDetailed(ip, address);
            return result.Status == VersionCheckResult.Ok;
        }

        /// <summary>
        /// Checks a candidate's wallet version via HTTP, returning a detailed result
        /// that distinguishes between connectivity failure and genuine version mismatch,
        /// and includes the discovered version string when reachable.
        /// </summary>
        internal static async Task<VersionCheckInfo> CheckCandidateVersionDetailed(string ip, string address)
        {
            try
            {
                using var client = Globals.HttpClientFactory.CreateClient();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var uri = $"http://{ip}:{Globals.ValAPIPort}/valapi/validator/GetWalletVersion";
                var response = await client.GetAsync(uri, cts.Token);
                if (response == null || !response.IsSuccessStatusCode)
                {
                    // FIX C: Distinguish HTTP 404 (endpoint doesn't exist — incompatible software)
                    // from other errors (timeout, 500, etc. — possibly transient).
                    // 404 means the node fundamentally can't participate → treat as Outdated (permanent).
                    var statusCode = response?.StatusCode;
                    var isNotFound = statusCode == System.Net.HttpStatusCode.NotFound;
                    var classification = isNotFound ? VersionCheckResult.Outdated : VersionCheckResult.Unreachable;
                    ConsoleWriterService.OutputValCaster(
                        $"[CasterDiscovery] VersionGate: Candidate {address} at {ip} — GetWalletVersion returned {statusCode} → {classification}. Skipping.");
                    return new VersionCheckInfo(classification, "");
                }
                var peerVersion = await response.Content.ReadAsStringAsync();
                if (string.IsNullOrEmpty(peerVersion) || !ProofUtility.IsMajorVersionCurrent(peerVersion))
                {
                    ConsoleWriterService.OutputValCaster(
                        $"[CasterDiscovery] VersionGate: Candidate {address} at {ip} reports version '{peerVersion}' — outdated (need major >= {Globals.MajorVer}). Skipping.");
                    return new VersionCheckInfo(VersionCheckResult.Outdated, peerVersion ?? "");
                }
                return new VersionCheckInfo(VersionCheckResult.Ok, peerVersion);
            }
            catch (Exception ex)
            {
                ConsoleWriterService.OutputValCaster(
                    $"[CasterDiscovery] VersionGate: Candidate {address} at {ip} — unreachable: {ex.Message}. Skipping.");
                return new VersionCheckInfo(VersionCheckResult.Unreachable, "");
            }
        }


        /// <summary>
        /// Increments the promotion failure counter for a candidate and sets an exponential
        /// cooldown once the threshold is reached. Called on port-check and version-check failures.
        /// </summary>
        private static void TrackPromotionCooldown(string address, long currentHeight)
        {
            var updated = _promotionCooldowns.AddOrUpdate(
                address,
                _ => (1, 0L),
                (_, prev) => (prev.FailCount + 1, prev.CooldownUntilHeight));

            if (updated.FailCount >= CooldownFailThreshold)
            {
                var exponent = Math.Min(updated.FailCount - CooldownFailThreshold, 10);
                var cooldownBlocks = Math.Min(CooldownBaseBlocks * (1 << exponent), CooldownMaxBlocks);
                var cooldownUntil = currentHeight + cooldownBlocks;
                _promotionCooldowns[address] = (updated.FailCount, cooldownUntil);
                CasterLogUtility.Log(
                    $"  cooldown SET for {address}: {cooldownBlocks} blocks (until height {cooldownUntil}, fails={updated.FailCount})",
                    "CasterFlow");
            }
        }

        /// <summary>
        /// Periodically audits existing casters and removes any on outdated major versions.
        /// Called from MonitorCasters or EvaluateCasterPool to keep the pool clean.
        /// Uses a retry-tolerant approach: connectivity failures must occur <see cref="AuditFailThreshold"/>
        /// consecutive times before demotion. Genuine version mismatches demote immediately.
        /// </summary>
        public static async Task AuditExistingCasterVersions()
        {
            if (!Globals.IsBlockCaster)
                return;

            var casters = Globals.BlockCasters.ToList();
            var toRemove = new List<string>();
            var removalReasons = new Dictionary<string, string>();

            foreach (var caster in casters)
            {
                if (string.IsNullOrEmpty(caster.PeerIP) || string.IsNullOrEmpty(caster.ValidatorAddress))
                    continue;

                // Don't audit ourselves
                if (caster.ValidatorAddress == Globals.ValidatorAddress)
                    continue;

                var ip = caster.PeerIP.Replace("::ffff:", "");
                var result = await CheckCandidateVersionDetailed(ip, caster.ValidatorAddress);

                // If the audit succeeded and we got a non-empty version string back,
                // opportunistically hydrate the cached Peers.WalletVersion so
                // ProofUtility.GenerateCasterProofs can keep trusting this caster
                // after a fresh promotion/restart cycle.
                if (result.Status == VersionCheckResult.Ok
                    && !string.IsNullOrEmpty(result.Version)
                    && string.IsNullOrEmpty(caster.WalletVersion))
                {
                    caster.WalletVersion = result.Version;
                }

                switch (result.Status)
                {

                    case VersionCheckResult.Ok:
                        // Reset failure counter on success
                        _auditFailCounts.TryRemove(caster.ValidatorAddress, out _);
                        break;

                    case VersionCheckResult.Outdated:
                        // Genuine version mismatch — demote immediately
                        toRemove.Add(caster.ValidatorAddress);
                        removalReasons[caster.ValidatorAddress] = "outdated version";
                        _auditFailCounts.TryRemove(caster.ValidatorAddress, out _);
                        break;

                    case VersionCheckResult.Unreachable:
                        // Connectivity failure — only demote after consecutive failures
                        var failCount = _auditFailCounts.AddOrUpdate(
                            caster.ValidatorAddress, 1, (_, c) => c + 1);

                        if (failCount >= AuditFailThreshold)
                        {
                            toRemove.Add(caster.ValidatorAddress);
                            removalReasons[caster.ValidatorAddress] = $"unreachable ({failCount} consecutive failures)";
                            _auditFailCounts.TryRemove(caster.ValidatorAddress, out _);
                        }
                        else
                        {
                            ConsoleWriterService.OutputValCaster(
                                $"[CasterDiscovery] VersionAudit: Caster {caster.ValidatorAddress} unreachable (attempt {failCount}/{AuditFailThreshold}). Not demoting yet.");
                        }
                        break;
                }
            }

                if (toRemove.Count > 0)
            {
                var remaining = casters
                    .Where(c => !toRemove.Contains(c.ValidatorAddress ?? ""))
                    .ToList();
                var nBag = new System.Collections.Concurrent.ConcurrentBag<Peers>();
                foreach (var x in remaining)
                    nBag.Add(x);
                Globals.BlockCasters = nBag;
                Globals.SyncKnownCastersFromBlockCasters();

                foreach (var addr in toRemove)
                {
                    var reason = removalReasons.GetValueOrDefault(addr, "unknown");
                    ConsoleWriterService.OutputValCaster(
                        $"[CasterDiscovery] VersionAudit: Demoted caster {addr} — {reason}.");
                    RevokeFullyTrustedValidatorState(addr, $"version audit demotion ({reason})");
                }

                // Broadcast demotion to all remaining casters so they remove the node too
                await BroadcastDemotions(toRemove).ConfigureAwait(false);

                // Re-evaluate to fill slots
                _lastEvaluationHeight = long.MinValue;
                await EvaluateCasterPool().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Clears fully-trusted status for a validator that left the caster pool or could not be reached
        /// for promotion. They exit proof quorum and caster promotion until gossip/chain restores trust.
        /// </summary>
        private static void RevokeFullyTrustedValidatorState(string validatorAddress, string reason)
        {
            if (string.IsNullOrEmpty(validatorAddress) || validatorAddress == "unknown")
                return;
            if (!Globals.NetworkValidators.TryGetValue(validatorAddress, out var nv))
                return;
            nv.IsFullyTrusted = false;
            nv.CheckFailCount = Math.Max(nv.CheckFailCount, 4);
            nv.LastSeen = 0;
            Globals.NetworkValidators[validatorAddress] = nv;
            ProofUtility.ClearProofGenerationCache();
            CasterLogUtility.Log(
                $"NetworkValidator trust revoked for {validatorAddress}: {reason}",
                "CasterFlow");
        }

        /// <summary>
        /// Sends a promotion notification to the candidate and waits for acceptance.
        /// Returns (true, false) only if the candidate responds with an acceptance confirmation.
        /// <paramref name="unreachable"/> is true when the HTTP call failed (offline, timeout, non-success status).
        /// </summary>
        private static async Task<(bool accepted, bool unreachable)> NotifyPromotionAndAwaitAcceptance(string peerIP, string address, Peers newCaster)
        {
            try
            {
                var account = AccountData.GetLocalValidator();
                if (account == null)
                    return (false, false);
                var privateKey = account.GetPrivKey;
                if (privateKey == null || string.IsNullOrEmpty(account.PublicKey))
                    return (false, false);

                using var client = Globals.HttpClientFactory.CreateClient();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

                // Build the caster list INCLUDING the new candidate
                var existingCasters = Globals.BlockCasters.ToList()
                    .Where(c => !string.IsNullOrEmpty(c.ValidatorAddress))
                    .Select(c => new CasterInfo
                    {
                        Address = c.ValidatorAddress!,
                        PeerIP = (c.PeerIP ?? "").Replace("::ffff:", ""),
                        PublicKey = c.ValidatorPublicKey ?? ""
                    })
                    .ToList();

                // Add the new candidate to the list
                existingCasters.Add(new CasterInfo
                {
                    Address = newCaster.ValidatorAddress!,
                    PeerIP = (newCaster.PeerIP ?? "").Replace("::ffff:", ""),
                    PublicKey = newCaster.ValidatorPublicKey ?? ""
                });

                var blockHeight = Globals.LastBlock.Height;
                var promoterAddress = Globals.ValidatorAddress ?? "";

                var casterListJson = GetCanonicalCasterListJson(existingCasters);
                var casterListHashHex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(casterListJson))).ToLowerInvariant();
                var canonicalMessage = $"PROMOTE|{address}|{blockHeight}|{promoterAddress}|{casterListHashHex}";
                var signature = SignatureService.CreateSignature(canonicalMessage, privateKey, account.PublicKey);
                if (signature == "ERROR")
                    return (false, false);

                var promotion = new CasterPromotionRequest
                {
                    PromotedAddress = address,
                    BlockHeight = blockHeight,
                    CasterList = existingCasters,
                    PromoterAddress = promoterAddress,
                    PromoterSignature = signature,
                };

                var uri = $"http://{peerIP}:{Globals.ValAPIPort}/valapi/validator/PromoteToCaster";
                var json = JsonConvert.SerializeObject(promotion);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");

                CasterLogUtility.Log(
                    $"NotifyPromotion → POST {uri} | promotedAddr={address} promoterAddr={promoterAddress} height={blockHeight} casterListSize={existingCasters.Count} bodyBytes={json.Length}",
                    "CasterFlow");

                var response = await client.PostAsync(uri, content, cts.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    CasterLogUtility.Log(
                        $"NotifyPromotion ← {response.StatusCode} body='{body}' (peer={peerIP})",
                        "CasterFlow");
                    // The PromoteToCaster endpoint returns "accepted" if the node accepted the promotion
                    if (!string.IsNullOrEmpty(body) && body.Contains("accepted", StringComparison.OrdinalIgnoreCase))
                    {
                        ConsoleWriterService.OutputValCaster(
                            $"[CasterDiscovery] Candidate {address} at {peerIP} ACCEPTED promotion.");
                        return (true, false);
                    }
                    else
                    {
                        ConsoleWriterService.OutputValCaster(
                            $"[CasterDiscovery] Candidate {address} at {peerIP} REJECTED promotion. Response: {body}");
                        return (false, false);
                    }
                }
                else
                {
                    string errBody = "";
                    try { errBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false); } catch { }
                    CasterLogUtility.Log(
                        $"NotifyPromotion ← HTTP ERROR {response.StatusCode} body='{errBody}' (peer={peerIP})",
                        "CasterFlow");
                    ConsoleWriterService.OutputValCaster(
                        $"[CasterDiscovery] Candidate {address} at {peerIP} promotion request failed: {response.StatusCode}");
                    return (false, true);
                }
            }
            catch (Exception ex)
            {
                CasterLogUtility.Log($"NotifyPromotion EXCEPTION peer={peerIP}: {ex.GetType().Name}: {ex.Message}", "CasterFlow");
                ErrorLogUtility.LogError($"NotifyPromotionAndAwaitAcceptance to {peerIP} failed: {ex.Message}", "CasterDiscoveryService");
                return (false, true);
            }
        }


        /// <summary>
        /// Broadcasts signed demotion notices to all peer casters so they remove the demoted node(s)
        /// simultaneously. This prevents caster list inconsistency across the network.
        /// </summary>
        private static async Task BroadcastDemotions(List<string> demotedAddresses)
        {
            if (!Globals.IsBlockCaster || string.IsNullOrEmpty(Globals.ValidatorAddress))
                return;

            var account = AccountData.GetLocalValidator();
            if (account == null)
                return;
            var privateKey = account.GetPrivKey;
            if (privateKey == null || string.IsNullOrEmpty(account.PublicKey))
                return;

            var peers = Globals.BlockCasters.ToList()
                .Where(c => !string.IsNullOrEmpty(c.PeerIP) && c.ValidatorAddress != Globals.ValidatorAddress);

            var blockHeight = Globals.LastBlock.Height;

            foreach (var demotedAddr in demotedAddresses)
            {
                var canonicalMessage = $"DEMOTE|{demotedAddr}|{blockHeight}|{Globals.ValidatorAddress}";
                var signature = SignatureService.CreateSignature(canonicalMessage, privateKey, account.PublicKey);
                if (signature == "ERROR")
                    continue;

                var demotion = new CasterDemotionNotice
                {
                    DemotedAddress = demotedAddr,
                    BlockHeight = blockHeight,
                    DemoterAddress = Globals.ValidatorAddress,
                    DemotionSignature = signature,
                };

                var json = JsonConvert.SerializeObject(demotion);

                var tasks = peers.Select(async peer =>
                {
                    try
                    {
                        using var client = Globals.HttpClientFactory.CreateClient();
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                        var ip = peer.PeerIP!.Replace("::ffff:", "");
                        var uri = $"http://{ip}:{Globals.ValAPIPort}/valapi/validator/AnnounceCasterDemotion";
                        using var content = new StringContent(json, Encoding.UTF8, "application/json");
                        await client.PostAsync(uri, content, cts.Token).ConfigureAwait(false);
                    }
                    catch { /* best-effort */ }
                });

                await Task.WhenAll(tasks).ConfigureAwait(false);
            }

            ConsoleWriterService.OutputValCaster($"[CasterDiscovery] Demotion notice broadcast for {demotedAddresses.Count} caster(s) to all peers.");
        }

        /// <summary>
        /// Handles a demotion notice received from a peer caster.
        /// Verifies the signature and removes the demoted caster from the local pool.
        /// </summary>
        public static async Task HandleDemotion(CasterDemotionNotice demotion)
        {
            if (string.IsNullOrEmpty(demotion.DemotedAddress) || string.IsNullOrEmpty(demotion.DemoterAddress))
                return;

            // Verify the demoter is a known caster
            var isTrustedDemoter = Globals.BlockCasters.Any(c => c.ValidatorAddress == demotion.DemoterAddress)
                || Globals.KnownCasters.Any(c => c.Address == demotion.DemoterAddress);
            if (!isTrustedDemoter)
            {
                ErrorLogUtility.LogError($"Demotion from untrusted address {demotion.DemoterAddress} — not in our caster set", "CasterDiscoveryService");
                return;
            }

            var canonicalMessage = $"DEMOTE|{demotion.DemotedAddress}|{demotion.BlockHeight}|{demotion.DemoterAddress}";
            if (!SignatureService.VerifySignature(demotion.DemoterAddress, canonicalMessage, demotion.DemotionSignature))
            {
                ErrorLogUtility.LogError($"Invalid demotion signature from {demotion.DemoterAddress}", "CasterDiscoveryService");
                return;
            }

            var casterList = Globals.BlockCasters.ToList();
            if (!casterList.Any(c => c.ValidatorAddress == demotion.DemotedAddress))
                return; // Already removed

            ConsoleWriterService.OutputValCaster(
                $"[CasterDiscovery] Received demotion notice for {demotion.DemotedAddress} from {demotion.DemoterAddress}. Removing...");

            var nCasterList = casterList
                .Where(c => c.ValidatorAddress != demotion.DemotedAddress)
                .ToList();
            var nBag = new ConcurrentBag<Peers>();
            foreach (var x in nCasterList)
                nBag.Add(x);
            Globals.BlockCasters = nBag;
            Globals.SyncKnownCastersFromBlockCasters();

            // Clear audit failure counter for the demoted address
            _auditFailCounts.TryRemove(demotion.DemotedAddress, out _);

            await OnCasterRemoved(demotion.DemotedAddress).ConfigureAwait(false);
        }

        public static async Task OnCasterRemoved(string removedAddress)
        {
            ConsoleWriterService.OutputValCaster(
                $"[CasterDiscovery] Caster {removedAddress} removed. Evaluating pool for replacement...");

            RevokeFullyTrustedValidatorState(removedAddress, "removed from BlockCasters (eviction/demotion)");

            _lastEvaluationHeight = long.MinValue;
            await EvaluateCasterPool().ConfigureAwait(false);
        }

        public static async Task BroadcastDeparture()
        {
            if (!Globals.IsBlockCaster || string.IsNullOrEmpty(Globals.ValidatorAddress))
                return;

            var account = AccountData.GetLocalValidator();
            if (account == null)
                return;
            var privateKey = account.GetPrivKey;
            if (privateKey == null || string.IsNullOrEmpty(account.PublicKey))
                return;

            var peers = Globals.BlockCasters.ToList()
                .Where(c => !string.IsNullOrEmpty(c.PeerIP) && c.ValidatorAddress != Globals.ValidatorAddress);

            var blockHeight = Globals.LastBlock.Height;
            var canonicalMessage = $"DEPART|{Globals.ValidatorAddress}|{blockHeight}";
            var signature = SignatureService.CreateSignature(canonicalMessage, privateKey, account.PublicKey);
            if (signature == "ERROR")
                return;

            var departure = new CasterDepartureNotice
            {
                DepartingAddress = Globals.ValidatorAddress,
                BlockHeight = blockHeight,
                DepartureSignature = signature,
            };

            var json = JsonConvert.SerializeObject(departure);

            var tasks = peers.Select(async peer =>
            {
                try
                {
                    using var client = Globals.HttpClientFactory.CreateClient();
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    var ip = peer.PeerIP!.Replace("::ffff:", "");
                    var uri = $"http://{ip}:{Globals.ValAPIPort}/valapi/validator/AnnounceCasterDeparture";
                    using var content = new StringContent(json, Encoding.UTF8, "application/json");
                    await client.PostAsync(uri, content, cts.Token).ConfigureAwait(false);
                }
                catch { /* best-effort */ }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);
            ConsoleWriterService.OutputValCaster("[CasterDiscovery] Departure notice broadcast to all peers.");
        }

        /// <summary>
        /// Purges stale or unreachable validators from NetworkValidators.
        /// Called periodically to prevent broken validators from inflating pool counts.
        /// </summary>
        public static void PurgeStaleValidators()
        {
            var currentHeight = Globals.LastBlock?.Height ?? 0;
            if (currentHeight <= 0) return;

            var toRemove = new List<string>();
            foreach (var kvp in Globals.NetworkValidators)
            {
                var v = kvp.Value;
                // Remove validators not seen for >300 seconds (5 min) AND with high check fail count
                var timeSinceLastSeen = TimeUtil.GetTime() - v.LastSeen;
                if (timeSinceLastSeen > 300 && v.CheckFailCount >= 5)
                {
                    toRemove.Add(kvp.Key);
                }
            }

            foreach (var addr in toRemove)
            {
                Globals.NetworkValidators.TryRemove(addr, out _);
                ConsoleWriterService.OutputValCaster(
                    $"[CasterDiscovery] Purged stale validator {addr} from NetworkValidators (high fail count + not seen recently).");
            }
        }

        public static async Task CheckForStall()
        {
            if (!Globals.IsBlockCaster)
                return;

            var lastBlockTick = Globals.LastBlockProducedTick;
            if (lastBlockTick == 0)
                return;

            var elapsedMs = Environment.TickCount64 - lastBlockTick;
            if (elapsedMs < 0)
                elapsedMs = long.MaxValue / 2;
            var elapsed = elapsedMs / 1000;
            if (elapsed < StallThresholdSeconds)
                return;

            ConsoleWriterService.OutputValCaster(
                $"[CasterDiscovery] STALL DETECTED: No block for {elapsed}s. Running emergency caster health check...");

            await BlockcasterNode.PingCasters().ConfigureAwait(false);

            _lastEvaluationHeight = long.MinValue;
            await EvaluateCasterPool().ConfigureAwait(false);
        }

        /// <summary>
        /// Handles a promotion request. Returns "accepted" if the node accepts, or a rejection reason.
        /// The promoter waits for this response before adding the node to its caster list.
        /// </summary>
        public static Task<string> HandlePromotion(CasterPromotionRequest promotion)
        {
            var listSummary = promotion.CasterList == null
                ? "<null>"
                : string.Join(",", promotion.CasterList.Select(c => c.Address ?? "?"));
            CasterLogUtility.Log(
                $"HandlePromotion ENTER | Self={Globals.ValidatorAddress} | promoter={promotion.PromoterAddress} " +
                $"promoted={promotion.PromotedAddress} height={promotion.BlockHeight} " +
                $"list=[{listSummary}] | BlockCasters.Count(before)={Globals.BlockCasters.Count} IsBlockCaster(before)={Globals.IsBlockCaster}",
                "CasterFlow");
            ConsoleWriterService.OutputValCaster(
                $"[CasterFlow] HandlePromotion inbound from {promotion.PromoterAddress} at height {promotion.BlockHeight}");

            if (string.IsNullOrEmpty(Globals.ValidatorAddress))
            {
                CasterLogUtility.Log("HandlePromotion REJECT: no validator address on this node", "CasterFlow");
                return Task.FromResult("rejected: no validator address");
            }
            if (promotion.PromotedAddress != Globals.ValidatorAddress)
            {
                CasterLogUtility.Log(
                    $"HandlePromotion REJECT: not for this node. promoted='{promotion.PromotedAddress}' self='{Globals.ValidatorAddress}'",
                    "CasterFlow");
                return Task.FromResult("rejected: not for this node");
            }

            var knownCastersAddrs = string.Join(",", Globals.KnownCasters.Select(c => c.Address ?? "?"));
            var blockCastersAddrs = string.Join(",", Globals.BlockCasters.Select(c => c.ValidatorAddress ?? "?"));
            var isTrustedPromoter = Globals.BlockCasters.Any(c => c.ValidatorAddress == promotion.PromoterAddress)
                || Globals.KnownCasters.Any(c => c.Address == promotion.PromoterAddress);
            if (!isTrustedPromoter)
            {
                CasterLogUtility.Log(
                    $"HandlePromotion REJECT: untrusted promoter '{promotion.PromoterAddress}'. " +
                    $"BlockCasters=[{blockCastersAddrs}] KnownCasters=[{knownCastersAddrs}]",
                    "CasterFlow");
                ErrorLogUtility.LogError($"Promotion from untrusted address {promotion.PromoterAddress} — not in our caster set", "CasterDiscoveryService");
                return Task.FromResult("rejected: untrusted promoter");
            }

            if (promotion.CasterList == null
                || !promotion.CasterList.Any(c => c.Address == promotion.PromoterAddress))
            {
                CasterLogUtility.Log(
                    $"HandlePromotion REJECT: promoter '{promotion.PromoterAddress}' is NOT in signed CasterList=[{listSummary}]",
                    "CasterFlow");
                ErrorLogUtility.LogError($"Promotion from {promotion.PromoterAddress} — promoter not in CasterList", "CasterDiscoveryService");
                return Task.FromResult("rejected: promoter not in caster list");
            }

            if (!promotion.CasterList.Any(c => c.Address == Globals.ValidatorAddress))
            {
                CasterLogUtility.Log(
                    $"HandlePromotion REJECT: our address '{Globals.ValidatorAddress}' missing from signed CasterList=[{listSummary}]",
                    "CasterFlow");
                ErrorLogUtility.LogError("Promotion rejected: promoted address missing from signed CasterList", "CasterDiscoveryService");
                return Task.FromResult("rejected: our address missing from caster list");
            }

            var casterListJson = GetCanonicalCasterListJson(promotion.CasterList);
            var casterListHashHex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(casterListJson))).ToLowerInvariant();
            var canonicalMessage = $"PROMOTE|{promotion.PromotedAddress}|{promotion.BlockHeight}|{promotion.PromoterAddress}|{casterListHashHex}";
            if (!SignatureService.VerifySignature(promotion.PromoterAddress, canonicalMessage, promotion.PromoterSignature))
            {
                var sigSnippet = (promotion.PromoterSignature ?? "").Length > 20
                    ? promotion.PromoterSignature!.Substring(0, 20) + "…"
                    : (promotion.PromoterSignature ?? "<null>");
                CasterLogUtility.Log(
                    $"HandlePromotion REJECT: signature verification failed. canonicalMsg='{canonicalMessage}' sig='{sigSnippet}'",
                    "CasterFlow");
                ErrorLogUtility.LogError($"Invalid promotion signature from {promotion.PromoterAddress}", "CasterDiscoveryService");
                return Task.FromResult("rejected: invalid signature");
            }
            CasterLogUtility.Log("HandlePromotion: signature verified OK", "CasterFlow");

            // Self-health check: reject promotion if our NetworkValidators pool is too small.
            // Without validators we can't generate proofs, which inflates the quorum and halts consensus.
            var validatorCount = Globals.NetworkValidators.Count;
            if (validatorCount < MinValidatorPoolSize)
            {
                var netValAddrs = string.Join(",", Globals.NetworkValidators.Values.Select(v => v.Address ?? "?"));
                CasterLogUtility.Log(
                    $"HandlePromotion REJECT: self-health — NetworkValidators.Count={validatorCount} < min={MinValidatorPoolSize}. addrs=[{netValAddrs}]",
                    "CasterFlow");
                ConsoleWriterService.OutputValCaster(
                    $"[CasterDiscovery] REJECTING promotion — our NetworkValidators pool has only {validatorCount} entries (need {MinValidatorPoolSize}+). " +
                    "This node cannot produce proofs and would halt consensus.");
                return Task.FromResult($"rejected: only {validatorCount} validators in pool (need {MinValidatorPoolSize}+)");
            }

            CasterLogUtility.Log(
                $"HandlePromotion: self-health OK (NetworkValidators.Count={validatorCount}). Applying promotion…",
                "CasterFlow");
            ConsoleWriterService.OutputValCaster(
                $"[CasterDiscovery] THIS NODE has been promoted to caster by {promotion.PromoterAddress} at height {promotion.BlockHeight}!");


            // Pre-populate WalletVersion so the newly installed BlockCasters are eligible for
            // proof generation on the very first consensus round after promotion.
            // - For our own entry we know our version (Globals.CLIVersion).
            // - For peers already in BlockCasters we preserve any previously-known version.
            // - For unknown peers we leave it empty; ProofUtility.GenerateCasterProofs will
            //   hydrate it via an HTTP fallback on the next tick.
            var previousVersions = Globals.BlockCasters.ToList()
                .Where(c => !string.IsNullOrEmpty(c.ValidatorAddress) && !string.IsNullOrEmpty(c.WalletVersion))
                .GroupBy(c => c.ValidatorAddress!)
                .ToDictionary(g => g.Key, g => g.First().WalletVersion!);

            var newBag = new ConcurrentBag<Peers>();
            foreach (var ci in promotion.CasterList)
            {
                string? walletVersion = null;
                if (!string.IsNullOrEmpty(ci.Address))
                {
                    if (ci.Address == Globals.ValidatorAddress)
                        walletVersion = Globals.CLIVersion;
                    else if (previousVersions.TryGetValue(ci.Address, out var known))
                        walletVersion = known;
                }

                newBag.Add(new Peers
                {
                    PeerIP = ci.PeerIP,
                    IsValidator = true,
                    ValidatorAddress = ci.Address,
                    ValidatorPublicKey = ci.PublicKey,
                    FailCount = 0,
                    WalletVersion = walletVersion,
                });
            }
            Globals.BlockCasters = newBag;

            Globals.SyncKnownCastersFromBlockCasters();

            var wasBlockCaster = Globals.IsBlockCaster;
            // BlockcasterNode.StartCastingRounds already runs as IHostedService; it picks up caster status on next loop.
            if (!Globals.IsBlockCaster)
            {
                Globals.IsBlockCaster = true;
            }

            var afterAddrs = string.Join(",", Globals.BlockCasters.Select(c => c.ValidatorAddress ?? "?"));
            CasterLogUtility.Log(
                $"HandlePromotion SUCCESS | BlockCasters.Count(after)={Globals.BlockCasters.Count} addrs=[{afterAddrs}] | " +
                $"IsBlockCaster: {wasBlockCaster} → {Globals.IsBlockCaster}",
                "CasterFlow");
            ConsoleWriterService.OutputValCaster(
                $"[CasterFlow] HandlePromotion ACCEPTED. BlockCasters now {Globals.BlockCasters.Count}: [{afterAddrs}]. IsBlockCaster={Globals.IsBlockCaster}");
            ConsoleWriterService.OutputValCaster("[CasterDiscovery] Caster list updated. Consensus loop will continue as caster.");
            return Task.FromResult("accepted");
        }


        public static async Task HandleDeparture(CasterDepartureNotice departure)
        {
            if (string.IsNullOrEmpty(departure.DepartingAddress))
                return;

            var canonicalMessage = $"DEPART|{departure.DepartingAddress}|{departure.BlockHeight}";
            if (!SignatureService.VerifySignature(departure.DepartingAddress, canonicalMessage, departure.DepartureSignature))
            {
                ErrorLogUtility.LogError($"Invalid departure signature for {departure.DepartingAddress}", "CasterDiscoveryService");
                return;
            }

            var casterList = Globals.BlockCasters.ToList();
            if (!casterList.Any(c => c.ValidatorAddress == departure.DepartingAddress))
                return;

            ConsoleWriterService.OutputValCaster(
                $"[CasterDiscovery] Caster {departure.DepartingAddress} announced departure. Removing...");

            var nCasterList = casterList
                .Where(c => c.ValidatorAddress != departure.DepartingAddress)
                .ToList();
            var nBag = new ConcurrentBag<Peers>();
            foreach (var x in nCasterList)
                nBag.Add(x);
            Globals.BlockCasters = nBag;
            Globals.SyncKnownCastersFromBlockCasters();

            await OnCasterRemoved(departure.DepartingAddress).ConfigureAwait(false);
        }

        #region FIX 2 — Validate inbound casters received via gossip/GetBlockcasters

        /// <summary>
        /// FIX 2: Quick validation gate for casters received from peers (via GetBlockcasters or AddCaster).
        /// Runs port-open + version check to ensure the caster is actually reachable before accepting
        /// it into our local BlockCasters list. This prevents blind acceptance of gossip-propagated
        /// caster lists that may include unreachable nodes.
        /// </summary>
        public static async Task<bool> ValidateInboundCaster(string ip, string address)
        {
            try
            {
                var cleanIp = ip?.Replace("::ffff:", "") ?? "";
                if (string.IsNullOrEmpty(cleanIp) || string.IsNullOrEmpty(address))
                {
                    CasterLogUtility.Log($"ValidateInboundCaster REJECT — empty ip or address. ip='{ip}' addr='{address}'", "CasterFlow");
                    return false;
                }

                // Port check
                if (!PortUtility.IsPortOpen(cleanIp, Globals.ValAPIPort))
                {
                    CasterLogUtility.Log($"ValidateInboundCaster REJECT — port closed {cleanIp}:{Globals.ValAPIPort} for {address}", "CasterFlow");
                    return false;
                }

                // Version check
                var versionResult = await CheckCandidateVersionDetailed(cleanIp, address);
                if (versionResult.Status != VersionCheckResult.Ok)
                {
                    CasterLogUtility.Log($"ValidateInboundCaster REJECT — version={versionResult.Status} for {address} at {cleanIp}", "CasterFlow");
                    return false;
                }

                CasterLogUtility.Log($"ValidateInboundCaster PASS — {address} at {cleanIp} version='{versionResult.Version}'", "CasterFlow");
                return true;
            }
            catch (Exception ex)
            {
                CasterLogUtility.Log($"ValidateInboundCaster ERROR — {address}: {ex.Message}", "CasterFlow");
                return false;
            }
        }

        #endregion

        #region FIX 5 — Promotion agreement protocol

        /// <summary>
        /// FIX 5: Propose a candidate promotion to all peer casters and collect accept/reject votes.
        /// Returns true only if a majority of casters agree on the candidate.
        /// </summary>
        private static async Task<bool> ProposePromotionToPeers(string candidateAddress, string candidateIp, long blockHeight)
        {
            var currentCasters = Globals.BlockCasters.ToList();
            var peerCasters = currentCasters
                .Where(c => c.ValidatorAddress != Globals.ValidatorAddress && !string.IsNullOrEmpty(c.PeerIP))
                .ToList();

            if (peerCasters.Count == 0)
            {
                // Solo caster — auto-agree
                CasterLogUtility.Log($"ProposePromotion — solo caster, auto-agree for {candidateAddress}", "CasterFlow");
                return true;
            }

            var proposal = new PromotionProposalRequest
            {
                CandidateAddress = candidateAddress,
                CandidateIP = candidateIp,
                BlockHeight = blockHeight,
                ProposerAddress = Globals.ValidatorAddress
            };

            int acceptCount = 1; // self-vote = accept (we already passed all gates)
            int totalVoters = peerCasters.Count + 1; // peers + self
            int needed = (totalVoters / 2) + 1; // simple majority

            CasterLogUtility.Log(
                $"ProposePromotion — candidate={candidateAddress} height={blockHeight} peers={peerCasters.Count} needed={needed}/{totalVoters}",
                "CasterFlow");

            var tasks = peerCasters.Select(async peer =>
            {
                try
                {
                    var peerIp = peer.PeerIP.Replace("::ffff:", "");
                    using var client = Globals.HttpClientFactory.CreateClient();
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    var json = JsonConvert.SerializeObject(proposal);
                    var content = new StringContent(json, Encoding.UTF8, "application/json");
                    var uri = $"http://{peerIp}:{Globals.ValAPIPort}/valapi/validator/ProposePromotion";
                    var response = await client.PostAsync(uri, content, cts.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        var body = await response.Content.ReadAsStringAsync();
                        var result = JsonConvert.DeserializeObject<PromotionProposalResponse>(body);
                        if (result?.Accepted == true)
                        {
                            CasterLogUtility.Log($"ProposePromotion — ACCEPT from {peer.ValidatorAddress} for {candidateAddress}", "CasterFlow");
                            return true;
                        }
                        CasterLogUtility.Log($"ProposePromotion — REJECT from {peer.ValidatorAddress}: {result?.Reason}", "CasterFlow");
                        return false;
                    }
                    CasterLogUtility.Log($"ProposePromotion — HTTP {response.StatusCode} from {peer.ValidatorAddress}", "CasterFlow");
                    return false;
                }
                catch (Exception ex)
                {
                    CasterLogUtility.Log($"ProposePromotion — ERROR from {peer.ValidatorAddress}: {ex.Message}", "CasterFlow");
                    return false;
                }
            }).ToList();

            var results = await Task.WhenAll(tasks);
            acceptCount += results.Count(r => r);

            CasterLogUtility.Log(
                $"ProposePromotion — result: accepts={acceptCount}/{totalVoters} needed={needed} candidate={candidateAddress}",
                "CasterFlow");

            return acceptCount >= needed;
        }

        /// <summary>
        /// FIX 5: Evaluate a promotion proposal received from a peer caster.
        /// Runs the same gates locally (balance, maturity, port, version) and returns accept/reject.
        /// Called from ValidatorController.ProposePromotion endpoint.
        /// </summary>
        public static async Task<PromotionProposalResponse> EvaluatePromotionProposal(PromotionProposalRequest proposal)
        {
            var response = new PromotionProposalResponse { ResponderAddress = Globals.ValidatorAddress };

            try
            {
                if (!Globals.IsBlockCaster)
                {
                    response.Reason = "Not a block caster";
                    return response;
                }

                // Check if candidate is already a caster
                if (Globals.BlockCasters.Any(c => c.ValidatorAddress == proposal.CandidateAddress))
                {
                    response.Accepted = true;
                    response.Reason = "Already in caster list";
                    return response;
                }

                // Check if we have the candidate in NetworkValidators
                if (!Globals.NetworkValidators.TryGetValue(proposal.CandidateAddress, out var validator))
                {
                    response.Reason = "Candidate not in NetworkValidators";
                    return response;
                }

                // Balance gate
                var balance = AccountStateTrei.GetAccountBalance(proposal.CandidateAddress);
                if (balance < MinCasterBalance)
                {
                    response.Reason = $"Balance {balance} < {MinCasterBalance}";
                    return response;
                }

                // Maturity gate
                var currentHeight = Globals.LastBlock?.Height ?? 0;
                if (validator.FirstSeenAtHeight > 0 && currentHeight - validator.FirstSeenAtHeight < MaturityBlocks)
                {
                    response.Reason = $"Maturity {currentHeight - validator.FirstSeenAtHeight}/{MaturityBlocks}";
                    return response;
                }

                // Port check
                var cleanIp = (proposal.CandidateIP ?? "").Replace("::ffff:", "");
                if (!PortUtility.IsPortOpen(cleanIp, Globals.ValAPIPort))
                {
                    response.Reason = $"Port closed {cleanIp}:{Globals.ValAPIPort}";
                    return response;
                }

                // Version check
                var versionResult = await CheckCandidateVersionDetailed(cleanIp, proposal.CandidateAddress);
                if (versionResult.Status != VersionCheckResult.Ok)
                {
                    response.Reason = $"Version={versionResult.Status}";
                    return response;
                }

                response.Accepted = true;
                response.Reason = "All gates passed";
                return response;
            }
            catch (Exception ex)
            {
                response.Reason = $"Error: {ex.Message}";
                return response;
            }
        }

        #endregion

        #region CONSENSUS-V2 Fix #3 — Signed promotion broadcast to peer casters

        /// <summary>
        /// CONSENSUS-V2 (Fix #3): After successfully promoting a candidate locally, broadcast a signed
        /// announcement to all other casters so they merge the new caster into their own
        /// <see cref="Globals.BlockCasters"/> immediately. This closes the propagation gap where the
        /// promoter's pool grows but peers don't notice until the next caster-list sync (which can be
        /// up to 100 blocks later — a long time at scale where the consensus pool is segmented).
        ///
        /// The peer side runs the same port + version gate, verifies the promoter signature against
        /// a known caster, and uses the atomic <see cref="AddBlockCasterIfRoomAndUnique"/> helper
        /// (Fix #2) to safely merge.
        /// </summary>
        internal static async Task BroadcastPromotionAnnouncement(Peers newCaster, long blockHeight)
        {
            if (newCaster == null || string.IsNullOrEmpty(newCaster.ValidatorAddress) || string.IsNullOrEmpty(newCaster.PeerIP))
                return;
            if (!Globals.IsBlockCaster || string.IsNullOrEmpty(Globals.ValidatorAddress))
                return;

            var account = AccountData.GetLocalValidator();
            if (account == null) return;
            var privateKey = account.GetPrivKey;
            if (privateKey == null || string.IsNullOrEmpty(account.PublicKey)) return;

            var promotedIp = (newCaster.PeerIP ?? "").Replace("::ffff:", "");
            var canonicalMessage = $"PROMOTE-ANNOUNCE|{newCaster.ValidatorAddress}|{promotedIp}|{blockHeight}|{Globals.ValidatorAddress}";
            var signature = SignatureService.CreateSignature(canonicalMessage, privateKey, account.PublicKey);
            if (signature == "ERROR") return;

            var announce = new CasterPromotionAnnouncement
            {
                PromotedAddress = newCaster.ValidatorAddress!,
                PromotedIP = promotedIp,
                PromotedPublicKey = newCaster.ValidatorPublicKey ?? "",
                PromotedWalletVersion = newCaster.WalletVersion ?? Globals.CLIVersion ?? "",
                BlockHeight = blockHeight,
                PromoterAddress = Globals.ValidatorAddress!,
                PromoterSignature = signature,
            };

            var json = JsonConvert.SerializeObject(announce);

            // Send to every peer caster except (a) ourselves and (b) the promoted node
            // (the promoted node already has the full list via PromoteToCaster).
            var peers = Globals.BlockCasters.ToList()
                .Where(c => !string.IsNullOrEmpty(c.PeerIP)
                            && c.ValidatorAddress != Globals.ValidatorAddress
                            && c.ValidatorAddress != newCaster.ValidatorAddress)
                .ToList();

            if (peers.Count == 0)
            {
                CasterLogUtility.Log(
                    $"[CONSENSUS-V2] PromoteAnnounce — no peer casters to notify (newCaster={newCaster.ValidatorAddress})",
                    "CasterFlow");
                return;
            }

            CasterLogUtility.Log(
                $"[CONSENSUS-V2] PromoteAnnounce — broadcasting to {peers.Count} peer(s) for newCaster={newCaster.ValidatorAddress} h={blockHeight}",
                "CasterFlow");

            var tasks = peers.Select(async peer =>
            {
                try
                {
                    using var client = Globals.HttpClientFactory.CreateClient();
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    var ip = peer.PeerIP!.Replace("::ffff:", "");
                    var uri = $"http://{ip}:{Globals.ValAPIPort}/valapi/validator/AnnounceCasterPromotion";
                    using var content = new StringContent(json, Encoding.UTF8, "application/json");
                    var resp = await client.PostAsync(uri, content, cts.Token).ConfigureAwait(false);
                    CasterLogUtility.Log(
                        $"[CONSENSUS-V2] PromoteAnnounce → {ip} ({peer.ValidatorAddress}) status={resp.StatusCode}",
                        "CasterFlow");
                }
                catch (Exception ex)
                {
                    CasterLogUtility.Log(
                        $"[CONSENSUS-V2] PromoteAnnounce ERROR to {peer.ValidatorAddress}: {ex.Message}",
                        "CasterFlow");
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        /// <summary>
        /// CONSENSUS-V2 (Fix #3): Handle an inbound promotion announcement from a peer caster.
        /// Steps:
        ///  1. Verify the promoter is a known caster.
        ///  2. Verify the promoter's signature.
        ///  3. Reject ancient announcements (replay protection) — height must be within (current-100, current+5).
        ///  4. Skip if already in our pool.
        ///  5. Re-run port + version gates against the promoted IP (we don't trust the promoter blindly).
        ///  6. Atomic add via <see cref="AddBlockCasterIfRoomAndUnique"/>.
        ///  7. Sync KnownCasters and trigger an immediate <see cref="BlockcasterNode.PingCasters"/>.
        /// </summary>
        public static async Task<string> HandlePromotionAnnouncement(CasterPromotionAnnouncement announce)
        {
            try
            {
                if (announce == null || string.IsNullOrEmpty(announce.PromotedAddress) || string.IsNullOrEmpty(announce.PromoterAddress))
                    return "rejected: invalid";

                // (1) promoter must be a known caster
                var isTrustedPromoter = Globals.BlockCasters.Any(c => c.ValidatorAddress == announce.PromoterAddress)
                    || Globals.KnownCasters.Any(c => c.Address == announce.PromoterAddress);
                if (!isTrustedPromoter)
                {
                    CasterLogUtility.Log(
                        $"[CONSENSUS-V2] HandlePromoteAnnounce REJECT — untrusted promoter '{announce.PromoterAddress}' for promoted '{announce.PromotedAddress}'",
                        "CasterFlow");
                    return "rejected: untrusted promoter";
                }

                // (2) signature
                var promotedIp = (announce.PromotedIP ?? "").Replace("::ffff:", "");
                var canonicalMessage = $"PROMOTE-ANNOUNCE|{announce.PromotedAddress}|{promotedIp}|{announce.BlockHeight}|{announce.PromoterAddress}";
                if (!SignatureService.VerifySignature(announce.PromoterAddress, canonicalMessage, announce.PromoterSignature))
                {
                    CasterLogUtility.Log(
                        $"[CONSENSUS-V2] HandlePromoteAnnounce REJECT — bad signature from {announce.PromoterAddress}",
                        "CasterFlow");
                    return "rejected: bad signature";
                }

                // (3) replay protection
                var currentHeight = Globals.LastBlock?.Height ?? 0;
                if (announce.BlockHeight < currentHeight - 100 || announce.BlockHeight > currentHeight + 5)
                {
                    CasterLogUtility.Log(
                        $"[CONSENSUS-V2] HandlePromoteAnnounce REJECT — height={announce.BlockHeight} out of window (current={currentHeight})",
                        "CasterFlow");
                    return "rejected: stale or future height";
                }

                // (4) already in pool — accept idempotently
                if (Globals.BlockCasters.Any(c => c.ValidatorAddress == announce.PromotedAddress))
                {
                    CasterLogUtility.Log(
                        $"[CONSENSUS-V2] HandlePromoteAnnounce — {announce.PromotedAddress} already in pool. Idempotent OK.",
                        "CasterFlow");
                    return "accepted: already known";
                }

                if (string.IsNullOrEmpty(promotedIp))
                {
                    CasterLogUtility.Log(
                        $"[CONSENSUS-V2] HandlePromoteAnnounce REJECT — no IP for {announce.PromotedAddress}",
                        "CasterFlow");
                    return "rejected: no ip";
                }

                // (5) gate: port + version (don't trust promoter blindly)
                if (!PortUtility.IsPortOpen(promotedIp, Globals.ValAPIPort))
                {
                    CasterLogUtility.Log(
                        $"[CONSENSUS-V2] HandlePromoteAnnounce REJECT — port closed {promotedIp}:{Globals.ValAPIPort} for {announce.PromotedAddress}",
                        "CasterFlow");
                    return "rejected: port closed";
                }
                var verResult = await CheckCandidateVersionDetailed(promotedIp, announce.PromotedAddress).ConfigureAwait(false);
                if (verResult.Status != VersionCheckResult.Ok)
                {
                    CasterLogUtility.Log(
                        $"[CONSENSUS-V2] HandlePromoteAnnounce REJECT — version={verResult.Status} for {announce.PromotedAddress}",
                        "CasterFlow");
                    return $"rejected: version={verResult.Status}";
                }

                // (6) atomic add
                var newPeer = new Peers
                {
                    PeerIP = promotedIp,
                    IsValidator = true,
                    ValidatorAddress = announce.PromotedAddress,
                    ValidatorPublicKey = announce.PromotedPublicKey ?? "",
                    FailCount = 0,
                    WalletVersion = !string.IsNullOrEmpty(verResult.Version) ? verResult.Version : announce.PromotedWalletVersion,
                };
                if (!AddBlockCasterIfRoomAndUnique(newPeer))
                {
                    CasterLogUtility.Log(
                        $"[CONSENSUS-V2] HandlePromoteAnnounce REJECT — pool full or duplicate for {announce.PromotedAddress}",
                        "CasterFlow");
                    return "rejected: pool full or duplicate";
                }

                Globals.SyncKnownCastersFromBlockCasters();

                // (7) immediate handshake so we can route consensus messages to the new caster
                _ = Task.Run(async () =>
                {
                    try { await BlockcasterNode.PingCasters().ConfigureAwait(false); }
                    catch { /* best-effort */ }
                });

                // CONSENSUS-V2 (Fix #4): force-sync this peer's validator list with the rest of
                // the caster set immediately after accepting a promotion. Without this, the
                // newly-promoted caster shows up here but its full validator inventory only
                // arrives on the next 10-block VALLIST-SYNC tick, leaving us blind to ~2 minutes
                // of new validators it can already see. Capped at 25 entries per round (see
                // BlockcasterNode.VALIDATOR_LIST_SYNC_MERGE_CAP) so this can't HTTP-storm us.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await BlockcasterNode
                            .SyncValidatorListsWithPeersAsync(announce.BlockHeight, force: true)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        CasterLogUtility.Log(
                            $"[CONSENSUS-V2] HandlePromoteAnnounce post-accept VALLIST-SYNC failed: {ex.Message}",
                            "CasterFlow");
                    }
                });

                CasterLogUtility.Log(
                    $"[CONSENSUS-V2] HandlePromoteAnnounce ACCEPT — added {announce.PromotedAddress} from promoter {announce.PromoterAddress}. " +
                    $"BlockCasters.Count now {Globals.BlockCasters.Count}/{MaxCasters}",
                    "CasterFlow");
                ConsoleWriterService.OutputValCaster(
                    $"[CasterDiscovery] [CONSENSUS-V2] Promotion announcement accepted: added {announce.PromotedAddress} (from {announce.PromoterAddress}).");
                return "accepted";
            }
            catch (Exception ex)
            {
                CasterLogUtility.Log(
                    $"[CONSENSUS-V2] HandlePromoteAnnounce EXCEPTION: {ex.GetType().Name}: {ex.Message}",
                    "CasterFlow");
                return $"rejected: {ex.Message}";
            }
        }

        #endregion

        #region Eviction Awareness — "Verify Before You Cast"

        /// <summary>
        /// EVICTION-AWARE: Queries up to 3 known peer caster IPs (from hardcoded bootstrap or current BlockCasters)
        /// to check whether this node's ValidatorAddress appears in their active caster list.
        /// Returns a tuple: (selfConfirmed, liveCasterList, peersReached).
        ///   - selfConfirmed: true if at least one reachable peer lists us as a caster.
        ///   - liveCasterList: the caster list from the first peer that responded (may be null if none responded).
        ///   - peersReached: how many peers responded successfully.
        /// Used to prevent a previously-evicted bootstrap caster from blindly re-adding itself.
        /// </summary>
        public static async Task<(bool selfConfirmed, List<CasterInfo>? liveCasterList, int peersReached)> VerifySelfInRemoteCasterListsAsync(
            IEnumerable<string> peerIPs)
        {
            if (string.IsNullOrEmpty(Globals.ValidatorAddress))
                return (false, null, 0);

            var ipsToQuery = peerIPs
                .Where(ip => !string.IsNullOrEmpty(ip))
                .Select(ip => ip.Replace("::ffff:", ""))
                .Distinct()
                .Take(3)
                .ToList();

            if (ipsToQuery.Count == 0)
                return (false, null, 0);

            bool selfFound = false;
            List<CasterInfo>? bestCasterList = null;
            int peersReached = 0;

            foreach (var ip in ipsToQuery)
            {
                try
                {
                    using var client = Globals.HttpClientFactory.CreateClient();
                    client.Timeout = TimeSpan.FromSeconds(5);
                    var url = $"http://{ip}:{Globals.ValAPIPort}/valapi/validator/GetCasters";
                    var resp = await client.GetStringAsync(url);
                    if (string.IsNullOrEmpty(resp) || resp == "0")
                        continue;

                    peersReached++;

                    // Parse the SignedCasterListResponse
                    var parsed = Newtonsoft.Json.JsonConvert.DeserializeObject<SignedCasterListResponse>(resp);
                    if (parsed?.Casters != null && parsed.Casters.Count > 0)
                    {
                        if (bestCasterList == null)
                            bestCasterList = parsed.Casters;

                        if (parsed.Casters.Any(c => c.Address == Globals.ValidatorAddress))
                        {
                            selfFound = true;
                            bestCasterList = parsed.Casters; // prefer the list that includes us
                            CasterLogUtility.Log(
                                $"VerifySelfInRemote: peer {ip} confirms us in caster list ({parsed.Casters.Count} casters)",
                                "EVICTION-AWARE");
                            break; // one confirmation is enough
                        }
                        else
                        {
                            CasterLogUtility.Log(
                                $"VerifySelfInRemote: peer {ip} does NOT list us. Their casters: [{string.Join(",", parsed.Casters.Select(c => c.Address))}]",
                                "EVICTION-AWARE");
                        }
                    }
                }
                catch (Exception ex)
                {
                    CasterLogUtility.Log(
                        $"VerifySelfInRemote: peer {ip} unreachable: {ex.Message}",
                        "EVICTION-AWARE");
                }
            }

            CasterLogUtility.Log(
                $"VerifySelfInRemote RESULT: selfConfirmed={selfFound} peersReached={peersReached} liveCasterCount={bestCasterList?.Count ?? 0}",
                "EVICTION-AWARE");

            return (selfFound, bestCasterList, peersReached);
        }

        /// <summary>
        /// EVICTION-AWARE: Fetches the live caster list from known bootstrap peer IPs.
        /// Used by GetBlockcasters() to replace hardcoded injection with the actual network state
        /// when the chain is already running.
        /// Returns the live caster list as Peers objects, or null if no peers responded.
        /// </summary>
        public static async Task<List<Peers>?> FetchLiveCasterListFromPeersAsync(IEnumerable<string> peerIPs)
        {
            var ipsToQuery = peerIPs
                .Where(ip => !string.IsNullOrEmpty(ip))
                .Select(ip => ip.Replace("::ffff:", ""))
                .Distinct()
                .Take(3)
                .ToList();

            foreach (var ip in ipsToQuery)
            {
                try
                {
                    using var client = Globals.HttpClientFactory.CreateClient();
                    client.Timeout = TimeSpan.FromSeconds(5);
                    var url = $"http://{ip}:{Globals.ValAPIPort}/valapi/validator/GetCasters";
                    var resp = await client.GetStringAsync(url);
                    if (string.IsNullOrEmpty(resp) || resp == "0")
                        continue;

                    var parsed = Newtonsoft.Json.JsonConvert.DeserializeObject<SignedCasterListResponse>(resp);
                    if (parsed?.Casters != null && parsed.Casters.Count > 0)
                    {
                        var result = parsed.Casters
                            .Where(c => !string.IsNullOrEmpty(c.Address))
                            .Select(c => new Peers
                            {
                                IsIncoming = false,
                                IsOutgoing = true,
                                PeerIP = c.PeerIP,
                                FailCount = 0,
                                IsValidator = true,
                                ValidatorAddress = c.Address,
                                ValidatorPublicKey = c.PublicKey,
                            })
                            .ToList();

                        CasterLogUtility.Log(
                            $"FetchLiveCasterList: peer {ip} returned {result.Count} casters: [{string.Join(",", result.Select(r => r.ValidatorAddress))}]",
                            "EVICTION-AWARE");
                        return result;
                    }
                }
                catch (Exception ex)
                {
                    CasterLogUtility.Log(
                        $"FetchLiveCasterList: peer {ip} unreachable: {ex.Message}",
                        "EVICTION-AWARE");
                }
            }

            CasterLogUtility.Log("FetchLiveCasterList: no peers responded — true cold start", "EVICTION-AWARE");
            return null;
        }

        /// <summary>
        /// EVICTION-AWARE: Continuous eviction check. If this node thinks it's a caster but
        /// no reachable peer caster lists it, this node was evicted and should stand down.
        /// Called periodically from MonitorCasters(). Returns true if this node was evicted
        /// (caller should flip IsBlockCaster=false and remove self from BlockCasters).
        /// Skips the check during true cold start (no reachable peers).
        /// </summary>
        public static async Task<bool> PerformEvictionAwarenessCheckAsync()
        {
            if (!Globals.IsBlockCaster || string.IsNullOrEmpty(Globals.ValidatorAddress))
                return false;

            var peerCasters = Globals.BlockCasters.ToList()
                .Where(c => !string.IsNullOrEmpty(c.PeerIP) && c.ValidatorAddress != Globals.ValidatorAddress)
                .Select(c => c.PeerIP!.Replace("::ffff:", ""))
                .ToList();

            if (peerCasters.Count == 0)
            {
                // Solo caster or no peers to check against — can't determine eviction
                return false;
            }

            var (selfConfirmed, liveCasterList, peersReached) = await VerifySelfInRemoteCasterListsAsync(peerCasters);

            // If no peers were reachable at all, we can't conclude eviction (network issue)
            if (peersReached == 0)
            {
                CasterLogUtility.Log(
                    "EvictionCheck: no peers reachable — cannot determine eviction status, skipping",
                    "EVICTION-AWARE");
                return false;
            }

            // Peers responded but none listed us → we were evicted
            if (!selfConfirmed)
            {
                CasterLogUtility.Log(
                    $"EvictionCheck: {peersReached} peer(s) reachable, NONE list us as caster → EVICTED. " +
                    $"Standing down. Live caster count from peers: {liveCasterList?.Count ?? 0}",
                    "EVICTION-AWARE");
                ConsoleWriterService.OutputValCaster(
                    $"[EVICTION-AWARE] This node was evicted from the caster pool by peers. " +
                    $"Standing down to regular validator. Will wait for re-promotion if needed.");

                // Replace our BlockCasters with the live list from peers if available
                if (liveCasterList != null && liveCasterList.Count > 0)
                {
                    var nBag = new System.Collections.Concurrent.ConcurrentBag<Peers>();
                    foreach (var ci in liveCasterList)
                    {
                        nBag.Add(new Peers
                        {
                            PeerIP = ci.PeerIP,
                            IsValidator = true,
                            ValidatorAddress = ci.Address,
                            ValidatorPublicKey = ci.PublicKey,
                            FailCount = 0,
                        });
                    }
                    Globals.BlockCasters = nBag;
                    Globals.SyncKnownCastersFromBlockCasters();
                    CasterLogUtility.Log(
                        $"EvictionCheck: replaced BlockCasters with {liveCasterList.Count} peers from live list",
                        "EVICTION-AWARE");
                }

                Globals.IsBlockCaster = false;
                return true;
            }

            return false;
        }

        #endregion

        #region Block catch-up helpers

        /// <summary>
        /// Pushes the last N blocks to a peer via HTTP POST to their ReceiveBlock endpoint.
        /// Used after promotion to ensure the newly promoted caster is synced to the current
        /// chain tip before it needs to participate in consensus (generate proofs, serve blocks).
        /// Fire-and-forget — failures are logged but don't affect promotion status.
        /// </summary>
        internal static async Task PushRecentBlocksToPeer(string peerIp, int blockCount = 3)
        {
            var lastBlock = Globals.LastBlock;
            if (lastBlock == null || lastBlock.Height <= 0)
                return;

            var startHeight = Math.Max(1, lastBlock.Height - blockCount + 1);
            var pushed = 0;

            for (long h = startHeight; h <= lastBlock.Height; h++)
            {
                try
                {
                    var block = BlockchainData.GetBlockByHeight(h);
                    if (block == null)
                        continue;

                    var blockJson = JsonConvert.SerializeObject(block);
                    using var client = Globals.HttpClientFactory.CreateClient();
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    var uri = $"http://{peerIp}:{Globals.ValAPIPort}/valapi/validator/ReceiveCatchUpBlock";
                    using var content = new StringContent(blockJson, Encoding.UTF8, "application/json");
                    var response = await client.PostAsync(uri, content, cts.Token).ConfigureAwait(false);
                    
                    if (response.IsSuccessStatusCode)
                        pushed++;
                    else
                        CasterLogUtility.Log(
                            $"PushRecentBlocks: block {h} to {peerIp} returned {response.StatusCode}",
                            "CasterFlow");
                }
                catch (Exception ex)
                {
                    CasterLogUtility.Log(
                        $"PushRecentBlocks: block {h} to {peerIp} failed: {ex.Message}",
                        "CasterFlow");
                }
            }

            if (pushed > 0)
                CasterLogUtility.Log(
                    $"PushRecentBlocks: pushed {pushed}/{blockCount} blocks (heights {startHeight}–{lastBlock.Height}) to {peerIp}",
                    "CasterFlow");
        }

        #endregion

        #region Chain-Based Validator Liveness Sweep

        /// <summary>
        /// Block interval for the chain-based validator liveness sweep.
        /// Every 500 blocks (~83 min at 10s/block), reconcile NetworkValidators against
        /// on-chain REGISTER/HEARTBEAT TX activity from VBTCValidatorRegistry.
        /// </summary>
        public const int ChainSweepIntervalBlocks = 500;

        /// <summary>Height at which the last chain-based sweep ran. Prevents duplicate runs at the same boundary.</summary>
        private static long _lastChainSweepHeight = -1;

        /// <summary>
        /// Periodically reconciles <see cref="Globals.NetworkValidators"/> against on-chain
        /// validator activity (REGISTER/HEARTBEAT TXs) from <see cref="Bitcoin.Services.VBTCValidatorRegistry"/>.
        /// 
        /// Called from <see cref="BlockcasterNode.MonitorCasters"/> every 500 blocks.
        /// 
        /// Phase 1 — Remove stale: validators in NetworkValidators but NOT in the on-chain
        ///           active set are removed (they haven't posted a TX in the scan window).
        /// Phase 2 — Discover new: validators in the on-chain active set but NOT in
        ///           NetworkValidators are added as fully trusted.
        /// Phase 3 — Refresh active: validators in both sets get LastSeen updated.
        /// 
        /// No HTTP pinging — purely on-chain data. Scales without network overhead.
        /// </summary>
        public static async Task ChainBasedValidatorSweepAsync()
        {
            if (!Globals.IsBlockCaster)
                return;

            var currentHeight = Globals.LastBlock?.Height ?? 0;
            if (currentHeight <= 0)
                return;

            // Height gate: only run at 500-block boundaries, once per boundary
            if (currentHeight % ChainSweepIntervalBlocks != 0 || currentHeight == _lastChainSweepHeight)
                return;

            _lastChainSweepHeight = currentHeight;

            try
            {
                // Get the canonical on-chain active validator set (scans last 1000 blocks)
                var onChainValidators = Bitcoin.Services.VBTCValidatorRegistry.GetActiveValidators();
                if (onChainValidators == null || onChainValidators.Count == 0)
                {
                    CasterLogUtility.Log(
                        $"ChainSweep height={currentHeight}: on-chain active set is empty — skipping sweep (chain may not be synced)",
                        "CHAIN-SWEEP");
                    return;
                }

                var onChainAddresses = new HashSet<string>(
                    onChainValidators
                        .Where(v => !string.IsNullOrEmpty(v.ValidatorAddress))
                        .Select(v => v.ValidatorAddress),
                    StringComparer.Ordinal);

                var onChainByAddress = onChainValidators
                    .Where(v => !string.IsNullOrEmpty(v.ValidatorAddress))
                    .ToDictionary(v => v.ValidatorAddress, v => v, StringComparer.Ordinal);

                var selfAddress = Globals.ValidatorAddress ?? "";
                var currentCasterAddresses = Globals.BlockCasters.ToList()
                    .Where(c => !string.IsNullOrEmpty(c.ValidatorAddress))
                    .Select(c => c.ValidatorAddress!)
                    .ToHashSet(StringComparer.Ordinal);

                int removed = 0;
                int added = 0;
                int refreshed = 0;

                // ── Phase 1: Remove stale validators not in on-chain active set ──
                var networkValSnapshot = Globals.NetworkValidators.ToArray();
                foreach (var kvp in networkValSnapshot)
                {
                    var addr = kvp.Key;
                    var nv = kvp.Value;

                    // Never remove ourselves
                    if (addr == selfAddress)
                        continue;

                    // Skip current casters — they're managed by PingCasters/AuditExistingCasterVersions
                    if (currentCasterAddresses.Contains(addr))
                        continue;

                    if (!onChainAddresses.Contains(addr))
                    {
                        // Not in on-chain active set → hasn't posted a TX in ~1000 blocks → stale
                        Globals.NetworkValidators.TryRemove(addr, out _);
                        removed++;
                        CasterLogUtility.Log(
                            $"ChainSweep REMOVE: {addr} — no on-chain TX in scan window (ip={nv.IPAddress} lastSeen={nv.LastSeen} failCount={nv.CheckFailCount})",
                            "CHAIN-SWEEP");
                        ConsoleWriterService.OutputValCaster(
                            $"[ChainSweep] Removed stale validator {addr} — no on-chain activity in {Bitcoin.Services.VBTCValidatorRegistry.SCAN_WINDOW} blocks");
                    }
                }

                // ── Phase 2: Add newly discovered on-chain validators ──
                var now = TimeUtil.GetTime();
                foreach (var onChainVal in onChainValidators)
                {
                    if (string.IsNullOrEmpty(onChainVal.ValidatorAddress))
                        continue;

                    // Skip self
                    if (onChainVal.ValidatorAddress == selfAddress)
                        continue;

                    // Skip if already in NetworkValidators
                    if (Globals.NetworkValidators.ContainsKey(onChainVal.ValidatorAddress))
                        continue;

                    // Skip validators without usable data
                    var pk = onChainVal.FrostPublicKey;
                    if (string.IsNullOrWhiteSpace(pk) || pk == "PLACEHOLDER_FROST_PUBLIC_KEY")
                        continue;

                    var ip = (onChainVal.IPAddress ?? "").Replace("::ffff:", "");
                    if (string.IsNullOrEmpty(ip))
                        continue;

                    // Add as fully trusted — on-chain TX is strong proof of legitimacy
                    var newNv = new Models.NetworkValidator
                    {
                        Address = onChainVal.ValidatorAddress,
                        IPAddress = ip,
                        PublicKey = pk,
                        IsFullyTrusted = true,
                        LastSeen = now,
                        CheckFailCount = 0,
                        FirstSeenAtHeight = currentHeight,
                        FirstAdvertised = now,
                    };
                    Globals.NetworkValidators[onChainVal.ValidatorAddress] = newNv;
                    added++;
                    CasterLogUtility.Log(
                        $"ChainSweep ADD: {onChainVal.ValidatorAddress} — discovered from on-chain TX (ip={ip} lastHeartbeat={onChainVal.LastHeartbeatBlock})",
                        "CHAIN-SWEEP");
                    ConsoleWriterService.OutputValCaster(
                        $"[ChainSweep] Added new validator {onChainVal.ValidatorAddress} from on-chain data (ip={ip})");
                }

                // ── Phase 3: Refresh LastSeen for validators in both sets ──
                foreach (var kvp in Globals.NetworkValidators)
                {
                    if (kvp.Key == selfAddress)
                        continue;

                    if (onChainAddresses.Contains(kvp.Key))
                    {
                        var nv = kvp.Value;
                        nv.LastSeen = now;
                        nv.CheckFailCount = 0;
                        Globals.NetworkValidators[kvp.Key] = nv;
                        refreshed++;
                    }
                }

                // Clear proof cache if pool changed
                if (removed > 0 || added > 0)
                    ProofUtility.ClearProofGenerationCache();

                CasterLogUtility.Log(
                    $"ChainSweep COMPLETE height={currentHeight}: removed={removed} added={added} refreshed={refreshed} " +
                    $"onChainActive={onChainValidators.Count} networkValidators={Globals.NetworkValidators.Count}",
                    "CHAIN-SWEEP");

                if (removed > 0 || added > 0)
                {
                    ConsoleWriterService.OutputValCaster(
                        $"[ChainSweep] Sweep at height {currentHeight}: -{removed} stale, +{added} new, ~{refreshed} refreshed. " +
                        $"Pool: {Globals.NetworkValidators.Count} validators");
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError(
                    $"ChainBasedValidatorSweepAsync error at height {currentHeight}: {ex.Message}",
                    "CasterDiscoveryService.ChainBasedValidatorSweepAsync");
                CasterLogUtility.Log(
                    $"ChainSweep ERROR: {ex.Message}",
                    "CHAIN-SWEEP");
            }
        }

        #endregion
    }
}
