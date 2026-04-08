namespace ReserveBlockCore.Models
{
    /// <summary>
    /// Caster quorum attestation bundle (embedded on Block; excluded from block hash — CONSENSUS_DECENTRALIZATION_PLAN).
    /// </summary>
    public class ConsensusCertificate
    {
        public long BlockHeight { get; set; }
        public string BlockHash { get; set; } = string.Empty;
        public string WinnerAddress { get; set; } = string.Empty;
        public string PrevHash { get; set; } = string.Empty;
        public List<CasterAttestation> Attestations { get; set; } = new List<CasterAttestation>();
    }

    public class CasterAttestation
    {
        public string CasterAddress { get; set; } = string.Empty;
        public string Signature { get; set; } = string.Empty;
        public long Timestamp { get; set; }
    }
}
