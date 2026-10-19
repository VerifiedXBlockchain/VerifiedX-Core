using System.Collections.Generic;
using System.Numerics;
using Nethereum.Hex.HexTypes;
using Nethereum.RPC.Eth.DTOs;
using VerifiedXCore.Bitcoin.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// #277: a bridge burn was keyed on its Base transaction's hash alone, so the second burn of a batched transaction was
    /// taken for the first and never redeemed. A burn's id is the hash for a transaction's only burn and "{hash}:{n}" for
    /// the n-th of several (both kinds, in emitted order): what the exit watcher hands the casters
    /// (BaseBridgeExitWatchService.BurnIdsOf) and what the casters read back from Base (BaseBridgeService.BurnAt, inside
    /// TryGetBurnEventAsync). Each burn has one id.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BridgeBurnIdTests
    {
        private static FilterLog Log(string tx, long index) => new() { TransactionHash = tx, LogIndex = new HexBigInteger(index) };
        private static BaseBridgeService.BurnEventInfo Burn(long sats) => new() { AmountSats = sats, Destination = "dest", Burner = "0xb" };

        [Fact]
        public void TheWatcher_NamesEachBurnOfABatchedTransaction_ByItsPosition_AndALoneBurnByItsHash()
        {
            var batched = "0x" + new string('a', 64);
            var lone = "0x" + new string('b', 64);
            var prior = Globals.PathAuditRulesHeight;
            try
            {
                Globals.PathAuditRulesHeight = 0;
                var ids = BaseBridgeExitWatchService.BurnIdsOf(new[] { Log(batched, 7), Log(lone, 2), Log(batched, 3) });

                Assert.Equal($"{batched}:0", ids[(batched, new BigInteger(3))]);   // emitted first, whichever kind it is
                Assert.Equal($"{batched}:1", ids[(batched, new BigInteger(7))]);
                Assert.Equal(lone, ids[(lone, new BigInteger(2))]);
                Assert.Equal((batched, (int?)1), BaseBridgeService.SplitBurnId($"{batched}:1"));
                Assert.Equal((lone, (int?)null), BaseBridgeService.SplitBurnId(lone));

                // #291: before the path-audit rules a position is refused by every caster, so every burn keeps its
                // transaction's hash, as before #277.
                Globals.PathAuditRulesHeight = long.MaxValue;
                ids = BaseBridgeExitWatchService.BurnIdsOf(new[] { Log(batched, 7), Log(lone, 2), Log(batched, 3) });
                Assert.Equal(batched, ids[(batched, new BigInteger(3))]);
                Assert.Equal(batched, ids[(batched, new BigInteger(7))]);
            }
            finally
            {
                Globals.PathAuditRulesHeight = prior;
            }
        }

        /// <summary>
        /// #291: a positioned burn read by a caster still before the path-audit rules (a block behind the one that named
        /// it) was answered "no" and the record failed and was pruned; it is "not yet", and the record waits to be retried.
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task APositionedBurnReadBeforeTheRules_IsNotYet_NotFailed()
        {
            var prior = BaseBridgeService.BurnEventsForTests;
            try
            {
                BaseBridgeService.BurnEventsForTests = (_, _) => (false, null, BaseBridgeService.PositionBeforeRules);
                var record = new BurnExitConsensusService.ProcessedBurnRecord
                {
                    BaseBurnTxHash = "0x" + new string('c', 64) + ":1", ExitType = BurnExitConsensusService.BurnExitType.BtcExit, Amount = 0.002M, BtcDestination = "dest",
                };
                var (ok, transient, _, evidence) = await BurnExitConsensusService.VerifyBurnEvidenceAsync(record);
                Assert.False(ok);
                Assert.True(transient);                                                   // kept Pending and asked again
                Assert.Null(evidence);
            }
            finally
            {
                BaseBridgeService.BurnEventsForTests = prior;
            }
        }

        [Fact]
        public void BaseIsRead_ForTheOneBurnAnIdNames()
        {
            var prior = Globals.PathAuditRulesHeight;
            try
            {
                Globals.PathAuditRulesHeight = 0;
                var mixed = new List<(bool Vfx, BaseBridgeService.BurnEventInfo? Info)> { (true, Burn(100)), (false, Burn(200)) };

                Assert.Equal(200, BaseBridgeService.BurnAt(mixed, 1, vfxExit: false).Info!.AmountSats);
                Assert.Equal(100, BaseBridgeService.BurnAt(mixed, 0, vfxExit: true).Info!.AmountSats);
                Assert.Contains("No matching burn", BaseBridgeService.BurnAt(mixed, 0, vfxExit: false).Reason); // the other kind: looked up as that
                Assert.False(BaseBridgeService.BurnAt(mixed, 2, vfxExit: false).Ok);
                Assert.False(BaseBridgeService.BurnAt(mixed, null, vfxExit: false).Ok);                      // the hash names no burn of several

                var single = new List<(bool Vfx, BaseBridgeService.BurnEventInfo? Info)> { (false, Burn(300)) };
                Assert.Equal(300, BaseBridgeService.BurnAt(single, null, vfxExit: false).Info!.AmountSats);
                Assert.False(BaseBridgeService.BurnAt(single, 0, vfxExit: false).Ok);                        // a lone burn has no position

                Globals.PathAuditRulesHeight = long.MaxValue;                                                    // before the rules: as it was
                Assert.False(BaseBridgeService.BurnAt(mixed, 1, vfxExit: false).Ok);
                Assert.Equal(200, BaseBridgeService.BurnAt(mixed, null, vfxExit: false).Info!.AmountSats);
            }
            finally
            {
                Globals.PathAuditRulesHeight = prior;
            }
        }
    }
}
