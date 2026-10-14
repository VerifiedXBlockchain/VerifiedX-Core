using Newtonsoft.Json.Linq;
using VerifiedXCore.Models;

namespace VerifiedXCore.Bitcoin.Services
{
    /// <summary>
    /// Per-block conflict tracking for bridge transactions. Transaction validation sees only
    /// persisted state, so two bridge transactions in ONE block that redeem the same Base burn, or
    /// draw on the same lock, each pass individually. Tracking what the block has already claimed
    /// lets validation reject the second one deterministically (mirrors the per-block nullifier and
    /// withdrawal-contract tracking).
    /// </summary>
    public static class BridgeIntraBlockGuard
    {
        public sealed class State
        {
            public HashSet<string> BurnHashes { get; } = new(StringComparer.Ordinal);
            public HashSet<string> LockIds { get; } = new(StringComparer.Ordinal);
        }

        public static bool AppliesTo(TransactionType type) =>
            type == TransactionType.VBTC_V2_BRIDGE_POOL_UNLOCK
            || type == TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC
            || type == TransactionType.VBTC_V2_BRIDGE_UNLOCK;

        /// <summary>
        /// Registers the burn hash and lock IDs a bridge transaction claims. Returns false (with a
        /// reason) when the block has already claimed any of them. Non-bridge transactions and
        /// unparseable data pass through untouched.
        /// </summary>
        public static (bool Ok, string Reason) TryRegister(Transaction tx, State state, long? height = null)
        {
            if (tx == null || state == null || !AppliesTo(tx.TransactionType)) return (true, "");
            if (string.IsNullOrWhiteSpace(tx.Data)) return (true, "");

            JObject jobj;
            try { jobj = JObject.Parse(tx.Data); }
            catch { return (true, ""); }

            var at = height ?? ((Globals.LastBlock?.Height ?? -1) + 1);
            var burn = NormalizeBurn(RedeemedBurn(tx.TransactionType, jobj, at));
            var lockIds = ExtractLockIds(jobj, asApply: at >= Globals.PathAuditRulesHeight);

            // Check everything first, then register, so a rejected tx leaves the state untouched.
            if (burn.Length > 0 && state.BurnHashes.Contains(burn))
                return (false, $"Duplicate Base burn {burn} within block (already redeemed by an earlier bridge transaction).");
            foreach (var id in lockIds)
                if (state.LockIds.Contains(id))
                    return (false, $"Bridge lock {id} is already drawn on by an earlier bridge transaction within this block.");

            if (burn.Length > 0) state.BurnHashes.Add(burn);
            foreach (var id in lockIds) state.LockIds.Add(id);
            return (true, "");
        }

        /// <summary>
        /// The burn a bridge transaction redeems. An exit to Bitcoin redeems BaseBurnTxHash; before the path-audit rules
        /// the guard took ExitBurnTxHash first, a field the exit never reads, so one committee caster could give each copy
        /// of an exit its own made-up ExitBurnTxHash and spell BaseBurnTxHash differently: every copy passed, each drew its
        /// own locks, and only the first was recorded and paid (fifth review, round 13).
        /// </summary>
        public static string? RedeemedBurn(TransactionType type, JObject jobj, long height)
        {
            if (type == TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC && height >= Globals.PathAuditRulesHeight)
                return jobj["BaseBurnTxHash"]?.ToString();
            return jobj["ExitBurnTxHash"]?.ToString() ?? jobj["BaseBurnTxHash"]?.ToString();
        }

        public static string NormalizeBurn(string? hash)
        {
            var h = (hash ?? string.Empty).Trim().ToLowerInvariant();
            if (h.StartsWith("0x", StringComparison.Ordinal)) h = h.Substring(2);
            return h;
        }

        public static List<string> ExtractLockIds(JObject jobj, bool asApply = false)
        {
            var ids = new List<string>();
            var allocs = jobj["Allocations"];
            // From the path-audit rules, as apply reads them (the typed reader matches a key in any case, and the last of
            // two spellings wins): "lockId" was not seen here, and two exits drew one lock in one block; the second was
            // paid without its draw (fifth review, round 16).
            if (asApply && allocs is JArray)
            {
                foreach (var a in allocs.ToObject<List<VerifiedXCore.Bitcoin.Models.PoolUnlockAllocation>>() ?? new())
                {
                    var id = a?.LockId?.Trim();
                    if (!string.IsNullOrEmpty(id)) ids.Add(id);
                }
                return ids.Distinct(StringComparer.Ordinal).ToList();
            }
            if (allocs is JArray arr)
            {
                foreach (var a in arr)
                {
                    var id = a?["LockId"]?.ToString()?.Trim();
                    if (!string.IsNullOrEmpty(id)) ids.Add(id);
                }
            }
            else
            {
                var single = jobj["LockId"]?.ToString()?.Trim();
                if (!string.IsNullOrEmpty(single)) ids.Add(single);
            }
            return ids.Distinct(StringComparer.Ordinal).ToList();
        }
    }
}
