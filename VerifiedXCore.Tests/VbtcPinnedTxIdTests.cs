using NBitcoin;
using VerifiedXCore.Bitcoin.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// C-273: a coordinator retrying a withdrawal judged its pinned transaction by LastSignedBtcTxId, which is written only
    /// after a broadcast reported success. A broadcast that timed out but went through left it empty, the spent pinned
    /// coins read as "conflicted away", and the paid withdrawal was rebuilt (and the rebuild refused). The id now comes
    /// from the pinned unsigned transaction itself, which a Taproot witness does not change.
    /// </summary>
    public class VbtcPinnedTxIdTests
    {
        [Fact]
        public void APinnedTransactionsId_ComesFromItsUnsignedBytes_AndSigningDoesNotChangeIt()
        {
            var key = new Key();
            var vault = key.PubKey.GetTaprootFullPubKey().GetAddress(Network.Main);
            var unsigned = Network.Main.CreateTransaction();
            unsigned.Inputs.Add(new TxIn(new OutPoint(new uint256(RandomUtils.GetBytes(32)), 0)));
            unsigned.Outputs.Add(Money.Satoshis(90_000), vault.ScriptPubKey);
            var expected = unsigned.GetHash().ToString();

            Assert.Equal(expected, VBTCService.PinnedTxIdOf(unsigned.ToHex(), null));                 // no id recorded: still known
            Assert.Equal(expected, VBTCService.PinnedTxIdOf(unsigned.ToHex(), "stale-or-other-id"));  // the pinned bytes win

            var signed = unsigned.Clone();
            signed.Inputs[0].WitScript = new WitScript(new[] { new byte[64] });                         // a Taproot key-path signature
            Assert.Equal(expected, signed.GetHash().ToString());

            Assert.Equal("recorded", VBTCService.PinnedTxIdOf(null, "recorded"));
            Assert.Null(VBTCService.PinnedTxIdOf(null, null));                                          // nothing known: reuse, never rebuild
            Assert.Equal(VBTCService.PinnedReuseDecision.ReusePinned, VBTCService.DecidePinnedTxDisposition(true, false, null));
        }
    }
}
