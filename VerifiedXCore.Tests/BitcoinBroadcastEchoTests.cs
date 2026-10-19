using System;
using System.Threading.Tasks;
using NBitcoin;
using VerifiedXCore.Bitcoin.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A broadcast reports the signed transaction's own txid. A server that answers with another txid has not broadcast
    /// this transaction: the broadcast fails, naming both, so no withdrawal or exit is recorded as paid by a txid the
    /// server chose.
    /// </summary>
    public class BitcoinBroadcastEchoTests
    {
        private static Transaction Signed()
        {
            var tx = Network.TestNet.CreateTransaction();
            tx.Inputs.Add(new OutPoint(uint256.One, 0));
            tx.Outputs.Add(Money.Satoshis(50_000), new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.TestNet));
            return tx;
        }

        private static async Task<(bool Success, string TxHash, string Error)> BroadcastAnswered(Transaction tx, string? echoed, string? error = null)
        {
            try
            {
                BitcoinTransactionService.BroadcastHexForTests = _ => Task.FromResult((echoed, error));
                return await BitcoinTransactionService.BroadcastTransaction(tx);
            }
            finally { BitcoinTransactionService.BroadcastHexForTests = null; }
        }

        [Fact]
        public async Task AServerThatEchoesAForeignTxid_IsAFailedBroadcast()
        {
            var tx = Signed();
            var foreign = new string('f', 64);
            var (ok, txid, error) = await BroadcastAnswered(tx, foreign);
            Assert.False(ok);
            Assert.Equal(string.Empty, txid);
            Assert.Contains(foreign, error);
            Assert.Contains(tx.GetHash().ToString(), error);
        }

        [Fact]
        public async Task AServerThatEchoesTheSignedTxid_IsASuccess_WithTheSignedTxid()
        {
            var tx = Signed();
            var (ok, txid, _) = await BroadcastAnswered(tx, tx.GetHash().ToString().ToUpperInvariant());
            Assert.True(ok);
            Assert.Equal(tx.GetHash().ToString(), txid);
        }

        [Fact]
        public async Task AServerThatRejects_IsAFailedBroadcast()
        {
            var (ok, txid, error) = await BroadcastAnswered(Signed(), null, "bad-txns-inputs-missingorspent");
            Assert.False(ok);
            Assert.Equal(string.Empty, txid);
            Assert.Contains("bad-txns-inputs-missingorspent", error);
        }
    }
}
