namespace ReserveBlockCore.Models
{
    /// <summary>
    /// One validator in the canonical snapshot used for deterministic proof generation (CONSENSUS_DECENTRALIZATION_PLAN).
    /// </summary>
    public class ValidatorSnapshotEntry
    {
        public string Address { get; set; } = string.Empty;
        /// <summary>Public key string used in VRF seed (prefer on-chain VBTC FrostPublicKey; must match across nodes).</summary>
        public string PublicKey { get; set; } = string.Empty;
        public string IPAddress { get; set; } = string.Empty;
        public decimal Balance { get; set; }
        public long LastHeartbeatHeight { get; set; }
    }
}
