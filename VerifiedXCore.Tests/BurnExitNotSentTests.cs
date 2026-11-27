using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// An exit transaction this node's own mempool would not take (it holds one on the same locks) was sent nowhere. The
    /// caster marked the burn failed, as for any send failure, and gave it up; or, before the sender reported the refusal,
    /// marked it complete and left it for 50 blocks. The burn now goes back to wait a short while and is planned again.
    /// Any other failure is a failure, as before.
    /// </summary>
    public class BurnExitNotSentTests
    {
        private static BurnExitConsensusService.ProcessedBurnRecord Record() => new()
        {
            BaseBurnTxHash = "0x" + new string('a', 64),
            Status = BurnExitConsensusService.BurnExitStatus.VfxTxCreated,
        };

        [Fact]
        public void A_burn_whose_exit_the_mempool_did_not_take_is_planned_again_shortly()
        {
            var record = Record();
            var before = TimeUtil.GetTime();
            var refusal = $"{TransactionData.MempoolRefusedPrefix} (it conflicts with one already waiting).";

            Assert.True(TransactionData.IsMempoolRefusal(refusal));
            Assert.True(BurnExitConsensusService.NotSentIsRetried(record, refusal, "VFX pool unlock"));
            Assert.Equal(BurnExitConsensusService.BurnExitStatus.Pending, record.Status);
            Assert.True(record.NextAttemptAt > before);
            Assert.Equal(1, record.FailedRounds);
        }

        [Fact]
        public void A_burn_whose_exit_a_rule_refused_is_left_to_fail()
        {
            var record = Record();
            Assert.False(TransactionData.IsMempoolRefusal("TX Verify Failed: Exit burn transaction has already been consumed."));
            Assert.False(BurnExitConsensusService.NotSentIsRetried(record, "TX Verify Failed: Exit burn transaction has already been consumed.", "VFX pool unlock"));
            Assert.False(BurnExitConsensusService.NotSentIsRetried(record, null, "EXIT_TO_BTC"));
            Assert.Equal(BurnExitConsensusService.BurnExitStatus.VfxTxCreated, record.Status);
            Assert.Equal(0, record.FailedRounds);
        }
    }
}
