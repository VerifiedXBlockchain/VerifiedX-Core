using LiteDB;

namespace ReserveBlockCore.Models.Privacy
{
    public class NullifierRecord
    {
        public ObjectId Id { get; set; } = ObjectId.NewObjectId();
        public string Nullifier { get; set; } = "";
        public string AssetType { get; set; } = "";
        public long BlockHeight { get; set; }
        public long Timestamp { get; set; }
    }
}
