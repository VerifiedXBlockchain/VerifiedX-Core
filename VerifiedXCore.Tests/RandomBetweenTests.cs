using System.Linq;
using System.Numerics;
using VerifiedXCore.EllipticCurve;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// The random scalars behind every signing nonce and every new private key are drawn from the whole range, top bit
    /// included: the random bytes were read as a signed number, so every draw with its top bit set was thrown away.
    /// </summary>
    public class RandomBetweenTests
    {
        private static readonly BigInteger Order = Curves.secp256k1.N;
        private static readonly BigInteger TopBit = BigInteger.One << 255;

        [Fact]
        public void ScalarsUpToTheCurveOrder_HaveTheirTopBitSetAboutHalfTheTime()
        {
            // About half of [1, N-1] is at or above 2^255: 64 draws all below it happen with probability about 2^-64.
            var draws = Enumerable.Range(0, 64).Select(_ => Integer.randomBetween(BigInteger.One, Order - 1)).ToList();

            Assert.All(draws, d => Assert.InRange(d, BigInteger.One, Order - 1));
            Assert.Contains(draws, d => d >= TopBit);
            Assert.Contains(draws, d => d < TopBit);
        }

        [Fact]
        public void NewPrivateKeys_CanHaveTheirTopBitSet()
        {
            var secrets = Enumerable.Range(0, 64).Select(_ => new PrivateKey("secp256k1").secret).ToList();

            Assert.All(secrets, s => Assert.InRange(s, BigInteger.One, Order - 1));
            Assert.Contains(secrets, s => s >= TopBit);
        }

        [Fact]
        public void SmallRanges_StayInRange_AndReachBothEnds()
        {
            var bits = Enumerable.Range(0, 200).Select(_ => Integer.randomBetween(0, 1)).ToList();
            Assert.Contains(bits, b => b == 0);
            Assert.Contains(bits, b => b == 1);

            Assert.All(Enumerable.Range(0, 200).Select(_ => Integer.randomBetween(5, 260)), v => Assert.InRange(v, new BigInteger(5), new BigInteger(260)));
            Assert.Equal(new BigInteger(7), Integer.randomBetween(7, 7));
        }

        [Fact]
        public void SignaturesWithFullRangeNonces_StillVerify()
        {
            for (var i = 0; i < 16; i++)
            {
                var key = new PrivateKey("secp256k1");
                var signature = Ecdsa.sign("message " + i, key);
                Assert.True(Ecdsa.verify("message " + i, signature, key.publicKey()));
                Assert.False(Ecdsa.verify("another message", signature, key.publicKey()));
            }
        }
    }
}
