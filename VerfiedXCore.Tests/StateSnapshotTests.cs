using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LiteDB;
using ReserveBlockCore;
using ReserveBlockCore.Data;
using ReserveBlockCore.Extensions;
using ReserveBlockCore.Models;
using ReserveBlockCore.Services;
using Xunit;

namespace VerfiedXCore.Tests
{
    /// <summary>
    /// Snapshot-restore recovery layer tests: LastModifiedHeight stamping via the typed
    /// extension overloads, deletion tombstones, slot rotation/diffing in StateSnapshotService,
    /// restore-slot selection, and crash-mid-update handling.
    ///
    /// All chain-derived live collections are backed by ONE temp LiteDB file (collection names
    /// are globally unique) and the snapshot store by a second — mirroring production layout
    /// closely enough for the copy/diff logic while keeping the fixture small.
    /// </summary>
    [Collection("DbContextSequential")]
    public class StateSnapshotTests : IDisposable
    {
        private readonly string _livePath;
        private readonly string _snapPath;
        private readonly LiteDatabase _live;
        private readonly LiteDatabase _snap;

        public StateSnapshotTests()
        {
            _livePath = Path.Combine(Path.GetTempPath(), $"snap_live_{Guid.NewGuid():N}.db");
            _snapPath = Path.Combine(Path.GetTempPath(), $"snap_store_{Guid.NewGuid():N}.db");
            _live = new LiteDatabase(new ConnectionString { Filename = _livePath, Connection = ConnectionType.Direct });
            _snap = new LiteDatabase(new ConnectionString { Filename = _snapPath, Connection = ConnectionType.Direct });

            // Every chain-derived source the snapshot spec list reads from:
            DbContext.DB_AccountStateTrei = _live;
            DbContext.DB_SmartContractStateTrei = _live;
            DbContext.DB_WorldStateTrei = _live;
            DbContext.DB_DecShopStateTrei = _live;
            DbContext.DB_DNR = _live;
            DbContext.DB_TopicTrei = _live;
            DbContext.DB_Vote = _live;
            DbContext.DB_Wallet = _live;
            DbContext.DB_Reserve = _live;
            DbContext.DB_TokenizedWithdrawals = _live;
            DbContext.DB_VBTCWithdrawalRequests = _live;
            DbContext.DB_vBTC = _live;
            DbContext.DB_Privacy = _live;
            DbContext.DB_Snapshot = _snap;

            Globals.TreisUpdating = false;
            Globals.LastBlock = new Block { Height = 100 };
            StateWriteContext.Clear();

            // Snapshot cycles refuse to run on state not flagged synced.
            StateTreiStatusService.SetSynced(100);
        }

        public void Dispose()
        {
            StateWriteContext.Clear();
            try { _live.Dispose(); } catch { }
            try { _snap.Dispose(); } catch { }
            DbContext.DB_AccountStateTrei = null;
            DbContext.DB_SmartContractStateTrei = null;
            DbContext.DB_WorldStateTrei = null;
            DbContext.DB_DecShopStateTrei = null;
            DbContext.DB_DNR = null;
            DbContext.DB_TopicTrei = null;
            DbContext.DB_Vote = null;
            DbContext.DB_Wallet = null;
            DbContext.DB_Reserve = null;
            DbContext.DB_TokenizedWithdrawals = null;
            DbContext.DB_VBTCWithdrawalRequests = null;
            DbContext.DB_vBTC = null;
            DbContext.DB_Privacy = null;
            DbContext.DB_Snapshot = null;
            try { if (File.Exists(_livePath)) File.Delete(_livePath); } catch { }
            try { if (File.Exists(_snapPath)) File.Delete(_snapPath); } catch { }
        }

        private ILiteCollection<AccountStateTrei> Accounts()
            => _live.GetCollection<AccountStateTrei>(DbContext.RSRV_ASTATE_TREI);

        private ILiteCollection<SmartContractStateTrei> Contracts()
            => _live.GetCollection<SmartContractStateTrei>(DbContext.RSRV_SCSTATE_TREI);

        private static AccountStateTrei NewAccount(string key, decimal balance) => new AccountStateTrei
        {
            Key = key,
            Balance = balance,
            Nonce = 0,
            StateRoot = "root",
            CodeHash = "hash"
        };

        private static SmartContractStateTrei NewContract(string uid) => new SmartContractStateTrei
        {
            SmartContractUID = uid,
            ContractData = "data",
            MinterAddress = "minter",
            OwnerAddress = "owner"
        };

        // ── Stamping ────────────────────────────────────────────────────────────────────────

