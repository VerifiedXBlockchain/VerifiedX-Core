using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.P2P;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// VX-09 (HIGH): "An unauthenticated peer requests the entire block database in one call".
    ///
    /// Audit PoC: SendBlockList(0, 1e9) and (0, long.MaxValue) returned the same as (0, 1000) — the end
    /// bound was never validated; control SendBlockList(5, 10) honoured its arguments. On a populated chain
    /// one call materialises the whole block table plus several copies.
    /// </summary>
    [Collection("DbContextSequential")]
    public class VX09_BlockListBoundsTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock = Globals.LastBlock;

        public VX09_BlockListBoundsTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"vx09_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();

            var blocks = BlockchainData.GetBlocks();
            for (long h = 0; h < 1205; h++)
                blocks.InsertSafe(new Block { Height = h, Hash = "h" + h, Transactions = new List<Transaction>() });
            Globals.LastBlock = new Block { Height = 1204 };
        }

        public void Dispose()
        {
            Globals.LastBlock = _priorLastBlock;
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static P2PServer Peer(string ip)
        {
            var context = new Mock<HubCallerContext>();
            var features = new FeatureCollection();
            var connection = new Mock<IHttpConnectionFeature>();
            connection.Setup(x => x.RemoteIpAddress).Returns(IPAddress.Parse(ip));
            features.Set(connection.Object);
            context.Setup(x => x.Features).Returns(features);
            context.Setup(x => x.ConnectionId).Returns(Guid.NewGuid().ToString());
            return new P2PServer { Context = context.Object };
        }

        private static List<Block> Decode(string reply) => JsonConvert.DeserializeObject<List<Block>>(reply.ToDecompress())!;

        [Theory]
        [InlineData(1_000_000_000L)]
        [InlineData(long.MaxValue)]
        public async Task VX09_AuditPoC_HugeEnd_IsClampedToOneBatch(long end)
        {
            var reply = await Peer("172.28.0.99").SendBlockList(0, end);
            var list = Decode(reply);
            Assert.Equal(BlockServeLimits.MaxBlocksPerList, list.Count);        // not the whole chain (1205)
            Assert.Equal(0, list.First().Height);
            Assert.Equal(BlockServeLimits.MaxBlocksPerList - 1, list.Last().Height);
        }

        [Fact]
        public async Task VX09_Control_SmallRange_IsHonoured()
        {
            var list = Decode(await Peer("172.28.0.99").SendBlockList(5, 10));
            Assert.Equal(Enumerable.Range(5, 6).Select(x => (long)x), list.Select(b => b.Height));
        }

        [Theory]
        [InlineData(10, 5)]    // end before start
        [InlineData(-1, 5)]    // negative start
        [InlineData(5000, 6000)] // past the tip
        public async Task VX09_MalformedRange_Refused(long start, long end) =>
            Assert.Equal("0", await Peer("172.28.0.99").SendBlockList(start, end));

        [Fact]
        public void VX09_ReplyFormat_IsUnchanged_ForExistingDownloaders()
        {
            var blocks = Enumerable.Range(0, 3).Select(h => BlockchainData.GetBlockByHeight(h)!).ToList();
            var (json, count) = BlockServeLimits.BuildListJson(0, 2, BlockchainData.GetBlockByHeight);
            Assert.Equal(3, count);
            Assert.Equal(JsonConvert.SerializeObject(blocks), json);
        }

        [Fact]
        public void VX09_ReplyBytes_AreBounded_EvenForLargeBlocks()
        {
            var big = new string('x', 1_000_000);
            Block Get(long h) => new Block { Height = h, Hash = "b" + h, Transactions = new List<Transaction> { new Transaction { Data = big } } };
            var (json, count) = BlockServeLimits.BuildListJson(0, 999, Get);
            Assert.True(json.Length <= BlockServeLimits.MaxListBytes + 1, $"{json.Length}");
            Assert.InRange(count, 1, 8);
        }

        [Fact]
        public async Task VX09_ConcurrentBuildsPerPeer_AreCapped()
        {
            var ip = "172.28.0." + new Random().Next(100, 200);
            var a = await BlockServeLimits.TryEnterAsync(ip, TimeSpan.FromMilliseconds(50));
            var b = await BlockServeLimits.TryEnterAsync(ip, TimeSpan.FromMilliseconds(50));
            var c = await BlockServeLimits.TryEnterAsync(ip, TimeSpan.FromMilliseconds(50));
            Assert.NotNull(a); Assert.NotNull(b); Assert.Null(c);
            a!.Dispose();
            var d = await BlockServeLimits.TryEnterAsync(ip, TimeSpan.FromMilliseconds(50));
            Assert.NotNull(d);
            b!.Dispose(); d!.Dispose();
        }

        [Fact]
        public void VX09_SpanByteBudget_IsClamped() =>
            Assert.Equal(BlockServeLimits.MaxListBytes, BlockServeLimits.ClampByteBudget(long.MaxValue));

        [Fact]
        public async Task VX09_FollowUp_SendBlockSpan_TakesAServeSlot()
        {
            const string ip = "172.28.9.99";
            var a = await BlockServeLimits.TryEnterAsync(ip, TimeSpan.FromMilliseconds(50));
            var b = await BlockServeLimits.TryEnterAsync(ip, TimeSpan.FromMilliseconds(50));
            try
            {
                Assert.NotNull(a); Assert.NotNull(b);
                Assert.Null(await Peer(ip).SendBlockSpan(0, 1_000_000)); // both of this peer's slots are held
            }
            finally { a?.Dispose(); b?.Dispose(); }
        }
    }
}
