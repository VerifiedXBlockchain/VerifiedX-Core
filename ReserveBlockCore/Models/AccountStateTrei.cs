using ReserveBlockCore.Extensions;
using ReserveBlockCore.Data;

namespace ReserveBlockCore.Models
{
    public class AccountStateTrei
    {
        public long Id { get; set; }
        public string Key { get; set; }
        public string? RecoveryAccount { get; set; }
        public long Nonce { get; set; }
        public decimal Balance { get; set; }
        public decimal LockedBalance { get; set; }
        public string StateRoot { get; set; }
        public string CodeHash { get; set; }
        public List<TokenAccount>? TokenAccounts { get; set; }

        public static decimal GetAccountBalance(string address)
        {
            var balance = 0.00M;
            var accounts = DbContext.DB_AccountStateTrei.GetCollection<AccountStateTrei>(DbContext.RSRV_ASTATE_TREI);
            var account = accounts.FindOne(x => x.Key == address);
            if(account != null)
            {
                balance = account.Balance;
            }    

            return balance;
        }

        public static decimal GetNetworkTotal()
        {
            var accounts = DbContext.DB_AccountStateTrei.GetCollection<AccountStateTrei>(DbContext.RSRV_ASTATE_TREI);
            var networkBalance = accounts.Query().Where(x => x.Balance > 0.00M).ToList().Sum(x => x.Balance);

            return networkBalance;
        }

        public static long GetNextNonce(string address)
        {
            long nonce = 0;
            var accounts = DbContext.DB_AccountStateTrei.GetCollection<AccountStateTrei>(DbContext.RSRV_ASTATE_TREI);
            var account = accounts.FindOne(x => x.Key == address);
            if (account != null)
            {
                nonce = account.Nonce;
            }

            // HAL-067 Fix: Check mempool for pending TXs from this address to prevent duplicate nonces
            // when sending multiple transactions in rapid succession
            var mempool = Data.TransactionData.GetPool();
            if (mempool != null)
            {
                var pendingTxs = mempool.Find(x => x.FromAddress == address)
                    .OrderBy(x => x.Nonce)
                    .ToList();

                if (pendingTxs.Any())
                {
                    // Return highest mempool nonce + 1
                    var highestNonce = pendingTxs.Last().Nonce;
                    if (highestNonce >= nonce)
                    {
                        nonce = highestNonce + 1;
                    }
                }
            }

            return nonce;
        }
    }
}
