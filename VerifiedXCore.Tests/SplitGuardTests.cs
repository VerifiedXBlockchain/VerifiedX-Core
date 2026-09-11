using VerifiedXCore.Nodes;
using VerifiedXCore.Services;
using static VerifiedXCore.Services.ValidatorCommitGate;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// SPLIT-GUARD (Sep 2026): the pure decision logic that prevents two caster groups from both
    /// producing (legacy-era quorum floor) and validators from committing a minority block on a
    /// single peer's push (validator commit gate).
    /// </summary>
    [Collection("GlobalCasterState")]
    public class SplitGuardTests
    {
        private static ProbeAnswer A(string ip, string hash, bool committed = true)
            => new ProbeAnswer { Ip = ip, Hash = hash, Committed = committed };

        // ── Legacy quorum floor ──────────────────────────────────────────

        [Fact]
        public void LegacyDenominator_IsAtLeastMaxCasters_OutsideBootstrap()
        {
            Assert.Equal(CasterDiscoveryService.MaxCasters, BlockcasterNode.LegacyQuorumDenominator(3, bootstrapMode: false));
            Assert.Equal(CasterDiscoveryService.MaxCasters, BlockcasterNode.LegacyQuorumDenominator(1, bootstrapMode: false));
            Assert.Equal(6, BlockcasterNode.LegacyQuorumDenominator(6, bootstrapMode: false));
        }

        [Fact]
        public void LegacyDenominator_UsesBag_InBootstrap()
        {
            Assert.Equal(3, BlockcasterNode.LegacyQuorumDenominator(3, bootstrapMode: true));
        }

        [Fact]
        public void LegacyQuorum_TwoDisjointMajorities_AreImpossible()
        {
            // With the floor, the required agreement in a 5-caster pool is 3 no matter how a
            // caster's local bag shrank. Two disjoint groups of 3 cannot exist within 5.
            var required = Math.Max(2, BlockcasterNode.LegacyQuorumDenominator(3, false) / 2 + 1);
            Assert.Equal(3, required);
            Assert.True(required * 2 > CasterDiscoveryService.MaxCasters);
        }

        // ── Validator commit gate ────────────────────────────────────────

        [Fact]
        public void Gate_TwoDistinctCasterDeliveries_Confirm()
        {
            Assert.Equal(Verdict.Confirmed, Decide(new[] { "c1", "c2" }, Array.Empty<ProbeAnswer>(), "H"));
        }

        [Fact]
        public void Gate_SingleCasterDelivery_IsNotEnough()
        {
            // One caster pushing the block, nobody else answering yet → wait and retry, never commit.
            Assert.Equal(Verdict.Pending, Decide(new[] { "c1" }, Array.Empty<ProbeAnswer>(), "H"));
        }

        [Fact]
        public void Gate_ProbeMajorityCommitted_Confirms()
        {
            var answers = new[] { A("c1", "H"), A("c2", "H"), A("c3", "X") };
            Assert.Equal(Verdict.Confirmed, Decide(Array.Empty<string>(), answers, "H"));
        }

        [Fact]
        public void Gate_MinorityBlock_IsRejected()
        {
            // The 912,064 shape: one caster delivered A; three casters have committed B.
            var answers = new[] { A("c1", "A"), A("c2", "B"), A("c3", "B"), A("c4", "B") };
            Assert.Equal(Verdict.Rejected, Decide(new[] { "c1" }, answers, "A"));
        }

        [Fact]
        public void Gate_DraftAnswers_DoNotCount()
        {
            // Round drafts (Committed=false) are not evidence either way.
            var answers = new[] { A("c1", "H", committed: false), A("c2", "H", committed: false), A("c3", "H", committed: false) };
            Assert.Equal(Verdict.Pending, Decide(Array.Empty<string>(), answers, "H"));
        }

        [Fact]
        public void Gate_DeliveryThenDissent_CountsAsDissenter()
        {
            // c1 pushed A but has since committed B: fresher answer wins.
            var answers = new[] { A("c1", "B"), A("c2", "B") };
            Assert.Equal(Verdict.Rejected, Decide(new[] { "c1" }, answers, "A"));
        }

        [Fact]
        public void Gate_DeliveryPlusOneCommittedAgree_Confirms()
        {
            var answers = new[] { A("c2", "H") };
            Assert.Equal(Verdict.Confirmed, Decide(new[] { "c1" }, answers, "H"));
        }

        [Fact]
        public void Gate_TooFewAnswers_NoDeliveries_IsUndecidable()
        {
            Assert.Equal(Verdict.Undecidable, Decide(Array.Empty<string>(), new[] { A("c1", "H") }, "H"));
            Assert.Equal(Verdict.Undecidable, Decide(Array.Empty<string>(), Array.Empty<ProbeAnswer>(), "H"));
        }

        [Fact]
        public void Gate_SplitVote_IsPending()
        {
            var answers = new[] { A("c1", "H"), A("c2", "H"), A("c3", "X"), A("c4", "X") };
            Assert.Equal(Verdict.Pending, Decide(Array.Empty<string>(), answers, "H"));
        }

        [Fact]
        public void Gate_IgnoresEmptyAndZeroHashes()
        {
            var answers = new[] { A("c1", ""), A("c2", "0"), A("c3", "H"), A("c4", "H") };
            Assert.Equal(Verdict.Confirmed, Decide(Array.Empty<string>(), answers, "H"));
        }
    }
}
