using System.Collections.Concurrent;
using System.Text;
using Newtonsoft.Json;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Services
{
    /// <summary>
    /// Committee seat self-heal (Oct 2026, after the Sep 30 stall and the Oct 1 fork). Before this, a restarted member
    /// was re-added by reachability (heal, after 2 probes) and evicted for not casting (disavow, after 3 checks) in a
    /// loop; a dead member's seat stayed in the record forever because only the version audit removed seats and it
    /// audited the live list, which had already dropped the member; and promotions were proposed into a full record,
    /// failed to append and locked every signer for 10 minutes.
    ///
    /// The rules:
    ///  • Absent = unreachable, or reachable but not casting. Each caster times every committee member's absence from
    ///    when it first saw it; the timer resets only when the member casts again.
    ///  • Grace: 5 minutes (10 after a signed /maintenance notice). Inside it the seat is kept.
    ///  • A restarted member resumes only with the newest record listing it, a chain showing it was gone less than the
    ///    grace period, and signed approvals from enough other members that no removal majority can also exist.
    ///    A signer that approved a resume refuses to sign that member's removal, and the reverse.
    ///  • After the grace period one proposer (the lowest present address) proposes the removal; each signer signs only
    ///    when its own timer agrees. Promotion waits until the removal is in the record: one candidate at a time,
    ///    rate-limited, by the same proposer.
    /// </summary>
    public static class CasterSeatService
    {
        public const long GraceSeconds = 300;
        public const long MaintenanceGraceSeconds = 600;
        /// <summary>Signers' timers start a few seconds apart; a signer agrees once its own timer is this close.</summary>
        public const long TimerToleranceSeconds = 60;
        public const long RequestSkewSeconds = 120;
        public const long ActionIntervalSeconds = 60;
        public const long ResumeRetrySeconds = 10;
        /// <summary>How long an approved resume blocks signing that member's removal. Covers the resume itself; once the
        /// member casts, our observation of it casting blocks the removal anyway.</summary>
        public const long ResumeApprovalHoldSeconds = 120;

        // ── Absence ─────────────────────────────────────────────────────────────────────────────────

        public sealed class Absence
        {
            public long SinceUnix;
            public long SinceHeight;
        }

        private static readonly ConcurrentDictionary<string, Absence> _absent = new(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, long> _maintenanceUntil = new(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, long> _departures = new(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, long> _outdated = new(StringComparer.Ordinal);
        /// <summary>Member → (record seq, unix) of a removal we signed / a resume we approved. Mutual exclusion (C.3d).</summary>
        private static readonly ConcurrentDictionary<string, (long Seq, long At)> _removalSigned = new(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, (long Seq, long At)> _resumeApproved = new(StringComparer.Ordinal);

        /// <summary>Records one observation. Present clears the timer; absent starts it if it isn't running.</summary>
        internal static void Observe(string address, bool present, long nowUnix, long height)
        {
            if (string.IsNullOrEmpty(address)) return;
            if (present)
            {
                if (_absent.TryRemove(address, out var was))
                    CasterLogUtility.Log($"SEAT: {address} is casting again after {nowUnix - was.SinceUnix}s absent.", "SEAT");
                return;
            }
            if (_absent.TryAdd(address, new Absence { SinceUnix = nowUnix, SinceHeight = height }))
                CasterLogUtility.Log($"SEAT: {address} absent (not reachable or not casting) from height {height} — grace {GraceFor(address)}s.", "SEAT");
        }

        internal static long? AbsentSeconds(string address, long nowUnix) =>
            _absent.TryGetValue(address, out var a) ? Math.Max(0, nowUnix - a.SinceUnix) : null;

        /// <summary>5 minutes, or 10 when the member announced maintenance and went absent inside that window.</summary>
        internal static long GraceFor(string address)
        {
            if (_maintenanceUntil.TryGetValue(address, out var until))
            {
                var since = _absent.TryGetValue(address, out var a) ? a.SinceUnix : TimeUtil.GetTime();
                if (since <= until) return MaintenanceGraceSeconds;
            }
            return GraceSeconds;
        }

        internal static bool PastGrace(string address, long nowUnix, long tolerance = 0) =>
            AbsentSeconds(address, nowUnix) is long s && s >= GraceFor(address) - tolerance;

        /// <summary>Probes every other member of the newest record each monitor tick and updates the timers.</summary>
        public static async Task ObserveCommitteeAsync()
        {
            var head = CasterMembershipStore.GetCurrent();
            if (head == null)
            {
                _absent.Clear();
                return;
            }
            var members = head.Casters.Where(c => !string.IsNullOrEmpty(c.Address) && c.Address != Globals.ValidatorAddress).ToList();
            foreach (var key in _absent.Keys)
                if (!members.Any(m => m.Address == key))
                    _absent.TryRemove(key, out _);

            var now = TimeUtil.GetTime();
            var height = Globals.LastBlock?.Height ?? 0;
            var results = await Task.WhenAll(members.Select(async m => (m.Address, Present: await IsCastingAsync(m.PeerIP))));
            foreach (var r in results)
                Observe(r.Address, r.Present, now, height);
        }

        /// <summary>True when the peer answers /Health without the <c>Casting</c> field (a build before the resume protocol).</summary>
        internal static async Task<bool> IsLegacyBuildAsync(string? peerIp)
        {
            if (string.IsNullOrEmpty(peerIp)) return false;
            try
            {
                using var client = Globals.HttpClientFactory.CreateClient();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var resp = await client.GetAsync($"http://{peerIp.Replace("::ffff:", "")}:{Globals.ValAPIPort}/valapi/validator/Health", cts.Token);
                if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return true;
                if (!resp.IsSuccessStatusCode) return false;
                var body = Newtonsoft.Json.Linq.JObject.Parse(await resp.Content.ReadAsStringAsync());
                return body.Property("Casting") == null;
            }
            catch { return false; }
        }

        /// <summary>Present = reachable and reporting that it casts (<c>Casting</c>; older builds: <c>IsBlockCaster</c>).</summary>
        internal static async Task<bool> IsCastingAsync(string? peerIp)
        {
            if (string.IsNullOrEmpty(peerIp)) return false;
            try
            {
                using var client = Globals.HttpClientFactory.CreateClient();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var resp = await client.GetAsync($"http://{peerIp.Replace("::ffff:", "")}:{Globals.ValAPIPort}/valapi/validator/Health", cts.Token);
                if (!resp.IsSuccessStatusCode) return false;
                var body = await resp.Content.ReadAsStringAsync();
                var health = JsonConvert.DeserializeAnonymousType(body, new { IsBlockCaster = (bool?)null, Casting = (bool?)null });
                return health?.Casting ?? health?.IsBlockCaster ?? false;
            }
            catch { return false; }
        }

        // ── This node's casting state ───────────────────────────────────────────────────────────────

        /// <summary>Set once this process may cast: resumed, promoted, or cast during a seed bootstrap. A process starts
        /// unconfirmed in the record era — a restart is a resume, never an automatic return to the seat.</summary>
        public static volatile bool ResumeConfirmed;
        /// <summary>Set by the round loop while RECORD-HOLD sits it out.</summary>
        public static volatile bool HeldByRecord;
        /// <summary>Set by /maintenance; a normal exit after it keeps the seat instead of departing.</summary>
        public static volatile bool MaintenanceAnnounced;

        public static bool NeedsResume => CasterMembershipStore.RecordEraActive && !ResumeConfirmed && !Globals.IsBootstrapMode;

        /// <summary>What /Health reports as <c>Casting</c>, and what peers time absence by.</summary>
        public static bool CastingNow => Globals.IsBlockCaster && !NeedsResume && !HeldByRecord;

        public static void ConfirmCasting(string reason)
        {
            if (ResumeConfirmed) return;
            ResumeConfirmed = true;
            CasterLogUtility.Log($"SEAT: casting confirmed — {reason}.", "SEAT");
        }

        internal static void ResetForTests()
        {
            _absent.Clear(); _maintenanceUntil.Clear(); _departures.Clear(); _outdated.Clear();
            _removalSigned.Clear(); _resumeApproved.Clear();
            ResumeConfirmed = false; HeldByRecord = false; MaintenanceAnnounced = false;
            _lastRemovalAttempt = 0; _lastPromotionAttempt = 0; _lastResumeAttempt = 0;
        }

        // ── Removal after the grace period ──────────────────────────────────────────────────────────

        private static long _lastRemovalAttempt;
        private static long _lastPromotionAttempt;

        /// <summary>Pure: the single proposer for seat changes — the lowest address among present members.</summary>
        internal static string? SeatProposer(IEnumerable<string> members, Func<string, bool> isPresent) =>
            members.Where(isPresent).OrderBy(a => a, StringComparer.Ordinal).FirstOrDefault();

        /// <summary>Pure: the member to remove — the longest absent past its grace period, or null.</summary>
        internal static string? PickRemoval(IEnumerable<string> members, Func<string, long?> absentSeconds, Func<string, long> graceFor) =>
            members.Where(a => absentSeconds(a) is long s && s >= graceFor(a))
                .OrderByDescending(a => absentSeconds(a))
                .ThenBy(a => a, StringComparer.Ordinal)
                .FirstOrDefault();

        private static bool IsPresentForProposer(string address) =>
            address == Globals.ValidatorAddress ? CastingNow : !_absent.ContainsKey(address);

        private static bool IsSeatProposer(CasterMembershipRecord head) =>
            SeatProposer(head.Casters.Select(c => c.Address), IsPresentForProposer) == Globals.ValidatorAddress;

        /// <summary>Monitor tick: the seat proposer proposes removing one member past its grace period.</summary>
        public static async Task TryRemoveAbsentAsync()
        {
            var head = CasterMembershipStore.GetCurrent();
            if (head == null || !CastingNow || Globals.IsBootstrapMode || string.IsNullOrEmpty(Globals.ValidatorAddress)) return;
            var now = TimeUtil.GetTime();
            if (now - _lastRemovalAttempt < ActionIntervalSeconds) return;
            if (!IsSeatProposer(head)) return;

            var target = PickRemoval(head.Casters.Select(c => c.Address).Where(a => a != Globals.ValidatorAddress),
                a => AbsentSeconds(a, now), GraceFor);
            if (target == null) return;

            _lastRemovalAttempt = now;
            CasterLogUtility.Log($"SEAT: proposing removal of {target} — absent {AbsentSeconds(target, now)}s (grace {GraceFor(target)}s).", "SEAT");
            if (await CasterMembershipService.ProposeRotationAsync("Demotion", target, null))
            {
                _absent.TryRemove(target, out _);
                await CasterDiscoveryService.OnCasterRemoved(target).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Refusal reason for a promotion attempt in the record era, or null to go ahead. A seat is free only when the
        /// record has room (a member evicted from the live list still holds its seat until it is removed).
        /// </summary>
        public static string? PromotionRefusal()
        {
            var head = CasterMembershipStore.GetCurrent();
            if (head == null) return null;
            if (head.Casters.Count >= CasterDiscoveryService.MaxCasters)
                return $"record is full ({head.Casters.Count}/{CasterDiscoveryService.MaxCasters}); a seat frees only after a removal";
            if (!CastingNow) return "this caster is not casting";
            if (!IsSeatProposer(head)) return "another member is the seat proposer";
            var now = TimeUtil.GetTime();
            if (now - _lastPromotionAttempt < ActionIntervalSeconds)
                return $"last promotion attempt {now - _lastPromotionAttempt}s ago";
            return null;
        }

        public static void NotePromotionAttempt() => _lastPromotionAttempt = TimeUtil.GetTime();

        // ── Signer rules ────────────────────────────────────────────────────────────────────────────

        public static void NoteDeparture(string address) => _departures[address] = TimeUtil.GetTime();
        public static void NoteOutdated(string address) => _outdated[address] = TimeUtil.GetTime();

        /// <summary>
        /// Why this node will not sign removing <paramref name="address"/>, or null. A removal needs this node's own
        /// evidence: the member's signed departure, its absence past the grace period by our timer, an outdated version
        /// we saw ourselves, or a promotion in the head record that never took. Never while we approved its resume.
        /// </summary>
        internal static string? RemovalRefusal(string changeType, string address, CasterMembershipRecord head, long nowUnix)
        {
            if (changeType == "Departure" && _departures.TryGetValue(address, out var departed) && nowUnix - departed <= MaintenanceGraceSeconds)
                return null;
            if (_resumeApproved.TryGetValue(address, out var approved) && approved.Seq == head.RecordSeq && nowUnix - approved.At <= ResumeApprovalHoldSeconds)
                return "we approved its resume";
            if (PastGrace(address, nowUnix, TimerToleranceSeconds))
                return null;
            if (_outdated.TryGetValue(address, out var seen) && nowUnix - seen <= MaintenanceGraceSeconds)
                return null;
            if (head.ChangeType == "Promotion" && head.ChangedAddress == address && AbsentSeconds(address, nowUnix) != null)
                return null;
            var absent = AbsentSeconds(address, nowUnix);
            return absent == null
                ? "it is casting by our observation"
                : $"absent {absent}s by our timer, grace {GraceFor(address)}s";
        }

        public static void NoteRemovalSigned(string address, long seq) => _removalSigned[address] = (seq, TimeUtil.GetTime());
        internal static void NoteResumeApproved(string address, long seq, long nowUnix) => _resumeApproved[address] = (seq, nowUnix);
        internal static void NoteMaintenance(string address, long untilUnix) => _maintenanceUntil[address] = untilUnix;

        // ── Resume (approver side) ──────────────────────────────────────────────────────────────────

        internal static string ResumeRequestMessage(CasterResumeRequest r) => $"RESUME|{r.Address}|{r.RecordSeq}|{r.RecordHash}|{r.Timestamp}";
        internal static string ResumeApprovalMessage(string address, long seq, string hash, long requestTimestamp) => $"RESUME-OK|{address}|{seq}|{hash}|{requestTimestamp}";
        internal static string MaintenanceMessage(string address, long ts) => $"MAINT|{address}|{ts}";

        /// <summary>
        /// Pure given the timers: why a resume request is refused, or null. The request must be fresh and signed, both
        /// sides must hold the same record listing both, the member must be inside its grace period by our timer, and we
        /// must not have signed its removal.
        /// </summary>
        internal static string? ResumeRefusal(CasterResumeRequest req, CasterMembershipRecord? head, string? self, long nowUnix, Func<string, string, string, bool> verify)
        {
            if (string.IsNullOrEmpty(req.Address) || !verify(req.Address, ResumeRequestMessage(req), req.Signature)) return "bad signature";
            if (Math.Abs(nowUnix - req.Timestamp) > RequestSkewSeconds) return "stale request";
            if (head == null) return "no record";
            if (!string.Equals(head.RecordHash, req.RecordHash, StringComparison.OrdinalIgnoreCase))
                return $"different record (ours seq {head.RecordSeq})";
            if (!head.Casters.Any(c => c.Address == req.Address)) return "not in the record";
            if (string.IsNullOrEmpty(self) || !head.Casters.Any(c => c.Address == self)) return "we are not a member";
            if (PastGrace(req.Address, nowUnix)) return $"absent {AbsentSeconds(req.Address, nowUnix)}s, past the {GraceFor(req.Address)}s grace period";
            if (_removalSigned.TryGetValue(req.Address, out var r) && r.Seq == head.RecordSeq + 1 && nowUnix - r.At < CasterMembershipStore.SignedMarkExpirySeconds)
                return "we signed its removal";
            return null;
        }

        public static CasterResumeApproval HandleResumeRequest(CasterResumeRequest req)
        {
            var head = CasterMembershipStore.GetCurrent();
            var now = TimeUtil.GetTime();
            var answer = new CasterResumeApproval
            {
                SignerAddress = Globals.ValidatorAddress ?? "",
                Address = req.Address,
                RecordSeq = req.RecordSeq,
                RecordHash = req.RecordHash,
                Timestamp = req.Timestamp,
                HeadSeq = head?.RecordSeq ?? -1,
            };
            var refusal = ResumeRefusal(req, head, Globals.ValidatorAddress, now, SignatureService.VerifySignature);
            var account = refusal == null ? AccountData.GetLocalValidator() : null;
            if (refusal == null && account?.GetPrivKey == null) refusal = "no validator key";
            if (refusal != null)
            {
                answer.Reason = refusal;
                CasterLogUtility.Log($"SEAT: refused resume of {req.Address} — {refusal}.", "SEAT");
                return answer;
            }
            var sig = SignatureService.CreateSignature(ResumeApprovalMessage(req.Address, req.RecordSeq, req.RecordHash, req.Timestamp), account!.GetPrivKey!, account.PublicKey);
            if (sig == "ERROR") { answer.Reason = "signing failed"; return answer; }
            NoteResumeApproved(req.Address, head!.RecordSeq, now);
            answer.Approve = true;
            answer.Signature = sig;
            CasterLogUtility.Log($"SEAT: approved resume of {req.Address} at record seq {head.RecordSeq}.", "SEAT");
            return answer;
        }

        /// <summary>Pure: approvals from other members needed so that no removal majority can exist alongside the resume.
        /// A removal needs n/2+1 signers among the other n-1 members, so a resume needs (n-1)-(n/2+1)+1 of them.</summary>
        internal static int ResumeNeed(int committeeSize) => Math.Max(1, committeeSize - 1 - committeeSize / 2);

        /// <summary>Pure: too few other members run the resume protocol for a resume to ever be approved (unreachable members don't count as older builds).</summary>
        internal static bool LegacyFleet(int others, int legacyPeers, int need) => legacyPeers > 0 && others - legacyPeers < need;

        /// <summary>Pure: the distinct members whose valid approval matches this request.</summary>
        internal static HashSet<string> ValidApprovers(CasterResumeRequest req, CasterMembershipRecord head, IEnumerable<CasterResumeApproval> approvals, Func<string, string, string, bool> verify)
        {
            var members = head.Casters.Select(c => c.Address).ToHashSet(StringComparer.Ordinal);
            var msg = ResumeApprovalMessage(req.Address, req.RecordSeq, req.RecordHash, req.Timestamp);
            return approvals
                .Where(a => a != null && a.Approve && a.SignerAddress != req.Address && members.Contains(a.SignerAddress)
                    && a.Address == req.Address && a.RecordSeq == req.RecordSeq && a.Timestamp == req.Timestamp
                    && string.Equals(a.RecordHash, req.RecordHash, StringComparison.OrdinalIgnoreCase)
                    && verify(a.SignerAddress, msg, a.Signature))
                .Select(a => a.SignerAddress)
                .ToHashSet(StringComparer.Ordinal);
        }

        /// <summary>A peer's notice that a member resumed: verified against our record, then the member is back in our
        /// live list and its absence timer is cleared.</summary>
        public static bool HandleResumedNotice(CasterResumedNotice notice)
        {
            var req = notice?.Request;
            var head = CasterMembershipStore.GetCurrent();
            if (req == null || head == null) return false;
            if (!string.Equals(head.RecordHash, req.RecordHash, StringComparison.OrdinalIgnoreCase)) return false;
            if (!SignatureService.VerifySignature(req.Address, ResumeRequestMessage(req), req.Signature)) return false;
            var member = head.Casters.FirstOrDefault(c => c.Address == req.Address);
            if (member == null) return false;
            var approvers = ValidApprovers(req, head, notice!.Approvals ?? new(), SignatureService.VerifySignature);
            if (approvers.Count < ResumeNeed(head.Casters.Count)) return false;

            _absent.TryRemove(req.Address, out _);
            CasterDiscoveryService.AddBlockCasterIfRoomAndUnique(new Peers
            {
                IsIncoming = false,
                IsOutgoing = true,
                IsValidator = true,
                PeerIP = member.PeerIP,
                ValidatorAddress = member.Address,
                ValidatorPublicKey = member.PublicKey,
            });
            Globals.SyncKnownCastersFromBlockCasters();
            CasterLogUtility.Log($"SEAT: {req.Address} resumed with {approvers.Count} approvals — back in the live list.", "SEAT");
            return true;
        }

        // ── Resume (requester side) ─────────────────────────────────────────────────────────────────

        private static long _lastResumeAttempt;
        private static string _lastResumeHold = "waiting for the first resume attempt";

        /// <summary>
        /// Pure: was this member active recently enough to resume? Walks back from the tip to its last attestation in a
        /// block certificate (or to the height its seat took effect, if it joined after that) and compares block times.
        /// A chain stalled while everyone was down counts as no time gone.
        /// </summary>
        internal static (bool Ok, string Detail) ChainAbsence(string self, long tipHeight, Func<long, Block?> getBlock, long seatEffectiveHeight, long graceSeconds)
        {
            var tip = getBlock(tipHeight);
            if (tip == null) return (false, "tip block unavailable");
            for (var h = tipHeight; h >= 0; h--)
            {
                var b = h == tipHeight ? tip : getBlock(h);
                if (b == null) return (false, $"block {h} unavailable");
                var gone = tip.Timestamp - b.Timestamp;
                if (gone > graceSeconds)
                    return (false, $"no attestation of ours in the last {graceSeconds}s of chain (searched back to {h})");
                if (b.ConsensusCertificate?.Attestations?.Any(a => a.CasterAddress == self) == true)
                    return (true, $"last attested block {h}, {gone}s before the tip");
                if (h <= seatEffectiveHeight)
                    return (true, $"seat took effect at {seatEffectiveHeight}, {gone}s before the tip");
            }
            return (false, "no chain");
        }

        /// <summary>Effective height of the record that gave us our current, unbroken run of membership.</summary>
        private static long SeatEffectiveHeight(string self)
        {
            var chain = CasterMembershipStore.GetSince(-1).OrderByDescending(r => r.RecordSeq).ToList();
            long effective = long.MaxValue;
            foreach (var r in chain)
            {
                if (!r.Casters.Any(c => c.Address == self)) break;
                effective = r.EffectiveFromHeight;
            }
            return effective == long.MaxValue ? -1 : effective;
        }

        private static string MaintenanceMarkerPath() => Path.Combine(GetPathUtility.GetDatabasePath(), "caster_maintenance.txt");

        private static long OwnGraceSeconds()
        {
            try
            {
                var path = MaintenanceMarkerPath();
                if (File.Exists(path) && long.TryParse(File.ReadAllText(path).Trim(), out var at)
                    && TimeUtil.GetTime() - at <= MaintenanceGraceSeconds + GraceSeconds)
                    return MaintenanceGraceSeconds;
            }
            catch { }
            return GraceSeconds;
        }

        /// <summary>
        /// The round loop calls this while <see cref="NeedsResume"/>. Returns null once this node may cast, otherwise why it
        /// is still waiting. Attempts are spaced <see cref="ResumeRetrySeconds"/> apart.
        /// </summary>
        public static async Task<string?> TryResumeAsync()
        {
            if (!NeedsResume) return null;
            var now = TimeUtil.GetTime();
            if (now - _lastResumeAttempt < ResumeRetrySeconds) return _lastResumeHold;
            _lastResumeAttempt = now;
            _lastResumeHold = await ResumeAttemptAsync(now);
            return NeedsResume ? _lastResumeHold : null;
        }

        private static async Task<string> ResumeAttemptAsync(long now)
        {
            var self = Globals.ValidatorAddress;
            var head = CasterMembershipStore.GetCurrent();
            if (string.IsNullOrEmpty(self) || head == null) return "no record";
            if (!Globals.IsChainSynced) return "chain not synced";
            if (!head.Casters.Any(c => c.Address == self)) return $"not in record seq {head.RecordSeq}";

            var others = head.Casters.Where(c => c.Address != self && !string.IsNullOrEmpty(c.PeerIP)).ToList();

            // Rolling upgrade: members on older builds can never approve a resume (they have no endpoint, and their /Health
            // has no Casting field). While too few could approve, resume the way those builds do — straight back to the
            // seat — rather than sit out or lose the seat.
            var legacyPeers = (await Task.WhenAll(others.Select(m => IsLegacyBuildAsync(m.PeerIP)))).Count(x => x);
            if (LegacyFleet(others.Count, legacyPeers, ResumeNeed(head.Casters.Count)))
            {
                ConfirmCasting($"only {others.Count - legacyPeers} of {others.Count} other members run the resume protocol (need {ResumeNeed(head.Casters.Count)}) — resuming as older builds do");
                return "resumed";
            }

            var grace = OwnGraceSeconds();
            var chain = ChainAbsence(self, Globals.LastBlock.Height, BlockchainData.GetBlockByHeight, SeatEffectiveHeight(self), grace);
            if (!chain.Ok)
            {
                CasterLogUtility.Log($"SEAT: not resuming — {chain.Detail}; gone longer than the {grace}s grace period. Rejoins as a candidate after removal.", "SEAT");
                return $"gone too long ({chain.Detail})";
            }

            var account = AccountData.GetLocalValidator();
            if (account?.GetPrivKey == null) return "no validator key";
            var req = new CasterResumeRequest { Address = self, RecordSeq = head.RecordSeq, RecordHash = head.RecordHash, Timestamp = now };
            req.Signature = SignatureService.CreateSignature(ResumeRequestMessage(req), account.GetPrivKey, account.PublicKey);
            if (req.Signature == "ERROR") return "signing failed";

            var json = JsonConvert.SerializeObject(req);
            var answers = (await Task.WhenAll(others.Select(async m =>
            {
                try
                {
                    using var client = Globals.HttpClientFactory.CreateClient();
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                    using var content = new StringContent(json, Encoding.UTF8, "application/json");
                    var resp = await client.PostAsync($"http://{m.PeerIP.Replace("::ffff:", "")}:{Globals.ValAPIPort}/valapi/validator/RequestCasterResume", content, cts.Token);
                    if (!resp.IsSuccessStatusCode) return null;
                    var a = JsonConvert.DeserializeObject<CasterResumeApproval>(await resp.Content.ReadAsStringAsync());
                    return a != null && a.SignerAddress == m.Address ? (a, m.PeerIP.Replace("::ffff:", "")) : ((CasterResumeApproval Answer, string Ip)?)null;
                }
                catch { return null; }
            }))).Where(x => x != null).Select(x => x!.Value).ToList();

            if (answers.Count == 0)
                return "no other committee member reachable (a full outage is handled by the seed bootstrap)";

            var newer = answers.Where(x => x.Answer.HeadSeq > head.RecordSeq).Select(x => x.Ip).ToList();
            if (newer.Count > 0)
            {
                try { await CasterDiscoveryService.FetchAndAdoptMembershipAsync(newer); } catch { }
                return "a peer holds a newer record — catching up first";
            }

            var approvers = ValidApprovers(req, head, answers.Select(x => x.Answer), SignatureService.VerifySignature);
            var need = ResumeNeed(head.Casters.Count);
            if (approvers.Count < need)
            {
                var refusals = string.Join("; ", answers.Where(x => !x.Answer.Approve).Select(x => $"{x.Answer.SignerAddress}: {x.Answer.Reason}"));
                return $"{approvers.Count}/{need} resume approvals{(refusals.Length > 0 ? $" (refused — {refusals})" : "")}";
            }

            ConfirmCasting($"resumed at record seq {head.RecordSeq} with {approvers.Count}/{need} approvals ({chain.Detail})");
            try { File.Delete(MaintenanceMarkerPath()); } catch { }
            var notice = JsonConvert.SerializeObject(new CasterResumedNotice { Request = req, Approvals = answers.Select(x => x.Answer).Where(a => approvers.Contains(a.SignerAddress)).ToList() });
            _ = Task.WhenAll(others.Select(async m =>
            {
                try
                {
                    using var client = Globals.HttpClientFactory.CreateClient();
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    using var content = new StringContent(notice, Encoding.UTF8, "application/json");
                    await client.PostAsync($"http://{m.PeerIP.Replace("::ffff:", "")}:{Globals.ValAPIPort}/valapi/validator/AnnounceCasterResumed", content, cts.Token);
                }
                catch { }
            }));
            return "resumed";
        }

        // ── /maintenance ────────────────────────────────────────────────────────────────────────────

        /// <summary>Operator command before a planned restart: peers give this member 10 minutes instead of 5, and the
        /// coming exit keeps the seat. The member still passes the resume checks when it returns.</summary>
        public static async Task<string> AnnounceMaintenanceAsync()
        {
            var head = CasterMembershipStore.GetCurrent();
            var self = Globals.ValidatorAddress;
            if (head == null || string.IsNullOrEmpty(self) || !head.Casters.Any(c => c.Address == self))
                return "This node holds no committee seat; nothing to announce.";
            var account = AccountData.GetLocalValidator();
            if (account?.GetPrivKey == null) return "No validator key.";
            var ts = TimeUtil.GetTime();
            var sig = SignatureService.CreateSignature(MaintenanceMessage(self, ts), account.GetPrivKey, account.PublicKey);
            if (sig == "ERROR") return "Signing failed.";
            var json = JsonConvert.SerializeObject(new CasterMaintenanceNotice { Address = self, Timestamp = ts, Signature = sig });

            var others = head.Casters.Where(c => c.Address != self && !string.IsNullOrEmpty(c.PeerIP)).ToList();
            var acks = (await Task.WhenAll(others.Select(async m =>
            {
                try
                {
                    using var client = Globals.HttpClientFactory.CreateClient();
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                    using var content = new StringContent(json, Encoding.UTF8, "application/json");
                    var resp = await client.PostAsync($"http://{m.PeerIP.Replace("::ffff:", "")}:{Globals.ValAPIPort}/valapi/validator/AnnounceCasterMaintenance", content, cts.Token);
                    return resp.IsSuccessStatusCode;
                }
                catch { return false; }
            }))).Count(x => x);

            MaintenanceAnnounced = true;
            try { File.WriteAllText(MaintenanceMarkerPath(), ts.ToString()); } catch { }
            CasterLogUtility.Log($"SEAT: maintenance announced to {acks}/{others.Count} members.", "SEAT");
            return $"Maintenance announced to {acks} of {others.Count} committee members. Your seat is held for {MaintenanceGraceSeconds / 60} minutes; " +
                   "stop the node normally now (the exit will not give up the seat). On restart it must pass the resume checks.";
        }

        public static bool HandleMaintenanceNotice(CasterMaintenanceNotice notice)
        {
            var head = CasterMembershipStore.GetCurrent();
            var now = TimeUtil.GetTime();
            if (notice == null || head == null || string.IsNullOrEmpty(notice.Address)) return false;
            if (Math.Abs(now - notice.Timestamp) > RequestSkewSeconds) return false;
            if (!head.Casters.Any(c => c.Address == notice.Address)) return false;
            if (!SignatureService.VerifySignature(notice.Address, MaintenanceMessage(notice.Address, notice.Timestamp), notice.Signature)) return false;
            NoteMaintenance(notice.Address, notice.Timestamp + MaintenanceGraceSeconds);
            CasterLogUtility.Log($"SEAT: {notice.Address} announced maintenance — grace {MaintenanceGraceSeconds}s if it goes absent before {notice.Timestamp + MaintenanceGraceSeconds}.", "SEAT");
            return true;
        }

        /// <summary>Diagnostics for GetConsensusState.</summary>
        public static object Snapshot()
        {
            var now = TimeUtil.GetTime();
            return new
            {
                CastingNow,
                ResumeConfirmed,
                NeedsResume,
                ResumeHold = NeedsResume ? _lastResumeHold : "",
                Absent = _absent.ToDictionary(kv => kv.Key, kv => new { Seconds = now - kv.Value.SinceUnix, kv.Value.SinceHeight, Grace = GraceFor(kv.Key) }),
            };
        }
    }
}
