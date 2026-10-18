using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A full state rebuild (ResetTreis) must leave no snapshot slot that a later restore could use to bring back the
    /// state the rebuild replaced; the next snapshot cycle then captures the rebuilt state.
    /// </summary>
    [Collection("DbContextSequential")]
    public class RebuildStateSnapshotInvalidationTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly bool _priorTreisUpdating;

        public RebuildStateSnapshotInvalidationTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"rbsnap_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            _priorLastBlock = Globals.LastBlock;
            _priorTreisUpdating = Globals.TreisUpdating;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            Globals.TreisUpdating = false;
            Globals.LastBlock = new Block { Height = 100 };
            StateTreiStatusService.SetSynced(100);
        }

        public void Dispose()
        {
            StateWriteContext.Clear();
            try { DbContext.CloseDB(); } catch { }
            Globals.LastBlock = _priorLastBlock;
            Globals.TreisUpdating = _priorTreisUpdating;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static void PutAccount(string key, decimal balance, long height)
        {
            StateWriteContext.SetHeight(height);
            try
            {
                StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = key, Balance = balance, Nonce = 0, StateRoot = "root", CodeHash = "hash" });
            }
            finally { StateWriteContext.Clear(); }
        }

        private static decimal? SlotBalance(int slotId, string key)
        {
            var doc = DbContext.DB_Snapshot.GetCollection($"s{slotId}_astate").FindOne(LiteDB.Query.EQ("Key", key));
            return doc == null ? null : (decimal?)doc["Balance"].AsDecimal;
        }

        [Fact]
        public async Task Rebuild_InvalidatesEverySnapshotSlot_IncludingAnchors()
        {
            PutAccount("addrA", 5m, 50);
            await StateSnapshotService.UpdateCycleAsync(100, "h100");
            await StateSnapshotService.UpdateAnchorCycleAsync(100, "h100", SnapshotManifest.AnchorSlot100);
            Assert.NotNull(StateSnapshotService.PickSlotForRestore(100));

            await BlockRollbackUtility.ResetTreis();

            Assert.False(StateSnapshotService.HasValidSlot());
            Assert.Null(StateSnapshotService.PickSlotForRestore(long.MaxValue));
        }

        [Fact]
        public async Task ARebuildStoppedAfterTheWipe_LeavesTheStateMarkedUnsynced()
        {
            PutAccount("addrA", 5m, 50);
            Assert.True(StateTreiStatusService.IsSynced());

            // What a crash during the replay leaves on disk: the status as it stands once the old state is gone.
            StateTreiStatus? afterWipe = null;
            BlockRollbackUtility.StateWipedForTests = () => afterWipe = StateTreiStatusService.GetStatus();
            try { await BlockRollbackUtility.ResetTreis(); }
            finally { BlockRollbackUtility.StateWipedForTests = null; }

            Assert.False(afterWipe!.IsSynced); // never the old IsSynced=true over partial state
            Assert.True(afterWipe.RebuildInProgress);
            Assert.NotEqual(true, StateTreiStatusService.GetStatus()?.RebuildInProgress); // the rebuild ended, so it is no longer in progress
        }

        private static void StoreBlockWithTx(long height, string txHash) => BlockchainData.GetBlocks().InsertSafe(new Block
        {
            Height = height, Hash = $"hash-{height}", PrevHash = $"hash-{height - 1}", StateRoot = "root", Timestamp = TimeUtil.GetTime(),
            Transactions = new List<Transaction> { new Transaction { Hash = txHash, FromAddress = "xA", ToAddress = "xB", Amount = 0M, TransactionType = TransactionType.TX, Timestamp = TimeUtil.GetTime() } },
        });

        [Fact]
        public async Task ARebuildEndingWithAStorageFault_KeepsTheDurableStop_AcrossARestart()
        {
            StoreBlockWithTx(1, "tx-1");
            StateData.TxApplyForTests = tx => { if (tx.Hash == "tx-1") throw new IOException("disk read failed"); };
            try { Assert.False(await BlockRollbackUtility.ResetTreis()); }
            finally { StateData.TxApplyForTests = null; }

            Assert.True(StateTreiStatusService.GetStatus()?.RebuildInProgress); // replaying the same store cannot repair it
            try
            {
                SnapshotRestoreUtility.RecallStateDestroyed(); // the next start
                Assert.True(SnapshotRestoreUtility.StateDestroyedByFailedRestore);
            }
            finally { SnapshotRestoreUtility.ClearStateDestroyed(); }
        }

        [Fact]
        public async Task ARebuildWhoseBlockStartStepHitsAStorageFault_KeepsTheDurableStop()
        {
            StoreBlockWithTx(1, "tx-1");
            StateData.BlockStartStepForTests = h => { if (h == 1) throw new IOException("disk read failed"); };
            try { Assert.False(await BlockRollbackUtility.ResetTreis()); }
            finally { StateData.BlockStartStepForTests = null; }

            Assert.True(StateTreiStatusService.GetStatus()?.RebuildInProgress);
        }

        [Fact]
        public async Task ARebuildEndingWithAFailureTheChainCauses_DoesNotStrandTheNode()
        {
            StoreBlockWithTx(1, "tx-1");
            StateData.TxApplyForTests = tx => { if (tx.Hash == "tx-1") throw new ArgumentException("unreadable field"); }; // what the data causes, on every node
            try { await BlockRollbackUtility.ResetTreis(); }
            finally { StateData.TxApplyForTests = null; }

            Assert.NotEqual(true, StateTreiStatusService.GetStatus()?.RebuildInProgress);
        }

        [Fact]
        public void AFailureReportedDuringARebuild_DoesNotLiftItsStop()
        {
            StateTreiStatusService.SetRebuildStarted();
            StateTreiStatusService.SetFailed("Reserve settlement failed on this node.");
            Assert.True(StateTreiStatusService.GetStatus()?.RebuildInProgress);
        }

        [Fact]
        public void ARebuildStillInProgressAtStartup_StopsTheNode_UntilARebuildCompletes()
        {
            try
            {
                StateTreiStatusService.SetRebuildStarted(); // as a crash mid-replay leaves it
                SnapshotRestoreUtility.RecallStateDestroyed();
                Assert.True(SnapshotRestoreUtility.StateDestroyedByFailedRestore);

                // A later failure reason does not hide an interrupted rebuild's partial state from the next start...
                SnapshotRestoreUtility.ClearStateDestroyed();
                StateTreiStatusService.SetRebuildStarted();
                StateTreiStatusService.SetRestoreDestroyed(false);
                SnapshotRestoreUtility.RecallStateDestroyed();
                Assert.True(SnapshotRestoreUtility.StateDestroyedByFailedRestore);

                // ...and a completed rebuild clears it.
                SnapshotRestoreUtility.ClearStateDestroyed();
                StateTreiStatusService.SetSynced(100);
                SnapshotRestoreUtility.RecallStateDestroyed();
                Assert.False(SnapshotRestoreUtility.StateDestroyedByFailedRestore);
            }
            finally { SnapshotRestoreUtility.ClearStateDestroyed(); }
        }

        [Fact]
        public async Task AfterRebuild_NextCycleCapturesTheRebuiltRows()
        {
            PutAccount("addrA", 5m, 50);
            await StateSnapshotService.UpdateCycleAsync(100, "h100");

            await BlockRollbackUtility.ResetTreis();
            StateTreiStatusService.SetSynced(100);
            // The replay re-creates the row stamped with its original block height, below the old slot height.
            PutAccount("addrA", 7m, 50);

            await StateSnapshotService.UpdateCycleAsync(110, "h110");
            var slot = StateSnapshotService.PickSlotForRestore(110);
            Assert.NotNull(slot);
            Assert.Equal(7m, SlotBalance(slot!.SlotId, "addrA"));
        }

        /// <summary>
        /// No snapshot is taken while the withdrawal store is being rebuilt: a row it has put back reads "requested" until
        /// the rebuild brings it to how its withdrawal ended, and a restore of such a snapshot would bring it back open.
        /// </summary>
        [Fact]
        public async Task WhileTheWithdrawalStoreIsRebuilt_NoSnapshotIsTaken()
        {
            PutAccount("addrA", 5m, 50);
            try
            {
                VerifiedXCore.Bitcoin.Services.VBTCWithdrawalStoreRebuildService.SetRunningForTests(true);
                await StateSnapshotService.UpdateCycleAsync(100, "h100");
                await StateSnapshotService.UpdateAnchorCycleAsync(100, "h100", SnapshotManifest.AnchorSlot100);
                Assert.False(StateSnapshotService.HasValidSlot());
            }
            finally { VerifiedXCore.Bitcoin.Services.VBTCWithdrawalStoreRebuildService.SetRunningForTests(false); }

            await StateSnapshotService.UpdateCycleAsync(110, "h110");
            Assert.NotNull(StateSnapshotService.PickSlotForRestore(110));
        }
    }
}
