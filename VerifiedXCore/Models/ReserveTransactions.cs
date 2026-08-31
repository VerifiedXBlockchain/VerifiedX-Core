using LiteDB;
using VerifiedXCore.Data;
using VerifiedXCore.Utilities;
using System.ComponentModel.DataAnnotations;

namespace VerifiedXCore.Models
{
    public class ReserveTransactions
    {
        [BsonId]
        public Guid Id { get; set; }
        public string Hash { get; set; }
        public long ConfirmTimestamp { get; set; } //will not be valid till after this.
        public string FromAddress { get; set; }
        public string ToAddress { get; set; }
        public decimal Amount { get; set; }
        public long Nonce { get; set; }
        public decimal Fee { get; set; }
        public long Timestamp { get; set; }
        public string? Data { get; set; } = null;
        public long? UnlockTime { get; set; } = null;

        [StringLength(512)]
        public string Signature { get; set; }
        public long Height { get; set; }
        public TransactionType TransactionType { get; set; }
        public ReserveTransactionStatus ReserveTransactionStatus { get; set; }


        #region Get ReserveTransactions DB
        public static LiteDB.ILiteCollection<ReserveTransactions>? GetReserveTransactionsDb()
        {
            try
            {
                var rTx = DbContext.DB_Reserve.GetCollection<ReserveTransactions>(DbContext.RSRV_RESERVE_TRANSACTIONS);
                return rTx;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError(ex.ToString(), "ReserveTransactions.GetReserveTransactionsDb()");
                return null;
            }

        }

        #endregion

        #region Get ReserveTransactionsCalledBack DB
        public static LiteDB.ILiteCollection<string>? GetReserveTransactionsCalledBackDb()
        {
            try
            {
                var rTx = DbContext.DB_Reserve.GetCollection<string>(DbContext.RSRV_RESERVE_TRANSACTIONS_CALLED_BACK);
                return rTx;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError(ex.ToString(), "ReserveTransactions.GetReserveTransactionsCalledBackDb()");
                return null;
            }

        }

        #endregion

        #region Get ReserveTransactions transaction
        public static ReserveTransactions? GetTransactions(string hash)
        {
            try
            {
                var db = GetReserveTransactionsDb();
                var rec = db.Query().Where(x => x.Hash == hash).FirstOrDefault();
                if (rec != null)
                {
                    return rec;
                }
                return null;
            }
            catch (Exception ex)
            {

            }
            return null;
        }

        #endregion

        #region Get ReserveTransactions transaction list
        public static IEnumerable<ReserveTransactions>? GetTransactionList(string fromAddress)
        {
            try
            {
                var db = GetReserveTransactionsDb();
                var rec = db.Query().Where(x => x.FromAddress == fromAddress).ToEnumerable();
                if (rec != null)
                {
                    return rec;
                }
                return null;
            }
            catch (Exception ex)
            {

            }
            return null;
        }

        #endregion

        #region Save Reserve Transactions
        public static void SaveReserveTx(ReserveTransactions rTx)
        {
            try
            {
                var db = GetReserveTransactionsDb();
                var rec = db.FindOne(x => x.Hash == rTx.Hash);
                if(rec == null)
                {
                    db.InsertSafe(rTx);
                }
            }
            catch (Exception ex)
            {

            }

        }

        #endregion

        #region Get pending vBTC V2 reserve-transfer total
        /// <summary>
        /// Sum of in-flight (Pending) VBTC_V2_TRANSFER amounts from a reserve address for one
        /// contract. Reserve vBTC sends defer the ledger debit until unlock, so consensus must
        /// subtract these from the spendable balance or the same balance could be spent
        /// repeatedly during the unlock window. Rows are written at block-apply on every node,
        /// so this is deterministic. excludeHash skips the TX being validated (replay safety).
        /// </summary>
        public static decimal GetPendingVBTCTransferTotal(string fromAddress, string scUID, string? excludeHash = null)
        {
            try
            {
                var db = GetReserveTransactionsDb();
                if (db == null) return 0M;

                var pending = db.Query().Where(x => x.FromAddress == fromAddress
                    && x.TransactionType == TransactionType.VBTC_V2_TRANSFER
                    && x.ReserveTransactionStatus == ReserveTransactionStatus.Pending).ToList();

                decimal total = 0M;
                foreach (var rtx in pending)
                {
                    if (excludeHash != null && rtx.Hash == excludeHash) continue;
                    if (string.IsNullOrEmpty(rtx.Data)) continue;
                    try
                    {
                        var jobj = Newtonsoft.Json.Linq.JObject.Parse(rtx.Data);
                        if (jobj["ContractUID"]?.ToObject<string>() != scUID) continue;
                        var amt = jobj["Amount"]?.ToObject<decimal?>();
                        if (amt.HasValue && amt.Value > 0) total += amt.Value;
                    }
                    catch { }
                }
                return total;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError(ex.ToString(), "ReserveTransactions.GetPendingVBTCTransferTotal()");
                return 0M;
            }
        }

        #endregion

        #region Get ReserveTransactions transaction called back list
        public static bool GetTransactionsCalledBack(string hash)
        {
            try
            {
                var db = GetReserveTransactionsCalledBackDb();
                var rec = db.Query().Where(x => x == hash).FirstOrDefault();
                if (rec != null)
                {
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                return false;
            }
        }

        #endregion

        #region Save Reserve Transactions
        public static void SaveReserveTxCallBack(string rTxHash)
        {
            try
            {
                var db = GetReserveTransactionsCalledBackDb();
                var rec = db.FindOne(x => x == rTxHash);
                if (rec == null)
                {
                    db.InsertSafe(rTxHash);
                }
            }
            catch (Exception ex)
            {

            }

        }

        #endregion

    }
    public enum ReserveTransactionStatus
    {
        Pending,
        Confirmed,
        CalledBack,
        Recovered
    }

}
