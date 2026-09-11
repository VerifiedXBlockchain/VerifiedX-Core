namespace VerifiedXCore.Bitcoin.FROST.Models
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
        public string? CeremonyId { get; set; }  // MPC ceremony ID — key packages are stored under this ID
        public string LeaderAddress { get; set; }
        public long Timestamp { get; set; }
        public string LeaderSignature { get; set; }
        public List<string> SignerAddresses { get; set; }
        public int RequiredThreshold { get; set; }
        /// <summary>
        /// FIND-028: VFX withdrawal request TX hash. When set, validators enforce one-time signing
        /// per withdrawal to prevent double-spend attacks. Null for non-withdrawal signings (e.g. bridge exits).
        /// </summary>
        public string? WithdrawalRequestHash { get; set; }

        /// <summary>
        /// Which transaction input this ceremony signs (multi-input withdrawals run one ceremony per
        /// input). Old coordinators omit this — defaults to 0 (legacy single-input behavior).
        /// </summary>
        public int InputIndex { get; set; }

        /// <summary>Total input count of the transaction being signed. 0/absent = legacy (treated as 1).</summary>
        public int InputCount { get; set; }

        /// <summary>
        /// Ordered BIP341 sighash per input for the WHOLE transaction. Validators pin this set on the
        /// first sign/start for a withdrawal; later inputs must match it, so a fake "input k" carrying
        /// a different transaction's sighash is refused.
        /// </summary>
        public List<string>? AllInputSighashes { get; set; }

        /// <summary>
        /// "txid:vout" of every input the transaction spends. Drives the validator-side per-contract
        /// conflict rule: while a signed withdrawal tx is outstanding for a contract, a DIFFERENT
        /// withdrawal's tx must spend at least one of its inputs (so at most one can confirm).
        /// </summary>
        public List<string>? TxInputOutpoints { get; set; }

        /// <summary>
        /// Txid of the unsigned transaction. For Taproot key-path spends the txid does not change
        /// when the witness is added, so this is also the final broadcast txid — validators use it to
        /// detect on-chain confirmation and release the contract-level pin.
        /// </summary>
        public string? BtcTxId { get; set; }

        /// <summary>
        /// Full unsigned transaction (hex). REQUIRED: validators rebuild every input's BIP341 sighash
        /// from this plus <see cref="Prevouts"/> and refuse to sign a MessageHash they cannot reproduce,
        /// or a transaction whose inputs/outputs are not an authorized spend of the contract vault.
        /// </summary>
        public string? UnsignedTxHex { get; set; }

        /// <summary>Prevout (value + scriptPubKey) of every input, in input order. REQUIRED.</summary>
        public List<VerifiedXCore.Bitcoin.Models.PinnedWithdrawalCoin>? Prevouts { get; set; }
    }

    /// <summary>
    /// Signing Abort Request — coordinator tells validators a ceremony died so they can drop the
    /// session and (if no share was generated) mark the tracker Failed for a fast retry.
    /// Authenticated by REPLAYING the session's original start signature over
    /// "{SessionId}.{LeaderAddress}.{Timestamp}" — works for the web-wallet flow where no fresh
    /// signature can be minted.
    /// </summary>
    public class FrostSigningAbortRequest
    {
        public string SessionId { get; set; } = "";
        public string LeaderAddress { get; set; } = "";
        /// <summary>The timestamp from the ORIGINAL start message.</summary>
        public long Timestamp { get; set; }
        /// <summary>The ORIGINAL start signature (replayed).</summary>
        public string LeaderSignature { get; set; } = "";
    }

    /// <summary>
    /// Per-transaction context threaded from the transaction builder through the signing
    /// coordinator into each input's sign/start request.
    /// </summary>
    public class FrostSigningTxContext
    {
        public int InputCount { get; set; }
        public List<string> AllInputSighashes { get; set; } = new();
        public List<string> TxInputOutpoints { get; set; } = new();
        public string BtcTxId { get; set; } = "";
        /// <summary>Unsigned transaction hex — validators rebuild sighashes from it.</summary>
        public string UnsignedTxHex { get; set; } = "";
        /// <summary>Prevouts in input order — validators rebuild sighashes from them.</summary>
        public List<VerifiedXCore.Bitcoin.Models.PinnedWithdrawalCoin> Prevouts { get; set; } = new();
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

    /// <summary>
    /// Pre-signed leader authentication for web wallet / external signer flows.
    /// When provided to FROST ceremony methods, the pre-signed signatures are used
    /// instead of calling AddressSignature() which requires a local private key.
    /// </summary>
    public class PreSignedLeaderAuth
    {
        /// <summary>
        /// The session ID the web wallet signed over. When set, CoordinateDKGCeremony /
        /// CoordinateSigningCeremony reuse this instead of generating a new Guid,
        /// ensuring the leader message matches the pre-signed signature.
        /// </summary>
        public string? SessionId { get; set; }

        /// <summary>
        /// Pre-signed signature for the DKG/signing start broadcast.
        /// Message format: "{sessionId}.{leaderAddress}.{timestamp}"
        /// </summary>
        public string StartSignature { get; set; } = "";

        /// <summary>
        /// Timestamp used when generating the start signature.
        /// Must match the timestamp embedded in StartSignature.
        /// </summary>
        public long StartTimestamp { get; set; }

        /// <summary>
        /// Pre-signed signature for DKG share distribution (Round 2).
        /// Only needed for DKG ceremonies. Message format: "{sessionId}.{leaderAddress}.{timestamp}"
        /// </summary>
        public string? ShareDistributionSignature { get; set; }

        /// <summary>
        /// Timestamp used when generating the share distribution signature.
        /// Only needed for DKG ceremonies.
        /// </summary>
        public long? ShareDistributionTimestamp { get; set; }

        /// <summary>
        /// Per-input pre-signed start auths for MULTI-INPUT withdrawals. Each transaction input runs
        /// its own FROST ceremony with its own session id (input 0 = the base SessionId, input k =
        /// "{base}:i{k}"), and validators verify each start against "{sessionId}.{leader}.{timestamp}"
        /// — so the web wallet must sign one start message per input at Prepare time. Null/empty for
        /// single-input withdrawals (fully backward compatible: input 0 uses the top-level fields).
        /// </summary>
        public List<PreSignedInputAuth>? InputAuths { get; set; }

        /// <summary>
        /// Resolves the auth to use for a given input index: input 0 (or anything without a
        /// per-input entry list) uses the top-level session/signature; higher inputs require a
        /// matching InputAuths entry and return null when it is missing — callers must treat a null
        /// as "the prepared auth does not cover this input" and fail with a re-Prepare instruction
        /// rather than reusing input 0's session (which validators reject as a session collision).
        /// </summary>
        public PreSignedLeaderAuth? ForInput(int inputIndex)
        {
            if (inputIndex == 0)
                return this;

            var entry = InputAuths?.FirstOrDefault(a => a.InputIndex == inputIndex);
            if (entry == null)
                return null;

            return new PreSignedLeaderAuth
            {
                SessionId = entry.SessionId,
                StartSignature = entry.Signature,
                StartTimestamp = entry.Timestamp,
                ShareDistributionSignature = ShareDistributionSignature,
                ShareDistributionTimestamp = ShareDistributionTimestamp
            };
        }
    }

    /// <summary>
    /// One input's pre-signed ceremony-start auth (multi-input withdrawals).
    /// </summary>
    public class PreSignedInputAuth
    {
        public int InputIndex { get; set; }
        public string SessionId { get; set; } = "";
        /// <summary>Signature over "{SessionId}.{leaderAddress}.{Timestamp}".</summary>
        public string Signature { get; set; } = "";
        public long Timestamp { get; set; }
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
