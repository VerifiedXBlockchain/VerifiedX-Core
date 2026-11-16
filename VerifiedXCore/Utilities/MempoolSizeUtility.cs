using Newtonsoft.Json;
using VerifiedXCore.Models;

namespace VerifiedXCore.Utilities
{
    public class MempoolSizeUtility
    {
        public static List<Transaction> SizeMempoolDown(List<Transaction> mempoolTxs)
        {
            var txList = new List<Transaction>();
            long sizeCount = 0;

            var eligible = mempoolTxs.Where(x => x.TransactionRating != null && x.TransactionRating != TransactionRating.F).ToList();

            // Each sender's earliest validator heartbeat goes first: free transfers share its rating and may be dated up to
            // the age limit back, so sorted by time alone a flood of them kept heartbeats out until validators left the
            // active set. One per sender, and a heartbeat needs a funded validator address, so this cannot fill a block.
            var heartbeats = eligible.Where(x => x.TransactionType == TransactionType.VBTC_V2_VALIDATOR_HEARTBEAT)
                .GroupBy(x => x.FromAddress).Select(g => g.OrderBy(x => x.Timestamp).First()).ToList();
            var first = new HashSet<Transaction>(heartbeats, ReferenceEqualityComparer.Instance);

            //Sorts the rest of the mempool by rating and then by date received
            var mempoolSorted = heartbeats.Concat(eligible.Where(x => !first.Contains(x)).OrderBy(x => x.TransactionRating).ThenBy(x => x.Timestamp));

            foreach (var mempoolTx in mempoolSorted)
            {
                var result = VerifyTXSize(mempoolTx, sizeCount);
                if(result.Item1 == true)
                {
                    txList.Add(mempoolTx);
                    sizeCount += result.Item2;
                }
            }

            return txList;
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
