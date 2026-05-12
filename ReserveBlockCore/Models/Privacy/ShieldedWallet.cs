using LiteDB;

namespace ReserveBlockCore.Models.Privacy
{
    public class ShieldedWallet
    {
        public ObjectId Id { get; set; } = ObjectId.NewObjectId();
        /// <summary>Optional link to a transparent VFX account that owns this shielded row (wallet UX).</summary>
        public string? TransparentSourceAddress { get; set; }
        public string ShieldedAddress { get; set; } = "";
        public Dictionary<string, decimal> ShieldedBalances { get; set; } = new();
        public List<UnspentCommitment> UnspentCommitments { get; set; } = new();
        public byte[]? SpendingKey { get; set; }
        public byte[] ViewingKey { get; set; } = Array.Empty<byte>();
        public bool IsViewOnly { get; set; }
        public long LastScannedBlock { get; set; }
    }
}
