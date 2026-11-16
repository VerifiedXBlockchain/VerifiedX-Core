using Newtonsoft.Json;
using VerifiedXCore.Models;

namespace VerifiedXCore.Utilities
{
    public class MempoolSizeUtility
    {
        /// <summary>Block space for the priority lane, taken before and outside the pool's 1 MB.</summary>
        public const long PriorityLaneBytes = 1024 * 1024;

        /// <summary>
        /// The transactions whose timing matters to the chain and its funds, in the order the lane takes them: the Bitcoin
        /// report or rewind (the chain's view of Bitcoin waits for it), vault re-keys (a vault whose holders fall below its
        /// threshold first is lost), validator heartbeats and registrations (a validator leaves the active set without
        /// one), and reserve Recover() (refused ten minutes after it is signed). Null for every other transaction.
        /// </summary>
        public static int? PriorityLaneRank(Transaction tx) => tx.TransactionType switch
        {
            TransactionType.VBTC_V2_BTC_REPORT or TransactionType.VBTC_V2_BTC_REORG => 0,
            TransactionType.VBTC_V2_VAULT_REKEY => 1,
            TransactionType.VBTC_V2_VALIDATOR_HEARTBEAT or TransactionType.VBTC_V2_VALIDATOR_REGISTER => 2,
            TransactionType.RESERVE when IsRecover(tx) => 3,
            _ => null,
        };

        public static bool InPriorityLane(Transaction tx) => PriorityLaneRank(tx) != null;

        private static bool IsRecover(Transaction tx)
        {
            if (string.IsNullOrEmpty(tx.Data)) return false;
            try { return (string?)Newtonsoft.Json.Linq.JObject.Parse(tx.Data)["Function"] == "Recover()"; }
            catch (JsonException) { return false; }
        }

        public static List<Transaction> SizeMempoolDown(List<Transaction> mempoolTxs)
        {
            var txList = new List<Transaction>();
            long sizeCount = 0;

            var eligible = mempoolTxs.Where(x => x.TransactionRating != null && x.TransactionRating != TransactionRating.F).ToList();

            // The priority lane first, in its own space: within a rating the pool is taken oldest first, and a sender
            // chooses its timestamp, so these could otherwise be kept out of blocks. One per sender (the Bitcoin
            // attestation's sender-less transactions are bounded by their one-per-block rules and validator signatures);
            // what the lane has no room for joins the rest of the pool.
            var lane = new List<Transaction>();
            long laneBytes = 0;
            var laneSenders = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tx in eligible.Where(InPriorityLane).OrderBy(x => PriorityLaneRank(x)).ThenBy(x => x.Timestamp))
            {
                if (!VerifiedXCore.Bitcoin.Attestation.BtcAttestTx.IsAttestationType(tx.TransactionType) && !laneSenders.Add(tx.FromAddress ?? ""))
                    continue;
                var size = JsonConvert.SerializeObject(tx).Length;
                if (laneBytes + size > PriorityLaneBytes)
                    continue;
                lane.Add(tx);
                laneBytes += size;
            }
            var inLane = new HashSet<Transaction>(lane, ReferenceEqualityComparer.Instance);

            //Sorts the rest of the mempool by rating and then by date received
            var mempoolSorted = eligible.Where(x => !inLane.Contains(x)).OrderBy(x => x.TransactionRating).ThenBy(x => x.Timestamp);

            foreach (var mempoolTx in mempoolSorted)
            {
                var result = VerifyTXSize(mempoolTx, sizeCount);
                if(result.Item1 == true)
                {
                    txList.Add(mempoolTx);
                    sizeCount += result.Item2;
                }
            }

            return lane.Concat(txList).ToList();
        }

        private static (bool, long) VerifyTXSize(Transaction tx, long currentSize)
        {
            var txJsonSize = JsonConvert.SerializeObject(tx);
            var size = Convert.ToInt64(txJsonSize.Length);

            var mempoolSize = Convert.ToInt64(currentSize + size).ToSize(GenericExtensions.SizeUnits.MB);
            
            if (mempoolSize > 1.00M)
            {
                return (false, 0);
            }

            return (true, size);
        }
    }
}
