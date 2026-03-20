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
    }
}
