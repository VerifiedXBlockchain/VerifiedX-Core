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
            /// <summary>Exits the block has completed ("complete:" + burn) or failed ("fail:" + burn), from Globals.BridgeFailKeepsLocksHeight.</summary>
            public HashSet<string> ClosedExits { get; } = new(StringComparer.Ordinal);
        }

        /// <summary>The reason a second failure of one exit in a block is refused (a producer's fault, not this node's state).</summary>
        public const string ExitAlreadyFailedPrefix = "Bridge exit already failed within this block";

        /// <summary>The reason a completion and a failure of one exit in one block are refused (a producer's fault, not this node's state).</summary>
        public const string ExitAlreadyClosedPrefix = "Bridge exit already completed or failed within this block";

        public static bool AppliesTo(TransactionType type) =>
            type == TransactionType.VBTC_V2_BRIDGE_POOL_UNLOCK
            || type == TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC
            || type == TransactionType.VBTC_V2_BRIDGE_UNLOCK
            || type == TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC_FAIL
            || type == TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC_COMPLETE
            || type == TransactionType.VBTC_V2_BRIDGE_BURN_REFUND; // claims its burn: an exit and a refund of one burn do not share a block

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

            // A completion and a failure of one exit do not share a block, from Globals.BridgeFailKeepsLocksHeight: both are
            // judged on the exit as the block before left it, and from that height a failure gives its locks back for use,
            // so an exit completed (paid on Bitcoin) and failed in one block left its paid locks to be drawn again. The
            // second waits for the next block, where the exit's state decides it.
            if (tx.TransactionType == TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC_COMPLETE)
            {
                if (at < Globals.BridgeFailKeepsLocksHeight) return (true, "");
                var completed = NormalizeBurn(jobj["BaseBurnTxHash"]?.ToString());
                if (completed.Length == 0) return (true, "");
                if (state.ClosedExits.Contains("fail:" + completed) || !state.ClosedExits.Add("complete:" + completed))
                    return (false, $"{ExitAlreadyClosedPrefix}: burn {completed} is closed by an earlier transaction of the block.");
                return (true, "");
            }

            // One failure per exit per block, from the path-audit rules. Each failure is judged on the exit's plan as the
            // block before left it; a second failure of the same part found the plan already trimmed by the first, so
            // nothing matched, and apply failed the whole exit - its paid part then re-exited and was paid again.
            if (tx.TransactionType == TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC_FAIL)
            {
                if (at < Globals.PathAuditRulesHeight) return (true, "");
                var failed = NormalizeBurn(jobj["BaseBurnTxHash"]?.ToString());
                if (failed.Length == 0) return (true, "");
                if (at >= Globals.BridgeFailKeepsLocksHeight && state.ClosedExits.Contains("complete:" + failed))
                    return (false, $"{ExitAlreadyClosedPrefix}: burn {failed} is closed by an earlier transaction of the block.");
                if (!state.BurnHashes.Add("fail:" + failed))
                    return (false, $"{ExitAlreadyFailedPrefix}: burn {failed} is failed by an earlier transaction of the block.");
                if (at >= Globals.BridgeFailKeepsLocksHeight) state.ClosedExits.Add("fail:" + failed);
                return (true, "");
            }

            var burn = NormalizeBurn(RedeemedBurn(tx.TransactionType, jobj, at));
            var lockIds = at >= Globals.BridgeAllocationsArrayHeight ? AllocatedLockIds(jobj) : ExtractLockIds(jobj, asApply: at >= Globals.PathAuditRulesHeight);

            // Check everything first, then register, so a rejected tx leaves the state untouched.
            // A transaction's hash and one of its burns named by position are the same burn too (#277).
            if (burn.Length > 0 && state.BurnHashes.Any(b => !b.StartsWith("fail:", StringComparison.Ordinal) && VerifiedXCore.Bitcoin.Models.VBTCBridgeConsumedBurn.Overlaps(b, burn)))
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
            // A refund consumes BaseBurnTxHash and reads no other burn field.
            if (type == TransactionType.VBTC_V2_BRIDGE_BURN_REFUND)
                return jobj["BaseBurnTxHash"]?.ToString();
            return jobj["ExitBurnTxHash"]?.ToString() ?? jobj["BaseBurnTxHash"]?.ToString();
        }

        public static string NormalizeBurn(string? hash)
        {
            var h = (hash ?? string.Empty).Trim().ToLowerInvariant();
            if (h.StartsWith("0x", StringComparison.Ordinal)) h = h.Substring(2);
            return h;
        }

        /// <summary>
        /// From Globals.BridgeAllocationsArrayHeight: every lock a bridge transaction names, trimmed and lower-cased as the
        /// lock look-up matches them: each allocation's lock, read whatever the shape of Allocations, and the outer LockId
        /// (what a bridge unlock draws). Before it an object-shaped Allocations was claimed only by the outer LockId, which
        /// could name a made-up lock while the allocations drew a real one, so two exits in one block drew on one lock.
        /// Claiming both never takes fewer locks than before.
        /// </summary>
        public static List<string> AllocatedLockIds(JObject jobj)
        {
            var allocs = jobj["Allocations"];
            List<VerifiedXCore.Bitcoin.Models.PoolUnlockAllocation>? read = null;
            if (allocs != null)
            {
                try { read = allocs.ToObject<List<VerifiedXCore.Bitcoin.Models.PoolUnlockAllocation>>(); }
                catch { read = null; }
            }
            var ids = (read ?? new()).Select(a => a?.LockId).Append(jobj["LockId"]?.ToString());
            return ids.Select(NormalizeLockId).Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        }

        /// <summary>A lock id as the lock look-up matches it: trimmed, any case.</summary>
        public static string NormalizeLockId(string? lockId) => (lockId ?? string.Empty).Trim().ToLowerInvariant();

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
