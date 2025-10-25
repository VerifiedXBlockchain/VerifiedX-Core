using ReserveBlockCore.Models;
using ReserveBlockCore.Services;
using System.Collections.Concurrent;
using System.Net;

namespace ReserveBlockCore.Utilities
{
    /// <summary>
    /// HAL-14 Security Enhancement: Connection security utilities for preventing spoofing attacks
    /// and monitoring authentication attempts
    /// </summary>
    public static class ConnectionSecurityHelper
    {
        // Track connection attempts per IP for rate limiting
        private static readonly ConcurrentDictionary<string, ConnectionAttemptHistory> _connectionAttempts 
            = new ConcurrentDictionary<string, ConnectionAttemptHistory>();

        // Track authentication failures for security monitoring
        private static readonly ConcurrentDictionary<string, AuthenticationFailureHistory> _authFailures 
            = new ConcurrentDictionary<string, AuthenticationFailureHistory>();

        private static readonly object _cleanupLock = new object();
        private static DateTime _lastCleanup = DateTime.UtcNow;

        /// <summary>
        /// Check if IP should be rate limited based on connection attempts
        /// </summary>
        public static bool ShouldRateLimit(string ipAddress)
        {
            if (string.IsNullOrWhiteSpace(ipAddress))
                return true;

            CleanupExpiredHistory();

            var key = ipAddress;
            var now = DateTime.UtcNow;

            _connectionAttempts.AddOrUpdate(key, 
                new ConnectionAttemptHistory { FirstAttempt = now, AttemptCount = 1, LastAttempt = now },
                (k, existing) =>
                {
                    existing.AttemptCount++;
                    existing.LastAttempt = now;
                    return existing;
                });

            var history = _connectionAttempts[key];

            // Rate limit: Max 10 attempts per minute
            if (history.AttemptCount > 10 && (now - history.FirstAttempt).TotalMinutes < 1)
            {
                LogSecurityEvent($"Rate limiting IP {ipAddress} - {history.AttemptCount} attempts in {(now - history.FirstAttempt).TotalSeconds:F1} seconds", 
                    "ConnectionRateLimit");
                return true;
            }

            return false;
        }

        /// <summary>
        /// Track authentication failure for security monitoring
        /// </summary>
        public static void RecordAuthenticationFailure(string ipAddress, string address, string reason)
        {
            if (string.IsNullOrWhiteSpace(ipAddress))
                return;

            var key = ipAddress;
            var now = DateTime.UtcNow;

            _authFailures.AddOrUpdate(key,
                new AuthenticationFailureHistory 
                { 
                    FirstFailure = now, 
                    FailureCount = 1, 
                    LastFailure = now,
                    LastAddress = address,
                    LastReason = reason
                },
                (k, existing) =>
                {
                    existing.FailureCount++;
                    existing.LastFailure = now;
                    existing.LastAddress = address;
                    existing.LastReason = reason;
                    return existing;
                });

            var history = _authFailures[key];

            // Security alert: Multiple authentication failures
            if (history.FailureCount >= 5)
            {
                LogSecurityEvent($"Security Alert: IP {ipAddress} has {history.FailureCount} authentication failures. " +
                    $"Last attempt: address={address}, reason={reason}", "AuthenticationFailures");

                // Consider escalating to ban if too many failures
                if (history.FailureCount >= 10)
                {
                    BanService.BanPeer(ipAddress, $"Excessive authentication failures: {history.FailureCount}", 
                        "ConnectionSecurityHelper");
                }
            }
        }

