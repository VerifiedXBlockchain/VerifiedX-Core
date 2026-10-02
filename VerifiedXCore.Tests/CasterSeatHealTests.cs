using System;
using System.Collections.Generic;
using System.Linq;
using VerifiedXCore;
using VerifiedXCore.Models;
using VerifiedXCore.Nodes;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Committee seat self-heal (plan C). Sep 30: a full record with two restarting members, a promotion signed into the
    /// full record that locked every signer for 10 minutes, and restarted members re-added by reachability then evicted
    /// for not casting, in a loop. These cover the decisions: absence timing and grace, who proposes, what a signer will
    /// sign, when a resume is approved, that a resume and a removal can't both win, and the chain-based absence check.
    /// </summary>
    [Collection("GlobalCasterState")]
    public class CasterSeatHealTests : IDisposable
    {
        private const long T0 = 1_790_000_000;
        private static readonly string[] Members = { "RBQT6", "RFzuJ", "RH9XA", "RK28y", "RSeed1" };

        public CasterSeatHealTests() => CasterSeatService.ResetForTests();
        public void Dispose() => CasterSeatService.ResetForTests();

        private static CasterMembershipRecord Head(long seq = 4, string changeType = "Promotion", string changed = "RFzuJ") => new()
        {
            RecordSeq = seq,
            RecordHash = "hash" + seq,
            ChangeType = changeType,
            ChangedAddress = changed,
            Casters = Members.Select(a => new CasterInfo { Address = a, PeerIP = "10.0.0.1" }).ToList(),
        };

        private static bool AnySig(string addr, string msg, string sig) => sig == "sig:" + addr + ":" + msg;
        private static string Sign(string addr, string msg) => "sig:" + addr + ":" + msg;

        private static CasterResumeRequest Request(string addr, CasterMembershipRecord head, long ts)
        {
            var r = new CasterResumeRequest { Address = addr, RecordSeq = head.RecordSeq, RecordHash = head.RecordHash, Timestamp = ts };
            r.Signature = Sign(addr, CasterSeatService.ResumeRequestMessage(r));
            return r;
        }

        // ── C.1/C.2: absence and grace ────────────────────────────────────────────────────────────────

        [Fact]
        public void Absence_StartsOnce_AndResetsOnlyWhenTheMemberCasts()
        {
            CasterSeatService.Observe("RBQT6", present: false, T0, 100);
            CasterSeatService.Observe("RBQT6", present: false, T0 + 120, 110);   // still absent: the timer keeps its start
            Assert.Equal(200, CasterSeatService.AbsentSeconds("RBQT6", T0 + 200));
            Assert.False(CasterSeatService.PastGrace("RBQT6", T0 + 299));
            Assert.True(CasterSeatService.PastGrace("RBQT6", T0 + 300));

            CasterSeatService.Observe("RBQT6", present: true, T0 + 310, 125);
            Assert.Null(CasterSeatService.AbsentSeconds("RBQT6", T0 + 320));
            Assert.False(CasterSeatService.PastGrace("RBQT6", T0 + 900));
        }

        [Fact]
        public void Maintenance_DoublesTheGrace_ForAnAbsenceThatStartsInsideItsWindow()
        {
            Assert.Equal(CasterSeatService.GraceSeconds, CasterSeatService.GraceFor("RFzuJ"));
            CasterSeatService.NoteMaintenance("RFzuJ", T0 + CasterSeatService.MaintenanceGraceSeconds);
            CasterSeatService.Observe("RFzuJ", present: false, T0 + 30, 100);
            Assert.Equal(CasterSeatService.MaintenanceGraceSeconds, CasterSeatService.GraceFor("RFzuJ"));
            Assert.False(CasterSeatService.PastGrace("RFzuJ", T0 + 30 + 400));
            Assert.True(CasterSeatService.PastGrace("RFzuJ", T0 + 30 + 600));
        }

        // ── C.4: one proposer, one removal ────────────────────────────────────────────────────────────

        [Fact]
        public void TheSeatProposer_IsTheLowestPresentAddress()
        {
            var absent = new HashSet<string> { "RBQT6" };
            Assert.Equal("RFzuJ", CasterSeatService.SeatProposer(Members, a => !absent.Contains(a)));
            Assert.Equal("RBQT6", CasterSeatService.SeatProposer(Members, _ => true));
            Assert.Null(CasterSeatService.SeatProposer(Members, _ => false));
        }

        [Fact]
        public void Removal_PicksTheLongestAbsentPastGrace_OneAtATime()
        {
            var absent = new Dictionary<string, long> { ["RBQT6"] = 700, ["RFzuJ"] = 320, ["RK28y"] = 120 };
            long? Abs(string a) => absent.TryGetValue(a, out var s) ? s : null;
            Assert.Equal("RBQT6", CasterSeatService.PickRemoval(Members, Abs, _ => 300));
            Assert.Null(CasterSeatService.PickRemoval(Members, Abs, _ => 800));            // nobody past grace
            Assert.Equal("RFzuJ", CasterSeatService.PickRemoval(Members, Abs, a => a == "RBQT6" ? 800 : 300));
        }

        // ── C.4/C.3d: what a signer signs ─────────────────────────────────────────────────────────────

        [Fact]
        public void ASigner_SignsARemoval_OnlyOnItsOwnEvidence()
        {
            var head = Head(changeType: "Demotion", changed: "RX");
            Assert.NotNull(CasterSeatService.RemovalRefusal("Demotion", "RBQT6", head, T0));          // casting, by our observation

            CasterSeatService.Observe("RBQT6", false, T0, 100);
            Assert.NotNull(CasterSeatService.RemovalRefusal("Demotion", "RBQT6", head, T0 + 200));    // inside grace
            Assert.Null(CasterSeatService.RemovalRefusal("Demotion", "RBQT6", head, T0 + 250));       // within the timer tolerance
            Assert.Null(CasterSeatService.RemovalRefusal("Demotion", "RBQT6", head, T0 + 400));

            CasterSeatService.NoteDeparture("RK28y");                                                 // its own signed notice
            Assert.Null(CasterSeatService.RemovalRefusal("Departure", "RK28y", head, TimeUtilNow()));
            CasterSeatService.NoteOutdated("RH9XA");                                                  // outdated, seen ourselves
            Assert.Null(CasterSeatService.RemovalRefusal("Demotion", "RH9XA", head, TimeUtilNow()));
        }

        [Fact]
        public void APromotionThatNeverTook_CanBeRolledBack()
        {
            var head = Head(changeType: "Promotion", changed: "RFzuJ");
            Assert.NotNull(CasterSeatService.RemovalRefusal("Demotion", "RFzuJ", head, T0));          // not yet seen absent
            CasterSeatService.Observe("RFzuJ", false, T0, 100);
            Assert.Null(CasterSeatService.RemovalRefusal("Demotion", "RFzuJ", head, T0 + 5));
        }

        [Fact]
        public void AResumeAndARemoval_CannotBothBeSignedByOneMember()
        {
            var head = Head();
            CasterSeatService.Observe("RBQT6", false, T0, 100);

            // We approve its resume just inside grace → we then refuse to sign its removal, even once past grace.
            Assert.Null(CasterSeatService.ResumeRefusal(Request("RBQT6", head, T0 + 250), head, "RH9XA", T0 + 250, AnySig));
            CasterSeatService.NoteResumeApproved("RBQT6", head.RecordSeq, T0 + 250);
            Assert.Equal("we approved its resume", CasterSeatService.RemovalRefusal("Demotion", "RBQT6", head, T0 + 320));
            // The hold covers the resume; if the member never started casting, the removal goes ahead afterwards.
            Assert.Null(CasterSeatService.RemovalRefusal("Demotion", "RBQT6", head, T0 + 250 + CasterSeatService.ResumeApprovalHoldSeconds + 1));

            // And the reverse: after signing a removal at head+1, we refuse its resume.
            CasterSeatService.ResetForTests();
            CasterSeatService.Observe("RFzuJ", false, TimeUtilNow() - 60, 100);
            CasterSeatService.NoteRemovalSigned("RFzuJ", head.RecordSeq + 1);
            Assert.Equal("we signed its removal", CasterSeatService.ResumeRefusal(Request("RFzuJ", head, TimeUtilNow()), head, "RH9XA", TimeUtilNow(), AnySig));
        }

        [Fact]
        public void ResumeAndRemovalMajorities_AlwaysIntersect()
        {
            for (var n = 2; n <= 9; n++)
            {
                var others = n - 1;
                var removalSigners = n / 2 + 1;                        // drawn from the others (the member won't sign its own removal)
                var need = CasterSeatService.ResumeNeed(n);
                Assert.True(need >= 1);
                Assert.True(need + removalSigners > others, $"n={n}: resume {need} + removal {removalSigners} must exceed {others}");
            }
            Assert.Equal(2, CasterSeatService.ResumeNeed(5));          // 5-member committee: 2 of the 4 others
        }

        // ── C.3: resume requests ──────────────────────────────────────────────────────────────────────

        [Fact]
        public void AResume_IsRefusedUnlessFreshSignedCurrentAndInsideGrace()
        {
            var head = Head();
            var now = T0 + 1_000;
            Assert.Null(CasterSeatService.ResumeRefusal(Request("RBQT6", head, now), head, "RH9XA", now, AnySig));

            var forged = Request("RBQT6", head, now); forged.Signature = "x";
            Assert.Equal("bad signature", CasterSeatService.ResumeRefusal(forged, head, "RH9XA", now, AnySig));
            Assert.Equal("stale request", CasterSeatService.ResumeRefusal(Request("RBQT6", head, now - 600), head, "RH9XA", now, AnySig));
            Assert.StartsWith("different record", CasterSeatService.ResumeRefusal(Request("RBQT6", Head(3), now), head, "RH9XA", now, AnySig));
            Assert.Equal("not in the record", CasterSeatService.ResumeRefusal(Request("RVGy4", head, now), head, "RH9XA", now, AnySig));
            Assert.Equal("we are not a member", CasterSeatService.ResumeRefusal(Request("RBQT6", head, now), head, "RVGy4", now, AnySig));

            CasterSeatService.Observe("RBQT6", false, now - 400, 100);
            Assert.Contains("past the", CasterSeatService.ResumeRefusal(Request("RBQT6", head, now), head, "RH9XA", now, AnySig));
        }

        [Fact]
        public void Approvals_CountOnlyDistinctOtherMembers_ForThisExactRequest()
        {
            var head = Head();
            var req = Request("RBQT6", head, T0);
            CasterResumeApproval Ok(string signer, long ts = T0) => new()
            {
                SignerAddress = signer, Address = "RBQT6", RecordSeq = head.RecordSeq, RecordHash = head.RecordHash, Timestamp = ts, Approve = true,
                Signature = Sign(signer, CasterSeatService.ResumeApprovalMessage("RBQT6", head.RecordSeq, head.RecordHash, ts)),
            };
            var approvals = new List<CasterResumeApproval>
            {
                Ok("RH9XA"), Ok("RH9XA"),                       // duplicate
                Ok("RBQT6"),                                    // the requester itself
                Ok("RVGy4"),                                    // not a member
                Ok("RK28y", T0 - 1),                            // a different (older) request
                new() { SignerAddress = "RSeed1", Approve = false, Reason = "absent" },
                Ok("RFzuJ"),
            };
            var approvers = CasterSeatService.ValidApprovers(req, head, approvals, AnySig);
            Assert.Equal(new[] { "RFzuJ", "RH9XA" }, approvers.OrderBy(x => x, StringComparer.Ordinal).ToArray());
        }

        [Fact]
        public void AResumedNotice_IsHonouredOnlyWhileFresh()
        {
            var head = Head();
            var req = Request("RBQT6", head, T0);
            CasterResumeApproval Ok(string signer) => new()
            {
                SignerAddress = signer, Address = "RBQT6", RecordSeq = head.RecordSeq, RecordHash = head.RecordHash, Timestamp = T0, Approve = true,
                Signature = Sign(signer, CasterSeatService.ResumeApprovalMessage("RBQT6", head.RecordSeq, head.RecordHash, T0)),
            };
            var notice = new CasterResumedNotice { Request = req, Approvals = new() { Ok("RH9XA"), Ok("RFzuJ") } };

            Assert.Null(CasterSeatService.ResumedNoticeRefusal(notice, head, T0 + 5, AnySig));
            // The same, validly signed notice replayed later (the record unchanged) must not clear the absence timer again.
            Assert.Equal("stale notice", CasterSeatService.ResumedNoticeRefusal(notice, head, T0 + CasterSeatService.RequestSkewSeconds + 1, AnySig));

            Assert.Equal("different record", CasterSeatService.ResumedNoticeRefusal(notice, Head(5), T0 + 5, AnySig));
            var thin = new CasterResumedNotice { Request = req, Approvals = new() { Ok("RH9XA") } };
            Assert.Equal("1/2 approvals", CasterSeatService.ResumedNoticeRefusal(thin, head, T0 + 5, AnySig));
            var forged = new CasterResumedNotice { Request = Request("RBQT6", head, T0), Approvals = notice.Approvals };
            forged.Request!.Signature = "x";
            Assert.Equal("bad signature", CasterSeatService.ResumedNoticeRefusal(forged, head, T0 + 5, AnySig));
        }

        [Fact]
        public void MaintenanceHold_NeedsEnoughMembersToKnowAboutIt()
        {
            // 5-member committee: removal takes 3 signers among the 4 others, so at most 2 may have missed the notice.
            Assert.Equal(2, CasterSeatService.MaintenanceAcksNeeded(5));
            for (var n = 2; n <= 9; n++)
            {
                var need = CasterSeatService.MaintenanceAcksNeeded(n);
                var missed = (n - 1) - need;
                Assert.True(missed < n / 2 + 1, $"n={n}: {missed} members without the notice could still sign a removal");
            }
        }

        [Fact]
        public void DuringARollingUpgrade_OlderPeersDontStrandAResumingMember()
        {
            // 5-member committee: 4 others, need 2. Three older builds (404) leave one that could approve.
            Assert.True(CasterSeatService.LegacyFleet(others: 4, legacyPeers: 3, need: 2));
            Assert.False(CasterSeatService.LegacyFleet(others: 4, legacyPeers: 2, need: 2));
            Assert.False(CasterSeatService.LegacyFleet(others: 4, legacyPeers: 0, need: 2));   // unreachable is not legacy
        }

        // ── C.3b: chain-based absence ─────────────────────────────────────────────────────────────────

        private static Func<long, Block?> Chain(long tip, long blockSeconds, params (long Height, string Attester)[] attested) => h =>
        {
            if (h < 0 || h > tip) return null;
            var b = new Block { Height = h, Timestamp = T0 + h * blockSeconds, Transactions = new List<Transaction>() };
            var who = attested.Where(a => a.Height == h).Select(a => a.Attester).ToList();
            if (who.Count > 0)
                b.ConsensusCertificate = new ConsensusCertificate { Attestations = who.Select(a => new CasterAttestation { CasterAddress = a }).ToList() };
            return b;
        };

        [Fact]
        public void TheChainShowsHowLongAMemberWasGone()
        {
            // 12 s blocks, tip 1000. Last attestation at 990 → 120 s gone: resume allowed.
            Assert.True(CasterSeatService.ChainAbsence("RBQT6", 1000, Chain(1000, 12, (990, "RBQT6")), -1, 300).Ok);
            // Last attestation at 950 → 600 s gone: past the 5-minute grace, not with 10-minute maintenance.
            Assert.False(CasterSeatService.ChainAbsence("RBQT6", 1000, Chain(1000, 12, (950, "RBQT6")), -1, 300).Ok);
            Assert.True(CasterSeatService.ChainAbsence("RBQT6", 1000, Chain(1000, 12, (950, "RBQT6")), -1, 600).Ok);
            // Never attested, seat took effect at 995: it joined recently.
            Assert.True(CasterSeatService.ChainAbsence("RBQT6", 1000, Chain(1000, 12), 995, 300).Ok);
            // Never attested, long-standing seat: gone too long.
            Assert.False(CasterSeatService.ChainAbsence("RBQT6", 1000, Chain(1000, 12), 10, 300).Ok);
        }

        [Fact]
        public void AChainThatStalledWhileEveryoneWasDown_CountsAsNoTimeGone()
        {
            // The tip is the last block; it stopped because the committee was down. Gone is measured to the tip, not the clock.
            Assert.True(CasterSeatService.ChainAbsence("RBQT6", 500, Chain(500, 12, (499, "RBQT6")), -1, 300).Ok);
        }

        // ── C.8: restarts don't split the round's proof set ───────────────────────────────────────────

        [Fact]
        public void WithoutAMatchingSet_TheRoundUsesValidatorsAQuorumSees()
        {
            // Sep 30: three live members, each missing a different restarting validator.
            var a = new List<string> { "V1", "V2", "V3", "V4" };
            var b = new List<string> { "V1", "V2", "V3", "V5" };
            var c = new List<string> { "V1", "V2", "V4", "V5" };
            Assert.Equal(new[] { "V1", "V2" }, BlockcasterNode.QuorumPresenceSet(new[] { a, b, c }, 3));
            Assert.Equal(new[] { "V1", "V2", "V3", "V4", "V5" }, BlockcasterNode.QuorumPresenceSet(new[] { a, b, c }, 2));
            // Order of arrival doesn't matter.
            Assert.Equal(BlockcasterNode.QuorumPresenceSet(new[] { c, a, b }, 2), BlockcasterNode.QuorumPresenceSet(new[] { a, b, c }, 2));
            // One caster can't shrink the set to its favourite.
            var rogue = new List<string> { "V3" };
            Assert.Equal(new[] { "V1", "V2", "V3", "V4" }, BlockcasterNode.QuorumPresenceSet(new[] { a, a, rogue }, 2));
            // Too few commitments: no set.
            Assert.Null(BlockcasterNode.QuorumPresenceSet(new[] { a, b }, 3));
        }

        private static long TimeUtilNow() => VerifiedXCore.Utilities.TimeUtil.GetTime();
    }
}