        [Fact]
        public void TypedOverloads_StampLastModifiedHeight_OnInsertAndUpdate()
        {
            StateWriteContext.SetHeight(42);
            try
            {
                var col = Accounts();
                var acct = NewAccount("addr1", 10m);
                col.InsertSafe(acct); // must resolve to the typed stamping overload

                var stored = col.FindOne(x => x.Key == "addr1");
                Assert.Equal(42, stored.LastModifiedHeight);

                StateWriteContext.SetHeight(55);
                stored.Balance = 25m;
                col.UpdateSafe(stored);

                stored = col.FindOne(x => x.Key == "addr1");
                Assert.Equal(55, stored.LastModifiedHeight);
                Assert.Equal(25m, stored.Balance);
            }
            finally
            {
                StateWriteContext.Clear();
            }
        }

        [Fact]
        public async Task TypedOverloads_StampAsyncWrites_AndSmartContracts()
        {
            StateWriteContext.SetHeight(77);
            try
            {
                var accounts = Accounts();
                await accounts.InsertSafeAsync(NewAccount("addr2", 1m));
                Assert.Equal(77, accounts.FindOne(x => x.Key == "addr2").LastModifiedHeight);

                var contracts = Contracts();
                SmartContractStateTrei.SaveSmartContract(NewContract("sc:1"));
                Assert.Equal(77, contracts.FindOne(x => x.SmartContractUID == "sc:1").LastModifiedHeight);
            }
            finally
            {
                StateWriteContext.Clear();
            }
        }

        [Fact]
        public void StampHeight_FallsBackToOverStamp_WhenContextUnset()
        {
            StateWriteContext.Clear();
            Globals.LastBlock = new Block { Height = 500 };

            // Over-stamp (tip + 1) is the safe direction: an unchanged record copied one extra
            // time is harmless, an under-stamped change missed by the diff is corruption.
            Assert.Equal(501, StateWriteContext.StampHeight);

            StateWriteContext.SetHeight(123);
            Assert.Equal(123, StateWriteContext.StampHeight);
            StateWriteContext.Clear();
            Assert.Equal(501, StateWriteContext.StampHeight);
        }

        // ── Tombstones ──────────────────────────────────────────────────────────────────────

        [Fact]
        public void DeleteSmartContract_WritesTombstone()
        {
            StateWriteContext.SetHeight(88);
            try
            {
                var sc = NewContract("sc:doomed");
                SmartContractStateTrei.SaveSmartContract(sc);
                SmartContractStateTrei.DeleteSmartContract(sc);

                var tombstones = StateTombstone.GetTombstones().FindAll().ToList();
                var ts = Assert.Single(tombstones);
                Assert.Equal(StateTombstone.COLL_SCSTATE, ts.SourceColl);
                Assert.Equal("sc:doomed", ts.Key);
                Assert.Equal(88, ts.Height);
            }
            finally
            {
                StateWriteContext.Clear();
            }
        }

        // ── Rotation / diffing ──────────────────────────────────────────────────────────────

        [Fact]
        public async Task UpdateCycle_RotatesThreeSlots_AndDiffCopiesChanges()
        {
            SeedAccount("alice", 100m, stamp: 35);
            SeedAccount("bob", 50m, stamp: 35);

            await StateSnapshotService.UpdateCycleAsync(40); // slot A full copy @40
            await StateSnapshotService.UpdateCycleAsync(50); // slot B full copy @50
            await StateSnapshotService.UpdateCycleAsync(60); // slot C full copy @60

            var heights = StateSnapshotService.GetSlots().Select(x => x.Height).OrderBy(x => x).ToArray();
            Assert.Equal(new long[] { 40, 50, 60 }, heights);
            Assert.All(StateSnapshotService.GetSlots(), s => Assert.Equal(SnapshotSlotStatus.Valid, s.Status));

            // Mutate alice at height 65 — only the slot that rolls forward past 65 sees it.
            SeedAccount("alice", 999m, stamp: 65);

            await StateSnapshotService.UpdateCycleAsync(70); // oldest slot (40) rolls to 70 via diff

            var slots = StateSnapshotService.GetSlots();
            var rolled = slots.Single(x => x.Height == 70);
            var stale = slots.Single(x => x.Height == 50);

            Assert.Equal(999m, SlotBalance(rolled.SlotId, "alice"));
            Assert.Equal(100m, SlotBalance(stale.SlotId, "alice"));
            Assert.Equal(50m, SlotBalance(rolled.SlotId, "bob")); // untouched record still present after diff
        }

