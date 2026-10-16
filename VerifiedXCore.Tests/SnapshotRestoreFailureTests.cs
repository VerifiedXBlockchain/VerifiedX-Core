using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A snapshot restore that fails after it has wiped the state (and deleted the blocks above its target) leaves the
    /// in-memory tip on the block store's tip and stops block validation, instead of reading as "nothing changed".
    /// </summary>
    [Collection("DbContextSequential")]
    public class SnapshotRestoreFailureTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly bool _priorResyncing = Globals.IsResyncing;
        private readonly bool _priorStopTimers = Globals.StopAllTimers;
        private readonly long _priorBondHeight = Globals.ValidatorBondHeight;

        public SnapshotRestoreFailureTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"restore_fail_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
        }

        public void Dispose()
        {
            SnapshotRestoreUtility.ClearStateDestroyed();
            try { DbContext.CloseDB(); } catch { }
            Globals.LastBlock = _priorLastBlock;
            Globals.IsResyncing = _priorResyncing;
            Globals.StopAllTimers = _priorStopTimers;
            Globals.ValidatorBondHeight = _priorBondHeight;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static Block AddBlock(long height)
        {
            var block = new Block
            {
                Height = height, Hash = $"hash-{height}", PrevHash = $"hash-{height - 1}", StateRoot = $"root-{height}", Timestamp = TimeUtil.GetTime(),
                Transactions = new List<Transaction>(),
            };
            BlockchainData.GetBlocks().InsertSafe(block);
            return block;
        }

        /// <summary>
        /// A chain 1..50 with a verified snapshot slot at 45 and no block 46. The validator bonds are made to start at 46,
        /// so replaying block 47 fails: its block-start step finds no bonds (the block that starts them is missing).
        /// </summary>
        private static async Task ChainWithSlotAt45_ThatFailsToReplay()
        {
            Block tip = null!;
            for (long h = 1; h <= 45; h++) tip = AddBlock(h);
            Globals.LastBlock = tip;
            StateTreiStatusService.SetSynced(45);
            await StateSnapshotService.UpdateCycleAsync(45, tip.Hash);
            Assert.NotNull(StateSnapshotService.PickVerifiedSlotForRestore(48));
            for (long h = 47; h <= 50; h++) tip = AddBlock(h);
            Globals.LastBlock = tip;
            Globals.ValidatorBondHeight = 46;
        }

        [Fact]
        public async Task FailedReplay_LeavesTheTipOnTheStoredChain_AndStopsValidation()
        {
            await ChainWithSlotAt45_ThatFailsToReplay();

            Assert.False(await SnapshotRestoreUtility.TryRestoreAsync(48));

            Assert.True(SnapshotRestoreUtility.StateDestroyedByFailedRestore);
            Assert.Equal(BlockchainData.GetLastBlock()!.Height, Globals.LastBlock.Height);
            Assert.Equal(48, Globals.LastBlock.Height);   // blocks 49 and 50 were deleted; the in-memory tip is not left on them
            Assert.False(StateTreiStatusService.IsSynced());

            // The old branch's next block is not accepted over the gap, on the wiped state.
            var next = new Block { Height = 51, Hash = "hash-51", PrevHash = "hash-50", Timestamp = TimeUtil.GetTime(), Transactions = new List<Transaction>() };
            Assert.False(await BlockValidatorService.ValidateBlock(next, true));
            var onStoredTip = new Block { Height = 49, Hash = "hash-49b", PrevHash = "hash-48", Timestamp = TimeUtil.GetTime(), Transactions = new List<Transaction>() };
            Assert.False(await BlockValidatorService.ValidateBlock(onStoredTip, true));
        }

        [Fact]
        public async Task SuccessfulRestore_DoesNotStopValidation_AndClearsAnEarlierFailure()
        {
            await ChainWithSlotAt45_ThatFailsToReplay();
            Assert.False(await SnapshotRestoreUtility.TryRestoreAsync(48));
            Assert.True(SnapshotRestoreUtility.StateDestroyedByFailedRestore);

            // Under the settings the slot was taken with, the same blocks replay: the restore succeeds and lifts the stop.
            Globals.ValidatorBondHeight = _priorBondHeight;
            Assert.True(await SnapshotRestoreUtility.TryRestoreAsync(48));
            Assert.False(SnapshotRestoreUtility.StateDestroyedByFailedRestore);
            Assert.Equal(48, Globals.LastBlock.Height);
            Assert.True(StateTreiStatusService.IsSynced());
        }

        [Fact]
        public async Task NoUsableSlot_IsNotADestroyedState()
        {
            for (long h = 1; h <= 3; h++) Globals.LastBlock = AddBlock(h);

            Assert.False(await SnapshotRestoreUtility.TryRestoreAsync(2)); // nothing wiped, nothing deleted

            Assert.False(SnapshotRestoreUtility.StateDestroyedByFailedRestore);
            Assert.Equal(3, Globals.LastBlock.Height);
        }
    }
}
