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

    /// <summary>
    /// Sent to a validator when it has been promoted to caster status.
    /// MUST be signed by the promoter — verified on receipt. See CasterDiscoveryService.GetCanonicalCasterListJson for hash input.
    /// </summary>
    public class CasterPromotionRequest
    {
        public string PromotedAddress { get; set; } = "";
        public long BlockHeight { get; set; }
        public List<CasterInfo> CasterList { get; set; } = new();
        public string PromoterAddress { get; set; } = "";
        /// <summary>Signature of PROMOTE|{PromotedAddress}|{BlockHeight}|{PromoterAddress}|{CasterListHashHex}.</summary>
        public string PromoterSignature { get; set; } = "";
    }

    /// <summary>Graceful caster departure; MUST be signed by the departing caster.</summary>
    public class CasterDepartureNotice
    {
        public string DepartingAddress { get; set; } = "";
        public long BlockHeight { get; set; }
        /// <summary>Signature of DEPART|{DepartingAddress}|{BlockHeight}.</summary>
        public string DepartureSignature { get; set; } = "";
    }
}
