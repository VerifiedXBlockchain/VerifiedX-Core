using NBitcoin;
using VerifiedXCore.Bitcoin.FROST;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Follow-up hardening: the contract-level outpoint pin (non-expiring double-payout backstop)
    /// was created only when the leader supplied TxInputOutpoints/BtcTxId. Both are now derived from
    /// the parsed transaction inside authorization, so omitting them cannot disable the pin.
    /// </summary>
    public class FrostTxBindingDerivationTests
    {
        [Fact]
        public void DerivesTxIdAndOrderedLowercaseOutpoints()
        {
            var tx = Transaction.Create(Network.TestNet);
            var h1 = new uint256("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var h2 = new uint256("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            tx.Inputs.Add(new OutPoint(h1, 3));
            tx.Inputs.Add(new OutPoint(h2, 0));
            tx.Outputs.Add(Money.Satoshis(1000), new Key().PubKey.WitHash.ScriptPubKey);

            var (txid, outpoints) = FrostSigningAuthorization.DeriveTxBinding(tx);

            Assert.Equal(tx.GetHash().ToString().ToLowerInvariant(), txid);
            Assert.Equal(new[] { $"{h1}:3", $"{h2}:0" }, outpoints.ToArray());
            Assert.All(outpoints, o => Assert.Equal(o, o.ToLowerInvariant()));
        }

        [Fact]
        public void DerivationIsDeterministic()
        {
            var tx = Transaction.Create(Network.TestNet);
            tx.Inputs.Add(new OutPoint(new uint256("cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"), 1));
            tx.Outputs.Add(Money.Satoshis(500), new Key().PubKey.WitHash.ScriptPubKey);
            var a = FrostSigningAuthorization.DeriveTxBinding(tx);
            var b = FrostSigningAuthorization.DeriveTxBinding(tx);
            Assert.Equal(a.TxId, b.TxId);
            Assert.Equal(a.Outpoints, b.Outpoints);
        }
    }
}
