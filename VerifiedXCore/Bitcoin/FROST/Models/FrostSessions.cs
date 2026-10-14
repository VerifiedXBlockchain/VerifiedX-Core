using System.Collections.Concurrent;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Bitcoin.FROST.Models
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
        /// <summary>The network address the start came from (a leader's node). Counted, so one host cannot take every place.</summary>
        public string? SourceIp { get; set; }
        
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
        /// <summary>
        /// FIND-028: VFX withdrawal request TX hash for dedup tracking. Null for non-withdrawal signings.
        /// </summary>
        public string? WithdrawalRequestHash { get; set; }
        public List<string> SignerAddresses { get; set; }
        public int RequiredThreshold { get; set; }
        public long StartTimestamp { get; set; }

        /// <summary>Timestamp from the leader's start message (the value the leader signed over).</summary>
        public long LeaderStartTimestamp { get; set; }
        /// <summary>Leader's start signature — round messages must replay it to prove leadership.</summary>
        public string? LeaderStartSignature { get; set; }
        /// <summary>
        /// Remote endpoint the start request came from. Round-2 / share / abort calls must come from
        /// the same endpoint: the start signature is visible to every signer, so a peer could
        /// otherwise replay it to consume our single-shot nonce over a bogus commitment set, or abort
        /// the session outright.
        /// </summary>
        public string LeaderRemoteIp { get; set; } = "";

        /// <summary>Which transaction input this session signs (multi-input withdrawals; 0 = legacy).</summary>
        public int InputIndex { get; set; }
        /// <summary>"txid:vout" of every input of the transaction being signed (for the contract-level conflict pin).</summary>
        public List<string>? TxInputOutpoints { get; set; }
        /// <summary>Txid of the (unsigned == final, Taproot) transaction being signed.</summary>
        public string? BtcTxId { get; set; }
        
        // FROST native library state for this validator's participation
        public string? MyKeyPackage { get; set; }              // This validator's key package (loaded from persistent store)
        public string? NonceSecret { get; set; }               // Secret nonce from SignRound1Nonces (kept private)

        private readonly object _nonceGate = new();

        /// <summary>True once the secret nonce has been handed to the signer. A FROST nonce is single-use.</summary>
        public bool NonceConsumed { get; private set; }

        /// <summary>
        /// Hands out the secret nonce EXACTLY ONCE and wipes it. Signing twice with the same nonce
        /// against different commitment sets leaks this validator's long-term key share, so any
        /// second request (replayed leader headers, coordinator retry, malicious peer) must fail.
        /// </summary>
        public bool TryConsumeNonceSecret(out string? nonceSecret)
        {
            lock (_nonceGate)
            {
                if (NonceConsumed || string.IsNullOrEmpty(NonceSecret))
                {
                    nonceSecret = null;
                    return false;
                }
                nonceSecret = NonceSecret;
                NonceSecret = null;
                NonceConsumed = true;
                return true;
            }
        }

        /// <summary>
        /// The leader's posted nonce set must carry THIS validator's own round-1 commitment unchanged.
        /// A leader that substitutes or drops our commitment is trying to make us sign over a
        /// commitment set we never produced.
        /// </summary>
        public bool OwnCommitmentMatches(string myAddress, IDictionary<string, string>? postedNonces)
        {
            if (string.IsNullOrEmpty(myAddress) || postedNonces == null) return false;
            if (!Round1Nonces.TryGetValue(myAddress, out var mine) || string.IsNullOrEmpty(mine)) return false;
            if (!postedNonces.TryGetValue(myAddress, out var posted) || string.IsNullOrEmpty(posted)) return false;
            if (string.Equals(posted, mine, StringComparison.Ordinal)) return true;
            try
            {
                return Newtonsoft.Json.Linq.JToken.DeepEquals(
                    Newtonsoft.Json.Linq.JToken.Parse(posted),
                    Newtonsoft.Json.Linq.JToken.Parse(mine));
            }
            catch { return false; }
        }
        
        /// <summary>
        /// Stored participant order from DKG key store. If populated, this is used instead of
        /// recomputing from SignerAddresses, ensuring signing uses the exact same identifier
        /// mapping that was used during DKG.
        /// </summary>
        public List<string>? StoredParticipantOrder { get; set; }
        
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
        /// <summary>
        /// Key ceremonies that may be under way on this validator at once. Anyone may lead one (the leader holds no
        /// key material), so the places are guarded by what follows rather than by who asks: a start must be recent
        /// and not seen before, a start that never continues gives its place back after two minutes, and one leader
        /// and one network source each hold only a few. 50 places with none of that were filled, for fifteen minutes
        /// at a time, by thirteen made-up leader keys from one machine (third pass, 14 October 2026).
        /// </summary>
        public const int MAX_DKG_SESSIONS = 200;

        /// <summary>A ceremony whose leader has sent nothing after its start for this long no longer holds a place.</summary>
        public const int DKG_UNPROGRESSED_SECONDS = 120;

        /// <summary>Ceremonies under way that one network address may have started here.</summary>
        public const int MAX_DKG_SESSIONS_PER_SOURCE = 8;

        /// <summary>
        /// How far a ceremony start's timestamp may be from this validator's clock, in seconds (a web wallet signs its
        /// start, then asks for the ceremony a while later). A start carries its leader's signature over the session
        /// id and this timestamp and nothing else, so an old one could be sent again by anybody who had seen it (every
        /// participant has): it opened a session under that leader, and since a leader's new start for a vault
        /// replaces its earlier ones, it removed the ceremony that leader was running.
        /// </summary>
        public const int DKG_START_MAX_AGE_SECONDS = 1800;

        /// <summary>Session ids of the starts this validator has accepted, kept twice as long as a start stays fresh.</summary>
        private static readonly ConcurrentDictionary<string, long> SeenDkgStarts = new(StringComparer.Ordinal);

        /// <summary>Records an accepted start. False when this session id was accepted before.</summary>
        public static bool NoteDkgStart(string sessionId, long nowSeconds)
        {
            if (SeenDkgStarts.Count > 4096)
                foreach (var old in SeenDkgStarts.Where(kv => kv.Value < nowSeconds - 2 * DKG_START_MAX_AGE_SECONDS).Select(kv => kv.Key).ToList())
                    SeenDkgStarts.TryRemove(old, out _);
            return SeenDkgStarts.TryAdd(sessionId, nowSeconds);
        }

        public static bool DkgStartWasSeen(string? sessionId) => !string.IsNullOrEmpty(sessionId) && SeenDkgStarts.ContainsKey(sessionId);

        /// <summary>Tests only.</summary>
        internal static void ForgetDkgStarts() => SeenDkgStarts.Clear();

        /// <summary>Whether a ceremony's leader has come back after its start: commitments, shares or verifications are held.</summary>
        public static bool DkgHasProgressed(DKGSession s) =>
            s.Round1Commitments.Count > 1 || s.Round2Secret != null || s.GeneratedSharesJson != null
            || !s.ReceivedSharesJson.IsEmpty || !s.ReceivedShares.IsEmpty || !s.Round3Verifications.IsEmpty;

        /// <summary>
        /// Whether a ceremony holds a place: not finished, started within the window, and either its leader has
        /// continued it or it is still within the time a leader takes to come back.
        /// </summary>
        public static bool DkgIsUnderWay(DKGSession s, long nowSeconds) =>
            !s.IsCompleted && s.StartTimestamp >= nowSeconds - DKG_LEADER_CAP_WINDOW_SECONDS
            && (DkgHasProgressed(s) || s.StartTimestamp >= nowSeconds - DKG_UNPROGRESSED_SECONDS);

        /// <summary>
        /// Why a ceremony start is refused for want of room, age or novelty; null when it may go on. Counted over the
        /// sessions given, so the rule can be tested without the store. With no leader given only what can be judged
        /// before the leader's signature is checked (the validator's total, the start's age) is judged.
        /// </summary>
        public static string? DkgStartRefusal(IEnumerable<DKGSession> sessions, long nowSeconds, string? sessionId, long startTimestamp, string? leaderAddress, string? sourceIp)
        {
            if (Math.Abs(nowSeconds - startTimestamp) > DKG_START_MAX_AGE_SECONDS)
                return "This ceremony start is not recent. Sign a new one.";
            var underWay = sessions.Where(s => DkgIsUnderWay(s, nowSeconds)).ToList();
            if (underWay.Count >= MAX_DKG_SESSIONS)
                return "Maximum concurrent DKG sessions reached. Try again later.";
            if (string.IsNullOrEmpty(leaderAddress)) return null;
            if (DkgStartWasSeen(sessionId))
                return "This ceremony start was used before. Start again under a new session id.";
            if (underWay.Count(s => string.Equals(s.LeaderAddress, leaderAddress, StringComparison.Ordinal)) >= MAX_DKG_SESSIONS_PER_LEADER)
                return $"Too many open DKG sessions for this leader (cap {MAX_DKG_SESSIONS_PER_LEADER}). Finish or abandon them first.";
            if (!string.IsNullOrEmpty(sourceIp) && underWay.Count(s => string.Equals(s.SourceIp, sourceIp, StringComparison.Ordinal)) >= MAX_DKG_SESSIONS_PER_SOURCE)
                return $"Too many key ceremonies under way from this network address (cap {MAX_DKG_SESSIONS_PER_SOURCE}). Try again shortly.";
            return null;
        }

        /// <summary>
        /// Max IN-PROGRESS DKG sessions a single leader address may hold open. Without a per-leader
        /// cap, any address can fill the global cap and block legitimate contract creation. Must
        /// exceed the coordinator's own start-retry count (each retry opens a new session id under
        /// the same leader) or a legitimate retry would be refused by exactly the validators it needs.
        /// </summary>
        public const int MAX_DKG_SESSIONS_PER_LEADER = 4;

        /// <summary>An in-progress DKG session older than this no longer counts against its leader.</summary>
        public const int DKG_LEADER_CAP_WINDOW_SECONDS = 900;
        
        /// <summary>
        /// Maximum concurrent signing sessions allowed. One session signs one input of one Bitcoin transaction, and a
        /// vault the chain watches can have many withdrawals being signed at once, each led in turn by several of its
        /// signers: 50 sessions kept for an hour were used up by a handful of ordinary withdrawals, and could be
        /// filled on purpose by the requester of one small pinned withdrawal (second path audit, 14 October 2026).
        /// </summary>
        public const int MAX_SIGNING_SESSIONS = 500;

        /// <summary>A signing session is dropped this long after it was started; a ceremony takes seconds.</summary>
        public const int SIGNING_SESSION_SECONDS = 600;

        /// <summary>Signing sessions one leader may have open on this validator.</summary>
        public const int MAX_SIGNING_SESSIONS_PER_LEADER = 60;

        /// <summary>
        /// Signing sessions one leader may have open here for the same thing to sign (one input of one transaction): a
        /// retry. Counted per leader: a limit across leaders let one leader's three sessions refuse every other leader
        /// of that input for as long as it renewed them.
        /// </summary>
        public const int MAX_SIGNING_SESSIONS_PER_LEADER_AND_HASH = 2;

        /// <summary>How far a signing start's timestamp may be from this validator's clock, in seconds: an old start cannot be sent again to use up its leader's room.</summary>
        public const int SIGNING_START_MAX_AGE_SECONDS = 1800; // wide enough for a wallet that signs its start, then asks for the ceremony a while later

        /// <summary>
        /// Why a new signing session is refused for want of room; null when there is room. Counted over the sessions
        /// given (the open ones), so the rule can be tested without the store. With no leader given only the
        /// validator's total is judged (before the leader's signature has been checked).
        /// </summary>
        public static string? SigningStartRefusal(IEnumerable<SigningSession> open, string? leaderAddress, string? messageHash)
        {
            var sessions = open as ICollection<SigningSession> ?? open.ToList();
            if (sessions.Count >= MAX_SIGNING_SESSIONS)
                return "Maximum concurrent signing sessions reached. Try again later.";
            if (string.IsNullOrEmpty(leaderAddress)) return null;
            if (sessions.Count(x => x.LeaderAddress == leaderAddress) >= MAX_SIGNING_SESSIONS_PER_LEADER)
                return $"This leader already has {MAX_SIGNING_SESSIONS_PER_LEADER} signing sessions open here. Try again later.";
            if (!string.IsNullOrEmpty(messageHash)
                && sessions.Count(x => x.LeaderAddress == leaderAddress && string.Equals(x.MessageHash, messageHash, StringComparison.OrdinalIgnoreCase)) >= MAX_SIGNING_SESSIONS_PER_LEADER_AND_HASH)
                return $"This leader already has {MAX_SIGNING_SESSIONS_PER_LEADER_AND_HASH} signing sessions open here for the same input. Try again later.";
            return null;
        }
        
        /// <summary>Maximum number of participant addresses per session (supports mainnet scale)</summary>
        public const int MAX_PARTICIPANTS = 200;
        
        /// <summary>Minimum required threshold percentage</summary>
        public const int MIN_THRESHOLD = 51;
        
        /// <summary>Maximum required threshold percentage</summary>
        public const int MAX_THRESHOLD = 100;
        
        /// <summary>Maximum session ID length</summary>
        public const int MAX_SESSION_ID_LENGTH = 100;
        
        /// <summary>Maximum commitment/share data length in characters (FIND-014: bound data before FFI, scaled for 200 participants)</summary>
        public const int MAX_COMMITMENT_DATA_LENGTH = 32768;
        
        public static ConcurrentDictionary<string, DKGSession> DKGSessions { get; } = new();

        /// <summary>
        /// Number of RECENT, IN-PROGRESS DKG sessions led by <paramref name="leaderAddress"/>.
        /// Completed sessions stay in storage until the hourly cleanup so results can be fetched, and
        /// abandoned ones linger too; neither may count against the leader, or a user creating a third
        /// contract (or the coordinator retrying a start) would be refused.
        /// </summary>
        public static int CountDkgSessionsForLeader(string? leaderAddress) => CountDkgSessionsForLeader(leaderAddress, TimeUtil.GetTime());

        public static int CountDkgSessionsForLeader(string? leaderAddress, long nowSeconds)
        {
            if (string.IsNullOrEmpty(leaderAddress)) return 0;
            var cutoff = nowSeconds - DKG_LEADER_CAP_WINDOW_SECONDS;
            return DKGSessions.Values.Count(s => !s.IsCompleted
                                                 && s.StartTimestamp >= cutoff
                                                 && string.Equals(s.LeaderAddress, leaderAddress, StringComparison.Ordinal));
        }

        /// <summary>
        /// True when another (not completed) DKG session already targets the same SmartContractUID.
        /// Two concurrent DKGs for one contract would finalize into two different group keys and
        /// split the validators; only one may be in flight.
        /// </summary>
        public static bool HasInProgressDkgForContract(string? smartContractUID, string? exceptSessionId)
        {
            if (string.IsNullOrEmpty(smartContractUID)) return false;
            return DKGSessions.Values.Any(s => !s.IsCompleted
                                               && string.Equals(s.SmartContractUID, smartContractUID, StringComparison.Ordinal)
                                               && !string.Equals(s.SessionId, exceptSessionId, StringComparison.Ordinal));
        }

        /// <summary>
        /// True when another in-progress DKG for the contract was started by a DIFFERENT leader.
        /// Two leaders racing one contract must be refused; a leader restarting its own ceremony
        /// (the coordinator retries a start with a fresh session id) is handled by
        /// <see cref="SupersedeOwnDkgSessions"/>.
        /// </summary>
        public static bool HasInProgressDkgForContractByOtherLeader(string? smartContractUID, string? leaderAddress, string? exceptSessionId)
        {
            if (string.IsNullOrEmpty(smartContractUID)) return false;
            return DKGSessions.Values.Any(s => !s.IsCompleted
                                               && string.Equals(s.SmartContractUID, smartContractUID, StringComparison.Ordinal)
                                               && !string.Equals(s.SessionId, exceptSessionId, StringComparison.Ordinal)
                                               && !string.Equals(s.LeaderAddress, leaderAddress, StringComparison.Ordinal));
        }

        /// <summary>
        /// Drops this leader's own earlier in-progress sessions for the contract (its start signature
        /// has already been verified by the caller). Returns how many were removed.
        /// </summary>
        public static int SupersedeOwnDkgSessions(string? smartContractUID, string? leaderAddress, string? exceptSessionId)
        {
            if (string.IsNullOrEmpty(smartContractUID) || string.IsNullOrEmpty(leaderAddress)) return 0;
            var stale = DKGSessions.Where(kv => !kv.Value.IsCompleted
                                                && string.Equals(kv.Value.SmartContractUID, smartContractUID, StringComparison.Ordinal)
                                                && string.Equals(kv.Value.LeaderAddress, leaderAddress, StringComparison.Ordinal)
                                                && !string.Equals(kv.Key, exceptSessionId, StringComparison.Ordinal))
                                   .Select(kv => kv.Key).ToList();
            var removed = 0;
            foreach (var key in stale) if (DKGSessions.TryRemove(key, out _)) removed++;
            return removed;
        }

        /// <summary>Pure cap rule, testable without touching the storage.</summary>
        public static bool LeaderMayOpenDkgSession(int openSessionsForLeader) => openSessionsForLeader < MAX_DKG_SESSIONS_PER_LEADER;
        public static ConcurrentDictionary<string, SigningSession> SigningSessions { get; } = new();
        
        /// <summary>
        /// Clean up old sessions (older than 1 hour)
        /// </summary>
        public static void CleanupOldSessions()
        {
            var currentTime = VerifiedXCore.Utilities.TimeUtil.GetTime();
            var oneHourAgo = currentTime - 3600;
            
            // Cleanup DKG sessions
            var oldDKG = DKGSessions.Where(kvp => kvp.Value.StartTimestamp < oneHourAgo).Select(kvp => kvp.Key).ToList();
            foreach (var key in oldDKG)
            {
                DKGSessions.TryRemove(key, out _);
            }
            
            // Cleanup signing sessions (a shorter life than a key ceremony's: see SIGNING_SESSION_SECONDS)
            var signingCutoff = currentTime - SIGNING_SESSION_SECONDS;
            var oldSigning = SigningSessions.Where(kvp => kvp.Value.StartTimestamp < signingCutoff).Select(kvp => kvp.Key).ToList();
            foreach (var key in oldSigning)
            {
                SigningSessions.TryRemove(key, out _);
            }
        }
    }
}
