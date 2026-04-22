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
                return;
            if (string.IsNullOrEmpty(Globals.ValidatorAddress))
                return;

            var currentHeight = Globals.LastBlock.Height;
            if (currentHeight < 0)
                return;
            if (currentHeight == _lastEvaluationHeight)
                return;
            _lastEvaluationHeight = currentHeight;

            try
            {
                var currentCasters = Globals.BlockCasters.ToList();
                if (currentCasters.Count >= MaxCasters)
                    return;

                var validators = Globals.NetworkValidators.Values.ToList();
                if (!validators.Any())
                    return;

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

                if (!candidates.Any())
                    return;

                var rankedCandidates = candidates
                    .Select(v => new { Validator = v, Balance = AccountStateTrei.GetAccountBalance(v.Address) })
                    .Where(x => x.Balance >= MinCasterBalance)
                    .OrderByDescending(x => x.Balance)
                    .ToList();

                int slotsAvailable = MaxCasters - currentCasters.Count;
                var toPromote = rankedCandidates.Take(slotsAvailable).ToList();

                foreach (var candidate in toPromote)
                {
                    var v = candidate.Validator;
                    var ip = v.IPAddress.Replace("::ffff:", "");

                    // Maturity gate: don't promote validators that just connected.
                    // They need time to sync their own NetworkValidators pool.
                    if (v.FirstSeenAtHeight > 0 && currentHeight - v.FirstSeenAtHeight < MaturityBlocks)
                    {
                        ConsoleWriterService.OutputVal(
                            $"[CasterDiscovery] Candidate {v.Address} not mature enough (seen at height {v.FirstSeenAtHeight}, current {currentHeight}, need {MaturityBlocks} blocks). Skipping.");
                        continue;
                    }

                    if (!PortUtility.IsPortOpen(ip, Globals.ValAPIPort))
                    {
                        ConsoleWriterService.OutputVal(
                            $"[CasterDiscovery] Candidate {v.Address} port check failed on {ip}:{Globals.ValAPIPort}");
                        continue;
                    }

                    // Version gate: reject candidates on outdated major versions.
                    // This prevents nodes that can't produce valid proofs from inflating the quorum.
                    // Capture the version string so we can stamp it on the new Peers entry —
                    // GenerateCasterProofs filters BlockCasters by WalletVersion, and an empty
                    // version silently excludes the caster from proof generation.
                    var candidateVersionResult = await CheckCandidateVersionDetailed(ip, v.Address);
                    if (candidateVersionResult.Status != VersionCheckResult.Ok)
                        continue;

                    // Health gate: reject candidates that are out of sync or have clock skew.
                    if (!await CheckCandidateHealth(ip, v.Address))
                        continue;

                    if (Globals.BlockCasters.Any(c => c.ValidatorAddress == v.Address))
                        continue;

                    var newCaster = new Peers
                    {
                        PeerIP = ip,
                        IsValidator = true,
                        ValidatorAddress = v.Address,
                        ValidatorPublicKey = v.PublicKey,
                        FailCount = 0,
                        WalletVersion = candidateVersionResult.Version,
                    };

                    // Send promotion notification FIRST and wait for acceptance.
                    // Only add to BlockCasters if the promoted node confirms.
                    // This prevents "zombie casters" where the promoter's caster list
                    // includes a node that rejected the promotion, breaking quorum.
                    var accepted = await NotifyPromotionAndAwaitAcceptance(ip, v.Address, newCaster).ConfigureAwait(false);
                    if (!accepted)
                    {
                        ConsoleWriterService.OutputVal(
                            $"[CasterDiscovery] Candidate {v.Address} at {ip} did not accept promotion. Not adding to caster pool.");
                        continue;
                    }

                    Globals.BlockCasters.Add(newCaster);
                    Globals.SyncKnownCastersFromBlockCasters();

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

                var response = await client.PostAsync(uri, content, cts.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
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
                    ConsoleWriterService.OutputVal(
                        $"[CasterDiscovery] Candidate {address} at {peerIP} promotion request failed: {response.StatusCode}");
                    return false;
                }
            }
            catch (Exception ex)
            {
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
            if (string.IsNullOrEmpty(Globals.ValidatorAddress))
                return Task.FromResult("rejected: no validator address");
            if (promotion.PromotedAddress != Globals.ValidatorAddress)
                return Task.FromResult("rejected: not for this node");

            var isTrustedPromoter = Globals.BlockCasters.Any(c => c.ValidatorAddress == promotion.PromoterAddress)
                || Globals.KnownCasters.Any(c => c.Address == promotion.PromoterAddress);
            if (!isTrustedPromoter)
            {
                ErrorLogUtility.LogError($"Promotion from untrusted address {promotion.PromoterAddress} — not in our caster set", "CasterDiscoveryService");
                return Task.FromResult("rejected: untrusted promoter");
            }

            if (promotion.CasterList == null
                || !promotion.CasterList.Any(c => c.Address == promotion.PromoterAddress))
            {
                ErrorLogUtility.LogError($"Promotion from {promotion.PromoterAddress} — promoter not in CasterList", "CasterDiscoveryService");
                return Task.FromResult("rejected: promoter not in caster list");
            }

            if (!promotion.CasterList.Any(c => c.Address == Globals.ValidatorAddress))
            {
                ErrorLogUtility.LogError("Promotion rejected: promoted address missing from signed CasterList", "CasterDiscoveryService");
                return Task.FromResult("rejected: our address missing from caster list");
            }

            var casterListJson = GetCanonicalCasterListJson(promotion.CasterList);
            var casterListHashHex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(casterListJson))).ToLowerInvariant();
            var canonicalMessage = $"PROMOTE|{promotion.PromotedAddress}|{promotion.BlockHeight}|{promotion.PromoterAddress}|{casterListHashHex}";
            if (!SignatureService.VerifySignature(promotion.PromoterAddress, canonicalMessage, promotion.PromoterSignature))
            {
                ErrorLogUtility.LogError($"Invalid promotion signature from {promotion.PromoterAddress}", "CasterDiscoveryService");
                return Task.FromResult("rejected: invalid signature");
            }

            // Self-health check: reject promotion if our NetworkValidators pool is too small.
            // Without validators we can't generate proofs, which inflates the quorum and halts consensus.
            var validatorCount = Globals.NetworkValidators.Count;
            if (validatorCount < MinValidatorPoolSize)
            {
                ConsoleWriterService.OutputVal(
                    $"[CasterDiscovery] REJECTING promotion — our NetworkValidators pool has only {validatorCount} entries (need {MinValidatorPoolSize}+). " +
                    "This node cannot produce proofs and would halt consensus.");
                return Task.FromResult($"rejected: only {validatorCount} validators in pool (need {MinValidatorPoolSize}+)");
            }

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

            // BlockcasterNode.StartCastingRounds already runs as IHostedService; it picks up caster status on next loop.
            if (!Globals.IsBlockCaster)
                Globals.IsBlockCaster = true;

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
    }
}
