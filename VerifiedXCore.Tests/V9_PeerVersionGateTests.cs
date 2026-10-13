using System;
using VerifiedXCore;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// 9.0.0: a node on a new major version used to refuse every peer on the previous one at once (P2P handshakes, the
    /// validator liveness sweep, proof generation and the winner check). A node upgraded one at a time would cut itself
    /// off from a network that had not yet upgraded. The required major version now depends on the height: the previous
    /// release until this release's first fork height (Globals.BitcoinAttestationRecordHeight), then this one.
    /// </summary>
    [Collection("DbContextSequential")]
    public class V9_PeerVersionGateTests : IDisposable
    {
        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly long _priorRecordHeight = Globals.BitcoinAttestationRecordHeight;

        public void Dispose()
        {
            Globals.LastBlock = _priorLastBlock;
            Globals.BitcoinAttestationRecordHeight = _priorRecordHeight;
        }

        /// <summary>
        /// The heights as set on 13 October 2026 (tips: mainnet 7,496,705, testnet 1,124,010). Observation needs no
        /// coordination and starts at once; recording is the one fork point, with the fund-loss rules; enforcement
        /// follows after the migration window. Both networks' checkpoints are twelve real headers that link, meet
        /// their own proof-of-work and end at the first block of a difficulty interval.
        /// </summary>
        [Fact]
        public void ThisReleaseIsMajorNine_AndItsHeightsAndCheckpointsAreSet()
        {
            Assert.Equal(9, Globals.MajorVer);
            Assert.Equal(8, Globals.PriorMajorVer);

            // Mainnet (the defaults a node starts with).
            Assert.Equal(7_517_000, Globals.FundLossAuditHeight);
            Assert.Equal(Globals.FundLossAuditHeight, _priorRecordHeight);
            Assert.True(Globals.BitcoinAttestationObserveHeight < _priorRecordHeight);
            Assert.True(_priorRecordHeight < Globals.BitcoinAttestationHeight);
            Assert.Equal(Globals.FundLossAuditHeight, Globals.VaultBurnRuleHeight);
            // Testnet (applied at startup).
            Assert.Equal(1_143_500, Globals.FundLossAuditTestnetHeight);
            Assert.Equal(Globals.FundLossAuditTestnetHeight, Globals.BitcoinAttestationRecordTestnetHeight);
            Assert.True(Globals.BitcoinAttestationObserveTestnetHeight < Globals.BitcoinAttestationRecordTestnetHeight);
            Assert.True(Globals.BitcoinAttestationRecordTestnetHeight < Globals.BitcoinAttestationTestnetHeight);

            Assert.True(VerifiedXCore.Bitcoin.Attestation.BtcCheckpoint.TryParse(Globals.BitcoinCheckpointHeightMainnet, Globals.BitcoinCheckpointHeadersMainnet, out var mainnet, out var why), why);
            Assert.Equal(969_696, mainnet!.Height);
            Assert.Equal("000000000000000000001e39df127cbab82a824ac43cfcdbf62e59f6a08b9f0b", mainnet.Tip.Hash);
            Assert.True(VerifiedXCore.Bitcoin.Attestation.BtcCheckpoint.TryParse(Globals.BitcoinCheckpointHeightTestnet, Globals.BitcoinCheckpointHeadersTestnet, out var testnet, out why), why);
            Assert.Equal(155_232, testnet!.Height);
            Assert.Equal("0000000000000002314dc5b90175408b22d2c80dfe99979629b40e572d40345b", testnet.Tip.Hash);
        }

        [Fact]
        public void ThePreviousRelease_IsAcceptedUntilTheForkHeight_AndRefusedFromIt()
        {
            Globals.BitcoinAttestationRecordHeight = 1_000;

            foreach (var height in new long[] { -1, 0, 999 })   // -1: the tip before it is loaded at startup
            {
                Globals.LastBlock = new Block { Height = height };
                Assert.Equal(8, Globals.MinimumPeerMajorVersion(height));
                Assert.True(WalletVersionUtility.Verify("8.2.1.1742-beta"));
                Assert.True(WalletVersionUtility.Verify("9.0.0.1745-beta"));
                Assert.True(WalletVersionUtility.Verify("10.0.0.1"));
                Assert.False(WalletVersionUtility.Verify("7.9.9.1"));
                Assert.True(ProofUtility.IsMajorVersionCurrent("8.2.1.1742-beta"));
                Assert.True(ProofUtility.IsMajorVersionCurrent("9.0.0.1745-beta"));
                Assert.False(ProofUtility.IsMajorVersionCurrent("7.0.0.1"));
            }

            foreach (var height in new long[] { 1_000, 1_001, 5_000_000 })
            {
                Globals.LastBlock = new Block { Height = height };
                Assert.Equal(9, Globals.MinimumPeerMajorVersion(height));
                Assert.False(WalletVersionUtility.Verify("8.2.1.1742-beta"));
                Assert.True(WalletVersionUtility.Verify("9.0.0.1745-beta"));
                Assert.False(ProofUtility.IsMajorVersionCurrent("8.2.1.1742-beta"));
                Assert.True(ProofUtility.IsMajorVersionCurrent("9.0.0.1745-beta"));
            }

            // Malformed or missing versions are refused at any height.
            Globals.LastBlock = new Block { Height = 10 };
            Assert.False(WalletVersionUtility.Verify(""));
            Assert.False(WalletVersionUtility.Verify(null!));
            Assert.False(WalletVersionUtility.Verify("abc"));
            Assert.False(ProofUtility.IsMajorVersionCurrent(null));
            Assert.False(ProofUtility.IsMajorVersionCurrent("x.y"));
        }

        /// <summary>With the fork height unset, the previous release is always accepted.</summary>
        [Fact]
        public void WithTheForkHeightUnset_ThePreviousReleaseIsAlwaysAccepted()
        {
            Globals.BitcoinAttestationRecordHeight = long.MaxValue;
            Globals.LastBlock = new Block { Height = 50_000_000 };
            Assert.True(WalletVersionUtility.Verify("8.2.1.1742-beta"));
            Assert.True(ProofUtility.IsMajorVersionCurrent("8.2.1.1742-beta"));
            Assert.False(WalletVersionUtility.Verify("7.5.0.1"));
        }
    }
}
