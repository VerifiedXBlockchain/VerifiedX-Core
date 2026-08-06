using System.Collections.Generic;
using Xunit;
using VerifiedXCore.Bitcoin.ElectrumX.Results;
using VerifiedXCore.Bitcoin.Services;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Coverage for the resilient Taproot UTXO fetch: ElectrumX is the sole system of record, an
    /// empty Electrum answer must be corroborated (Electrum quorum, or a single empty answer plus
    /// the ADVISORY Esplora cross-check) before being trusted, and a server that answers
    /// empty-while-funded must be flagged so it rotates out of selection. Exercises the pure
    /// decision/mapping logic (DecideEmptyVerdict, MapEsploraUtxo) and the UtxoLookupResult
    /// semantics without touching the network.
    /// </summary>
    public class TaprootUTXOCorroborationTests
    {
        private static BlockchainScripthashListunspentResult Utxo(string txHash, uint pos = 0, ulong value = 1000, int height = 800000)
        {
            return new BlockchainScripthashListunspentResult { TxHash = txHash, TxPos = pos, Value = value, Height = height };
        }

        [Fact]
        public void DecideEmptyVerdict_CrossCheckShowsFunds_PenalizesAndRetries()
        {
            // This is the incident: an Electrum server answered empty for a funded address. The
            // lagging server must be penalized and the caller must retry — Esplora data is advisory
            // only and is never used as a spend source.
            var verdict = BitcoinTransactionService.DecideEmptyVerdict(
                electrumEmptyAnswers: 1, esploraAnswered: true, esploraHasUtxos: true);

            Assert.Equal(BitcoinTransactionService.EmptyCorroborationVerdict.PenalizeAndRetry, verdict);
        }

        [Fact]
        public void DecideEmptyVerdict_ElectrumQuorumEmpty_ConfirmedWithoutEsplora()
        {
            // Two Electrum servers agreeing on empty is authoritative even when the advisory
            // cross-check is unreachable — we do not depend on public APIs to conclude.
            var verdict = BitcoinTransactionService.DecideEmptyVerdict(
                electrumEmptyAnswers: 2, esploraAnswered: false, esploraHasUtxos: false);

            Assert.Equal(BitcoinTransactionService.EmptyCorroborationVerdict.ConfirmedEmpty, verdict);
        }

        [Fact]
        public void DecideEmptyVerdict_SingleEmptyWithEsploraAgreement_Confirmed()
        {
            var verdict = BitcoinTransactionService.DecideEmptyVerdict(
                electrumEmptyAnswers: 1, esploraAnswered: true, esploraHasUtxos: false);

            Assert.Equal(BitcoinTransactionService.EmptyCorroborationVerdict.ConfirmedEmpty, verdict);
        }

        [Fact]
        public void DecideEmptyVerdict_SingleEmptyUncorroborated_Inconclusive()
        {
            // One server said empty, nothing else answered: NOT trustworthy — must surface as a
            // transient failure, never as "the vault has no coins".
            var verdict = BitcoinTransactionService.DecideEmptyVerdict(
                electrumEmptyAnswers: 1, esploraAnswered: false, esploraHasUtxos: false);

            Assert.Equal(BitcoinTransactionService.EmptyCorroborationVerdict.Inconclusive, verdict);
        }

        [Fact]
        public void DecideEmptyVerdict_NoAnswersAtAll_Inconclusive()
        {
            var verdict = BitcoinTransactionService.DecideEmptyVerdict(
                electrumEmptyAnswers: 0, esploraAnswered: false, esploraHasUtxos: false);

            Assert.Equal(BitcoinTransactionService.EmptyCorroborationVerdict.Inconclusive, verdict);
        }

        [Fact]
        public void DecideEmptyVerdict_QuorumEmptyButCrossCheckFunded_StillPenalizes()
        {
            // A funded cross-check outranks even an Electrum quorum: both quorum members could be
            // lagging (e.g. shared upstream). Penalize + retry is always the safe answer.
            var verdict = BitcoinTransactionService.DecideEmptyVerdict(
                electrumEmptyAnswers: 3, esploraAnswered: true, esploraHasUtxos: true);

            Assert.Equal(BitcoinTransactionService.EmptyCorroborationVerdict.PenalizeAndRetry, verdict);
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
                blockHeight: 845123);

            Assert.Equal(0, result.Height);
        }

        [Fact]
        public void UtxoLookupResult_Found_IsSuccessWithUtxos()
        {
            var utxos = new List<BlockchainScripthashListunspentResult> { Utxo("aa") };

            var result = UtxoLookupResult.Found(utxos, UtxoSource.Electrum, serversTried: 1);

            Assert.True(result.Success);
            Assert.True(result.HasUtxos);
            Assert.False(result.IsConfirmedEmpty);
            Assert.Equal(UtxoSource.Electrum, result.Source);
            Assert.Same(utxos, result.Utxos);
        }

        [Fact]
        public void UtxoLookupResult_ConfirmedEmpty_IsSuccessWithNoUtxos()
        {
            // Both Electrum and Esplora answered empty: the address genuinely has no coins.
            var result = UtxoLookupResult.ConfirmedEmpty(serversTried: 3);

            Assert.True(result.Success);
            Assert.False(result.HasUtxos);
            Assert.True(result.IsConfirmedEmpty);
        }

        [Fact]
        public void UtxoLookupResult_Failed_IsNeitherFoundNorConfirmedEmpty()
        {
            // All sources unreachable: the lookup is INCONCLUSIVE. It must not read as an empty
            // address (that would surface as "No UTXOs found" and poison withdrawals).
            var result = UtxoLookupResult.Failed("no Bitcoin data source reachable", serversTried: 3);

            Assert.False(result.Success);
            Assert.False(result.HasUtxos);
            Assert.False(result.IsConfirmedEmpty);
            Assert.Equal(UtxoSource.None, result.Source);
            Assert.NotEmpty(result.Error);
        }
    }
}
