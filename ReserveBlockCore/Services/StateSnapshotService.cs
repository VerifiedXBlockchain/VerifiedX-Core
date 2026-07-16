using LiteDB;
using ReserveBlockCore.Bitcoin.Models;
using ReserveBlockCore.Data;
using ReserveBlockCore.Models;
using ReserveBlockCore.Privacy;
using ReserveBlockCore.Utilities;

namespace ReserveBlockCore.Services
{
    /// <summary>
    /// Maintains rotating snapshots of ALL chain-derived state in <c>rsrvsnapshot.db</c> so fork
    /// and crash recovery can restore state from a near-tip snapshot and replay a handful of
    /// blocks instead of wiping everything and replaying the entire chain from genesis
    /// (ResetTreis, ~45 min on mainnet).
    ///
    /// Design:
    /// - 3 slots, refreshed every <see cref="SnapshotCadence"/> blocks (steady state heights
    ///   H, H-10, H-20). Forks are bounded at depth ≤10, so the H-10/H-20 slots always cover a
    ///   fork target even when the newest slot is fork-tainted or mid-update.
    /// - The two big treis (account state, SC state) are diff-copied via their
    ///   <c>LastModifiedHeight</c> stamp (see StateTreiStampExtensions) plus deletion tombstones;
    ///   every other chain-derived collection is small (&lt;1 MB) and fully re-copied each cycle.
    /// - Crash safety without transactions: a slot is marked Updating (checkpointed) BEFORE data
    ///   is written and marked Valid with its new height (checkpointed) as the LAST step. A crash
    ///   mid-cycle leaves that slot Updating (= unusable, rebuilt by full copy next cycle) while
    ///   the other slots stay Valid.
    /// </summary>
    public static class StateSnapshotService
    {
        public const int SnapshotCadence = 10;
        private const long MinSnapshotHeight = 30;
        private static volatile bool _isSnapshotting = false;

        public static bool IsSnapshotting => _isSnapshotting;

        private sealed class SnapColl
        {
            public string Tag = "";
            public Func<LiteDatabase?> SourceDb = () => null;
            public string SourceColl = "";
            /// <summary>Diff-tracked via LastModifiedHeight; otherwise fully re-copied each cycle.</summary>
            public bool DiffTracked;
            /// <summary>Record key field used to apply deletion tombstones (diff-tracked only).</summary>
            public string? KeyField;
            /// <summary>Tombstone source tag (StateTombstone.SourceColl) for this collection.</summary>
            public string? TombstoneTag;
        }

