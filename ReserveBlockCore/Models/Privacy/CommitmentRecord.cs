using LiteDB;

namespace ReserveBlockCore.Models.Privacy
{
    public class CommitmentRecord
    {
        public ObjectId Id { get; set; } = ObjectId.NewObjectId();
        public string Commitment { get; set; } = "";

        /// <summary>
        /// Base64-encoded Poseidon note hash (32 bytes): <c>Poseidon(amount_scaled, randomness)</c>.
        /// Used as the Merkle leaf and for in-circuit amount-commitment binding.
        /// </summary>
        public string NoteHash { get; set; } = "";

        public string AssetType { get; set; } = "";
        public long TreePosition { get; set; }
        public long BlockHeight { get; set; }
        public long Timestamp { get; set; }
        public bool IsSpent { get; set; }
    }
}
