using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using static VerifiedXCore.Utilities.ForkResolutionUtility;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// STALL-RESOLVE (Sep 2026): the pure decision logic behind automatic recovery from a
    /// dead minority fork of ANY age — caster-weighted majority vote, unbounded divergence
    /// search, stall override, retry backoff, and the recovery download-source policy.
    /// </summary>
    [Collection("GlobalCasterState")]
    public class StallResolveTests
    {
        private static ProbeResult P(string ip, string hash, bool caster = false)
            => new ProbeResult { Ip = ip, Hash = hash, IsCaster = caster };

        // ── Majority vote ────────────────────────────────────────────────

        [Fact]
        public void StrictMajority_Boundaries()
        {
            Assert.False(IsStrictMajority(0, 0));
            Assert.False(IsStrictMajority(1, 2));
            Assert.True(IsStrictMajority(2, 3));
            Assert.False(IsStrictMajority(2, 4));
            Assert.True(IsStrictMajority(3, 4));
        }

        [Fact]
        public void CasterMajority_IsDecisive_EvenWhenValidatorsAgreeWithUs()
        {
            // The stranded-four case: three fellow stranded validators hold our hash, two casters don't.
            var results = new List<ProbeResult>
            {
                P("c1", "MAJ", caster: true), P("c2", "MAJ", caster: true),
                P("v1", "OURS"), P("v2", "OURS"), P("v3", "OURS"),
            };
            var v = ComputeMajority(results, "OURS");
            Assert.True(v.IsMinority);
            Assert.Equal("casters", v.DecidedBy);
            Assert.Equal("MAJ", v.MajorityHash);
            Assert.Equal(new[] { "c1", "c2" }, v.MajorityPeers.OrderBy(x => x));
            Assert.Equal(new[] { "v1", "v2", "v3" }, v.AgreeingPeers.OrderBy(x => x));
        }

        [Fact]
        public void CastersAgreeingWithUs_AreAuthoritative_OverValidatorMajority()
        {
            var results = new List<ProbeResult>
            {
                P("c1", "OURS", caster: true), P("c2", "OURS", caster: true),
                P("v1", "X"), P("v2", "X"), P("v3", "X"), P("v4", "X"),
            };
            var v = ComputeMajority(results, "OURS");
            Assert.False(v.IsMinority);
            Assert.Equal("casters", v.DecidedBy);
        }

        [Fact]
        public void SingleCaster_IsNotDecisive_FallsBackToAllResponders()
        {
            var results = new List<ProbeResult>
            {
                P("c1", "MAJ", caster: true),
                P("v1", "MAJ"), P("v2", "MAJ"), P("v3", "OURS"),
            };
            var v = ComputeMajority(results, "OURS");
            Assert.True(v.IsMinority);
            Assert.Equal("all", v.DecidedBy);
            Assert.Equal(3, v.MajorityPeers.Count);
        }

        [Fact]
        public void NoCasters_TieIsNotMinority()
        {
            var results = new List<ProbeResult> { P("a", "X"), P("b", "OURS") };
            Assert.False(ComputeMajority(results, "OURS").IsMinority);
        }

        [Fact]
        public void MajorityHash_IsMostCommonDifferingHash_DeterministicTieBreak()
        {
            var results = new List<ProbeResult> { P("a", "B"), P("b", "A"), P("c", "B"), P("d", "A"), P("e", "C") };
            var v = ComputeMajority(results, "OURS");
            Assert.True(v.IsMinority);
            Assert.Equal("A", v.MajorityHash); // 2 vs 2 → ordinal-lowest key wins
            Assert.Equal(new[] { "b", "d" }, v.MajorityPeers.OrderBy(x => x));
        }

        [Fact]
        public void EmptyOrZeroHashes_AreIgnored()
        {
            var results = new List<ProbeResult> { P("a", ""), P("b", "0"), P("c", "X") };
            var v = ComputeMajority(results, "OURS");
            // Only one valid responder differs: 1/1 is a strict majority of responders.
            Assert.True(v.IsMinority);
            Assert.Equal(1, v.Responders);
        }

        // ── Divergence search ────────────────────────────────────────────

        /// <summary>Local chain: common hashes below forkHeight, own hashes at/above it.</summary>
        private static (Func<long, Task<string?>> peer, Func<long, string?> local, Func<int> probes) Chains(long forkHeight, long peerTip, long? peerUnresponsiveBelow = null, bool genesisDiffers = false)
        {
            int probes = 0;
            Task<string?> Peer(long h)
            {
                probes++;
                if (peerUnresponsiveBelow.HasValue && h < peerUnresponsiveBelow.Value) return Task.FromResult<string?>(null);
                if (h > peerTip) return Task.FromResult<string?>(null);
                if (h == 0 && genesisDiffers) return Task.FromResult<string?>("PG");
                return Task.FromResult<string?>(h >= forkHeight ? $"P{h}" : $"C{h}");
            }
            string? Local(long h) => h == 0 && genesisDiffers ? "LG" : (h >= forkHeight ? $"L{h}" : $"C{h}");
            return (Peer, Local, () => probes);
        }

        [Fact]
        public async Task Divergence_Shallow_DepthOne()
        {
            var (peer, local, _) = Chains(forkHeight: 912064, peerTip: 925000);
            Assert.Equal(912064, await FindDivergenceHeightAsync(peer, local, 912064));
        }

        [Fact]
        public async Task Divergence_WithinLinearWindow()
        {
            var (peer, local, probes) = Chains(forkHeight: 1000, peerTip: 5000);
            Assert.Equal(1000, await FindDivergenceHeightAsync(peer, local, 1005));
            Assert.True(probes() <= LINEAR_SEARCH_WINDOW + 1);
        }

        [Fact]
        public async Task Divergence_Deep_BinarySearch_IsLogarithmic()
        {
            var (peer, local, probes) = Chains(forkHeight: 500, peerTip: 1_500_000);
            Assert.Equal(500, await FindDivergenceHeightAsync(peer, local, 1_000_000));
            Assert.True(probes() < 45, $"probes={probes()}");
        }

        [Fact]
        public async Task Divergence_PeerUnresponsive_ReturnsMinusOne()
        {
            var (peer, local, _) = Chains(forkHeight: 500, peerTip: 2000, peerUnresponsiveBelow: 400);
            Assert.Equal(-1, await FindDivergenceHeightAsync(peer, local, 2000));
        }

        [Fact]
        public async Task Divergence_ForeignChain_GenesisDiffers_ReturnsMinusOne()
        {
            var (peer, local, _) = Chains(forkHeight: 1, peerTip: 2000, genesisDiffers: true);
            Assert.Equal(-1, await FindDivergenceHeightAsync(peer, local, 2000));
        }

        [Fact]
        public async Task Divergence_ZeroTip_ReturnsMinusOne()
        {
            var (peer, local, _) = Chains(forkHeight: 1, peerTip: 10);
            Assert.Equal(-1, await FindDivergenceHeightAsync(peer, local, 0));
        }

        // ── Stall override + backoff ─────────────────────────────────────

        [Fact]
        public void StallOverride_OnlyWhenStalledAndMajorityFarAhead()
        {
            Assert.False(ShouldOverrideLocalWin(stalled: false, localTip: 100, remoteTip: 10_000));
            Assert.False(ShouldOverrideLocalWin(stalled: true, localTip: 100, remoteTip: 100 + ForkChoiceUtility.MAX_REORG_DEPTH));
            Assert.True(ShouldOverrideLocalWin(stalled: true, localTip: 100, remoteTip: 100 + ForkChoiceUtility.MAX_REORG_DEPTH + 1));
        }

        [Fact]
        public void Backoff_Schedule()
        {
            Assert.Equal(0, NextBackoffSeconds(0));
            Assert.Equal(10, NextBackoffSeconds(1));
            Assert.Equal(30, NextBackoffSeconds(2));
            Assert.Equal(60, NextBackoffSeconds(3));
            Assert.Equal(120, NextBackoffSeconds(4));
            Assert.Equal(120, NextBackoffSeconds(400));
        }

        // ── Recovery source policy ───────────────────────────────────────

        [Fact]
        public void SourcePolicy_Inactive_EverythingEligible()
        {
            RecoverySourcePolicy.Clear();
            Assert.False(RecoverySourcePolicy.IsActive);
            Assert.True(RecoverySourcePolicy.IsEligible("1.2.3.4"));
            Assert.True(RecoverySourcePolicy.IsEligible((string?)null));
        }

        [Fact]
        public void SourcePolicy_AllowList_And_Quarantine()
        {
            try
            {
                RecoverySourcePolicy.Set(new[] { "10.0.0.1", "::ffff:10.0.0.2" }, new[] { "10.0.0.9", "10.0.0.1" });
                Assert.True(RecoverySourcePolicy.IsActive);
                Assert.True(RecoverySourcePolicy.IsEligible("10.0.0.1"));      // allowed wins over quarantine
                Assert.True(RecoverySourcePolicy.IsEligible("::ffff:10.0.0.2")); // normalized
                Assert.False(RecoverySourcePolicy.IsEligible("10.0.0.9"));     // quarantined
                Assert.False(RecoverySourcePolicy.IsEligible("10.0.0.7"));     // not on allow-list
                Assert.False(RecoverySourcePolicy.IsEligible(""));
                Assert.Equal(2, RecoverySourcePolicy.AllowedCount);
                Assert.Equal(1, RecoverySourcePolicy.QuarantinedCount);
            }
            finally
            {
                RecoverySourcePolicy.Clear();
            }
        }

        [Fact]
        public void SourcePolicy_QuarantineOnly_AllowsEveryoneElse()
        {
            try
            {
                RecoverySourcePolicy.Set(Array.Empty<string>(), new[] { "10.0.0.9" });
                Assert.True(RecoverySourcePolicy.IsActive);
                Assert.False(RecoverySourcePolicy.IsEligible("10.0.0.9"));
                Assert.True(RecoverySourcePolicy.IsEligible("10.0.0.1"));
            }
            finally
            {
                RecoverySourcePolicy.Clear();
            }
        }

        [Fact]
        public void ForkDetection_StrictMajority_DelegatesToUtility()
        {
            Assert.True(ForkDetectionService.IsStrictMajority(2, 3));
            Assert.False(ForkDetectionService.IsStrictMajority(1, 2));
        }
    }
}
