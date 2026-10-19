using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A block stamped past this node's clock is refused against the local clock, and the refusal is not sticky: the
    /// allowance stays 60 seconds, the sender is not banned, and nothing remembers the block, so the same block is taken
    /// again once the node's clock passes its timestamp.
    /// </summary>
    [Collection("DbContextSequential")]
    public class FutureBlockRefusalTests : IDisposable
    {
        private const long H = 5_000;
        private const string Ip = "203.0.113.7";

        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock = Globals.LastBlock;

        public FutureBlockRefusalTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"future_block_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            Globals.LastBlock = new Block { Height = H - 1, Hash = "tip", Timestamp = 1_700_000_000 };
            BlockDownloadService.BlockDict.Clear();
            Globals.BannedIPs ??= new System.Collections.Concurrent.ConcurrentDictionary<string, Peers>();
            Globals.BannedIPs.TryRemove(Ip, out _);
        }

        public void Dispose()
        {
            BlockDownloadService.BlockDict.Clear();
            Globals.BannedIPs.TryRemove(Ip, out _);
            Globals.LastBlock = _priorLastBlock;
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static Block FutureBlock() => new()
        {
            Height = H, Hash = "future-block", Validator = "xProducer", Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 600,
            Transactions = new List<Transaction>(),
        };

        [Fact]
        public void TheAllowance_IsSixtySecondsPastTheLocalClock()
        {
            const long now = 1_800_000_000;
            Assert.Equal(60, BlockValidatorService.FutureBlockAllowanceSeconds);
            Assert.False(BlockValidatorService.IsAheadOfClock(now, now));
            Assert.False(BlockValidatorService.IsAheadOfClock(now + 60, now));
            Assert.True(BlockValidatorService.IsAheadOfClock(now + 61, now));
        }

        [Fact]
        public void ABlockAheadOfOurClock_IsTakenOnceTheClockPassesIt()
        {
            var block = FutureBlock();
            Assert.True(BlockValidatorService.IsAheadOfClock(block.Timestamp, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            Assert.False(BlockValidatorService.IsAheadOfClock(block.Timestamp, block.Timestamp - 60));
        }

        [Fact]
        public async Task ARefusedBlock_BansNoOne_AndIsNotRemembered()
        {
            var block = FutureBlock();
            Assert.True(BlockStaging.Stage(block, Ip));

            await BlockValidatorService.ValidateBlocks();

            Assert.Equal(H - 1, Globals.LastBlock.Height);
            Assert.False(Globals.BannedIPs.ContainsKey(Ip));
            Assert.False(BlockDownloadService.BlockDict.ContainsKey(H));
            // Fetched again, the same block is staged and judged afresh.
            Assert.True(BlockStaging.Stage(block, Ip));
        }
    }
}
