using System.Diagnostics;
using System.Text.RegularExpressions;
using NBitcoin;
using VerifiedXCore.Privacy;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Text another party writes is decoded only through decoders that refuse a text longer than any value of its kind
    /// before decoding it. Base58 decoding costs time that grows with the square of the length, and a few million
    /// characters end the process with a stack overflow. No code calls the raw decoders.
    /// </summary>
    public class BoundedDecodeTests
    {
        [Fact]
        public void A_shielded_address_longer_than_any_is_refused_without_being_decoded()
        {
            var body = new string('2', 200_000); // valid Base58 characters: unbounded, this decode takes seconds
            var clock = Stopwatch.StartNew();
            Assert.False(ShieldedAddressCodec.TryDecode(ShieldedAddressConstants.Prefix + body, out _, out _, out var error));
            Assert.True(clock.ElapsedMilliseconds < 200, $"took {clock.ElapsedMilliseconds} ms");
            Assert.Contains("Base58", error);
        }

        [Fact]
        public void A_shielded_address_is_at_most_97_characters_after_its_prefix()
        {
            Assert.Equal(97, BoundedDecode.MaxBase58Chars(ShieldedAddressConstants.PayloadLengthV2 + 4));
            // Every address of the longest form fits: the most a 71-byte value can take in Base58.
            var largest = NBitcoin.DataEncoders.Encoders.Base58.EncodeData(Enumerable.Repeat((byte)0xff, ShieldedAddressConstants.PayloadLengthV2 + 4).ToArray());
            Assert.True(largest.Length <= 97);
            Assert.Equal(Enumerable.Repeat((byte)0xff, 71), BoundedDecode.Base58(largest, 71));
        }

        [Fact]
        public void A_bitcoin_address_is_decoded_up_to_the_longest_and_refused_past_it()
        {
            var taproot = new Key().PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, Network.Main).ToString();
            var legacy = new Key().PubKey.GetAddress(ScriptPubKeyType.Legacy, Network.Main).ToString();
            Assert.Equal(taproot, BoundedDecode.BitcoinAddress(taproot, Network.Main).ToString());
            Assert.Equal(legacy, BoundedDecode.BitcoinAddress(legacy, Network.Main).ToString());

            var clock = Stopwatch.StartNew();
            Assert.Throws<FormatException>(() => BoundedDecode.BitcoinAddress(new string('1', 1_000_000), Network.Main));
            Assert.True(clock.ElapsedMilliseconds < 50, $"took {clock.ElapsedMilliseconds} ms");
            Assert.Throws<FormatException>(() => BoundedDecode.BitcoinAddress(new string('q', BoundedDecode.MaxBitcoinAddressChars + 1), Network.Main));
        }

        [Fact]
        public void No_code_calls_a_raw_base58_or_address_decoder()
        {
            var raw = new Regex(@"(^|[^.\w])BitcoinAddress\.Create\(|Encoders\.Base58(Check)?\.DecodeData\(|NBitcoin\.BitcoinAddress\.Create\(");
            var offenders = new List<string>();
            foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "VerifiedXCore"), "*.cs", SearchOption.AllDirectories))
            {
                var path = file.Replace('\\', '/');
                if (path.Contains("/obj/") || path.Contains("/bin/") || path.EndsWith("/Utilities/BoundedDecode.cs")) continue;
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                    if (raw.IsMatch(lines[i]) && !lines[i].TrimStart().StartsWith("//") && !lines[i].TrimStart().StartsWith("///"))
                        offenders.Add($"{path.Substring(path.IndexOf("/VerifiedXCore/", StringComparison.Ordinal) + 1)}:{i + 1}");
            }
            Assert.True(offenders.Count == 0, "Decode through BoundedDecode instead:\n  " + string.Join("\n  ", offenders));
        }

        private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "") =>
            Path.GetDirectoryName(Path.GetDirectoryName(thisFile))!;
    }
}
