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
                    // Validator already exists and is trusted
                    if (!string.IsNullOrEmpty(advertisingPeerIP))
                    {
                        networkVal.ConfirmingSources.Add(advertisingPeerIP);
                    }
                    validator.CheckFailCount = networkVal.CheckFailCount;
                    validator.ConfirmingSources = networkVal.ConfirmingSources;
                    validator.IsFullyTrusted = networkVal.IsFullyTrusted;
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
            // Could be configured via settings, for now use empty list
            // This would typically include known seed nodes or configured trusted peers
            var trustedSources = new HashSet<string>();
            return trustedSources.Contains(peerIP);
        }

        // HAL-11 Fix: Cleanup stale pending validators
        public static void CleanupStaleValidators()
        {
            var currentTime = TimeUtil.GetTime();
            var staleThreshold = currentTime - 3600; // 1 hour

            var staleValidators = _pendingValidators.Where(kvp => kvp.Value.FirstAdvertised < staleThreshold).ToList();
            
            foreach (var staleValidator in staleValidators)
            {
                _pendingValidators.TryRemove(staleValidator.Key, out _);
                LogUtility.Log($"Removed stale pending validator {staleValidator.Key}", "NetworkValidator.CleanupStaleValidators");
            }
        }

        // HAL-11 Fix: Get pending validators for monitoring
        public static Dictionary<string, NetworkValidator> GetPendingValidators()
        {
            return new Dictionary<string, NetworkValidator>(_pendingValidators);
        }
    }

    // HAL-11 Fix: Track validator advertisements per peer
    public class ValidatorAdvertisementTracker
    {
        public List<long> Timestamps { get; set; } = new List<long>();
    }
}