        /// <summary>
        /// The definitive snapshot set — every chain-derived collection wiped by
        /// BlockRollbackUtility.WipeChainDerivedState must be represented here, or a restore
        /// would leave it empty with only the replayed tail re-populated.
        /// Local/secret collections (wallets, keys, FROST material, arbiter shares, bridge
        /// mint tracking, shielded wallets, reserve account keys) are deliberately absent.
        /// </summary>
        private static readonly SnapColl[] Specs =
        {
            new SnapColl { Tag = "astate", SourceDb = () => DbContext.DB_AccountStateTrei, SourceColl = DbContext.RSRV_ASTATE_TREI, DiffTracked = true, KeyField = "Key", TombstoneTag = StateTombstone.COLL_ASTATE },
            new SnapColl { Tag = "scstate", SourceDb = () => DbContext.DB_SmartContractStateTrei, SourceColl = DbContext.RSRV_SCSTATE_TREI, DiffTracked = true, KeyField = "SmartContractUID", TombstoneTag = StateTombstone.COLL_SCSTATE },
            new SnapColl { Tag = "wstate", SourceDb = () => DbContext.DB_WorldStateTrei, SourceColl = DbContext.RSRV_WSTATE_TREI },
            new SnapColl { Tag = "decshop", SourceDb = () => DbContext.DB_DecShopStateTrei, SourceColl = DbContext.RSRV_DECSHOPSTATE_TREI },
            new SnapColl { Tag = "dnr", SourceDb = () => DbContext.DB_DNR, SourceColl = DbContext.RSRV_DNR },
            new SnapColl { Tag = "btcdnr", SourceDb = () => DbContext.DB_DNR, SourceColl = DbContext.RSRV_BITCOIN_ADNR },
            new SnapColl { Tag = "topic", SourceDb = () => DbContext.DB_TopicTrei, SourceColl = DbContext.RSRV_TOPIC_TREI },
            new SnapColl { Tag = "vote", SourceDb = () => DbContext.DB_Vote, SourceColl = DbContext.RSRV_VOTE },
            new SnapColl { Tag = "tokenvote", SourceDb = () => DbContext.DB_Wallet, SourceColl = DbContext.RSRV_TOKEN_VOTE },
            new SnapColl { Tag = "rsrvtx", SourceDb = () => DbContext.DB_Reserve, SourceColl = DbContext.RSRV_RESERVE_TRANSACTIONS },
            new SnapColl { Tag = "rsrvtxcb", SourceDb = () => DbContext.DB_Reserve, SourceColl = DbContext.RSRV_RESERVE_TRANSACTIONS_CALLED_BACK },
            new SnapColl { Tag = "tokwd", SourceDb = () => DbContext.DB_TokenizedWithdrawals, SourceColl = DbContext.RSRV_TOKENIZED_WITHDRAWALS },
            new SnapColl { Tag = "vbtcwd", SourceDb = () => DbContext.DB_VBTCWithdrawalRequests, SourceColl = DbContext.RSRV_VBTC_WITHDRAWAL_REQUESTS },
            new SnapColl { Tag = "bridgelocks", SourceDb = () => DbContext.DB_VBTCWithdrawalRequests, SourceColl = VBTCBridgeLockState.CollectionName },
            new SnapColl { Tag = "bridgeexits", SourceDb = () => DbContext.DB_VBTCWithdrawalRequests, SourceColl = VBTCBridgeBtcExitState.CollectionName },
            new SnapColl { Tag = "vbtcv2c", SourceDb = () => DbContext.DB_vBTC, SourceColl = DbContext.RSRV_VBTC_V2_CONTRACTS },
            new SnapColl { Tag = "vbtcv2x", SourceDb = () => DbContext.DB_vBTC, SourceColl = DbContext.RSRV_VBTC_V2_CANCELLATIONS },
            // Shielded pool chain state (~229 KB today). CommitmentRecord mutates (IsSpent), so
            // full-copy is required for correctness anyway; promote to diff-tracking if the pool
            // ever grows into the tens of MB.
            new SnapColl { Tag = "privcomm", SourceDb = () => DbContext.DB_Privacy, SourceColl = PrivacyDbContext.PRIV_COMMITMENTS },
            new SnapColl { Tag = "privnull", SourceDb = () => DbContext.DB_Privacy, SourceColl = PrivacyDbContext.PRIV_NULLIFIERS },
            new SnapColl { Tag = "privpool", SourceDb = () => DbContext.DB_Privacy, SourceColl = PrivacyDbContext.PRIV_POOL_STATE },
            new SnapColl { Tag = "privmerkle", SourceDb = () => DbContext.DB_Privacy, SourceColl = PrivacyDbContext.PRIV_MERKLE_NODES },
        };

        private static string SlotCollName(int slot, string tag) => $"s{slot}_{tag}";

        // ── Manifest ────────────────────────────────────────────────────────────────────────

        /// <summary>Returns all slot manifests, creating Empty entries on first use.</summary>
        public static List<SnapshotManifest> GetSlots()
        {
            var col = SnapshotManifest.GetManifest();
            if (col == null) return new List<SnapshotManifest>();

            var slots = col.FindAll().ToList();
            for (int i = 1; i <= SnapshotManifest.SlotCount; i++)
            {
                if (slots.All(x => x.SlotId != i))
                {
                    var slot = new SnapshotManifest { SlotId = i, Height = 0, Status = SnapshotSlotStatus.Empty, UpdatedUtc = DateTime.UtcNow };
                    col.Upsert(slot);
                    slots.Add(slot);
                }
            }
            return slots.OrderBy(x => x.SlotId).ToList();
        }

