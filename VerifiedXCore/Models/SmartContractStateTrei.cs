using VerifiedXCore.Extensions;
using VerifiedXCore.Data;
using VerifiedXCore.Models.SmartContracts;

namespace VerifiedXCore.Models
{
    /// <summary>
    /// A contract's state without its ledger (<see cref="SmartContractStateTrei.GetSmartContractHeader"/>). Read-only: no
    /// write takes it.
    /// </summary>
    public sealed record SmartContractHeader(long Id, string SmartContractUID, string? ContractData, string? MinterAddress, string? OwnerAddress, string? NextOwner, bool IsLocked)
    {
        /// <summary>A throwaway state carrying only the UID and the code, for the checks that read just those two.</summary>
        internal SmartContractStateTrei CodeOnly() => new() { Id = Id, SmartContractUID = SmartContractUID, ContractData = ContractData };
    }

    public class SmartContractStateTrei
    {
        public long Id { get; set; }
        public string SmartContractUID { get; set; }
        public string ContractData { get; set; }
        public string MinterAddress { get; set; }
        public string OwnerAddress { get; set; }
        public string? NextOwner { get; set; }
        public bool IsLocked { get; set; }
        public string? Locators { get; set; }
        public long Nonce { get; set; }
        public bool KeyRevealed { get; set; }
        public bool? IsToken { get; set; }
        public string? MD5List { get; set; }
        public bool? MinterManaged { get; set; }
        public decimal? PurchaseAmount { get; set; } //Royalty is included in this.
        public List<string>? PurchaseKeys { get; set; }
        /// <summary>
        /// The purchase key of the sale in progress, recorded by Sale_Start from Globals.SaleListingKeyHeight and
        /// cleared when the sale completes or is cancelled. Null outside a sale and for a sale started below the height.
        /// </summary>
        public string? SaleKey { get; set; }
        /// <summary>
        /// Buyers of sales that had no recorded key (started below Globals.SaleListingKeyHeight) and were cancelled at
        /// or above it. The contract is not listed for sale to them again. Null when there are none.
        /// </summary>
        public List<string>? CancelledSaleBuyers { get; set; }
        public TokenDetails? TokenDetails { get; set; }
        public List<SmartContractStateTreiTokenizationTX>? SCStateTreiTokenizationTXes { get; set; }

        /// <summary>
        /// True once this contract's ledger is kept as entries of its own (from Globals.VaultLedgerHeight;
        /// Bitcoin.Attestation.VaultLedgerStore): its rows are then no longer in this document.
        /// </summary>
        public bool? LedgerMoved { get; set; }
        /// <summary>Moved ledger: how many holders hold a balance (not zero).</summary>
        public int? LedgerHolders { get; set; }
        /// <summary>Moved ledger: the sum of the holders' balances below zero.</summary>
        public decimal? LedgerNegative { get; set; }
        /// <summary>
        /// Moved ledger: rows added in this apply and not yet saved. Written into the entries with the contract, in one
        /// write (VaultLedgerStore.InOneWrite); never stored in the document.
        /// </summary>
        [LiteDB.BsonIgnore]
        public List<SmartContractStateTreiTokenizationTX>? PendingLedgerRows { get; set; }

        /// <summary>
        /// Block height at which this record was last inserted/updated. Stamped automatically by
        /// the typed overloads in <see cref="Extensions.StateTreiStampExtensions"/> and used by
        /// StateSnapshotService to diff-copy only changed records into snapshot slots.
        /// Legacy records without the field deserialize as 0 (always older than any snapshot).
        /// </summary>
        public long LastModifiedHeight { get; set; }

        public static LiteDB.ILiteCollection<SmartContractStateTrei> GetSCST()
        {
            var scs = DbContext.DB_SmartContractStateTrei.GetCollection<SmartContractStateTrei>(DbContext.RSRV_SCSTATE_TREI);
            return scs;
        }

        public static SmartContractStateTrei? GetSmartContractState(string smartContractUID)
        {
            var scs = GetSCST();
            if (scs != null)
            {
                // The oldest document of a UID, as the scan before the UID index read it: through a non-unique index the
                // first match depends on the index's random levels, which differ between nodes (fourth review, round 8).
                var sc = scs.Find(x => x.SmartContractUID == smartContractUID).OrderBy(x => x.Id).FirstOrDefault();
                if (sc != null)
                {
                    return sc;
                }
            }

            return null;
        }

