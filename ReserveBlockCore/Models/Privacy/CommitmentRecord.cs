using LiteDB;

namespace ReserveBlockCore.Models.Privacy
{
    public class CommitmentRecord
    {
        public ObjectId Id { get; set; } = ObjectId.NewObjectId();
        public string Commitment { get; set; } = "";
        public string AssetType { get; set; } = "";
        public long TreePosition { get; set; }
        public long BlockHeight { get; set; }
        public long Timestamp { get; set; }
        public bool IsSpent { get; set; }
    }
}
