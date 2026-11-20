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

        private static BurnExitConsensusService.ProcessedBurnRecord Due(string burn, string burner, long seenAt, decimal amount = 0.01M,
            BurnExitConsensusService.BurnExitType type = BurnExitConsensusService.BurnExitType.VfxPoolUnlock, string alertedBy = "") => new()
        {
            BaseBurnTxHash = burn, BurnerAddress = burner, DetectedAtBlock = seenAt, Amount = amount, ExitType = type, AlertedBy = alertedBy,
            FieldsFromBase = alertedBy.Length == 0,                                  // this node read it on Base, or a peer alerted it
            Status = BurnExitConsensusService.BurnExitStatus.Pending,
        };

        /// <summary>
        /// A holder's flood of tiny burns does not hold back another holder's exit: a sweep takes each burner's first burns
        /// before anyone's later ones and at most two per burner, first come within that (a later burn cannot be put ahead
        /// of an earlier one), unverified alerts last, and at most two waves in all, so the sweep ends and the stuck-exit
        /// check runs on time.
        /// </summary>
        [Fact]
        public void ASweep_IsFairAndFirstCome_AndBounded()
        {
            var flood = Enumerable.Range(0, 500).Select(i => Due($"flood-{i:D3}", "0xFlooder", seenAt: 100 + i)).ToList();
            var victim = Due("victim", "0xHolder", seenAt: 900);                          // burned after the whole flood
            var early = Due("early", "0xOther", seenAt: 50);
            var alerted = Due("alerted", "0xAlerted", seenAt: 10, alertedBy: "xPeerCaster");

            var sweep = BurnExitConsensusService.SweepOrder(flood.Append(victim).Append(early).Append(alerted));

            Assert.True(sweep.Count <= BurnExitConsensusService.MaxRoundsPerSweep);
            Assert.Equal(BurnExitConsensusService.MaxRoundsPerBurnerPerSweep, sweep.Count(r => r.BurnerAddress == "0xFlooder"));
            Assert.Equal(new[] { "early", "flood-000", "victim" }, sweep.Take(3).Select(r => r.BaseBurnTxHash));   // each burner's first, first come
            Assert.True(sweep.IndexOf(victim) < BurnExitConsensusService.MaxConcurrentRounds, "the holder's exit is not in the first wave");
            Assert.Equal("alerted", sweep.Last().BaseBurnTxHash);                         // what this node cannot verify goes last
            Assert.Equal(new[] { "flood-000", "flood-001" }, sweep.Where(r => r.BurnerAddress == "0xFlooder").Select(r => r.BaseBurnTxHash));
            // The same burns in any order give the same sweep.
            var shuffled = BurnExitConsensusService.SweepOrder(flood.AsEnumerable().Reverse().Append(alerted).Append(early).Append(victim));
            Assert.Equal(sweep.Select(r => r.BaseBurnTxHash), shuffled.Select(r => r.BaseBurnTxHash));
        }

        /// <summary>
        /// A Bitcoin exit below the least a vault's part of an exit must be cannot be paid and is refused after its round:
        /// it is given up before it takes a slot, but only on fields taken from Base, never on a peer's alert (which could
        /// understate a real burn to have it given up). Found on Base by this node's watch, an alert's record takes the
        /// event's exit type, amount, destination and burner.
        /// </summary>
        [Fact]
        public void ABitcoinExitTooSmallToPay_IsGivenUpBeforeItsRound_OnlyOnFieldsFromBase()
        {
            var min = VerifiedXCore.Bitcoin.Attestation.BtcWithdrawals.MinExitPartSats;
            decimal Btc(long sats) => sats / 100_000_000M;
            var btc = BurnExitConsensusService.BurnExitType.BtcExit;
            Assert.True(BurnExitConsensusService.IsUnpayableBtcExit(Due("dust", "0xA", 1, Btc(1), btc)));
            Assert.True(BurnExitConsensusService.IsUnpayableBtcExit(Due("under", "0xA", 1, Btc(min - 1), btc)));
            Assert.False(BurnExitConsensusService.IsUnpayableBtcExit(Due("enough", "0xA", 1, Btc(min), btc)));
            Assert.False(BurnExitConsensusService.IsUnpayableBtcExit(Due("vfx", "0xA", 1, Btc(1))));               // a VFX unlock has no Bitcoin payout

            var held = (System.Collections.Concurrent.ConcurrentDictionary<string, BurnExitConsensusService.ProcessedBurnRecord>)typeof(BurnExitConsensusService)
                .GetField("_processedBurns", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
            // A peer's alert understates a real burn and names another burner.
            var claimed = Due("claimed-" + Guid.NewGuid().ToString("N"), "0xInvented", 1, Btc(1), btc, alertedBy: "xPeerCaster");
            held[claimed.BaseBurnTxHash] = claimed;
            try
            {
                Assert.False(BurnExitConsensusService.IsUnpayableBtcExit(claimed));                               // the alert's claim
                claimed.FoundOnBase = true;                                                                       // found, not corrected
                Assert.False(BurnExitConsensusService.IsUnpayableBtcExit(claimed));
                Assert.Equal("alert:xPeerCaster", BurnExitConsensusService.BurnerKey(claimed));

                // This node's watch reads the event: the record takes what Base holds.
                Assert.True(BurnExitConsensusService.IsAlreadyProcessed(claimed.BaseBurnTxHash, btc, Btc(min * 4), "0xRealBurner", "bc1qrealdestination", ""));
                Assert.True(claimed.FieldsFromBase);
                Assert.Equal(Btc(min * 4), claimed.Amount);
                Assert.Equal("bc1qrealdestination", claimed.BtcDestination);
                Assert.False(BurnExitConsensusService.IsUnpayableBtcExit(claimed));                               // payable: left to its round
                Assert.Equal("burner:0xrealburner", BurnExitConsensusService.BurnerKey(claimed));

                // Corrected once: a later read does not change it.
                Assert.True(BurnExitConsensusService.IsAlreadyProcessed(claimed.BaseBurnTxHash, btc, Btc(1), "0xOther", "bc1qother", ""));
                Assert.Equal(Btc(min * 4), claimed.Amount);
            }
            finally { held.TryRemove(claimed.BaseBurnTxHash, out _); }
        }

        /// <summary>
        /// Once a round has started on a record, the watch of Base does not change its fields under it: the round's evidence
        /// check reads the exit type it started with (a type changed mid-check failed a real burn). Only "found" is noted.
        /// </summary>
        [Fact]
        public void ARecordInARound_IsNotCorrectedUnderIt()
        {
            var held = (System.Collections.Concurrent.ConcurrentDictionary<string, BurnExitConsensusService.ProcessedBurnRecord>)typeof(BurnExitConsensusService)
                .GetField("_processedBurns", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
            var inRound = Due("in-round-" + Guid.NewGuid().ToString("N"), "0xClaimed", 1, 0.01M, BurnExitConsensusService.BurnExitType.BtcExit, alertedBy: "xPeerCaster");
            inRound.Status = BurnExitConsensusService.BurnExitStatus.InConsensus;
            held[inRound.BaseBurnTxHash] = inRound;
            try
            {
                Assert.True(BurnExitConsensusService.IsAlreadyProcessed(inRound.BaseBurnTxHash, BurnExitConsensusService.BurnExitType.VfxPoolUnlock, 0.02M, "0xReal", "", "xVfxDest"));
                Assert.Equal(BurnExitConsensusService.BurnExitType.BtcExit, inRound.ExitType);
                Assert.Equal(0.01M, inRound.Amount);
                Assert.False(inRound.FieldsFromBase);
                Assert.True(inRound.FoundOnBase);
            }
            finally { held.TryRemove(inRound.BaseBurnTxHash, out _); }
        }

        /// <summary>
        /// The burner a peer's alert names is not signed: the sweep counts an alert's burns against the caster that sent it,
        /// so one caster announcing burns under many invented burners still gets one burner's turns.
        /// </summary>
        [Fact]
        public void AnAlertsBurns_CountAgainstItsCaster_NotTheBurnersItNames()
        {
            var invented = Enumerable.Range(0, 50).Select(i =>
            {
                var r = Due($"inv-{i:D2}", $"0xInvented{i}", seenAt: 10 + i, alertedBy: "xLyingCaster");
                r.FoundOnBase = true;                                                    // real burns the watch found, uncorrected
                return r;
            }).ToList();
            var holder = Due("holder", "0xHolder", seenAt: 900);

            var sweep = BurnExitConsensusService.SweepOrder(invented.Append(holder));

            Assert.Equal(BurnExitConsensusService.MaxRoundsPerBurnerPerSweep, sweep.Count(r => r.AlertedBy == "xLyingCaster"));
            Assert.Contains(holder, sweep.Take(BurnExitConsensusService.MaxConcurrentRounds));
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