        [Fact]
        public async Task UpdateCycle_PropagatesDeletionsViaTombstones_AndPrunes()
        {
            StateWriteContext.SetHeight(35);
            SmartContractStateTrei.SaveSmartContract(NewContract("sc:burn"));
            StateWriteContext.Clear();

            await StateSnapshotService.UpdateCycleAsync(40);
            await StateSnapshotService.UpdateCycleAsync(50);
            await StateSnapshotService.UpdateCycleAsync(60);

            // Burn at height 65 — deletion must reach the slot rolling 40 → 70.
            StateWriteContext.SetHeight(65);
            SmartContractStateTrei.DeleteSmartContract(NewContract("sc:burn"));
            StateWriteContext.Clear();

            await StateSnapshotService.UpdateCycleAsync(70);

            var rolled = StateSnapshotService.GetSlots().Single(x => x.Height == 70);
            Assert.Equal(0, SlotCount(rolled.SlotId, "scstate", "sc:burn"));

            // Slots at 50/60 still predate the burn and legitimately keep the record.
            var stale = StateSnapshotService.GetSlots().Single(x => x.Height == 50);
            Assert.Equal(1, SlotCount(stale.SlotId, "scstate", "sc:burn"));

            // Once every slot has rolled past the tombstone height it gets pruned.
            await StateSnapshotService.UpdateCycleAsync(80); // 50 → 80
            await StateSnapshotService.UpdateCycleAsync(90); // 60 → 90
            Assert.Equal(0, StateTombstone.GetTombstones().Count());

            Assert.All(StateSnapshotService.GetSlots(),
                s => Assert.Equal(0, SlotCount(s.SlotId, "scstate", "sc:burn")));
        }

        [Fact]
        public async Task UpdateCycle_SkipsWhenStateNotSynced()
        {
            StateTreiStatusService.SetFailed("test: dirty state");
            await StateSnapshotService.UpdateCycleAsync(40);
            Assert.False(StateSnapshotService.HasValidSlot());
        }

        // ── Restore-slot selection ──────────────────────────────────────────────────────────

        [Fact]
        public async Task PickSlotForRestore_ChoosesNewestSlotAtOrBelowTarget()
        {
            SeedAccount("alice", 100m, stamp: 35);
            await StateSnapshotService.UpdateCycleAsync(40);
            await StateSnapshotService.UpdateCycleAsync(50);
            await StateSnapshotService.UpdateCycleAsync(60);

            Assert.Equal(50, StateSnapshotService.PickSlotForRestore(55)!.Height);
            Assert.Equal(60, StateSnapshotService.PickSlotForRestore(60)!.Height);
            Assert.Equal(40, StateSnapshotService.PickSlotForRestore(41)!.Height);
            Assert.Null(StateSnapshotService.PickSlotForRestore(39)); // below oldest → ResetTreis fallback
        }

        [Fact]
        public async Task InvalidateAbove_ExcludesForkTaintedSlots()
        {
            SeedAccount("alice", 100m, stamp: 35);
            await StateSnapshotService.UpdateCycleAsync(40);
            await StateSnapshotService.UpdateCycleAsync(50);
            await StateSnapshotService.UpdateCycleAsync(60);

            StateSnapshotService.InvalidateAbove(45);

            Assert.Equal(40, StateSnapshotService.PickSlotForRestore(60)!.Height);
            Assert.Equal(2, StateSnapshotService.GetSlots().Count(x => x.Status == SnapshotSlotStatus.Invalid));
        }

        [Fact]
        public async Task PickVerifiedSlot_RejectsForkTaintedSnapshot_ByBlockHashAnchor()
        {
            // Block store lives in DbContext.DB — give it a temp home for this test.
            DbContext.DB = _live;
            try
            {
                var blocks = _live.GetCollection<Block>(DbContext.RSRV_BLOCKS);
                blocks.Insert(new Block { Height = 40, Hash = "canonical-hash-40", Transactions = new List<Transaction>() });
                blocks.Insert(new Block { Height = 50, Hash = "canonical-hash-50", Transactions = new List<Transaction>() });

                SeedAccount("alice", 100m, stamp: 35);
                await StateSnapshotService.UpdateCycleAsync(40, "canonical-hash-40");
                await StateSnapshotService.UpdateCycleAsync(50, "FORK-hash-50"); // taken on a fork branch

                // Height-only picker would happily hand back the fork-tainted slot at 50...
                Assert.Equal(50, StateSnapshotService.PickSlotForRestore(55)!.Height);

                // ...the verified picker rejects it (hash mismatch vs block store), invalidates it,
                // and falls back to the older clean slot.
                var verified = StateSnapshotService.PickVerifiedSlotForRestore(55);
                Assert.NotNull(verified);
                Assert.Equal(40, verified!.Height);
                Assert.Contains(StateSnapshotService.GetSlots(), s => s.Height == 50 && s.Status == SnapshotSlotStatus.Invalid);

                // A slot whose anchor block is missing from the store entirely is also rejected.
                blocks.DeleteMany(x => x.Height == 40);
                Assert.Null(StateSnapshotService.PickVerifiedSlotForRestore(55));
            }
            finally
            {
                DbContext.DB = null;
            }
        }

