using System.Collections.Concurrent;

namespace VerifiedXCore.Services
{
    /// <summary>
    /// In-memory dial cooldowns for outbound peer / validator hub connections.
    ///
    /// Why: the 10s height-check loops evict any node that is more than 3 blocks behind (or whose
    /// height probe failed), and the 10s / 15s connect loops immediately re-dialed the very same
    /// node because nothing remembered the eviction. Failed dials likewise never backed off because
    /// on-chain discovery reset FailCount to 0 every loop. Both produced a full SignalR handshake
    /// every 10–30 seconds per stale or unreachable peer, forever.
    ///
    /// This class holds "do not dial before" timestamps keyed by bare IP. The connect routines fold
    /// <see cref="BlockedIPs"/> into their skip sets; nothing here is persisted.
    /// </summary>
    public static class PeerConnectionBackoff
    {
        /// <summary>Cooldown after a node was evicted for being more than 3 blocks behind the network.</summary>
        public const int LagEvictionCooldownMs = 10 * 60 * 1000;

        /// <summary>Shorter cooldown when eviction was caused by a failed / timed-out height probe rather than confirmed lag.</summary>
        public const int ProbeFailureCooldownMs = 2 * 60 * 1000;

        /// <summary>Base delay for the first failed dial; doubles per consecutive failure.</summary>
        public const int ConnectFailureBaseMs = 30 * 1000;

        /// <summary>Upper bound for any connect-failure backoff.</summary>
        public const int ConnectFailureMaxMs = 10 * 60 * 1000;

        /// <summary>Cooldown for peers that connect but fail the consensus-version handshake (old binary).</summary>
        public const int IncompatibleCooldownMs = 10 * 60 * 1000;

        private static readonly ConcurrentDictionary<string, long> _blockedUntilMs = new ConcurrentDictionary<string, long>();

        private static long NowMs() => Environment.TickCount64;

        private static string Normalize(string ip)
        {
            if (string.IsNullOrWhiteSpace(ip))
                return string.Empty;
            var clean = ip.Replace("::ffff:", "").Trim();
            var colon = clean.IndexOf(':');
            return colon > 0 ? clean.Substring(0, colon) : clean;
        }

        /// <summary>Block an IP from being dialed for <paramref name="durationMs"/> from now. Never shortens an existing block.</summary>
        public static void Block(string ip, long durationMs)
        {
            var key = Normalize(ip);
            if (key.Length == 0)
                return;

            var until = NowMs() + Math.Max(0, durationMs);
            _blockedUntilMs.AddOrUpdate(key, until, (_, existing) => Math.Max(existing, until));
        }

        /// <summary>Called by the height-check loops when a node is removed for lagging or failing its height probe.</summary>
        public static void MarkLagEvicted(string ip, bool probeFailed)
        {
            Block(ip, probeFailed ? ProbeFailureCooldownMs : LagEvictionCooldownMs);
        }

        /// <summary>Exponential backoff for a failed outbound dial: 30s, 60s, 120s ... capped at 10 minutes.</summary>
        public static void MarkConnectFailure(string ip, int failCount)
        {
            Block(ip, ConnectFailureDelayMs(failCount));
        }

        /// <summary>Peer connected but is not consensus-compatible; do not keep re-handshaking with it.</summary>
        public static void MarkIncompatible(string ip)
        {
            Block(ip, IncompatibleCooldownMs);
        }

        /// <summary>Clear any cooldown after a successful connection.</summary>
        public static void Clear(string ip)
        {
            var key = Normalize(ip);
            if (key.Length > 0)
                _blockedUntilMs.TryRemove(key, out _);
        }

        public static bool IsBlocked(string ip)
        {
            var key = Normalize(ip);
            if (key.Length == 0 || !_blockedUntilMs.TryGetValue(key, out var until))
                return false;

            if (until > NowMs())
                return true;

            _blockedUntilMs.TryRemove(key, out _);
            return false;
        }

        /// <summary>Every IP currently under cooldown. Expired entries are pruned as a side effect.</summary>
        public static HashSet<string> BlockedIPs()
        {
            var now = NowMs();
            var result = new HashSet<string>();
            foreach (var kvp in _blockedUntilMs)
            {
                if (kvp.Value > now)
                    result.Add(kvp.Key);
                else
                    _blockedUntilMs.TryRemove(kvp.Key, out _);
            }
            return result;
        }

        /// <summary>Milliseconds remaining on an IP's cooldown, or 0 when it may be dialed.</summary>
        public static long RemainingMs(string ip)
        {
            var key = Normalize(ip);
            if (key.Length == 0 || !_blockedUntilMs.TryGetValue(key, out var until))
                return 0;
            return Math.Max(0, until - NowMs());
        }

        public static long ConnectFailureDelayMs(int failCount)
        {
            var exponent = Math.Clamp(failCount - 1, 0, 6);
            var delay = (long)ConnectFailureBaseMs << exponent;
            return Math.Min(delay, ConnectFailureMaxMs);
        }

        /// <summary>Test hook.</summary>
        public static void ResetForTests() => _blockedUntilMs.Clear();
    }
}
