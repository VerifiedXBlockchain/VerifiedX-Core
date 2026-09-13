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

        [Fact]
        public void NullEvidence_LeavesRecordUntouched()
        {
            var rec = new BurnExitConsensusService.ProcessedBurnRecord { Amount = 1.0M, BtcDestination = "x" };
            BurnExitConsensusService.ApplyEvidenceCorrection(rec, null);
            Assert.Equal(1.0M, rec.Amount);
            Assert.Equal("x", rec.BtcDestination);
        }
    }
}