        [Fact]
        public async Task CrashMidUpdate_SlotLeftUpdating_IsSkippedAndRebuilt()
        {
            SeedAccount("alice", 100m, stamp: 35);
            await StateSnapshotService.UpdateCycleAsync(40);
            await StateSnapshotService.UpdateCycleAsync(50);

            // Simulate a crash mid-cycle: slot stuck in Updating.
            var manifest = SnapshotManifest.GetManifest();
            var victim = StateSnapshotService.GetSlots().Single(x => x.Height == 40);
            victim.Status = SnapshotSlotStatus.Updating;
            manifest.Upsert(victim);

            Assert.Equal(50, StateSnapshotService.PickSlotForRestore(100)!.Height); // Updating slot skipped

            // Next cycle prefers the broken slot and rebuilds it via full copy.
            await StateSnapshotService.UpdateCycleAsync(60);
            var rebuilt = StateSnapshotService.GetSlots().Single(x => x.SlotId == victim.SlotId);
            Assert.Equal(SnapshotSlotStatus.Valid, rebuilt.Status);
            Assert.Equal(60, rebuilt.Height);
            Assert.Equal(100m, SlotBalance(rebuilt.SlotId, "alice"));
        }

        // ── Restore round-trip ──────────────────────────────────────────────────────────────

        [Fact]
        public async Task RestoreSlotToLive_RoundTripsAllRecords()
        {
            SeedAccount("alice", 100m, stamp: 35);
            StateWriteContext.SetHeight(35);
            SmartContractStateTrei.SaveSmartContract(NewContract("sc:keep"));
            StateWriteContext.Clear();

            await StateSnapshotService.UpdateCycleAsync(40);
            var slot = StateSnapshotService.PickSlotForRestore(40)!;

            // Diverge live state after the snapshot, then wipe it (as TryRestoreAsync does).
            SeedAccount("alice", 1m, stamp: 45);
            SeedAccount("mallory", 666m, stamp: 45);
            Accounts().DeleteAllSafe();
            Contracts().DeleteAllSafe();

            Assert.True(StateSnapshotService.RestoreSlotToLive(slot.SlotId));

            var alice = Accounts().FindOne(x => x.Key == "alice");
            Assert.Equal(100m, alice.Balance);
            Assert.Null(Accounts().FindOne(x => x.Key == "mallory"));
            Assert.NotNull(Contracts().FindOne(x => x.SmartContractUID == "sc:keep"));
        }

        [Fact]
        public async Task Bootstrap_NoOpsOnceAValidSlotExists()
        {
            SeedAccount("alice", 100m, stamp: 35);
            Globals.LastBlock = new Block { Height = 40 };

            await StateSnapshotService.BootstrapAsync();
            Assert.True(StateSnapshotService.HasValidSlot());
            var height = StateSnapshotService.GetSlots().Single(x => x.Status == SnapshotSlotStatus.Valid).Height;
            Assert.Equal(40, height);

            Globals.LastBlock = new Block { Height = 90 };
            await StateSnapshotService.BootstrapAsync(); // must not touch existing slots
            Assert.Equal(height, StateSnapshotService.GetSlots().Single(x => x.Status == SnapshotSlotStatus.Valid).Height);
        }

        // ── Helpers ─────────────────────────────────────────────────────────────────────────

        private void SeedAccount(string key, decimal balance, long stamp)
        {
            StateWriteContext.SetHeight(stamp);
            try
            {
                var col = Accounts();
                var existing = col.FindOne(x => x.Key == key);
                if (existing == null)
                {
                    col.InsertSafe(NewAccount(key, balance));
                }
                else
                {
                    existing.Balance = balance;
                    col.UpdateSafe(existing);
                }
            }
            finally
            {
                StateWriteContext.Clear();
            }
        }

        private decimal SlotBalance(int slotId, string key)
        {
            var col = _snap.GetCollection($"s{slotId}_astate");
            var doc = col.FindOne("$.Key = @0", key);
            Assert.NotNull(doc);
            return doc["Balance"].AsDecimal;
        }

        private int SlotCount(int slotId, string tag, string scUid)
        {
            var col = _snap.GetCollection($"s{slotId}_{tag}");
            return col.Count("$.SmartContractUID = @0", scUid);
        }
    }
}
