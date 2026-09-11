using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Covers the Sep 2026 connection-churn fix: lag-evicted peers must stay off the dial list for a
    /// cooldown, failed dials must back off exponentially, and the validator connect loop must slow
    /// down when it repeatedly makes no progress toward TARGET_VAL_CONNECTIONS.
    /// </summary>
    [Collection("PeerBackoff")]
    public class PeerConnectionBackoffTests
    {
        public PeerConnectionBackoffTests()
        {
            PeerConnectionBackoff.ResetForTests();
        }

        [Fact]
        public void LagEviction_BlocksIp_UntilCooldownExpires()
        {
            PeerConnectionBackoff.MarkLagEvicted("3.8.125.251", probeFailed: false);

            Assert.True(PeerConnectionBackoff.IsBlocked("3.8.125.251"));
            Assert.Contains("3.8.125.251", PeerConnectionBackoff.BlockedIPs());
            Assert.InRange(PeerConnectionBackoff.RemainingMs("3.8.125.251"),
                PeerConnectionBackoff.LagEvictionCooldownMs - 5_000, PeerConnectionBackoff.LagEvictionCooldownMs);
        }

        [Fact]
        public void ProbeFailure_UsesShorterCooldown_ThanConfirmedLag()
        {
            PeerConnectionBackoff.MarkLagEvicted("10.0.0.1", probeFailed: true);
            PeerConnectionBackoff.MarkLagEvicted("10.0.0.2", probeFailed: false);

            Assert.True(PeerConnectionBackoff.RemainingMs("10.0.0.1") < PeerConnectionBackoff.RemainingMs("10.0.0.2"));
        }

        [Fact]
        public void Normalizes_Ipv6MappedPrefix_AndPortSuffix()
        {
            PeerConnectionBackoff.MarkLagEvicted("::ffff:52.194.221.48:13338", probeFailed: false);

            Assert.True(PeerConnectionBackoff.IsBlocked("52.194.221.48"));
            Assert.Contains("52.194.221.48", PeerConnectionBackoff.BlockedIPs());
        }

        [Fact]
        public void Clear_RemovesBlock_AfterSuccessfulConnect()
        {
            PeerConnectionBackoff.MarkConnectFailure("44.200.94.141", 3);
            Assert.True(PeerConnectionBackoff.IsBlocked("44.200.94.141"));

            PeerConnectionBackoff.Clear("44.200.94.141");

            Assert.False(PeerConnectionBackoff.IsBlocked("44.200.94.141"));
            Assert.DoesNotContain("44.200.94.141", PeerConnectionBackoff.BlockedIPs());
        }

        [Fact]
        public void ExpiredBlock_IsNotReported_AndIsPruned()
        {
            PeerConnectionBackoff.Block("1.2.3.4", 0);

            Assert.False(PeerConnectionBackoff.IsBlocked("1.2.3.4"));
            Assert.DoesNotContain("1.2.3.4", PeerConnectionBackoff.BlockedIPs());
        }

        [Fact]
        public void Block_NeverShortens_AnExistingLongerBlock()
        {
            PeerConnectionBackoff.MarkIncompatible("5.6.7.8"); // 10 min
            PeerConnectionBackoff.MarkConnectFailure("5.6.7.8", 1); // 30 s

            Assert.True(PeerConnectionBackoff.RemainingMs("5.6.7.8") > PeerConnectionBackoff.ConnectFailureBaseMs * 2);
        }

        [Theory]
        [InlineData(0, 30_000)]
        [InlineData(1, 30_000)]
        [InlineData(2, 60_000)]
        [InlineData(3, 120_000)]
        [InlineData(4, 240_000)]
        [InlineData(5, 480_000)]
        [InlineData(6, 600_000)]
        [InlineData(50, 600_000)]
        [InlineData(199, 600_000)]
        public void ConnectFailureDelay_DoublesPerFailure_CappedAtTenMinutes(int failCount, long expectedMs)
        {
            Assert.Equal(expectedMs, PeerConnectionBackoff.ConnectFailureDelayMs(failCount));
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(1, 30_000)]
        [InlineData(2, 60_000)]
        [InlineData(3, 120_000)]
        [InlineData(4, 240_000)]
        [InlineData(5, 300_000)]
        [InlineData(100, 300_000)]
        public void ValidatorConnectLoop_BacksOffWithNoProgress_CappedAtFiveMinutes(int streak, long expectedMs)
        {
            Assert.Equal(expectedMs, ValidatorService.ValConnectBackoffMs(streak));
        }
    }
}
