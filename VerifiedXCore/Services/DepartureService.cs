using Newtonsoft.Json;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;
using System.Runtime.InteropServices;

namespace VerifiedXCore.Services
{
    /// <summary>
    /// Wave 2 (/depart, /safe-update): graceful caster departure that WAITS for the network
    /// to acknowledge before shutting down. Closes the gap where BroadcastDeparture was
    /// fire-and-forget (only triggered by the ProcessExit hook) and the departing operator
    /// never knew whether a replacement was promoted.
    /// </summary>
    public static class DepartureService
    {
        private const int PollIntervalMs = 5000;

        /// <summary>
        /// Broadcasts departure (if caster), stops entering casting rounds, and polls peer
        /// casters' GetCasters lists until a majority no longer include this node — observing
        /// the effect of HandleDeparture → OnCasterRemoved → EvaluateCasterPool on the peers.
        /// Returns true when the network acknowledged the departure (or the node wasn't a
        /// caster); false on timeout (safe to proceed with shutdown, but log a warning).
        /// </summary>
        public static async Task<bool> DepartAndWaitAsync(TimeSpan timeout)
        {
            Globals.IsDeparting = true;

            if (!Globals.IsBlockCaster || string.IsNullOrEmpty(Globals.ValidatorAddress))
            {
                ConsoleWriterService.Output("[Depart] Not an active caster — nothing to hand off. Proceeding to shutdown.");
                return true;
            }

            var selfAddress = Globals.ValidatorAddress;
            var peerIPs = Globals.BlockCasters.ToList()
                .Where(c => !string.IsNullOrEmpty(c.PeerIP) && c.ValidatorAddress != selfAddress)
                .Select(c => c.PeerIP!.Replace("::ffff:", ""))
                .Distinct()
                .ToList();

            ConsoleWriterService.Output("[Depart] Broadcasting signed departure to peer casters…");
            await CasterDiscoveryService.BroadcastDeparture();

            // Stand down locally: stop being a caster and drop self from the local bag so
            // the ProcessExit departure hook no-ops (it guards on IsBlockCaster).
            Globals.IsBlockCaster = false;
            var remaining = Globals.BlockCasters.ToList().Where(c => c.ValidatorAddress != selfAddress).ToList();
            var nBag = new System.Collections.Concurrent.ConcurrentBag<Peers>();
            remaining.ForEach(x => nBag.Add(x));
            Globals.BlockCasters = nBag;
            Globals.SyncKnownCastersFromBlockCasters();

            if (peerIPs.Count == 0)
            {
                ConsoleWriterService.Output("[Depart] No peer casters to confirm with. Proceeding to shutdown.");
                return true;
            }

            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                var peerLists = await FetchPeerCasterListsAsync(peerIPs);
                if (IsSelfAbsentFromMajority(peerLists, selfAddress))
                {
                    ConsoleWriterService.Output($"[Depart] Confirmed: majority of {peerLists.Count} responding caster(s) no longer list this node. Safe to shut down.");
                    CasterLogUtility.Log($"Departure confirmed by majority of peers ({peerLists.Count} responses).", "DEPART");
                    return true;
                }
                await Task.Delay(PollIntervalMs);
            }

            ConsoleWriterService.Output("[Depart] WARNING: timed out waiting for peers to confirm removal. Proceeding with shutdown anyway — verify caster pool health after restart.");
            CasterLogUtility.Log("Departure confirmation TIMED OUT — proceeding with shutdown.", "DEPART");
            return false;
        }

        /// <summary>
        /// Pure decision helper (unit-tested): true when a strict majority of responding peers
        /// no longer include <paramref name="selfAddress"/> in their caster lists.
        /// Empty response set → false (no evidence).
        /// </summary>
        public static bool IsSelfAbsentFromMajority(List<List<string>> peerCasterLists, string selfAddress)
        {
            if (peerCasterLists == null || peerCasterLists.Count == 0)
                return false;
            var absentCount = peerCasterLists.Count(list => !list.Contains(selfAddress, StringComparer.Ordinal));
            return absentCount * 2 > peerCasterLists.Count;
        }

