using System.Collections.Concurrent;
using VerifiedXCore.Nodes;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Regression tests for the Sep 12 2026 bootstrap-node incident (stuck at 7286400 for 4h20m):
    ///  1) rollback/snapshot-restore left the rolled-back block's TX hashes in the MemBlocks replay
    ///     guard, so the same block re-delivered by the majority failed ValidateBlock()-13 forever;
    ///  2) the GetAllBlocks inner loop re-fetched that block indefinitely, so the recovery holding
    ///     IsRecoveryInProgress never returned and every other self-heal path was locked out;
    ///  3) "already been sent" rejections were counted as state-trie corruption and, once the
    ///     snapshot restore had been tried, the node re-flagged/re-logged "exhausted" every 1-3 s;
    ///  4) two message-7 arrivals 6 s apart during one stall counted as two failed desync attempts
    ///     and rolled back a healthy tip whose next block had merely been refused by this node's
    ///     own CASTER-HASH-PENDING gate.
    /// </summary>
    [Collection("GlobalCasterState")]
    public class RollbackReplayGuardRecoveryTests
    {
        // ── 1) MemBlocks replay guard is pruned above the rollback target ──────────

        [Fact]
        public void PruneMemBlocksAbove_RemovesOnlyEntriesAboveTarget()
        {
            var saved = Globals.MemBlocks;
            try
            {
                Globals.MemBlocks = new ConcurrentDictionary<string, long>();
                Globals.MemBlocks["tx-at-399"] = 7286399;
                Globals.MemBlocks["tx-at-400"] = 7286400;
                Globals.MemBlocks["heartbeat-dfc37577"] = 7286401; // the rolled-back block's TX
                Globals.MemBlocks["tx-at-402"] = 7286402;

                var removed = BlockRollbackUtility.PruneMemBlocksAbove(7286400);

                Assert.Equal(2, removed);
                Assert.True(Globals.MemBlocks.ContainsKey("tx-at-399"));
                Assert.True(Globals.MemBlocks.ContainsKey("tx-at-400"));
                Assert.False(Globals.MemBlocks.ContainsKey("heartbeat-dfc37577"));
                Assert.False(Globals.MemBlocks.ContainsKey("tx-at-402"));
            }
            finally
            {
                Globals.MemBlocks = saved;
            }
        }

        [Fact]
        public void PruneMemBlocksAbove_NothingAboveTarget_RemovesNothing()
        {
            var saved = Globals.MemBlocks;
            try
            {
                Globals.MemBlocks = new ConcurrentDictionary<string, long>();
                Globals.MemBlocks["a"] = 10;
                Globals.MemBlocks["b"] = 20;

                Assert.Equal(0, BlockRollbackUtility.PruneMemBlocksAbove(20));
                Assert.Equal(2, Globals.MemBlocks.Count);
            }
            finally
            {
                Globals.MemBlocks = saved;
            }
        }

        // ── 2) download loop bails after repeated re-fetches of a rejected tip+1 ────

        [Fact]
        public void ExceededTipRefetches_OnlyAboveCap()
        {
            Assert.False(BlockDownloadService.ExceededTipRefetches(0));
            Assert.False(BlockDownloadService.ExceededTipRefetches(BlockDownloadService.MAX_TIP_REFETCHES_WITHOUT_PROGRESS));
            Assert.True(BlockDownloadService.ExceededTipRefetches(BlockDownloadService.MAX_TIP_REFETCHES_WITHOUT_PROGRESS + 1));
        }

        // ── 3) replay-guard rejections are not state-corruption evidence ───────────

        [Theory]
        [InlineData("This transactions has already been sent.")]
        [InlineData("Duplicate nullifier within block.")]
        [InlineData("Duplicate withdrawal request for contract abc within block.")]
        public void IsStateCorruptionSignal_False_ForReplayAndInBlockDuplicates(string reason)
        {
            Assert.False(BlockValidatorService.IsStateCorruptionSignal(reason));
        }

        [Theory]
        [InlineData("This is a new account with no balance.")]
        [InlineData("Nonce is incorrect.")]
        [InlineData("")]
        [InlineData(null)]
        public void IsStateCorruptionSignal_True_ForStateShapedOrUnknownReasons(string? reason)
        {
            Assert.True(BlockValidatorService.IsStateCorruptionSignal(reason));
        }

        [Fact]
        public void AlreadySentReason_IsSharedConstant()
        {
            // The filter keys on the exact string both validators return.
            Assert.Equal("This transactions has already been sent.", TransactionValidatorService.TX_ALREADY_SENT_REASON);
        }

        // ── 4) desync escalation: spaced attempts + own-gate stall guard ──────────

        [Fact]
        public void CountsAsNewDesyncAttempt_RequiresFullStallWindow()
        {
            Assert.False(BlockcasterNode.CountsAsNewDesyncAttempt(0));
            Assert.False(BlockcasterNode.CountsAsNewDesyncAttempt(6_000)); // the incident: 3:47:25 → 3:47:31
            Assert.False(BlockcasterNode.CountsAsNewDesyncAttempt(BlockcasterNode.DESYNC_RECOVERY_TIMEOUT_MS - 1));
            Assert.True(BlockcasterNode.CountsAsNewDesyncAttempt(BlockcasterNode.DESYNC_RECOVERY_TIMEOUT_MS));
            Assert.True(BlockcasterNode.CountsAsNewDesyncAttempt(long.MaxValue / 2)); // first ever attempt
        }

        [Fact]
        public void ShouldEscalateDesyncToRollback_NeedsSpacedAttempts_AndNotOwnGate()
        {
            Assert.False(BlockcasterNode.ShouldEscalateDesyncToRollback(1, ownPendingGateStall: false));
            Assert.True(BlockcasterNode.ShouldEscalateDesyncToRollback(2, ownPendingGateStall: false));
            Assert.False(BlockcasterNode.ShouldEscalateDesyncToRollback(2, ownPendingGateStall: true));
            Assert.False(BlockcasterNode.ShouldEscalateDesyncToRollback(5, ownPendingGateStall: true));
        }

        [Fact]
        public void IsOwnPendingGateStall_TrueOnlyForRecentRefusalAtTipPlusOne()
        {
            const long now = 1_000_000;
            const long window = 90_000;

            BlockValidatorService.RecordCasterHashPending(7286402, now - 6_000);

            Assert.True(BlockValidatorService.IsOwnPendingGateStall(7286401, window, now));   // tip 7286401, refused 7286402
            Assert.False(BlockValidatorService.IsOwnPendingGateStall(7286400, window, now));  // tip moved elsewhere
            Assert.False(BlockValidatorService.IsOwnPendingGateStall(7286402, window, now));  // refused height already committed
            Assert.False(BlockValidatorService.IsOwnPendingGateStall(7286401, window, now + window + 1)); // aged out

            // A newer refusal at a different height supersedes the old signal.
            BlockValidatorService.RecordCasterHashPending(7286410, now);
            Assert.False(BlockValidatorService.IsOwnPendingGateStall(7286401, window, now));
            Assert.True(BlockValidatorService.IsOwnPendingGateStall(7286409, window, now));

            BlockValidatorService.RecordCasterHashPending(-1, 0);
        }
    }
}
