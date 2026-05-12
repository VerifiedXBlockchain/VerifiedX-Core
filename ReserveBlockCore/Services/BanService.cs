using LiteDB;
using ReserveBlockCore.Models;
using ReserveBlockCore.Utilities;
using System.Net;

namespace ReserveBlockCore.Services
{
    public class BanService
    {
        static SemaphoreSlim BanServiceLock = new SemaphoreSlim(1, 1);
        /// <summary>
        /// Checks whether the given IP belongs to an active block caster.
        /// Caster IPs must never be banned — doing so breaks consensus quorum
        /// and can cause cascading network splits.
        /// </summary>
        public static bool IsCasterIP(string ipAddress)
        {
            if (string.IsNullOrEmpty(ipAddress))
                return false;
            var normalized = ipAddress.Replace("::ffff:", "");
            return Globals.BlockCasters.Any(c =>
                !string.IsNullOrEmpty(c.PeerIP) &&
                c.PeerIP.Replace("::ffff:", "").Replace(":" + Globals.Port, "") == normalized);
        }

        /// <summary>
        /// Unbans all IPs that belong to active block casters.
        /// Called periodically from MonitorCasters and RunUnban to prevent
        /// consensus deadlocks caused by caster-to-caster bans.
        /// </summary>
        public static void UnbanCasterIPs()
        {
            try
            {
                var casterIPs = Globals.BlockCasters.ToList()
                    .Where(c => !string.IsNullOrEmpty(c.PeerIP))
                    .Select(c => c.PeerIP!.Replace("::ffff:", "").Replace(":" + Globals.Port, ""))
                    .Where(ip => !string.IsNullOrEmpty(ip))
                    .ToList();

                foreach (var ip in casterIPs)
                {
                    if (Globals.BannedIPs.ContainsKey(ip))
                    {
                        UnbanPeer(ip);
                        BanLogUtility.Log($"Auto-unbanned caster IP: {ip} (caster IPs must not be banned)", "BanService.UnbanCasterIPs");
                    }
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"UnbanCasterIPs error: {ex.Message}", "BanService.UnbanCasterIPs()");
            }
        }

        public static void BanPeer(string ipAddress, string message, string location)
        {
            if (Globals.AdjudicateAccount == null)
            {
                if (Globals.AdjNodes.ContainsKey(ipAddress))
                    return;
            }
            else
            {
                if (Globals.Nodes.ContainsKey(ipAddress))
                    return;
            }

            // FIX 1: Never ban active caster IPs — doing so breaks consensus quorum
            // and causes cascading network splits where casters ban each other.
            if (IsCasterIP(ipAddress))
            {
                BanLogUtility.Log($"BanPeer SKIPPED — {ipAddress} is an active caster IP. Reason: {message}", location);
                return;
            }

            var peers = Peers.GetAll();
            var peer = Peers.GetPeer(ipAddress);
            if(peer == null)
            {
                var nPeer = new Peers
                {
                    BanCount = 1,
                    BannedFromAreasList = new List<string> { location },
                    FailCount = 0,
                    InitialBanDate = DateTime.UtcNow,
                    IsBanned = true,
                    IsIncoming = true,
                    IsOutgoing = false,
                    LastBanDate = DateTime.UtcNow,
                    LastBannedFromArea = location,
                    NextUnbanDate = GetNextUnbanDate(1),
                    PeerIP= ipAddress,  
                };

                if(peers != null)
                {
                    peers.InsertSafe(nPeer);
                }
                BanLogUtility.Log($"IP Address Banned: {ipAddress}. Ban count: {1}. Ban reason: {message}", location);
                Globals.BannedIPs[ipAddress] = nPeer;
            }
            else
            {
                if(!peer.IsPermaBanned && !peer.IsValidator)
                {
                    if (peer.BannedFromAreasList != null)
                    {
                        peer.BannedFromAreasList.Add(location);
                    }
                    else
                    {
                        peer.BannedFromAreasList = new List<string> { location };
                    }
                    peer.BanCount += 1;
                    peer.NextUnbanDate = GetNextUnbanDate(peer.BanCount);
                    peer.LastBannedFromArea = location;
                    peer.LastBanDate = DateTime.UtcNow;
                    peer.IsBanned = true;

                    if (peers != null)
                    {
                        peers.UpdateSafe(peer);
                    }
                }
                
                Globals.BannedIPs[ipAddress] = peer;
            }

            ReleasePeer(ipAddress);
        }

