using System.Security.Cryptography;
using System.Text;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Services
{
    /// <summary>
    /// The validator-ratified emergency election: the rules. A committee can only be changed by a majority of itself, so
    /// three dead members of five stop the chain with nobody able to replace them. The replacement is decided here by a
    /// different and larger electorate, chain-derived: the bonded, aged validators that are not on the committee. Who is
    /// elected is a pure function of the chain, so there is nothing to propose and nothing to vote on but "yes".
    ///
    /// <list type="bullet">
    /// <item><b>At most one side can ratify.</b> Ratifying takes two thirds of the electorate (<see cref="RatifyNeed"/>).</item>
    /// <item><b>A committee that lost the validators stands down</b> (<see cref="ReachNeed"/>, VreeService): it needs more than a
    /// third of them to acknowledge its tip, and a validator that acknowledges does not sign. Acknowledgements plus
    /// signatures exceed the electorate for every size, so the two cannot both be met.</item>
    /// <item><b>If a fork happens anyway</b> the branch carrying a ratified record wins (ForkChoiceUtility).</item>
    /// <item><b>An elected committee's term ends by rule</b> (<see cref="HandoverBlocks"/>): it is replaced by a scheduled rotation
    /// seeded from an anchor final before it existed, and its certificates stop counting if nothing replaces it.</item>
    /// </list>
    ///
    /// Design and attack analysis: docs/PLAN-Caster-Majority-Outage-Recovery.md. The network side is VreeService.
    /// </summary>
    public static class VreeElection
    {
        public const string EmergencyElectionChangeType = "EmergencyElection";
        public const string ScheduledRotationChangeType = "ScheduledRotation";
        public const string MaintenanceHoldChangeType = "MaintenanceHold";
        public const int SchemeVersionV1 = 1;

        /// <summary>Tip age at which rung 0 opens: every existing self-heal runs first.</summary>
        public const long ElectSeconds = 300;
        /// <summary>A further rung opens every this many seconds.</summary>
        public const long RungSeconds = 60;
        /// <summary>A validator that saw a ratified rung signs no other for this long.</summary>
        public const long RatifiedHoldSeconds = 180;
        /// <summary>Casters probe the validators this often.</summary>
        public const long ProbeSeconds = 15;
        /// <summary>A caster needs its acknowledgements from within this window.</summary>
        public const long ReachWindowSeconds = 60;
        public const int RungSize = 5;
        /// <summary>Below this many electors the election is dark; never fewer signatures than this ratify.</summary>
        public const int MinElectors = 4;
        /// <summary>An elected committee's term (about 17 hours).</summary>
        public const long TermBlocks = 5_000;
        /// <summary>Rung 0 of a rotation takes effect this far past the lapse, and a lapsed committee's certificates stop counting past it.</summary>
        public const long HandoverBlocks = 12;
        public const long MaxHoldSeconds = 3_600;

        /// <summary>
        /// How long this node's chain has stood still: from the tip's timestamp, or from when this node committed it if that
        /// is later. A validator crafts its next block once, right after the tip, so the block that ends a stall is dated
        /// before it, and by its timestamp alone the first block of an elected committee would already look stalled.
        /// </summary>
        public static long StallSeconds()
        {
            var tip = Globals.LastBlock;
            if (tip == null || tip.Height < 0) return long.MaxValue;
            return Math.Max(0, TimeUtil.GetTime() - Math.Max(tip.Timestamp, Globals.LastBlockAddedTimestamp));
        }

        /// <summary>Whether a record, or a block, at this height is under the election's rules.</summary>
        public static bool Active(long height) => height >= Globals.VreeActivationHeight;

        public static bool IsElectionRecord(CasterMembershipRecord? r) =>
            r != null && (r.ChangeType == EmergencyElectionChangeType || r.ChangeType == ScheduledRotationChangeType);

        public static bool IsHoldRecord(CasterMembershipRecord? r) => r?.ChangeType == MaintenanceHoldChangeType;

        /// <summary>Whether a record's hash covers the election's fields.</summary>
        public static bool IsVreeRecord(CasterMembershipRecord? r) => IsElectionRecord(r) || IsHoldRecord(r);

        private static string NetworkId => Globals.IsTestNet ? "testnet" : "mainnet";

        /// <summary>The election's fields, appended to the signed payload of a record that has them.</summary>
        public static string PayloadSuffix(CasterMembershipRecord r) =>
            $"|VREE-V1|{NetworkId}|{ConsensusMessageFormatter.NormalizeHash(r.EpochHash)}|{r.Rung}|{r.SchemeVersion}|{r.LapseHeight}|{r.HoldSeconds}";

        // ── Thresholds ──────────────────────────────────────────────────────────────────────────

        /// <summary>Signatures that ratify: two thirds of the electorate rounded up, and never fewer than <see cref="MinElectors"/>.</summary>
        public static int RatifyNeed(int electors) => Math.Max(MinElectors, (2 * electors + 2) / 3);

        /// <summary>
        /// Acknowledgements a caster needs: strictly more than a third. A third rounded up plus two thirds rounded up is
        /// exactly the set whenever its size is a multiple of three, so a third of the validators isolated with three
        /// casters kept the old committee producing while the other two thirds ratified (the simulator's 12-validator case).
        /// </summary>
        public static int ReachNeed(int reachSet) => reachSet / 3 + 1;

        /// <summary>Whether a caster's acknowledgements allow it to keep producing. A set too small to elect from never stands anyone down.</summary>
        public static bool ReachHeld(int acknowledgements, int reachSet) =>
            reachSet < MinElectors || acknowledgements >= ReachNeed(reachSet);

        /// <summary>The worst time from a caster losing the validators to its last block: a probe period, the window, one block.</summary>
        public static long WorstStandDownSeconds(long blockSeconds) => ProbeSeconds + ReachWindowSeconds + blockSeconds;

        // ── Ranking and rungs ───────────────────────────────────────────────────────────────────

        /// <summary>An address's place in the ranking. It reads the seed and the address only: never the stall height or its hash.</summary>
        public static string Rank(string seed, string address)
        {
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes($"VFX-VREE-V1|{NetworkId}|{seed}|{address}"))).ToLowerInvariant();
        }

        public static List<string> Ranked(string seed, IEnumerable<string> candidates) =>
            candidates.Distinct(StringComparer.Ordinal)
                .OrderBy(a => Rank(seed, a), StringComparer.Ordinal).ThenBy(a => a, StringComparer.Ordinal).ToList();

        /// <summary>
        /// Rung <paramref name="rung"/>: places 5r..5r+4 of the ranking, wrapping past its end, so a short tail is padded from
        /// the top (a rung of 3 or 4 would tolerate one dead member, not two) and rungs go on with r still increasing.
        /// </summary>
        public static List<string> RungMembers(IReadOnlyList<string> ranked, int rung)
        {
            var n = ranked.Count;
            if (n == 0 || rung < 0) return new List<string>();
            var start = (int)(rung * (long)RungSize % n);
            return Enumerable.Range(0, Math.Min(RungSize, n)).Select(i => ranked[(start + i) % n]).ToList();
        }

        // ── The epoch ───────────────────────────────────────────────────────────────────────────

        /// <summary>Whether an address counts as an elector at a height, from its bond (<see cref="ValidatorBond.AgedAt"/>). Test seam.</summary>
        internal static Func<string, long, bool> ElectorEligible =
            (address, height) => ValidatorBond.Active(height) && ValidatorBond.AgedAt(ValidatorBond.Row(address), height);

        /// <summary>A block from block storage. Test seam.</summary>
        internal static Func<long, Block?> BlockAt = BlockchainData.GetBlockByHeight;

        /// <summary>
        /// One election: who votes, who stands, and the order they stand in. E (a stall at H) is seeded from the epoch's
        /// anchor block: the producer of H does not choose it and neither does whoever picks the moment of the stall.
        /// E' (the rotation ending an elected committee's term) is seeded from the anchor of the epoch before the stall,
        /// final before that committee existed, and a day from the anchor that seeded its rungs, so grinding one does not
        /// buy both. Its candidates are read at the lapse, so the old committee's survivors stand again.
        /// </summary>
        public sealed class ElectionEpoch
        {
            public string ChangeType { get; init; } = "";
            public string EpochHash { get; init; } = "";
            public string Seed { get; init; } = "";
            public long Anchor { get; init; }
            public long BondHeight { get; init; }
            public HashSet<string> Excluded { get; init; } = new(StringComparer.Ordinal);
            /// <summary>V(E), address to its chain entry.</summary>
            public Dictionary<string, VBTCValidator> Electors { get; init; } = new(StringComparer.Ordinal);
            /// <summary>C(E) in rank order.</summary>
            public List<string> Ranked { get; init; } = new();

            public int RatifyNeeded => RatifyNeed(Electors.Count);
            public bool Dark => Electors.Count < MinElectors;

            /// <summary>The record of rung <paramref name="rung"/> as this node computes it. There is no other valid one.</summary>
            public CasterMembershipRecord Build(CasterMembershipRecord prev, int rung, long effective)
            {
                var record = new CasterMembershipRecord
                {
                    RecordSeq = prev.RecordSeq + 1,
                    EffectiveFromHeight = effective,
                    PrevRecordHash = prev.RecordHash,
                    ChangeType = ChangeType,
                    ChangedAddress = "",
                    Casters = RungMembers(Ranked, rung).Select(CasterEntry).OrderBy(c => c.Address, StringComparer.Ordinal).ToList(),
                    EpochHash = EpochHash,
                    Rung = rung,
                    SchemeVersion = SchemeVersionV1,
                    LapseHeight = ChangeType == EmergencyElectionChangeType ? effective + TermBlocks : 0,
                };
                record.RecordHash = CasterMembershipStore.ComputeRecordHash(record);
                return record;
            }

            private CasterInfo CasterEntry(string address)
            {
                var v = Electors[address];
                return new CasterInfo
                {
                    Address = address,
                    PeerIP = NormalizedIp(v.IPAddress),
                    // The key the validator registered under, when it is the address's key; the casters fetch it otherwise.
                    PublicKey = NetworkValidator.BoundPublicKey(address, v.FrostPublicKey),
                };
            }
        }

        private static string NormalizedIp(string? ip) => (ip ?? "").Replace("::ffff:", "");

        /// <summary>A candidate must be somewhere: an address no node can dial, or that would break the signed payload, would only cost its rung.</summary>
        internal static bool IsDialable(string? ip) => System.Net.IPAddress.TryParse(NormalizedIp(ip), out _);

        private sealed record CachedAnchor(long Anchor, string? Hash, List<VBTCValidator> Active);
        private static volatile CachedAnchor? _anchorCache;

        /// <summary>
        /// The active set at an anchor, scanned once an epoch. The registry keeps one window of block summaries, and a scan this
        /// far below the tip evicts the tip's: every consensus read after it would read a thousand whole blocks again.
        /// </summary>
        private static List<VBTCValidator> ActiveAtAnchor(long anchor)
        {
            var hash = VrfSeed.BlockHashAt(anchor);
            var cached = _anchorCache;
            if (cached != null && cached.Anchor == anchor && cached.Hash == hash) return cached.Active;
            var active = VrfSeed.ActiveAt(anchor);
            _anchorCache = new CachedAnchor(anchor, hash, active);
            return active;
        }

        private sealed record CachedEpoch(string Key, long At, ElectionEpoch Epoch);
        private static volatile CachedEpoch? _epochCache;
        /// <summary>Seconds a computed epoch is reused: a signing tick and every collected signature read it.</summary>
        private const long EpochCacheSeconds = 30;

        internal static void ClearCachesForTests()
        {
            _epochCache = null;
            _anchorCache = null;
            _carriedVerdicts.Clear();
            _ratifiedSeen.Clear();
            _ratifiedNoted.Clear();
        }

        private static ElectionEpoch? BuildEpoch(string changeType, string epochHash, string? seed, long anchor, long bondHeight, HashSet<string> excluded)
        {
            if (string.IsNullOrEmpty(seed) || anchor < 0)
                return null;
            var key = $"{changeType}|{epochHash}|{seed}|{anchor}|{bondHeight}|{CasterMembershipStore.ComputeCasterSetHash(excluded)}";
            var now = TimeUtil.GetTime();
            var cached = _epochCache;
            if (cached != null && cached.Key == key && now - cached.At < EpochCacheSeconds)
                return cached.Epoch;

            // The IP and key as of the bond height, for one that is still heartbeating; as of the anchor otherwise.
            var latest = VrfSeed.ActiveAt(bondHeight).Where(v => !string.IsNullOrEmpty(v.ValidatorAddress))
                .GroupBy(v => v.ValidatorAddress, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var electors = new Dictionary<string, VBTCValidator>(StringComparer.Ordinal);
            foreach (var v in ActiveAtAnchor(anchor))
            {
                if (string.IsNullOrEmpty(v.ValidatorAddress) || v.RegistrationBlockHeight >= anchor || excluded.Contains(v.ValidatorAddress))
                    continue;
                if (!ElectorEligible(v.ValidatorAddress, bondHeight))
                    continue;
                electors[v.ValidatorAddress] = latest.TryGetValue(v.ValidatorAddress, out var current) && !string.IsNullOrEmpty(current.IPAddress) ? current : v;
            }
            var epoch = new ElectionEpoch
            {
                ChangeType = changeType, EpochHash = epochHash, Seed = seed!, Anchor = anchor, BondHeight = bondHeight, Excluded = excluded, Electors = electors,
                Ranked = Ranked(seed!, electors.Where(e => IsDialable(e.Value.IPAddress)).Select(e => e.Key)),
            };
            _epochCache = new CachedEpoch(key, now, epoch);
            return epoch;
        }

        /// <summary>E: the election for a stall at <paramref name="stallHeight"/>. Null when the record era or the VRF epochs have not started.</summary>
        public static ElectionEpoch? EpochForStall(long stallHeight, string stallHash)
        {
            if (!VrfSeed.Active(stallHeight))
                return null;
            var committee = CasterMembershipStore.GetCommitteeForHeight(stallHeight);
            if (committee == null)
                return null;
            var anchor = VrfSeed.AnchorHeight(stallHeight);
            return BuildEpoch(EmergencyElectionChangeType, stallHash, VrfSeed.BlockHashAt(anchor), anchor, stallHeight, committee);
        }

        /// <summary>E': the rotation ending the term <paramref name="term"/> started, replacing the committee of <paramref name="current"/>.</summary>
        public static ElectionEpoch? EpochForRotation(CasterMembershipRecord term, CasterMembershipRecord current)
        {
            if (term.ChangeType != EmergencyElectionChangeType || term.LapseHeight <= 0)
                return null;
            var stall = term.EffectiveFromHeight - 1;
            if (!VrfSeed.Active(term.LapseHeight))
                return null;
            var lapseHash = VrfSeed.BlockHashAt(term.LapseHeight);
            if (string.IsNullOrEmpty(lapseHash))
                return null;
            var seedHeight = Math.Max(0, VrfSeed.AnchorHeight(stall) - Globals.VrfEpochBlocks);
            var interimSet = current.Casters.Select(c => c.Address).ToHashSet(StringComparer.Ordinal);
            return BuildEpoch(ScheduledRotationChangeType, lapseHash!, VrfSeed.BlockHashAt(seedHeight), VrfSeed.AnchorHeight(term.LapseHeight), term.LapseHeight, interimSet);
        }

        /// <summary>
        /// The election record whose term governs <paramref name="record"/>, or null. A term is the chain's, not one record's:
        /// an elected committee's ordinary rotations and holds stay under it. Otherwise one ordinary demotion, which has no
        /// lapse of its own, would end the term limit for a captured committee. A rotation or a seed reset ends the term.
        /// </summary>
        public static CasterMembershipRecord? TermOf(CasterMembershipRecord? record, Func<long, CasterMembershipRecord?> bySeq)
        {
            for (var r = record; r != null; r = bySeq(r.RecordSeq - 1))
            {
                if (r.ChangeType == EmergencyElectionChangeType) return r.LapseHeight > 0 ? r : null;
                if (r.ChangeType is ScheduledRotationChangeType or CasterMembershipStore.BootstrapResetChangeType or "Genesis") return null;
            }
            return null;
        }

        /// <summary>The reach set of a caster: the eligible set minus its own committee, as of its tip.</summary>
        public static ElectionEpoch? ReachSetFor(long tipHeight, string tipHash, HashSet<string> committee)
        {
            if (!VrfSeed.Active(tipHeight))
                return null;
            var anchor = VrfSeed.AnchorHeight(tipHeight);
            return BuildEpoch("Reach", "", VrfSeed.BlockHashAt(anchor), anchor, tipHeight - tipHeight % 100, committee);
        }

        // ── What is open at this node's tip ─────────────────────────────────────────────────────

        /// <summary>The election open at a node's tip, and on what clock.</summary>
        public sealed class ElectionView
        {
            public ElectionEpoch Epoch { get; init; } = null!;
            /// <summary>The record every rung of this election succeeds.</summary>
            public CasterMembershipRecord Prev { get; init; } = null!;
            public long TipHeight { get; init; }
            public string TipHash { get; init; } = "";
            public long TipAgeSeconds { get; init; }
            /// <summary>The rung open now by this node's clock.</summary>
            public int ClockRung { get; init; }
            /// <summary>A rotation's rung 0 opens at the lapse and needs no stall.</summary>
            public bool IsRotation => Epoch.ChangeType == ScheduledRotationChangeType;
            /// <summary>The height the term ends, for a rotation.</summary>
            public long LapseHeight { get; init; }

            public long EffectiveFor(int rung) => IsRotation && rung == 0 ? LapseHeight + HandoverBlocks : TipHeight + 1;

            /// <summary>Why rung <paramref name="rung"/> cannot exist at this tip, or null.</summary>
            public string? RungClosed(int rung)
            {
                if (rung < 0) return "no such rung";
                if (IsRotation && rung == 0 && TipHeight >= LapseHeight + HandoverBlocks) return "rung 0 of the rotation would take effect at or below the tip";
                if ((!IsRotation || rung > 0) && TipAgeSeconds < ElectSeconds) return $"tip is only {TipAgeSeconds}s old";
                return null;
            }

            public CasterMembershipRecord Build(int rung) => Epoch.Build(Prev, rung, EffectiveFor(rung));
        }

        /// <summary>The rung open at a tip age. A rotation's rung 0 is open from the lapse; its later rungs start on the stall clock, one up.</summary>
        public static int ClockRungAt(long tipAgeSeconds, bool rotation)
        {
            if (tipAgeSeconds < ElectSeconds) return 0;
            var rung = (int)Math.Min((tipAgeSeconds - ElectSeconds) / RungSeconds, int.MaxValue - 1);
            return rotation ? rung + 1 : rung;
        }

        /// <summary>
        /// The election open at this tip: the rotation from an elected committee's lapse, otherwise an emergency once the
        /// tip is stale. Rungs of one election are siblings: they all succeed the record below any rung already installed.
        /// </summary>
        public static ElectionView? ViewAt(CasterMembershipRecord? head, long tipHeight, string? tipHash, long tipAgeSeconds, Func<long, CasterMembershipRecord?> bySeq)
        {
            if (head == null || tipHeight <= 0 || string.IsNullOrEmpty(tipHash))
                return null;

            // A rotation not in force yet leaves the record below it governing: its later rungs are its siblings.
            var governing = head.ChangeType == ScheduledRotationChangeType && head.EffectiveFromHeight > tipHeight ? bySeq(head.RecordSeq - 1) : head;
            var term = TermOf(governing, bySeq);
            if (term != null && tipHeight >= term.LapseHeight)
            {
                var rotation = EpochForRotation(term, governing!);
                if (rotation == null) return null;
                return new ElectionView
                {
                    Epoch = rotation, Prev = governing!, TipHeight = tipHeight, TipHash = tipHash!, TipAgeSeconds = tipAgeSeconds, LapseHeight = term.LapseHeight,
                    ClockRung = ClockRungAt(tipAgeSeconds, rotation: true),
                };
            }

            if (tipAgeSeconds < ElectSeconds)
                return null;
            var epoch = EpochForStall(tipHeight, tipHash!);
            if (epoch == null) return null;
            // Rung r+1 of this stall is a sibling of rung r, and outranks a seed reset of this stall (the reset's sibling too).
            var prev = head.EffectiveFromHeight > tipHeight && (head.ChangeType == CasterMembershipStore.BootstrapResetChangeType
                       || (head.ChangeType == EmergencyElectionChangeType && head.EpochHash == tipHash))
                ? bySeq(head.RecordSeq - 1) : head;
            if (prev == null) return null;
            return new ElectionView
            {
                Epoch = epoch, Prev = prev, TipHeight = tipHeight, TipHash = tipHash!, TipAgeSeconds = tipAgeSeconds,
                ClockRung = ClockRungAt(tipAgeSeconds, rotation: false),
            };
        }

        // ── Signing rules ───────────────────────────────────────────────────────────────────────

        /// <summary>What a validator knows when it decides whether to sign. Everything but the view comes from its own state.</summary>
        public sealed class SignFacts
        {
            /// <summary>This validator's address is in the electorate.</summary>
            public bool IsElector { get; init; }
            /// <summary>A block above its tip, or another block at it, was seen with a certificate that verifies.</summary>
            public bool VerifiedEvidenceAgainstTip { get; init; }
            /// <summary>Its own fresh probe found fewer than a quorum of the committee casting at the tip.</summary>
            public bool CommitteeUnreachable { get; init; }
            /// <summary>The probe of the committee and the pull of the record ran for this tip.</summary>
            public bool SurveyFresh { get; init; }
            public bool HoldActive { get; init; }
            /// <summary>It saw a ratified rung for this election within <see cref="RatifiedHoldSeconds"/>.</summary>
            public bool RatifiedHold { get; init; }
        }

        /// <summary>Why a validator may not sign rung <paramref name="rung"/> of the election in view, or null. Each rule answers for itself.</summary>
        public static string? SignRefusal(ElectionView view, int rung, SignFacts facts)
        {
            if (!Active(view.EffectiveFor(Math.Max(rung, 0))))
                return "election not active at this height";
            if (view.Epoch.Dark)
                return $"electorate of {view.Epoch.Electors.Count} is below {MinElectors}";
            if (!facts.IsElector)
                return "this validator is not an elector";
            if (view.RungClosed(rung) is { } closed)
                return $"rule 1: {closed}";
            if (!facts.SurveyFresh)
                return "rule 8: survey of the network is not fresh yet";
            if (facts.VerifiedEvidenceAgainstTip)
                return "rule 2: a certified block above or beside this tip was verified";
            if (!view.IsRotation && !facts.CommitteeUnreachable)
                return "rule 3: the committee is reachable";
            if (facts.HoldActive)
                return "rule 4: a maintenance hold is active";
            if (Math.Abs((long)rung - view.ClockRung) > 1)
                return $"rule 6: rung {rung} is not within one of the rung open now ({view.ClockRung})";
            if (facts.RatifiedHold)
                return "rule 7: a ratified rung was seen within the hold";
            return null;
        }

        /// <summary>Pure: rule 3 from a sweep of Health. Reachable means answering, casting and at the tip: a hung round loop still answers HTTP.</summary>
        public static bool CommitteeUnreachable(IEnumerable<NetworkStallSurvey.Node> known, IEnumerable<NetworkStallSurvey.Reply> replies, long tipHeight, int quorum)
        {
            var committee = known.Where(k => k.IsCommittee).Select(k => k.Ip).ToHashSet(StringComparer.Ordinal);
            var casting = replies.Where(r => committee.Contains(r.Ip) && r.Casting && r.Height == tipHeight)
                .Select(r => r.Ip).Distinct(StringComparer.Ordinal).Count();
            return casting < quorum;
        }

        // ── Maintenance hold ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Whether a hold keeps this node from signing. It runs on this node's clock against the timestamp of the block
        /// before its effective height (the tip, when the chain stopped first). Of the holds issued at a stalled tip only
        /// the first counts, and each is capped at <see cref="MaxHoldSeconds"/>, so a live committee cannot chain them past that.
        /// </summary>
        public static bool HoldActive(IEnumerable<CasterMembershipRecord> records, long tipHeight, long tipTimestamp, long nowUnix, Func<long, Block?> blockAt)
        {
            var holds = records.Where(IsHoldRecord).OrderBy(r => r.RecordSeq).ToList();
            var firstAtTip = holds.FirstOrDefault(h => h.EffectiveFromHeight - 1 >= tipHeight);
            foreach (var hold in holds)
            {
                var anchor = hold.EffectiveFromHeight - 1;
                if (anchor >= tipHeight && !ReferenceEquals(hold, firstAtTip))
                    continue;
                var anchorTime = anchor >= tipHeight ? tipTimestamp : blockAt(anchor)?.Timestamp;
                if (anchorTime != null && nowUnix - anchorTime < Math.Min(hold.HoldSeconds, MaxHoldSeconds))
                    return true;
            }
            return false;
        }

        /// <summary>Why a hold record is not one (it changes nobody, and it is short), or null. Its signatures are the previous set's, as for any record.</summary>
        internal static string? HoldShapeRefusal(CasterMembershipRecord prev, CasterMembershipRecord candidate)
        {
            if (!Active(candidate.EffectiveFromHeight)) return "maintenance holds are not active at this height";
            if (candidate.HoldSeconds <= 0 || candidate.HoldSeconds > MaxHoldSeconds) return $"hold of {candidate.HoldSeconds}s is outside 1..{MaxHoldSeconds}";
            if (!string.IsNullOrEmpty(candidate.ChangedAddress)) return "a hold changes nobody";
            if (candidate.SchemeVersion != SchemeVersionV1) return "unknown signature scheme";
            if (candidate.LapseHeight != 0) return "a hold has no term";
            if (candidate.Casters.Count != prev.Casters.Count) return "a hold keeps the committee as it is";
            foreach (var c in candidate.Casters)
            {
                var same = prev.Casters.FirstOrDefault(p => p.Address == c.Address);
                if (same == null || same.PeerIP != c.PeerIP || same.PublicKey != c.PublicKey) return "a hold keeps the committee as it is";
            }
            return null;
        }

        // ── Term limit ──────────────────────────────────────────────────────────────────────────

        /// <summary>Whether a block at <paramref name="height"/> is past the term of the committee that governs it: its certificates no longer count.</summary>
        public static bool Lapsed(CasterMembershipRecord? governing, long height, Func<long, CasterMembershipRecord?> bySeq) =>
            TermOf(governing, bySeq) is { } term && Active(term.EffectiveFromHeight) && height > term.LapseHeight + HandoverBlocks;

        /// <summary>
        /// Why an ordinary record may not succeed <paramref name="prev"/>, or null. From its lapse a committee changes nothing
        /// and takes effect nowhere past it: every new record would change what the electors sign, so a captured committee
        /// issuing one a minute could keep the rotation that ends its term from ever reaching two thirds.
        /// </summary>
        internal static string? TermRefusal(CasterMembershipRecord prev, CasterMembershipRecord candidate, long tipHeight, Func<long, CasterMembershipRecord?> bySeq)
        {
            if (TermOf(prev, bySeq) is not { } term || !Active(term.EffectiveFromHeight)) return null;
            if (candidate.EffectiveFromHeight > term.LapseHeight) return $"takes effect past the end of this committee's term at {term.LapseHeight}";
            // Only while the rotation is open. A node far past it, rebuilding its record chain from peers, needs what the committee signed during its term.
            return tipHeight >= term.LapseHeight && tipHeight <= term.LapseHeight + HandoverBlocks ? $"this committee's term ended at {term.LapseHeight}" : null;
        }

        /// <summary>
        /// Whether <paramref name="head"/> has not taken over yet and members must not act on it: a rotation replaces the
        /// whole committee a dozen blocks ahead, and the outgoing members produce until then. Every other record acts at
        /// once, as records always did: its members are mostly the same people.
        /// </summary>
        public static bool RotationPending(CasterMembershipRecord? head, long tipHeight) =>
            head?.ChangeType == ScheduledRotationChangeType && head.EffectiveFromHeight > tipHeight + 1;

        /// <summary>
        /// A rung's members are new to the seat: nobody can approve their resume, because the others wait for the same.
        /// True only until the record's first block is committed; after that a restarted member resumes as any other.
        /// </summary>
        public static bool FreshlyElected(CasterMembershipRecord? head, long tipHeight, string? self) =>
            IsElectionRecord(head) && !string.IsNullOrEmpty(self) && tipHeight + 1 == head!.EffectiveFromHeight
            && Active(head.EffectiveFromHeight) && head.Casters.Any(c => c.Address == self);

        // ── Validating a record ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Whether the set and the signatures are the ones this epoch gives: the record recomputes entry for entry, and two
        /// thirds of the electorate signed its payload. Nothing here reads a clock.
        /// </summary>
        internal static string? ContentRefusal(ElectionEpoch? epoch, CasterMembershipRecord prev, CasterMembershipRecord c)
        {
            if (epoch == null) return "the election's seed or committee cannot be read here";
            if (epoch.Dark) return $"electorate of {epoch.Electors.Count} is below {MinElectors}";
            if (c.Rung < 0) return "no such rung";
            if (c.Casters == null || c.Casters.Count < CasterMembershipStore.MinCommitteeSize) return "the rung has too few members";
            if (c.SchemeVersion != SchemeVersionV1) return "unknown signature scheme";
            if (!string.Equals(c.PrevRecordHash, prev.RecordHash, StringComparison.OrdinalIgnoreCase)) return "prevRecordHash mismatch";
            if (!string.Equals(c.EpochHash, epoch.EpochHash, StringComparison.OrdinalIgnoreCase)) return "epoch hash mismatch";
            if (epoch.Build(prev, c.Rung, c.EffectiveFromHeight).RecordHash != c.RecordHash || CasterMembershipStore.ComputeRecordHash(c) != c.RecordHash)
                return "record is not the rung this chain gives";

            // A collector puts on the record exactly the signatures it verified (CollectRungAsync), so an honest record
            // carries as many as ratify, each valid, distinct and an elector's. Anything else is refused at the first one:
            // the record is public and a verify costs milliseconds, so a copy with junk signatures, from anyone through
            // ProbeTip or AnnounceMembershipRecord, would otherwise cost one verify per elector on every validator.
            var need = epoch.RatifyNeeded;
            var signatures = c.Signatures ?? new List<RecordSignature>();
            if (signatures.Count > need) return $"{signatures.Count} signatures, more than the {need} that ratify";
            var payload = CasterMembershipStore.CanonicalPayload(c);
            var valid = new HashSet<string>(StringComparer.Ordinal);
            foreach (var sig in signatures)
            {
                if (string.IsNullOrEmpty(sig?.SignerAddress) || string.IsNullOrEmpty(sig.Signature)) return "an empty signature";
                if (!epoch.Electors.ContainsKey(sig.SignerAddress)) return $"a signature from {sig.SignerAddress}, which is not an elector";
                if (!valid.Add(sig.SignerAddress)) return $"two signatures from {sig.SignerAddress}";
                if (!SignatureService.VerifySignature(sig.SignerAddress, payload, sig.Signature)) return $"an invalid signature from {sig.SignerAddress}";
            }
            return valid.Count >= need ? null : $"signatures {valid.Count}/{need} from the electorate";
        }

        /// <summary>
        /// A live append of an election or rotation record: bound to this node's own tip and stall, and no probe of the
        /// committee (reach is a per-vantage fact, and the signatures are the evidence). A node past the record takes it
        /// only as its own chain carries it: the block that carries it was judged when it was validated.
        /// </summary>
        public static bool ValidateElection(CasterMembershipRecord prev, CasterMembershipRecord c, out string reason)
        {
            reason = "";
            if (!Active(c.EffectiveFromHeight)) { reason = "election records are not active at this height"; return false; }
            if (c.Casters == null) { reason = "no caster set"; return false; }
            if (CasterMembershipStore.ComputeRecordHash(c) != c.RecordHash) { reason = "recordHash mismatch"; return false; }
            var tip = Globals.LastBlock;
            if (tip == null || tip.Height < 0) { reason = "no tip"; return false; }

            if (tip.Height >= c.EffectiveFromHeight)
            {
                if (OwnChainCarries(c)) return true;
                reason = $"effective height {c.EffectiveFromHeight} is at or below the tip {tip.Height}, and this chain's block there is not this record's";
                return false;
            }

            reason = BindingRefusal(prev, c, tip.Height, tip.Hash, StallSeconds(), VrfSeed.BlockHashAt, CasterMembershipStore.GetBySeq) ?? "";
            if (reason.Length > 0) return false;
            reason = ContentRefusal(EpochFor(prev, c), prev, c) ?? "";
            return reason.Length == 0;
        }

        /// <summary>
        /// Whether a record is the one this node's block at its effective height is under. The block carries it, or (a bulk
        /// sync took it from a node with no record store, and no certificate was required there) it does not but the record
        /// judges valid on it and its certificate is that committee's: a node in that state would otherwise refuse every
        /// block at the tip for good, with nothing an operator could do but resync.
        /// </summary>
        private static bool OwnChainCarries(CasterMembershipRecord c)
        {
            var block = BlockAt(c.EffectiveFromHeight);
            if (block == null) return false;
            if (block.MembershipRecord?.RecordHash == c.RecordHash) return true;
            var withRecord = new Block
            {
                Height = block.Height, Hash = block.Hash, PrevHash = block.PrevHash, Validator = block.Validator, Version = block.Version,
                Timestamp = block.Timestamp, ConsensusCertificate = block.ConsensusCertificate, MembershipRecord = c,
            };
            return JudgeCarried(withRecord) == CarriedVerdict.Valid && withRecord.ConsensusCertificate != null && ConsensusCertificateVerifier.VerifyOrNotRequired(withRecord);
        }

        private static ElectionEpoch? EpochFor(CasterMembershipRecord prev, CasterMembershipRecord c) =>
            c.ChangeType == EmergencyElectionChangeType
                ? EpochForStall(c.EffectiveFromHeight - 1, c.EpochHash)
                : TermOf(prev, CasterMembershipStore.GetBySeq) is { } term ? EpochForRotation(term, prev) : null;

        /// <summary>Pure: the own-tip and own-stall binding of a live append.</summary>
        internal static string? BindingRefusal(CasterMembershipRecord prev, CasterMembershipRecord c, long tipHeight, string? tipHash, long tipAgeSeconds, Func<long, string?> hashAt, Func<long, CasterMembershipRecord?> bySeq)
        {
            if (c.ChangeType == EmergencyElectionChangeType)
            {
                if (c.EffectiveFromHeight != tipHeight + 1) return $"effective height {c.EffectiveFromHeight} is not our tip + 1 ({tipHeight + 1})";
                if (!string.Equals(c.EpochHash, tipHash, StringComparison.OrdinalIgnoreCase)) return "the election is for another block than our tip";
                if (tipAgeSeconds < ElectSeconds) return $"our tip is only {tipAgeSeconds}s old (need {ElectSeconds}s); the chain is not stalled here";
                return null;
            }

            if (TermOf(prev, bySeq) is not { } term) return "a rotation follows an elected committee only";
            if (tipHeight < term.LapseHeight) return $"our tip {tipHeight} has not reached the lapse at {term.LapseHeight}";
            if (!string.Equals(c.EpochHash, hashAt(term.LapseHeight), StringComparison.OrdinalIgnoreCase)) return "the rotation is for another block than this chain's lapse block";
            if (c.Rung == 0)
                return c.EffectiveFromHeight == term.LapseHeight + HandoverBlocks ? null : "rung 0 of a rotation takes effect at the lapse + the handover";
            if (c.EffectiveFromHeight != tipHeight + 1) return $"effective height {c.EffectiveFromHeight} is not our tip + 1 ({tipHeight + 1})";
            return tipAgeSeconds < ElectSeconds ? $"our tip is only {tipAgeSeconds}s old (need {ElectSeconds}s)" : null;
        }

        // ── The record a block carries ──────────────────────────────────────────────────────────

        public enum CarriedVerdict { None, Valid, Invalid, CannotJudge }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CarriedVerdict> _carriedVerdicts = new(StringComparer.Ordinal);
        private const int CarriedVerdictCacheSize = 64;

        private static string CarriedKey(Block block, CasterMembershipRecord r)
        {
            // The signatures are outside the record's hash, so they are part of the key: a copy with junk ones is its own question.
            using var sha = SHA256.Create();
            var signatures = string.Join(",", (r.Signatures ?? new()).Select(s => $"{s?.SignerAddress}:{s?.Signature}"));
            return $"{block.Hash}|{r.RecordHash}|{Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(signatures)))}";
        }

        /// <summary>
        /// Whether the record a block carries is a ratified election for that block: every node at the block's parent
        /// reaches the same answer, on any tip above it, so it reads block time and never a clock. The record chain below
        /// it must already be here (CannotJudge otherwise: the caller refuses the block for now and pulls).
        /// </summary>
        public static CarriedVerdict JudgeCarried(Block block)
        {
            var r = block?.MembershipRecord;
            if (r == null) return CarriedVerdict.None;
            if (!Active(block!.Height) || !IsElectionRecord(r) || r.EffectiveFromHeight != block.Height || r.Casters == null) return CarriedVerdict.Invalid;
            // Before the cache: the key names the record by its claimed hash, so the fields must be shown to be what it hashes.
            if (CasterMembershipStore.FieldHoldingTheSeparator(r) != null || CasterMembershipStore.ComputeRecordHash(r) != r.RecordHash) return CarriedVerdict.Invalid;
            var key = CarriedKey(block, r);
            if (_carriedVerdicts.TryGetValue(key, out var known)) return known;

            var verdict = JudgeCarriedUncached(block, r);
            if (verdict != CarriedVerdict.CannotJudge)
            {
                if (_carriedVerdicts.Count >= CarriedVerdictCacheSize) _carriedVerdicts.Clear();
                _carriedVerdicts[key] = verdict;
            }
            return verdict;
        }

        private static CarriedVerdict JudgeCarriedUncached(Block block, CasterMembershipRecord r)
        {
            var prev = CasterMembershipStore.GetBySeq(r.RecordSeq - 1);
            var parent = BlockAt(block.Height - 1);
            if (parent == null || parent.Hash != block.PrevHash) return CarriedVerdict.CannotJudge;
            if (prev == null) return CarriedVerdict.CannotJudge; // the record chain below it is not here yet
            if (!string.Equals(r.PrevRecordHash, prev.RecordHash, StringComparison.OrdinalIgnoreCase)) return CarriedVerdict.Invalid;
            // No check of the stall in block time: a validator crafts its next block once, just after the tip, so the block
            // that ends a stall is dated from before it. Two thirds of the electorate each checked the stall by their own clock.
            if (r.ChangeType == EmergencyElectionChangeType)
            {
                if (!string.Equals(r.EpochHash, block.PrevHash, StringComparison.OrdinalIgnoreCase)) return CarriedVerdict.Invalid;
            }
            else
            {
                var term = TermOf(prev, CasterMembershipStore.GetBySeq);
                if (term == null || block.Height <= term.LapseHeight) return CarriedVerdict.Invalid;
                if (!string.Equals(r.EpochHash, VrfSeed.BlockHashAt(term.LapseHeight), StringComparison.OrdinalIgnoreCase)) return CarriedVerdict.Invalid;
                if (r.Rung == 0 && block.Height != term.LapseHeight + HandoverBlocks) return CarriedVerdict.Invalid;
            }
            return ContentRefusal(EpochFor(prev, r), prev, r) == null ? CarriedVerdict.Valid : CarriedVerdict.Invalid;
        }

        /// <summary>The committee of a block that carries a valid record, or null: it needs no record held here to be judged.</summary>
        public static HashSet<string>? CarriedCommittee(Block? block)
        {
            if (block?.MembershipRecord == null || JudgeCarried(block) != CarriedVerdict.Valid) return null;
            return block.MembershipRecord.Casters.Select(c => c.Address).ToHashSet(StringComparer.Ordinal);
        }

        /// <summary>The rung a block's valid record installs, when its certificate is that committee's, or null: Layer 3 of fork choice. A record alone is public, and anyone could put it on a block.</summary>
        public static int? CarriedRung(Block? block) =>
            block?.MembershipRecord != null && JudgeCarried(block) == CarriedVerdict.Valid && ConsensusCertificateVerifier.VerifyOrNotRequired(block)
                ? block.MembershipRecord.Rung : null;

        /// <summary>Puts the record a block at its effective height carries on the block, when this node holds it.</summary>
        public static void AttachRecord(Block block)
        {
            if (block == null || block.MembershipRecord != null || !Active(block.Height)) return;
            var governing = CasterMembershipStore.GetForHeight(block.Height);
            if (IsElectionRecord(governing) && governing!.EffectiveFromHeight == block.Height)
                block.MembershipRecord = governing;
        }

        /// <summary>
        /// Before a block's certificate is judged. Below the height no block carries a record. A record that is not valid is
        /// removed: it is outside the block's hash, so anyone can put one on a good block, and the block must then pass or
        /// fail on its own. A block at the effective height of a record held here is given it: its certificate must then be
        /// that committee's, and what is stored and served carries it. Returns why the block must wait, or null.
        /// </summary>
        public static string? PrepareBlockRecord(Block block)
        {
            if (!Active(block.Height)) { block.MembershipRecord = null; return null; }
            switch (JudgeCarried(block))
            {
                case CarriedVerdict.Valid:
                    return null;
                case CarriedVerdict.CannotJudge:
                    // A wallet or explorer node has no record store and requires no certificate. It keeps what it was given:
                    // the record is outside the block's hash, and whoever syncs from it judges the record for itself.
                    if (string.IsNullOrEmpty(Globals.ValidatorAddress) && CasterMembershipStore.GetCurrent() == null)
                        return null;
                    // Anyone can put a record on a good block. One whose certificate holds without it needs no record. "Holds"
                    // is strict: on a node with no record no certificate is required, and it would drop what it must keep.
                    var carried = block.MembershipRecord;
                    block.MembershipRecord = null;
                    if (ConsensusCertificateVerifier.HasValidCertificate(block))
                        return null;
                    block.MembershipRecord = carried;
                    ValidatorMembershipPull.RequestSoon();
                    return $"the record block {block.Height} carries extends a record this node does not hold yet";
                case CarriedVerdict.Invalid:
                    CasterLogUtility.Log($"VREE: block {block.Height} carried a record that is not valid; removed.", "VREE");
                    block.MembershipRecord = null;
                    break;
            }
            AttachRecord(block);
            return null;
        }

        /// <summary>Why a reorg onto <paramref name="remote"/> at the divergence must not cross the ratified record this node's chain is under, or null.</summary>
        public static string? CrossingRefusal(CasterMembershipRecord? head, long divergenceHeight, long tipHeight, Block remote, Func<long, CasterMembershipRecord?> bySeq)
        {
            // The newest election at or above the divergence that this chain has started, however many ordinary records followed it.
            for (var r = head; r != null && r.EffectiveFromHeight >= divergenceHeight; r = bySeq(r.RecordSeq - 1))
            {
                if (!IsElectionRecord(r) || !Active(r.EffectiveFromHeight) || r.EffectiveFromHeight > tipHeight) continue;
                if (CarriedRung(remote) is { } rung && rung <= r.Rung) return null;
                return $"this chain is under the ratified record seq {r.RecordSeq} from height {r.EffectiveFromHeight}, and the other branch diverges at {divergenceHeight} without a record that outranks it";
            }
            return null;
        }

        /// <summary>A seed reset may replace a rung that was ratified and never started, once this long has shown no rung can follow it (too few electors left alive).</summary>
        public const long ResetAfterElectionSeconds = 1_200;

        /// <summary>
        /// Whether a seed reset of the same predecessor may take the place of this record: a sibling, so the rung that never
        /// started leaves the record chain altogether. On top of it the reset would strand every node that does not hold
        /// that one rung, and no block would ever carry it. The election outranks the reset until the time has passed.
        /// </summary>
        public static bool ResetMayReplace(CasterMembershipRecord head, CasterMembershipRecord reset, long tipHeight, long tipAgeSeconds) =>
            ElectionNotStarted(head, tipHeight) && reset.ChangeType == CasterMembershipStore.BootstrapResetChangeType
            && reset.RecordSeq == head.RecordSeq && reset.EffectiveFromHeight == tipHeight + 1
            && string.Equals(reset.PrevRecordHash, head.PrevRecordHash, StringComparison.OrdinalIgnoreCase)
            && tipAgeSeconds >= ResetAfterElectionSeconds;

        /// <summary>Whether a record is a ratified rung that has not produced its first block.</summary>
        public static bool ElectionNotStarted(CasterMembershipRecord? record, long tipHeight) =>
            IsElectionRecord(record) && record!.EffectiveFromHeight > tipHeight && Active(record.EffectiveFromHeight);

        // ── Seen ratified ───────────────────────────────────────────────────────────────────────

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _ratifiedSeen = new(StringComparer.Ordinal);

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _ratifiedNoted = new(StringComparer.Ordinal);

        /// <summary>This node saw a valid ratified record for the election bound to this hash. Each record starts the hold once: replaying an old one must not extend it for ever.</summary>
        public static void NoteRatified(string? epochHash, string? recordHash, long? nowUnix = null)
        {
            if (string.IsNullOrEmpty(epochHash) || string.IsNullOrEmpty(recordHash)) return;
            if (_ratifiedSeen.Count > 64) { _ratifiedSeen.Clear(); _ratifiedNoted.Clear(); }
            if (_ratifiedNoted.TryAdd(recordHash, 1))
                _ratifiedSeen[epochHash] = nowUnix ?? TimeUtil.GetTime();
        }

        /// <summary>Whether a ratified rung of this election was seen within the hold. A record held from before a restart counts from the restart.</summary>
        public static bool WithinRatifiedHold(CasterMembershipRecord? head, string epochHash, long nowUnix)
        {
            if (IsElectionRecord(head) && head!.EpochHash == epochHash && !_ratifiedSeen.ContainsKey(epochHash))
                _ratifiedSeen[epochHash] = nowUnix;
            return _ratifiedSeen.TryGetValue(epochHash, out var seen) && nowUnix - seen < RatifiedHoldSeconds;
        }
    }
}
