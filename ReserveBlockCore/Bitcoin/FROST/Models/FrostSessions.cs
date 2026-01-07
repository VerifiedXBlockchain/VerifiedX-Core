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
        
        // Round 1: Commitments
        public ConcurrentDictionary<string, string> Round1Commitments { get; set; } = new();
        
        // Round 2: Shares (per validator, from each other validator)
        public ConcurrentDictionary<string, List<FrostDKGShareMessage>> ReceivedShares { get; set; } = new();
        
        // Round 3: Verifications
        public ConcurrentDictionary<string, bool> Round3Verifications { get; set; } = new();
        
        // Final result
        public string? GroupPublicKey { get; set; }
        public string? TaprootAddress { get; set; }
        public string? DKGProof { get; set; }
        public bool IsCompleted { get; set; }
        
        public DKGSession()
        {
            ParticipantAddresses = new List<string>();
            Round1Commitments = new ConcurrentDictionary<string, string>();
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
        
        // Round 1: Nonce commitments
        public ConcurrentDictionary<string, string> Round1Nonces { get; set; } = new();
        
        // Round 2: Signature shares
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