        public static void UnbanPeer(string ipAddress)
        {
            try
            {
                Globals.BannedIPs.TryRemove(ipAddress, out _);
                Globals.MessageLocks.TryRemove(ipAddress, out _);

                var peerDb = Peers.GetAll();
                var peer = peerDb.FindOne(x => x.PeerIP == ipAddress);
                if (peer != null)
                {
                    peer.IsBanned = false;
                    peer.IsPermaBanned = false;
                    peer.BanCount = 0;
                    peer.InitialBanDate = null;
                    peer.NextUnbanDate = null;
                    peer.LastBanDate = null;
                    peer.BannedFromAreasList = null;
                    peer.LastBannedFromArea = null;
                    peerDb.UpdateSafe(peer);
                }
            }
            catch { }
        }

        private static DateTime GetNextUnbanDate(int banCount)
        {
            // FORK-FIX: Softened ban escalation — the old schedule was too aggressive.
            // During a fork, legitimate validators rapidly accumulate bans (parent hash
            // mismatch, block reception errors) and get perma-banned after ~10 occurrences.
            // New schedule: much longer ramp before serious bans, and perma-ban threshold
            // raised from 10 to 25 to survive extended network disagreements.
            if(banCount <= 2)
            {
                return DateTime.UtcNow.AddMinutes(1);
            }
            else if(banCount <= 4)
            {
                return DateTime.UtcNow.AddMinutes(5);
            }
            else if(banCount <= 7)
            {
                return DateTime.UtcNow.AddMinutes(15);
            }
            else if(banCount <= 10)
            {
                return DateTime.UtcNow.AddMinutes(30);
            }
            else if(banCount <= 15)
            {
                return DateTime.UtcNow.AddHours(1);
            }
            else if(banCount <= 20)
            {
                return DateTime.UtcNow.AddHours(6);
            }
            else if(banCount <= 25)
            {
                return DateTime.UtcNow.AddHours(24);
            }
            else
            {
                return DateTime.UtcNow.AddYears(99); //perma banned now (was 10, now 25+)
            }
        }

        public static async Task PeerBanUnbanService()
        {
            while(true)
            {
                var delay = Task.Delay(60000);
                if (Globals.StopAllTimers && !Globals.IsChainSynced)
                {
                    await delay;
                    continue;
                }
                await BanServiceLock.WaitAsync();
                try
                {
                    await UpdateABL();

                    await RunUnban();

                    await RunBanReset();

                    await RunPermaBan();
                }
                finally
                {
                    BanServiceLock.Release();
                }

                await delay;
            }
        }
        private static async Task UpdateABL()
        {
            Config.Config.ProcessABL();
        }

        private static void ReleasePeer(string ipAddress)
        {
            try
            {
                // Disconnect from all SignalR hub connections
                if (Globals.P2PPeerDict.TryRemove(ipAddress, out var peerContext))
                    peerContext?.Abort();

                if (Globals.P2PValDict.TryRemove(ipAddress, out var valContext))
                    valContext?.Abort();

                if (Globals.BeaconPeerDict.TryRemove(ipAddress, out var beaconContext))
                    beaconContext?.Abort();

                // Disconnect from Fortis pool
                if (Globals.FortisPool.TryGetFromKey1(ipAddress, out var pool))
                    pool.Value.Context?.Abort();

                // Disconnect from node connections
                if (Globals.AdjNodes.TryRemove(ipAddress, out var adjnode) && adjnode.Connection != null)
                    adjnode.Connection.DisposeAsync().ConfigureAwait(false).GetAwaiter().GetResult();

                if (Globals.Nodes.TryRemove(ipAddress, out var node) && node.Connection != null)
                    node.Connection.DisposeAsync().ConfigureAwait(false).GetAwaiter().GetResult();

                // Remove from validator registry (by IP address)
                var normalizedIP = ipAddress.Replace("::ffff:", "");
                var validatorsToRemove = Globals.NetworkValidators
                    .Where(kvp => kvp.Value.IPAddress == normalizedIP)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var validatorAddress in validatorsToRemove)
                {
                    Globals.NetworkValidators.TryRemove(validatorAddress, out _);
                }
            }
            catch (Exception ex)
            {
                // Log any exceptions during peer release
                ErrorLogUtility.LogError(
                    $"Exception during ReleasePeer for {ipAddress}: {ex.Message}",
                    "BanService.ReleasePeer()");
            }
        }

