using System;
using VerifiedXCore.Bitcoin.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// The vBTCb V4 EIP-712 digests Core builds equal the ones ethers builds (the library the contract's Hardhat tests sign
    /// with), and signatures made by either side recover to the same signer. Reference values: ethers v6
    /// TypedDataEncoder.hash / Wallet.signTypedData, key 0x11..11, domain ("vBTCb", "4", 8453, the mainnet proxy).
    /// </summary>
    public class VbtcbTypedDataTests
    {
        private const string Proxy = "0x09096c6eBf35F0aDAEeC02D06a0dE77E790F132D";
        private const string Signer = "0x19E7E376E7C213B7E7e7e46cc70A5dD086DAff2A";
        private static readonly byte[] Key = Convert.FromHexString(new string('1', 64));

        private static byte[] Hex(string h) => Convert.FromHexString(h[2..]);

        [Fact]
        public void TheMintDigest_MatchesEthers_AndSignaturesRecoverBothWays()
        {
            var digest = VbtcbTypedData.MintDigest("0xD140DfAB7F08B5ce8E54A8b364E447b49E6EEda6", 400_000, "5425a291-lock", 7_600_123, 8453, Proxy);
            Assert.Equal(Hex("0x395dfdea28573a3817d3f5cb1a9ea37ba80e010c7693f634ba769da8ab96b4a0"), digest);

            const string ethersSig = "0x06930ed88caed7170477ef941c6259ee39fb389479896a80ed5fccc1f4d2988f0f944b79d399afd432030755a88b4216757a445962a6f2c7a7899b119ebfa3ed1c";
            Assert.Equal(Signer, ValidatorEthKeyService.RecoverSigner(digest, ethersSig, personalMessage: false), StringComparer.OrdinalIgnoreCase);
            Assert.Equal(Signer, ValidatorEthKeyService.RecoverSigner(digest, ValidatorEthKeyService.SignDigestHex(digest, Key), personalMessage: false), StringComparer.OrdinalIgnoreCase);
        }

        [Fact]
        public void TheDigest_ChangesWithEveryFieldOfTheDomainAndTheMint()
        {
            var baseline = VbtcbTypedData.MintDigest("0xD140DfAB7F08B5ce8E54A8b364E447b49E6EEda6", 400_000, "l", 1, 8453, Proxy);
            Assert.NotEqual(baseline, VbtcbTypedData.MintDigest("0xD140DfAB7F08B5ce8E54A8b364E447b49E6EEda7", 400_000, "l", 1, 8453, Proxy));
            Assert.NotEqual(baseline, VbtcbTypedData.MintDigest("0xD140DfAB7F08B5ce8E54A8b364E447b49E6EEda6", 400_001, "l", 1, 8453, Proxy));
            Assert.NotEqual(baseline, VbtcbTypedData.MintDigest("0xD140DfAB7F08B5ce8E54A8b364E447b49E6EEda6", 400_000, "L", 1, 8453, Proxy));
            Assert.NotEqual(baseline, VbtcbTypedData.MintDigest("0xD140DfAB7F08B5ce8E54A8b364E447b49E6EEda6", 400_000, "l", 2, 8453, Proxy));
            Assert.NotEqual(baseline, VbtcbTypedData.MintDigest("0xD140DfAB7F08B5ce8E54A8b364E447b49E6EEda6", 400_000, "l", 1, 84532, Proxy));
            Assert.NotEqual(baseline, VbtcbTypedData.MintDigest("0xD140DfAB7F08B5ce8E54A8b364E447b49E6EEda6", 400_000, "l", 1, 8453, "0x1154DDB476A38c01A389E1324121E15Df6703D19"));
        }
    }
}
