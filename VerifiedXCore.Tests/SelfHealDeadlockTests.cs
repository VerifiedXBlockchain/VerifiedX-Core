using VerifiedXCore.Models;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Sep 20–21 2026, testnet halted 16h at 975,533. The root pattern: every recovery mechanism was
    /// gated on a condition the failure itself made permanently true. These tests pin the pure
    /// decisions behind each fix so the pattern cannot silently return:
    ///  • ONE-QUORUM: a single majority rule feeds every gate (no seed-only relaxation);
    ///  • COMMITTEE-CAP: a record larger than the pool is refused at write time;
    ///  • ROTATION-LIVENESS: an aborted rotation's signed-seq mark expires instead of latching;
    ///  • SELF-HEAL maturity: height OR wall-clock, so a halted chain can still promote;
    ///  • WATCHDOG: only a real height change resets it;
    ///  • BOOTSTRAP-RESET: survey verdicts are fail-safe, and the ladder never proposes on blindness,
    ///    a peer ahead, a hash disagreement, or an undecidable committee without an operator.
    /// </summary>
    public class SelfHealDeadlockTests
    {
        // ── ONE-QUORUM ───────────────────────────────────────────────────

        [Theory]
        [InlineData(1, 1)]
        [InlineData(2, 2)]
        [InlineData(3, 2)]
        [InlineData(4, 3)]
        [InlineData(5, 3)]
        [InlineData(6, 4)]
        public void Quorum_IsMajority_FlooredAtOne(int n, int expected)
        {
            Assert.Equal(expected, ConsensusQuorum.Required(n));
        }

        [Fact]
        public void Quorum_ZeroDenominator_IsUnsatisfiable()
        {
            Assert.Equal(int.MaxValue, ConsensusQuorum.Required(0));
        }

        // ── COMMITTEE-CAP ────────────────────────────────────────────────

        private static CasterMembershipRecord Rec(long seq, long eff, string prev, string changeType, int casterCount)
        {
            var r = new CasterMembershipRecord
            {
                RecordSeq = seq,
                EffectiveFromHeight = eff,
                PrevRecordHash = prev,
                ChangeType = changeType,
                ChangedAddress = $"x{casterCount}",
                Casters = Enumerable.Range(1, casterCount).Select(i => new CasterInfo { Address = $"x{i}", PeerIP = $"10.0.0.{i}", PublicKey = $"pk{i}" }).ToList(),
                Signatures = new List<RecordSignature>()
            };
            r.RecordHash = CasterMembershipStore.ComputeRecordHash(r);
            return r;
        }

        [Fact]
        public void CommitteeCap_RecordLargerThanPool_RefusedBeforeSignatures()
        {
            var prev = Rec(14, 1000, "p", "Promotion", CasterDiscoveryService.MaxCasters);
            var oversized = Rec(15, 1001, prev.RecordHash, "Promotion", CasterDiscoveryService.MaxCasters + 1);
            Assert.False(CasterMembershipStore.ValidateSuccessor(prev, oversized, out var reason));
            Assert.Contains("exceeds pool cap", reason);
        }

        [Fact]
        public void CommitteeCap_RecordAtPoolSize_PassesCapCheck()
        {
            var prev = Rec(14, 1000, "p", "Promotion", CasterDiscoveryService.MaxCasters - 1);
            var atCap = Rec(15, 1001, prev.RecordHash, "Promotion", CasterDiscoveryService.MaxCasters);
            CasterMembershipStore.ValidateSuccessor(prev, atCap, out var reason);
            Assert.DoesNotContain("exceeds pool cap", reason); // fails later on signatures, never on the cap
        }

        // ── ROTATION-LIVENESS: mark expiry ───────────────────────────────

        [Fact]
        public void StaleMark_HeadAtOrPastSeq_NeverExpires()
        {
            var m = new CasterMembershipStore.SignedSeqMarker { Id = 16, RecordHash = "h", SetHash = "s", SignedAt = 1000 };
            Assert.False(CasterMembershipStore.IsStaleAbortedMark(m, 16, headSeq: 16, nowUnix: 1000 + CasterMembershipStore.SignedMarkExpirySeconds + 1));
        }

        [Fact]
        public void StaleMark_LegacyNoTimestamp_NeverExpires()
        {
            var m = new CasterMembershipStore.SignedSeqMarker { Id = 16, RecordHash = "h", SetHash = "s", SignedAt = 0 };
            Assert.False(CasterMembershipStore.IsStaleAbortedMark(m, 16, headSeq: 15, nowUnix: long.MaxValue / 2));
        }

        [Fact]
        public void StaleMark_YoungerThanExpiry_Holds()
        {
            var m = new CasterMembershipStore.SignedSeqMarker { Id = 16, RecordHash = "h", SetHash = "s", SignedAt = 1000 };
            Assert.False(CasterMembershipStore.IsStaleAbortedMark(m, 16, headSeq: 15, nowUnix: 1000 + CasterMembershipStore.SignedMarkExpirySeconds - 1));
        }

        [Fact]
        public void StaleMark_OldAndNeverAppended_Expires()
        {
            var m = new CasterMembershipStore.SignedSeqMarker { Id = 16, RecordHash = "h", SetHash = "s", SignedAt = 1000 };
            Assert.True(CasterMembershipStore.IsStaleAbortedMark(m, 16, headSeq: 15, nowUnix: 1000 + CasterMembershipStore.SignedMarkExpirySeconds));
        }

        // ── SELF-HEAL maturity ───────────────────────────────────────────

        [Fact]
        public void Maturity_HaltedChain_MaturesByWallClock()
        {
            // The 975,533 case: first seen at the frozen tip, Δheight stays 0 forever.
            var v = new NetworkValidator { Address = "x", FirstSeenAtHeight = 975533, FirstSeenAt = 1000 };
            Assert.False(CasterDiscoveryService.IsMatureCandidate(v, 975533, 1000 + CasterDiscoveryService.MaturitySeconds - 1, out _));
            Assert.True(CasterDiscoveryService.IsMatureCandidate(v, 975533, 1000 + CasterDiscoveryService.MaturitySeconds, out _));
        }

        [Fact]
        public void Maturity_ByHeight_StillWorks()
        {
            var v = new NetworkValidator { Address = "x", FirstSeenAtHeight = 100, FirstSeenAt = 1000 };
            Assert.True(CasterDiscoveryService.IsMatureCandidate(v, 100 + CasterDiscoveryService.MaturityBlocks, 1001, out _));
            Assert.False(CasterDiscoveryService.IsMatureCandidate(v, 100 + CasterDiscoveryService.MaturityBlocks - 1, 1001, out _));
        }

        [Fact]
        public void Maturity_UnknownFirstSeen_TreatedAsMature()
        {
            var v = new NetworkValidator { Address = "x", FirstSeenAtHeight = 0, FirstSeenAt = 0 };
            Assert.True(CasterDiscoveryService.IsMatureCandidate(v, 5000, 1, out _));
        }

        // ── WATCHDOG ─────────────────────────────────────────────────────

        [Fact]
        public void Watchdog_OnlyHeightChangeResets_AndEscalationRepeats()
        {
            var s = new ChainProgressWatchdog.State();
            Assert.Equal(ChainProgressWatchdog.Level.Moving, ChainProgressWatchdog.Evaluate(s, 100, 0));
            Assert.Equal(ChainProgressWatchdog.Level.Quiet, ChainProgressWatchdog.Evaluate(s, 100, ChainProgressWatchdog.WarnAfterSeconds - 1));
            Assert.Equal(ChainProgressWatchdog.Level.Warn, ChainProgressWatchdog.Evaluate(s, 100, ChainProgressWatchdog.WarnAfterSeconds));
            Assert.Equal(ChainProgressWatchdog.Level.Quiet, ChainProgressWatchdog.Evaluate(s, 100, ChainProgressWatchdog.WarnAfterSeconds + 1)); // warn once
            Assert.Equal(ChainProgressWatchdog.Level.Escalate, ChainProgressWatchdog.Evaluate(s, 100, ChainProgressWatchdog.EscalateAfterSeconds));
            Assert.Equal(ChainProgressWatchdog.Level.Quiet, ChainProgressWatchdog.Evaluate(s, 100, ChainProgressWatchdog.EscalateAfterSeconds + 1));
            Assert.Equal(ChainProgressWatchdog.Level.Escalate, ChainProgressWatchdog.Evaluate(s, 100, ChainProgressWatchdog.EscalateAfterSeconds + ChainProgressWatchdog.RepeatEverySeconds));
            // A real block resets everything — a "successful round" that commits nothing cannot.
            Assert.Equal(ChainProgressWatchdog.Level.Moving, ChainProgressWatchdog.Evaluate(s, 101, ChainProgressWatchdog.EscalateAfterSeconds + ChainProgressWatchdog.RepeatEverySeconds + 1));
            Assert.Equal(ChainProgressWatchdog.Level.Quiet, ChainProgressWatchdog.Evaluate(s, 101, ChainProgressWatchdog.EscalateAfterSeconds + ChainProgressWatchdog.RepeatEverySeconds + 2));
        }

        // ── BOOTSTRAP-RESET survey ───────────────────────────────────────

        private static NetworkStallSurvey.Node N(string ip, bool committee = false) => new() { Ip = ip, Address = "a" + ip, IsCommittee = committee };
        private static NetworkStallSurvey.Reply R(string ip, long h, string hash = "H") => new() { Ip = ip, Height = h, TipHash = hash };

        private static readonly List<NetworkStallSurvey.Node> SixKnown = new()
        {
            N("1", committee: true), N("2", committee: true), N("3", committee: true), N("4"), N("5"), N("6")
        };

        [Fact]
        public void Survey_PeerAhead_VetoesRegardlessOfReach()
        {
            var replies = new List<NetworkStallSurvey.Reply> { R("1", 101) }; // one responder, but AHEAD
            var r = NetworkStallSurvey.Evaluate(SixKnown, replies, 100, "H", 2);
            Assert.Equal(NetworkStallSurvey.Verdict.PeerAhead, r.Verdict);
        }

        [Fact]
        public void Survey_HashDisagreement_IsAForkNotAStall()
        {
            var replies = new List<NetworkStallSurvey.Reply> { R("1", 100), R("2", 100), R("3", 100), R("4", 100, "OTHER"), R("5", 100), R("6", 100) };
            var r = NetworkStallSurvey.Evaluate(SixKnown, replies, 100, "H", 2);
            Assert.Equal(NetworkStallSurvey.Verdict.HashDisagreement, r.Verdict);
        }

        [Fact]
        public void Survey_InsufficientReach_BlindIsNotStalled()
        {
            // The connection-leak fear: only 2 of 6 answer (below 50%), both stalled — must NOT be a stall.
            var replies = new List<NetworkStallSurvey.Reply> { R("1", 100), R("2", 100) };
            var r = NetworkStallSurvey.Evaluate(SixKnown, replies, 100, "H", 2);
            Assert.Equal(NetworkStallSurvey.Verdict.InsufficientReach, r.Verdict);
        }

        [Fact]
        public void Survey_CommitteeMajorityUnreachable_IsUndecidable()
        {
            // Enough reach overall, but only 1 of 3 committee members answered (quorum 2).
            var replies = new List<NetworkStallSurvey.Reply> { R("1", 100), R("4", 100), R("5", 100), R("6", 100) };
            var r = NetworkStallSurvey.Evaluate(SixKnown, replies, 100, "H", 2);
            Assert.Equal(NetworkStallSurvey.Verdict.CommitteeUnreachable, r.Verdict);
            Assert.Equal(1, r.CommitteeResponded);
        }

        [Fact]
        public void Survey_ProvenStall_OnlyWhenEveryGatePasses()
        {
            var replies = new List<NetworkStallSurvey.Reply> { R("1", 100), R("2", 100), R("4", 100), R("5", 100) };
            var r = NetworkStallSurvey.Evaluate(SixKnown, replies, 100, "H", 2);
            Assert.Equal(NetworkStallSurvey.Verdict.Stalled, r.Verdict);
            Assert.Equal(4, r.Responded);
            Assert.Equal(2, r.CommitteeResponded);
        }

        // ── BOOTSTRAP-RESET ladder ───────────────────────────────────────

        private static readonly long Stale = CasterMembershipStore.BootstrapResetMinStallSeconds;

        [Fact]
        public void Ladder_NotStalledLongEnough_NeverProposes()
        {
            var s = new BootstrapResetService.State { Consecutive = 2 };
            Assert.Equal(BootstrapResetService.Decision.NotStalledYet, BootstrapResetService.Decide(s, 0, Stale - 1, false, NetworkStallSurvey.Verdict.Stalled));
            Assert.Equal(0, s.Consecutive);
        }

        [Fact]
        public void Ladder_SeedsCanMeetQuorum_NoResetNeeded()
        {
            var s = new BootstrapResetService.State();
            Assert.Equal(BootstrapResetService.Decision.QuorumReachableBySeeds, BootstrapResetService.Decide(s, 0, Stale, true, NetworkStallSurvey.Verdict.Stalled));
        }

        [Fact]
        public void Ladder_ProposesOnlyAfterConsecutiveProvenStalls()
        {
            var s = new BootstrapResetService.State();
            for (var i = 1; i < BootstrapResetService.RequiredConsecutiveSurveys; i++)
                Assert.Equal(BootstrapResetService.Decision.StreakAdvanced, BootstrapResetService.Decide(s, 0, Stale, false, NetworkStallSurvey.Verdict.Stalled));
            Assert.Equal(BootstrapResetService.Decision.Propose, BootstrapResetService.Decide(s, 0, Stale, false, NetworkStallSurvey.Verdict.Stalled));
        }

        [Fact]
        public void Ladder_AnyVeto_ResetsTheStreak()
        {
            var s = new BootstrapResetService.State { Consecutive = 2 };
            Assert.Equal(BootstrapResetService.Decision.StreakReset, BootstrapResetService.Decide(s, 0, Stale, false, NetworkStallSurvey.Verdict.PeerAhead));
            Assert.Equal(0, s.Consecutive);
            s.Consecutive = 2;
            Assert.Equal(BootstrapResetService.Decision.StreakReset, BootstrapResetService.Decide(s, 0, Stale, false, NetworkStallSurvey.Verdict.HashDisagreement));
            s.Consecutive = 2;
            Assert.Equal(BootstrapResetService.Decision.StreakReset, BootstrapResetService.Decide(s, 0, Stale, false, NetworkStallSurvey.Verdict.InsufficientReach));
        }

        [Fact]
        public void Ladder_CommitteeUnreachable_RequiresOperator_UnlessForced()
        {
            var s = new BootstrapResetService.State { Consecutive = 2 };
            Assert.Equal(BootstrapResetService.Decision.OperatorRequired, BootstrapResetService.Decide(s, 0, Stale, false, NetworkStallSurvey.Verdict.CommitteeUnreachable));
            Assert.Equal(0, s.Consecutive);

            s.ForceUntilUnix = 1000; // operator armed
            for (var i = 1; i < BootstrapResetService.RequiredConsecutiveSurveys; i++)
                Assert.Equal(BootstrapResetService.Decision.StreakAdvanced, BootstrapResetService.Decide(s, 10, Stale, false, NetworkStallSurvey.Verdict.CommitteeUnreachable));
            Assert.Equal(BootstrapResetService.Decision.Propose, BootstrapResetService.Decide(s, 10, Stale, false, NetworkStallSurvey.Verdict.CommitteeUnreachable));
        }

        [Fact]
        public void Ladder_ForceNeverOverridesAheadOrDisagreement()
        {
            var s = new BootstrapResetService.State { ForceUntilUnix = 1000, Consecutive = 2 };
            Assert.Equal(BootstrapResetService.Decision.StreakReset, BootstrapResetService.Decide(s, 10, Stale, false, NetworkStallSurvey.Verdict.PeerAhead));
            s.Consecutive = 2;
            Assert.Equal(BootstrapResetService.Decision.StreakReset, BootstrapResetService.Decide(s, 10, Stale, false, NetworkStallSurvey.Verdict.HashDisagreement));
        }

        [Fact]
        public void Ladder_ForceExpires()
        {
            var s = new BootstrapResetService.State { ForceUntilUnix = 1000 };
            Assert.Equal(BootstrapResetService.Decision.OperatorRequired, BootstrapResetService.Decide(s, 1000, Stale, false, NetworkStallSurvey.Verdict.CommitteeUnreachable));
        }

        // ── BOOTSTRAP-RESET acceptance: a moving chain refuses it ────────

        [Fact]
        public void ResetRecord_RefusedWhileOurTipIsFresh()
        {
            var candidate = Rec(16, 975534, "prev", CasterMembershipStore.BootstrapResetChangeType, 3);
            Assert.False(CasterMembershipStore.ValidateBootstrapReset(candidate, CasterMembershipStore.BootstrapResetMinStallSeconds - 1, out var reason));
            Assert.Contains("not stalled here", reason);
        }
    }
}