        public static async Task RunUnban()
        {
            // FIX 2: Always unban caster IPs first, regardless of their NextUnbanDate.
            // This is a safety net for any caster IPs that were banned before Fix 1 was deployed,
            // or that were banned via a code path that bypasses BanPeer (e.g., direct dictionary insert).
            UnbanCasterIPs();

            try
            {
                var peers = Peers.GetAll();
                if (peers != null)
                {
                    var bannedPeers = peers.Query().Where(x =>
                            x.IsBanned &&
                            x.NextUnbanDate != null &&
                            x.NextUnbanDate.Value <= DateTime.UtcNow &&
                            !x.IsPermaBanned).ToEnumerable();

                    if (bannedPeers.Count() > 0)
                    {
                        foreach (var bPeer in bannedPeers)
                        {
                            bPeer.IsBanned = false;
                            peers.UpdateSafe(bPeer);
                            Globals.BannedIPs.TryRemove(bPeer.PeerIP, out _);
                            Globals.MessageLocks.TryRemove(bPeer.PeerIP, out _);
                        }
                    }
                }
            }
            catch(Exception ex)
            {

            }
            
        }

        private static async Task RunBanReset()
        {
            try
            {
                var peers = Peers.GetAll();
                if (peers != null)
                {
                    var unbannedPeersWithCount = peers.Query().Where(x =>
                        x.BanCount > 0 &&
                        !x.IsBanned &&
                        !x.IsPermaBanned &&
                        x.NextUnbanDate != null &&
                        x.NextUnbanDate.Value.AddHours(1) <= DateTime.UtcNow).ToEnumerable();

                    if (unbannedPeersWithCount.Count() > 0)
                    {
                        foreach (var ubPeer in unbannedPeersWithCount)
                        {
                            ubPeer.BanCount = 0;
                            ubPeer.InitialBanDate = null;
                            ubPeer.NextUnbanDate = null;
                            ubPeer.LastBanDate = null;
                            ubPeer.BannedFromAreasList = null;
                            ubPeer.LastBannedFromArea = null;
                            peers.UpdateSafe(ubPeer);
                            Globals.BannedIPs.TryRemove(ubPeer.PeerIP, out _);
                            Globals.MessageLocks.TryRemove(ubPeer.PeerIP, out _);
                        }
                    }
                }
            }
            catch { }
        }

        private static async Task RunPermaBan()
        {
            try
            {
                var peers = Peers.GetAll();
                if (peers != null)
                {
                    // FORK-FIX: Raised perma-ban threshold from 10 to 25 to match softened escalation.
                    // During fork events, legitimate validators can accumulate many short bans
                    // from parent hash mismatches. They shouldn't be perma-banned for this.
                    var permaBanList = peers.Query().Where(x =>
                        x.IsBanned &&
                        x.BanCount > 25 &&
                        !x.IsPermaBanned).ToEnumerable();

                    if (permaBanList.Count() > 0)
                    {
                        foreach (var permaBanPeer in permaBanList)
                        {
                            permaBanPeer.IsPermaBanned = true;
                            peers.UpdateSafe(permaBanPeer);
                            Globals.BannedIPs[permaBanPeer.PeerIP] = permaBanPeer;
                        }
                    }
                }
            }
            catch { }
        }
    }
}
