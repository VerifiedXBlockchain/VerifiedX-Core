using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Newtonsoft.Json;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using System.Collections.Concurrent;

namespace VerifiedXCore.Models
{
    public class NetworkValidator
    {
        public string IPAddress { get; set; }
        public string Address { get; set; }
        public string UniqueName { get; set; }
        public string PublicKey { get; set; }
        public string Signature { get; set; }
        public string SignatureMessage { get; set; }
        public int CheckFailCount { get; set; }
        public long Latency { get; set; }
        
        // HAL-26 Fix: TTL tracking for validator registry cleanup
        public long LastSeen { get; set; }
        
        /// <summary>Block height at which this validator was first added to NetworkValidators.
        /// Used for maturity gating — prevents premature caster promotion of freshly-connected nodes.</summary>
        public long FirstSeenAtHeight { get; set; }

        /// <summary>SELF-HEAL (Sep 2026): unix seconds this validator was first added. The maturity gate
        /// accepts <see cref="FirstSeenAtHeight"/> OR this — a height-only gate can never be satisfied on
        /// a halted chain (Δ stays 0), which locked promotion out for the whole testnet 975,533 outage.
        /// Preserved across reconnects like FirstSeenAtHeight. 0 = unknown (height rule only).</summary>
        public long FirstSeenAt { get; set; }

        /// <summary>Last block height reported by this validator via GetBlockHeight HTTP call.
        /// Used by the height gate to exclude still-syncing validators from proof generation
        /// and winner selection. Updated by VerifyWinnerAvailability and proof generation.</summary>
        public long LastKnownHeight { get; set; }

        /// <summary>Unix timestamp (seconds) when LastKnownHeight was last updated.
        /// Height cache expires after HEIGHT_CACHE_TTL_SECONDS to force re-check.</summary>
        public long LastHeightCheckTime { get; set; }
        
        // HAL-11 Security enhancements
        public long AdvertisementTimestamp { get; set; }
        public string AdvertisementNonce { get; set; }
        public HashSet<string> ConfirmingSources { get; set; } = new HashSet<string>();
        public long FirstAdvertised { get; set; }
        public bool IsFullyTrusted { get; set; } = false;
        public string OriginalAdvertiser { get; set; }

        // HAL-11 Security: Rate limiting and source tracking
        private static readonly ConcurrentDictionary<string, ValidatorAdvertisementTracker> _advertisementTrackers = 
            new ConcurrentDictionary<string, ValidatorAdvertisementTracker>();
        
        private static readonly ConcurrentDictionary<string, NetworkValidator> _pendingValidators = 
            new ConcurrentDictionary<string, NetworkValidator>();

        /// <summary>
        /// VX-07: a validator advertisement is signed by the validator over exactly
        /// "{Address}:{unixTime}:{PublicKey}" (ValidatorNode Status). The receiver used to verify a
        /// caller-supplied free-text message, so ANY signature the validator ever produced could carry an
        /// attacker-chosen PublicKey/IP. The message must now be that exact three-part shape and name this
        /// entry's Address and PublicKey (the SignalR handshake signs four parts and other paths two, so a
        /// signature from another purpose cannot be reused here).
        /// </summary>
        /// <summary>
        /// VX-15: a registry entry's PublicKey must be the key that owns its Address (it seeds the VRF, and VX-05
        /// rejects proofs whose key does not derive the address). Writers that take a key from peer data store it only
        /// when it binds; otherwise "" (the entry then has no usable key, exactly as a mismatched key already had).
        /// </summary>
        public static string BoundPublicKey(string? address, string? publicKey) =>
            ConsensusRequestAuth.PublicKeyMatchesAddress(publicKey, address) ? publicKey! : "";

        public static bool TryParseSignedAdvertisement(NetworkValidator validator, out long signedAt)
        {
            signedAt = 0;
            if (validator == null || string.IsNullOrEmpty(validator.SignatureMessage)) return false;
            var parts = validator.SignatureMessage.Split(':');
            if (parts.Length != 3) return false;
            if (parts[0] != validator.Address) return false;
            if (!long.TryParse(parts[1], out signedAt)) return false;
            return parts[2] == validator.PublicKey;
        }

