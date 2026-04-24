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
                ConsoleWriterService.OutputVal(
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

                rankedCandidates = rankedCandidates
                    .Where(x => x.Balance >= MinCasterBalance)
                    .OrderByDescending(x => x.Balance)
                    .ToList();

                int slotsAvailable = MaxCasters - currentCasters.Count;
                var toPromote = rankedCandidates.Take(slotsAvailable).ToList();
                CasterLogUtility.Log(
                    $"EvalTick — slotsAvailable={slotsAvailable}, attempting promotion of {toPromote.Count} candidate(s): [{string.Join(",", toPromote.Select(p => p.Validator.Address))}]",
                    "CasterFlow");

                foreach (var candidate in toPromote)
                {
                    var v = candidate.Validator;
                    var ip = v.IPAddress.Replace("::ffff:", "");

                    CasterLogUtility.Log(
                        $"  >> Candidate {v.Address} ip={ip} balance={candidate.Balance} firstSeen={v.FirstSeenAtHeight} now={currentHeight} maturityΔ={(v.FirstSeenAtHeight > 0 ? currentHeight - v.FirstSeenAtHeight : -1)}/{MaturityBlocks}",
                        "CasterFlow");
                    ConsoleWriterService.OutputVal($"[CasterFlow] Promoting candidate {v.Address} ({ip})…");

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
                        ConsoleWriterService.OutputVal(
                            $"[CasterDiscovery] Candidate {v.Address} not mature enough (seen at height {v.FirstSeenAtHeight}, current {currentHeight}, need {MaturityBlocks} blocks). Skipping.");
                        continue;
                    }
                    CasterLogUtility.Log($"     maturity=PASS", "CasterFlow");

                    if (!PortUtility.IsPortOpen(ip, Globals.ValAPIPort))
                    {
                        CasterLogUtility.Log($"  <<  portOpen=FAIL {ip}:{Globals.ValAPIPort}", "CasterFlow");
                        ConsoleWriterService.OutputVal(
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
                        ConsoleWriterService.OutputVal(
                            $"[CasterDiscovery] Promotion agreement failed for {v.Address}. Skipping — will retry next height.");
                        continue;
                    }
                    CasterLogUtility.Log($"     promotionAgreement=PASS", "CasterFlow");

                    // Send promotion notification FIRST and wait for acceptance.
                    // Only add to BlockCasters if the promoted node confirms.
                    // This prevents "zombie casters" where the promoter's caster list
                    // includes a node that rejected the promotion, breaking quorum.
                    CasterLogUtility.Log($"     sending PromoteToCaster HTTP to {ip}…", "CasterFlow");
                    ConsoleWriterService.OutputVal($"[CasterFlow] → POST PromoteToCaster {ip}");
                    var accepted = await NotifyPromotionAndAwaitAcceptance(ip, v.Address, newCaster).ConfigureAwait(false);
                    if (!accepted)
                    {
                        CasterLogUtility.Log($"  <<  promotion REJECTED/failed by candidate", "CasterFlow");
                        ConsoleWriterService.OutputVal(
                            $"[CasterDiscovery] Candidate {v.Address} at {ip} did not accept promotion. Not adding to caster pool.");
                        continue;
                    }

                    Globals.BlockCasters.Add(newCaster);
                    Globals.SyncKnownCastersFromBlockCasters();

                    CasterLogUtility.Log(
                        $"  ✓ PROMOTED val={v.Address} ip={ip}. BlockCasters.Count now {Globals.BlockCasters.Count}/{MaxCasters}",
                        "CasterFlow");
                    ConsoleWriterService.OutputVal(
                        $"[CasterDiscovery] Promoted {v.Address} (balance: {candidate.Balance}) to caster. Pool: {Globals.BlockCasters.Count}/{MaxCasters}");
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
                                ConsoleWriterService.OutputVal(
                                    $"[CasterDiscovery] HealthCheck: Candidate {address} at {ip} is {heightDiff} blocks behind (theirs={status.Height}, ours={Globals.LastBlock.Height}). Skipping.");
                                return false;
                            }
                        }
                    }
                }
                else
                {
                    ConsoleWriterService.OutputVal(
                        $"[CasterDiscovery] HealthCheck: Candidate {address} at {ip} — CasterReadyCheck returned {response.StatusCode}. Skipping.");
                    return false;
                }
            }
            catch (Exception ex)
            {
                ConsoleWriterService.OutputVal(
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
                    ConsoleWriterService.OutputVal(
                        $"[CasterDiscovery] VersionGate: Candidate {address} at {ip} — GetWalletVersion returned {response?.StatusCode}. Skipping.");
                    // Got a response but it was an error — treat as unreachable (node might be starting up)
                    return new VersionCheckInfo(VersionCheckResult.Unreachable, "");
                }
                var peerVersion = await response.Content.ReadAsStringAsync();
                if (string.IsNullOrEmpty(peerVersion) || !ProofUtility.IsMajorVersionCurrent(peerVersion))
                {
                    ConsoleWriterService.OutputVal(
                        $"[CasterDiscovery] VersionGate: Candidate {address} at {ip} reports version '{peerVersion}' — outdated (need major >= {Globals.MajorVer}). Skipping.");
                    return new VersionCheckInfo(VersionCheckResult.Outdated, peerVersion ?? "");
                }
                return new VersionCheckInfo(VersionCheckResult.Ok, peerVersion);
            }
            catch (Exception ex)
            {
                ConsoleWriterService.OutputVal(
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
                            ConsoleWriterService.OutputVal(
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
                    ConsoleWriterService.OutputVal(
                        $"[CasterDiscovery] VersionAudit: Demoted caster {addr} — {reason}.");
                }

                // Broadcast demotion to all remaining casters so they remove the node too
                await BroadcastDemotions(toRemove).ConfigureAwait(false);

                // Re-evaluate to fill slots
                _lastEvaluationHeight = long.MinValue;
                await EvaluateCasterPool().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Sends a promotion notification to the candidate and waits for acceptance.
        /// Returns true only if the candidate responds with an acceptance confirmation.
        /// The candidate's caster list includes the new caster so it knows the full pool.
        /// </summary>
        private static async Task<bool> NotifyPromotionAndAwaitAcceptance(string peerIP, string address, Peers newCaster)
        {
            try
            {
                var account = AccountData.GetLocalValidator();
                if (account == null)
                    return false;
                var privateKey = account.GetPrivKey;
                if (privateKey == null || string.IsNullOrEmpty(account.PublicKey))
                    return false;

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
                    return false;

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
                        ConsoleWriterService.OutputVal(
                            $"[CasterDiscovery] Candidate {address} at {peerIP} ACCEPTED promotion.");
                        return true;
                    }
                    else
                    {
                        ConsoleWriterService.OutputVal(
                            $"[CasterDiscovery] Candidate {address} at {peerIP} REJECTED promotion. Response: {body}");
                        return false;
                    }
                }
                else
                {
                    string errBody = "";
                    try { errBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false); } catch { }
                    CasterLogUtility.Log(
                        $"NotifyPromotion ← HTTP ERROR {response.StatusCode} body='{errBody}' (peer={peerIP})",
                        "CasterFlow");
                    ConsoleWriterService.OutputVal(
                        $"[CasterDiscovery] Candidate {address} at {peerIP} promotion request failed: {response.StatusCode}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                CasterLogUtility.Log($"NotifyPromotion EXCEPTION peer={peerIP}: {ex.GetType().Name}: {ex.Message}", "CasterFlow");
                ErrorLogUtility.LogError($"NotifyPromotionAndAwaitAcceptance to {peerIP} failed: {ex.Message}", "CasterDiscoveryService");
                return false;
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

            ConsoleWriterService.OutputVal($"[CasterDiscovery] Demotion notice broadcast for {demotedAddresses.Count} caster(s) to all peers.");
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

            ConsoleWriterService.OutputVal(
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
            ConsoleWriterService.OutputVal(
                $"[CasterDiscovery] Caster {removedAddress} removed. Evaluating pool for replacement...");

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
            ConsoleWriterService.OutputVal("[CasterDiscovery] Departure notice broadcast to all peers.");
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
                ConsoleWriterService.OutputVal(
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

            ConsoleWriterService.OutputVal(
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
            ConsoleWriterService.OutputVal(
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
                ConsoleWriterService.OutputVal(
                    $"[CasterDiscovery] REJECTING promotion — our NetworkValidators pool has only {validatorCount} entries (need {MinValidatorPoolSize}+). " +
                    "This node cannot produce proofs and would halt consensus.");
                return Task.FromResult($"rejected: only {validatorCount} validators in pool (need {MinValidatorPoolSize}+)");
            }

            CasterLogUtility.Log(
                $"HandlePromotion: self-health OK (NetworkValidators.Count={validatorCount}). Applying promotion…",
                "CasterFlow");
            ConsoleWriterService.OutputVal(
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
            ConsoleWriterService.OutputVal(
                $"[CasterFlow] HandlePromotion ACCEPTED. BlockCasters now {Globals.BlockCasters.Count}: [{afterAddrs}]. IsBlockCaster={Globals.IsBlockCaster}");
            ConsoleWriterService.OutputVal("[CasterDiscovery] Caster list updated. Consensus loop will continue as caster.");
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

            ConsoleWriterService.OutputVal(
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
    }
}
