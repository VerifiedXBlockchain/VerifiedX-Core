using LiteDB;
using VerifiedXCore.Data;

namespace VerifiedXCore.Bitcoin.Models
{
    /// <summary>
    /// This node's own record of the last vBTCb validator addition its key signed as a contract validator: the epoch and
    /// the contract's admin nonce then. One addition per epoch is what keeps the set's growth to a third at a time; a
    /// second list at the same nonce replaces the first (only one can be sent), but once the nonce has moved an addition
    /// has gone through and this node signs no other until the next epoch. Local key use, not chain state.
    /// </summary>
    public class BaseSignerSignLog
    {
        [BsonId]
        public string Contract { get; set; } = "";
        public long Epoch { get; set; }
        public string AdminNonce { get; set; } = "";

        private const string CollectionName = "rsrv_base_signer_sign_log";

        public static BaseSignerSignLog? Get(string contract) =>
            DbContext.DB_vBTC.GetCollection<BaseSignerSignLog>(CollectionName).FindById(contract.Trim().ToLowerInvariant());

        public static void Save(string contract, long epoch, string adminNonce) =>
            DbContext.DB_vBTC.GetCollection<BaseSignerSignLog>(CollectionName).Upsert(new BaseSignerSignLog
            {
                Contract = contract.Trim().ToLowerInvariant(), Epoch = epoch, AdminNonce = adminNonce,
            });
    }
}
