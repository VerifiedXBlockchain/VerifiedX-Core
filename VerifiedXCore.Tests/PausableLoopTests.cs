using System.Threading;
using System.Threading.Tasks;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// The Base exit watcher and the mint attestation loop were written as <c>while (!Globals.StopAllTimers)</c>, and startup
    /// holds StopAllTimers true until the first sync ends: during a sync longer than the loop's interval the loop ended at
    /// its first look and never ran again, so no burn was seen. Both now run through PausableLoop, which waits out the
    /// pause and goes on.
    /// </summary>
    [Collection("DbContextSequential")]
    public class PausableLoopTests
    {
        [Fact]
        public async Task ALoopPausedByStartupSync_GoesOnOnceTheSyncEnds()
        {
            var prior = Globals.StopAllTimers;
            using var cts = new CancellationTokenSource();
            var runs = 0;
            try
            {
                Globals.StopAllTimers = true;                                   // startup: held until the first sync ends
                var loop = PausableLoop.RunAsync(() => { Interlocked.Increment(ref runs); return Task.CompletedTask; }, 20, "test", cts.Token);
                await Task.Delay(300);
                Assert.Equal(0, runs);                                          // nothing runs while paused
                Assert.False(loop.IsCompleted);                                 // paused, not ended

                Globals.StopAllTimers = false;                                  // the sync ends
                await Task.Delay(400);
                Assert.True(runs > 0, "the loop did not go on after the pause");

                cts.Cancel();
                await loop;
            }
            finally
            {
                cts.Cancel();
                Globals.StopAllTimers = prior;
            }
        }
    }
}
