using System.Linq;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Follow-up hardening: the committee used to verify bridge votes decides block validity, so it
    /// must come only from deterministic sources (the signed membership record for the height, else
    /// the hard-coded seed allowlist). Node-local live caster views (BlockCasters, KnownCasters) are
    /// excluded.
    /// </summary>
    public class CommitteeDeterminismTests
    {
        [Fact]
        public void WithoutMembershipRecord_FallbackIsExactlyTheSeedAllowlist()
        {
            // No DB / no record era in this test process: the fallback must be the seed set, and
            // must NOT include whatever happens to be in the live caster bag.
            Globals.BlockCasters.Add(new Models.Peers { ValidatorAddress = "xLiveOnlyCaster", PeerIP = "1.2.3.4" });
            try
            {
                var committee = BridgeCasterConsensus.GetCommitteeForHeight(123);
                Assert.Equal(Globals.BootstrapCasterAddresses.OrderBy(x => x), committee.OrderBy(x => x));
                Assert.DoesNotContain("xLiveOnlyCaster", committee);
            }
            finally
            {
                var kept = Globals.BlockCasters.Where(p => p.ValidatorAddress != "xLiveOnlyCaster").ToList();
                Globals.BlockCasters = new System.Collections.Concurrent.ConcurrentBag<Models.Peers>(kept);
            }
        }

        [Fact]
        public void RequiredVotes_IsMajorityOfCommittee_WithFloorOfTwo()
        {
            Assert.Equal(2, BridgeCasterConsensus.RequiredVotesFor(new System.Collections.Generic.HashSet<string> { "a" }));
            Assert.Equal(2, BridgeCasterConsensus.RequiredVotesFor(new System.Collections.Generic.HashSet<string> { "a", "b", "c" }));
            Assert.Equal(3, BridgeCasterConsensus.RequiredVotesFor(new System.Collections.Generic.HashSet<string> { "a", "b", "c", "d", "e" }));
        }
    }
}