        /// <param name="directFromValidator">
        /// VX-07: true only for the validator's own Status call to this node (IPAddress = the socket IP).
        /// Only a direct advertisement may change an existing entry's IP, key or name, and it must be fresh
        /// and single-use. Gossiped copies (relayed by other peers, carrying the validator's last — possibly
        /// old — signature) can confirm or refresh an entry but never move it.
        /// </param>
        public static async Task<bool> AddValidatorToPool(NetworkValidator validator, string advertisingPeerIP = null, bool directFromValidator = false)
        {
            try
            {
                // VX-07: the signed message must bind this entry's Address and PublicKey…
                if (!TryParseSignedAdvertisement(validator, out var signedAt))
                {
                    ErrorLogUtility.LogError($"Malformed validator advertisement for {validator?.Address} from peer {advertisingPeerIP}", "NetworkValidator.AddValidatorToPool");
                    return false;
                }

                // …and the PublicKey must be the key that owns the Address (it seeds the VRF).
                if (!ConsensusRequestAuth.PublicKeyMatchesAddress(validator.PublicKey, validator.Address))
                {
                    ErrorLogUtility.LogError($"Validator advertisement PublicKey does not derive {validator.Address} (peer {advertisingPeerIP})", "NetworkValidator.AddValidatorToPool");
                    return false;
                }

                // HAL-11 Fix: Enhanced signature verification
                var verifySig = SignatureService.VerifySignature(
                                validator.Address,
                                validator.SignatureMessage,
                                validator.Signature);

                if (!verifySig)
                {
                    ErrorLogUtility.LogError($"Invalid signature for validator {validator.Address} from peer {advertisingPeerIP}", "NetworkValidator.AddValidatorToPool");
                    return false;
                }

                // VX-07: a direct advertisement changes routing, so it must be fresh and used once.
                // (The old AdvertisementTimestamp check was skipped for 0 — what senders send — and the
                // gossip paths overwrite that field with local time, so it never applied.)
                if (directFromValidator)
                {
                    var now = TimeUtil.GetTime();
                    if (!ConsensusRequestAuth.IsFresh(signedAt, now))
                    {
                        ErrorLogUtility.LogError($"Stale or future direct advertisement for {validator.Address} (signed {signedAt}, now {now})", "NetworkValidator.AddValidatorToPool");
                        return false;
                    }
                    if (!ConsensusRequestAuth.TryConsume(validator.Signature, now))
                    {
                        ErrorLogUtility.LogError($"Replayed direct advertisement for {validator.Address}", "NetworkValidator.AddValidatorToPool");
                        return false;
                    }
                }

                // HAL-11 Fix: Cross-validation logic
                var existingValidator = Globals.NetworkValidators.TryGetValue(validator.Address, out var networkVal);
                var pendingValidator = _pendingValidators.TryGetValue(validator.Address, out var pendingVal);

                if (existingValidator && networkVal != null)
                {
                    // VX-07: MERGE onto the stored entry. It used to be replaced wholesale by the wire object,
                    // so any advertisement (a replayed signature, a gossiped copy) rewrote the entry's IP,
                    // PublicKey, name and first-seen height. Trust, first-seen and confirmations are local
                    // facts and are always kept.
                    if (!string.IsNullOrEmpty(advertisingPeerIP))
                    {
                        networkVal.ConfirmingSources.Add(advertisingPeerIP);
                    }
                    // Reset fail count on re-advertisement — the validator is clearly online
                    // if it's being advertised by peers again
                    networkVal.CheckFailCount = 0;
                    networkVal.LastSeen = TimeUtil.GetTime(); // HAL-26 Fix: Update last seen timestamp

                    // Only the validator's own fresh, single-use advertisement may move or rename it.
                    if (directFromValidator)
                    {
                        networkVal.IPAddress = validator.IPAddress;
                        networkVal.PublicKey = validator.PublicKey;
                        networkVal.UniqueName = validator.UniqueName;
                        networkVal.Signature = validator.Signature;
                        networkVal.SignatureMessage = validator.SignatureMessage;
                    }

                    // RESTART-FIX: If validator was added directly (e.g. P2P connect) with
                    // IsFullyTrusted=false, promote to trusted once we get a peer confirmation
                    // or if the advertising source is a trusted bootstrap peer.
                    if (!networkVal.IsFullyTrusted && !string.IsNullOrEmpty(advertisingPeerIP))
                    {
                        // Another peer is vouching for this validator — check confirmations
                        if (networkVal.ConfirmingSources.Count >= GetRequiredConfirmations()
                            || IsTrustedBootstrapSource(advertisingPeerIP))
                        {
                            networkVal.IsFullyTrusted = true;
                            LogUtility.Log($"Validator {networkVal.Address} promoted to fully trusted (was untrusted) after confirmation from {networkVal.ConfirmingSources.Count} sources", "NetworkValidator.AddValidatorToPool");
                        }
                    }

                    Globals.NetworkValidators[networkVal.Address] = networkVal;
                    return true;
                }
                else if (pendingValidator && pendingVal != null)
                {
                    // Validator exists in pending state, check for cross-validation
                    if (!string.IsNullOrEmpty(advertisingPeerIP))
                    {
                        pendingVal.ConfirmingSources.Add(advertisingPeerIP);
                        
                        // HAL-11 Fix: Require multiple sources for full trust
                        if (pendingVal.ConfirmingSources.Count >= GetRequiredConfirmations())
                        {
                            // Promote to fully trusted validator
                            pendingVal.IsFullyTrusted = true;
                            pendingVal.LastSeen = TimeUtil.GetTime(); // HAL-26 Fix: Set initial last seen
                            Globals.NetworkValidators.TryAdd(pendingVal.Address, pendingVal);
                            _pendingValidators.TryRemove(validator.Address, out _);
                            
                            LogUtility.Log($"Validator {validator.Address} promoted to fully trusted after confirmation from {pendingVal.ConfirmingSources.Count} sources", "NetworkValidator.AddValidatorToPool");
                            return true;
                        }
                        else
                        {
                            // Update pending validator with new information
                            _pendingValidators[validator.Address] = pendingVal;
                            LogUtility.Log($"Validator {validator.Address} confirmed by additional source. Total confirmations: {pendingVal.ConfirmingSources.Count}", "NetworkValidator.AddValidatorToPool");
                            return true;
                        }
                    }
                }
                else
                {
                    // HAL-11 Fix: Rate limiting per advertising peer — "max 10 NEW validators per hour per
                    // peer". Applied here (new entries only) so refreshing known validators from a peer's
                    // list does not exhaust the budget and starve their liveness.
                    if (!string.IsNullOrEmpty(advertisingPeerIP) && !CheckRateLimit(advertisingPeerIP))
                    {
                        ErrorLogUtility.LogError($"Rate limit exceeded for validator advertisements from peer {advertisingPeerIP}", "NetworkValidator.AddValidatorToPool");
                        return false;
                    }

                    // VX-15: first-seen is a local fact, never taken from the wire.
                    validator.FirstSeenAtHeight = Globals.LastBlock?.Height ?? 0;

                    // New validator - add to pending state
                    validator.FirstAdvertised = TimeUtil.GetTime();
                    validator.OriginalAdvertiser = advertisingPeerIP ?? "unknown";
                    validator.IsFullyTrusted = false;
                    
                    if (!string.IsNullOrEmpty(advertisingPeerIP))
                    {
                        validator.ConfirmingSources.Add(advertisingPeerIP);
                    }

                    // POST-SYNC LIVENESS GATE: After the liveness sweep completes,
                    // new validators from gossip must pass a liveness + version check
                    // before being added. This prevents P2P gossip from re-adding
                    // offline/outdated validators that were just swept.
                    if (Globals.ValidatorLivenessSweepComplete && !string.IsNullOrEmpty(validator.IPAddress))
                    {
                        var isLive = await CheckValidatorLiveness(validator.IPAddress);
                        if (!isLive)
                        {
                            LogUtility.Log($"Validator {validator.Address} at {validator.IPAddress} REJECTED by post-sweep liveness gate (unreachable or outdated version)", "NetworkValidator.AddValidatorToPool");
                            return false;
                        }
                    }

                    // HAL-11 Fix: Only add directly to main pool if from trusted bootstrap sources
                    if (IsTrustedBootstrapSource(advertisingPeerIP))
                    {
                        validator.IsFullyTrusted = true;
                        validator.LastSeen = TimeUtil.GetTime(); // HAL-26 Fix: Set initial last seen
                        Globals.NetworkValidators.TryAdd(validator.Address, validator);
                        LogUtility.Log($"Validator {validator.Address} added directly from trusted bootstrap source {advertisingPeerIP}", "NetworkValidator.AddValidatorToPool");
                    }
                    else
                    {
                        // Add to pending validation
                        _pendingValidators.TryAdd(validator.Address, validator);
                        LogUtility.Log($"Validator {validator.Address} added to pending validation from {advertisingPeerIP}", "NetworkValidator.AddValidatorToPool");
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error adding validator to pool: {ex.Message}", "NetworkValidator.AddValidatorToPool");
                return false;
            }
        }

        // HAL-11 Fix: Rate limiting implementation
        private static bool CheckRateLimit(string peerIP)
        {
            var tracker = _advertisementTrackers.GetOrAdd(peerIP, _ => new ValidatorAdvertisementTracker());
            var currentTime = TimeUtil.GetTime();
            
            // Clean old entries (older than 1 hour)
            tracker.Timestamps.RemoveAll(t => currentTime - t > 3600);
            
            // Check if rate limit is exceeded (max 10 new validators per hour per peer)
            if (tracker.Timestamps.Count >= 10)
            {
                return false;
            }
            
            tracker.Timestamps.Add(currentTime);
            return true;
        }

        // HAL-11 Fix: Determine required confirmations based on network size
        private static int GetRequiredConfirmations()
        {
            var connectedValidators = Globals.ValidatorNodes.Count;
            if (connectedValidators < 3) return 1;  // Bootstrap scenario
            if (connectedValidators < 10) return 2;
            return 3; // Normal operation
        }

        // HAL-11 Fix: Check if source is a trusted bootstrap peer
        private static bool IsTrustedBootstrapSource(string peerIP)
        {
            if (string.IsNullOrEmpty(peerIP))
                return false;

            // RESTART-FIX: Use actual bootstrap caster IPs from config
            // These are the hardcoded bootstrap peers that are inherently trusted.
            var cleanIP = peerIP.Replace("::ffff:", "");
            
            // Check against known bootstrap caster IPs
            var bootstrapPeers = Globals.BlockCasters
                .Where(c => !string.IsNullOrEmpty(c.PeerIP))
                .Select(c => c.PeerIP.Replace("::ffff:", ""))
                .ToHashSet();
            
            if (bootstrapPeers.Contains(cleanIP))
                return true;

            // Also check against connected validator nodes that are known casters
            var casterIPs = Globals.ValidatorNodes.Values
                .Where(v => v.IsConnected)
                .Select(v => v.NodeIP.Replace("::ffff:", "").Replace(":" + Globals.Port, ""))
                .ToHashSet();

            // If the advertising peer is one of our connected validators, trust it
            return casterIPs.Contains(cleanIP);
        }

        // HAL-11 Fix: Cleanup stale pending validators
        // HAL-26 Fix: Enhanced to also cleanup main NetworkValidators registry
        public static void CleanupStaleValidators()
        {
            var currentTime = TimeUtil.GetTime();
            var pendingStaleThreshold = currentTime - 3600; // 1 hour for pending validators
            var mainRegistryStaleThreshold = currentTime - 7200; // 2 hours for main registry (was 24h — too lenient, stale validators stayed 18+ hours)
            var failCountThreshold = 50; // Remove validators with very high sustained fail counts

            // Cleanup pending validators (1 hour inactivity)
            var stalePendingValidators = _pendingValidators.Where(kvp => kvp.Value.FirstAdvertised < pendingStaleThreshold).ToList();
            
            foreach (var staleValidator in stalePendingValidators)
            {
                _pendingValidators.TryRemove(staleValidator.Key, out _);
                LogUtility.Log($"Removed stale pending validator {staleValidator.Key}", "NetworkValidator.CleanupStaleValidators");
            }

            // HAL-26 Fix: Cleanup main NetworkValidators registry
            // Never prune our own validator address from the registry
            var selfAddress = Globals.ValidatorAddress;
            var staleMainValidators = Globals.NetworkValidators
                .Where(kvp => 
                    kvp.Key != selfAddress && // Never prune self
                    (
                        // Remove if not seen in 24 hours
                        (kvp.Value.LastSeen > 0 && kvp.Value.LastSeen < mainRegistryStaleThreshold) ||
                        // Remove if has high fail count
                        kvp.Value.CheckFailCount > failCountThreshold
                    ))
                .ToList();

            int removedCount = 0;
            foreach (var staleValidator in staleMainValidators)
            {
                if (Globals.NetworkValidators.TryRemove(staleValidator.Key, out var removed))
                {
                    removedCount++;
                    var reason = removed.CheckFailCount > failCountThreshold 
                        ? $"high fail count ({removed.CheckFailCount})" 
                        : $"inactive for {(currentTime - removed.LastSeen) / 3600} hours";
                    
                    LogUtility.Log($"Removed stale validator {staleValidator.Key} from main registry. Reason: {reason}", "NetworkValidator.CleanupStaleValidators");
                }
            }

            if (removedCount > 0)
            {
                LogUtility.Log($"HAL-26: Pruned {removedCount} stale validators from main registry. Current registry size: {Globals.NetworkValidators.Count}", "NetworkValidator.CleanupStaleValidators");
            }
        }

        // HAL-11 Fix: Get pending validators for monitoring
        public static Dictionary<string, NetworkValidator> GetPendingValidators()
        {
            return new Dictionary<string, NetworkValidator>(_pendingValidators);
        }

        // HAL-26 Fix: Update last seen timestamp for existing validator
        public static void UpdateLastSeen(string validatorAddress)
        {
            if (Globals.NetworkValidators.TryGetValue(validatorAddress, out var validator))
            {
                validator.LastSeen = TimeUtil.GetTime();
                Globals.NetworkValidators[validatorAddress] = validator;
            }
        }

        /// <summary>
        /// CASTER-PROMOTE-FIX: Upsert a validator as fully trusted following a direct
        /// authenticated SignalR connection (P2PValidatorServer / P2PBlockcasterServer).
        ///
        /// The previous direct-connect path used <c>Globals.NetworkValidators.TryAdd</c>,
        /// which is a no-op when the key already exists. If the validator had previously
        /// been placed in <c>NetworkValidators</c> via a gossip path with
        /// <c>IsFullyTrusted=false</c>, or if a different code path had reset the trust
        /// flag, the direct-connect would silently fail to promote it. The validator
        /// would then never appear in <c>EvaluateCasterPool</c>'s candidate list
        /// (because the candidate filter requires <c>IsFullyTrusted=true</c>) and
        /// could never be promoted to a caster.
        ///
        /// This helper guarantees:
        ///   1) The validator is present in <c>NetworkValidators</c>.
        ///   2) <c>IsFullyTrusted = true</c> (a completed signature-authenticated
        ///      SignalR handshake is stronger proof than any gossip vouch).
        ///   3) The validator is no longer quarantined in <c>_pendingValidators</c>.
        ///   4) <c>FirstSeenAtHeight</c> is preserved if already set (to avoid
        ///      resetting the maturity-gate timer on reconnects), otherwise populated.
        /// </summary>
        public static void UpsertTrustedOnDirectConnect(NetworkValidator validator)
        {
            if (validator == null || string.IsNullOrEmpty(validator.Address))
                return;

            var currentTime = TimeUtil.GetTime();
            validator.IsFullyTrusted = true;
            if (validator.LastSeen == 0) validator.LastSeen = currentTime;
            if (validator.FirstAdvertised == 0) validator.FirstAdvertised = currentTime;

            // Preserve FirstSeenAtHeight across reconnects so the maturity gate
            // doesn't reset every time the validator drops and reconnects.
            // FIX 1: But if the stored value is too stale (>1000 blocks behind tip),
            // reset it to current height. This prevents a node that was seen 10,000
            // blocks ago (e.g. from a prior process run with stale Peers DB) from
            // instantly passing the maturity gate on restart.
            var currentTip = Globals.LastBlock?.Height ?? 0;
            if (Globals.NetworkValidators.TryGetValue(validator.Address, out var existing))
            {
                if (existing.FirstSeenAtHeight > 0)
                {
                    var staleDelta = currentTip - existing.FirstSeenAtHeight;
                    if (staleDelta > 1000)
                    {
                        // Too stale — reset to current height so maturity gate restarts
                        LogUtility.Log(
                            $"Validator {validator.Address} FirstSeenAtHeight reset: was {existing.FirstSeenAtHeight} (delta={staleDelta}), now={currentTip}",
                            "NetworkValidator.UpsertTrustedOnDirectConnect");
                        validator.FirstSeenAtHeight = currentTip;
                        validator.FirstSeenAt = currentTime;
                    }
                    else
                    {
                        validator.FirstSeenAtHeight = existing.FirstSeenAtHeight;
                        validator.FirstSeenAt = existing.FirstSeenAt > 0 ? existing.FirstSeenAt : currentTime;
                    }
                }
                else if (validator.FirstSeenAtHeight == 0)
                    validator.FirstSeenAtHeight = currentTip;
            }
            else if (validator.FirstSeenAtHeight == 0)
            {
                validator.FirstSeenAtHeight = currentTip;
            }
            // Wall-clock first-seen starts now unless preserved above.
            if (validator.FirstSeenAt == 0)
                validator.FirstSeenAt = currentTime;

            // Upsert into the trusted registry.
            Globals.NetworkValidators[validator.Address] = validator;

            // Clear any pending-quarantine entry so the duplicate doesn't linger.
            _pendingValidators.TryRemove(validator.Address, out _);

            LogUtility.Log(
                $"Validator {validator.Address} upserted as fully trusted via direct connection (IP={validator.IPAddress})",
                "NetworkValidator.UpsertTrustedOnDirectConnect");
        }


        /// <summary>
        /// Post-sync liveness sweep: after chain sync completes, loop through all NetworkValidators,
        /// call GetWalletVersion on each one. Remove any that don't respond within 1.5s or that
        /// report an outdated major version. Records the synced height as a watermark.
        /// </summary>
        public static async Task RunPostSyncLivenessSweep()
        {
            var syncedHeight = Globals.LastBlock?.Height ?? 0;
            Globals.ValidatorListSyncedHeight = syncedHeight;

            var validators = Globals.NetworkValidators.ToArray();
            var toRemove = new List<string>();
            var checkedCount = 0;

            LogUtility.Log($"POST-SYNC LIVENESS SWEEP: Starting. Checking {validators.Length} validators at synced height {syncedHeight}", "NetworkValidator.RunPostSyncLivenessSweep");

            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(3);

            foreach (var kvp in validators)
            {
                // Skip self
                if (kvp.Key == Globals.ValidatorAddress)
                    continue;

                var ip = kvp.Value.IPAddress?.Replace("::ffff:", "");
                if (string.IsNullOrEmpty(ip))
                {
                    toRemove.Add(kvp.Key);
                    continue;
                }

                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
                    var uri = $"http://{ip}:{Globals.ValAPIPort}/valapi/validator/GetWalletVersion";
                    var resp = await client.GetAsync(uri, cts.Token);

                    if (!resp.IsSuccessStatusCode)
                    {
                        toRemove.Add(kvp.Key);
                        LogUtility.Log($"LIVENESS-SWEEP: {kvp.Key} at {ip} — HTTP {resp.StatusCode}. Removing.", "NetworkValidator.RunPostSyncLivenessSweep");
                        continue;
                    }

                    // Version check — reject outdated major versions
                    var peerVersion = await resp.Content.ReadAsStringAsync();
                    if (!string.IsNullOrEmpty(peerVersion))
                    {
                        var cleanVersion = peerVersion.Trim().Trim('"');
                        var parts = cleanVersion.Split('.');
                        if (parts.Length > 0 && int.TryParse(parts[0], out var major))
                        {
                            if (major < Globals.MajorVer)
                            {
                                toRemove.Add(kvp.Key);
                                LogUtility.Log($"LIVENESS-SWEEP: {kvp.Key} at {ip} — version '{cleanVersion}' outdated (need major >= {Globals.MajorVer}). Removing.", "NetworkValidator.RunPostSyncLivenessSweep");
                                continue;
                            }
                        }
                    }

                    checkedCount++;
                }
                catch (Exception ex)
                {
                    toRemove.Add(kvp.Key);
                    LogUtility.Log($"LIVENESS-SWEEP: {kvp.Key} at {ip} — unreachable: {ex.Message}. Removing.", "NetworkValidator.RunPostSyncLivenessSweep");
                }
            }

            foreach (var addr in toRemove)
                Globals.NetworkValidators.TryRemove(addr, out _);

            Globals.ValidatorLivenessSweepComplete = true;

            LogUtility.Log(
                $"POST-SYNC LIVENESS SWEEP COMPLETE: checked {validators.Length}, passed {checkedCount}, removed {toRemove.Count}, remaining {Globals.NetworkValidators.Count}. Watermark height={syncedHeight}",
                "NetworkValidator.RunPostSyncLivenessSweep");
        }

        /// <summary>
        /// Perform a single-validator liveness + version check. Returns true if the validator
        /// is reachable and running a compatible version. Used by P2P gossip to gate new additions.
        /// </summary>
        public static async Task<bool> CheckValidatorLiveness(string ipAddress)
        {
            var ip = ipAddress?.Replace("::ffff:", "");
            if (string.IsNullOrEmpty(ip))
                return false;

            try
            {
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(3);
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
                var uri = $"http://{ip}:{Globals.ValAPIPort}/valapi/validator/GetWalletVersion";
                var resp = await client.GetAsync(uri, cts.Token);

                if (!resp.IsSuccessStatusCode)
                    return false;

                var peerVersion = await resp.Content.ReadAsStringAsync();
                if (!string.IsNullOrEmpty(peerVersion))
                {
                    var cleanVersion = peerVersion.Trim().Trim('"');
                    var parts = cleanVersion.Split('.');
                    if (parts.Length > 0 && int.TryParse(parts[0], out var major))
                    {
                        if (major < Globals.MajorVer)
                            return false;
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Auto-promote a validator to fully trusted when it produces a committed block.
        /// If the validator solved a block that passed full validation, it is definitively legitimate.
        /// Also promotes from _pendingValidators if found there but not yet in NetworkValidators.
        /// </summary>
        public static void PromoteBlockProducer(string validatorAddress, string ipAddress = null)
        {
            if (string.IsNullOrEmpty(validatorAddress))
                return;

            var currentTime = TimeUtil.GetTime();

            // Case 1: Already in NetworkValidators — just flip IsFullyTrusted
            if (Globals.NetworkValidators.TryGetValue(validatorAddress, out var existing))
            {
                if (!existing.IsFullyTrusted)
                {
                    existing.IsFullyTrusted = true;
                    existing.LastSeen = currentTime;
                    Globals.NetworkValidators[validatorAddress] = existing;
                    LogUtility.Log($"Validator {validatorAddress} promoted to fully trusted via block production", "NetworkValidator.PromoteBlockProducer");
                }
                else
                {
                    // Already trusted, just update LastSeen
                    existing.LastSeen = currentTime;
                    Globals.NetworkValidators[validatorAddress] = existing;
                }
                return;
            }

            // WATERMARK GATE: After the liveness sweep completes, do NOT create new
            // NetworkValidator entries from historical blocks that are below the watermark.
            // This prevents offline validators from being resurrected during block processing.
            if (Globals.ValidatorLivenessSweepComplete)
            {
                var currentBlockHeight = Globals.LastBlock?.Height ?? 0;
                if (currentBlockHeight <= Globals.ValidatorListSyncedHeight)
                {
                    // This block is from before/at the sync watermark — skip creating new entries
                    return;
                }
            }

            // Case 2: In pending validators — promote to NetworkValidators
            if (_pendingValidators.TryRemove(validatorAddress, out var pending))
            {
                pending.IsFullyTrusted = true;
                pending.LastSeen = currentTime;
                pending.FirstSeenAtHeight = Globals.LastBlock?.Height ?? 0;
                Globals.NetworkValidators[validatorAddress] = pending;
                LogUtility.Log($"Validator {validatorAddress} promoted from pending to fully trusted via block production", "NetworkValidator.PromoteBlockProducer");
                return;
            }

            // Case 3: Not known at all — create a minimal entry so EvaluateCasterPool can find it later
            // This happens when a block arrives from a validator we haven't seen advertise yet
            if (!string.IsNullOrEmpty(ipAddress))
            {
                var newVal = new NetworkValidator
                {
                    Address = validatorAddress,
                    IPAddress = ipAddress,
                    IsFullyTrusted = true,
                    LastSeen = currentTime,
                    FirstSeenAtHeight = Globals.LastBlock?.Height ?? 0,
                };
                Globals.NetworkValidators[validatorAddress] = newVal;
                LogUtility.Log($"Validator {validatorAddress} added as fully trusted via block production (new entry, IP={ipAddress})", "NetworkValidator.PromoteBlockProducer");
            }
        }
    }

    // HAL-11 Fix: Track validator advertisements per peer
    public class ValidatorAdvertisementTracker
    {
        public List<long> Timestamps { get; set; } = new List<long>();
    }
}
