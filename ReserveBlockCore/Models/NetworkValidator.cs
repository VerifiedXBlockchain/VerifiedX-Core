using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Newtonsoft.Json;
using ReserveBlockCore.Services;
using ReserveBlockCore.Utilities;
using System.Collections.Concurrent;

namespace ReserveBlockCore.Models
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

        public static async Task<bool> AddValidatorToPool(NetworkValidator validator, string advertisingPeerIP = null)
        {
            try
            {
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

                // HAL-11 Fix: Validate timestamp freshness
                if (validator.AdvertisementTimestamp > 0)
                {
                    var currentTime = TimeUtil.GetTime();
                    var timeDiff = Math.Abs(currentTime - validator.AdvertisementTimestamp);
                    
                    if (timeDiff > 300) // 5 minute window
                    {
                        ErrorLogUtility.LogError($"Validator advertisement timestamp too old for {validator.Address}. Diff: {timeDiff}s", "NetworkValidator.AddValidatorToPool");
                        return false;
                    }
                    
                    // DIAGNOSTIC: Log timestamp diff on success so we can verify freshness
                    LogUtility.Log($"Validator {validator.Address} timestamp OK (diff={timeDiff}s) from peer {advertisingPeerIP}", "NetworkValidator.AddValidatorToPool");
                }

                // HAL-11 Fix: Rate limiting per advertising peer
                if (!string.IsNullOrEmpty(advertisingPeerIP))
                {
                    if (!CheckRateLimit(advertisingPeerIP))
                    {
                        ErrorLogUtility.LogError($"Rate limit exceeded for validator advertisements from peer {advertisingPeerIP}", "NetworkValidator.AddValidatorToPool");
                        return false;
                    }
                }

                // HAL-11 Fix: Cross-validation logic
                var existingValidator = Globals.NetworkValidators.TryGetValue(validator.Address, out var networkVal);
                var pendingValidator = _pendingValidators.TryGetValue(validator.Address, out var pendingVal);

                if (existingValidator && networkVal != null)
                {
                    // Validator already exists — update confirming sources
                    if (!string.IsNullOrEmpty(advertisingPeerIP))
                    {
                        networkVal.ConfirmingSources.Add(advertisingPeerIP);
                    }
                    // Reset fail count on re-advertisement — the validator is clearly online
                    // if it's being advertised by peers again
                    validator.CheckFailCount = 0;
                    validator.ConfirmingSources = networkVal.ConfirmingSources;
                    validator.LastSeen = TimeUtil.GetTime(); // HAL-26 Fix: Update last seen timestamp

                    // RESTART-FIX: If validator was added directly (e.g. P2P connect) with
                    // IsFullyTrusted=false, promote to trusted once we get a peer confirmation
                    // or if the advertising source is a trusted bootstrap peer.
                    if (networkVal.IsFullyTrusted)
                    {
                        validator.IsFullyTrusted = true;
                    }
                    else if (!string.IsNullOrEmpty(advertisingPeerIP))
                    {
                        // Another peer is vouching for this validator — check confirmations
                        if (networkVal.ConfirmingSources.Count >= GetRequiredConfirmations()
                            || IsTrustedBootstrapSource(advertisingPeerIP))
                        {
                            validator.IsFullyTrusted = true;
                            LogUtility.Log($"Validator {validator.Address} promoted to fully trusted (was untrusted) after confirmation from {networkVal.ConfirmingSources.Count} sources", "NetworkValidator.AddValidatorToPool");
                        }
                        else
                        {
                            validator.IsFullyTrusted = false;
                        }
                    }
                    else
                    {
                        validator.IsFullyTrusted = networkVal.IsFullyTrusted;
                    }

                    Globals.NetworkValidators[networkVal.Address] = validator;
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
                    // New validator - add to pending state
                    validator.FirstAdvertised = TimeUtil.GetTime();
                    validator.OriginalAdvertiser = advertisingPeerIP ?? "unknown";
                    validator.IsFullyTrusted = false;
                    
                    if (!string.IsNullOrEmpty(advertisingPeerIP))
                    {
                        validator.ConfirmingSources.Add(advertisingPeerIP);
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
            var mainRegistryStaleThreshold = currentTime - 86400; // 24 hours for main registry
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
            if (Globals.NetworkValidators.TryGetValue(validator.Address, out var existing))
            {
                if (existing.FirstSeenAtHeight > 0)
                    validator.FirstSeenAtHeight = existing.FirstSeenAtHeight;
                else if (validator.FirstSeenAtHeight == 0)
                    validator.FirstSeenAtHeight = Globals.LastBlock?.Height ?? 0;
            }
            else if (validator.FirstSeenAtHeight == 0)
            {
                validator.FirstSeenAtHeight = Globals.LastBlock?.Height ?? 0;
            }

            // Upsert into the trusted registry.
            Globals.NetworkValidators[validator.Address] = validator;

            // Clear any pending-quarantine entry so the duplicate doesn't linger.
            _pendingValidators.TryRemove(validator.Address, out _);

            LogUtility.Log(
                $"Validator {validator.Address} upserted as fully trusted via direct connection (IP={validator.IPAddress})",
                "NetworkValidator.UpsertTrustedOnDirectConnect");
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
