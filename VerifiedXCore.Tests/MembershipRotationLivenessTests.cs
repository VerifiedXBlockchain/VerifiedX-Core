using VerifiedXCore.Models;
using VerifiedXCore.Services;
using Xunit;
using static VerifiedXCore.Services.CasterMembershipStore;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// ROTATION-LIVENESS (Sep 13 2026): the mainnet caster committee deadlocked at record seq 3 for
    /// eleven hours. Every proposer built its own record for the same change (different
    /// EffectiveFromHeight / member IP view → different hash), locked itself to that hash with the
    /// equivocation marker, and no record could ever reach 3-of-5 again because every retry was a
    /// "different record". These tests pin the pure decisions behind the fix:
    ///  • equivocation identity is the caster SET, not the record hash;
    ///  • pre-fix markers (no set identity) are upgraded once instead of blocking forever;
    ///  • the effective height is window-quantized so same-window proposers build the same record;
    ///  • a same-set sibling with a lower hash is adopted so the chain converges.
    /// </summary>
    public class MembershipRotationLivenessTests
    {
        private static CasterMembershipRecord Rec(long seq, long eff, string prev, params (string Addr, string Ip, string Pk)[] casters)
        {
            var r = new CasterMembershipRecord
            {
                RecordSeq = seq,
                EffectiveFromHeight = eff,
                PrevRecordHash = prev,
                ChangeType = "Promotion",
                ChangedAddress = casters.Last().Addr,
                Casters = casters.Select(c => new CasterInfo { Address = c.Addr, PeerIP = c.Ip, PublicKey = c.Pk }).ToList(),
                Signatures = new List<RecordSignature>()
            };
            r.RecordHash = ComputeRecordHash(r);
            return r;
        }

        // ── Equivocation identity ───────────────────────────────────────────

        [Fact]
        public void SignedMarker_NoMarker_Inserts()
        {
            Assert.Equal(SignedMarkDecision.Insert, EvaluateSignedMarker(null, "h1", "s1"));
        }

        [Fact]
        public void SignedMarker_SameRecord_IsIdempotent()
        {
            var m = new SignedSeqMarker { Id = 3, RecordHash = "H1", SetHash = "s1" };
            Assert.Equal(SignedMarkDecision.Idempotent, EvaluateSignedMarker(m, "h1", "s1"));
        }

        [Fact]
        public void SignedMarker_SameSetDifferentHash_IsAllowed()
        {
            // The incident: two proposers, same promotion, different EffectiveFromHeight.
            var m = new SignedSeqMarker { Id = 3, RecordHash = "h-proposer-A", SetHash = "set-with-R9Kr" };
            Assert.Equal(SignedMarkDecision.SameSetRefresh, EvaluateSignedMarker(m, "h-proposer-B", "set-with-R9Kr"));
        }

        [Fact]
        public void SignedMarker_DifferentSet_IsRefused()
        {
            var m = new SignedSeqMarker { Id = 3, RecordHash = "h1", SetHash = "set-with-X" };
            Assert.Equal(SignedMarkDecision.Refuse, EvaluateSignedMarker(m, "h2", "set-with-Y"));
        }

        [Fact]
        public void SignedMarker_LegacyMarkerWithoutSet_IsUpgradedOnce()
        {
            // Marker persisted by the pre-fix build: only a hash, no set identity. This is exactly
            // the object that held the deadlock in place across restarts.
            var legacy = new SignedSeqMarker { Id = 3, RecordHash = "h-old" };
            Assert.Equal(SignedMarkDecision.LegacyUpgrade, EvaluateSignedMarker(legacy, "h-new", "set-new"));
        }

        // ── Caster-set identity ─────────────────────────────────────────────

        [Fact]
        public void CasterSetHash_IgnoresOrderIpKeyAndHeight()
        {
            var a = Rec(3, 100, "p", ("xA", "1.1.1.1", "PKA"), ("xB", "2.2.2.2", "PKB"));
            var b = Rec(3, 110, "p", ("xB", "9.9.9.9", ""), ("xA", "::ffff:1.1.1.1", "PKA2"));
            Assert.NotEqual(a.RecordHash, b.RecordHash);
            Assert.Equal(ComputeCasterSetHash(a), ComputeCasterSetHash(b));
        }

        [Fact]
        public void CasterSetHash_DiffersForDifferentSets()
        {
            var a = Rec(3, 100, "p", ("xA", "", ""), ("xB", "", ""));
            var b = Rec(3, 100, "p", ("xA", "", ""), ("xC", "", ""));
            Assert.NotEqual(ComputeCasterSetHash(a), ComputeCasterSetHash(b));
            Assert.Equal(64, ComputeCasterSetHash(a).Length);
        }

        // ── Effective-height quantization ───────────────────────────────────

        [Fact]
        public void RotationEffectiveHeight_SameWindow_SameValue_AndAheadOfTip()
        {
            var values = Enumerable.Range(100, 8).Select(tip => ComputeRotationEffectiveHeight(50, tip)).Distinct().ToList();
            Assert.Single(values);                     // tips 100..107 → one record height
            Assert.Equal(110, values[0]);
            for (long tip = 100; tip < 200; tip++)
                Assert.True(ComputeRotationEffectiveHeight(50, tip) > tip + RotationEffectiveMargin - 1);
        }

        [Fact]
        public void RotationEffectiveHeight_NeverBelowHeadPlusOne()
        {
            Assert.Equal(501, ComputeRotationEffectiveHeight(500, 100));
        }

        // ── Sibling adoption ────────────────────────────────────────────────

        [Fact]
        public void CanonicalSibling_LowerHashSameSetSamePrev_IsAdoptable()
        {
            var x = Rec(3, 110, "prev", ("xA", "1.1.1.1", "K"), ("xB", "2.2.2.2", "K"));
            var y = Rec(3, 120, "prev", ("xA", "1.1.1.1", "K"), ("xB", "2.2.2.2", "K"));
            Assert.NotEqual(x.RecordHash, y.RecordHash);
            var lower = string.CompareOrdinal(x.RecordHash, y.RecordHash) < 0 ? x : y;
            var higher = ReferenceEquals(lower, x) ? y : x;
            Assert.True(IsCanonicalSibling(head: higher, candidate: lower));
            Assert.False(IsCanonicalSibling(head: lower, candidate: higher)); // never move to the higher hash
            Assert.False(IsCanonicalSibling(head: lower, candidate: lower));  // same record is not a sibling
        }

        [Fact]
        public void CanonicalSibling_RejectsDifferentSetPrevOrSeq()
        {
            var head = Rec(3, 110, "prev", ("xA", "", ""), ("xB", "", ""));
            Assert.False(IsCanonicalSibling(head, Rec(3, 120, "prev", ("xA", "", ""), ("xC", "", ""))));   // different set
            Assert.False(IsCanonicalSibling(head, Rec(3, 120, "other", ("xA", "", ""), ("xB", "", ""))));  // different predecessor
            Assert.False(IsCanonicalSibling(head, Rec(4, 120, "prev", ("xA", "", ""), ("xB", "", ""))));   // different seq
        }
    }
}
