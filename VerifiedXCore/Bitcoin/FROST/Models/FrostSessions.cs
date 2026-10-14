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
        /// <summary>
        /// The network the start came from (a leader's node): its /24 or /64, <see cref="VerifiedXCore.Utilities.RemoteIp.Group"/>.
        /// Counted, so one host cannot take every place by using more of its addresses.
        /// </summary>
        public string? SourceIp { get; set; }
        /// <summary>A re-key's ceremony: the vault the new key is for (signed into this validator's attestation).</summary>
        public string? RekeyOf { get; set; }
        /// <summary>The leader signed the start in full (<see cref="FrostDkgLeaderAuth"/>): its Round 2 set and share batch must be signed too.</summary>
        public bool LeaderSigned { get; set; }
        /// <summary>The hash of the start this session was opened by: the same start sent again is answered, not refused.</summary>
        public string? StartBodyHash { get; set; }
        
        // FROST native library state for this validator's participation
        public ushort MyParticipantIndex { get; set; }         // This validator's 1-based index
        public string? Round1SecretPackage { get; set; }       // Secret from DKGRound1Generate (kept private)
        public string? Round2Secret { get; set; }              // Secret from DKGRound2GenerateShares (kept private)
        public string? GeneratedSharesJson { get; set; }       // Shares this validator created for others
        /// <summary>The one-time key each of this validator's shares was sealed with, by recipient identifier (revealed only to settle that recipient's signed report).</summary>
        [Newtonsoft.Json.JsonIgnore]
        public ConcurrentDictionary<string, string> SealEphemerals { get; set; } = new();
        
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

        /// <summary>How far the ceremony has gone here: 0 started, then <see cref="FrostSessionStorage.DkgStage"/>. Each step is taken once.</summary>
        public int Stage { get; set; }

        /// <summary>When the ceremony last went a step further here (its start, until then).</summary>
        public long LastProgressTimestamp { get; set; }

        /// <summary>Its coordinator gave it up (<c>POST /frost/dkg/abort/{sessionId}</c>): it holds no place and its secrets are gone.</summary>
        public bool IsAborted { get; set; }

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
        /// Key ceremonies for vault creations that may be under way on this validator at once. Anyone may lead one (the
        /// leader holds no key material), so the places are guarded by what follows rather than by who asks: a start
        /// must be recent and not seen before, a ceremony holds its place only while it goes a step further every two
        /// minutes (its few steps are each taken once), and one leader and one network (a /24 or /64) each hold only a
        /// few. 50 places with none of that were filled, for fifteen minutes at a time, by thirteen made-up leader keys
        /// from one machine (third pass, 14 October 2026). Re-keys do not use these places (<see cref="MAX_REKEY_DKG_SESSIONS"/>).
        /// </summary>
        public const int MAX_DKG_SESSIONS = 200;

        /// <summary>A ceremony that has gone no step further here for this long no longer holds a place (it goes on if its leader comes back).</summary>
        public const int DKG_UNPROGRESSED_SECONDS = 120;

        /// <summary>Ceremonies under way that one network (a /24 or /64) may have started here.</summary>
        public const int MAX_DKG_SESSIONS_PER_SOURCE = 8;

        /// <summary>
        /// Places kept for re-keys (plan 8.8), apart from creations': a vault whose key is losing its holders must get a
        /// new one whoever is filling the creations' places. A re-key's ceremony is joined only for a vault the chain
        /// says is due, led by a validator that may hold a key share (active, its stake bonded for two days), at most
        /// <see cref="MAX_REKEY_DKG_SESSIONS_PER_LEADER"/> a leader. There is no limit per vault: a limit of two let one
        /// validator restarting its own two starts keep every other leader off a vault (fourth review). Filling every
        /// place takes fifty such validators.
        /// </summary>
        public const int MAX_REKEY_DKG_SESSIONS = 100;

        /// <summary>Re-key ceremonies under way that one leader may have started here (an attempt and its retry).</summary>
        public const int MAX_REKEY_DKG_SESSIONS_PER_LEADER = 2;

        /// <summary>The steps a ceremony takes here, each once: its Round 2 shares made, every share for this validator received, its key made.</summary>
        public static class DkgStage
        {
            public const int Round2 = 1;
            public const int SharesIn = 2;
            public const int Done = 3;
        }

        /// <summary>Records that a ceremony went a step further here. A step already taken changes nothing, so no message keeps a place by being sent again.</summary>
        public static void MarkDkgStage(DKGSession s, int stage, long? nowSeconds = null)
        {
            if (stage <= s.Stage) return;
            s.Stage = stage;
            s.LastProgressTimestamp = nowSeconds ?? TimeUtil.GetTime();
        }

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

        /// <summary>Forgets a start, so its session id can be opened again (an unsigned copy replaced by its leader's signed start).</summary>
        public static void ForgetDkgStart(string sessionId) => SeenDkgStarts.TryRemove(sessionId, out _);

        /// <summary>
        /// The leader's signed start (<paramref name="signedHash"/>) replaces a session of the same id that a copy without the
        /// leader's full signature opened, unless it is that very start with nothing done yet (then it is only marked
        /// signed): a participant could otherwise open a validator's session first with content of its choosing, or the
        /// leader's own content with a Round 2 set of its own, and the leader's start was refused or ran on that set
        /// (fourth review, rounds 6 and 7). Never a signed session or a finished one. True when it was replaced (or there
        /// was nothing to replace).
        /// </summary>
        public static bool MakeWayForSignedStart(string sessionId, string signedHash)
        {
            if (!DKGSessions.TryGetValue(sessionId, out var held)) return true;
            if (held.LeaderSigned || held.IsCompleted || (held.StartBodyHash == signedHash && held.Stage == 0)) return false;
            AbortDkg(held);
            DKGSessions.TryRemove(sessionId, out _);
            ForgetDkgStart(sessionId);
            return true;
        }

        /// <summary>Tests only.</summary>
        internal static void ForgetDkgStarts() => SeenDkgStarts.Clear();

        /// <summary>
        /// Whether a ceremony has gone a step further here than its start. Commitments posted with a Round 2 request are
        /// not a step: anybody who knows the session id could post them (the session's own leader among them).
        /// </summary>
        public static bool DkgHasProgressed(DKGSession s) => s.Stage > 0;

        /// <summary>
        /// Whether a ceremony holds a place: not finished or given up, started within the window, and gone a step further
        /// (or started) within the last <see cref="DKG_UNPROGRESSED_SECONDS"/>.
        /// </summary>
        public static bool DkgIsUnderWay(DKGSession s, long nowSeconds) =>
            !s.IsCompleted && !s.IsAborted && s.StartTimestamp >= nowSeconds - DKG_LEADER_CAP_WINDOW_SECONDS
            && Math.Max(s.StartTimestamp, s.LastProgressTimestamp) >= nowSeconds - DKG_UNPROGRESSED_SECONDS;

        /// <summary>
        /// Why a ceremony start is refused for want of room, age or novelty; null when it may go on. Counted over the
        /// sessions given, so the rule can be tested without the store. With no leader given only what can be judged
        /// before the leader's signature is checked (the validator's total, the start's age) is judged.
        /// </summary>
        /// <remarks>
        /// A re-key's start (<paramref name="rekeyOf"/> set) is counted among re-keys only, and not per network: its leader
        /// is a validator, checked by the caller (<see cref="RekeyLeaderRefusal"/>), and validators may share a host.
        /// </remarks>
        public static string? DkgStartRefusal(IEnumerable<DKGSession> sessions, long nowSeconds, string? sessionId, long startTimestamp, string? leaderAddress, string? sourceIp, string? rekeyOf = null)
        {
            if (Math.Abs(nowSeconds - startTimestamp) > DKG_START_MAX_AGE_SECONDS)
                return "This ceremony start is not recent. Sign a new one.";
            var rekey = !string.IsNullOrEmpty(rekeyOf);
            var underWay = sessions.Where(s => DkgIsUnderWay(s, nowSeconds) && string.IsNullOrEmpty(s.RekeyOf) != rekey).ToList();
            if (underWay.Count >= (rekey ? MAX_REKEY_DKG_SESSIONS : MAX_DKG_SESSIONS))
                return rekey ? "Maximum concurrent re-key ceremonies reached. Try again later." : "Maximum concurrent DKG sessions reached. Try again later.";
            if (string.IsNullOrEmpty(leaderAddress)) return null;
            if (DkgStartWasSeen(sessionId))
                return "This ceremony start was used before. Start again under a new session id.";
            var perLeader = rekey ? MAX_REKEY_DKG_SESSIONS_PER_LEADER : MAX_DKG_SESSIONS_PER_LEADER;
            if (underWay.Count(s => string.Equals(s.LeaderAddress, leaderAddress, StringComparison.Ordinal)) >= perLeader)
                return $"Too many open DKG sessions for this leader (cap {perLeader}). Finish or abandon them first.";
            if (!rekey && !string.IsNullOrEmpty(sourceIp) && underWay.Count(s => string.Equals(s.SourceIp, sourceIp, StringComparison.Ordinal)) >= MAX_DKG_SESSIONS_PER_SOURCE)
                return $"Too many key ceremonies under way from this network (cap {MAX_DKG_SESSIONS_PER_SOURCE}). Try again shortly.";
            return null;
        }

        /// <summary>
        /// Why a re-key's start is not taken from this leader: only a validator that may hold a key share leads one (active,
        /// funded, its stake bonded for the minimum age, as for a ceremony's participants). Null when it may.
        /// </summary>
        public static string? RekeyLeaderRefusal(string? leaderAddress, Func<string, bool>? mayHoldAShare = null)
        {
            if (string.IsNullOrEmpty(leaderAddress)) return "A re-key's ceremony needs a leader.";
            var eligible = mayHoldAShare ?? (a =>
            {
                var v = VerifiedXCore.Bitcoin.Services.VBTCValidatorRegistry.GetValidator(a);
                return v?.IsActive == true && VerifiedXCore.Services.ValidatorBond.CeremonyEligible(new[] { v }, (Globals.LastBlock?.Height ?? 0) + 1).Count > 0;
            });
            return eligible(leaderAddress) ? null : "A re-key's ceremony is led by an active vBTC validator whose stake has been bonded for the minimum age.";
        }

        /// <summary>
        /// Gives a ceremony up (its coordinator's attempt failed): it holds no place from now on and its secrets are
        /// dropped. A finished ceremony is left alone: its key package may be the vault's. False when there is nothing to give up.
        /// </summary>
        public static bool AbortDkg(DKGSession s)
        {
            if (s.IsCompleted || s.IsAborted) return false;
            s.IsAborted = true;
            s.Round1SecretPackage = null;
            s.Round2Secret = null;
            s.GeneratedSharesJson = null;
            s.ReceivedSharesJson.Clear();
            s.ReceivedShares.Clear();
            return true;
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
            // Only a ceremony under way: an idle or given-up one kept another leader off the UID for an hour, and a
            // validator could open such sessions under the UIDs it expected the next re-key leader to use (fourth review,
            // round 5). Two that both finish cannot both keep a key under one UID (the key store refuses a second).
            var now = TimeUtil.GetTime();
            return DKGSessions.Values.Any(s => DkgIsUnderWay(s, now)
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
