namespace ReserveBlockCore.Models
{
    public class SubmitAttestationRequest
    {
        public long BlockHeight { get; set; }
        public string BlockHash { get; set; } = "";
        public string WinnerAddress { get; set; } = "";
        public string PrevHash { get; set; } = "";
        public string CasterAddress { get; set; } = "";
        public string Signature { get; set; } = "";
    }

    public class RequestBlockRequest
    {
        public long BlockHeight { get; set; }
        public string CasterAddress { get; set; } = "";
        public string WinnerAddress { get; set; } = "";
        public long Timestamp { get; set; }
        public string Signature { get; set; } = "";
    }

    /// <summary>CASTER-CONSENSUS-FIX: Winner vote exchange request for mandatory agreement phase.</summary>
    public class WinnerVoteRequest
    {
        public long BlockHeight { get; set; }
        public string VoterAddress { get; set; } = "";
        public string WinnerAddress { get; set; } = "";
    }

    public class CasterInfo
    {
        public string Address { get; set; } = "";
        public string PeerIP { get; set; } = "";
        public string PublicKey { get; set; } = "";
    }

    public class SignedCasterListResponse
    {
        public int AsOfBlockHeight { get; set; }
        public List<CasterInfo> Casters { get; set; } = new();
        public string SignerAddress { get; set; } = "";
        public string Signature { get; set; } = "";
    }

    /// <summary>Placeholder for signed rotation payloads (Phase 3+).</summary>
    public class CasterRotationBroadcast
    {
        public long EffectiveHeight { get; set; }
        public List<CasterInfo> ProposedCasters { get; set; } = new();
        public List<string> CasterSignatures { get; set; } = new();
    }
}
