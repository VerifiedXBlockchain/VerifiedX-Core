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

        /// <summary>
        /// A signature recovers only in the form the contract counts (ECDSA.tryRecover): 65 bytes, v of 27 or 28, r and s in
        /// range with s in the lower half, written as hex with nothing around it. The 0x prefix and the hex's case are free.
        /// </summary>
        [Fact]
        public void ASignatureRecovers_OnlyInTheFormTheContractCounts()
        {
            var digest = VbtcbTypedData.MintDigest("0xD140DfAB7F08B5ce8E54A8b364E447b49E6EEda6", 400_000, "5425a291-lock", 7_600_123, 8453, Proxy);
            const string sig = "0x06930ed88caed7170477ef941c6259ee39fb389479896a80ed5fccc1f4d2988f0f944b79d399afd432030755a88b4216757a445962a6f2c7a7899b119ebfa3ed1c";
            string? Recover(string? signature) => ValidatorEthKeyService.RecoverSigner(digest, signature, personalMessage: false);
            string With(Action<byte[]> change)
            {
                var bytes = Hex(sig);
                change(bytes);
                return "0x" + Convert.ToHexString(bytes);
            }

            foreach (var counted in new[] { sig, sig[2..], "0X" + sig[2..].ToUpperInvariant() })
                Assert.Equal(Signer, Recover(counted), StringComparer.OrdinalIgnoreCase);

            foreach (var form in VbtcbSignatureForms.NotCounted)
                Assert.Null(Recover(VbtcbSignatureForms.Reencode(sig, (string)form[0])));
            foreach (var v in new byte[] { 0, 1, 29, 30, 35 })
                Assert.Null(Recover(With(b => b[64] = v)));
            Assert.Null(Recover(With(b => Array.Clear(b, 0, 32))));               // r of zero
            Assert.Null(Recover(With(b => Array.Fill(b, (byte)0xff, 0, 32))));    // r above the curve order
            Assert.Null(Recover(With(b => Array.Clear(b, 32, 32))));              // s of zero
            Assert.Null(Recover(sig[..^2]));                                      // 64 bytes
            Assert.Null(Recover(sig + "00"));                                     // 66 bytes
            Assert.Null(Recover(sig[..^1]));                                      // half a byte
            Assert.Null(Recover(""));
            Assert.Null(Recover(null));
        }

        [Fact]
        public void TheValidatorSetDigests_MatchEthers()
        {
            var validators = new[] { "0xD140DfAB7F08B5ce8E54A8b364E447b49E6EEda6", "0x00000000000000000000000000000000000000aa" };
            Assert.Equal(Hex("0x9c2350acd424c69ca56c23acdcc2d0f47c28609da939de02c5ca03d1bf091546"), VbtcbTypedData.AddValidatorsDigest(validators, 7_603_200, 5, 8453, Proxy));
            Assert.Equal(Hex("0x5519f1f7a86c3a8bd9d6fd192a147c4787a4c9b3958f82e034fe74635755e15f"), VbtcbTypedData.RemoveValidatorsDigest(validators, 7_603_200, 5, 8453, Proxy));
            var casters = new[] { "0x00000000000000000000000000000000000000bb" };
            Assert.Equal(Hex("0xeb13d078200ad88fb09073f25064b4cc7ae225e724fde6897cefe01af2269e55"), VbtcbTypedData.ProposeRemovalDigest(validators, casters, 7_603_200, 5, 8453, Proxy));
            Assert.Equal(Hex("0x14a1b144a32d46dc301689c102534addd8b8af62e3780c9c644d9709c1bbde4e"), VbtcbTypedData.ProposeRemovalDigest(validators, Array.Empty<string>(), 7_603_200, 5, 8453, Proxy));
            Assert.Equal(Hex("0x1dc7589a5e142003da5187296c5daae368b8004345ad8e9ee97e06d52d81d290"), VbtcbTypedData.UpdateCastersDigest(validators, casters, 7_603_200, 5, 8453, Proxy));
            Assert.Equal(Hex("0x2efaeb9e79a603a544bdf529de4f2fc5310f8a9136e3743c7441fd7d74cbe8ab"), VbtcbTypedData.UpdateCastersDigest(validators, Array.Empty<string>(), 7_603_200, 5, 8453, Proxy));
        }

        [Fact]
        public void TheV5Digests_MatchEthers()
        {
            Assert.Equal(Hex("0xed134b040f69c6615fa7f104421ea6ad6b00a99dcc136e51db6c6714a471aadf"), VbtcbTypedData.CancelPendingDigest(7, 8453, Proxy));
            Assert.Equal(Hex("0x3ac46ce13dc71113270dc8dd2152fbce97140867e8917ae198d59850e0c70fad"),
                VbtcbTypedData.CancelPendingKindsDigest(VbtcbTypedData.PendingRemoval, 7, 8453, Proxy));
            // A burn named by its transaction hash, and the second burn of a transaction that holds several ("hash:1").
            const string burnTx = "0xb80dd397372defb854c53f02b1b9bf28f7ce79d1225ea82ad3369cd1ff2f5a6b";
            Assert.Equal(Hex("0xf4b76f61875d88d3a2409cbb14422f6f90b713cde9b78a8ca57c7cb2e764588f"),
                VbtcbTypedData.RefundDigest("0x1111111111111111111111111111111111111111", 25_000, burnTx, 8453, Proxy));
            Assert.Equal(Hex("0x2394f227f1fd726c7517059368f7e11870232fb79c997f018802550a0ddcc915"),
                VbtcbTypedData.RefundDigest("0x1111111111111111111111111111111111111111", 25_000, burnTx + ":1", 8453, Proxy));
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

        [Fact]
        public void TheContractAbi_IsV5_AndHoldsWhatCoreAndTheWalletCall()
        {
            var abi = new Nethereum.ABI.ABIDeserialisation.ABIJsonDeserialiser().DeserialiseContract(BaseBridgeService.CONTRACT_ABI);
            var functions = abi.Functions.Select(f => f.Name).ToHashSet();
            foreach (var name in new[] { "mintWithProof", "isLockIdUsed", "requiredMintSignatures", "balanceOf", "burnForVfxExit", "burnForBTCExit",
                         "MESSAGE_VERSION", "getValidators", "getCasters", "requiredCasterSignatures", "updateCasters", "cancelPending", "proposeRemoval",
                         "cancelPendingKinds", "refundBurn", "burnForBTCExitWithMinPayout", "MIN_BTC_EXIT_SATS" })
                Assert.Contains(name, functions);
            Assert.DoesNotContain("upgradeWithValidatorApproval", functions);
            Assert.Equal(4, abi.Functions.Single(f => f.Name == "addValidatorBatch").InputParameters.Length);
        }
    }
}
