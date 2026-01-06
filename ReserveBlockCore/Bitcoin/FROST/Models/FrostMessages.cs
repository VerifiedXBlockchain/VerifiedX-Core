namespace ReserveBlockCore.Bitcoin.FROST.Models
{
    #region DKG Messages

    /// <summary>
    /// DKG Start Request - Leader broadcasts to all validators
    /// </summary>
    public class FrostDKGStartRequest
    {
        public string SessionId { get; set; }
        public string SmartContractUID { get; set; }
        public string LeaderAddress { get; set; }
        public long Timestamp { get; set; }
        public string LeaderSignature { get; set; }
        public List<string> ParticipantAddresses { get; set; }
        public int RequiredThreshold { get; set; }
    }

    /// <summary>
    /// DKG Round 1 - Commitment Phase
    /// Each validator sends their polynomial commitments
    /// </summary>
    public class FrostDKGRound1Message
    {
        public string SessionId { get; set; }
        public string ValidatorAddress { get; set; }
        public string CommitmentData { get; set; }  // Base64 encoded commitment
        public long Timestamp { get; set; }
        public string ValidatorSignature { get; set; }
    }

    /// <summary>
    /// DKG Round 2 - Share Distribution
    /// Validators send encrypted shares to each other (point-to-point)
    /// </summary>
    public class FrostDKGShareMessage
    {
        public string SessionId { get; set; }
        public string FromValidatorAddress { get; set; }
        public string ToValidatorAddress { get; set; }
        public string EncryptedShare { get; set; }  // Encrypted with recipient's public key
        public long Timestamp { get; set; }
        public string ValidatorSignature { get; set; }
    }

    /// <summary>
    /// DKG Round 3 - Verification Phase
    /// Validators report whether they successfully verified all shares
    /// </summary>
    public class FrostDKGRound3Message
    {
        public string SessionId { get; set; }
        public string ValidatorAddress { get; set; }
        public bool Verified { get; set; }  // true if all shares verified successfully
        public long Timestamp { get; set; }
        public string ValidatorSignature { get; set; }
    }

    /// <summary>
    /// DKG Result - Final output from successful DKG ceremony
    /// </summary>
    public class FrostDKGResult
    {
        public string SessionId { get; set; }
        public string SmartContractUID { get; set; }
        public string GroupPublicKey { get; set; }  // Aggregated FROST group public key
        public string TaprootAddress { get; set; }  // bc1p... Taproot address
        public string DKGProof { get; set; }  // Base64 encoded DKG completion proof
        public long CompletionTimestamp { get; set; }
        public List<string> ParticipantAddresses { get; set; }
        public int Threshold { get; set; }
    }

    #endregion

    #region Signing Messages

    /// <summary>
    /// Signing Start Request - Leader broadcasts to all validators
    /// </summary>
    public class FrostSigningStartRequest
    {
        public string SessionId { get; set; }
        public string MessageHash { get; set; }  // Bitcoin transaction sighash (BIP 341)
        public string SmartContractUID { get; set; }
        public string LeaderAddress { get; set; }
        public long Timestamp { get; set; }
        public string LeaderSignature { get; set; }
        public List<string> SignerAddresses { get; set; }
        public int RequiredThreshold { get; set; }
    }

    /// <summary>
    /// Signing Round 1 - Nonce Commitment Phase
    /// Each signer sends their nonce commitment
    /// </summary>
    public class FrostSigningRound1Message
    {
        public string SessionId { get; set; }
        public string ValidatorAddress { get; set; }
        public string NonceCommitment { get; set; }  // Base64 encoded nonce commitment
        public long Timestamp { get; set; }
        public string ValidatorSignature { get; set; }
    }

    /// <summary>
    /// Signing Round 2 - Signature Share Generation
    /// Each signer sends their partial Schnorr signature
    /// </summary>
    public class FrostSigningRound2Message
    {
        public string SessionId { get; set; }
        public string ValidatorAddress { get; set; }
        public string SignatureShare { get; set; }  // Base64 encoded partial signature
        public long Timestamp { get; set; }
        public string ValidatorSignature { get; set; }
    }

    /// <summary>
    /// Signing Result - Final aggregated Schnorr signature
    /// </summary>
    public class FrostSigningResult
    {
        public string SessionId { get; set; }
        public string MessageHash { get; set; }
        public string SchnorrSignature { get; set; }  // Final aggregated Schnorr signature (64 bytes hex)
        public bool SignatureValid { get; set; }
        public long CompletionTimestamp { get; set; }
        public List<string> SignerAddresses { get; set; }
        public int Threshold { get; set; }
    }

    #endregion

    #region Session Management

    /// <summary>
    /// FROST Session - Tracks ongoing ceremony state
    /// </summary>
    public class FrostSession
    {
        public string SessionId { get; set; }
        public FrostCeremonyType CeremonyType { get; set; }
        public FrostSessionStatus Status { get; set; }
        public string LeaderAddress { get; set; }
        public List<string> ParticipantAddresses { get; set; }
        public int RequiredThreshold { get; set; }
        public long StartTimestamp { get; set; }
        public long? CompletionTimestamp { get; set; }
        
        // DKG-specific
        public string? SmartContractUID { get; set; }
        public string? GroupPublicKey { get; set; }
        public string? TaprootAddress { get; set; }
        public string? DKGProof { get; set; }
        
        // Signing-specific
        public string? MessageHash { get; set; }
        public string? SchnorrSignature { get; set; }
        
        // Round data storage
        public Dictionary<string, string> Round1Data { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, string> Round2Data { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, string> Round3Data { get; set; } = new Dictionary<string, string>();
    }

    public enum FrostCeremonyType
    {
        DKG,
        Signing
    }

    public enum FrostSessionStatus
    {
        Initializing,
        Round1InProgress,
        Round2InProgress,
        Round3InProgress,
        Completed,
        Failed,
        Timeout
    }

    #endregion
}
