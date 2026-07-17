using System.Collections.Concurrent;
using ReserveBlockCore.Utilities;

namespace ReserveBlockCore.Bitcoin.Services
{
    /// <summary>
    /// In-memory blacklist of vBTC contract UIDs whose FROST key packages are known to be broken
    /// (e.g., DKG'd before the participant ordering fix). Contracts in this list are skipped
    /// during BTC exit contract selection. Entries auto-expire after a configurable TTL.
    /// </summary>
    public static class FrostContractBlacklist
    {
        private static readonly ConcurrentDictionary<string, DateTime> _blacklist = new();

        /// <summary>Default blacklist TTL: 24 hours</summary>
        private static readonly TimeSpan DefaultTTL = TimeSpan.FromHours(24);

        /// <summary>
        /// Add a contract UID to the FROST blacklist with the default TTL.
        /// </summary>
        public static void Blacklist(string scUID, string reason = "")
        {
            if (string.IsNullOrEmpty(scUID)) return;
            var expiry = DateTime.UtcNow.Add(DefaultTTL);
            _blacklist[scUID] = expiry;
            LogUtility.Log($"[FrostContractBlacklist] Blacklisted contract {scUID} until {expiry:u}. Reason: {reason}",
                "FrostContractBlacklist.Blacklist");
        }

        /// <summary>
        /// Add a contract UID to the FROST blacklist with a custom TTL.
        /// </summary>
        public static void Blacklist(string scUID, TimeSpan ttl, string reason = "")
        {
            if (string.IsNullOrEmpty(scUID)) return;
            var expiry = DateTime.UtcNow.Add(ttl);
            _blacklist[scUID] = expiry;
            LogUtility.Log($"[FrostContractBlacklist] Blacklisted contract {scUID} until {expiry:u} (TTL: {ttl}). Reason: {reason}",
                "FrostContractBlacklist.Blacklist");
        }

        /// <summary>
        /// Check if a contract UID is currently blacklisted (and not expired).
        /// </summary>
        public static bool IsBlacklisted(string scUID)
        {
            if (string.IsNullOrEmpty(scUID)) return false;
            if (!_blacklist.TryGetValue(scUID, out var expiry)) return false;

            if (DateTime.UtcNow >= expiry)
            {
                // Expired — remove and return false
                _blacklist.TryRemove(scUID, out _);
                return false;
            }
            return true;
        }

        /// <summary>
        /// Remove a contract from the blacklist (e.g., after successful re-DKG).
        /// </summary>
        public static bool Remove(string scUID)
        {
            if (string.IsNullOrEmpty(scUID)) return false;
            var removed = _blacklist.TryRemove(scUID, out _);
            if (removed)
                LogUtility.Log($"[FrostContractBlacklist] Removed contract {scUID} from blacklist", "FrostContractBlacklist.Remove");
            return removed;
        }

        /// <summary>
        /// Get all currently blacklisted contracts (non-expired).
        /// </summary>
        public static Dictionary<string, DateTime> GetAll()
        {
            CleanupExpired();
            return _blacklist.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }

        /// <summary>
        /// Remove expired entries.
        /// </summary>
        public static void CleanupExpired()
        {
            var now = DateTime.UtcNow;
            var expired = _blacklist.Where(kvp => now >= kvp.Value).Select(kvp => kvp.Key).ToList();
            foreach (var key in expired)
                _blacklist.TryRemove(key, out _);
        }
    }
}