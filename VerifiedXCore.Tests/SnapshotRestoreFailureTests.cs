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

            // Under the settings the slot was taken with, and with the missing block back, the restore succeeds and lifts the stop.
            Globals.ValidatorBondHeight = _priorBondHeight;
            BlockchainData.GetBlocks().DeleteManySafe(x => x.Height > 45); // re-added in height order, as a chain is stored
            for (long h = 46; h <= 48; h++) AddBlock(h);
            Assert.True(await SnapshotRestoreUtility.TryRestoreAsync(48));
            Assert.False(SnapshotRestoreUtility.StateDestroyedByFailedRestore);
            Assert.Equal(48, Globals.LastBlock.Height);
            Assert.True(StateTreiStatusService.IsSynced());
        }

        [Fact]
        public async Task TheStop_KeepsTheNodeOutOfProduction_AndOutlivesARestart()
        {
            await ChainWithSlotAt45_ThatFailsToReplay();
            Assert.False(await SnapshotRestoreUtility.TryRestoreAsync(48));
            Assert.False(Globals.ProducerReady);

            // A restart forgets the in-memory flag; the recorded status brings it back.
            SnapshotRestoreUtility.ClearStateDestroyed();
            SnapshotRestoreUtility.RecallStateDestroyed();
            Assert.True(SnapshotRestoreUtility.StateDestroyedByFailedRestore);

            // A later failure reason (an improper shutdown flagged at startup) does not drop it.
            SnapshotRestoreUtility.ClearStateDestroyed();
            StateTreiStatusService.SetFailed("Improper shutdown.");
            SnapshotRestoreUtility.RecallStateDestroyed();
            Assert.True(SnapshotRestoreUtility.StateDestroyedByFailedRestore);

            // A state that is merely unverified (synced once, then flagged) is not stopped.
            SnapshotRestoreUtility.ClearStateDestroyed();
            StateTreiStatusService.SetSynced(48);
            StateTreiStatusService.SetFailed("Improper shutdown.");
            SnapshotRestoreUtility.RecallStateDestroyed();
            Assert.False(SnapshotRestoreUtility.StateDestroyedByFailedRestore);
        }

        [Fact]
        public void ACrashMidRestore_StillStopsTheNodeAfterTheRestart()
        {
            // The restore marked the state before its wipe, then the process died before it finished.
            StateTreiStatusService.SetFailed("Snapshot restore in progress (slot 1 -> 48).");
            StateTreiStatusService.SetRestoreDestroyed(true);
            StateTreiStatusService.SetFailed("Improper shutdown.");

            SnapshotRestoreUtility.RecallStateDestroyed();

            Assert.True(SnapshotRestoreUtility.StateDestroyedByFailedRestore);
        }

        [Fact]
        public async Task AReplayRangeWithAMissingBlock_IsNotARestoredState()
        {
            await ChainWithSlotAt45_ThatFailsToReplay();
            Globals.ValidatorBondHeight = _priorBondHeight; // every block present replays; block 46 is still missing

            Assert.False(await SnapshotRestoreUtility.TryRestoreAsync(48));

            Assert.True(SnapshotRestoreUtility.StateDestroyedByFailedRestore);
            Assert.False(StateTreiStatusService.IsSynced());
        }

        [Fact]
        public async Task ARebuildOverAGappedBlockStore_DoesNotLiftTheStop()
        {
            for (long h = 1; h <= 5; h++) if (h != 3) AddBlock(h);
            StateTreiStatusService.SetFailed("Snapshot restore failed after wiping the state (restore to 5).");
            StateTreiStatusService.SetRestoreDestroyed(true);
            SnapshotRestoreUtility.RecallStateDestroyed();

            Assert.False(await BlockRollbackUtility.ResetTreis());
            Assert.True(SnapshotRestoreUtility.StateDestroyedByFailedRestore);
            Assert.False(StateTreiStatusService.IsSynced());

            // With the block back, the rebuild succeeds and lifts the stop.
            BlockchainData.GetBlocks().DeleteManySafe(x => x.Height > 2);
            for (long h = 3; h <= 5; h++) AddBlock(h);
            Assert.True(await BlockRollbackUtility.ResetTreis());
            Assert.False(SnapshotRestoreUtility.StateDestroyedByFailedRestore);
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
