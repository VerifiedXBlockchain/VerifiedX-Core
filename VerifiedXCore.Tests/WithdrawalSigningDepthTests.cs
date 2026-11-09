using System;
using System.Threading.Tasks;
using VerifiedXCore.Bitcoin.FROST;
using VerifiedXCore.Models;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A signer gives no share for a withdrawal on a vault the chain does not watch until its request block is past any reorg
    /// the chain makes on its own, and still on this node's chain by hash. A request in a block that is later replaced could
    /// otherwise be signed and paid on Bitcoin before its VFX side reverts.
    /// </summary>
    [Collection("GlobalCasterState")]
    public class WithdrawalSigningDepthTests
    {
        [Fact]
        public void AShareWaitsTwelveBlocksBelowTheTip_AndForTheRequestToStillBeInItsBlock()
        {
            var reqAt = 1_000L;
            Func<long, string, bool> holds = (_, _) => true;
            Assert.Equal(12, FrostSigningAuthorization.WithdrawalSigningDepth);
            Assert.Contains("signing starts", FrostSigningAuthorization.RequestNotSettled("w", reqAt, reqAt, holds)!);
            Assert.Contains("signing starts", FrostSigningAuthorization.RequestNotSettled("w", reqAt, reqAt + 11, holds)!);
            Assert.Null(FrostSigningAuthorization.RequestNotSettled("w", reqAt, reqAt + 12, holds));
            Assert.Contains("no longer", FrostSigningAuthorization.RequestNotSettled("w", reqAt, reqAt + 12, (_, _) => false)!);
            Assert.Null(FrostSigningAuthorization.RequestNotSettled("w", 0, 5, (_, _) => false)); // no mined height recorded
        }

        [Fact]
        public async Task TheLeaderWaitsForTheDepth_AndGivesUpOnAChainThatIsNotMoving()
        {
            var prior = Globals.LastBlock;
            try
            {
                Globals.LastBlock = new Block { Height = 500 };
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await FrostSigningAuthorization.WaitForRequestToSettleAsync(500 - 12, maxSeconds: 60); // deep enough: at once
                Assert.True(sw.ElapsedMilliseconds < 1_000);
                sw.Restart();
                await FrostSigningAuthorization.WaitForRequestToSettleAsync(499, maxSeconds: 2);
                Assert.True(sw.ElapsedMilliseconds >= 1_500);
            }
            finally { Globals.LastBlock = prior; }
        }
    }
}
