using VerifiedXCore.Bitcoin.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Follow-up hardening: a committee caster's alert with a REAL burn hash but a wrong amount or
    /// destination must not push honest casters into failing a legitimate exit permanently. The
    /// chain is authoritative: when the burn exists on Base, the record is corrected from the
    /// evidence and consensus continues. Only a missing/unreadable burn stops the round.
    /// </summary>
    public class BurnEvidenceCorrectionTests
    {
        [Fact]
        public void PoolUnlockRecord_CorrectedFromEvidence()
        {
            var rec = new BurnExitConsensusService.ProcessedBurnRecord
            {
                BaseBurnTxHash = "0xburn", ExitType = BurnExitConsensusService.BurnExitType.VfxPoolUnlock,
                Amount = 5.0M, VfxDestinationAddress = "RAttackerChosen",
            };
            var ev = new BaseBridgeService.BurnEventInfo { AmountSats = 12_345_678, Destination = "RRealDestination" };

            BurnExitConsensusService.ApplyEvidenceCorrection(rec, ev);

            Assert.Equal(0.12345678M, rec.Amount);
            Assert.Equal("RRealDestination", rec.VfxDestinationAddress);
        }

        [Fact]
        public void BtcExitRecord_CorrectedFromEvidence()
        {
            var rec = new BurnExitConsensusService.ProcessedBurnRecord
            {
                BaseBurnTxHash = "0xburn", ExitType = BurnExitConsensusService.BurnExitType.BtcExit,
                Amount = 0.001M, BtcDestination = "tb1qattacker",
            };
            var ev = new BaseBridgeService.BurnEventInfo { AmountSats = 200_000_000, Destination = "tb1qreal" };

            BurnExitConsensusService.ApplyEvidenceCorrection(rec, ev);

            Assert.Equal(2.0M, rec.Amount);
            Assert.Equal("tb1qreal", rec.BtcDestination);
        }

        /// <summary>
        /// A committee caster's alert naming the other burn type had every caster that took it find no event of that type,
        /// fail the burn and skip it for good (fifth review, round 21). The type is taken from Base like the amount.
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task AnAlertOfTheWrongType_IsCorrectedFromBase()
        {
            BaseBridgeService.BurnEventsForTests = (hash, vfxExit) => vfxExit
                ? (true, new BaseBridgeService.BurnEventInfo { AmountSats = 50_000, Destination = "RRealDestination" }, "")
                : (false, null, "No matching burn event in receipt");
            try
            {
                var rec = new BurnExitConsensusService.ProcessedBurnRecord
                {
                    BaseBurnTxHash = "0xburn", ExitType = BurnExitConsensusService.BurnExitType.BtcExit, Amount = 0.0005M, BtcDestination = "tb1qwrong",
                };
                var (ok, transient, _, evidence) = await BurnExitConsensusService.VerifyBurnEvidenceAsync(rec);
                Assert.False(transient);
                Assert.NotNull(evidence);                                   // the caller corrects the record and goes on
                Assert.Equal(BurnExitConsensusService.BurnExitType.VfxPoolUnlock, rec.ExitType);
                BurnExitConsensusService.ApplyEvidenceCorrection(rec, evidence);
                Assert.Equal("RRealDestination", rec.VfxDestinationAddress);
            }
            finally { BaseBridgeService.BurnEventsForTests = null; }
        }

        /// <summary>When the other type's look-up cannot be answered, the burn is asked again later, not failed (round 22).</summary>
        [Fact]
        public async System.Threading.Tasks.Task AnUnanswerableSecondLookUp_IsTransient()
        {
            BaseBridgeService.BurnEventsForTests = (hash, vfxExit) => vfxExit
                ? (false, null, "Could not read receipt: timeout")
                : (false, null, "No matching burn event in receipt");
            try
            {
                var rec = new BurnExitConsensusService.ProcessedBurnRecord { BaseBurnTxHash = "0xburn", ExitType = BurnExitConsensusService.BurnExitType.BtcExit, Amount = 0.001M };
                var (ok, transient, _, _) = await BurnExitConsensusService.VerifyBurnEvidenceAsync(rec);
                Assert.False(ok);
                Assert.True(transient);
            }
            finally { BaseBridgeService.BurnEventsForTests = null; }
        }

        [Fact]
        public void NullEvidence_LeavesRecordUntouched()
        {
            var rec = new BurnExitConsensusService.ProcessedBurnRecord { Amount = 1.0M, BtcDestination = "x" };
            BurnExitConsensusService.ApplyEvidenceCorrection(rec, null);
            Assert.Equal(1.0M, rec.Amount);
            Assert.Equal("x", rec.BtcDestination);
        }
    
        /// <summary>
        /// A pool unlock was marked complete once broadcast; refused afterwards (its destination's vault filled, its locks
        /// drawn first), its burn sat unused for good. Some blocks after the broadcast, one whose burn the chain has not
        /// consumed is made again (fourth review, round 7).
        /// </summary>
        [Fact]
        public void APoolUnlockNotMined_IsMadeAgain_OneMinedIsNot()
        {
            var rec = new BurnExitConsensusService.ProcessedBurnRecord
            {
                BaseBurnTxHash = "0xburn", ExitType = BurnExitConsensusService.BurnExitType.VfxPoolUnlock,
                Status = BurnExitConsensusService.BurnExitStatus.Complete, CompletionBroadcastAtBlock = 1_000,
            };
            Assert.Null(BurnExitConsensusService.UnlockToRemake(rec, 1_010, _ => false));       // too soon
            Assert.True(BurnExitConsensusService.UnlockToRemake(rec, 1_100, _ => false));       // never mined
            Assert.False(BurnExitConsensusService.UnlockToRemake(rec, 1_100, _ => true));       // mined
            rec.ExitType = BurnExitConsensusService.BurnExitType.BtcExit;
            Assert.Null(BurnExitConsensusService.UnlockToRemake(rec, 1_100, _ => false));       // not an unlock
        }
    }
}
