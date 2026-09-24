using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using VerifiedXCore.Extensions;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// BB-4 (VX-09, VX-20, VX-22): GZip decompression never produces more than a stated number of bytes. The overloads
    /// without a limit used to copy the whole stream (a 50 KB bomb became 50 MB, and so on up to gigabytes).
    /// </summary>
    public class BB4_BoundedDecompressionTests
    {
        private static byte[] Gzip(byte[] raw)
        {
            using var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, CompressionLevel.Optimal)) gz.Write(raw, 0, raw.Length);
            return ms.ToArray();
        }

        private static byte[] Bomb(int decompressedBytes) => Gzip(new byte[decompressedBytes]); // zeros compress ~1000:1

        [Fact]
        public void BB4_RoundTrip_UnderTheLimit()
        {
            var raw = Encoding.UTF8.GetBytes("hello bounded world");
            Assert.Equal(raw, Gzip(raw).ToDecompress(1024));
            Assert.Equal(raw, Gzip(raw).ToDecompress());
        }

        [Fact]
        public void BB4_ExactlyAtTheLimit_IsAllowed()
        {
            var raw = new byte[4096];
            Assert.Equal(4096, Gzip(raw).ToDecompress(4096).Length);
        }

        [Fact]
        public void BB4_Bomb_StopsAtTheLimit()
        {
            var bomb = Bomb(50 * 1024 * 1024);
            Assert.True(bomb.Length < 200 * 1024); // small on the wire
            Assert.Throws<InvalidDataException>(() => bomb.ToDecompress(8 * 1024 * 1024));
        }

        [Fact]
        public void BB4_DefaultOverloads_AreBoundedToo()
        {
            var bomb = Bomb(GenericExtensions.DefaultMaxDecompressedBytes + 1024);
            Assert.Throws<InvalidDataException>(() => bomb.ToDecompress());
            Assert.Throws<InvalidDataException>(() => Convert.ToBase64String(bomb).ToDecompress());
        }

        [Fact]
        public void BB4_StringOverload_RoundTripsAndBounds()
        {
            var text = "compressed text payload";
            var b64 = Convert.ToBase64String(Gzip(Encoding.Unicode.GetBytes(text)));
            Assert.Equal(text, b64.ToDecompress(1024));
            Assert.Throws<InvalidDataException>(() => b64.ToDecompress(4));
        }
    }
}
