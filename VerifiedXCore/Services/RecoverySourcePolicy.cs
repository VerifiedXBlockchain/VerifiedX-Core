using System.Collections.Concurrent;
using VerifiedXCore.Models;
using VerifiedXCore.P2P;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Services
{
    /// <summary>
    /// STALL-RESOLVE: constrains which P2P peers <see cref="BlockDownloadService.GetAllBlocks"/> may
    /// pull blocks from while a fork recovery is in flight.
    ///
    /// Without this, the downloader hands the first missing height to the LOWEST-height peer group,
    /// so a fellow stranded node sitting exactly at the divergence height re-serves the dead branch
    /// and the recovery fails ("REORG INCOMPLETE ... minority peers answered first").
    ///
    /// Allowed = peers that answered with the majority hash at the divergence probe.
    /// Quarantined = peers that answered with OUR (losing) hash. Quarantined peers are never used
    /// as a source while the policy is active; when an allow-list is set, only allowed peers are.
    /// The policy is cleared by the recovery that set it (success or failure) — normal sync is untouched.
    /// </summary>
    public static class RecoverySourcePolicy
    {
        private static readonly ConcurrentDictionary<string, byte> _allowed = new();
        private static readonly ConcurrentDictionary<string, byte> _quarantined = new();
        private static volatile bool _active = false;

        public static bool IsActive => _active;
        public static int AllowedCount => _allowed.Count;
        public static int QuarantinedCount => _quarantined.Count;

        public static void Set(IEnumerable<string> allowed, IEnumerable<string> quarantined)
        {
            _allowed.Clear();
            _quarantined.Clear();
            foreach (var ip in allowed.Select(Normalize).Where(ip => ip.Length > 0))
                _allowed[ip] = 1;
            foreach (var ip in quarantined.Select(Normalize).Where(ip => ip.Length > 0))
                if (!_allowed.ContainsKey(ip))
                    _quarantined[ip] = 1;
            _active = _allowed.Count > 0 || _quarantined.Count > 0;
        }

        public static void Clear()
        {
            _allowed.Clear();
            _quarantined.Clear();
            _active = false;
        }

        /// <summary>Pure: is this peer IP an acceptable block source under the current policy?</summary>
        public static bool IsEligible(string? ip)
        {
            if (!_active) return true;
            var n = Normalize(ip);
            if (n.Length == 0) return false;
            if (_quarantined.ContainsKey(n)) return false;
            if (_allowed.Count > 0) return _allowed.ContainsKey(n);
            return true;
        }

        public static bool IsEligible(NodeInfo? node) => node != null && IsEligible(node.NodeIP);

        public static string Normalize(string? ip) => (ip ?? "").Replace("::ffff:", "").Trim();

        /// <summary>
        /// Makes sure we hold a P2P connection to at least one allowed source. Stranded nodes often
        /// only have P2P links to their fellow stranded peers; the majority peers we learned about
        /// over the validator API may not be in <see cref="Globals.Nodes"/> at all. Also drops P2P
        /// links to quarantined peers so they cannot answer block requests during the pass.
        /// </summary>
        public static async Task EnsureSourcesConnectedAsync(int waitSeconds = 8)
        {
            try
            {
                foreach (var ip in _quarantined.Keys)
                {
                    if (Globals.Nodes.TryRemove(ip, out var bad) && bad?.Connection != null)
                    {
                        try { await bad.Connection.DisposeAsync(); } catch { }
                    }
                }

                var connectedAllowed = Globals.Nodes.Values.Count(n => n.IsConnected && _allowed.ContainsKey(Normalize(n.NodeIP)));
                if (_allowed.Count == 0 || connectedAllowed > 0)
                    return;

                // P2PClient.Connect refuses new links at MaxPeers. A stranded node's slots are
                // typically full of peers that are no use for recovery — evict the lowest-height
                // non-allowed peers to make room for the majority sources.
                var needed = Math.Min(_allowed.Count, 3);
                var room = Globals.MaxPeers - Globals.Nodes.Count;
                if (room < needed)
                {
                    var evict = Globals.Nodes.Values
                        .Where(n => !_allowed.ContainsKey(Normalize(n.NodeIP)))
                        .OrderBy(n => n.NodeHeight)
                        .Take(needed - Math.Max(0, room))
                        .ToList();
                    foreach (var n in evict)
                    {
                        if (Globals.Nodes.TryRemove(n.NodeIP, out var gone) && gone?.Connection != null)
                        {
                            try { await gone.Connection.DisposeAsync(); } catch { }
                        }
                    }
                    if (evict.Count > 0)
                        LogUtility.Log($"[RecoverySourcePolicy] Evicted {evict.Count} low-height peer(s) to make room for majority sources.", "RecoverySourcePolicy");
                }

                var peerDb = Peers.GetAll();
                foreach (var ip in _allowed.Keys)
                {
                    if (Globals.Nodes.ContainsKey(ip))
                        continue;
                    var peer = peerDb?.FindOne(x => x.PeerIP == ip) ?? new Peers { PeerIP = ip, IsOutgoing = true };
                    if (peer.IsBanned || peer.IsPermaBanned)
                    {
                        peer.IsBanned = false;
                        peer.IsPermaBanned = false;
                    }
                    _ = P2PClient.ManualConnectToPeers(peer);
                }

                var deadline = DateTime.UtcNow.AddSeconds(waitSeconds);
                while (DateTime.UtcNow < deadline)
                {
                    if (Globals.Nodes.Values.Any(n => n.IsConnected && _allowed.ContainsKey(Normalize(n.NodeIP))))
                        return;
                    await Task.Delay(500);
                }
                LogUtility.Log($"[RecoverySourcePolicy] No P2P connection to any of {_allowed.Count} majority source(s) after {waitSeconds}s — download will proceed with whatever eligible peers exist.", "RecoverySourcePolicy");
            }
            catch (Exception ex)
            {
                LogUtility.Log($"[RecoverySourcePolicy] EnsureSourcesConnected error: {ex.Message}", "RecoverySourcePolicy");
            }
        }
    }
}
