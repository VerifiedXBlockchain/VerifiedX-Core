using ReserveBlockCore;
using ReserveBlockCore.Bitcoin.Models;
using ReserveBlockCore.Services;

namespace ReserveBlockCore.Bitcoin.Services
{
    /// <summary>
    /// Validates adaptive-majority caster votes for Base bridge exit / sync flows (VFX signatures).
    /// </summary>
    public static class BridgeCasterConsensus
    {
        public static int RequiredCasterVotes => Math.Max(2, Globals.ActiveCasterCount / 2 + 1);

        public static string BuildVoteMessage(string baseBurnTxHash, string burnType, long timestamp) =>
            $"VFX_BRIDGE_BURN|{baseBurnTxHash}|{burnType}|{timestamp}";

        public static bool TryVerifyVotes(IEnumerable<CasterConsensusVote>? votes, string baseBurnTxHash, string burnType)
        {
            if (votes == null)
                return false;
            var ok = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in votes)
            {
                if (v == null || string.IsNullOrEmpty(v.CasterAddress)) continue;
                if (!string.Equals(v.BaseBurnTxHash, baseBurnTxHash, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!string.Equals(v.BurnType, burnType, StringComparison.Ordinal))
                    continue;
                var msg = BuildVoteMessage(v.BaseBurnTxHash, v.BurnType, v.Timestamp);
                if (string.IsNullOrEmpty(v.Signature)) continue;
                if (!ReserveBlockCore.Services.SignatureService.VerifySignature(v.CasterAddress, msg, v.Signature))
                    continue;
                ok.Add(v.CasterAddress);
            }
            return ok.Count >= RequiredCasterVotes;
        }
    }
}
