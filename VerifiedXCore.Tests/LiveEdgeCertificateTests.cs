using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A block that extends a recent tip is at the live edge: its committee certificate exists, so ValidateBlock requires
    /// it on the download path too (a peer could start a download on a synced node). A node catching up from an older tip
    /// keeps the download exemption, since the certificates of those blocks no longer exist.
    /// </summary>
    public class LiveEdgeCertificateTests
    {
        private const long Tip = 7_500_000;
        private const long TipTime = 1_790_000_000;

        [Fact]
        public void TheNextBlockOnARecentTip_IsAtTheLiveEdge()
        {
            Assert.True(BlockValidatorService.AtLiveEdge(Tip + 1, Tip, TipTime, TipTime + 12));
            Assert.True(BlockValidatorService.AtLiveEdge(Tip + 1, Tip, TipTime, TipTime + BlockValidatorService.LiveEdgeTipAgeSeconds));
        }

        [Fact]
        public void CatchingUpFromAnOlderTip_IsNotTheLiveEdge()
        {
            Assert.False(BlockValidatorService.AtLiveEdge(Tip + 1, Tip, TipTime, TipTime + BlockValidatorService.LiveEdgeTipAgeSeconds + 1));
            Assert.False(BlockValidatorService.AtLiveEdge(Tip + 1, Tip, TipTime, TipTime + 86_400));
        }

        [Fact]
        public void ABlockThatDoesNotExtendTheTip_IsNotTheLiveEdge()
        {
            Assert.False(BlockValidatorService.AtLiveEdge(Tip + 2, Tip, TipTime, TipTime + 12));
            Assert.False(BlockValidatorService.AtLiveEdge(Tip, Tip, TipTime, TipTime + 12));
        }
    }
}
