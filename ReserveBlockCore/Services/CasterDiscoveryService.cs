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

        private static long _lastEvaluationHeight = long.MinValue;
        private static long _lastRefreshAtHeight = -1;

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

                    if (!PortUtility.IsPortOpen(ip, Globals.ValAPIPort))
                    {
                        ConsoleWriterService.OutputVal(
                            $"[CasterDiscovery] Candidate {v.Address} port check failed on {ip}:{Globals.ValAPIPort}");
                        continue;
                    }

                    // Version gate: reject candidates on outdated major versions.
                    // This prevents nodes that can't produce valid proofs from inflating the quorum.
                    if (!await CheckCandidateVersion(ip, v.Address))
                        continue;

                    var newCaster = new Peers
                    {
                        PeerIP = ip,
                        IsValidator = true,
                        ValidatorAddress = v.Address,
                        ValidatorPublicKey = v.PublicKey,
                        FailCount = 0,
                    };

                    if (Globals.BlockCasters.Any(c => c.ValidatorAddress == v.Address))
                        continue;

                    Globals.BlockCasters.Add(newCaster);
                    Globals.SyncKnownCastersFromBlockCasters();

                    ConsoleWriterService.OutputVal(
                        $"[CasterDiscovery] Promoted {v.Address} (balance: {candidate.Balance}) to caster. Pool: {Globals.BlockCasters.Count}/{MaxCasters}");

                    await NotifyPromotion(ip, v.Address).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"EvaluateCasterPool error: {ex.Message}", "CasterDiscoveryService");
            }
        }

        /// <summary>
        /// Checks a candidate's wallet version via HTTP before promotion.
        /// Returns true if the candidate is on a compatible major version.
        /// </summary>
        private static async Task<bool> CheckCandidateVersion(string ip, string address)
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
                    return false;
                }
                var peerVersion = await response.Content.ReadAsStringAsync();
                if (string.IsNullOrEmpty(peerVersion) || !ProofUtility.IsMajorVersionCurrent(peerVersion))
                {
                    ConsoleWriterService.OutputVal(
                        $"[CasterDiscovery] VersionGate: Candidate {address} at {ip} reports version '{peerVersion}' — outdated (need major >= {Globals.MajorVer}). Skipping.");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                ConsoleWriterService.OutputVal(
                    $"[CasterDiscovery] VersionGate: Candidate {address} at {ip} — version check failed: {ex.Message}. Skipping.");
                return false;
            }
        }

        /// <summary>
        /// Periodically audits existing casters and removes any on outdated major versions.
        /// Called from MonitorCasters or EvaluateCasterPool to keep the pool clean.
        /// </summary>
        public static async Task AuditExistingCasterVersions()
        {
            if (!Globals.IsBlockCaster)
                return;

            var casters = Globals.BlockCasters.ToList();
            var toRemove = new List<string>();

            foreach (var caster in casters)
            {
                if (string.IsNullOrEmpty(caster.PeerIP) || string.IsNullOrEmpty(caster.ValidatorAddress))
                    continue;

                // Don't audit ourselves
                if (caster.ValidatorAddress == Globals.ValidatorAddress)
                    continue;

                var ip = caster.PeerIP.Replace("::ffff:", "");
                if (!await CheckCandidateVersion(ip, caster.ValidatorAddress))
                {
                    toRemove.Add(caster.ValidatorAddress);
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
                    ConsoleWriterService.OutputVal(
                        $"[CasterDiscovery] VersionAudit: Demoted caster {addr} — outdated version.");
                }

                // Re-evaluate to fill slots
                _lastEvaluationHeight = long.MinValue;
                await EvaluateCasterPool().ConfigureAwait(false);
            }
        }

        private static async Task NotifyPromotion(string peerIP, string address)
        {
            try
            {
                var account = AccountData.GetLocalValidator();
                if (account == null)
                    return;
                var privateKey = account.GetPrivKey;
                if (privateKey == null || string.IsNullOrEmpty(account.PublicKey))
                    return;

                using var client = Globals.HttpClientFactory.CreateClient();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

                var casterList = Globals.BlockCasters.ToList()
                    .Where(c => !string.IsNullOrEmpty(c.ValidatorAddress))
                    .Select(c => new CasterInfo
                    {
                        Address = c.ValidatorAddress!,
                        PeerIP = (c.PeerIP ?? "").Replace("::ffff:", ""),
                        PublicKey = c.ValidatorPublicKey ?? ""
                    })
                    .ToList();

                var blockHeight = Globals.LastBlock.Height;
                var promoterAddress = Globals.ValidatorAddress ?? "";

                var casterListJson = GetCanonicalCasterListJson(casterList);
                var casterListHashHex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(casterListJson))).ToLowerInvariant();
                var canonicalMessage = $"PROMOTE|{address}|{blockHeight}|{promoterAddress}|{casterListHashHex}";
                var signature = SignatureService.CreateSignature(canonicalMessage, privateKey, account.PublicKey);
                if (signature == "ERROR")
                    return;

                var promotion = new CasterPromotionRequest
                {
                    PromotedAddress = address,
                    BlockHeight = blockHeight,
                    CasterList = casterList,
                    PromoterAddress = promoterAddress,
                    PromoterSignature = signature,
                };

                var uri = $"http://{peerIP}:{Globals.ValAPIPort}/valapi/validator/PromoteToCaster";
                var json = JsonConvert.SerializeObject(promotion);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");

                await client.PostAsync(uri, content, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"NotifyPromotion to {peerIP} failed: {ex.Message}", "CasterDiscoveryService");
            }
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

        public static Task HandlePromotion(CasterPromotionRequest promotion)
        {
            if (string.IsNullOrEmpty(Globals.ValidatorAddress))
                return Task.CompletedTask;
            if (promotion.PromotedAddress != Globals.ValidatorAddress)
                return Task.CompletedTask;

            var isTrustedPromoter = Globals.BlockCasters.Any(c => c.ValidatorAddress == promotion.PromoterAddress)
                || Globals.KnownCasters.Any(c => c.Address == promotion.PromoterAddress);
            if (!isTrustedPromoter)
            {
                ErrorLogUtility.LogError($"Promotion from untrusted address {promotion.PromoterAddress} — not in our caster set", "CasterDiscoveryService");
                return Task.CompletedTask;
            }

            if (promotion.CasterList == null
                || !promotion.CasterList.Any(c => c.Address == promotion.PromoterAddress))
            {
                ErrorLogUtility.LogError($"Promotion from {promotion.PromoterAddress} — promoter not in CasterList", "CasterDiscoveryService");
                return Task.CompletedTask;
            }

            if (!promotion.CasterList.Any(c => c.Address == Globals.ValidatorAddress))
            {
                ErrorLogUtility.LogError("Promotion rejected: promoted address missing from signed CasterList", "CasterDiscoveryService");
                return Task.CompletedTask;
            }

            var casterListJson = GetCanonicalCasterListJson(promotion.CasterList);
            var casterListHashHex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(casterListJson))).ToLowerInvariant();
            var canonicalMessage = $"PROMOTE|{promotion.PromotedAddress}|{promotion.BlockHeight}|{promotion.PromoterAddress}|{casterListHashHex}";
            if (!SignatureService.VerifySignature(promotion.PromoterAddress, canonicalMessage, promotion.PromoterSignature))
            {
                ErrorLogUtility.LogError($"Invalid promotion signature from {promotion.PromoterAddress}", "CasterDiscoveryService");
                return Task.CompletedTask;
            }

            ConsoleWriterService.OutputVal(
                $"[CasterDiscovery] THIS NODE has been promoted to caster by {promotion.PromoterAddress} at height {promotion.BlockHeight}!");

            var newBag = new ConcurrentBag<Peers>();
            foreach (var ci in promotion.CasterList)
            {
                newBag.Add(new Peers
                {
                    PeerIP = ci.PeerIP,
                    IsValidator = true,
                    ValidatorAddress = ci.Address,
                    ValidatorPublicKey = ci.PublicKey,
                    FailCount = 0,
                });
            }
            Globals.BlockCasters = newBag;
            Globals.SyncKnownCastersFromBlockCasters();

            // BlockcasterNode.StartCastingRounds already runs as IHostedService; it picks up caster status on next loop.
            if (!Globals.IsBlockCaster)
                Globals.IsBlockCaster = true;

            ConsoleWriterService.OutputVal("[CasterDiscovery] Caster list updated. Consensus loop will continue as caster.");
            return Task.CompletedTask;
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
