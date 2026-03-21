namespace ReserveBlockCore.Models.Privacy
{
    /// <summary>
    /// Wallet-side UTXO-style shielded note (Phase 3 commitment selection). Fields align with privacy plan:
    /// commitment id, asset, amount, randomness, tree position, anchor height, spent flag.
    /// </summary>
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
