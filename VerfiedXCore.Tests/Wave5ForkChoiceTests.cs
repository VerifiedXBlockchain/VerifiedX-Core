using ReserveBlockCore.Models;
using ReserveBlockCore.Services;
using ReserveBlockCore.Utilities;
using static ReserveBlockCore.Utilities.ForkChoiceUtility;

namespace VerfiedXCore.Tests
{
    /// <summary>
    /// Wave 5 (fork choice + bounded reorg): the pure decision rule, majority gate, and
    /// cert-weight counting. Complements the existing pre-commit ForkChoiceTests
    /// (SelectCanonicalBlock) — rule 3 here uses the same ordinal-hash semantics.
    /// </summary>
    [Collection("GlobalCasterState")]
    public class Wave5ForkChoiceTests
    {
        private static BranchSummary Branch(long tip, string hash, int certWeight = 0, bool certApplicable = false)
            => new BranchSummary { TipHeight = tip, HashAtDivergence = hash, CertWeight = certWeight, CertRuleApplicable = certApplicable };

        // ── Rule 1: certificate weight ───────────────────────────────────

        [Fact]
        public void CertWeight_Dominates_WhenBothApplicable()
        {
            var local = Branch(100, "zzz", certWeight: 3, certApplicable: true);
            var remote = Branch(105, "aaa", certWeight: 2, certApplicable: true);
            // Local has FEWER height and LOSING hash but MORE cert weight → wins.
            Assert.Equal(Outcome.LocalWins, CompareBranches(local, remote));
            Assert.Equal(Outcome.RemoteWins, CompareBranches(remote, local));
        }

        [Fact]
        public void CertWeight_Ignored_UnlessBothApplicable()
        {
            // One side pre-cert-era → rule 1 skipped → height decides.
            var local = Branch(100, "zzz", certWeight: 5, certApplicable: true);
            var remote = Branch(105, "aaa", certWeight: 0, certApplicable: false);
            Assert.Equal(Outcome.RemoteWins, CompareBranches(local, remote));
        }

        // ── Rule 2: height ───────────────────────────────────────────────

        [Fact]
        public void Height_Decides_WhenCertsEqualOrInapplicable()
        {
            Assert.Equal(Outcome.LocalWins, CompareBranches(Branch(105, "zzz"), Branch(100, "aaa")));
            Assert.Equal(Outcome.RemoteWins, CompareBranches(Branch(100, "zzz"), Branch(105, "aaa")));
            // Equal cert weight, both applicable → falls through to height.
            Assert.Equal(Outcome.LocalWins, CompareBranches(Branch(105, "zzz", 2, true), Branch(100, "aaa", 2, true)));
        }

        // ── Rule 3: lowest ordinal hash ──────────────────────────────────

        [Fact]
        public void LowestOrdinalHash_BreaksTies()
        {
            Assert.Equal(Outcome.LocalWins, CompareBranches(Branch(100, "aaa"), Branch(100, "bbb")));
            Assert.Equal(Outcome.RemoteWins, CompareBranches(Branch(100, "bbb"), Branch(100, "aaa")));
        }

        [Fact]
        public void DistinctHashes_NeverTie()
        {
            // Exhaustive symmetry check: for any two distinct hashes, exactly one side wins
            // and the decision flips when the sides swap.
            var hashes = new[] { "0a", "0b", "ff", "00", "Zz", "zZ" };
            foreach (var h1 in hashes)
                foreach (var h2 in hashes.Where(h => h != h1))
                {
                    var fwd = CompareBranches(Branch(100, h1), Branch(100, h2));
                    var rev = CompareBranches(Branch(100, h2), Branch(100, h1));
                    Assert.NotEqual(fwd, rev);
                }
        }

        [Fact]
        public void IdenticalHash_NoForkExisted_LocalWinsNoOp()
        {
            Assert.Equal(Outcome.LocalWins, CompareBranches(Branch(100, "same"), Branch(105, "same")));
        }

        [Fact]
        public void NullBranches_DefensiveLocalWins()
        {
            Assert.Equal(Outcome.LocalWins, CompareBranches(null!, Branch(100, "aaa")));
            Assert.Equal(Outcome.LocalWins, CompareBranches(Branch(100, "aaa"), null!));
        }

        // ── Bounds + gates ───────────────────────────────────────────────

        [Fact]
        public void ReorgBound_MatchesHeightGapCap()
        {
            Assert.Equal(10, MAX_REORG_DEPTH);
        }

        [Fact]
        public void StrictMajority_Gate()
        {
            Assert.False(ForkDetectionService.IsStrictMajority(0, 0));
            Assert.False(ForkDetectionService.IsStrictMajority(1, 2)); // exactly half is not enough
            Assert.False(ForkDetectionService.IsStrictMajority(2, 4));
            Assert.True(ForkDetectionService.IsStrictMajority(2, 3));
            Assert.True(ForkDetectionService.IsStrictMajority(3, 4));
            Assert.True(ForkDetectionService.IsStrictMajority(1, 1));
        }

        // ── Cert weight counting ─────────────────────────────────────────

        [Fact]
        public void CertWeight_NullOrJunk_CountsZero()
        {
            Assert.Equal(0, CountValidCertWeight(null));
            Assert.Equal(0, CountValidCertWeight(new Block { Height = 100, Hash = "h", Validator = "v", PrevHash = "p", Version = 4 }));

            var withJunk = new Block
            {
                Height = 100,
                Hash = "h",
                Validator = "v",
                PrevHash = "p",
                Version = 4,
                ConsensusCertificate = new ConsensusCertificate
                {
                    BlockHeight = 100,
                    BlockHash = "h",
                    WinnerAddress = "v",
                    PrevHash = "p",
                    Attestations = new List<CasterAttestation>
                    {
                        new() { CasterAddress = "xNotACommitteeMember", Signature = "MA==.MA==", Timestamp = 0 },
                        new() { CasterAddress = "", Signature = "x", Timestamp = 0 },
                    }
                }
            };
            Assert.Equal(0, CountValidCertWeight(withJunk));
        }

        [Fact]
        public void CertRuleApplicable_RequiresEraAndVersion()
        {
            var origEnforce = ReserveBlockCore.Globals.CertEnforceHeight;
            try
            {
                ReserveBlockCore.Globals.CertEnforceHeight = 1000;
                Assert.False(IsCertRuleApplicable(999, 4));   // below boundary
                Assert.True(IsCertRuleApplicable(1000, 4));   // at boundary, supported version
                Assert.False(IsCertRuleApplicable(1000, 3));  // unsupported version
            }
            finally
            {
                ReserveBlockCore.Globals.CertEnforceHeight = origEnforce;
            }
        }
    }
}
