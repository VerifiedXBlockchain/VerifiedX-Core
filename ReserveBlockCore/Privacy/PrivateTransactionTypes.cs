using ReserveBlockCore.Models;

namespace ReserveBlockCore.Privacy
{
    public static class PrivateTransactionTypes
    {
        public static bool IsPrivateTransaction(TransactionType t) =>
            t == TransactionType.VFX_SHIELD
            || t == TransactionType.VFX_UNSHIELD
            || t == TransactionType.VFX_PRIVATE_TRANSFER
            || t == TransactionType.VBTC_V2_SHIELD
            || t == TransactionType.VBTC_V2_UNSHIELD
            || t == TransactionType.VBTC_V2_PRIVATE_TRANSFER;

        /// <summary>T→Z: uses transparent sender nonce and ECDSA; hash includes amount/from/nonce.</summary>
        public static bool IsTransparentShield(TransactionType t) =>
            t == TransactionType.VFX_SHIELD || t == TransactionType.VBTC_V2_SHIELD;

        /// <summary>Z→T or Z→Z: PLONK sentinel signature, fee 0, nonce 0.</summary>
        public static bool IsZkAuthorizedPrivate(TransactionType t) =>
            t == TransactionType.VFX_UNSHIELD
            || t == TransactionType.VFX_PRIVATE_TRANSFER
            || t == TransactionType.VBTC_V2_UNSHIELD
            || t == TransactionType.VBTC_V2_PRIVATE_TRANSFER;

        /// <summary>
        /// Canonical transaction order within a block (matches block validation:
        /// OrderBy FromAddress, ThenBy Nonce, ThenBy Hash).
        /// </summary>
        public static IReadOnlyList<Transaction> OrderTransactionsForReplay(IEnumerable<Transaction> transactions) =>
            transactions
                .OrderBy(t => t.FromAddress, StringComparer.Ordinal)
                .ThenBy(t => t.Nonce)
                .ThenBy(t => t.Hash, StringComparer.Ordinal)
                .ToList();

        /// <summary>
        /// Preserves order; includes non-private txs; skips private txs once <paramref name="maxPrivate"/> is reached (default <see cref="Globals.MaxPrivateTxPerBlock"/>).
        /// </summary>
        public static List<Transaction> TakeWhilePrivateTxCap(IEnumerable<Transaction> transactions, int maxPrivate = -1)
        {
            if (maxPrivate < 0)
                maxPrivate = Globals.MaxPrivateTxPerBlock;

            var list = new List<Transaction>();
            int priv = 0;
            foreach (var t in transactions)
            {
                if (IsPrivateTransaction(t.TransactionType))
                {
                    if (priv >= maxPrivate)
                        continue;
                    priv++;
                }
                list.Add(t);
            }
            return list;
        }
    }
}
