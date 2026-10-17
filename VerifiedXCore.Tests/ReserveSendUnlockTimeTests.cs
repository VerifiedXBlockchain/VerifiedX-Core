using System;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>A reserve send the wallet builds unlocks no sooner than the 24-hour minimum, whatever delay it is given.</summary>
    public class ReserveSendUnlockTimeTests
    {
        private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        [Fact]
        public void ANegativeDelay_StillGivesTheTwentyFourHourMinimum()
        {
            var before = Now;
            var unlock = TimeUtil.GetReserveSendTime(-5);
            Assert.True(unlock >= before + 24 * 3600);
        }

        [Fact]
        public void ZeroAndPositiveDelays_AreUnchanged()
        {
            var before = Now;
            Assert.InRange(TimeUtil.GetReserveSendTime(0), before + 24 * 3600, Now + 24 * 3600);
            Assert.InRange(TimeUtil.GetReserveSendTime(12), before + 36 * 3600, Now + 36 * 3600);
        }
    }
}
