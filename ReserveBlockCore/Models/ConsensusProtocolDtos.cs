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
        /// <summary>DETERMINISTIC-CONSENSUS: Addresses this voter excluded (failed liveness) so peers can apply the same exclusions.</summary>
        public List<string> ExcludedAddresses { get; set; } = new();
    }

    /// <summary>DETERMINISTIC-CONSENSUS: Exchange validator lists between casters to ensure identical NetworkValidators sets.</summary>
    public class ValidatorListExchangeRequest
    {
        public long BlockHeight { get; set; }
        public string CasterAddress { get; set; } = "";
        public List<string> ValidatorAddresses { get; set; } = new();
    }

    /// <summary>DETERMINISTIC-CONSENSUS: Response to validator list exchange with the responder's validator list.</summary>
    public class ValidatorListExchangeResponse
    {
        public long BlockHeight { get; set; }
        public string CasterAddress { get; set; } = "";
        public List<string> ValidatorAddresses { get; set; } = new();
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

    /// <summary>FIX 5: Promotion proposal sent to peer casters for agreement before promoting a candidate.</summary>
    public class PromotionProposalRequest
    {
        public string CandidateAddress { get; set; } = "";
        public string CandidateIP { get; set; } = "";
        public long BlockHeight { get; set; }
        public string ProposerAddress { get; set; } = "";
    }

    /// <summary>FIX 5: Response to a promotion proposal — accept or reject with reason.</summary>
    public class PromotionProposalResponse
    {
        public bool Accepted { get; set; }
        public string Reason { get; set; } = "";
        public string ResponderAddress { get; set; } = "";
    }

    /// <summary>Graceful caster departure; MUST be signed by the departing caster.</summary>
    public class CasterDepartureNotice
    {
        public string DepartingAddress { get; set; } = "";
        public long BlockHeight { get; set; }
        /// <summary>Signature of DEPART|{DepartingAddress}|{BlockHeight}.</summary>
        public string DepartureSignature { get; set; } = "";
    }

    /// <summary>
    /// Broadcast when a caster is demoted (e.g., outdated version or persistent unreachability).
    /// Signed by the demoter (an existing caster) so all peers can verify and remove the demoted node
    /// simultaneously, preventing caster list inconsistency across the network.
    /// </summary>
    public class CasterDemotionNotice
    {
        public string DemotedAddress { get; set; } = "";
        public long BlockHeight { get; set; }
        public string DemoterAddress { get; set; } = "";
        /// <summary>Signature of DEMOTE|{DemotedAddress}|{BlockHeight}|{DemoterAddress}.</summary>
        public string DemotionSignature { get; set; } = "";
    }

}
