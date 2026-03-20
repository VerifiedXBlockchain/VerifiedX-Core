using LiteDB;

namespace ReserveBlockCore.Models.Privacy
{
    public class ShieldedPoolState
    {
        public ObjectId Id { get; set; } = ObjectId.NewObjectId();
        public string AssetType { get; set; } = "";
        public string CurrentMerkleRoot { get; set; } = "";
        public long TotalCommitments { get; set; }
        public decimal TotalShieldedSupply { get; set; }
        public long LastUpdateHeight { get; set; }
    }
}