        public static bool HasValidSlot() => GetSlots().Any(IsUsable);

        private static bool IsUsable(SnapshotManifest slot) =>
            slot.Status == SnapshotSlotStatus.Valid && slot.SchemaVersion == SnapshotManifest.CurrentSchemaVersion;

        /// <summary>Newest usable slot at or below the rollback target, or null (caller falls back to ResetTreis).</summary>
        public static SnapshotManifest? PickSlotForRestore(long targetHeight) =>
            GetSlots().Where(x => IsUsable(x) && x.Height <= targetHeight)
                      .OrderByDescending(x => x.Height)
                      .FirstOrDefault();

        /// <summary>
        /// FORK-TAINT GUARD: True when the slot's recorded block hash matches the block currently
        /// in the local store at the slot's height. Height comparison alone cannot detect a
        /// snapshot taken on a fork branch that ran deeper than the rollback target — the hash
        /// anchor can. Legacy slots without a recorded hash pass (height check only).
        /// </summary>
        public static bool VerifySlotAnchor(SnapshotManifest slot)
        {
            if (string.IsNullOrEmpty(slot.BlockHash))
                return true;

            try
            {
                var anchor = BlockchainData.GetBlockByHeight(slot.Height);
                return anchor != null && anchor.Hash == slot.BlockHash;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"[Snapshot] Anchor verification failed for slot {slot.SlotId}: {ex.Message}", "StateSnapshotService.VerifySlotAnchor");
                return false; // can't prove it's clean → don't use it
            }
        }

        /// <summary>
        /// Newest usable slot at or below the target whose block-hash anchor matches the local
        /// block store. Slots that fail anchor verification are marked Invalid (they were taken
        /// on a fork branch) and the next-older slot is tried. Null → caller falls back to ResetTreis.
        /// </summary>
        public static SnapshotManifest? PickVerifiedSlotForRestore(long targetHeight)
        {
            var candidates = GetSlots()
                .Where(x => IsUsable(x) && x.Height <= targetHeight)
                .OrderByDescending(x => x.Height);

            var manifest = SnapshotManifest.GetManifest();
            foreach (var slot in candidates)
            {
                if (VerifySlotAnchor(slot))
                    return slot;

                LogUtility.Log(
                    $"[Snapshot] Slot {slot.SlotId} (height {slot.Height}) failed block-hash anchor check — " +
                    $"snapshot was taken on a fork branch. Invalidating and trying an older slot.",
                    "StateSnapshotService.PickVerifiedSlotForRestore");
                slot.Status = SnapshotSlotStatus.Invalid;
                slot.UpdatedUtc = DateTime.UtcNow;
                manifest?.Upsert(slot);
            }
            TryCheckpoint();
            return null;
        }

        /// <summary>Marks slots above the given height Invalid (their state includes rolled-back blocks).</summary>
        public static void InvalidateAbove(long height)
        {
            var col = SnapshotManifest.GetManifest();
            if (col == null) return;
            foreach (var slot in GetSlots().Where(x => x.Status == SnapshotSlotStatus.Valid && x.Height > height))
            {
                slot.Status = SnapshotSlotStatus.Invalid;
                slot.UpdatedUtc = DateTime.UtcNow;
                col.Upsert(slot);
            }
            TryCheckpoint();
        }

