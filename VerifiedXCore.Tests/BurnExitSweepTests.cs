using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VerifiedXCore.Bitcoin.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// The pending-burn sweep ran each burn's round in turn, and a round mostly waits (8 s for proposals, 8 s for
    /// confirmations): a holder's many one-satoshi burns kept every other exit behind them. Rounds now run several at
    /// once, and a burn whose round did not reach the threshold waits before it is tried again.
    /// </summary>
    public class BurnExitSweepTests
    {
        [Fact]
        public async Task ManyBurnsRounds_RunSeveralAtOnce_AndOneThatThrowsDoesNotStopTheRest()
        {
            var burns = Enumerable.Range(0, 64).Select(i => $"burn-{i}").ToList();
            var done = new ConcurrentBag<string>();
            var running = 0;
            var most = 0;
            async Task Round(string burn)
            {
                var now = Interlocked.Increment(ref running);
                InterlockedMax(ref most, now);
                try
                {
                    await Task.Delay(200);
                    if (burn == "burn-3") throw new InvalidOperationException("this round failed");
                    done.Add(burn);
                }
                finally { Interlocked.Decrement(ref running); }
            }

            var timer = Stopwatch.StartNew();
            await BurnExitConsensusService.RunRounds(burns, Round, BurnExitConsensusService.MaxConcurrentRounds);
            timer.Stop();

            Assert.Equal(63, done.Count);                                              // all but the one that threw
            Assert.True(most <= BurnExitConsensusService.MaxConcurrentRounds, $"{most} at once");
            Assert.True(most > 1, "the rounds ran one at a time");
            Assert.True(timer.ElapsedMilliseconds < 64 * 200 / 2, $"64 rounds took {timer.ElapsedMilliseconds} ms");
        }

        [Fact]
        public void ARoundShortOfTheThreshold_WaitsLongerEachTime_UpToFiveMinutes()
        {
            Assert.Equal(30, BurnExitConsensusService.RetryDelaySeconds(1));
            Assert.Equal(60, BurnExitConsensusService.RetryDelaySeconds(2));
            Assert.Equal(120, BurnExitConsensusService.RetryDelaySeconds(3));
            Assert.Equal(240, BurnExitConsensusService.RetryDelaySeconds(4));
            Assert.Equal(300, BurnExitConsensusService.RetryDelaySeconds(5));
            Assert.Equal(300, BurnExitConsensusService.RetryDelaySeconds(500));
            Assert.True(BurnExitConsensusService.RetryDelaySeconds(500) < BridgeCasterConsensus.ELECTION_WINDOW_SECONDS);
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int current;
            while (value > (current = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current) { }
        }
    }
}
