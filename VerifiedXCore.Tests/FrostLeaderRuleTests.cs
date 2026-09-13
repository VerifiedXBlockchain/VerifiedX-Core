using VerifiedXCore.Bitcoin.FROST;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Follow-up hardening: user withdrawals are led only by their owner (every legitimate
    /// coordinator path sets the requester as leader); bridge exits only by committee casters.
    /// Letting any registered validator lead someone else's withdrawal allowed sign-and-withhold of
    /// a competing transaction, pinning the contract (FIND-028) and stalling the owner indefinitely.
    /// </summary>
    public class FrostLeaderRuleTests
    {
        [Fact]
        public void UserWithdrawal_OnlyOwnerMayLead()
        {
            Assert.True(FrostSigningAuthorization.IsLeaderAllowed(isBridgeExit: false, leaderIsOwner: true, leaderIsCommitteeCaster: false).Ok);
            var (ok, reason) = FrostSigningAuthorization.IsLeaderAllowed(isBridgeExit: false, leaderIsOwner: false, leaderIsCommitteeCaster: true);
            Assert.False(ok);
            Assert.Contains("withdrawal owner", reason);
        }

        [Fact]
        public void BridgeExit_OnlyCommitteeCasterMayLead()
        {
            Assert.True(FrostSigningAuthorization.IsLeaderAllowed(isBridgeExit: true, leaderIsOwner: false, leaderIsCommitteeCaster: true).Ok);
            var (ok, reason) = FrostSigningAuthorization.IsLeaderAllowed(isBridgeExit: true, leaderIsOwner: true, leaderIsCommitteeCaster: false);
            Assert.False(ok);
            Assert.Contains("committee caster", reason);
        }
    }
}
