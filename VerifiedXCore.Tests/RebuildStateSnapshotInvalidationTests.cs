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
            bool? syncedAfterWipe = null;
            BlockRollbackUtility.StateWipedForTests = () => syncedAfterWipe = StateTreiStatusService.IsSynced();
            try { await BlockRollbackUtility.ResetTreis(); }
            finally { BlockRollbackUtility.StateWipedForTests = null; }

            Assert.False(syncedAfterWipe); // the next startup rebuilds instead of snapshotting partial state
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
    }
}