        /// <summary>
        /// Validate that an address authentication attempt is legitimate
        /// </summary>
        public static bool ValidateAuthenticationAttempt(string ipAddress, string address)
        {
            if (string.IsNullOrWhiteSpace(ipAddress) || string.IsNullOrWhiteSpace(address))
                return false;

            // Check for suspicious patterns
            if (IsSuspiciousAddressPattern(address))
            {
                LogSecurityEvent($"Suspicious address pattern detected from IP {ipAddress}: {address}", 
                    "SuspiciousAddressPattern");
                return false;
            }

            // Check if IP is trying multiple different addresses rapidly
            if (IsAddressSwitchingAttack(ipAddress, address))
            {
                LogSecurityEvent($"Possible address switching attack from IP {ipAddress}: attempting {address}", 
                    "AddressSwitchingAttack");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Enhanced ABL check with security logging
        /// </summary>
        public static bool IsAddressBlocklisted(string address, string ipAddress, string context)
        {
            if (string.IsNullOrWhiteSpace(address))
                return false;

            var ablList = Globals.ABL.ToList();
            var isBlocked = ablList.Exists(x => x == address);

            if (isBlocked)
            {
                LogSecurityEvent($"ABL check blocked authenticated address {address} from IP {ipAddress} in context: {context}", 
                    "ABLViolation");
            }

            return isBlocked;
        }

        /// <summary>
        /// Clear connection history for an IP (called when connection succeeds)
        /// </summary>
        public static void ClearConnectionHistory(string ipAddress)
        {
            if (string.IsNullOrWhiteSpace(ipAddress))
                return;

            _connectionAttempts.TryRemove(ipAddress, out _);
            _authFailures.TryRemove(ipAddress, out _);
        }

        /// <summary>
        /// Get security statistics for monitoring
        /// </summary>
        public static SecurityStatistics GetSecurityStatistics()
        {
            CleanupExpiredHistory();

            return new SecurityStatistics
            {
                ActiveConnectionAttempts = _connectionAttempts.Count,
                ActiveAuthenticationFailures = _authFailures.Count,
                TotalRateLimitedIPs = _connectionAttempts.Values.Count(h => h.AttemptCount > 10),
                TotalHighFailureIPs = _authFailures.Values.Count(h => h.FailureCount >= 5),
                LastCleanup = _lastCleanup
            };
        }

        private static bool IsSuspiciousAddressPattern(string address)
        {
            // Check for obviously invalid or suspicious address patterns
            if (string.IsNullOrWhiteSpace(address))
                return true;

            // Check for common test/placeholder addresses
            var suspiciousPatterns = new[]
            {
                "test", "example", "placeholder", "dummy", "fake",
                "aaaaa", "11111", "00000", "xxxxx"
            };

            return suspiciousPatterns.Any(pattern => 
                address.ToLowerInvariant().Contains(pattern));
        }

        private static bool IsAddressSwitchingAttack(string ipAddress, string address)
        {
            // Track addresses attempted by each IP
            var key = $"addresses_{ipAddress}";
            var now = DateTime.UtcNow;

            if (!_connectionAttempts.TryGetValue(key, out var history))
            {
                // First attempt from this IP
                return false;
            }

            // If IP is trying many different addresses quickly, it's suspicious
            if (history.AttemptCount > 3 && (now - history.FirstAttempt).TotalMinutes < 5)
            {
                return true;
            }

            return false;
        }

        private static void CleanupExpiredHistory()
        {
            lock (_cleanupLock)
            {
                if ((DateTime.UtcNow - _lastCleanup).TotalMinutes < 5)
                    return;

                _lastCleanup = DateTime.UtcNow;
            }

            var cutoff = DateTime.UtcNow.AddHours(-1);

            // Clean up old connection attempts
            var expiredConnections = _connectionAttempts
                .Where(kvp => kvp.Value.LastAttempt < cutoff)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in expiredConnections)
            {
                _connectionAttempts.TryRemove(key, out _);
            }

            // Clean up old authentication failures  
            var expiredFailures = _authFailures
                .Where(kvp => kvp.Value.LastFailure < cutoff)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in expiredFailures)
            {
                _authFailures.TryRemove(key, out _);
            }
        }

        private static void LogSecurityEvent(string message, string category)
        {
            try
            {               
                // Also log to error log for high priority events
                if (category == "ABLViolation" || category == "AuthenticationFailures")
                {
                    ErrorLogUtility.LogError($"HAL-14 Security: {message}", $"ConnectionSecurityHelper.{category}");
                }
            }
            catch
            {
                // Don't let logging failures affect security functionality
            }
        }
    }

    public class ConnectionAttemptHistory
    {
        public DateTime FirstAttempt { get; set; }
        public DateTime LastAttempt { get; set; }
        public int AttemptCount { get; set; }
    }

    public class AuthenticationFailureHistory
    {
        public DateTime FirstFailure { get; set; }
        public DateTime LastFailure { get; set; }
        public int FailureCount { get; set; }
        public string LastAddress { get; set; } = string.Empty;
        public string LastReason { get; set; } = string.Empty;
    }

    public class SecurityStatistics
    {
        public int ActiveConnectionAttempts { get; set; }
        public int ActiveAuthenticationFailures { get; set; }
        public int TotalRateLimitedIPs { get; set; }
        public int TotalHighFailureIPs { get; set; }
        public DateTime LastCleanup { get; set; }
    }
}
