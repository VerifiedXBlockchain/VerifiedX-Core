using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NBitcoin;
using VerifiedXCore.Bitcoin;
using VerifiedXCore.Bitcoin.Attestation;
using VerifiedXCore.Bitcoin.Attestation.Net;
using VerifiedXCore.Bitcoin.FROST;
using VerifiedXCore.Bitcoin.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// The VFX testnet follows the VFX signet, a Bitcoin signet whose blocks only the node farm can sign. The fixtures
    /// are the real chain: every header from genesis to 2,046 (read from the signer's bitcoind) and block 2,046, which
    /// holds a coinbase and two payments.
    /// </summary>
    public class BtcAttest22_VfxSignetTests
    {
        private const long Checkpoint = 2_016;

        private static SortedDictionary<long, BtcHeader> Headers()
        {
            var map = new SortedDictionary<long, BtcHeader>();
            foreach (var line in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "btc-headers-vfx-signet.txt")))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split(',');
                Assert.True(BtcHeader.TryParse(parts[1].Trim(), out var header));
                map[long.Parse(parts[0])] = header!;
            }
            return map;
        }

        private static Block Block2046() =>
            Block.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "vfx-signet-block-2046.txt")).Trim(), Network.VfxSignet);

        private static BtcHeader HeaderOf(Block block)
        {
            Assert.True(BtcHeader.TryParse(Convert.ToHexString(block.Header.ToBytes()).ToLowerInvariant(), out var header));
            return header!;
        }

        [Fact]
        public void TheVfxTestnet_FollowsTheVfxSignet()
        {
            var prior = Globals.IsTestNet;
            try
            {
                Globals.IsTestNet = true;
                Assert.Same(BtcChainRules.Signet, BtcChainRules.Current);
                Assert.Same(Network.VfxSignet, FrostDkgAttestation.ConsensusNetwork);
                Globals.IsTestNet = false;
                Assert.Same(BtcChainRules.Mainnet, BtcChainRules.Current);
                Assert.Same(Network.Main, FrostDkgAttestation.ConsensusNetwork);
            }
            finally
            {
                Globals.IsTestNet = prior;
            }
        }

        [Fact]
        public void TheShippedCheckpoint_IsTheRealChain()
        {
            var all = Headers();
            Assert.Equal(Checkpoint, Globals.BitcoinCheckpointHeightTestnet);
            var expected = string.Concat(Enumerable.Range(0, BtcCheckpoint.HeaderCount).Select(i => all[Checkpoint - (BtcCheckpoint.HeaderCount - 1) + i].Hex));
            Assert.Equal(expected, Globals.BitcoinCheckpointHeadersTestnet);
            Assert.True(BtcCheckpoint.TryParse(Checkpoint, Globals.BitcoinCheckpointHeadersTestnet, out var checkpoint, out var reason), reason);
            Assert.Equal("0000027cac5b1c96e3459b40b572662d7e9d2014336ef73362befaca43a9ba9a", checkpoint!.Tip.Hash);
        }

        [Fact]
        public void EveryHeaderAfterTheCheckpoint_IsAccepted_AtTheSignetLimit()
        {
            var all = Headers();
            var accepted = all.Where(kv => kv.Key <= Checkpoint).ToDictionary(kv => kv.Key, kv => kv.Value);
            foreach (var (height, header) in all.Where(kv => kv.Key > Checkpoint))
            {
                var reason = BtcChainRules.Signet.Validate(header, height, h => accepted.TryGetValue(h, out var x) ? x : null);
                Assert.True(reason == null, $"refused at {height}: {reason}");
                accepted[height] = header;
                Assert.Equal(BtcChainRules.SignetLimitBits, header.Bits);
                Assert.True(BtcChainRules.Signet.CountsTowardBurial(header));
            }
            Assert.True(accepted.Keys.Max() > Checkpoint + 25);
        }

        [Fact]
        public void TheFirstRetarget_StaysAtTheSignetLimit()
        {
            // The first interval ran from the 2020 genesis to 2026: the retarget is capped at four times easier, which
            // the limit caps again. Real block 2,016 carries exactly that.
            var all = Headers();
            Assert.True(BtcChainRules.Signet.ExpectedBits(Checkpoint, all[Checkpoint].Time, h => all.TryGetValue(h, out var x) ? x : null, out var bits, out var reason), reason);
            Assert.Equal(BtcChainRules.SignetLimitBits, bits);
            Assert.Equal(all[Checkpoint].Bits, bits);
        }

        [Fact]
        public void AHeaderClaimingAnotherTarget_IsRefused()
        {
            var all = Headers();
            var height = Checkpoint + 1;
            // Mainnet's limit in place of the signet's: the bits are bytes 72 to 75 of the header.
            var hex = all[height].Hex.Substring(0, 144) + "ffff001d" + all[height].Hex.Substring(152);
            Assert.True(BtcHeader.TryParse(hex, out var forged));
            var reason = BtcChainRules.Signet.Validate(forged!, height, h => all.TryGetValue(h, out var x) ? x : null);
            Assert.Contains("the rules require 1e0377ae", reason);
        }

        [Fact]
        public void ARealSignetBlock_PassesTheBlockCheck_WithOrWithoutWitnessData()
        {
            var block = Block2046();
            var header = Headers()[2_046];
            Assert.Equal(3, block.Transactions.Count);
            Assert.Null(BtcBlockCheck.Verify(block, header, Network.VfxSignet));
            Assert.Null(BtcBlockCheck.Verify(block.WithOptions(TransactionOptions.None), header, Network.VfxSignet));
        }

        [Fact]
        public void ABlockThatIsNotSigned_IsRefusedOnTheSignet()
        {
            // A block that matches its own header in every other way, with a broken signature: what anyone can mine.
            var block = Block2046();
            var commitment = block.Transactions[0].Outputs.Last(o => o.ScriptPubKey.ToHex().StartsWith("6a24aa21a9ed"));
            var bytes = commitment.ScriptPubKey.ToBytes();
            bytes[bytes.Length - 10] ^= 0x01;
            commitment.ScriptPubKey = new Script(bytes);
            block.UpdateMerkleRoot();
            var header = HeaderOf(block);

            Assert.Contains("is not signed for vfx-signet", BtcBlockCheck.Verify(block, header, Network.VfxSignet));
            // The signature is a signet rule only: the same block passes where no challenge applies.
            Assert.Null(BtcBlockCheck.Verify(block, header, Network.TestNet4));
        }

        [Fact]
        public void TheSignetsEndpoints_AreItsOwn()
        {
            Assert.Equal(new[] { "signet.verifiedx.io", "signet-2.verifiedx.io" }, VfxSignet.Hosts);
            var esplora = Assert.Single(BtcEsploraSource.For(Network.VfxSignet));
            Assert.Equal("https://signet.verifiedx.io/api", esplora.Name);
            Assert.Equal("c69e715c", Convert.ToHexString(Network.VfxSignet.MagicBytes).ToLowerInvariant());
            Assert.Equal(38333, Network.VfxSignet.DefaultPort);
            Assert.False(BitcoinTransactionService.IsEsploraCrossCheckSupported(Network.VfxSignet));
        }
    }
}
