using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Newtonsoft.Json;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.P2P;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// VX-20 (MEDIUM): "A malicious sync peer forces gigabyte allocations".
    ///
    /// Audit PoC: a peer's SendBlockList reply is a base64 GZip string; the client decompressed it with no limit
    /// (ToDecompress) before deserialising, inside a catch that swallowed everything including OutOfMemory. A reply of
    /// a few MB expanded to gigabytes. (The audit's claimed V1 equivalent is a typed Block bounded by the hub's message
    /// size, so it is not affected; the other remote decompress is the SendActiveVals reply.)
    /// </summary>
    public class VX20_RemoteDecompressionTests
    {
        private static byte[] Gzip(byte[] raw)
        {
            using var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, CompressionLevel.Optimal)) gz.Write(raw, 0, raw.Length);
            return ms.ToArray();
        }

        /// <summary>What a malicious peer returns from SendBlockList: base64 of a GZip bomb of UTF-16 text.</summary>
        private static string BlockSpanBomb(int decompressedBytes) => Convert.ToBase64String(Gzip(new byte[decompressedBytes]));

        [Fact]
        public void VX20_AuditPoC_BlockSpanBomb_StopsAtTheBound()
        {
            var reply = BlockSpanBomb(64 * 1024 * 1024);
            Assert.True(reply.Length < 1_000_000); // under the hub's message limit on the wire
            Assert.Throws<InvalidDataException>(() => P2PClient.DecodeBlockSpan(reply));
        }

        [Fact]
        public void VX20_AuditPoC_ThePrimitiveTheClientUsed_IsBoundedToo()
        {
            // GetBlockList called blockSpan.ToDecompress() — now bounded by default as well.
            var reply = BlockSpanBomb(GenericExtensions.DefaultMaxDecompressedBytes + 1024);
            Assert.Throws<InvalidDataException>(() => reply.ToDecompress());
        }

        [Fact]
        public void VX20_Control_GenuineBlockSpan_Decodes()
        {
            var blocks = new List<Block> { new Block { Height = 7, Hash = "h7", Transactions = new List<Transaction>() }, new Block { Height = 8, Hash = "h8", Transactions = new List<Transaction>() } };
            var reply = JsonConvert.SerializeObject(blocks).ToCompress();
            var decoded = P2PClient.DecodeBlockSpan(reply);
            Assert.Equal(2, decoded!.Count);
            Assert.Equal("h8", decoded[1].Hash);
        }

        [Fact]
        public void VX20_ActiveValidatorReplyBomb_StopsAtTheBound()
        {
            var reply = BlockSpanBomb(P2PClient.MaxRemoteDecompressedBytes + 1024);
            Assert.Throws<InvalidDataException>(() => reply.ToDecompress(P2PClient.MaxRemoteDecompressedBytes));
        }

        [Fact]
        public void VX20_Adjacent_SmartContractCodeBomb_StopsAtTheBound()
        {
            // Contract code arrives inside transactions and every node decompresses it during validation.
            var bomb = Gzip(new byte[SmartContractUtility.MaxDecompressedContractBytes + 1024]);
            Assert.Throws<InvalidDataException>(() => SmartContractUtility.Decompress(bomb));

            var code = Encoding.Unicode.GetBytes("function Main() { return 1; }");
            Assert.Equal(code, SmartContractUtility.Decompress(SmartContractUtility.Compress(code))); // control
        }

        [Fact]
        public void VX20_FollowUp_HonestReplyNearTheServersListCap_Decodes()
        {
            // The server caps a list at MaxListBytes of JSON TEXT and compresses it as UTF-16 (2 bytes per char).
            var bigTx = new Transaction { Hash = "t", FromAddress = "a", ToAddress = "b", Data = new string('d', 6_000_000) };
            var blocks = new List<Block> { new Block { Height = 1, Hash = "h1", Transactions = new List<Transaction> { bigTx } } };
            var json = JsonConvert.SerializeObject(blocks);
            Assert.True(json.Length < VerifiedXCore.P2P.BlockServeLimits.MaxListBytes);
            var decoded = P2PClient.DecodeBlockSpan(json.ToCompress());
            Assert.Single(decoded!);
        }
    }
}
