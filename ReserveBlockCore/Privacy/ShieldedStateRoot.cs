using System.Globalization;
using System.Linq;
using System.Text;
using ReserveBlockCore.Models.Privacy;
using ReserveBlockCore.Services;

namespace ReserveBlockCore.Privacy
{
    /// <summary>
    /// Deterministic hash of all <see cref="ShieldedPoolState"/> rows in <c>DB_Privacy</c> (post-block).
    /// Stored on <see cref="Models.WorldTrei"/> as <c>ShieldedStateRoot</c>.
    /// </summary>
    public static class ShieldedStateRoot
    {
        public static string Compute()
        {
            try
            {
                return ComputeFromPools(PrivacyDbContext.PoolState().FindAll());
            }
            catch
            {
                return "";
            }
        }

        /// <summary>Canonical hash for an ordered set of pool rows (used by <see cref="Compute"/> and tests).</summary>
        public static string ComputeFromPools(IEnumerable<ShieldedPoolState> pools)
        {
            var ordered = pools.OrderBy(x => x.AssetType, StringComparer.Ordinal).ToList();
            var sb = new StringBuilder();
            foreach (var p in ordered)
            {
                sb.Append(p.AssetType);
                sb.Append('|');
                sb.Append(p.CurrentMerkleRoot ?? "");
                sb.Append('|');
                sb.Append(p.TotalCommitments);
                sb.Append('|');
                sb.Append(p.TotalShieldedSupply.ToString("G29", CultureInfo.InvariantCulture));
                sb.Append('|');
                sb.Append(p.LastUpdateHeight);
                sb.Append(';');
            }
            if (sb.Length == 0)
                return HashingService.GenerateHash("privacy:empty");
            return HashingService.GenerateHash(sb.ToString());
        }
    }
}
