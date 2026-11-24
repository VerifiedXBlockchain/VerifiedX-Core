using Newtonsoft.Json;
using VerifiedXCore.Models;

namespace VerifiedXCore.Services
{
    public class FeeCalcService
    {
        /// <summary>The fee <see cref="CalculateTXFee"/> gives a transaction of <paramref name="bytes"/>.</summary>
        public static decimal CalculateFeeForBytes(long bytes)
        {
            var fee = (bytes / 1024.0M) * 0.00002M;
            return fee < 0.000003M ? 0.000003M : decimal.Round(fee, 8, MidpointRounding.ToPositiveInfinity);
        }

        public static decimal CalculateTXFee(Transaction tx)
        {
            var txFee = new decimal();
            var baseFeeMultiplier = 0.00002M;
            var kb = 1024.0M;

            var txSize = JsonConvert.SerializeObject(tx).Length;
            
            txFee = (txSize / kb) * baseFeeMultiplier;

            //base fee of 0.000003 must always exist
            if (txFee < 0.000003M)
            {
                txFee = 0.000003M;
            }
            else
            {
                //fees are now capped at 8 decimal places and rounded up at the 8th decimal place
                txFee = decimal.Round(txFee, 8, MidpointRounding.ToPositiveInfinity);
            }
            // From Globals.CanonicalInputHeight written the one way Amount and Fee are accepted (0.00000600 as 0.000006):
            // rounding keeps eight places, trailing zeros included. The same value; below the height as before.
            return LedgerIntegrityRules.SpellForNextBlock(txFee, legacy: txFee);
        }

        public static decimal GetFeeMinimum()
        {
            if(Globals.LastBlock.Height > 9999999)
            {
                return 0.000003M;
            }
            else
            {
                return 0.0M;
            }
        }

        public static void CalculateDataFee()
        {
            //calc for data fee
        }
    }
}
