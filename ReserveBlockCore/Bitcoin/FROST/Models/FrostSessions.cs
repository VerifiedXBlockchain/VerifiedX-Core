using System.Collections.Concurrent;

namespace ReserveBlockCore.Bitcoin.FROST.Models
{
    /// <summary>
    /// In-memory storage for DKG ceremony sessions
    /// </summary>
    public class DKGSession
    {
        public string SessionId { get; set; }
        public string SmartContractUID { get; set; }
        public string LeaderAddress { get; set; }
        public List<string> ParticipantAddresses { get; set; }
        public int RequiredThreshold { get; set; }
        public long StartTimestamp { get; set; }
        
        // FROST native library state for this validator's participation
        public ushort MyParticipantIndex { get; set; }         // This validator's 1-based index
        public string? Round1SecretPackage { get; set; }       // Secret from DKGRound1Generate (kept private)
        public string? Round2Secret { get; set; }              // Secret from DKGRound2GenerateShares (kept private)
        public string? GeneratedSharesJson { get; set; }       // Shares this validator created for others
        
        // Round 1: Commitments (from all validators)
        public ConcurrentDictionary<string, string> Round1Commitments { get; set; } = new();
        
        // Round 2: Received shares from other validators (keyed by sender address)
        public ConcurrentDictionary<string, string> ReceivedSharesJson { get; set; } = new();
        
        // Legacy share storage (kept for compatibility)
        public ConcurrentDictionary<string, List<FrostDKGShareMessage>> ReceivedShares { get; set; } = new();
        
        // Round 3: Verifications
        public ConcurrentDictionary<string, bool> Round3Verifications { get; set; } = new();
        
        // Final result from FROST native library
        public string? GroupPublicKey { get; set; }
        public string? TaprootAddress { get; set; }
        public string? DKGProof { get; set; }
        public string? FinalKeyPackage { get; set; }           // This validator's key package (for signing)
        public string? FinalPubkeyPackage { get; set; }        // Group pubkey package (for signature aggregation)
        public bool IsCompleted { get; set; }
        
        public DKGSession()
        {
            ParticipantAddresses = new List<string>();
            Round1Commitments = new ConcurrentDictionary<string, string>();
            ReceivedSharesJson = new ConcurrentDictionary<string, string>();
            ReceivedShares = new ConcurrentDictionary<string, List<FrostDKGShareMessage>>();
            Round3Verifications = new ConcurrentDictionary<string, bool>();
        }
    }

    /// <summary>
    /// In-memory storage for signing ceremony sessions
    /// </summary>
    public class SigningSession
    {
        public string SessionId { get; set; }
        public string MessageHash { get; set; }
        public string SmartContractUID { get; set; }
        public string LeaderAddress { get; set; }
        public List<string> SignerAddresses { get; set; }
        public int RequiredThreshold { get; set; }
        public long StartTimestamp { get; set; }
        
        // FROST native library state for this validator's participation
        public string? MyKeyPackage { get; set; }              // This validator's key package (loaded from persistent store)
        public string? NonceSecret { get; set; }               // Secret nonce from SignRound1Nonces (kept private)
        
        // Round 1: Nonce commitments (from all validators)
        public ConcurrentDictionary<string, string> Round1Nonces { get; set; } = new();
        
        // Round 2: Signature shares (from all validators)
        public ConcurrentDictionary<string, string> Round2Shares { get; set; } = new();
        
        // Final result
        public string? SchnorrSignature { get; set; }
        public bool SignatureValid { get; set; }
        public bool IsCompleted { get; set; }
        
        public SigningSession()
        {
            SignerAddresses = new List<string>();
            Round1Nonces = new ConcurrentDictionary<string, string>();
            Round2Shares = new ConcurrentDictionary<string, string>();
        }
    }

    /// <summary>
    /// Global session storage accessible from FrostStartup
    /// </summary>
    public static class FrostSessionStorage
    {
        /// <summary>Maximum concurrent DKG sessions allowed</summary>
        public const int MAX_DKG_SESSIONS = 50;
        
        /// <summary>Maximum concurrent signing sessions allowed</summary>
        public const int MAX_SIGNING_SESSIONS = 50;
        
        /// <summary>Maximum number of participant addresses per session</summary>
        public const int MAX_PARTICIPANTS = 20;
        
        /// <summary>Minimum required threshold percentage</summary>
        public const int MIN_THRESHOLD = 51;
        
        /// <summary>Maximum required threshold percentage</summary>
        public const int MAX_THRESHOLD = 100;
        
        /// <summary>Maximum session ID length</summary>
        public const int MAX_SESSION_ID_LENGTH = 100;
        
        /// <summary>Maximum commitment/share data length in characters (FIND-014: bound data before FFI)</summary>
        public const int MAX_COMMITMENT_DATA_LENGTH = 4096;
        
        public static ConcurrentDictionary<string, DKGSession> DKGSessions { get; } = new();
        public static ConcurrentDictionary<string, SigningSession> SigningSessions { get; } = new();
        
        /// <summary>
        /// Clean up old sessions (older than 1 hour)
        /// </summary>
        public static void CleanupOldSessions()
        {
            var currentTime = ReserveBlockCore.Utilities.TimeUtil.GetTime();
            var oneHourAgo = currentTime - 3600;
            
            // Cleanup DKG sessions
            var oldDKG = DKGSessions.Where(kvp => kvp.Value.StartTimestamp < oneHourAgo).Select(kvp => kvp.Key).ToList();
            foreach (var key in oldDKG)
            {
                DKGSessions.TryRemove(key, out _);
            }
            
            // Cleanup signing sessions
            var oldSigning = SigningSessions.Where(kvp => kvp.Value.StartTimestamp < oneHourAgo).Select(kvp => kvp.Key).ToList();
            foreach (var key in oldSigning)
            {
                SigningSessions.TryRemove(key, out _);
            }
        }
    }
}