        /// <summary>
        /// The contract's header: who holds it, whether it is changing hands, its code; not its ledger. A vault's ledger
        /// is a list inside its state document, one row a holder, and <see cref="GetSmartContractState"/> builds every row
        /// whatever the caller reads, so a rule that needed only the owner or the stored spelling of the UID cost as much as
        /// the vault had holders. The same document is read (the oldest for the UID); only the fields below are built. The
        /// header cannot be written back, so it cannot stand in for the state and wipe a ledger.
        /// </summary>
        public static SmartContractHeader? GetSmartContractHeader(string smartContractUID)
        {
            var scs = GetSCST();
            if (scs == null || smartContractUID == null) return null;
            var h = scs.Query().Where(x => x.SmartContractUID == smartContractUID).OrderBy(x => x.Id)
                .Select(x => new { x.Id, x.SmartContractUID, x.ContractData, x.MinterAddress, x.OwnerAddress, x.NextOwner, x.IsLocked })
                .FirstOrDefault();
            return h == null ? null : new SmartContractHeader(h.Id, h.SmartContractUID, h.ContractData, h.MinterAddress, h.OwnerAddress, h.NextOwner, h.IsLocked);
        }

        public static IEnumerable<SmartContractStateTrei>? GetSmartContractsOwnedByAddress(string address)
        {
            var scs = GetSCST();
            if (scs != null)
            {
                var scList = scs.Query().Where(x => x.OwnerAddress == address).ToEnumerable();
                if (scList.Count() > 0)
                {
                    return scList;
                }
            }

            return null;
        }

        public static IEnumerable<SmartContractStateTrei>? GetvBTCSmartContracts(string address)
        {
            var scs = GetSCST();
            if (scs == null)
                return null;

            // First, get contracts where user is direct owner - this part works fine with LiteDB
            var ownerContracts = scs.Query()
                .Where(contract => contract.OwnerAddress == address)
                .ToEnumerable();

            // Then, load all contracts and filter in memory for transaction matches
            // This is less efficient but works around LiteDB limitations
            var allContracts = scs.Query().ToEnumerable();
            var txContracts = allContracts.Where(contract => VerifiedXCore.Bitcoin.Attestation.VaultLedgerStore.Names(contract, address));

            // Combine and return results
            var result = ownerContracts.Union(txContracts).Distinct();
            if (result.Any())
                return result;

            return null;
        }

        public static void SaveSmartContract(SmartContractStateTrei scMain)
        {

            var scs = GetSCST();

            var exist = scs.FindOne(x => x.SmartContractUID == scMain.SmartContractUID);

            if(exist == null)
            {
                var savesLedger = scMain.PendingLedgerRows is { Count: > 0 };
                VerifiedXCore.Bitcoin.Attestation.VaultLedgerStore.InOneWrite(scMain, () =>
                {
                    var inserted = scs.InsertSafe(scMain);
                    if (savesLedger && (inserted == null || inserted.IsNull))
                        throw new InvalidOperationException($"Contract {scMain.SmartContractUID} was not written with its ledger.");
                });
            }
        }
        public static void UpdateSmartContract(SmartContractStateTrei scMain)
        {
            var scs = GetSCST();
            if (scMain == null)
            {
                scs.UpdateSafe(scMain);
                return;
            }

            // A moved ledger's unsaved rows are written with the contract, in one write; a write that fails is thrown.
            var savesLedger = scMain.PendingLedgerRows is { Count: > 0 };
            VerifiedXCore.Bitcoin.Attestation.VaultLedgerStore.InOneWrite(scMain, () =>
            {
                // A vault the chain watches: its ledger is kept within what one document holds, and a write that fails is
                // thrown, never dropped; a dropped ledger write left a withdrawal paid with no debit (fourth review, round 5).
                if (savesLedger || VerifiedXCore.Bitcoin.Attestation.BtcAttestBalance.IsAttestedVault(scMain.SmartContractUID))
                {
                    VerifiedXCore.Bitcoin.Attestation.BtcVaultLedger.CompactWatched(scMain);
                    if (!scs.UpdateOrThrow(scMain))
                        throw new InvalidOperationException($"Vault {scMain.SmartContractUID}'s state is not in the state trie; it was not written.");
                    return;
                }

                scs.UpdateSafe(scMain);
            });
        }

        public static void DeleteSmartContract(SmartContractStateTrei scMain)
        {
            var scs = GetSCST();

            scs.DeleteManySafe(x => x.SmartContractUID == scMain.SmartContractUID);
            if (scMain.LedgerMoved == true)
                VerifiedXCore.Bitcoin.Attestation.VaultLedgerStore.ClearEntriesOf(scMain.SmartContractUID);

            // Snapshot diff queries can't see deletions — record a tombstone so
            // StateSnapshotService propagates the delete into snapshot slots.
            StateTombstone.Record(StateTombstone.COLL_SCSTATE, scMain.SmartContractUID);
        }
    }
}
