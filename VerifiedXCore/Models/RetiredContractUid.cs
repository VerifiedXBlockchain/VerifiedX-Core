using LiteDB;
using VerifiedXCore.Data;

namespace VerifiedXCore.Models
{
    /// <summary>
    /// A contract UID whose state record a Burn() deleted, from Globals.PathAuditRulesHeight on. Nothing is created under
    /// it again: a sale bid names only the bare UID, so a UID burned and minted again under other content could complete
    /// a bid made for the first. Chain-derived: wiped with the contract state, rebuilt by replay, copied whole by
    /// StateSnapshotService. Rows are never deleted outside that wipe.
    /// </summary>
    public class RetiredContractUid
    {
        public long Id { get; set; }
        public string SmartContractUID { get; set; } = string.Empty;
        public long BurnedAtHeight { get; set; }
        public string BurnTxHash { get; set; } = string.Empty;

        public const string CollectionName = "rsrv_scstate_retired_uids";

        public static ILiteCollection<RetiredContractUid> GetCollection() =>
            DbContext.DB_SmartContractStateTrei.GetCollection<RetiredContractUid>(CollectionName);

        /// <summary>Matched as SmartContractStateTrei.GetSmartContractState matches a UID. A storage fault throws (fails closed).</summary>
        public static bool IsRetired(string? uid) =>
            !string.IsNullOrEmpty(uid) && GetCollection().Exists(x => x.SmartContractUID == uid);

        /// <summary>Records the retirement. A storage fault throws, so the apply can flag the block (StateData.BurnSmartContract).</summary>
        public static void Retire(string uid, long height, string? txHash)
        {
            var c = GetCollection();
            if (c.Exists(x => x.SmartContractUID == uid)) return;
            c.Insert(new RetiredContractUid { SmartContractUID = uid, BurnedAtHeight = height, BurnTxHash = txHash ?? "" });
        }
    }
}
