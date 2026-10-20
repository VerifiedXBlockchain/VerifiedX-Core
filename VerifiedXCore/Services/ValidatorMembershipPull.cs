using VerifiedXCore.Models;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Services
{
    /// <summary>
    /// A validator that is not a caster pulls the caster membership record chain once a minute (the caster loop's own
    /// cadence) from the casters it knows, the members of its own head record and the bootstrap seeds, and adopts what
    /// verifies (CasterDiscoveryService.FetchAndAdoptMembershipAsync: full successor validation, contested seqs refused).
    ///
    /// Until this (20 October 2026) a non-caster adopted records only in ValidatorNode.GetBlockcasters, which runs
    /// discovery only while its caster list is empty, so only at startup; every periodic pull lived in the caster
    /// round loop, and a finalized record was pushed only to the committee and to the validators connected to the
    /// minting caster at that moment. A validator restarted after a promotion kept an older record for good. On
    /// testnet its committee was then the three seeds, so it refused every block whose certificate carried fewer
    /// than two seed signatures (about one block in eight), fell behind, could not serve the block it had won, and
    /// the casters took the round: 54% of blocks to casters before the restart, 73% after it.
    /// </summary>
    public static class ValidatorMembershipPull
    {
        /// <summary>Seconds between pulls; the same as the caster loop's RECORD_PULL_SECONDS.</summary>
        public const int PullSeconds = 60;

        private static DateTime _lastPullUtc = DateTime.MinValue;
        private static readonly SemaphoreSlim Gate = new(1, 1);

        internal static void ResetForTests() => _lastPullUtc = DateTime.MinValue;

        /// <summary>The peers asked: known casters, the head record's members and the bootstrap seeds, never this node.</summary>
        public static List<string> PeerIps()
        {
            var head = CasterMembershipStore.GetCurrent();
            return Globals.BlockCasters.ToList().Select(c => c.PeerIP)
                .Concat((head?.Casters ?? new List<CasterInfo>()).Select(c => c.PeerIP))
                .Concat(SeedNodeService.GetBootstrapSeedPeers().Select(p => p.PeerIP))
                .Where(ip => !string.IsNullOrEmpty(ip))
                .Select(ip => ip!.Replace("::ffff:", ""))
                .Where(ip => ip != Globals.ReportedIP)
                .Distinct()
                .ToList();
        }

        /// <summary>
        /// Called on every caster-monitor tick. Pulls when this node validates but does not cast and the last pull is
        /// <see cref="PullSeconds"/> or more ago. Returns true when a pull ran. A changed head reconciles the caster
        /// list to the record (so the node connects to every current caster); an unchanged head leaves it alone.
        /// </summary>
        public static async Task<bool> PullIfDueAsync()
        {
            if (Globals.IsBlockCaster || string.IsNullOrEmpty(Globals.ValidatorAddress))
                return false;
            if ((DateTime.UtcNow - _lastPullUtc).TotalSeconds < PullSeconds)
                return false;
            if (!await Gate.WaitAsync(0))
                return false;
            try
            {
                _lastPullUtc = DateTime.UtcNow;
                var peers = PeerIps();
                if (peers.Count == 0)
                    return false;
                var before = CasterMembershipStore.GetCurrent()?.RecordSeq ?? -1;
                await CasterDiscoveryService.FetchAndAdoptMembershipAsync(peers, reconcileWhenUnchanged: false);
                var after = CasterMembershipStore.GetCurrent()?.RecordSeq ?? -1;
                if (after != before)
                    CasterLogUtility.Log($"MEMBERSHIP: validator pull moved the record head from seq {before} to seq {after}; the caster list now follows the record.", "MEMBERSHIP");
                return true;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Validator membership pull failed: {ex.Message}", "ValidatorMembershipPull.PullIfDueAsync()");
                return true;
            }
            finally
            {
                Gate.Release();
            }
        }
    }
}
