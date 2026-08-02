using System.Collections.Generic;
using Xunit;
using ReserveBlockCore.Bitcoin.ElectrumX.Results;
using ReserveBlockCore.Bitcoin.Services;

namespace VerfiedXCore.Tests
{
    /// <summary>
    /// Coverage for the resilient Taproot UTXO fetch: an empty Electrum result must be CORROBORATED
    /// against Esplora before being trusted, and a server that returns empty-while-funded must be
    /// flagged so it rotates out of selection. Exercises the pure decision/mapping logic
    /// (DecideUtxoSource, MapEsploraUtxo) without touching the network.
    /// </summary>
    public class TaprootUTXOCorroborationTests
    {
        private static BlockchainScripthashListunspentResult Utxo(string txHash, uint pos = 0, ulong value = 1000, int height = 800000)
        {
            return new BlockchainScripthashListunspentResult { TxHash = txHash, TxPos = pos, Value = value, Height = height };
        }

        [Fact]
        public void DecideUtxoSource_ElectrumHasUtxos_UsesElectrumAndDoesNotPenalize()
        {
            var electrum = new List<BlockchainScripthashListunspentResult> { Utxo("aa") };
            var esplora = new List<BlockchainScripthashListunspentResult> { Utxo("bb") };

            var (utxos, penalize) = BitcoinTransactionService.DecideUtxoSource(electrum, esplora);

            Assert.Same(electrum, utxos);
            Assert.False(penalize);
        }

        [Fact]
        public void DecideUtxoSource_ElectrumEmptyButEsploraFunded_UsesEsploraAndPenalizes()
        {
            // This is the incident: Electrum returned empty for a funded address.
            var electrum = new List<BlockchainScripthashListunspentResult>();
            var esplora = new List<BlockchainScripthashListunspentResult> { Utxo("funded") };

            var (utxos, penalize) = BitcoinTransactionService.DecideUtxoSource(electrum, esplora);

            Assert.Same(esplora, utxos);
            Assert.True(penalize, "Empty-while-funded must flag the Electrum servers for FailCount bump");
        }

        [Fact]
        public void DecideUtxoSource_BothEmpty_ReturnsEmptyAndDoesNotPenalize()
        {
            // Genuinely empty address: only conclude "no UTXOs" after BOTH sources agree.
            var (utxos, penalize) = BitcoinTransactionService.DecideUtxoSource(
                new List<BlockchainScripthashListunspentResult>(),
                new List<BlockchainScripthashListunspentResult>());

            Assert.Empty(utxos);
            Assert.False(penalize);
        }

        [Fact]
        public void DecideUtxoSource_NullInputs_TreatedAsEmpty()
        {
            var (utxos, penalize) = BitcoinTransactionService.DecideUtxoSource(null, null);

            Assert.Empty(utxos);
            Assert.False(penalize);
        }

        [Fact]
        public void MapEsploraUtxo_MapsAllFieldsTheBuilderNeeds()
        {
            // The builder selects on Value and re-fetches the raw tx by TxHash at TxPos to build the Coin.
            var result = BitcoinTransactionService.MapEsploraUtxo(
                txid: "9f1c0e2a",
                vout: 2,
                value: 177692,
                confirmed: true,
                blockHeight: 845123);

            Assert.Equal("9f1c0e2a", result.TxHash);
            Assert.Equal((uint)2, result.TxPos);
            Assert.Equal((ulong)177692, result.Value);
            Assert.Equal(845123, result.Height);
        }

        [Fact]
        public void MapEsploraUtxo_UnconfirmedMapsToZeroHeight()
        {
            // Parity with Electrum listunspent, where mempool/unconfirmed entries report height 0.
            var result = BitcoinTransactionService.MapEsploraUtxo(
                txid: "deadbeef",
                vout: 0,
                value: 5000,
                confirmed: false,
                blockHeight: 0);

            Assert.Equal(0, result.Height);
            Assert.Equal((ulong)5000, result.Value);
        }
    }
}
