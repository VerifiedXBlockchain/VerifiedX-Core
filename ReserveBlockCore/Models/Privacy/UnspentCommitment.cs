namespace ReserveBlockCore.Models.Privacy
{
    public class UnspentCommitment
    {
        public string Commitment { get; set; } = "";
        public string AssetType { get; set; } = "";
        public decimal Amount { get; set; }
        public byte[] Randomness { get; set; } = Array.Empty<byte>();
        public long TreePosition { get; set; }
        public long BlockHeight { get; set; }
        public bool IsSpent { get; set; }
    }
}
