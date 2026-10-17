using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.Services;
using VerifiedXCore.Models;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// AddBlock waits while Globals.TreisUpdating is set, so UpdateTreis must clear it however the apply ends.
    /// </summary>
    [Collection("DbContextSequential")]
    public class TreisUpdatingFlagTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly bool _priorFlag;

        public TreisUpdatingFlagTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"tuf_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            _priorFlag = Globals.TreisUpdating;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
        }

        public void Dispose()
        {
            Globals.TreisUpdating = _priorFlag;
            StateWriteContext.Clear();
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        [Fact]
        public async Task ApplyThatThrows_StillClearsTreisUpdating()
        {
            // A block with no transaction list makes the apply throw part-way through.
            var block = new Block { Height = 5, StateRoot = "sr", Transactions = null! };

            await Assert.ThrowsAnyAsync<Exception>(() => StateData.UpdateTreis(block));
            Assert.False(Globals.TreisUpdating);
        }

        [Fact]
        public async Task AnApplyThatThrows_AfterTheBlockIsCommitted_CountsAsIncomplete()
        {
            // Block validation applies a committed block through this: a throw is an incomplete apply (false), which the
            // caller flags, instead of escaping to a catch that only logged.
            var block = new Block { Height = 5, StateRoot = "sr", Transactions = null! };

            Assert.False(await BlockValidatorService.ApplyStateAsync(block));
            Assert.False(Globals.TreisUpdating);
            Assert.True(await BlockValidatorService.ApplyStateAsync(new Block { Height = 6, StateRoot = "sr", Transactions = new List<Transaction>() }));
        }

        [Fact]
        public async Task NormalApply_ReturnsTrue_AndClearsTreisUpdating()
        {
            var block = new Block { Height = 5, StateRoot = "sr", Transactions = new List<Transaction>() };

            Assert.True(await StateData.UpdateTreis(block));
            Assert.False(Globals.TreisUpdating);
        }
    }
}
