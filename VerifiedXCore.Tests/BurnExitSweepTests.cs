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

        /// <summary>
        /// A round short of the threshold puts its burn back with a retry time; the sweep leaves it until then, and a round
        /// that reaches consensus starts the count again.
        /// </summary>
        [Fact]
        public void AShortRound_KeepsItsBurnOutOfTheSweepUntilItsRetryTime()
        {
            const long t = 1_700_000_000;
            BurnExitConsensusService.ProcessedBurnRecord Burn(string id) => new() { BaseBurnTxHash = id, Status = BurnExitConsensusService.BurnExitStatus.Pending };
            var shortOne = Burn("short");
            var other = Burn("other");
            shortOne.Status = BurnExitConsensusService.BurnExitStatus.InConsensus;   // as RunConsensusRound has it then

            Assert.Equal(30, BurnExitConsensusService.NoteShortRound(shortOne, t));
            Assert.Equal(BurnExitConsensusService.BurnExitStatus.Pending, shortOne.Status);
            Assert.Equal(1, shortOne.FailedRounds);
            Assert.Equal(t + 30, shortOne.NextAttemptAt);
            Assert.Equal(new[] { "other" }, BurnExitConsensusService.DueForARound(new[] { shortOne, other }, t + 29).Select(b => b.BaseBurnTxHash));
            Assert.Equal(new[] { "short", "other" }, BurnExitConsensusService.DueForARound(new[] { shortOne, other }, t + 30).Select(b => b.BaseBurnTxHash));

            Assert.Equal(60, BurnExitConsensusService.NoteShortRound(shortOne, t + 30));   // short again: longer
            Assert.Empty(BurnExitConsensusService.DueForARound(new[] { shortOne }, t + 89));
            // A burn in consensus or done is not taken whatever its time.
            other.Status = BurnExitConsensusService.BurnExitStatus.ConsensusReached;
            Assert.Empty(BurnExitConsensusService.DueForARound(new[] { other }, t + 10_000));
        }

        /// <summary>
        /// An exit waiting its turn keeps the votes its round collected. If the committee changed meanwhile, they are checked
        /// against the new one before the exit is sent; short of its threshold the burn goes back for a fresh round.
        /// </summary>
        [Fact]
        public void AQueuedExitsVotes_AreCheckedAgainstTheCommitteeAtItsTurn()
        {
            var votes = new[] { "xA", "xB", "xC" }.Select(a => new VerifiedXCore.Bitcoin.Models.CasterConsensusVote { CasterAddress = a }).ToList();
            Assert.True(BurnExitConsensusService.VotesCarry(votes, new System.Collections.Generic.HashSet<string> { "xA", "xB", "xC", "xD" }, 3));
            Assert.False(BurnExitConsensusService.VotesCarry(votes, new System.Collections.Generic.HashSet<string> { "xA", "xD", "xE", "xF" }, 3));
            votes.Add(new VerifiedXCore.Bitcoin.Models.CasterConsensusVote { CasterAddress = "xA" });             // one caster twice counts once
            Assert.False(BurnExitConsensusService.VotesCarry(votes, new System.Collections.Generic.HashSet<string> { "xA", "xB", "xD" }, 3));
        }

        /// <summary>
        /// The next exit waits for the one before to be mined only when that one sent something: an exit that failed (a
        /// small burn with no eligible lock) or that the chain already held drew no lock, and holding the turn for it
        /// brought back the one-at-a-time delay.
        /// </summary>
        [Theory]
        [InlineData(BurnExitConsensusService.BurnExitStatus.VfxTxCreated, 0L, true)]          // a Bitcoin exit sent
        [InlineData(BurnExitConsensusService.BurnExitStatus.FrostInProgress, 0L, true)]
        [InlineData(BurnExitConsensusService.BurnExitStatus.Complete, 100L, true)]            // a VFX pool unlock broadcast at this turn
        [InlineData(BurnExitConsensusService.BurnExitStatus.Complete, 99L, false)]            // broadcast at an earlier turn
        [InlineData(BurnExitConsensusService.BurnExitStatus.Complete, 0L, false)]             // the chain already had it
        [InlineData(BurnExitConsensusService.BurnExitStatus.Failed, 0L, false)]               // nothing eligible: nothing sent
        [InlineData(BurnExitConsensusService.BurnExitStatus.ConsensusReached, 0L, false)]
        [InlineData(BurnExitConsensusService.BurnExitStatus.Pending, 0L, false)]
        public void TheNextExitWaits_OnlyAfterOneWasSent(BurnExitConsensusService.BurnExitStatus after, long broadcastAt, bool waits)
        {
            var record = new BurnExitConsensusService.ProcessedBurnRecord { Status = after, CompletionBroadcastAtBlock = broadcastAt };
            Assert.Equal(waits, BurnExitConsensusService.SentAnExit(record, tipBefore: 100));
        }

        /// <summary>
        /// After an exit is sent, the next one waits until it is on chain (its burn recorded as used, which is when its locks
        /// are drawn), not a fixed number of blocks: no sooner, and at most the cap, after which the unmined retry takes it.
        /// </summary>
        [Fact]
        public async Task AnExitsTurn_LastsUntilItIsOnChain_OrTheCap()
        {
            var minedAt = DateTime.UtcNow + TimeSpan.FromMilliseconds(400);
            var timer = Stopwatch.StartNew();
            Assert.True(await BurnExitConsensusService.WaitUntilOnChain(() => DateTime.UtcNow >= minedAt, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(50)));
            timer.Stop();
            Assert.InRange(timer.ElapsedMilliseconds, 350, 3_000);                     // until it was mined, not the cap

            timer.Restart();
            Assert.False(await BurnExitConsensusService.WaitUntilOnChain(() => false, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(50)));
            Assert.InRange(timer.ElapsedMilliseconds, 250, 3_000);                     // never mined: the cap
            Assert.True(await BurnExitConsensusService.WaitUntilOnChain(() => true, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1))); // already on chain: no wait
            Assert.True(BurnExitConsensusService.MostAnExitHoldsTheTurn <= TimeSpan.FromSeconds(60));
        }

        /// <summary>The wait reads one lookup a second: whether the exit's own transaction is in a committed block.</summary>
        [Fact]
        public void AnExitIsOnChain_WhenTheTransactionItSentIsInACommittedBlock()
        {
            var record = new BurnExitConsensusService.ProcessedBurnRecord { CompletionVfxTxHash = "sent-" + Guid.NewGuid().ToString("N") };
            var applied = typeof(VerifiedXCore.Data.StateData).GetField("_stateAppliedThrough", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            var priorApplied = (long)applied.GetValue(null)!;
            Assert.False(BurnExitConsensusService.SentTransactionIsInABlock(record));
            Globals.MemBlocks[record.CompletionVfxTxHash] = 1_000;                   // as TxHashGuard.Add records a committed block's transactions
            try
            {
                applied.SetValue(null, 999L);                                         // committed, its state not applied yet
                Assert.False(BurnExitConsensusService.SentTransactionIsInABlock(record));
                applied.SetValue(null, 1_000L);                                       // UpdateTreis finished the block
                Assert.True(BurnExitConsensusService.SentTransactionIsInABlock(record));
            }
            finally { Globals.MemBlocks.TryRemove(record.CompletionVfxTxHash, out _); applied.SetValue(null, priorApplied); }
            Assert.False(BurnExitConsensusService.SentTransactionIsInABlock(new BurnExitConsensusService.ProcessedBurnRecord())); // nothing sent
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int current;
            while (value > (current = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current) { }
        }
    }
}

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// The height an exit's turn waits for is moved only by a block whose state was applied in full: after a failed apply
    /// the locks have not changed, and the next exit must not read them as if they had.
    /// </summary>
    [Collection("DbContextSequential")]
    public class StateAppliedThroughTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;

        public StateAppliedThroughTests()
        {
            _tempRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"applied_{Guid.NewGuid():N}") + System.IO.Path.DirectorySeparatorChar;
            System.IO.Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            VerifiedXCore.Data.DbContext.Initialize();
        }

        public void Dispose()
        {
            VerifiedXCore.Data.StateData.ApplyStepForTests = null;
            try { VerifiedXCore.Data.DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { System.IO.Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        [Fact]
        public async Task OnlyABlockAppliedInFull_MovesTheAppliedHeight()
        {
            VerifiedXCore.Models.Block At(long h) => new() { Height = h, Hash = $"b{h}-{Guid.NewGuid():N}", Timestamp = VerifiedXCore.Utilities.TimeUtil.GetTime(), Transactions = new System.Collections.Generic.List<VerifiedXCore.Models.Transaction>() };

            Assert.True(await VerifiedXCore.Data.StateData.UpdateTreis(At(5_000)));
            Assert.Equal(5_000, VerifiedXCore.Data.StateData.StateAppliedThrough);

            VerifiedXCore.Data.StateData.ApplyStepForTests = _ => throw new System.IO.IOException("this node's storage failed");
            try { await VerifiedXCore.Data.StateData.UpdateTreis(At(5_001)); } catch (System.IO.IOException) { }
            Assert.Equal(5_000, VerifiedXCore.Data.StateData.StateAppliedThrough);   // not applied: not counted
            VerifiedXCore.Data.StateData.ApplyStepForTests = null;

            Assert.True(await VerifiedXCore.Data.StateData.UpdateTreis(At(5_001)));
            Assert.Equal(5_001, VerifiedXCore.Data.StateData.StateAppliedThrough);
        }
    }
}
