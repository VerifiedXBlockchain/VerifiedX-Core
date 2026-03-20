using LiteDB;

namespace ReserveBlockCore.Models.Privacy
{
    /// <summary>
    /// Sparse persistence for Merkle tree nodes (level + index at that level).
    /// </summary>
    public class MerkleTreeNodeRecord
    {
        public ObjectId Id { get; set; } = ObjectId.NewObjectId();
        public string AssetType { get; set; } = "";
        public int Level { get; set; }
        public long Index { get; set; }
        /// <summary>Base64-encoded 32-byte Poseidon node hash.</summary>
        public string NodeHash { get; set; } = "";
    }
}
