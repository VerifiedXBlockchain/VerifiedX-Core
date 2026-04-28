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

    /// <summary>
    /// CONSENSUS-V2 (Fix #4): Full per-validator entry exchanged between casters during the
    /// periodic validator-list sync. Carries enough information for the receiver to materialize
    /// a fully-formed <see cref="NetworkValidator"/> entry without waiting for the much slower
    /// P2P gossip path. Receivers MUST still gate each entry through
    /// <see cref="NetworkValidator.CheckValidatorLiveness"/> before merging.
    /// </summary>
    public class ValidatorListEntry
    {
        public string Address { get; set; } = "";
        public string IPAddress { get; set; } = "";
        public string PublicKey { get; set; } = "";
        public long FirstSeenAtHeight { get; set; }
        public long LastSeen { get; set; }
    }

    /// <summary>
    /// CONSENSUS-V2 (Fix #4): Exchange validator lists between casters with full entries (Address +
    /// IP + PublicKey + FirstSeenAtHeight + LastSeen) so the receiver can actually merge missing
    /// validators into its own NetworkValidators dictionary.
    /// BREAKING: replaces the prior <c>List&lt;string&gt; ValidatorAddresses</c> shape — testnet only.
    /// </summary>
    public class ValidatorListExchangeRequest
    {
        public long BlockHeight { get; set; }
        public string CasterAddress { get; set; } = "";
        public List<ValidatorListEntry> Validators { get; set; } = new();
    }

    /// <summary>
    /// CONSENSUS-V2 (Fix #4): Response to validator list exchange. Carries the responder's full
    /// validator entries so the requesting caster can fill gaps in a single round-trip.
    /// </summary>
    public class ValidatorListExchangeResponse
    {
        public long BlockHeight { get; set; }
        public string CasterAddress { get; set; } = "";
        public List<ValidatorListEntry> Validators { get; set; } = new();
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

    /// <summary>
    /// CONSENSUS-V2 (Fix #3): Sent by a promoter to all peer casters AFTER it has successfully promoted
    /// a candidate (added it to its local BlockCasters bag). Peers verify the signature, run the same
    /// port + version gate, and merge the new caster into their own BlockCasters via the atomic helper.
    /// This eliminates the propagation gap where Caster A's pool grows but Caster B doesn't notice
    /// until the next block sync, which segments the consensus pool for several seconds.
    /// </summary>
    public class CasterPromotionAnnouncement
    {
        public string PromotedAddress { get; set; } = "";
        public string PromotedIP { get; set; } = "";
        public string PromotedPublicKey { get; set; } = "";
        public string PromotedWalletVersion { get; set; } = "";
        public long BlockHeight { get; set; }
        public string PromoterAddress { get; set; } = "";
        /// <summary>Signature of PROMOTE-ANNOUNCE|{PromotedAddress}|{PromotedIP}|{BlockHeight}|{PromoterAddress}.</summary>
        public string PromoterSignature { get; set; } = "";
    }

    /// <summary>
    /// CONSENSUS-V2 (Fix #5): Compact commitment to the local caster's view of the proof
    /// set at a specific block height. We exchange only the sorted address list and a hash
    /// over it — never the proof bodies themselves — so casters can converge on a common
    /// VRF input set in one tiny round-trip per peer. Receivers group commitments by
    /// <see cref="CommitmentHash"/>; if the largest group has supermajority size, every
    /// caster in that group is guaranteed to sort the SAME address set deterministically
    /// (regardless of how it locally received those proofs), eliminating a major class of
    /// proof-set divergence at scale.
    /// </summary>
    public class ProofSetCommitment
    {
        public long BlockHeight { get; set; }
        public string CasterAddress { get; set; } = "";
        /// <summary>Distinct proof addresses for this height, ordered by ordinal ascending.</summary>
        public List<string> ProofAddressesSorted { get; set; } = new();
        /// <summary>SHA256 hex of <c>"|".Join(ProofAddressesSorted)</c>. Lower-case, no separators.</summary>
        public string CommitmentHash { get; set; } = "";
    }

    /// <summary>
    /// CONSENSUS-V2 (Fix #5): Response to a <see cref="ProofSetCommitment"/> POST. Carries
    /// every commitment the responder currently holds for the requested height (keyed by
    /// caster address). The caller merges these into its local store and re-tallies.
    /// </summary>
    public class ProofSetExchangeResponse
    {
        public long BlockHeight { get; set; }
        public Dictionary<string, ProofSetCommitment> Commitments { get; set; } = new();
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
