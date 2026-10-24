using VerifiedXCore.Nodes;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// An agreed block that our own validation refused at the tip does not keep its pin: the pin made the gossip gate refuse
    /// every other block at that height, so the tip stayed wedged until the next agreement.
    /// </summary>
    [Collection("GlobalCasterState")]
    public class CasterRefusedCommitPinTests : System.IDisposable
    {
        private const long Height = 975_533;

        public CasterRefusedCommitPinTests() => Globals.CasterApprovedBlockHashDict.Clear();
        public void Dispose() => Globals.CasterApprovedBlockHashDict.Clear();

        [Fact]
        public void ARefusedCommitAtTheTip_WithdrawsItsPin()
        {
            Globals.CasterApprovedBlockHashDict[Height] = "agreed-hash";

            BlockcasterNode.ReleaseRefusedCommitPin(Height, "agreed-hash", triedAtTip: true);

            Assert.False(Globals.CasterApprovedBlockHashDict.ContainsKey(Height));
        }

        [Fact]
        public void ABlockNotYetTried_KeepsItsPin_ForTheDownloadToReachIt()
        {
            Globals.CasterApprovedBlockHashDict[Height] = "agreed-hash";

            BlockcasterNode.ReleaseRefusedCommitPin(Height, "agreed-hash", triedAtTip: false);

            Assert.Equal("agreed-hash", Globals.CasterApprovedBlockHashDict[Height]);
        }

        [Fact]
        public void APinSinceReplacedByAnotherAgreement_IsLeftAlone()
        {
            Globals.CasterApprovedBlockHashDict[Height] = "newer-agreed-hash";

            BlockcasterNode.ReleaseRefusedCommitPin(Height, "agreed-hash", triedAtTip: true);

            Assert.Equal("newer-agreed-hash", Globals.CasterApprovedBlockHashDict[Height]);
        }
    }
}