        private static async Task<List<List<string>>> FetchPeerCasterListsAsync(List<string> peerIPs)
        {
            var results = new List<List<string>>();
            foreach (var ip in peerIPs)
            {
                try
                {
                    using var client = Globals.HttpClientFactory.CreateClient();
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    var uri = $"http://{ip}:{Globals.ValAPIPort}/valapi/validator/GetCasters";
                    var resp = await client.GetAsync(uri, cts.Token);
                    if (!resp.IsSuccessStatusCode)
                        continue;
                    var body = await resp.Content.ReadAsStringAsync();
                    var parsed = JsonConvert.DeserializeAnonymousType(body, new { Height = 0L, Casters = new List<CasterInfo>() });
                    if (parsed?.Casters != null)
                        results.Add(parsed.Casters.Select(c => c.Address).Where(a => !string.IsNullOrEmpty(a)).ToList());
                }
                catch { /* unreachable peer — excluded from the vote */ }
            }
            return results;
        }

        /// <summary>
        /// /safe-update: download and verify the latest release for this OS → graceful departure → install the verified
        /// bytes → restart. Aborts without departing (the node keeps running in the pool) if no unambiguous OS asset is
        /// found or the download is not a signed release; the operator can fall back to the interactive /update.
        /// </summary>
        public static async Task SafeUpdateAsync()
        {
            // GetLatestDownloadFiles runs CheckVersion internally and returns the release assets.
            var assetNames = await VersionControlService.GetLatestDownloadFiles();

            if (Globals.UpToDate)
            {
                ConsoleWriterService.Output($"[SafeUpdate] Client already up to date ({Globals.CLIVersion}). Nothing to do.");
                Globals.IsDeparting = false;
                return;
            }

            var asset = SelectAssetForCurrentOS(assetNames ?? new List<string>());
            if (asset == null)
            {
                ConsoleWriterService.Output("[SafeUpdate] Could not unambiguously pick a release asset for this OS. Run /update to choose manually. Aborting (node keeps running).");
                Globals.IsDeparting = false;
                return;
            }

            ConsoleWriterService.Output($"[SafeUpdate] Downloading and verifying {asset}…");
            var zip = await VerifyThenDepartAsync(asset, () => DepartAndWaitAsync(TimeSpan.FromSeconds(60)));
            if (zip == null)
            {
                Globals.IsDeparting = false;
                return;
            }

            ConsoleWriterService.Output($"[SafeUpdate] Applying {asset}…");
            if (!VersionControlService.InstallFromBytes(zip))
            {
                ConsoleWriterService.Output("[SafeUpdate] Update FAILED. Node has departed the caster pool but is still running — restart or /update manually.");
                Globals.IsDeparting = false;
                return;
            }

            ConsoleWriterService.Output("[SafeUpdate] Update applied — restarting client…");
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                await WindowsUtilities.ClientRestart();
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                await LinuxUtilities.ClientRestart();
            else
                ConsoleWriterService.Output("[SafeUpdate] No restart command for this OS — please restart the client manually.");
        }

        /// <summary>
        /// Downloads and verifies <paramref name="asset"/> first, and departs only once it verifies, so an update that is
        /// refused never takes the node out of the caster pool. Returns the verified bytes, or null (nothing departed).
        /// </summary>
        internal static async Task<byte[]?> VerifyThenDepartAsync(string asset, Func<Task> depart)
        {
            var (zip, refusal) = await VersionControlService.DownloadVerifiedAsync(asset);
            if (zip == null)
            {
                ConsoleWriterService.Output($"[SafeUpdate] Not updating: {refusal} Node keeps running.");
                return null;
            }
            ConsoleWriterService.Output($"[SafeUpdate] {asset} is a signed release → departing gracefully before applying it…");
            await depart();
            return zip;
        }

        /// <summary>
        /// Pure helper (unit-tested): picks the single release asset matching the current OS
        /// keyword ("win" / "linux" / "osx"|"mac"). Returns null when zero or multiple match.
        /// </summary>
        public static string? SelectAssetForCurrentOS(List<string> assetNames)
        {
            string[] keywords;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                keywords = new[] { "win" };
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                keywords = new[] { "linux" };
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                keywords = new[] { "osx", "mac" };
            else
                return null;

            return SelectAssetByKeywords(assetNames, keywords);
        }

        /// <summary>OS-independent core of asset selection, for tests.</summary>
        public static string? SelectAssetByKeywords(List<string> assetNames, string[] keywords)
        {
            if (assetNames == null || assetNames.Count == 0)
                return null;
            var matches = VersionControlService.ReleaseZips(assetNames)
                .Where(a => keywords.Any(k => a.Contains(k, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            return matches.Count == 1 ? matches[0] : null;
        }
    }
}
