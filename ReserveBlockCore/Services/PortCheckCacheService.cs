using System.Collections.Concurrent;
using System.Net.Sockets;
using ReserveBlockCore.Utilities;

namespace ReserveBlockCore.Services
{
    /// <summary>
    /// HAL-022 Fix: Provides cached asynchronous port checking to prevent DoS attacks
    /// while still detecting one-way "parasitic" validators
    /// </summary>
    public static class PortCheckCacheService
    {
        private class PortCheckResult
        {
            public bool IsOpen { get; set; }
            public DateTime CheckedAt { get; set; }
        }

        private static readonly ConcurrentDictionary<string, PortCheckResult> _portCheckCache = new();
        private static readonly TimeSpan _cacheTTL = TimeSpan.FromMinutes(10);
        private static readonly int _checkTimeoutMs = 300; // Tight timeout to limit resource usage

        /// <summary>
        /// Asynchronously checks if a port is open on the specified host with caching
        /// </summary>
        public static async Task<bool> IsPortOpenAsync(string host, int port)
        {
            var cacheKey = $"{host}:{port}";

            // Check cache first
            if (_portCheckCache.TryGetValue(cacheKey, out var cachedResult))
            {
                if (DateTime.UtcNow - cachedResult.CheckedAt < _cacheTTL)
                {
                    return cachedResult.IsOpen;
                }
                else
                {
                    // Remove stale entry
                    _portCheckCache.TryRemove(cacheKey, out _);
                }
            }

            // Perform async port check with tight timeout
            var isOpen = await PerformPortCheckAsync(host, port);

            // Cache the result
            _portCheckCache[cacheKey] = new PortCheckResult
            {
                IsOpen = isOpen,
                CheckedAt = DateTime.UtcNow
            };

            return isOpen;
        }

        /// <summary>
        /// Performs the actual async port check with configurable timeout
        /// </summary>
        private static async Task<bool> PerformPortCheckAsync(string host, int port)
        {
            using (var tcpClient = new TcpClient())
            {
                try
                {
                    var connectTask = tcpClient.ConnectAsync(host, port);
                    var timeoutTask = Task.Delay(_checkTimeoutMs);

                    var completedTask = await Task.WhenAny(connectTask, timeoutTask);

                    if (completedTask == connectTask)
                    {
                        // Connection succeeded within timeout
                        await connectTask; // Await to catch any exceptions
                        return true;
                    }
                    else
                    {
                        // Timeout occurred
                        return false;
                    }
                }
                catch (SocketException)
                {
                    return false;
                }
                catch (Exception ex)
                {
                    ErrorLogUtility.LogError($"Port check error for {host}:{port}: {ex.Message}", "PortCheckCacheService.PerformPortCheckAsync");
                    return false;
                }
            }
        }

        /// <summary>
        /// Performs async port check and disconnects peer if port is not open
        /// </summary>
        public static async Task CheckAndDisconnectIfClosed(string peerIP, int port, Func<Task> disconnectAction, string serverType)
        {
            // Run check asynchronously without blocking
            _ = Task.Run(async () =>
            {
                try
                {
                    var isOpen = await IsPortOpenAsync(peerIP, port);
                    var isValAPIOpen = await IsPortOpenAsync(peerIP, Globals.ValAPIPort);
                    var isFrostAPIOpen = await IsPortOpenAsync(peerIP, Globals.FrostValidatorPort);

                    if (!isOpen || !isValAPIOpen || !isFrostAPIOpen)
                    {
                        if (Globals.OptionalLogging)
                        {
                            LogUtility.Log($"HAL-022: One-way validator detected - Val Port: {isOpen} | Val API Port: {isValAPIOpen} | Frost Port: {isFrostAPIOpen} not open on {peerIP}. Disconnecting.", serverType);
                        }

                        await disconnectAction();
                    }

                    await CleanupExpiredEntries();
                }
                catch (Exception ex)
                {
                    ErrorLogUtility.LogError($"HAL-022: Error during async port check for {peerIP}: {ex.Message}", $"{serverType}.CheckAndDisconnectIfClosed");
                }
            });
        }

        /// <summary>
        /// Clears expired cache entries to prevent memory buildup
        /// </summary>
        public static async Task CleanupExpiredEntries()
        {
            var now = DateTime.UtcNow;
            var expiredKeys = _portCheckCache
                .Where(kvp => now - kvp.Value.CheckedAt >= _cacheTTL)
                .Select(kvp => kvp.Key)
                .ToList();

            if (expiredKeys.Any())
            {
                foreach (var key in expiredKeys)
                {
                    _portCheckCache.TryRemove(key, out _);
                }
            }
        }
    }
}
