using LiteDB;
using VerifiedXCore.Data;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Bitcoin.Models
{
    /// <summary>
    /// Consensus-state registry of Base burn transaction hashes that have already been redeemed by
    /// a VFX bridge unlock. A burn is single-use: once a VBTC_V2_BRIDGE_POOL_UNLOCK (or any other
    /// unlock/exit) has been applied for it, no further unlock may reference the same hash.
    /// Replicated on every node through block application; included in state snapshots.
    /// </summary>
    public class VBTCBridgeConsumedBurn
    {
        public int Id { get; set; }
        /// <summary>Normalized (lowercase, no 0x) Base burn tx hash.</summary>
        public string BaseBurnTxHash { get; set; } = string.Empty;
        public string TxType { get; set; } = string.Empty;
        public string VfxTxHash { get; set; } = string.Empty;
        public long BlockHeight { get; set; }
        public long Timestamp { get; set; }

        public const string CollectionName = "rsrv_vbtc_bridge_consumed_burns";

        public static ILiteCollection<VBTCBridgeConsumedBurn> GetCollection()
        {
            var c = DbContext.DB_VBTCWithdrawalRequests.GetCollection<VBTCBridgeConsumedBurn>(CollectionName);
            c.EnsureIndex(x => x.BaseBurnTxHash, true);
            return c;
        }

        public static string Normalize(string? hash)
        {
            var h = (hash ?? string.Empty).Trim().ToLowerInvariant();
            if (h.StartsWith("0x", StringComparison.Ordinal)) h = h[2..];
            return h;
        }

        public static bool IsConsumed(string? hash)
        {
            var n = Normalize(hash);
            if (n.Length == 0) return false;
            try { return GetCollection().FindOne(x => x.BaseBurnTxHash == n) != null; }
            catch { return false; }
        }

        /// <summary>
        /// True if this burn hash has been redeemed by ANY bridge path: the consumed registry,
        /// an EXIT_TO_BTC record, or a lock finalized with this ExitBurnTxHash.
        /// </summary>
        public static bool IsBurnUsedAnywhere(string? hash)
        {
            var n = Normalize(hash);
            if (n.Length == 0) return false;
            if (IsConsumed(n)) return true;
            try
            {
                if (VBTCBridgeBtcExitState.GetCollection().FindAll().Any(x => Normalize(x.BaseBurnTxHash) == n))
                    return true;
            }
            catch { }
            try
            {
                if (VBTCBridgeLockState.GetCollection().FindAll().Any(x => !string.IsNullOrEmpty(x.ExitBurnTxHash) && Normalize(x.ExitBurnTxHash) == n))
                    return true;
            }
            catch { }
            return false;
        }

        public static bool TryMarkConsumed(string? hash, string txType, string vfxTxHash, long blockHeight)
        {
            var n = Normalize(hash);
            if (n.Length == 0) return false;
            try
            {
                var c = GetCollection();
                if (c.FindOne(x => x.BaseBurnTxHash == n) != null) return false;
                c.Insert(new VBTCBridgeConsumedBurn
                {
                    BaseBurnTxHash = n,
                    TxType = txType,
                    VfxTxHash = vfxTxHash ?? string.Empty,
                    BlockHeight = blockHeight,
                    Timestamp = TimeUtil.GetTime()
                });
                return true;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"VBTCBridgeConsumedBurn.TryMarkConsumed failed for {n}: {ex.Message}", "VBTCBridgeConsumedBurn");
                return false;
            }
        }
    }
}
