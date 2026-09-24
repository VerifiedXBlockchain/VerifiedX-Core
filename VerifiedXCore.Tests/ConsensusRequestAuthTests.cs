using System;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>BB-1: shared authentication primitives for the validator/consensus API.</summary>
    [Collection("GlobalCasterState")]
    public class ConsensusRequestAuthTests
    {
        private static (PrivateKey Key, string Pub, string Address) NewKey()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            return (key, pub, AccountData.GetHumanAddress(pub));
        }

        [Theory]
        [InlineData(1_800_000_000L, 1_800_000_000L, true)]
        [InlineData(1_799_999_910L, 1_800_000_000L, true)]   // 90 s old
        [InlineData(1_799_999_909L, 1_800_000_000L, false)]  // 91 s old
        [InlineData(1_800_000_090L, 1_800_000_000L, true)]   // 90 s ahead
        [InlineData(1_800_000_091L, 1_800_000_000L, false)]
        [InlineData(0L, 1_800_000_000L, false)]               // VX-07: zero used to skip the check
        [InlineData(-5L, 1_800_000_000L, false)]
        [InlineData(long.MinValue, 1_800_000_000L, false)]    // VX-23: Math.Abs overflow
        [InlineData(long.MaxValue, 1_800_000_000L, false)]
        public void IsFresh_WithoutOverflow(long ts, long now, bool expected) =>
            Assert.Equal(expected, ConsensusRequestAuth.IsFresh(ts, now));

        [Fact]
        public void IsFresh_MinValuePlusNow_DoesNotThrow()
        {
            // The exact audit input shape: now - ts wraps to long.MinValue under unchecked arithmetic.
            var now = 1_800_000_000L;
            Assert.False(ConsensusRequestAuth.IsFresh(long.MinValue + now, now));
        }

        [Theory]
        [InlineData(101, 100, true)]
        [InlineData(102, 100, true)]
        [InlineData(103, 100, false)]   // pre-seeding future rounds (VX-08: 9999 at tip 0)
        [InlineData(100, 100, false)]
        [InlineData(9999, 0, false)]
        public void IsHeightInWindow(long h, long tip, bool expected) =>
            Assert.Equal(expected, ConsensusRequestAuth.IsHeightInWindow(h, tip));

        [Fact]
        public void VerifySigner_BindsTheAddress()
        {
            var a = NewKey();
            var b = NewKey();
            var msg = ConsensusMessageFormatter.FormatWinnerVoteV1(10, a.Address, "xWinner", new[] { "z", "a" });
            var sig = SignatureService.CreateSignature(msg, a.Key, a.Pub);

            Assert.True(ConsensusRequestAuth.VerifySigner(a.Address, msg, sig));
            Assert.False(ConsensusRequestAuth.VerifySigner(b.Address, msg, sig));        // someone else's address
            Assert.False(ConsensusRequestAuth.VerifySigner(a.Address, msg + "x", sig));  // altered message
            Assert.False(ConsensusRequestAuth.VerifySigner(a.Address, msg, "forged"));
            Assert.False(ConsensusRequestAuth.VerifySigner(a.Address, msg, null));
        }

        [Fact]
        public void PublicKeyMatchesAddress()
        {
            var a = NewKey();
            Assert.True(ConsensusRequestAuth.PublicKeyMatchesAddress(a.Pub, a.Address));
            Assert.False(ConsensusRequestAuth.PublicKeyMatchesAddress("aa", a.Address));
            Assert.False(ConsensusRequestAuth.PublicKeyMatchesAddress(NewKey().Pub, a.Address));
        }

        [Fact]
        public void TryConsume_IsOneTime()
        {
            var sig = "sig-" + Guid.NewGuid();
            Assert.True(ConsensusRequestAuth.TryConsume(sig, 1));
            Assert.False(ConsensusRequestAuth.TryConsume(sig, 2));
            Globals.Signatures.TryRemove(sig, out _);
        }

        [Fact]
        public void WinnerVoteFormat_IsOrderIndependentForExclusions() =>
            Assert.Equal(
                ConsensusMessageFormatter.FormatWinnerVoteV1(5, "v", "w", new[] { "b", "a" }),
                ConsensusMessageFormatter.FormatWinnerVoteV1(5, "v", "w", new[] { "a", "b" }));
    }
}
