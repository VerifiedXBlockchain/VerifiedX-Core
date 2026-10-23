using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using VerifiedXCore.Bitcoin.Attestation;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Bitcoin facts in consensus, part 1: the header rules (docs/PLAN-vBTC-Bitcoin-Attestation.md, sections 3.1 and 8.1).
    /// The fixtures are real Bitcoin headers, 2,058 per network, each spanning a twelve-header checkpoint, a whole
    /// difficulty interval and the retarget that ends it: mainnet 967,669 to 969,726 and testnet4 153,205 to 155,262.
    /// They were rebuilt from block fields served by a public explorer and each was checked to hash to its block id.
    /// </summary>
    public class BtcAttest01_HeaderChainTests
    {
        private const long MainnetCheckpoint = 967_680;
        private const long Testnet4Checkpoint = 153_216;
        private const long SignetCheckpoint = 2_016;

        private static SortedDictionary<long, BtcHeader> Load(string file)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", file);
            var map = new SortedDictionary<long, BtcHeader>();
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split(',');
                Assert.True(BtcHeader.TryParse(parts[1].Trim(), out var header));
                map[long.Parse(parts[0])] = header!;
            }
            return map;
        }

        private static BtcCheckpoint Checkpoint(SortedDictionary<long, BtcHeader> all, long height)
        {
            var hex = string.Join("\n", Enumerable.Range(0, BtcCheckpoint.HeaderCount).Select(i => all[height - (BtcCheckpoint.HeaderCount - 1) + i].Hex));
            Assert.True(BtcCheckpoint.TryParse(height, hex, out var checkpoint, out var reason), reason);
            return checkpoint!;
        }

        /// <summary>Accepts every fixture header after the checkpoint in order, as the chain would, and returns the accepted set.</summary>
        private static Dictionary<long, BtcHeader> AcceptAll(BtcChainRules rules, SortedDictionary<long, BtcHeader> all, BtcCheckpoint checkpoint, out string? firstRefusal, out long refusedAt)
        {
            var accepted = new Dictionary<long, BtcHeader>();
            for (var h = checkpoint.Height - (BtcCheckpoint.HeaderCount - 1); h <= checkpoint.Height; h++)
                accepted[h] = checkpoint.At(h)!;
            firstRefusal = null;
            refusedAt = 0;
            foreach (var (height, header) in all.Where(kv => kv.Key > checkpoint.Height))
            {
                var reason = rules.Validate(header, height, h => accepted.TryGetValue(h, out var x) ? x : null);
                if (reason != null) { firstRefusal = reason; refusedAt = height; break; }
                accepted[height] = header;
            }
            return accepted;
        }

        [Fact]
        public void Mainnet_AWholeIntervalAndItsRetarget_AreAccepted()
        {
            var all = Load("btc-headers-mainnet.txt");
            var checkpoint = Checkpoint(all, MainnetCheckpoint);
            var accepted = AcceptAll(BtcChainRules.Mainnet, all, checkpoint, out var refusal, out var at);
            Assert.True(refusal == null, $"refused at {at}: {refusal}");
            Assert.Equal(all.Count, accepted.Count);

            // The interval's blocks all carry the checkpoint's target; the block that opens the next interval carries a
            // new one, and it is the one the rule computes from the interval's first and last block times.
            var boundary = MainnetCheckpoint + BtcChainRules.Interval;
            Assert.All(all.Where(kv => kv.Key > MainnetCheckpoint && kv.Key < boundary), kv => Assert.Equal(all[MainnetCheckpoint].Bits, kv.Value.Bits));
            Assert.True(BtcChainRules.Mainnet.ExpectedBits(boundary, all[boundary].Time, h => all.TryGetValue(h, out var x) ? x : null, out var bits, out _));
            Assert.Equal(all[boundary].Bits, bits);
            Assert.NotEqual(all[MainnetCheckpoint].Bits, bits);
        }

        [Fact]
        public void Testnet4_AWholeIntervalWithMinimumDifficultyBlocks_AndItsBip94Retarget_AreAccepted()
        {
            var all = Load("btc-headers-testnet4.txt");
            var checkpoint = Checkpoint(all, Testnet4Checkpoint);
            var accepted = AcceptAll(BtcChainRules.Testnet4, all, checkpoint, out var refusal, out var at);
            Assert.True(refusal == null, $"refused at {at}: {refusal}");
            Assert.Equal(all.Count, accepted.Count);

            // The fixture exercises the testnet rule: blocks at the easiest target sit among blocks at the real one.
            var inInterval = all.Where(kv => kv.Key > Testnet4Checkpoint && kv.Key < Testnet4Checkpoint + BtcChainRules.Interval).ToList();
            Assert.Contains(inInterval, kv => kv.Value.Bits == BtcChainRules.PowLimitBits);
            Assert.Contains(inInterval, kv => kv.Value.Bits != BtcChainRules.PowLimitBits);

            // Mainnet's rules refuse the same chain at its first easy block: the rules are not interchangeable.
            AcceptAll(BtcChainRules.Mainnet, all, checkpoint, out var mainnetRefusal, out var mainnetAt);
            Assert.NotNull(mainnetRefusal);
            Assert.Equal(inInterval.First(kv => kv.Value.Bits != all[kv.Key - 1].Bits).Key, mainnetAt);
        }

        [Fact]
        public void AForgedHeader_IsRefused_WhateverPartOfItIsWrong()
        {
            var all = Load("btc-headers-mainnet.txt");
            var checkpoint = Checkpoint(all, MainnetCheckpoint);
            Func<long, BtcHeader?> at = h => all.TryGetValue(h, out var x) ? x : null;
            var height = MainnetCheckpoint + 5;
            var real = all[height];
            Assert.Null(BtcChainRules.Mainnet.Validate(real, height, at));

            BtcHeader Mutate(Action<byte[]> change)
            {
                var raw = Convert.FromHexString(real.Hex);
                change(raw);
                Assert.True(BtcHeader.TryParse(Convert.ToHexString(raw), out var h));
                return h!;
            }

            // A different nonce: the hash no longer meets the target.
            Assert.Contains("proof-of-work", BtcChainRules.Mainnet.Validate(Mutate(r => r[76] ^= 0x01), height, at));
            // An easier target written into the header: not the target the rules require (checked before the work).
            Assert.Contains("the rules require", BtcChainRules.Mainnet.Validate(Mutate(r => { r[72] = 0xff; r[73] = 0xff; r[74] = 0x00; r[75] = 0x1d; }), height, at));
            // A different parent.
            Assert.Contains("does not extend", BtcChainRules.Mainnet.Validate(Mutate(r => r[10] ^= 0x01), height, at));
            // A changed merkle root or version breaks the work.
            Assert.Contains("proof-of-work", BtcChainRules.Mainnet.Validate(Mutate(r => r[40] ^= 0x01), height, at));
            Assert.Contains("proof-of-work", BtcChainRules.Mainnet.Validate(Mutate(r => r[0] ^= 0x01), height, at));
            // The right header at the wrong height, and a header whose parent is not known.
            Assert.Contains("does not extend", BtcChainRules.Mainnet.Validate(real, height + 1, at));
            Assert.Contains("not known", BtcChainRules.Mainnet.Validate(real, height, _ => null));
            Assert.Contains("above zero", BtcChainRules.Mainnet.Validate(real, 0, at));

            // A real header from elsewhere in the chain does not extend this tip either.
            Assert.Contains("does not extend", BtcChainRules.Mainnet.Validate(all[height + 3], height, at));

            // Not eighty bytes: not a header.
            Assert.False(BtcHeader.TryParse(real.Hex + "00", out _));
            Assert.False(BtcHeader.TryParse(real.Hex.Substring(2), out _));
            Assert.False(BtcHeader.TryParse("zz" + real.Hex.Substring(2), out _));
            Assert.False(BtcHeader.TryParse(null, out _));
        }

        /// <summary>The median-time rule: a header timed at or before the median of the eleven before it is refused, with valid work or not.</summary>
        [Fact]
        public void MedianTimePast_IsTheMiddleOfTheElevenBefore()
        {
            var all = Load("btc-headers-mainnet.txt");
            Func<long, BtcHeader?> at = h => all.TryGetValue(h, out var x) ? x : null;
            var height = MainnetCheckpoint + 1;
            Assert.True(BtcChainRules.MedianTimePast(height, at, out var median, out _));
            var times = Enumerable.Range(1, 11).Select(i => all[height - i].Time).OrderBy(t => t).ToList();
            Assert.Equal(times[5], median);
            Assert.True(all[height].Time > median);
            // The first header after a checkpoint needs all twelve checkpoint headers: eleven for the median, one to extend.
            Assert.False(BtcChainRules.MedianTimePast(height, h => h == height - 11 ? null : at(h), out _, out var why));
            Assert.Contains("not known", why);
        }

        [Fact]
        public void CompactTargets_DecodeAndEncode_AsBitcoinDoes()
        {
            Assert.True(BtcHeader.TryDecodeCompact(0x1d00ffff, out var limit));
            Assert.Equal(new BigInteger(0xffff) << 208, limit);
            Assert.Equal(0x1d00ffffu, BtcHeader.EncodeCompact(limit));
            Assert.Equal(0x1d00ffffu, BtcHeader.EncodeCompact((BigInteger.One << 224) - 1));          // the full limit rounds to the same compact

            Assert.True(BtcHeader.TryDecodeCompact(0x1b0404cb, out var t));
            Assert.Equal(new BigInteger(0x0404cb) << (8 * (0x1b - 3)), t);
            Assert.Equal(0x1b0404cbu, BtcHeader.EncodeCompact(t));

            Assert.True(BtcHeader.TryDecodeCompact(0x03123456, out var small));
            Assert.Equal(new BigInteger(0x123456), small);
            Assert.True(BtcHeader.TryDecodeCompact(0x02123456, out var smaller));
            Assert.Equal(new BigInteger(0x1234), smaller);
            Assert.Equal(0x02123400u, BtcHeader.EncodeCompact(new BigInteger(0x1234)));
            // A mantissa with its top bit set is written one byte longer, so it is never read as negative.
            Assert.Equal(0x05008000u, BtcHeader.EncodeCompact(new BigInteger(0x80000000)));
            Assert.Equal(0x04008000u, BtcHeader.EncodeCompact(new BigInteger(0x800000)));

            Assert.False(BtcHeader.TryDecodeCompact(0x1d80ffff, out _));     // sign bit: negative
            Assert.False(BtcHeader.TryDecodeCompact(0x00000000, out _));     // zero
            Assert.False(BtcHeader.TryDecodeCompact(0x01000000, out _));     // zero mantissa
            Assert.False(BtcHeader.TryDecodeCompact(0x23000001, out _));     // overflows 256 bits
            Assert.False(BtcHeader.TryDecodeCompact(0xff123456, out _));

            // Work at the easiest target is 2^32 + 2^16 + 1, as Bitcoin computes it for difficulty 1.
            var all = Load("btc-headers-testnet4.txt");
            var easy = all.Values.First(h => h.Bits == BtcChainRules.PowLimitBits);
            Assert.Equal(new BigInteger(0x100010001), easy.Work());
            Assert.True(all.Values.First(h => h.Bits != BtcChainRules.PowLimitBits).Work() > easy.Work());
        }

        [Fact]
        public void ACheckpoint_IsTwelveLinkedHeadersEndingOnAnIntervalBoundary()
        {
            var all = Load("btc-headers-mainnet.txt");
            var checkpoint = Checkpoint(all, MainnetCheckpoint);
            Assert.Equal(MainnetCheckpoint, checkpoint.Height);
            Assert.Equal(all[MainnetCheckpoint].Hash, checkpoint.Tip.Hash);
            Assert.Equal(all[MainnetCheckpoint - 11].Hash, checkpoint.At(MainnetCheckpoint - 11)!.Hash);
            Assert.Null(checkpoint.At(MainnetCheckpoint - 12));
            Assert.Null(checkpoint.At(MainnetCheckpoint + 1));

            string Hex(IEnumerable<long> heights) => string.Join(",", heights.Select(h => all[h].Hex));
            var twelve = Enumerable.Range(0, 12).Select(i => MainnetCheckpoint - 11 + i).ToList();

            // Separators and case do not matter.
            Assert.True(BtcCheckpoint.TryParse(MainnetCheckpoint, string.Join(" \r\n ", twelve.Select(h => all[h].Hex.ToUpperInvariant())), out _, out _));
            // Not an interval boundary.
            Assert.False(BtcCheckpoint.TryParse(MainnetCheckpoint + 1, Hex(twelve), out _, out var r1));
            Assert.Contains("first block of a difficulty interval", r1);
            // Eleven or thirteen headers.
            Assert.False(BtcCheckpoint.TryParse(MainnetCheckpoint, Hex(twelve.Skip(1)), out _, out _));
            Assert.False(BtcCheckpoint.TryParse(MainnetCheckpoint, Hex(twelve.Append(MainnetCheckpoint + 1)), out _, out _));
            // Twelve real headers that do not link (one skipped).
            Assert.False(BtcCheckpoint.TryParse(MainnetCheckpoint, Hex(twelve.Take(5).Concat(twelve.Skip(6)).Append(MainnetCheckpoint + 1)), out _, out var r2));
            Assert.Contains("does not extend", r2);
            // One header altered: it no longer meets its target.
            var bad = Convert.FromHexString(all[MainnetCheckpoint - 3].Hex); bad[77] ^= 0x10;
            var altered = string.Join(",", twelve.Select(h => h == MainnetCheckpoint - 3 ? Convert.ToHexString(bad) : all[h].Hex));
            Assert.False(BtcCheckpoint.TryParse(MainnetCheckpoint, altered, out _, out var r3));
            Assert.Contains("proof-of-work", r3);
            // Nothing set.
            Assert.False(BtcCheckpoint.TryParse(0, "", out _, out _));
            Assert.False(BtcCheckpoint.TryParse(MainnetCheckpoint, null, out _, out _));
        }

        /// <summary>The settings a release fills in are read per network, and an unset checkpoint says so.</summary>
        [Fact]
        public void TheConfiguredCheckpoint_ComesFromTheNetworksSettings()
        {
            var all = Load("btc-headers-vfx-signet.txt");
            var priorNet = Globals.IsTestNet; var priorH = Globals.BitcoinCheckpointHeightTestnet; var priorHex = Globals.BitcoinCheckpointHeadersTestnet;
            try
            {
                Globals.IsTestNet = true;
                Globals.BitcoinCheckpointHeightTestnet = 0;
                Globals.BitcoinCheckpointHeadersTestnet = "";
                Assert.Null(BtcCheckpoint.Configured(out var unset));
                Assert.Contains("no checkpoint headers are set", unset);

                Globals.BitcoinCheckpointHeightTestnet = SignetCheckpoint;
                Globals.BitcoinCheckpointHeadersTestnet = string.Join("", Enumerable.Range(0, 12).Select(i => all[SignetCheckpoint - 11 + i].Hex));
                var configured = BtcCheckpoint.Configured(out var reason);
                Assert.True(configured != null, reason);
                Assert.Equal(all[SignetCheckpoint].Hash, configured!.Tip.Hash);
                Assert.Same(BtcChainRules.Signet, BtcChainRules.Current);
            }
            finally
            {
                Globals.IsTestNet = priorNet; Globals.BitcoinCheckpointHeightTestnet = priorH; Globals.BitcoinCheckpointHeadersTestnet = priorHex;
            }
        }
    }
}
