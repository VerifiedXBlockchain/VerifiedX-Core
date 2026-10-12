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

        [Fact]
        public void ThisReleaseIsMajorNine_AndShipsWithItsForkHeightsUnset()
        {
            Assert.Equal(9, Globals.MajorVer);
            Assert.Equal(8, Globals.PriorMajorVer);
            Assert.Equal(long.MaxValue, _priorRecordHeight);
            Assert.Equal(long.MaxValue, Globals.BitcoinAttestationObserveHeight);
            Assert.Equal(long.MaxValue, Globals.BitcoinAttestationHeight);
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

        /// <summary>With the fork height unset (this build as shipped), the previous release is always accepted.</summary>
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
