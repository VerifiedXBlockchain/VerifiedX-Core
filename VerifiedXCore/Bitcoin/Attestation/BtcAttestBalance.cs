using LiteDB;
using VerifiedXCore.Data;

namespace VerifiedXCore.Bitcoin.Attestation
{
    /// <summary>
    /// What a vault holds on Bitcoin, as far as consensus is concerned (docs/PLAN-vBTC-Bitcoin-Attestation.md, 4.2).
    ///
    /// A vault created from the recording height on has its deposit script in state and its coins recorded by Bitcoin
    /// reports. For such a vault this, and nothing else, is the owner's deposit: at admission, in a block proposal and
    /// in block validation alike, so a block that spends more than the vault holds is invalid for every node. No
    /// Electrum server is asked and nothing is taken on the block producer's word.
    ///
    /// A vault created before the recording height has no deposit script in state. It keeps the earlier rules until
    /// the enforcement height and is frozen from then on.
    /// </summary>
    public static class BtcAttestBalance
    {
        public const decimal SatsPerBtc = 100_000_000M;

        /// <summary>Whether the chain watches this vault's Bitcoin (it was created from the recording height on).</summary>
        public static bool IsAttestedVault(string? scUID, LiteDatabase? db = null)
        {
            if (string.IsNullOrEmpty(scUID)) return false;
            // No store at all (a tool or a test that never opened the databases): nothing is watched. A store that
            // fails to answer is another matter and is not swallowed: answering "not watched" would hand a watched
            // vault to the earlier rules for that one call, which take the block producer's word for its balance.
            if (db == null && DbContext.DB_BtcAttest == null) return false;
            return BtcAttestStore.ScriptRowForVault(scUID, db) != null;
        }

        /// <summary>
        /// The vault's attested Bitcoin, in BTC: its unspent coins, and its coins pinned to a withdrawal that has not
        /// confirmed yet (that withdrawal is already debited on the ledger, so the coin counts until it is spent).
        /// </summary>
        public static decimal DepositBtc(string scUID, LiteDatabase? db = null) =>
            BtcAttestStore.Total(scUID, db).HeldSats / SatsPerBtc;

        /// <summary>
        /// What the vault has paid out for bridge exits, in BTC: part of its owner's ledger figure. A lock for the bridge
        /// debits the locker at once and the Bitcoin stays in the vault; when an exit later pays that Bitcoin out, the
        /// deposit falls and nothing else moves. Without this term the owner's figure would fall for the same vBTC twice.
        /// </summary>
        public static decimal SettledExitBtc(string scUID, LiteDatabase? db = null) =>
            BtcAttestStore.Total(scUID, db).SettledExitSats / SatsPerBtc;

        /// <summary>
        /// Everything added back to the owner's ledger figure for Bitcoin that left the vault and was already charged
        /// to somebody on the ledger: what bridge exits paid out (<see cref="SettledExitBtc"/>), and the Bitcoin fees of
        /// releases, each taken out of its requester's refund.
        /// </summary>
        public static decimal OwnerAddBackBtc(string scUID, LiteDatabase? db = null)
        {
            var total = BtcAttestStore.Total(scUID, db);
            return (total.SettledExitSats + total.ReleaseFeeSats) / SatsPerBtc;
        }
    }
}
