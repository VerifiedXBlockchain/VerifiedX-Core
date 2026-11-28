using LiteDB;

namespace VerifiedXCore.Models
{
    /// <summary>
    /// One holder's balance on a contract's vBTC ledger, from Globals.VaultLedgerHeight
    /// (Bitcoin.Attestation.VaultLedgerStore). Kept in the contract state's database file.
    /// </summary>
    public class VaultLedgerEntry
    {
        /// <summary>VaultLedgerStore.EntryId: the SHA-256 of the contract's stored UID and the address.</summary>
        [BsonId] public string Id { get; set; } = "";
        /// <summary>The contract's UID as state stores it.</summary>
        public string ContractUid { get; set; } = "";
        public string Address { get; set; } = "";
        /// <summary>The sum of every ledger row that has named the address on this contract; may be below zero (a former owner).</summary>
        public decimal Balance { get; set; }
        /// <summary>Block height of the last write, for the snapshot's diff copy.</summary>
        public long LastModifiedHeight { get; set; }
    }
}
