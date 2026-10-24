using VerifiedXCore.Models;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// C-268: each heartbeat in a block reset its validator's failure count, put it back in NetworkValidators if evicted
    /// and cleared its winner failures. Heartbeats are free and one fits in every block, so a validator that failed every
    /// round it won reset its failures before they counted and never left the proof pool. A heartbeat now does this at most
    /// once per HeartbeatRejoinMinBlocks per validator (BlockValidatorService.ApplyHeartbeatRejoin, called for each heartbeat
    /// in a committed block).
    /// </summary>
    [Collection("DbContextSequential")]
    public class HeartbeatRejoinThrottleTests
    {
        [Fact]
        public void AHeartbeat_ResetsAValidatorsFailures_AtMostOncePerWindow()
        {
            var address = "xHeartbeat" + System.Guid.NewGuid().ToString("N")[..8];
            BlockValidatorService.ResetHeartbeatRejoinsForTests();
            try
            {
                BlockValidatorService.ApplyHeartbeatRejoin(address, "10.0.0.9", 1_000);
                Assert.Equal(0, Globals.NetworkValidators[address].CheckFailCount);

                Globals.NetworkValidators[address].CheckFailCount = 4;                    // failing the rounds it wins
                BlockValidatorService.ApplyHeartbeatRejoin(address, "10.0.0.9", 1_001);
                Assert.Equal(4, Globals.NetworkValidators[address].CheckFailCount);          // the next block's heartbeat changes nothing

                Globals.NetworkValidators.TryRemove(address, out _);                          // evicted
                BlockValidatorService.ApplyHeartbeatRejoin(address, "10.0.0.9", 1_000 + BlockValidatorService.HeartbeatRejoinMinBlocks - 1);
                Assert.False(Globals.NetworkValidators.ContainsKey(address));                 // not back within the window

                BlockValidatorService.ApplyHeartbeatRejoin(address, "10.0.0.9", 1_000 + BlockValidatorService.HeartbeatRejoinMinBlocks);
                Assert.Equal(0, Globals.NetworkValidators[address].CheckFailCount);          // the next window's heartbeat re-joins it
            }
            finally
            {
                Globals.NetworkValidators.TryRemove(address, out _);
                BlockValidatorService.ResetHeartbeatRejoinsForTests();
            }
        }
    }
}
