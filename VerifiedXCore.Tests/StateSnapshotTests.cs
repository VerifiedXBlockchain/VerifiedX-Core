using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LiteDB;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.Attestation;
using VerifiedXCore.Data;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
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
            DbContext.DB_BtcAttest = _live;
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
            DbContext.DB_BtcAttest = null;
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
        public async Task ARollback_DropsDeletionsFromTheDiscardedBlocks_AndKeepsTheOnesBelowIt()
        {
            DbContext.DB = _live; // anchored slots, so slot 40 diffs forward instead of being copied whole
            try
            {
                var blocks = _live.GetCollection<Block>(DbContext.RSRV_BLOCKS);
                StateWriteContext.SetHeight(35);
                SmartContractStateTrei.SaveSmartContract(NewContract("sc:nft"));
                SmartContractStateTrei.SaveSmartContract(NewContract("sc:burned"));
                StateWriteContext.Clear();
                foreach (var h in new long[] { 40, 50, 60 })
                {
                    blocks.Insert(new Block { Height = h, Hash = $"canon-{h}", Transactions = new List<Transaction>() });
                    await StateSnapshotService.UpdateCycleAsync(h, $"canon-{h}");
                }

                // Burned at 61 on the chain that stays; sc:nft burned at 65 on a branch that is then rolled back to 62.
                StateWriteContext.SetHeight(61);
                SmartContractStateTrei.DeleteSmartContract(NewContract("sc:burned"));
                StateWriteContext.SetHeight(65);
                SmartContractStateTrei.DeleteSmartContract(NewContract("sc:nft"));
                StateWriteContext.Clear();

                StateSnapshotService.InvalidateAbove(62);
                // The rollback brings sc:nft back with its original stamp, below every slot height.
                StateWriteContext.SetHeight(35);
                SmartContractStateTrei.SaveSmartContract(NewContract("sc:nft"));
                StateWriteContext.Clear();

                blocks.Insert(new Block { Height = 70, Hash = "canon-70", Transactions = new List<Transaction>() });
                await StateSnapshotService.UpdateCycleAsync(70, "canon-70"); // slot 40 diffs forward to 70

                var rolled = StateSnapshotService.GetSlots().Single(x => x.Height == 70);
                Assert.Equal(SnapshotSlotStatus.Valid, rolled.Status);
                Assert.Equal(1, SlotCount(rolled.SlotId, "scstate", "sc:nft"));
                Assert.Equal(0, SlotCount(rolled.SlotId, "scstate", "sc:burned"));
            }
            finally
            {
                DbContext.DB = null;
            }
        }

        [Fact]
        public void DeletionRecordsAboveTheStoredTip_AreDropped_AtOrBelowItKept()
        {
            var tombstones = StateTombstone.GetTombstones()!;
            tombstones.Insert(new StateTombstone { SourceColl = StateTombstone.COLL_SCSTATE, Key = "at-tip", Height = 60 });
            tombstones.Insert(new StateTombstone { SourceColl = StateTombstone.COLL_SCSTATE, Key = "orphan", Height = 61 });

            StateSnapshotService.DropDeletionRecordsAbove(60);

            Assert.Equal(new[] { "at-tip" }, tombstones.FindAll().Select(x => x.Key).ToArray());
        }

        [Fact]
        public async Task TheFirstStartAfterAFormatChange_DropsEveryDeletionRecord_AndLaterStartsKeepThem()
        {
            SeedAccount("alice", 100m, stamp: 35);
            await StateSnapshotService.UpdateCycleAsync(40);
            await StateSnapshotService.UpdateCycleAsync(50);
            var tombstones = StateTombstone.GetTombstones()!;
            tombstones.Insert(new StateTombstone { SourceColl = StateTombstone.COLL_SCSTATE, Key = "left-over", Height = 45 }); // below the tip

            // A later start with slots in the current format keeps the records.
            StateSnapshotService.ResetAfterFormatChange();
            Assert.Equal(1, tombstones.Count());

            // A slot written by an older build: the first start of this one drops them all and invalidates the slots.
            var old = StateSnapshotService.GetSlots().First(x => x.Status == SnapshotSlotStatus.Valid);
            old.SchemaVersion = SnapshotManifest.CurrentSchemaVersion - 1;
            SnapshotManifest.GetManifest()!.Upsert(old);

            StateSnapshotService.ResetAfterFormatChange();

            Assert.Equal(0, tombstones.Count());
            Assert.False(StateSnapshotService.HasValidSlot());
        }

        [Fact]
        public async Task UpdateCycle_SkipsWhenStateNotSynced()
        {
            StateTreiStatusService.SetFailed("test: dirty state");
            await StateSnapshotService.UpdateCycleAsync(40);
            Assert.False(StateSnapshotService.HasValidSlot());
        }

        [Fact]
        public async Task UpdateCycle_RunsWhenNoStatusRecordExists()
        {
            // Fresh-sync wallets build state via full validation from genesis and never run
            // ResetTreis, so no StateTreiStatus record exists — snapshots must still run.
            _live.GetCollection("state_trei_status").DeleteAll();
            Assert.Null(StateTreiStatusService.GetStatus());

            SeedAccount("alice", 100m, stamp: 35);
            await StateSnapshotService.UpdateCycleAsync(40);

            Assert.True(StateSnapshotService.HasValidSlot());
            Assert.Equal(40, StateSnapshotService.PickSlotForRestore(40)!.Height);
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

        /// <summary>
        /// A slot taken on a branch a fast rollback removed: its anchor block was replaced, so the next cycle rebuilds it
        /// rather than diffing forward from its height (which kept the dead branch's state and re-anchored it).
        /// </summary>
        [Fact]
        public async Task UpdateCycle_RebuildsASlotWhoseAnchorBlockWasReplaced()
        {
            DbContext.DB = _live;
            try
            {
                var blocks = _live.GetCollection<Block>(DbContext.RSRV_BLOCKS);
                blocks.Insert(new Block { Height = 40, Hash = "fork-40", Transactions = new List<Transaction>() });
                SeedAccount("alice", 100m, stamp: 35);
                await StateSnapshotService.UpdateCycleAsync(40, "fork-40");
                foreach (var h in new long[] { 50, 60 })
                {
                    blocks.Insert(new Block { Height = h, Hash = $"canon-{h}", Transactions = new List<Transaction>() });
                    await StateSnapshotService.UpdateCycleAsync(h, $"canon-{h}");
                }

                // Block 40 is rolled back and replaced; the replacement's write is stamped at 40.
                blocks.DeleteMany(x => x.Height == 40);
                blocks.Insert(new Block { Height = 40, Hash = "canon-40", Transactions = new List<Transaction>() });
                SeedAccount("alice", 70m, stamp: 40);
                blocks.Insert(new Block { Height = 70, Hash = "canon-70", Transactions = new List<Transaction>() });

                await StateSnapshotService.UpdateCycleAsync(70, "canon-70"); // rolls the oldest slot (40) forward

                var rolled = StateSnapshotService.GetSlots().Single(x => x.Height == 70);
                Assert.Equal(70m, SlotBalance(rolled.SlotId, "alice"));
            }
            finally
            {
                DbContext.DB = null;
            }
        }

        [Fact]
        public async Task ASlotFromBeforeTheAnchorCheck_IsNotRestored_AndIsRebuilt()
        {
            SeedAccount("alice", 100m, stamp: 35);
            await StateSnapshotService.UpdateCycleAsync(40);
            var manifest = SnapshotManifest.GetManifest()!;
            var old = StateSnapshotService.GetSlots().Single(x => x.Height == 40);
            old.SchemaVersion = 1; // written by a build before the anchor check
            manifest.Upsert(old);

            Assert.Null(StateSnapshotService.PickSlotForRestore(45));

            SeedAccount("alice", 70m, stamp: 40); // what the slot's diff from 40 would miss
            await StateSnapshotService.UpdateCycleAsync(50);
            await StateSnapshotService.UpdateCycleAsync(60);
            await StateSnapshotService.UpdateCycleAsync(70); // reaches the old slot: rebuilt, not diffed

            var slots = StateSnapshotService.GetSlots();
            Assert.All(slots.Where(x => x.Status == SnapshotSlotStatus.Valid), x => Assert.Equal(SnapshotManifest.CurrentSchemaVersion, x.SchemaVersion));
            Assert.Equal(70m, SlotBalance(slots.Single(x => x.SlotId == old.SlotId).SlotId, "alice"));
        }

        [Fact]
        public async Task UpdateCycle_StillRollsAnAnchoredSlotForwardOnALinearChain()
        {
            DbContext.DB = _live;
            try
            {
                var blocks = _live.GetCollection<Block>(DbContext.RSRV_BLOCKS);
                SeedAccount("alice", 100m, stamp: 35);
                foreach (var h in new long[] { 40, 50, 60 })
                {
                    blocks.Insert(new Block { Height = h, Hash = $"canon-{h}", Transactions = new List<Transaction>() });
                    await StateSnapshotService.UpdateCycleAsync(h, $"canon-{h}");
                }
                SeedAccount("alice", 999m, stamp: 65);
                blocks.Insert(new Block { Height = 70, Hash = "canon-70", Transactions = new List<Transaction>() });

                await StateSnapshotService.UpdateCycleAsync(70, "canon-70");

                var rolled = StateSnapshotService.GetSlots().Single(x => x.Height == 70);
                Assert.Equal(SnapshotSlotStatus.Valid, rolled.Status);
                Assert.Equal("canon-70", rolled.BlockHash);
                Assert.Equal(999m, SlotBalance(rolled.SlotId, "alice"));
                Assert.NotNull(StateSnapshotService.PickVerifiedSlotForRestore(70));
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

        /// <summary>The burned-UID retirements (C-169) are chain state: a snapshot holds them and a restore brings them back.</summary>
        [Fact]
        public async Task RetiredContractUids_AreSnapshot_AndRestored()
        {
            RetiredContractUid.Retire("abc:1", 35, "burn");
            await StateSnapshotService.UpdateCycleAsync(40);
            var slot = StateSnapshotService.PickSlotForRestore(40)!;
            _live.GetCollection(RetiredContractUid.CollectionName).DeleteAll();
            Assert.False(RetiredContractUid.IsRetired("abc:1"));

            Assert.True(StateSnapshotService.RestoreSlotToLive(slot.SlotId));

            Assert.True(RetiredContractUid.IsRetired("abc:1"));
        }

        /// <summary>
        /// The Bitcoin attestation tables are chain state: a snapshot holds them as they stood at its height, and a
        /// restore brings exactly that back (a coin spent after the snapshot is unspent again, a later header is gone).
        /// Most of them are copied by the LastModifiedHeight stamp, so the second cycle must pick up the later writes.
        /// </summary>
        [Fact]
        public async Task BitcoinAttestationTables_AreSnapshotByStamp_AndRestoredAsTheyStood()
        {
            Assert.True(BtcHeader.TryParse(new string('1', 160), out var first));
            Assert.True(BtcHeader.TryParse(new string('2', 160), out var second));
            SeedAccount("alice", 100m, stamp: 35);

            StateWriteContext.SetHeight(35);
            BtcAttestStore.SaveHeader(900, first!, 35);
            BtcAttestStore.SaveScript(new VaultDepositScriptRow { ScriptHex = "5120aa", SmartContractUID = "v:1", DepositAddress = "bc1p", CreatedAtVfxHeight = 35, WatchFromBitcoinHeight = 890 });
            var coin = new VaultCoinRow { Outpoint = "aa:0", SmartContractUID = "v:1", ValueSats = 5_000, Status = VaultCoinStatus.Unspent, CreatedInHeight = 900 };
            BtcAttestStore.SaveCoin(coin, null);
            BtcAttestStore.SaveMeta(new AttestationMetaRow { CheckpointHeight = 800, TipHeight = 900, TipHash = first!.Hash, EventsThroughHeight = 900 });
            BtcAttestStore.SaveSignerSet(new AttestationSignerSetRow { Epoch = 0, AtVfxHeight = 30, Addresses = new List<string> { "a", "b", "c" } });
            BtcAttestStore.SaveWithdrawal(new VaultWithdrawalRow { Ref = "req|v:1", SmartContractUID = "v:1", RequestHash = "req", Requester = "alice", AmountSats = 4_000, State = VaultWithdrawalState.Waiting, RequestVfxHeight = 35 });
            StateWriteContext.Clear();

            await StateSnapshotService.UpdateCycleAsync(40);
            var early = StateSnapshotService.PickSlotForRestore(40)!;

            // After the snapshot: the coin is spent, a header and a signer set are added, the tip moves.
            StateWriteContext.SetHeight(45);
            BtcAttestStore.SaveCoin(new VaultCoinRow { Outpoint = "aa:0", SmartContractUID = "v:1", ValueSats = 5_000, Status = VaultCoinStatus.Spent, CreatedInHeight = 900, SpentByTxid = "bb", SpentInHeight = 901 }, coin);
            BtcAttestStore.SaveHeader(901, second!, 45);
            BtcAttestStore.SaveMeta(new AttestationMetaRow { CheckpointHeight = 800, TipHeight = 901, TipHash = second!.Hash, EventsThroughHeight = 901 });
            BtcAttestStore.SaveSignerSet(new AttestationSignerSetRow { Epoch = 1, AtVfxHeight = 45, Addresses = new List<string> { "a", "b", "c", "d" } });
            var pinned = BtcAttestStore.Withdrawal("req|v:1")!;
            pinned.State = VaultWithdrawalState.Pinned;
            pinned.Pins.Add(new VaultPin { Txid = "bb", TxHex = "00", Inputs = new List<string> { "aa:0" }, DestinationSats = 3_000, FeeSats = 1_000, PinnedAtVfxHeight = 45 });
            BtcAttestStore.SaveWithdrawal(pinned);
            StateWriteContext.Clear();
            Assert.Equal(0, BtcAttestStore.Total("v:1").HeldSats);

            // A later cycle copies the rows written since, by their stamp.
            await StateSnapshotService.UpdateCycleAsync(50);
            var late = StateSnapshotService.PickSlotForRestore(50)!;
            Assert.NotEqual(early.SlotId, late.SlotId);

            // The restore path wipes chain state first (BlockRollbackUtility.WipeChainDerivedState).
            Accounts().DeleteAllSafe();
            BtcAttestStore.WipeAll();
            Assert.True(StateSnapshotService.RestoreSlotToLive(late.SlotId));
            Assert.Equal(VaultCoinStatus.Spent, BtcAttestStore.Coin("aa:0")!.Status);
            Assert.Equal(0, BtcAttestStore.Total("v:1").HeldSats);
            Assert.Equal(second.Hash, BtcAttestStore.HeaderAt(901)!.Hash);
            Assert.Equal(901, BtcAttestStore.GetMeta()!.TipHeight);
            Assert.Equal(4, BtcAttestStore.SignerSet(1)!.Addresses.Count);
            Assert.Equal("bb", Assert.Single(BtcAttestStore.Withdrawal("req|v:1")!.Pins).Txid);
            Assert.Empty(BtcAttestStore.FreeCoinsOf("v:1"));

            Accounts().DeleteAllSafe();
            BtcAttestStore.WipeAll();
            Assert.True(StateSnapshotService.RestoreSlotToLive(early.SlotId));
            Assert.Equal(VaultCoinStatus.Unspent, BtcAttestStore.Coin("aa:0")!.Status);
            Assert.Equal(5_000, BtcAttestStore.Total("v:1").HeldSats);
            Assert.Equal(first.Hash, BtcAttestStore.HeaderAt(900)!.Hash);
            Assert.Null(BtcAttestStore.HeaderAt(901));
            var meta = BtcAttestStore.GetMeta()!;
            Assert.Equal(900, meta.TipHeight);
            Assert.Equal(900, meta.EventsThroughHeight);
            Assert.Equal(new[] { "a", "b", "c" }, BtcAttestStore.SignerSet(0)!.Addresses);
            Assert.Null(BtcAttestStore.SignerSet(1));
            Assert.Equal(VaultWithdrawalState.Waiting, BtcAttestStore.Withdrawal("req|v:1")!.State);
            Assert.Empty(BtcAttestStore.Withdrawal("req|v:1")!.Pins);
            Assert.Equal("aa:0", Assert.Single(BtcAttestStore.FreeCoinsOf("v:1")).Outpoint); // found through its index after the restore
            Assert.Equal("v:1", BtcAttestStore.ScriptRow("5120aa")!.SmartContractUID);
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
