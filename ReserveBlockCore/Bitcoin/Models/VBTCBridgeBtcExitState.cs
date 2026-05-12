using LiteDB;
using ReserveBlockCore.Data;

namespace ReserveBlockCore.Bitcoin.Models
{
    /// <summary>Consensus record for Base → BTC bridge exits (dedup by Base burn tx hash).</summary>
    public class VBTCBridgeBtcExitState
    {
        [BsonId]
        public string BaseBurnTxHash { get; set; } = string.Empty;

        public string LockId { get; set; } = string.Empty;
        public string SmartContractUID { get; set; } = string.Empty;
        public string OwnerAddress { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public long AmountSats { get; set; }
        public string BtcDestination { get; set; } = string.Empty;
        public string ExitTxHash { get; set; } = string.Empty;
        public long CreatedTimestamp { get; set; }
        public bool IsComplete { get; set; }
        public string? BtcTxHash { get; set; }

        private const string CollectionName = "rsrv_vbtc_bridge_btc_exits";

        public static ILiteCollection<VBTCBridgeBtcExitState> GetCollection()
        {
            return DbContext.DB_VBTCWithdrawalRequests.GetCollection<VBTCBridgeBtcExitState>(CollectionName);
        }

        public static VBTCBridgeBtcExitState? GetByBurnHash(string baseBurnTxHash)
        {
            if (string.IsNullOrWhiteSpace(baseBurnTxHash)) return null;
            return GetCollection().FindOne(x => x.BaseBurnTxHash == baseBurnTxHash.Trim());
        }

        public static bool TryInsert(VBTCBridgeBtcExitState rec)
        {
            try
            {
                if (GetByBurnHash(rec.BaseBurnTxHash) != null)
                    return false;
                GetCollection().Insert(rec);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Alias for <see cref="GetByBurnHash"/> used by FAIL TX handler.</summary>
        public static VBTCBridgeBtcExitState? GetByBurnTxHash(string baseBurnTxHash)
            => GetByBurnHash(baseBurnTxHash);

        public static bool Update(VBTCBridgeBtcExitState rec)
        {
            try { return GetCollection().Update(rec); } catch { return false; }
        }

        public static void MarkComplete(string baseBurnTxHash, string btcTxHash)
        {
            try
            {
                var c = GetCollection();
                var r = c.FindOne(x => x.BaseBurnTxHash == baseBurnTxHash);
                if (r == null) return;
                r.IsComplete = true;
                r.BtcTxHash = btcTxHash;
                c.Update(r);
            }
            catch { }
        }
    }
}
