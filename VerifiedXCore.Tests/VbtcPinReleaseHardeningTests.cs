using System.Collections.Generic;
using VerifiedXCore.Bitcoin.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// FIND-028 pin-release hardening (Aug 2026 testnet4 deadlock): a contract pin's release
    /// depended on a SINGLE Electrum server answer where every failure mode collapsed to
    /// "unconfirmed", so validators with a wedged Electrum path held pins forever after the
    /// pinned tx confirmed — and the coordinator, misreading the same broken answer, rebuilt a
    /// non-conflicting tx those validators then 409'd. These tests cover the two pure decision
    /// functions that fix both sides, plus the testnet4 Esplora routing rule.
    /// </summary>
    public class VbtcPinReleaseHardeningTests
    {
        // ---------- CombineConfirmationAnswers: tri-state combining rule ----------

        [Fact]
        public void Combine_NoAnswers_IsUnknown()
        {
            Assert.Null(BitcoinTransactionService.CombineConfirmationAnswers(new List<int>()));
            Assert.Null(BitcoinTransactionService.CombineConfirmationAnswers(null!));
        }

        [Fact]
        public void Combine_AllFailed_IsUnknown()
        {
            // -1 = server unreachable / errored: NOT evidence the tx is unconfirmed.
            Assert.Null(BitcoinTransactionService.CombineConfirmationAnswers(new List<int> { -1, -1 }));
        }

        [Fact]
        public void Combine_FailedPlusMempool_IsUnconfirmed()
        {
            // One server definitively answered "known, 0 conf" — that IS an answer.
            Assert.Equal(0, BitcoinTransactionService.CombineConfirmationAnswers(new List<int> { -1, 0 }));
        }

        [Fact]
        public void Combine_AnyPositiveWins()
        {
            // A lagging server's 0 must never outvote a healthy server that saw the block.
            Assert.Equal(3, BitcoinTransactionService.CombineConfirmationAnswers(new List<int> { 0, 3 }));
            Assert.Equal(1, BitcoinTransactionService.CombineConfirmationAnswers(new List<int> { -1, -1, 1 }));
            Assert.Equal(7, BitcoinTransactionService.CombineConfirmationAnswers(new List<int> { 7 }));
        }

        // ---------- DecidePinnedTxDisposition: coordinator stale-pin escape ----------

        [Fact]
        public void Disposition_UtxoLookupFailed_Reuses()
        {
            Assert.Equal(VBTCService.PinnedReuseDecision.ReusePinned,
                VBTCService.DecidePinnedTxDisposition(utxoLookupSucceeded: false, anyPinnedOutpointUnspent: false, pinnedTxConfirmations: 0));
        }

        [Fact]
        public void Disposition_PinnedOutpointStillUnspent_Reuses()
        {
            Assert.Equal(VBTCService.PinnedReuseDecision.ReusePinned,
                VBTCService.DecidePinnedTxDisposition(utxoLookupSucceeded: true, anyPinnedOutpointUnspent: true, pinnedTxConfirmations: 0));
        }

        [Fact]
        public void Disposition_OutpointsGone_TxConfirmed_AlreadyPaid()
        {
            Assert.Equal(VBTCService.PinnedReuseDecision.AlreadyPaid,
                VBTCService.DecidePinnedTxDisposition(utxoLookupSucceeded: true, anyPinnedOutpointUnspent: false, pinnedTxConfirmations: 2));
        }

        [Fact]
        public void Disposition_OutpointsGone_TxKnownUnconfirmed_Rebuilds()
        {
            // Definitive conflict: our tx's inputs were spent by something else.
            Assert.Equal(VBTCService.PinnedReuseDecision.RebuildFresh,
                VBTCService.DecidePinnedTxDisposition(utxoLookupSucceeded: true, anyPinnedOutpointUnspent: false, pinnedTxConfirmations: 0));
        }

        [Fact]
        public void Disposition_OutpointsGone_ConfirmationUnknown_Reuses()
        {
            // THE incident case: outpoints gone because our pinned tx CONFIRMED, but no Electrum
            // server could answer. The old code rebuilt a non-conflicting tx here (deadlock).
            Assert.Equal(VBTCService.PinnedReuseDecision.ReusePinned,
                VBTCService.DecidePinnedTxDisposition(utxoLookupSucceeded: true, anyPinnedOutpointUnspent: false, pinnedTxConfirmations: null));
        }

        // ---------- Esplora cross-check routing ----------

        [Fact]
        public void EsploraCrossCheck_NotSupportedOnTestnet4()
        {
            // Both Esplora integrations route non-mainnet to TESTNET3 URLs, so on testnet4 their
            // answers are wrong-chain poison for DecideEmptyVerdict — the cross-check must be
            // skipped entirely (Electrum quorum only).
            Assert.False(BitcoinTransactionService.IsEsploraCrossCheckSupported(NBitcoin.Network.TestNet4));
        }

        [Fact]
        public void EsploraCrossCheck_SupportedOnMainnetAndTestnet3()
        {
            Assert.True(BitcoinTransactionService.IsEsploraCrossCheckSupported(NBitcoin.Network.Main));
            Assert.True(BitcoinTransactionService.IsEsploraCrossCheckSupported(NBitcoin.Network.TestNet));
        }

        // ---------- DecideEmptyVerdict: no Esplora answer on testnet4 ----------

        [Fact]
        public void EmptyVerdict_SingleEmptyAnswer_NoEsplora_IsInconclusive()
        {
            // With the testnet4 skip, a lone lagging Electrum server answering "empty" can no
            // longer be upgraded to ConfirmedEmpty by a wrong-chain cross-check.
            Assert.Equal(BitcoinTransactionService.EmptyCorroborationVerdict.Inconclusive,
                BitcoinTransactionService.DecideEmptyVerdict(electrumEmptyAnswers: 1, esploraAnswered: false, esploraHasUtxos: false));
        }

        [Fact]
        public void EmptyVerdict_ElectrumQuorum_StillConfirmsWithoutEsplora()
        {
            Assert.Equal(BitcoinTransactionService.EmptyCorroborationVerdict.ConfirmedEmpty,
                BitcoinTransactionService.DecideEmptyVerdict(electrumEmptyAnswers: 2, esploraAnswered: false, esploraHasUtxos: false));
        }
    }
}
