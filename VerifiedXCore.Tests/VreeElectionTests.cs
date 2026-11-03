using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// The validator-ratified emergency election (docs/PLAN-Caster-Majority-Outage-Recovery.md). Test names in snake case are
    /// the outside simulator's (vfx-vree-sim), so the two stay comparable. Everything below runs on real keys, real
    /// signatures and the real record store; one process stands in for many nodes through the seams of VreeService.
    /// </summary>
    [Collection("DbContextSequential")]
    public class VreeElectionTests : IDisposable
    {
        private const long SeedHeight = 1_000;
        private const long H = 20_000;
        private const long HeadSeq = 3;

        private sealed record Member(PrivateKey Key, string Pub, string Address, string Ip);

        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly bool _priorSynced = Globals.IsChainSynced;
        private readonly bool _priorCaster = Globals.IsBlockCaster;
        private readonly string _priorValidator = Globals.ValidatorAddress;
        private readonly long _priorAdded = Globals.LastBlockAddedTimestamp;
        private readonly long _priorCertEnforce = Globals.CertEnforceHeight;
        private readonly long _priorSeedHeight = Globals.VrfSeedHeight;
        private readonly long _priorActivation = Globals.VreeActivationHeight;
        private readonly string _priorChainRef = BlockchainData.ChainRef;
        private readonly Func<long, string?> _priorHashAt = VrfSeed.BlockHashAt;
        private readonly Func<long, List<VBTCValidator>> _priorActiveAt = VrfSeed.ActiveAt;
        private readonly Func<string, long, bool> _priorEligible = VreeElection.ElectorEligible;
        private readonly Func<long, Block?> _priorBlockAt = VreeElection.BlockAt;
        private readonly Func<(string Address, PrivateKey? Key, string PublicKey)> _priorLocalKey = VreeService.LocalKey;
        private readonly Func<string, string, Task<string?>> _priorGet = VreeService.HttpGet;
        private readonly Func<string, string, string, Task<string?>> _priorPost = VreeService.HttpPost;
        private readonly System.Collections.Concurrent.ConcurrentBag<Peers> _priorBag = Globals.BlockCasters;

        private readonly AsyncLocal<Member?> _who = new();
        private readonly Dictionary<long, string> _hashes = new();
        private readonly Dictionary<long, Block> _blocks = new();
        private readonly HashSet<string> _unaged = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Member> _byIp = new(StringComparer.Ordinal);
        private readonly List<Member> _committee = new();
        private readonly List<Member> _electors = new();
        private CasterMembershipRecord _head = null!;

        public VreeElectionTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"vree_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            CasterMembershipStore.InvalidateCache();
            VreeElection.ClearCachesForTests();
            VreeService.ResetForTests();
            BlockchainData.ChainRef = "test-chain";
            Globals.IsChainSynced = true;
            Globals.IsBlockCaster = false;
            Globals.CertEnforceHeight = 1;
            Globals.VrfSeedHeight = SeedHeight;
            Globals.VreeActivationHeight = 1;
            Globals.BlockCasters = new System.Collections.Concurrent.ConcurrentBag<Peers>();
            Globals.LastBlockAddedTimestamp = 0;
            Globals.ValidatorAddress = "";

            VrfSeed.BlockHashAt = h => _hashes.TryGetValue(h, out var hash) ? hash : $"block-{h}";
            VrfSeed.ActiveAt = _ => Validators();
            VreeElection.ElectorEligible = (a, _) => !_unaged.Contains(a);
            VreeElection.BlockAt = h => _blocks.TryGetValue(h, out var b) ? b : new Block { Height = h, Hash = VrfSeed.BlockHashAt(h), Timestamp = TimeUtil.GetTime() - 1000 };
            VreeService.LocalKey = () => _who.Value is { } m ? (m.Address, m.Key, m.Pub) : ("", null, "");
            VreeService.HttpGet = (_, _) => Task.FromResult<string?>(null);
            VreeService.HttpPost = (_, _, _) => Task.FromResult<string?>(null);

            for (var i = 0; i < 5; i++) _committee.Add(NewMember());
            for (var i = 0; i < 12; i++) _electors.Add(NewMember());
            _head = Ordinary(HeadSeq, 500, "prev", _committee);
            Store(_head);
            StallAt(H, 400);
        }

        public void Dispose()
        {
            Globals.LastBlock = _priorLastBlock;
            Globals.IsChainSynced = _priorSynced;
            Globals.IsBlockCaster = _priorCaster;
            Globals.ValidatorAddress = _priorValidator;
            Globals.LastBlockAddedTimestamp = _priorAdded;
            Globals.CertEnforceHeight = _priorCertEnforce;
            Globals.VrfSeedHeight = _priorSeedHeight;
            Globals.VreeActivationHeight = _priorActivation;
            Globals.BlockCasters = _priorBag;
            BlockchainData.ChainRef = _priorChainRef;
            VrfSeed.BlockHashAt = _priorHashAt;
            VrfSeed.ActiveAt = _priorActiveAt;
            VreeElection.ElectorEligible = _priorEligible;
            VreeElection.BlockAt = _priorBlockAt;
            VreeService.LocalKey = _priorLocalKey;
            VreeService.HttpGet = _priorGet;
            VreeService.HttpPost = _priorPost;
            VreeElection.ClearCachesForTests();
            VreeService.ResetForTests();
            CasterMembershipStore.InvalidateCache();
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        // ── Harness ──────────────────────────────────────────────────────────────────────────────

        private int _ipCounter;

        private Member NewMember()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            var m = new Member(key, pub, AccountData.GetHumanAddress(pub), $"10.0.0.{++_ipCounter}");
            _byIp[m.Ip] = m;
            return m;
        }

        private List<VBTCValidator> Validators() => _committee.Concat(_electors)
            .Select(m => new VBTCValidator { ValidatorAddress = m.Address, IPAddress = m.Ip, FrostPublicKey = m.Pub, RegistrationBlockHeight = 1, IsActive = true }).ToList();

        private static CasterMembershipRecord Ordinary(long seq, long effective, string prev, IEnumerable<Member> members, string change = "Promotion")
        {
            var r = new CasterMembershipRecord
            {
                RecordSeq = seq, EffectiveFromHeight = effective, PrevRecordHash = prev, ChangeType = change,
                Casters = members.Select(m => new CasterInfo { Address = m.Address, PeerIP = m.Ip, PublicKey = m.Pub }).OrderBy(c => c.Address, StringComparer.Ordinal).ToList(),
            };
            r.RecordHash = CasterMembershipStore.ComputeRecordHash(r);
            return r;
        }

        private static void Store(CasterMembershipRecord r)
        {
            DbContext.DB_Config!.GetCollection<CasterMembershipRecord>("rsrv_caster_membership").Upsert(r.RecordSeq, r);
            CasterMembershipStore.InvalidateCache();
        }

        private void StallAt(long height, long ageSeconds)
        {
            var block = new Block { Height = height, Hash = VrfSeed.BlockHashAt(height), PrevHash = VrfSeed.BlockHashAt(height - 1), Timestamp = TimeUtil.GetTime() - ageSeconds, Version = 4 };
            _blocks[height] = block;
            Globals.LastBlock = block;
        }

        private Member Of(string address) => _committee.Concat(_electors).First(m => m.Address == address);

        private static CasterMembershipRecord Signed(CasterMembershipRecord r, IEnumerable<Member> signers)
        {
            var payload = CasterMembershipStore.CanonicalPayload(r);
            r.Signatures = signers.Select(m => new RecordSignature { SignerAddress = m.Address, Signature = SignatureService.CreateSignature(payload, m.Key, m.Pub) }).ToList();
            return r;
        }

        private VreeElection.ElectionView View() =>
            VreeElection.ViewAt(CasterMembershipStore.GetCurrent(), Globals.LastBlock.Height, Globals.LastBlock.Hash, VreeElection.StallSeconds(), CasterMembershipStore.GetBySeq)!;

        /// <summary>Rung r at this tip, ratified by exactly as many electors as it needs.</summary>
        private CasterMembershipRecord Ratified(int rung)
        {
            var view = View();
            var record = view.Build(rung);
            var electors = view.Epoch.Electors.Keys.OrderBy(a => a, StringComparer.Ordinal).Take(view.Epoch.RatifyNeeded).Select(Of);
            return Signed(record, electors);
        }

        private Block BlockAt(long height, string prevHash, long age, IEnumerable<Member> attestors, CasterMembershipRecord? carried = null, string hash = "")
        {
            var block = new Block
            {
                Height = height, Hash = hash.Length > 0 ? hash : $"next-{height}-{Guid.NewGuid():N}", PrevHash = prevHash, Validator = _electors[0].Address, Version = 4,
                Timestamp = TimeUtil.GetTime() - age, MembershipRecord = carried,
            };
            var msg = ConsensusMessageFormatter.FormatAttestationV1(block.Height, block.Hash, block.Validator, block.PrevHash);
            block.ConsensusCertificate = new ConsensusCertificate
            {
                BlockHeight = block.Height, BlockHash = block.Hash, WinnerAddress = block.Validator, PrevHash = block.PrevHash,
                Attestations = attestors.Select(m => new CasterAttestation { CasterAddress = m.Address, Signature = SignatureService.CreateSignature(msg, m.Key, m.Pub) }).ToList(),
            };
            return block;
        }

        private IEnumerable<Member> Members(CasterMembershipRecord r) => r.Casters.Select(c => Of(c.Address));

        private static VreeElection.SignFacts Allowed() => new()
        {
            IsElector = true, VerifiedEvidenceAgainstTip = false, CommitteeUnreachable = true, SurveyFresh = true, HoldActive = false, RatifiedHold = false,
        };

        // ── Thresholds and rungs ─────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData(155, 104, 52)]
        [InlineData(7, 5, 3)]
        [InlineData(12, 8, 5)]
        public void test_measured_thresholds(int electors, int ratify, int reach)
        {
            Assert.Equal(ratify, VreeElection.RatifyNeed(electors));
            Assert.Equal(reach, VreeElection.ReachNeed(electors));
        }

        [Fact]
        public void test_strict_layer2_threshold_keeps_the_layers_disjoint()
        {
            // Acknowledgers do not sign: if both thresholds could be met by disjoint validators the chain could fork.
            for (var n = 4; n <= 3000; n++)
                Assert.True(VreeElection.ReachNeed(n) + VreeElection.RatifyNeed(n) > n, $"n={n}");
        }

        [Fact]
        public void test_layer1_at_most_one_side_of_any_partition_ratifies()
        {
            for (var n = 4; n <= 400; n++)
                Assert.True(2 * VreeElection.RatifyNeed(n) > n, $"n={n}");
        }

        [Fact]
        public void test_standdown_bound_stays_under_the_reorg_cap_and_the_election()
        {
            var worst = VreeElection.WorstStandDownSeconds(12);
            Assert.True(worst < ForkChoiceUtility.MAX_REORG_DEPTH * 12);
            Assert.True(worst < VreeElection.ElectSeconds);
        }

        [Fact]
        public void test_rungs_cover_the_candidates_and_a_short_tail_is_padded_to_five()
        {
            var ranked = Enumerable.Range(0, 155).Select(i => $"a{i}").ToList();
            var seen = Enumerable.Range(0, 31).SelectMany(r => VreeElection.RungMembers(ranked, r)).ToList();
            Assert.Equal(155, seen.Count);
            Assert.Equal(155, seen.Distinct().Count());

            var seven = Enumerable.Range(0, 7).Select(i => $"a{i}").ToList();
            Assert.Equal(new[] { "a5", "a6", "a0", "a1", "a2" }, VreeElection.RungMembers(seven, 1));
            for (var r = 0; r < 20; r++)
                Assert.Equal(5, VreeElection.RungMembers(seven, r).Distinct().Count());
        }

        [Fact]
        public void test_ranking_reads_the_seed_and_the_address_only_in_any_input_order()
        {
            var a = new[] { "x", "y", "z", "w" };
            Assert.Equal(VreeElection.Ranked("seed", a), VreeElection.Ranked("seed", a.Reverse()));
            Assert.NotEqual(VreeElection.Ranked("seed", a), VreeElection.Ranked("other", a));
        }

        [Fact]
        public void TheEpochRankingDoesNotMoveWithTheStallHeightOrItsHash()
        {
            var one = VreeElection.EpochForStall(H, "stall-a")!.Ranked;
            VreeElection.ClearCachesForTests();
            var other = VreeElection.EpochForStall(H + 37, "stall-b")!.Ranked;
            Assert.Equal(one, other);
        }

        [Fact]
        public void TheElectorateExcludesTheCommittee_TheUnaged_AndTheLateRegistered()
        {
            _unaged.Add(_electors[0].Address);
            var epoch = VreeElection.EpochForStall(H, "h")!;
            Assert.Equal(11, epoch.Electors.Count);
            Assert.DoesNotContain(_electors[0].Address, epoch.Electors.Keys);
            foreach (var m in _committee) Assert.DoesNotContain(m.Address, epoch.Electors.Keys);
        }

        [Theory]
        [InlineData("1.2.3.4|x", false)]
        [InlineData("", false)]
        [InlineData("not an ip", false)]
        [InlineData("1.2.3.4", true)]
        [InlineData("::ffff:1.2.3.4", true)]
        public void ACandidateMustBeDialable_SoNoRegisteredStringCanBreakARung(string ip, bool dialable) =>
            Assert.Equal(dialable, VreeElection.IsDialable(ip));

        // ── Signing rules ────────────────────────────────────────────────────────────────────────

        [Fact]
        public void test_rules_each_refuses_with_its_own_reason()
        {
            var view = View();
            Assert.Null(VreeElection.SignRefusal(view, 0, Allowed()));
            Assert.Contains("not an elector", VreeElection.SignRefusal(view, 0, new VreeElection.SignFacts { IsElector = false, CommitteeUnreachable = true, SurveyFresh = true })!);
            Assert.StartsWith("rule 8", VreeElection.SignRefusal(view, 0, new VreeElection.SignFacts { IsElector = true, CommitteeUnreachable = true, SurveyFresh = false })!);
            Assert.StartsWith("rule 2", VreeElection.SignRefusal(view, 0, new VreeElection.SignFacts { IsElector = true, CommitteeUnreachable = true, SurveyFresh = true, VerifiedEvidenceAgainstTip = true })!);
            Assert.StartsWith("rule 3", VreeElection.SignRefusal(view, 0, new VreeElection.SignFacts { IsElector = true, CommitteeUnreachable = false, SurveyFresh = true })!);
            Assert.StartsWith("rule 4", VreeElection.SignRefusal(view, 0, new VreeElection.SignFacts { IsElector = true, CommitteeUnreachable = true, SurveyFresh = true, HoldActive = true })!);
            Assert.StartsWith("rule 6", VreeElection.SignRefusal(view, 5, Allowed())!);
            Assert.StartsWith("rule 7", VreeElection.SignRefusal(view, 0, new VreeElection.SignFacts { IsElector = true, CommitteeUnreachable = true, SurveyFresh = true, RatifiedHold = true })!);
        }

        [Fact]
        public void test_the_block_that_ends_a_stall_does_not_look_stalled_itself()
        {
            // Dated from before the stall, committed just now.
            StallAt(H, 400);
            Assert.True(VreeElection.StallSeconds() >= 400);
            Globals.LastBlockAddedTimestamp = TimeUtil.GetTime() - 3;
            Assert.True(VreeElection.StallSeconds() < VreeElection.ElectSeconds);
        }

        [Fact]
        public void test_a_tip_that_is_not_stale_opens_no_election()
        {
            StallAt(H, VreeElection.ElectSeconds - 1);
            Assert.Null(View());
            StallAt(H, VreeElection.ElectSeconds);
            Assert.NotNull(View());
        }

        [Fact]
        public void BelowTheActivationHeight_NoRuleFires()
        {
            Globals.VreeActivationHeight = long.MaxValue;
            VreeElection.ClearCachesForTests();
            Assert.Contains("not active", VreeElection.SignRefusal(View(), 0, Allowed())!);
            var record = Signed(View().Build(0), _electors.Take(8));
            Assert.False(CasterMembershipStore.TryAppend(record, out var why));
            Assert.Contains("not active", why);
            Assert.Null(VreeService.StandDownDecision(active: false, bootstrap: false, caster: true, firstProbeUnix: 1, now: 1000, acks: 0, reachSet: 100));
            Assert.Null(VreeService.StandDownReason(H + 1));
        }

        [Fact]
        public void test_http_200_without_casting_is_not_reachable_and_neither_is_another_height()
        {
            var known = _committee.Select(m => new NetworkStallSurvey.Node { Ip = m.Ip, Address = m.Address, IsCommittee = true }).ToList();
            NetworkStallSurvey.Reply R(int i, bool casting, long height) => new() { Ip = _committee[i].Ip, Height = height, Casting = casting };

            Assert.True(VreeElection.CommitteeUnreachable(known, Array.Empty<NetworkStallSurvey.Reply>(), H, 3));
            Assert.True(VreeElection.CommitteeUnreachable(known, new[] { R(0, false, H), R(1, false, H), R(2, false, H) }, H, 3)); // hung loop
            Assert.True(VreeElection.CommitteeUnreachable(known, new[] { R(0, true, H - 1), R(1, true, H), R(2, true, H) }, H, 3));
            Assert.False(VreeElection.CommitteeUnreachable(known, new[] { R(0, true, H), R(1, true, H), R(2, true, H) }, H, 3));
        }

        // ── Maintenance hold ─────────────────────────────────────────────────────────────────────

        private static CasterMembershipRecord Hold(long seq, long effective, long seconds) =>
            new() { RecordSeq = seq, EffectiveFromHeight = effective, ChangeType = VreeElection.MaintenanceHoldChangeType, HoldSeconds = seconds };

        [Fact]
        public void test_finding_3_a_hold_expires_during_the_stall_it_covers()
        {
            const long tipTime = 1_000_000;
            var holds = new[] { Hold(4, H + 1, 600) };
            Assert.True(VreeElection.HoldActive(holds, H, tipTime, tipTime + 599, _ => null));
            Assert.False(VreeElection.HoldActive(holds, H, tipTime, tipTime + 600, _ => null));
        }

        [Fact]
        public void test_a_second_hold_on_the_same_stall_is_ignored_and_none_outlasts_the_cap()
        {
            const long tipTime = 1_000_000;
            var holds = new[] { Hold(4, H + 1, 600), Hold(5, H + 2, 3_600) };
            Assert.False(VreeElection.HoldActive(holds, H, tipTime, tipTime + 700, _ => null));
            Assert.False(VreeElection.HoldActive(new[] { Hold(4, H + 1, 999_999) }, H, tipTime, tipTime + VreeElection.MaxHoldSeconds, _ => null));
        }

        [Fact]
        public void AHoldIssuedBeforeTheChainStopped_RunsFromItsOwnBlock()
        {
            const long tipTime = 1_000_000;
            var anchor = new Block { Height = H - 3, Timestamp = tipTime - 36 };
            Assert.True(VreeElection.HoldActive(new[] { Hold(4, H - 2, 600) }, H, tipTime, tipTime + 500, _ => anchor));
            Assert.False(VreeElection.HoldActive(new[] { Hold(4, H - 2, 600) }, H, tipTime, tipTime + 600, _ => anchor));
        }

        [Fact]
        public void AHoldChangesNobody_AndIsShort()
        {
            var ok = Ordinary(HeadSeq + 1, 600, _head.RecordHash, _committee, VreeElection.MaintenanceHoldChangeType);
            ok.HoldSeconds = 600; ok.SchemeVersion = 1;
            Assert.Null(VreeElection.HoldShapeRefusal(_head, ok));

            var tooLong = Ordinary(HeadSeq + 1, 600, _head.RecordHash, _committee, VreeElection.MaintenanceHoldChangeType);
            tooLong.HoldSeconds = VreeElection.MaxHoldSeconds + 1; tooLong.SchemeVersion = 1;
            Assert.NotNull(VreeElection.HoldShapeRefusal(_head, tooLong));

            var swaps = Ordinary(HeadSeq + 1, 600, _head.RecordHash, _committee.Take(4).Append(_electors[0]), VreeElection.MaintenanceHoldChangeType);
            swaps.HoldSeconds = 600; swaps.SchemeVersion = 1;
            Assert.NotNull(VreeElection.HoldShapeRefusal(_head, swaps));
        }

        [Fact]
        public void AHoldIsSignedByTheCommittee_AndItsSecondsAreCoveredByTheHash()
        {
            var hold = Ordinary(HeadSeq + 1, H + 2, _head.RecordHash, _committee, VreeElection.MaintenanceHoldChangeType);
            hold.HoldSeconds = 600; hold.SchemeVersion = 1;
            hold.RecordHash = CasterMembershipStore.ComputeRecordHash(hold);
            Assert.False(CasterMembershipStore.ValidateSuccessor(_head, Signed(hold, _electors.Take(8)), out _));
            Assert.True(CasterMembershipStore.ValidateSuccessor(_head, Signed(hold, _committee.Take(3)), out var why), why);

            hold.HoldSeconds = 3_600; // same signatures, longer hold
            Assert.False(CasterMembershipStore.ValidateSuccessor(_head, hold, out _));
        }

        // ── Accepting a record ───────────────────────────────────────────────────────────────────

        [Fact]
        public void test_validate_a_ratified_rung_is_appended_on_a_stalled_tip()
        {
            var record = Ratified(0);
            Assert.True(CasterMembershipStore.TryAppend(record, out var why), why);
            Assert.Equal(record.RecordHash, CasterMembershipStore.GetCurrent()!.RecordHash);
            Assert.Equal(H + 1 + VreeElection.TermBlocks, record.LapseHeight);
            Assert.Equal(record.RecordHash, CasterMembershipStore.GetForHeight(H + 1)!.RecordHash);
            Assert.Equal(_head.RecordHash, CasterMembershipStore.GetForHeight(H)!.RecordHash);
        }

        [Fact]
        public void test_a_node_whose_chain_is_moving_refuses()
        {
            var record = Ratified(0);
            StallAt(H, 20);
            Assert.False(CasterMembershipStore.TryAppend(record, out var why));
            Assert.Contains("not stalled", why);
        }

        [Fact]
        public void test_a_node_on_another_tip_refuses()
        {
            var record = Ratified(0);
            _hashes[H] = "another-block";
            StallAt(H, 400);
            Assert.False(CasterMembershipStore.TryAppend(record, out var why));
            Assert.Contains("another block", why);
        }

        [Fact]
        public void test_a_lagging_node_refuses_by_gossip_and_gets_it_from_the_block()
        {
            var record = Ratified(0);
            StallAt(H - 5, 400);
            Assert.False(CasterMembershipStore.TryAppend(record, out _));
        }

        [Fact]
        public void test_ratification_counts_distinct_electors_only()
        {
            var view = View();
            var need = view.Epoch.RatifyNeeded;
            Assert.Equal(8, need);

            var tooFew = Signed(view.Build(0), _electors.Take(need - 1));
            Assert.False(CasterMembershipStore.ValidateSuccessor(_head, tooFew, out var a));
            Assert.Contains("signatures", a);

            var doubled = Signed(view.Build(0), _electors.Take(need - 1));
            doubled.Signatures.Add(doubled.Signatures[0]);
            Assert.False(CasterMembershipStore.ValidateSuccessor(_head, doubled, out _));

            var committeeSigned = Signed(view.Build(0), _electors.Take(need - 1).Concat(_committee.Take(1)));
            Assert.False(CasterMembershipStore.ValidateSuccessor(_head, committeeSigned, out _));

            var outsider = NewMember();
            var outsiderSigned = Signed(view.Build(0), _electors.Take(need - 1).Append(outsider));
            Assert.False(CasterMembershipStore.ValidateSuccessor(_head, outsiderSigned, out _));

            var forged = Signed(view.Build(0), _electors.Take(need));
            forged.Signatures[0].Signature = forged.Signatures[1].Signature; // another elector's signature under this name
            Assert.False(CasterMembershipStore.ValidateSuccessor(_head, forged, out _));

            var flooded = Signed(view.Build(0), _electors.Take(need));
            flooded.Signatures.AddRange(Enumerable.Range(0, 100).Select(_ => flooded.Signatures[0]));
            Assert.False(CasterMembershipStore.ValidateSuccessor(_head, flooded, out var f));
            Assert.Contains("more signatures", f);
        }

        [Fact]
        public void test_rejects_each_tampered_field()
        {
            CasterMembershipRecord Fresh() => Ratified(0);
            void Refused(CasterMembershipRecord r, string because)
            {
                r.RecordHash = CasterMembershipStore.ComputeRecordHash(r); // a tamperer recomputes the hash; the signatures then fail, or the set does
                Assert.False(CasterMembershipStore.ValidateSuccessor(_head, r, out _), because);
            }

            var members = Fresh(); members.Casters[0] = new CasterInfo { Address = _electors[11].Address, PeerIP = "6.6.6.6", PublicKey = "" }; Refused(members, "members");
            var ip = Fresh(); ip.Casters[0].PeerIP = "6.6.6.6"; Refused(ip, "ip");
            var key = Fresh(); key.Casters[0].PublicKey = "04ff"; Refused(key, "key");
            var rung = Fresh(); rung.Rung = 1; Refused(rung, "rung");
            var lapse = Fresh(); lapse.LapseHeight += 1_000_000; Refused(lapse, "lapse");
            var effective = Fresh(); effective.EffectiveFromHeight += 1; Refused(effective, "effective");
            var scheme = Fresh(); scheme.SchemeVersion = 2; Refused(scheme, "scheme");
            var epoch = Fresh(); epoch.EpochHash = "other"; Refused(epoch, "epoch");
            var prev = Fresh(); prev.PrevRecordHash = "other"; Refused(prev, "prev");

            var noCasters = Fresh(); noCasters.Casters = null!;
            Assert.False(CasterMembershipStore.ValidateSuccessor(_head, noCasters, out _));
        }

        [Fact]
        public void TwoThirdsOfTheElectorateCannotRatifyASetOfTheirOwnChoosing()
        {
            // Every signature valid and every elector in on it: the members are a function of the chain, not of who signs.
            var view = View();
            var chosen = view.Build(0);
            chosen.Casters = _electors.Skip(7).Take(5).Select(m => new CasterInfo { Address = m.Address, PeerIP = m.Ip, PublicKey = m.Pub }).OrderBy(c => c.Address, StringComparer.Ordinal).ToList();
            chosen.RecordHash = CasterMembershipStore.ComputeRecordHash(chosen);
            Assert.NotEqual(view.Build(0).RecordHash, chosen.RecordHash);
            Signed(chosen, _electors.Take(view.Epoch.RatifyNeeded));

            Assert.False(CasterMembershipStore.ValidateSuccessor(_head, chosen, out var why));
            Assert.Contains("not the rung", why);
            Assert.Equal(VreeElection.CarriedVerdict.Invalid, VreeElection.JudgeCarried(BlockAt(H + 1, Globals.LastBlock.Hash, 0, Members(chosen).Take(3), chosen)));
        }

        [Fact]
        public void ARecordRatifiedForAnotherBlock_IsNotCarriedOnThisOne()
        {
            // Two thirds on another branch's block at this height: it does not install on this one.
            var record = Ratified(0);
            _hashes[H] = "this-chains-block";
            StallAt(H, 400);
            VreeElection.ClearCachesForTests();
            var block = BlockAt(H + 1, Globals.LastBlock.Hash, 0, Members(record).Take(3), record);
            Assert.Equal(VreeElection.CarriedVerdict.Invalid, VreeElection.JudgeCarried(block));
        }

        [Fact]
        public void AnElectorThatCannotBeDialedVotes_ButDoesNotStand()
        {
            var bad = _electors[0];
            var validators = Validators();
            validators.First(v => v.ValidatorAddress == bad.Address).IPAddress = "1.2.3.4|x";
            VrfSeed.ActiveAt = _ => validators;
            VreeElection.ClearCachesForTests();

            var epoch = VreeElection.EpochForStall(H, "h")!;
            Assert.Contains(bad.Address, epoch.Electors.Keys);
            Assert.DoesNotContain(bad.Address, epoch.Ranked);
            Assert.Equal(11, epoch.Ranked.Count);
        }

        [Fact]
        public void test_an_electorate_below_the_floor_is_dark()
        {
            _unaged.UnionWith(_electors.Skip(3).Select(m => m.Address));
            VreeElection.ClearCachesForTests();
            var view = View();
            Assert.True(view.Epoch.Dark);
            Assert.Contains("below", VreeElection.SignRefusal(view, 0, Allowed())!);
            Assert.False(CasterMembershipStore.ValidateSuccessor(_head, Signed(view.Build(0), _electors.Take(3)), out _));
        }

        [Fact]
        public void AnElectionReplacesACommitteeWhoseOwnRotationWasStillPending()
        {
            // The committee signed a demotion effective above the stall, then died. "Effective height must advance" would refuse every rung.
            var pending = Ordinary(HeadSeq + 1, H + 5, _head.RecordHash, _committee.Take(4), "Demotion");
            Store(pending);
            VreeElection.ClearCachesForTests();
            var record = Ratified(0);
            Assert.Equal(H + 1, record.EffectiveFromHeight);
            Assert.True(CasterMembershipStore.TryAppend(record, out var why), why);
            Assert.Equal(record.RecordHash, CasterMembershipStore.GetForHeight(H + 5)!.RecordHash);
        }

        [Fact]
        public void TheOrdinaryRecordFormatIsUnchanged_SoOldNodesAndOldRecordsAgree()
        {
            var ordinary = _head;
            var v1 = ConsensusMessageFormatter.FormatCasterMembershipV1(ordinary.RecordSeq, ordinary.EffectiveFromHeight, ordinary.PrevRecordHash, ordinary.ChangeType, ordinary.ChangedAddress,
                ordinary.Casters.OrderBy(c => c.Address, StringComparer.Ordinal).Select(c => (c.Address, c.PeerIP, c.PublicKey)));
            Assert.Equal(v1, CasterMembershipStore.CanonicalPayload(ordinary));

            var election = Ratified(0);
            Assert.StartsWith(ConsensusMessageFormatter.FormatCasterMembershipV1(election.RecordSeq, election.EffectiveFromHeight, election.PrevRecordHash, election.ChangeType, election.ChangedAddress,
                election.Casters.OrderBy(c => c.Address, StringComparer.Ordinal).Select(c => (c.Address, c.PeerIP, c.PublicKey))) + "|VREE-V1|", CasterMembershipStore.CanonicalPayload(election));
        }

        [Fact]
        public void ABlockWithoutARecordSerializesAsItAlwaysDid_AndTheRecordIsOutsideItsHash()
        {
            var block = new Block { Height = 5, Hash = "x", PrevHash = "p", MerkleRoot = "m", Transactions = new List<Transaction>() };
            Assert.DoesNotContain("MembershipRecord", JsonConvert.SerializeObject(block));
            var hash = block.GetBlockHash();
            block.MembershipRecord = _head;
            Assert.Equal(hash, block.GetBlockHash());
            var roundTrip = JsonConvert.DeserializeObject<Block>(JsonConvert.SerializeObject(block))!;
            Assert.Equal(_head.RecordHash, roundTrip.MembershipRecord!.RecordHash);
        }

        // ── Siblings and the seed reset ──────────────────────────────────────────────────────────

        [Fact]
        public void test_a_sibling_seen_by_gossip_starts_the_hold_and_does_not_replace_the_head()
        {
            var rung0 = Ratified(0);
            Assert.True(CasterMembershipStore.TryAppend(rung0, out _));
            StallAt(H, 700);
            var rung3 = Ratified(3);
            Assert.Equal(rung0.RecordSeq, rung3.RecordSeq);
            Assert.Equal(rung0.PrevRecordHash, rung3.PrevRecordHash);
            VreeElection.ClearCachesForTests();

            Assert.False(CasterMembershipStore.TryAppend(rung3, out _));
            Assert.Equal(rung0.RecordHash, CasterMembershipStore.GetCurrent()!.RecordHash);
            Assert.True(VreeElection.WithinRatifiedHold(CasterMembershipStore.GetCurrent(), rung3.EpochHash, TimeUtil.GetTime()));
        }

        [Fact]
        public void AMemberOfBothRungs_TakesTheHigherOne_AndAnOldRungCannotKnockAMemberBack()
        {
            var rung0 = Ratified(0);
            Assert.True(CasterMembershipStore.TryAppend(rung0, out _));
            StallAt(H, 700);
            var rung3 = Ratified(3);
            var both = rung3.Casters.First(c => rung0.Casters.Any(z => z.Address == c.Address)).Address;
            Globals.ValidatorAddress = both;
            Assert.True(CasterMembershipStore.TryAppend(rung3, out var why), why);
            Assert.Equal(rung3.RecordHash, CasterMembershipStore.GetCurrent()!.RecordHash);

            Assert.False(CasterMembershipStore.TryAppend(rung0, out _));
            Assert.Equal(rung3.RecordHash, CasterMembershipStore.GetCurrent()!.RecordHash);
        }

        [Fact]
        public void test_a_rung_s_other_members_install_it_from_the_first_members_announce()
        {
            var rung0 = Ratified(0);
            Assert.True(CasterMembershipStore.TryAppend(rung0, out _));
            StallAt(H, 700);
            var rung3 = Ratified(3);
            VreeElection.ClearCachesForTests();

            Globals.ValidatorAddress = rung3.Casters.First(c => !rung0.Casters.Any(z => z.Address == c.Address)).Address;
            Assert.True(CasterMembershipStore.TryAppend(rung3, out var why), why);
            Assert.Equal(rung3.RecordHash, CasterMembershipStore.GetCurrent()!.RecordHash);
        }

        [Fact]
        public void ReplayingARatifiedRungDoesNotExtendTheHold()
        {
            const long start = 1_000_000;
            VreeElection.NoteRatified("epoch", "rung-3", start);
            Assert.True(VreeElection.WithinRatifiedHold(null, "epoch", start));
            VreeElection.NoteRatified("epoch", "rung-3", start + VreeElection.RatifiedHoldSeconds - 1); // replayed just before it ends
            Assert.False(VreeElection.WithinRatifiedHold(null, "epoch", start + VreeElection.RatifiedHoldSeconds));

            VreeElection.NoteRatified("epoch", "rung-5", start + VreeElection.RatifiedHoldSeconds); // another rung does start one
            Assert.True(VreeElection.WithinRatifiedHold(null, "epoch", start + VreeElection.RatifiedHoldSeconds));
        }

        [Fact]
        public void ARungsOwnMemberInstallsItOverADeadSibling_UntilTheSiblingHasStarted()
        {
            var rung0 = Ratified(0);
            Assert.True(CasterMembershipStore.TryAppend(rung0, out _));
            StallAt(H, 700);
            var rung3 = Ratified(3);
            Assert.True(CasterMembershipStore.ReplaceHeadWithOwnRung(rung3, out var why), why);
            Assert.Equal(rung3.RecordHash, CasterMembershipStore.GetCurrent()!.RecordHash);

            StallAt(H + 1, 10);
            Assert.False(CasterMembershipStore.ReplaceHeadWithOwnRung(rung0, out var final));
            Assert.Contains("in force", final);
        }

        [Fact]
        public void test_emergency_election_outranks_a_bootstrap_reset_only_while_the_tip_is_h()
        {
            var reset = Ordinary(HeadSeq + 1, H + 1, _head.RecordHash, _committee.Take(3), CasterMembershipStore.BootstrapResetChangeType);
            Store(reset);
            VreeElection.ClearCachesForTests();
            StallAt(H, 700);

            var record = Ratified(0);
            Assert.Equal(reset.RecordSeq, record.RecordSeq);
            Assert.Equal(reset.PrevRecordHash, record.PrevRecordHash);
            Assert.True(CasterMembershipStore.TryAppend(record, out var why), why);
            Assert.Equal(record.RecordHash, CasterMembershipStore.GetCurrent()!.RecordHash);
        }

        [Fact]
        public void ASeedResetCanReplaceARungThatNeverStarted_OnceNoOtherRungCanFollow()
        {
            var rung0 = Ratified(0);
            Assert.True(CasterMembershipStore.TryAppend(rung0, out _));
            StallAt(H, VreeElection.ResetAfterElectionSeconds + 1);
            Assert.True(VreeElection.ElectionNotStarted(rung0, H));
            Assert.False(VreeElection.ElectionNotStarted(rung0, H + 1));

            // A sibling of the rung: the same predecessor and the next block. Any rung's holder takes it, and the rung is gone from the record chain.
            var reset = Ordinary(rung0.RecordSeq, H + 1, rung0.PrevRecordHash, _committee.Take(3), CasterMembershipStore.BootstrapResetChangeType);
            Assert.True(VreeElection.ResetMayReplace(rung0, reset, H, VreeElection.ResetAfterElectionSeconds));
            Assert.False(VreeElection.ResetMayReplace(rung0, reset, H, VreeElection.ResetAfterElectionSeconds - 1)); // the election outranks it until then
            Assert.False(VreeElection.ResetMayReplace(rung0, reset, H + 1, VreeElection.ResetAfterElectionSeconds)); // started
            Assert.False(VreeElection.ResetMayReplace(rung0, Ordinary(rung0.RecordSeq, H + 1, rung0.PrevRecordHash, _committee.Take(3)), H, VreeElection.ResetAfterElectionSeconds)); // only a reset
            var onTop = Ordinary(rung0.RecordSeq + 1, H + 1, rung0.RecordHash, _committee.Take(3), CasterMembershipStore.BootstrapResetChangeType);
            CasterMembershipStore.ValidateSuccessor(rung0, onTop, out var refused);
            Assert.Contains("not advancing", refused);
        }

        [Fact]
        public void test_equivocation_a_validator_signs_one_record_per_rung()
        {
            Assert.True(CasterMembershipStore.TryMarkElectionSigned("epoch", 0, "record-a"));
            Assert.True(CasterMembershipStore.TryMarkElectionSigned("epoch", 0, "record-a"));
            Assert.False(CasterMembershipStore.TryMarkElectionSigned("epoch", 0, "record-b"));
            Assert.True(CasterMembershipStore.TryMarkElectionSigned("epoch", 1, "record-b"));
        }

        // ── The record a block carries ───────────────────────────────────────────────────────────

        [Fact]
        public void AValidatorThatMissedTheGossip_VerifiesTheBlockUnderTheRecordItCarries_AndAdoptsIt()
        {
            var record = Ratified(0);
            var block = BlockAt(H + 1, Globals.LastBlock.Hash, 0, Members(record).Take(3), record);

            Assert.Null(VreeElection.PrepareBlockRecord(block));
            Assert.NotNull(block.MembershipRecord);
            Assert.True(ConsensusCertificateVerifier.VerifyOrNotRequired(block));
            Assert.True(VreeService.AdoptFromBlock(block, out var why), why);
            Assert.Equal(record.RecordHash, CasterMembershipStore.GetCurrent()!.RecordHash);
        }

        [Fact]
        public void TheOldCommitteesCertificate_IsNoGoodUnderARecordCarried_OrHeld()
        {
            var record = Ratified(0);
            var carried = BlockAt(H + 1, Globals.LastBlock.Hash, 0, _committee.Take(3), record);
            Assert.Null(VreeElection.PrepareBlockRecord(carried));
            Assert.False(ConsensusCertificateVerifier.VerifyOrNotRequired(carried));

            Assert.True(CasterMembershipStore.TryAppend(record, out _));
            var held = BlockAt(H + 1, Globals.LastBlock.Hash, 0, _committee.Take(3));
            Assert.Null(VreeElection.PrepareBlockRecord(held));
            Assert.Equal(record.RecordHash, held.MembershipRecord!.RecordHash); // given it: its certificate is now that committee's
            Assert.False(ConsensusCertificateVerifier.VerifyOrNotRequired(held));
        }

        [Fact]
        public void ANodeThatMissedTheRecord_StillTakesTheOldCommitteesBlock_AndForkChoiceResolvesItLater()
        {
            var old = BlockAt(H + 1, Globals.LastBlock.Hash, 0, _committee.Take(3));
            Assert.Null(VreeElection.PrepareBlockRecord(old));
            Assert.Null(old.MembershipRecord);
            Assert.True(ConsensusCertificateVerifier.VerifyOrNotRequired(old));
        }

        [Fact]
        public void AJunkRecordOnAGoodBlockIsRemoved_AndTheBlockStandsOnItsOwn()
        {
            StallAt(H, 20);
            var forged = Signed(Ordinary(HeadSeq + 1, H + 1, _head.RecordHash, _electors.Take(5), VreeElection.EmergencyElectionChangeType), _electors.Take(5));
            var good = BlockAt(H + 1, Globals.LastBlock.Hash, 0, _committee.Take(3), forged);

            Assert.Null(VreeElection.PrepareBlockRecord(good));
            Assert.Null(good.MembershipRecord);
            Assert.True(ConsensusCertificateVerifier.VerifyOrNotRequired(good));

            // Far ahead of the record chain: no way to judge it, and the block's own certificate decides.
            var ahead = Ordinary(HeadSeq + 50, H + 1, "nowhere", _electors.Take(5), VreeElection.EmergencyElectionChangeType);
            var good2 = BlockAt(H + 1, Globals.LastBlock.Hash, 0, _committee.Take(3), ahead);
            Assert.Null(VreeElection.PrepareBlockRecord(good2));
            Assert.Null(good2.MembershipRecord);
            Assert.True(ConsensusCertificateVerifier.VerifyOrNotRequired(good2));

            // No certificate of its own and a record it cannot judge: it waits.
            var waits = BlockAt(H + 1, Globals.LastBlock.Hash, 0, _electors.Take(3), ahead);
            Assert.NotNull(VreeElection.PrepareBlockRecord(waits));
            Assert.NotNull(waits.MembershipRecord);
        }

        [Fact]
        public void AValidRecordWithAMalformedCopy_IsJudgedByItsOwnSignatures()
        {
            var good = Ratified(0);
            var block = BlockAt(H + 1, Globals.LastBlock.Hash, 0, Members(good).Take(3), good);
            Assert.Equal(VreeElection.CarriedVerdict.Valid, VreeElection.JudgeCarried(block));

            var junk = JsonConvert.DeserializeObject<CasterMembershipRecord>(JsonConvert.SerializeObject(good))!;
            junk.Signatures.ForEach(s => s.Signature = "junk.junk");
            var sameBlockJunkRecord = new Block { Height = block.Height, Hash = block.Hash, PrevHash = block.PrevHash, Validator = block.Validator, Version = 4, Timestamp = block.Timestamp, ConsensusCertificate = block.ConsensusCertificate, MembershipRecord = junk };
            Assert.Equal(VreeElection.CarriedVerdict.Invalid, VreeElection.JudgeCarried(sameBlockJunkRecord)); // the cached verdict is not shared

            var noCasters = JsonConvert.DeserializeObject<CasterMembershipRecord>(JsonConvert.SerializeObject(good))!;
            noCasters.Casters = null!;
            sameBlockJunkRecord.MembershipRecord = noCasters;
            Assert.Equal(VreeElection.CarriedVerdict.Invalid, VreeElection.JudgeCarried(sameBlockJunkRecord));
        }

        [Fact]
        public void AFirstBlockCraftedBeforeTheStall_StillCarriesTheRecord()
        {
            // A validator crafts its next block once, a few seconds after the tip: the block that ends a stall is dated before it.
            var record = Ratified(0);
            var crafted = BlockAt(H + 1, Globals.LastBlock.Hash, 0, Members(record).Take(3), record);
            crafted.Timestamp = Globals.LastBlock.Timestamp + 15;
            Assert.Equal(VreeElection.CarriedVerdict.Valid, VreeElection.JudgeCarried(crafted));
        }

        [Fact]
        public void AForgedCasterListUnderARealRecordHash_IsNotServedFromTheVerdictCache()
        {
            var record = Ratified(0);
            var block = BlockAt(H + 1, Globals.LastBlock.Hash, 0, _committee.Take(3), record); // the real record; a certificate that fails
            Assert.Equal(VreeElection.CarriedVerdict.Valid, VreeElection.JudgeCarried(block));

            var attackers = _electors.Where(e => !record.Casters.Any(c => c.Address == e.Address)).Take(5).ToList();
            var forged = JsonConvert.DeserializeObject<CasterMembershipRecord>(JsonConvert.SerializeObject(record))!;
            forged.Casters = attackers.Select(m => new CasterInfo { Address = m.Address, PeerIP = m.Ip, PublicKey = m.Pub }).ToList();
            var replay = BlockAt(H + 1, block.PrevHash, 0, attackers.Take(3), forged, block.Hash);
            replay.Timestamp = block.Timestamp;

            Assert.Equal(VreeElection.CarriedVerdict.Invalid, VreeElection.JudgeCarried(replay));
            Assert.Null(VreeElection.CarriedCommittee(replay));
            Assert.False(ConsensusCertificateVerifier.VerifyOrNotRequired(replay));
            Assert.False(VreeService.AdoptFromBlock(replay, out _));
            Assert.Equal(_head.RecordHash, CasterMembershipStore.GetCurrent()!.RecordHash);

            var nullEntry = JsonConvert.DeserializeObject<CasterMembershipRecord>(JsonConvert.SerializeObject(record))!;
            nullEntry.Casters[0] = null!;
            replay.MembershipRecord = nullEntry;
            Assert.Equal(VreeElection.CarriedVerdict.Invalid, VreeElection.JudgeCarried(replay));
        }

        [Fact]
        public void FreeForkChoiceRankIsNotGivenToARecordWithoutItsCommitteesCertificate()
        {
            var record = Ratified(0);
            Assert.Equal(0, VreeElection.CarriedRung(BlockAt(H + 1, Globals.LastBlock.Hash, 0, Members(record).Take(3), record)));
            Assert.Null(VreeElection.CarriedRung(BlockAt(H + 1, Globals.LastBlock.Hash, 0, _committee.Take(3), record)));
        }

        [Fact]
        public void TheFirstBlockDecidesWhichSiblingCounts_AndABadCertificateDoesNotMoveTheHead()
        {
            var rung0 = Ratified(0);
            Assert.True(CasterMembershipStore.TryAppend(rung0, out _));
            StallAt(H, 700);
            var rung3 = Ratified(3);

            var bogus = BlockAt(H + 1, Globals.LastBlock.Hash, 0, _electors.Skip(9).Take(3).Where(m => !rung3.Casters.Any(c => c.Address == m.Address)), rung3);
            Assert.False(VreeService.AdoptFromBlock(bogus, out _));
            Assert.Equal(rung0.RecordHash, CasterMembershipStore.GetCurrent()!.RecordHash);

            var real = BlockAt(H + 1, Globals.LastBlock.Hash, 0, Members(rung3).Take(3), rung3);
            Assert.True(VreeService.AdoptFromBlock(real, out var why), why);
            Assert.Equal(rung3.RecordHash, CasterMembershipStore.GetCurrent()!.RecordHash);
        }

        [Fact]
        public void ANodePastTheRecordTakesItOnlyAsItsOwnChainCarriesIt()
        {
            var record = Ratified(0);
            StallAt(H + 3, 5);
            Assert.False(CasterMembershipStore.TryAppend(record, out var refused, fromSync: true));
            Assert.Contains("is not this record's", refused);

            _blocks[H + 1] = new Block { Height = H + 1, Hash = "b", MembershipRecord = record };
            var forged = JsonConvert.DeserializeObject<CasterMembershipRecord>(JsonConvert.SerializeObject(record))!;
            forged.Casters[0].PeerIP = "6.6.6.6"; // its claimed hash is the real one
            Assert.False(CasterMembershipStore.TryAppend(forged, out _, fromSync: true));
            Assert.True(CasterMembershipStore.TryAppend(record, out var why, fromSync: true), why);
        }

        [Fact]
        public void ANodeThatSyncedPastABlockWithoutItsRecord_IsNotWedged()
        {
            // A bulk sync took the block from a node with no record store, which served it without the record.
            var record = Ratified(0);
            var block = BlockAt(H + 1, Globals.LastBlock.Hash, 0, Members(record).Take(3));
            _blocks[H + 1] = block;
            StallAt(H + 3, 5);

            Assert.True(CasterMembershipStore.TryAppend(record, out var why, fromSync: true), why);
            Assert.Equal(record.RecordHash, CasterMembershipStore.GetCurrent()!.RecordHash);

            // Not for a block that committee did not certify.
            Store(_head);
            DbContext.DB_Config!.GetCollection<CasterMembershipRecord>("rsrv_caster_membership").Delete(record.RecordSeq);
            CasterMembershipStore.InvalidateCache();
            VreeElection.ClearCachesForTests();
            _blocks[H + 1] = BlockAt(H + 1, _blocks[H + 1].PrevHash, 0, _committee.Take(3), null, block.Hash);
            Assert.False(CasterMembershipStore.TryAppend(record, out _, fromSync: true));
        }

        [Fact]
        public void AWalletNodeWithNoRecordStore_KeepsFollowingAnElectionsFirstBlock()
        {
            var record = Ratified(0);
            var block = BlockAt(H + 1, Globals.LastBlock.Hash, 0, Members(record).Take(3), record);
            DbContext.DB_Config!.GetCollection<CasterMembershipRecord>("rsrv_caster_membership").DeleteAll();
            CasterMembershipStore.InvalidateCache();
            VreeElection.ClearCachesForTests();
            Globals.ValidatorAddress = "";
            Globals.CertEnforceHeight = long.MaxValue; // not armed: no record here

            Assert.Null(VreeElection.PrepareBlockRecord(block));
            Assert.NotNull(block.MembershipRecord);
            Assert.True(ConsensusCertificateVerifier.VerifyOrNotRequired(block));
            Assert.True(VreeService.AdoptFromBlock(block, out var why), why);

            // A validator in that state waits for the record instead.
            Globals.ValidatorAddress = _electors[0].Address;
            Assert.NotNull(VreeElection.PrepareBlockRecord(block));
        }

        [Fact]
        public void BelowTheActivationHeight_ABlockCarriesNoRecord()
        {
            var record = Ratified(0);
            Globals.VreeActivationHeight = long.MaxValue;
            VreeElection.ClearCachesForTests();
            var block = BlockAt(H + 1, Globals.LastBlock.Hash, 0, _committee.Take(3), record);
            Assert.Null(VreeElection.PrepareBlockRecord(block));
            Assert.Null(block.MembershipRecord);
            Assert.True(ConsensusCertificateVerifier.VerifyOrNotRequired(block));
        }

        // ── Through ValidateBlock ────────────────────────────────────────────────────────────────

        private Block ProducedBlock(Member producer, IEnumerable<Member> attestors, CasterMembershipRecord? carried)
        {
            var height = Globals.LastBlock.Height + 1;
            var prev = Globals.LastBlock.Hash;
            var block = new Block
            {
                Height = height, Version = BlockVersionUtility.GetBlockVersion(height), PrevHash = prev, Timestamp = TimeUtil.GetTime(), NumOfTx = 0,
                Validator = producer.Address, ValidatorAnswer = ProofUtility.ComputeVrf(producer.Pub, height, VrfSeed.ThirdInput(height, prev)!).ProofHash,
                ChainRefId = "test-chain", Transactions = new List<Transaction>(), MembershipRecord = carried,
            };
            block.MerkleRoot = block.MerkleRootOf();
            block.Hash = block.GetBlockHash();
            block.ValidatorSignature = SignatureService.CreateSignature(block.Hash, producer.Key, producer.Pub);
            var msg = ConsensusMessageFormatter.FormatAttestationV1(block.Height, block.Hash, block.Validator, block.PrevHash);
            block.ConsensusCertificate = new ConsensusCertificate
            {
                BlockHeight = block.Height, BlockHash = block.Hash, WinnerAddress = block.Validator, PrevHash = block.PrevHash,
                Attestations = attestors.Select(m => new CasterAttestation { CasterAddress = m.Address, Signature = SignatureService.CreateSignature(msg, m.Key, m.Pub) }).ToList(),
            };
            return block;
        }

        private static List<string> StepsSince(DateTime since) =>
            BlockDiagnostics.RollbackSnapshot().Where(r => r.TimeUtc >= since).Select(r => r.Location).ToList();

        [Fact]
        public async Task ValidateBlock_AdoptsTheCarriedRecord_OnAValidatorThatMissedTheGossip()
        {
            var record = Ratified(0);
            var block = ProducedBlock(_electors[11], Members(record).Take(3), record);
            var since = DateTime.UtcNow;

            await BlockValidatorService.ValidateBlock(block, false);

            var steps = StepsSince(since);
            Assert.DoesNotContain(steps, s => s.Contains("vree") || s.EndsWith("-cert"));
            Assert.Equal(record.RecordHash, CasterMembershipStore.GetCurrent()!.RecordHash);
        }

        [Fact]
        public async Task ValidateBlock_RefusesTheOldCommitteesBlock_OnceTheRecordIsHeld()
        {
            var record = Ratified(0);
            Assert.True(CasterMembershipStore.TryAppend(record, out _));
            var block = ProducedBlock(_electors[11], _committee.Take(3), null);
            var since = DateTime.UtcNow;

            Assert.False(await BlockValidatorService.ValidateBlock(block, false));

            Assert.Contains(StepsSince(since), s => s.EndsWith("-cert"));
            Assert.Equal(H, Globals.LastBlock.Height);
        }

        [Fact]
        public async Task ValidateBlock_InDownloads_RefusesABlockAtAHeldRecordsHeight_FromTheOldCommittee()
        {
            var record = Ratified(0);
            Assert.True(CasterMembershipStore.TryAppend(record, out _));
            Globals.IsChainSynced = false; // the download path skips certificates away from a synced tip
            var block = ProducedBlock(_electors[11], _committee.Take(3), null);

            var since = DateTime.UtcNow;

            Assert.False(await BlockValidatorService.ValidateBlock(block, false, blockDownloads: true));

            // It was given the record it lacks, and a block that carries a record must have that committee's certificate.
            Assert.Contains(StepsSince(since), s => s.EndsWith("-cert"));
            Assert.Equal(H, Globals.LastBlock.Height);
        }

        // ── Fork choice ──────────────────────────────────────────────────────────────────────────

        private static ForkChoiceUtility.BranchSummary Branch(string hash, long tip, int weight, int? rung) =>
            new() { HashAtDivergence = hash, TipHeight = tip, CertWeight = weight, CertRuleApplicable = true, ElectionRung = rung };

        [Fact]
        public void test_fork_choice_the_ratified_branch_wins_whatever_its_weight_or_height()
        {
            var ratified = Branch("zz", 10, 3, 0);
            var old = Branch("aa", 100, 5, null);
            Assert.Equal(ForkChoiceUtility.Outcome.LocalWins, ForkChoiceUtility.CompareBranches(ratified, old));
            Assert.Equal(ForkChoiceUtility.Outcome.RemoteWins, ForkChoiceUtility.CompareBranches(old, ratified));
            Assert.True(ForkChoiceUtility.DecidedOnElection(old, ratified));
            Assert.False(ForkChoiceUtility.DecidedOnCertWeight(old, ratified));
        }

        [Fact]
        public void test_fork_choice_lower_rung_wins_and_is_symmetric()
        {
            var low = Branch("zz", 10, 3, 0);
            var high = Branch("aa", 100, 5, 3);
            Assert.Equal(ForkChoiceUtility.Outcome.LocalWins, ForkChoiceUtility.CompareBranches(low, high));
            Assert.Equal(ForkChoiceUtility.Outcome.RemoteWins, ForkChoiceUtility.CompareBranches(high, low));
        }

        [Fact]
        public void test_fork_choice_falls_back_to_the_existing_rule()
        {
            Assert.Equal(ForkChoiceUtility.Outcome.LocalWins, ForkChoiceUtility.CompareBranches(Branch("zz", 10, 5, null), Branch("aa", 100, 3, null)));
            Assert.Equal(ForkChoiceUtility.Outcome.RemoteWins, ForkChoiceUtility.CompareBranches(Branch("zz", 10, 3, 1), Branch("aa", 10, 3, 1)));
        }

        [Fact]
        public void NoReorgCrossesARatifiedRecord_ForABranchThatDoesNotOutrankIt()
        {
            var rung0 = Ratified(0);
            Assert.True(CasterMembershipStore.TryAppend(rung0, out _));
            StallAt(H, 700);
            var rung3 = Ratified(3);
            var parentHash = Globals.LastBlock.Hash;
            var bare = BlockAt(H + 1, parentHash, 0, _committee.Take(3));
            var higherRung = BlockAt(H + 1, parentHash, 0, Members(rung3).Take(3), rung3);

            var head = CasterMembershipStore.GetCurrent();
            Assert.NotNull(VreeElection.CrossingRefusal(head, H + 1, H + 8, bare, CasterMembershipStore.GetBySeq));
            Assert.NotNull(VreeElection.CrossingRefusal(head, H + 1, H + 8, higherRung, CasterMembershipStore.GetBySeq));
            Assert.NotNull(VreeElection.CrossingRefusal(head, H - 2, H + 8, bare, CasterMembershipStore.GetBySeq)); // diverges below the record: its electors stood on our tip
            Assert.Null(VreeElection.CrossingRefusal(head, H + 9, H + 12, bare, CasterMembershipStore.GetBySeq)); // above the record: an ordinary fork
            Assert.Null(VreeElection.CrossingRefusal(_head, H + 1, H + 8, bare, CasterMembershipStore.GetBySeq)); // no record here

            // An ordinary record after the election does not end the protection.
            var demotion = Signed(Ordinary(rung0.RecordSeq + 1, H + 30, rung0.RecordHash, Members(rung0).Take(4), "Demotion"), Members(rung0).Take(3));
            Store(demotion);
            Assert.NotNull(VreeElection.CrossingRefusal(demotion, H + 1, H + 40, bare, CasterMembershipStore.GetBySeq));
        }

        // ── The term limit ───────────────────────────────────────────────────────────────────────

        private CasterMembershipRecord InstallInterim()
        {
            var record = Ratified(0);
            Assert.True(CasterMembershipStore.TryAppend(record, out var why), why);
            return record;
        }

        [Fact]
        public void test_a_captured_interim_committee_cannot_produce_past_the_lapse()
        {
            var interim = InstallInterim();
            var L = interim.LapseHeight;
            var signers = Members(interim).Take(3).ToList();
            StallAt(L + 12, 5);
            Assert.True(ConsensusCertificateVerifier.VerifyOrNotRequired(BlockAt(L + 12, "p", 0, signers)));
            Assert.False(ConsensusCertificateVerifier.VerifyOrNotRequired(BlockAt(L + 13, "p", 0, signers)));
            Globals.IsBlockCaster = true;
            Assert.NotNull(VreeService.StandDownReason(L + 13));
        }

        [Fact]
        public void AnOrdinaryRotationDoesNotEscapeTheTerm()
        {
            var interim = InstallInterim();
            var L = interim.LapseHeight;
            var demoted = Signed(Ordinary(interim.RecordSeq + 1, H + 30, interim.RecordHash, Members(interim).Take(4), "Demotion"), Members(interim).Take(3));
            StallAt(H + 20, 5);
            Assert.True(CasterMembershipStore.TryAppend(demoted, out var why), why);

            Assert.Equal(interim.RecordHash, VreeElection.TermOf(CasterMembershipStore.GetCurrent(), CasterMembershipStore.GetBySeq)!.RecordHash);
            StallAt(L + 13, 5);
            Assert.False(ConsensusCertificateVerifier.VerifyOrNotRequired(BlockAt(L + 13, "p", 0, Members(demoted).Take(3))));

            // And the rotation still opens, on top of the demotion.
            StallAt(L, 5);
            VreeElection.ClearCachesForTests();
            var view = View();
            Assert.True(view.IsRotation);
            Assert.Equal(demoted.RecordHash, view.Prev.RecordHash);
        }

        [Fact]
        public void ACommitteeChangesNothingFromItsLapse_OrPastIt()
        {
            var interim = InstallInterim();
            var L = interim.LapseHeight;
            var pastTerm = Signed(Ordinary(interim.RecordSeq + 1, L + 1, interim.RecordHash, Members(interim).Take(4), "Demotion"), Members(interim).Take(3));
            StallAt(L - 100, 5);
            Assert.False(CasterMembershipStore.ValidateSuccessor(interim, pastTerm, out var a));
            Assert.Contains("past the end", a);

            var atLapse = Signed(Ordinary(interim.RecordSeq + 1, L - 1, interim.RecordHash, Members(interim).Take(4), "Demotion"), Members(interim).Take(3));
            StallAt(L, 5);
            Assert.False(CasterMembershipStore.ValidateSuccessor(interim, atLapse, out var b));
            Assert.Contains("term ended", b);

            // A node far past the handover rebuilding its record chain still takes what the committee signed during its term.
            StallAt(L + VreeElection.HandoverBlocks + 1, 5);
            Assert.True(CasterMembershipStore.ValidateSuccessor(interim, atLapse, out var rebuilt), rebuilt);
        }

        [Fact]
        public void test_drill_7_handover_rung_0_takes_effect_twelve_blocks_on_and_the_interim_produces_until_then()
        {
            var interim = InstallInterim();
            var L = interim.LapseHeight;
            StallAt(L, 5);
            VreeElection.ClearCachesForTests();
            var view = View();
            Assert.True(view.IsRotation);
            Assert.Equal(0, view.ClockRung);
            Assert.Null(view.RungClosed(0));
            Assert.Equal(L + VreeElection.HandoverBlocks, view.EffectiveFor(0));

            var electors = view.Epoch.Electors.Keys.OrderBy(a => a, StringComparer.Ordinal).Take(view.Epoch.RatifyNeeded).Select(Of);
            var rotation = Signed(view.Build(0), electors);
            Assert.Equal(0, rotation.LapseHeight);
            Assert.True(CasterMembershipStore.TryAppend(rotation, out var why), why);

            // Pending: the seats stay with the interim, and the live list does not move.
            Assert.Equal(interim.RecordHash, CasterMembershipStore.GetSeatRecord()!.RecordHash);
            Assert.Equal(interim.RecordHash, CasterMembershipStore.GetForHeight(L + 11)!.RecordHash);
            Assert.Empty(Globals.BlockCasters);
            CasterMembershipService.ReconcileBlockCastersToRecord(rotation);
            Assert.Empty(Globals.BlockCasters);

            // At the block before its height the rotation takes the seats.
            StallAt(L + 11, 5);
            Assert.Equal(rotation.RecordHash, CasterMembershipStore.GetSeatRecord()!.RecordHash);
            Assert.Equal(rotation.RecordHash, CasterMembershipStore.GetForHeight(L + 12)!.RecordHash);
            Assert.Null(VreeElection.TermOf(rotation, CasterMembershipStore.GetBySeq));

            // Its first block carries it.
            var first = BlockAt(L + 12, Globals.LastBlock.Hash, 0, Members(rotation).Take(3));
            Assert.Null(VreeElection.PrepareBlockRecord(first));
            Assert.Equal(rotation.RecordHash, first.MembershipRecord!.RecordHash);
            Assert.True(ConsensusCertificateVerifier.VerifyOrNotRequired(first));
        }

        [Fact]
        public void test_the_rotation_excludes_the_interim_and_the_old_committee_stands_again()
        {
            var interim = InstallInterim();
            StallAt(interim.LapseHeight, 5);
            VreeElection.ClearCachesForTests();
            var epoch = View().Epoch;
            foreach (var c in interim.Casters) Assert.DoesNotContain(c.Address, epoch.Electors.Keys);
            foreach (var m in _committee) Assert.Contains(m.Address, epoch.Electors.Keys);
        }

        [Fact]
        public void test_grinding_the_election_seed_does_not_move_the_rotation_rung()
        {
            var interim = InstallInterim();
            var L = interim.LapseHeight;
            StallAt(L, 5);
            VreeElection.ClearCachesForTests();
            var before = VreeElection.RungMembers(View().Epoch.Ranked, 0);

            // A different anchor for the stall's epoch moves the election's rungs, and not the rotation's.
            var electionAnchor = VrfSeed.AnchorHeight(H);
            var electionBefore = VreeElection.EpochForStall(H, "h")!.Ranked;
            VreeElection.ClearCachesForTests();
            _hashes[electionAnchor] = "ground-anchor";
            StallAt(L, 5);
            var after = VreeElection.RungMembers(View().Epoch.Ranked, 0);
            Assert.Equal(before, after);
            VreeElection.ClearCachesForTests();
            Assert.NotEqual(electionBefore, VreeElection.EpochForStall(H, "h")!.Ranked);
        }

        [Fact]
        public void test_drill_7c_no_rotation_ratifies_the_stall_is_at_plus_12_and_later_rungs_chain_on_the_interim()
        {
            var interim = InstallInterim();
            var L = interim.LapseHeight;
            StallAt(L + 12, 400);
            VreeElection.ClearCachesForTests();
            var view = View();
            Assert.True(view.IsRotation);
            Assert.NotNull(view.RungClosed(0));
            Assert.Null(view.RungClosed(1));
            Assert.Equal(L + 13, view.EffectiveFor(1));
            var electors = view.Epoch.Electors.Keys.OrderBy(a => a, StringComparer.Ordinal).Take(view.Epoch.RatifyNeeded).Select(Of);
            var rung1 = Signed(view.Build(1), electors);
            Assert.Equal(interim.RecordHash, rung1.PrevRecordHash);
            Assert.True(CasterMembershipStore.TryAppend(rung1, out var why), why);
        }

        [Fact]
        public void test_drill_7b_rung_0_ratifies_but_is_dead_the_stall_is_at_plus_11_and_later_rungs_are_siblings()
        {
            var interim = InstallInterim();
            var L = interim.LapseHeight;
            StallAt(L, 5);
            VreeElection.ClearCachesForTests();
            var view0 = View();
            var electors = view0.Epoch.Electors.Keys.OrderBy(a => a, StringComparer.Ordinal).Take(view0.Epoch.RatifyNeeded).Select(Of).ToList();
            var rotation0 = Signed(view0.Build(0), electors);
            Assert.True(CasterMembershipStore.TryAppend(rotation0, out _));

            StallAt(L + 11, 400);
            VreeElection.ClearCachesForTests();
            var view = View();
            Assert.True(view.IsRotation);
            Assert.Equal(interim.RecordHash, view.Prev.RecordHash);
            Assert.Equal(L + 12, view.EffectiveFor(1));
            var rung1 = Signed(view.Build(1), electors);
            Assert.Equal(rotation0.RecordSeq, rung1.RecordSeq);
            Assert.True(CasterMembershipStore.ReplaceHeadWithOwnRung(rung1, out var why), why);
            Assert.Equal(rung1.RecordHash, CasterMembershipStore.GetCurrent()!.RecordHash);
        }

        // ── End to end: electors signing, a rung collecting ──────────────────────────────────────

        private void RouteSignatureRequests(HashSet<string>? dead = null) =>
            VreeService.HttpGet = (ip, path) =>
            {
                if (dead?.Contains(ip) == true || !_byIp.TryGetValue(ip, out var member) || !path.StartsWith("GetElectionSignature/")) return Task.FromResult<string?>(null);
                var parts = path.Split('/');
                _who.Value = member;
                return Task.FromResult<string?>(JsonConvert.SerializeObject(VreeService.AnswerSignature(long.Parse(parts[1]), int.Parse(parts[2]))));
            };

        [Fact]
        public async Task test_dead_majority_rung_0_ratifies_and_the_chain_moves()
        {
            VreeService.NoteSurveyForTests(Globals.LastBlock.Hash, committeeUnreachable: true);
            RouteSignatureRequests();
            var announced = new System.Collections.Concurrent.ConcurrentBag<string>();
            VreeService.HttpPost = (ip, path, _) => { announced.Add($"{ip}/{path}"); return Task.FromResult<string?>("Appended"); };

            var view = View();
            var member = Of(VreeElection.RungMembers(view.Epoch.Ranked, 0)[0]);
            _who.Value = member;
            Globals.ValidatorAddress = member.Address;
            await VreeService.CollectRungAsync(view, 0, member.Address);

            var head = CasterMembershipStore.GetCurrent()!;
            Assert.Equal(VreeElection.EmergencyElectionChangeType, head.ChangeType);
            Assert.Equal(0, head.Rung);
            Assert.Equal(H + 1, head.EffectiveFromHeight);
            Assert.Equal(view.Epoch.RatifyNeeded, head.Signatures.Count);
            Assert.Contains(head.Casters, c => c.Address == member.Address);

            // It told the electorate, not just the twenty peers an ordinary record reaches: the casters they pull from are dead.
            Assert.Equal(view.Epoch.Electors.Count, announced.Count(a => a.EndsWith("AnnounceMembershipRecord")));
            // Its members cast without a resume: nobody could approve one.
            Assert.True(VreeElection.FreshlyElected(head, H, member.Address));
            Assert.False(VreeElection.FreshlyElected(head, H, _electors.First(e => !head.Casters.Any(c => c.Address == e.Address)).Address));
            Assert.False(VreeElection.FreshlyElected(head, H + 1, member.Address));

            // H + 1 under the new committee, on a node that holds the record.
            var block = BlockAt(H + 1, Globals.LastBlock.Hash, 0, Members(head).Take(3));
            Assert.Null(VreeElection.PrepareBlockRecord(block));
            Assert.True(ConsensusCertificateVerifier.VerifyOrNotRequired(block));
        }

        [Fact]
        public async Task test_rung_0_dies_before_ratifying_rung_1_follows_at_its_own_opening()
        {
            StallAt(H, 365);
            VreeService.NoteSurveyForTests(Globals.LastBlock.Hash, committeeUnreachable: true);
            var view = View();
            Assert.Equal(1, view.ClockRung);
            var rung0 = VreeElection.RungMembers(view.Epoch.Ranked, 0).Select(Of).Take(3).Select(m => m.Ip).ToHashSet();
            RouteSignatureRequests(dead: rung0);

            var member = Of(VreeElection.RungMembers(view.Epoch.Ranked, 1)[0]);
            _who.Value = member;
            Globals.ValidatorAddress = member.Address;
            await VreeService.CollectRungAsync(view, 1, member.Address);

            var head = CasterMembershipStore.GetCurrent()!;
            Assert.Equal(1, head.Rung);
        }

        [Fact]
        public async Task test_finding_2_killing_a_whole_rung_can_starve_the_electorate()
        {
            VreeService.NoteSurveyForTests(Globals.LastBlock.Hash, committeeUnreachable: true);
            var view = View();
            var rung0 = VreeElection.RungMembers(view.Epoch.Ranked, 0);
            RouteSignatureRequests(dead: rung0.Select(Of).Select(m => m.Ip).ToHashSet());
            var member = Of(VreeElection.RungMembers(view.Epoch.Ranked, 1)[0]);
            _who.Value = member;
            await VreeService.CollectRungAsync(view, 0, member.Address); // 7 live signers, 8 needed
            Assert.Equal(_head.RecordHash, CasterMembershipStore.GetCurrent()!.RecordHash);
        }

        [Fact]
        public async Task ACollectorIgnoresABadSignature_AndAnElectorAnsweringForAnother()
        {
            VreeService.NoteSurveyForTests(Globals.LastBlock.Hash, committeeUnreachable: true);
            var view = View();
            // The two that sort first, so their signatures would be the first taken if they were collected.
            var sorted = view.Epoch.Electors.Keys.OrderBy(a => a, StringComparer.Ordinal).ToList();
            var liar = Of(sorted[0]);
            var impostor = Of(sorted[1]);
            VreeService.HttpGet = (ip, _) =>
            {
                var m = _byIp[ip];
                _who.Value = m;
                var answer = VreeService.AnswerSignature(H, 0);
                if (m == liar) answer.Signature = "junk.junk";
                if (m == impostor) answer.SignerAddress = Of(sorted[2]).Address;
                return Task.FromResult<string?>(JsonConvert.SerializeObject(answer));
            };
            var member = Of(VreeElection.RungMembers(view.Epoch.Ranked, 0).First(a => a != liar.Address && a != impostor.Address));
            _who.Value = member;
            await VreeService.CollectRungAsync(view, 0, member.Address);
            var head = CasterMembershipStore.GetCurrent()!;
            Assert.Equal(VreeElection.EmergencyElectionChangeType, head.ChangeType); // 10 honest of 12, 8 needed
            Assert.DoesNotContain(head.Signatures, s => s.SignerAddress == liar.Address || s.SignerAddress == impostor.Address);
        }

        [Fact]
        public void SigningFollowsTheChecksThroughAnswerSignature()
        {
            _who.Value = _electors[0];
            Assert.Contains("rule 8", VreeService.AnswerSignature(H, 0).Refusal);
            VreeService.NoteSurveyForTests(Globals.LastBlock.Hash, committeeUnreachable: false);
            Assert.Contains("rule 3", VreeService.AnswerSignature(H, 0).Refusal);
            VreeService.NoteSurveyForTests(Globals.LastBlock.Hash, committeeUnreachable: true);
            VreeService.NoteEvidence(H + 1, "certified");
            Assert.Contains("rule 2", VreeService.AnswerSignature(H, 0).Refusal);
            VreeService.ResetEvidenceForTests();
            Assert.NotEmpty(VreeService.AnswerSignature(H, 0).Signature);
            Assert.Contains("no election is open", VreeService.AnswerSignature(H - 1, 0).Refusal);
            _who.Value = _committee[0];
            Assert.Contains("not an elector", VreeService.AnswerSignature(H, 0).Refusal);
        }

        // ── Rule 2: claims ───────────────────────────────────────────────────────────────────────

        private void Serve(Dictionary<string, Block?> byIp) =>
            VreeService.HttpGet = (ip, path) => Task.FromResult(byIp.TryGetValue(ip, out var b) && path.StartsWith("GetBlock/") ? (b == null ? "0" : JsonConvert.SerializeObject(b)) : null);

        private Block RealBlock(long height, string prev, IEnumerable<Member> attestors)
        {
            var block = new Block { Height = height, PrevHash = prev, Validator = _electors[0].Address, Version = 4, Timestamp = TimeUtil.GetTime(), MerkleRoot = "m", NumOfTx = 0, ChainRefId = "c", ValidatorAnswer = "a", TotalValidators = 0, Transactions = new List<Transaction>() };
            block.Hash = block.GetBlockHash();
            var msg = ConsensusMessageFormatter.FormatAttestationV1(block.Height, block.Hash, block.Validator, block.PrevHash);
            block.ConsensusCertificate = new ConsensusCertificate
            {
                BlockHeight = block.Height, BlockHash = block.Hash, WinnerAddress = block.Validator, PrevHash = block.PrevHash,
                Attestations = attestors.Select(m => new CasterAttestation { CasterAddress = m.Address, Signature = SignatureService.CreateSignature(msg, m.Key, m.Pub) }).ToList(),
            };
            return block;
        }

        [Fact]
        public async Task test_drill_5_a_peer_claiming_h_plus_1000_changes_nothing()
        {
            _who.Value = _electors[0];
            VreeService.NoteSurveyForTests(Globals.LastBlock.Hash, committeeUnreachable: true);
            Serve(new Dictionary<string, Block?> { ["9.9.9.1"] = null, ["9.9.9.2"] = RealBlock(H + 1000, "p", _electors.Take(3)) }); // no block; a block with no valid certificate

            await VreeService.VerifyClaimsAsync(View(), new[]
            {
                new NetworkStallSurvey.Reply { Ip = "9.9.9.1", Height = H + 1000, TipHash = "x" },
                new NetworkStallSurvey.Reply { Ip = "9.9.9.2", Height = H + 1000, TipHash = "y" },
                new NetworkStallSurvey.Reply { Ip = "9.9.9.3", Height = H, TipHash = "other-hash" },
            });

            Assert.False(VreeService.HasEvidenceAgainst(H, Globals.LastBlock.Hash));
            Assert.NotEmpty(VreeService.AnswerSignature(H, 0).Signature);
        }

        [Fact]
        public async Task test_verified_peer_ahead_refuses_the_signature_for_the_right_reason()
        {
            _who.Value = _electors[0];
            VreeService.NoteSurveyForTests(Globals.LastBlock.Hash, committeeUnreachable: true);
            var real = RealBlock(H + 1, Globals.LastBlock.Hash, _committee.Take(3));
            Serve(new Dictionary<string, Block?> { ["9.9.9.1"] = real });

            await VreeService.VerifyClaimsAsync(View(), new[] { new NetworkStallSurvey.Reply { Ip = "9.9.9.1", Height = H + 1, TipHash = real.Hash } });

            Assert.True(VreeService.HasEvidenceAgainst(H, Globals.LastBlock.Hash));
            Assert.Contains("rule 2", VreeService.AnswerSignature(H, 0).Refusal);
            Assert.False(VreeService.HasEvidenceAgainst(H + 1, real.Hash)); // gone once the tip reaches the block
        }

        [Fact]
        public async Task AFloodOfClaimsFromUnbondedPeersCannotCrowdOutAnElectorsRealOne()
        {
            _who.Value = _electors[0];
            var real = RealBlock(H + 1, Globals.LastBlock.Hash, _committee.Take(3));
            Serve(new Dictionary<string, Block?> { [_electors[5].Ip] = real });
            var flood = Enumerable.Range(0, 50).Select(i => new NetworkStallSurvey.Reply { Ip = $"9.9.{i}.1", Height = H + 2 + i, TipHash = $"junk{i}" });

            await VreeService.VerifyClaimsAsync(View(), flood.Append(new NetworkStallSurvey.Reply { Ip = _electors[5].Ip, Height = H + 1, TipHash = real.Hash }));

            Assert.True(VreeService.HasEvidenceAgainst(H, Globals.LastBlock.Hash));
        }

        [Fact]
        public void EvidenceNobodyShowsAgainExpires_SoADeadCommitteesLastBlockCannotVetoForGood()
        {
            _who.Value = _electors[0];
            VreeService.NoteSurveyForTests(Globals.LastBlock.Hash, committeeUnreachable: true);
            VreeService.NoteEvidence(H + 1, "seen-once", TimeUtil.GetTime() - VreeElection.ElectSeconds + 30);
            Assert.True(VreeService.HasEvidenceAgainst(H, Globals.LastBlock.Hash));
            VreeService.NoteEvidence(H + 1, "seen-once", TimeUtil.GetTime() - VreeElection.ElectSeconds);
            Assert.False(VreeService.HasEvidenceAgainst(H, Globals.LastBlock.Hash));
            Assert.NotEmpty(VreeService.AnswerSignature(H, 0).Signature);
        }

        [Fact]
        public void test_a_validator_refuses_during_the_hold_after_a_ratified_rung()
        {
            _who.Value = _electors[0];
            VreeService.NoteSurveyForTests(Globals.LastBlock.Hash, committeeUnreachable: true);
            Assert.NotEmpty(VreeService.AnswerSignature(H, 0).Signature);
            VreeElection.NoteRatified(Globals.LastBlock.Hash, "a-rung");
            Assert.Contains("rule 7", VreeService.AnswerSignature(H, 0).Refusal);
        }

        // ── Layer 2 end to end: a caster probing, validators answering ───────────────────────────

        private Block CertifiedTipOf(IEnumerable<Member> committee, long height, long age)
        {
            var parent = VrfSeed.BlockHashAt(height - 1);
            var block = BlockAt(height, parent, age, committee.Take(3), null, VrfSeed.BlockHashAt(height));
            _blocks[height] = block;
            Globals.LastBlock = block;
            return block;
        }

        private void RouteProbes(HashSet<string>? dead = null) =>
            VreeService.HttpPost = async (ip, path, json) =>
            {
                if (dead?.Contains(ip) == true || !_byIp.TryGetValue(ip, out var member) || path != "ProbeTip") return null;
                _who.Value = member;
                return JsonConvert.SerializeObject(await VreeService.AnswerProbeAsync(JsonConvert.DeserializeObject<VreeProbe>(json), ip));
            };

        private HashSet<string> CommitteeSet() => _committee.Select(m => m.Address).ToHashSet(StringComparer.Ordinal);

        [Fact]
        public async Task test_probe_every_reachable_validator_acknowledges_a_certified_tip_and_the_count_is_on_health()
        {
            var tip = CertifiedTipOf(_committee, H, 5);
            RouteProbes();
            Globals.IsBlockCaster = true;
            await VreeService.ProbeOnceAsync(tip, CommitteeSet(), _committee[0].Address, TimeUtil.GetTime());
            var status = VreeService.Status();
            Assert.Equal(12, status.ReachSet);
            Assert.Equal(12, status.AckCount);
            Assert.Null(status.StandDown);
        }

        [Fact]
        public async Task AnAcknowledgementCountsOnlyWithItsValidatorsSignature_OnThisTip()
        {
            var tip = CertifiedTipOf(_committee, H, 5);
            var wrongTip = VreeService.AckMessage(H - 1, "older");
            VreeService.HttpPost = (ip, _, _) =>
            {
                var m = _byIp[ip];
                var i = _electors.IndexOf(m);
                var answer = i switch
                {
                    0 => new VreeProbeAnswer { Ack = true, Address = m.Address, Signature = "junk.junk" },                                                        // no signature
                    1 => new VreeProbeAnswer { Ack = true, Address = m.Address, Signature = SignatureService.CreateSignature(wrongTip, m.Key, m.Pub) },             // an old tip's
                    2 => new VreeProbeAnswer { Ack = true, Address = _electors[3].Address, Signature = SignatureService.CreateSignature(VreeService.AckMessage(tip.Height, tip.Hash), m.Key, m.Pub) }, // another's name
                    3 => new VreeProbeAnswer { Ack = false, Address = m.Address, Signature = SignatureService.CreateSignature(VreeService.AckMessage(tip.Height, tip.Hash), m.Key, m.Pub) },        // says no
                    4 => new VreeProbeAnswer { Ack = true, Address = m.Address, Signature = SignatureService.CreateSignature(VreeService.AckMessage(tip.Height, tip.Hash), m.Key, m.Pub) },         // the real one
                    _ => null,
                };
                return Task.FromResult<string?>(answer == null ? null : JsonConvert.SerializeObject(answer));
            };
            await VreeService.ProbeOnceAsync(tip, CommitteeSet(), _committee[0].Address, TimeUtil.GetTime());
            Assert.Equal(1, VreeService.Status().AckCount);
        }

        [Fact]
        public async Task test_drill_3_a_caster_that_lost_the_validators_stands_down_and_resumes_without_an_election()
        {
            var tip = CertifiedTipOf(_committee, H, 5);
            Globals.IsBlockCaster = true;
            var longAgo = TimeUtil.GetTime() - VreeElection.ReachWindowSeconds - 1;

            // Only 4 of 12 answer: exactly a third, and a third is not enough.
            var isolated = _electors.Skip(4).Select(m => m.Ip).ToHashSet();
            RouteProbes(dead: isolated);
            await VreeService.ProbeOnceAsync(tip, CommitteeSet(), _committee[0].Address, longAgo);
            await VreeService.ProbeOnceAsync(tip, CommitteeSet(), _committee[0].Address, TimeUtil.GetTime());
            Assert.Equal(4, VreeService.Status().AckCount);
            Assert.NotNull(VreeService.StandDownReason(H + 1));

            // It signs nothing while it stands down.
            Assert.NotNull(VreeService.Status().StandDown);

            // One more answers: the validators are back, and so is the caster.
            RouteProbes(dead: _electors.Skip(5).Select(m => m.Ip).ToHashSet());
            await VreeService.ProbeOnceAsync(tip, CommitteeSet(), _committee[0].Address, TimeUtil.GetTime());
            Assert.Equal(5, VreeService.Status().AckCount);
            Assert.Null(VreeService.StandDownReason(H + 1));
        }

        [Fact]
        public async Task ARungBeforeItsFirstBlock_DoesNotStandDown_ThoughValidatorsHoldingASiblingDoNotAcknowledgeIt()
        {
            var tip = CertifiedTipOf(_committee, H, 400);
            var record = Ratified(0);
            Assert.True(CasterMembershipStore.TryAppend(record, out _));
            var self = record.Casters[0].Address;
            Globals.ValidatorAddress = self;
            Globals.IsBlockCaster = true;
            var set = record.Casters.Select(c => c.Address).ToHashSet(StringComparer.Ordinal);
            await VreeService.ProbeOnceAsync(tip, set, self, TimeUtil.GetTime() - VreeElection.ReachWindowSeconds - 1);
            await VreeService.ProbeOnceAsync(tip, set, self, TimeUtil.GetTime()); // nobody answers
            Assert.Null(VreeService.StandDownReason(H + 1));

            StallAt(H + 1, 5); // its first block is out: from here it needs the validators like any committee
            Assert.NotNull(VreeService.StandDownReason(H + 2));
        }

        [Fact]
        public void TheOutgoingCommittee_IsStillAcknowledgedByARotationsSigners()
        {
            var interim = InstallInterim();
            StallAt(interim.LapseHeight, 5);
            VreeElection.ClearCachesForTests();
            var signer = _committee[0]; // the old committee stands again, and elects
            _who.Value = signer;
            VreeService.NoteSurveyForTests(Globals.LastBlock.Hash, committeeUnreachable: false);
            Assert.NotEmpty(VreeService.AnswerSignature(interim.LapseHeight, 0).Signature);
            Assert.Equal(0, VreeService.LastSignedForTests);
        }

        [Fact]
        public void TheStandDownDecision_GivesAFreshCasterOneWindow_AndATinySetNothingToFear()
        {
            Assert.Null(VreeService.StandDownDecision(true, false, true, firstProbeUnix: 0, now: 1000, acks: 0, reachSet: 100));
            Assert.Null(VreeService.StandDownDecision(true, false, true, firstProbeUnix: 1000 - VreeElection.ReachWindowSeconds + 1, now: 1000, acks: 0, reachSet: 100));
            Assert.NotNull(VreeService.StandDownDecision(true, false, true, firstProbeUnix: 1000 - VreeElection.ReachWindowSeconds, now: 1000, acks: 0, reachSet: 100));
            Assert.Null(VreeService.StandDownDecision(true, false, true, firstProbeUnix: 1, now: 1000, acks: 0, reachSet: 3));
            Assert.Null(VreeService.StandDownDecision(true, bootstrap: true, true, firstProbeUnix: 1, now: 1000, acks: 0, reachSet: 100));
            Assert.Null(VreeService.StandDownDecision(true, false, caster: false, firstProbeUnix: 1, now: 1000, acks: 0, reachSet: 100));
        }

        private VreeProbe ProbeFor(Block tip, string prober, ConsensusCertificate? certificate = null) => new()
        {
            Height = tip.Height, Hash = tip.Hash, PrevHash = tip.PrevHash, Validator = tip.Validator, Version = tip.Version, Certificate = certificate ?? tip.ConsensusCertificate, Prober = prober,
        };

        [Fact]
        public async Task test_probe_a_validator_refuses_what_it_cannot_verify()
        {
            var tip = CertifiedTipOf(_committee, H, 5);
            _who.Value = _electors[0];

            var ok = await VreeService.AnswerProbeAsync(ProbeFor(tip, _committee[0].Address), null);
            Assert.True(ok.Ack);
            Assert.True(SignatureService.VerifySignature(_electors[0].Address, VreeService.AckMessage(tip.Height, tip.Hash), ok.Signature));

            var outsider = await VreeService.AnswerProbeAsync(ProbeFor(tip, _electors[3].Address), null);
            Assert.False(outsider.Ack);

            VreeService.ResetForTests();
            var forgedCert = BlockAt(H, tip.PrevHash, 5, _electors.Take(3), null, tip.Hash).ConsensusCertificate;
            var forged = await VreeService.AnswerProbeAsync(ProbeFor(tip, _committee[0].Address, forgedCert), null);
            Assert.False(forged.Ack);

            Globals.IsChainSynced = false;
            Assert.False((await VreeService.AnswerProbeAsync(ProbeFor(tip, _committee[0].Address), null)).Ack);
            Globals.IsChainSynced = true;

            _who.Value = null;
            Assert.False((await VreeService.AnswerProbeAsync(ProbeFor(tip, _committee[0].Address), null)).Ack);
        }

        [Fact]
        public async Task test_drill_4_one_way_a_validator_that_acknowledges_a_block_above_its_tip_refuses_to_sign()
        {
            // The validator's tip stopped at H; the casters' probes still reach it, with H + 2.
            StallAt(H, 400);
            var committee = _committee;
            var ahead = BlockAt(H + 2, "p", 5, committee.Take(3));
            _who.Value = _electors[0];
            VreeService.NoteSurveyForTests(Globals.LastBlock.Hash, committeeUnreachable: true);

            var answer = await VreeService.AnswerProbeAsync(ProbeFor(ahead, committee[0].Address), null);

            Assert.True(answer.Ack);
            Assert.Contains("rule 2", VreeService.AnswerSignature(H, 0).Refusal);
        }

        [Fact]
        public async Task test_drill_5_one_free_peer_replaying_the_tip_vetoes_nothing()
        {
            // Anyone can replay the certified tip this validator already holds, every minute: it is no sign the committee is alive.
            var tip = CertifiedTipOf(_committee, H, 400);
            _who.Value = _electors[0];
            VreeService.NoteSurveyForTests(tip.Hash, committeeUnreachable: true);

            await VreeService.AnswerProbeAsync(ProbeFor(tip, _committee[0].Address), "1.2.3.4");

            Assert.NotEmpty(VreeService.AnswerSignature(H, 0).Signature);
        }

        [Fact]
        public async Task test_a_validator_that_signed_is_not_counted_on_the_other_side_for_the_hold()
        {
            var tip = CertifiedTipOf(_committee, H, 400);
            _who.Value = _electors[0];
            VreeService.NoteSurveyForTests(tip.Hash, committeeUnreachable: true);
            Assert.NotEmpty(VreeService.AnswerSignature(H, 0).Signature);

            var answer = await VreeService.AnswerProbeAsync(ProbeFor(tip, _committee[0].Address), null);
            Assert.False(answer.Ack);
            Assert.Contains("signed", answer.Reason);
        }

        [Fact]
        public async Task ASignerAcknowledgesTheCommitteeItElected_OrTheNewCommitteeWouldStandDown()
        {
            var tip = CertifiedTipOf(_committee, H, 400);
            var signer = _electors[0];
            _who.Value = signer;
            VreeService.NoteSurveyForTests(tip.Hash, committeeUnreachable: true);
            Assert.NotEmpty(VreeService.AnswerSignature(H, 0).Signature);

            var record = Ratified(0);
            Assert.True(CasterMembershipStore.TryAppend(record, out _));
            var answer = await VreeService.AnswerProbeAsync(ProbeFor(tip, record.Casters[0].Address), null);
            Assert.True(answer.Ack, answer.Reason);
        }

        [Fact]
        public async Task AVerifiedHashCannotBeReplayedAtAnotherHeight_AFreeVetoOfEveryElection()
        {
            var tip = CertifiedTipOf(_committee, H, 400);
            _who.Value = _electors[0];
            VreeService.NoteSurveyForTests(tip.Hash, committeeUnreachable: true);
            Assert.True((await VreeService.AnswerProbeAsync(ProbeFor(tip, _committee[0].Address), null)).Ack);

            var lie = ProbeFor(tip, _committee[0].Address);
            lie.Height = 999_999_999;
            Assert.False((await VreeService.AnswerProbeAsync(lie, null)).Ack);
            Assert.False(VreeService.HasEvidenceAgainst(H, tip.Hash));
            Assert.NotEmpty(VreeService.AnswerSignature(H, 0).Signature);
        }

        [Fact]
        public async Task test_a_block_in_another_chain_is_evidence_and_no_acknowledgement()
        {
            var tip = CertifiedTipOf(_committee, H, 400);
            var other = BlockAt(H, tip.PrevHash, 400, _committee.Take(3), null, "another-block-at-h");
            _who.Value = _electors[0];
            var answer = await VreeService.AnswerProbeAsync(ProbeFor(other, _committee[0].Address), null);
            Assert.False(answer.Ack);
            Assert.True(VreeService.HasEvidenceAgainst(H, tip.Hash));
        }

        [Fact]
        public async Task test_drill_9_a_validator_behind_on_the_record_adopts_it_from_the_probe_before_it_answers()
        {
            // A promotion: the validator has seq 3, the record the new tip was certified under is seq 4.
            var promoted = Signed(Ordinary(HeadSeq + 1, H, _head.RecordHash, _committee.Take(4).Append(_electors[11]), "Promotion"), _committee.Take(3));
            var tipBefore = Globals.LastBlock;
            StallAt(H - 1, 5);
            var tip = BlockAt(H, Globals.LastBlock.Hash, 5, new[] { _committee[0], _committee[1], _electors[11] });
            _who.Value = _electors[0];
            var probe = ProbeFor(tip, _committee[0].Address);

            var without = await VreeService.AnswerProbeAsync(probe, null);
            Assert.False(without.Ack); // the elector is not on the committee it holds, so the certificate falls short

            probe.Records = new List<CasterMembershipRecord> { promoted };
            var with = await VreeService.AnswerProbeAsync(probe, null);
            Assert.True(with.Ack, with.Reason);
            Assert.Equal(HeadSeq + 1, with.RecordSeq);
            Assert.Equal(promoted.RecordHash, CasterMembershipStore.GetCurrent()!.RecordHash);
        }

        [Fact]
        public async Task AnOldCommitteesProbe_IsNotAcknowledged_ByAValidatorHoldingTheElectedRecord()
        {
            var tip = CertifiedTipOf(_committee, H, 400);
            var record = Ratified(0);
            Assert.True(CasterMembershipStore.TryAppend(record, out _));
            _who.Value = _electors.First(e => !record.Casters.Any(c => c.Address == e.Address));

            var old = await VreeService.AnswerProbeAsync(ProbeFor(tip, _committee[0].Address), null);
            Assert.False(old.Ack);
            Assert.Contains("not on the committee", old.Reason);

            var fresh = await VreeService.AnswerProbeAsync(ProbeFor(tip, record.Casters[0].Address), null);
            Assert.True(fresh.Ack, fresh.Reason);
        }

        [Fact]
        public async Task ANewCommitteesFirstProbe_CarriesTheRecord_ToAValidatorThatMissedIt()
        {
            var tip = CertifiedTipOf(_committee, H, 400);
            var record = Ratified(0);
            _who.Value = _electors.First(e => !record.Casters.Any(c => c.Address == e.Address));
            var probe = ProbeFor(tip, record.Casters[0].Address);
            probe.Records = new List<CasterMembershipRecord> { record };

            var answer = await VreeService.AnswerProbeAsync(probe, null);

            Assert.True(answer.Ack, answer.Reason);
            Assert.Equal(record.RecordHash, CasterMembershipStore.GetCurrent()!.RecordHash);
        }

        [Fact]
        public void GetElectorate_ReportsTheNumbersTheActivationIsSetFrom()
        {
            var json = JsonConvert.SerializeObject(VreeService.Electorate());
            Assert.Contains("\"Electors\":12", json);
            Assert.Contains("\"RatifyNeed\":8", json);
            Assert.Contains("\"ReachNeed\":5", json);
        }
    }
}
