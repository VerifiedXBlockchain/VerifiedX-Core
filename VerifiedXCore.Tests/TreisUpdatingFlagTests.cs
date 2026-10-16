using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using VerifiedXCore;
using VerifiedXCore.Data;
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
        public async Task NormalApply_ReturnsTrue_AndClearsTreisUpdating()
        {
            var block = new Block { Height = 5, StateRoot = "sr", Transactions = new List<Transaction>() };

            Assert.True(await StateData.UpdateTreis(block));
            Assert.False(Globals.TreisUpdating);
        }
    }
}