        // ── Snapshot creation ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Per-cadence roll-forward, called synchronously from the block commit path (inside the
        /// block-processing lock, so no UpdateTreis can run concurrently). Rolls the oldest slot
        /// forward to <paramref name="height"/> — a diff cycle when the slot is usable, a full
        /// copy when it is Empty/Invalid/stale-schema.
        /// </summary>
        public static Task UpdateCycleAsync(long height, string? blockHash = null)
        {
            if (_isSnapshotting) return Task.CompletedTask;
            if (height < MinSnapshotHeight) return Task.CompletedTask;
            if (DbContext.DB_Snapshot == null) return Task.CompletedTask;

            if (Globals.TreisUpdating)
            {
                // Should be impossible from the commit-path call site; never copy mid-mutation.
                LogUtility.Log($"[Snapshot] Skipped cycle at {height}: TreisUpdating was true.", "StateSnapshotService");
                return Task.CompletedTask;
            }

            // Never snapshot state that is flagged dirty/incomplete — a snapshot of corrupt
            // state would just make recovery restore the corruption.
            if (!StateTreiStatusService.IsSynced())
            {
                LogUtility.Log($"[Snapshot] Skipped cycle at {height}: state trei not flagged synced.", "StateSnapshotService");
                return Task.CompletedTask;
            }

            _isSnapshotting = true;
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var slots = GetSlots();

                // Prefer rebuilding a broken/stale slot; otherwise roll the oldest forward.
                var slot = slots.FirstOrDefault(x => !IsUsable(x))
                        ?? slots.OrderBy(x => x.Height).First();

                var fullCopy = !IsUsable(slot) || slot.Height <= 0;
                var previousHeight = slot.Height;

                MarkSlot(slot, SnapshotSlotStatus.Updating, previousHeight, slot.BlockHash);

                foreach (var spec in Specs)
                {
                    if (fullCopy || !spec.DiffTracked)
                        FullCopy(spec, slot.SlotId);
                    else
                        DiffCopy(spec, slot.SlotId, previousHeight);
                }

                // Anchor the slot to the block it was taken at so restore can detect a snapshot
                // taken on a fork branch (see VerifySlotAnchor). Best-effort when the caller
                // didn't pass the hash — a null anchor just means restore skips the hash check.
                var anchorHash = blockHash;
                if (anchorHash == null)
                {
                    try { anchorHash = BlockchainData.GetBlockByHeight(height)?.Hash; } catch { }
                }
                MarkSlot(slot, SnapshotSlotStatus.Valid, height, anchorHash);
                PruneTombstones();

                sw.Stop();
                LogUtility.Log(
                    $"[Snapshot] Slot {slot.SlotId} {(fullCopy ? "full-copied" : $"rolled forward from {previousHeight}")} to height {height} in {sw.ElapsedMilliseconds} ms.",
                    "StateSnapshotService");
            }
            catch (Exception ex)
            {
                // Snapshotting must never break block processing. The slot stays Updating and
                // will be rebuilt via full copy on the next cycle.
                ErrorLogUtility.LogError($"[Snapshot] Update cycle at height {height} failed: {ex}", "StateSnapshotService.UpdateCycleAsync");
            }
            finally
            {
                _isSnapshotting = false;
            }
            return Task.CompletedTask;
        }

        /// <summary>
        /// One-time full copy into a slot when no usable slot exists (first run after upgrade, or
        /// right after a successful ResetTreis so the genesis-replay work is never repeated).
        /// Gated on StateTreiStatus being synced. No-op when a usable slot already exists.
        /// </summary>
        public static Task BootstrapAsync()
        {
            if (HasValidSlot()) return Task.CompletedTask;
            var height = Globals.LastBlock.Height;
            if (height < MinSnapshotHeight) return Task.CompletedTask;
            LogUtility.Log($"[Snapshot] Bootstrapping first snapshot slot at height {height}...", "StateSnapshotService");
            return UpdateCycleAsync(height, Globals.LastBlock.Hash);
        }

        // ── Copy primitives (raw BSON so schema changes never break the snapshot layer) ──────

        private static void FullCopy(SnapColl spec, int slotId)
        {
            var sourceDb = spec.SourceDb();
            if (sourceDb == null || DbContext.DB_Snapshot == null) return;

            var source = sourceDb.GetCollection(spec.SourceColl);
            var dest = DbContext.DB_Snapshot.GetCollection(SlotCollName(slotId, spec.Tag));

            dest.DeleteAll();
            // Batch inserts keep LiteDB's per-operation memory bounded on the big treis.
            var batch = new List<BsonDocument>(2000);
            foreach (var doc in source.FindAll())
            {
                batch.Add(doc);
                if (batch.Count >= 2000)
                {
                    dest.Insert(batch);
                    batch.Clear();
                }
            }
            if (batch.Count > 0)
                dest.Insert(batch);
        }

        private static void DiffCopy(SnapColl spec, int slotId, long sinceHeight)
        {
            var sourceDb = spec.SourceDb();
            if (sourceDb == null || DbContext.DB_Snapshot == null) return;

            var source = sourceDb.GetCollection(spec.SourceColl);
            var dest = DbContext.DB_Snapshot.GetCollection(SlotCollName(slotId, spec.Tag));

            // Deletions first: a key deleted and re-created inside the window has a fresh record
            // in the diff below; applying tombstones after the upsert would wrongly remove it.
            var tombstones = StateTombstone.GetTombstones();
            if (tombstones != null && spec.KeyField != null && spec.TombstoneTag != null)
            {
                foreach (var ts in tombstones.Find(x => x.SourceColl == spec.TombstoneTag && x.Height > sinceHeight))
                    dest.DeleteMany($"$.{spec.KeyField} = @0", ts.Key);
            }

            foreach (var doc in source.Find(LiteDB.Query.GT("LastModifiedHeight", sinceHeight)))
                dest.Upsert(doc);
        }

        private static void MarkSlot(SnapshotManifest slot, SnapshotSlotStatus status, long height, string? blockHash)
        {
            var col = SnapshotManifest.GetManifest();
            if (col == null) return;
            slot.Status = status;
            slot.Height = height;
            slot.BlockHash = blockHash;
            slot.UpdatedUtc = DateTime.UtcNow;
            slot.SchemaVersion = SnapshotManifest.CurrentSchemaVersion;
            col.Upsert(slot);
            // Durability ordering is the crash-safety mechanism: Updating must hit disk before
            // slot data changes, Valid only after they are all written.
            TryCheckpoint();
        }

        private static void PruneTombstones()
        {
            try
            {
                var tombstones = StateTombstone.GetTombstones();
                if (tombstones == null) return;
                var usable = GetSlots().Where(IsUsable).ToList();
                if (usable.Count < SnapshotManifest.SlotCount) return; // every slot must consume a tombstone before it can go
                var minHeight = usable.Min(x => x.Height);
                tombstones.DeleteMany(x => x.Height <= minHeight);
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"[Snapshot] Tombstone prune failed: {ex.Message}", "StateSnapshotService.PruneTombstones");
            }
        }

        private static void TryCheckpoint()
        {
            try { DbContext.DB_Snapshot?.Checkpoint(); } catch { }
        }

        // ── Restore-side access (used by SnapshotRestoreUtility) ─────────────────────────────

        /// <summary>Copies every snapshot collection of the given slot back into its live source
        /// collection. Caller is responsible for wiping live chain-derived state first and for
        /// all guard flags. Returns false if any source DB handle is unavailable.</summary>
        public static bool RestoreSlotToLive(int slotId)
        {
            if (DbContext.DB_Snapshot == null) return false;

            foreach (var spec in Specs)
            {
                var sourceDb = spec.SourceDb();
                if (sourceDb == null)
                {
                    ErrorLogUtility.LogError($"[Snapshot] Restore aborted: live DB for '{spec.Tag}' unavailable.", "StateSnapshotService.RestoreSlotToLive");
                    return false;
                }

                var snap = DbContext.DB_Snapshot.GetCollection(SlotCollName(slotId, spec.Tag));
                var live = sourceDb.GetCollection(spec.SourceColl);

                var batch = new List<BsonDocument>(2000);
                foreach (var doc in snap.FindAll())
                {
                    batch.Add(doc);
                    if (batch.Count >= 2000)
                    {
                        live.Insert(batch);
                        batch.Clear();
                    }
                }
                if (batch.Count > 0)
                    live.Insert(batch);
            }
            return true;
        }
    }
}
