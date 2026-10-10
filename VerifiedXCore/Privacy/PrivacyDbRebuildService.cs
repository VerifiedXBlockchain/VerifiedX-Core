using LiteDB;
using VerifiedXCore.Data;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Models.Privacy;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Privacy
{
    /// <summary>
    /// Replays shielded state from chain data using <see cref="PrivateTxPayload"/> in private transactions.
    /// </summary>
    public static class PrivacyDbRebuildService
    {
        public static Task<(bool Success, string Message)> TryRebuildFromBlocksAsync(CancellationToken cancellationToken = default) =>
            TryRebuildFromBlocksAsync(PrivacyDbContext.GetPrivacyDb(), cancellationToken);

        public static Task<(bool Success, string Message)> TryRebuildFromBlocksAsync(
            LiteDatabase privacyDb,
            CancellationToken cancellationToken = default,
            Action<string>? progress = null)
        {
            var col = BlockchainData.GetBlocks();
            if (col == null)
                return Task.FromResult((false, "Blockchain blocks collection unavailable."));
            // One streaming pass in height order (ToList() of every block exhausted memory on mainnet); only the blocks
            // that carry a private transaction are kept, which is a handful.
            // The tip is the highest STORED block, taken from the same pass. Never Globals.LastBlock: this runs at startup
            // before the tip is loaded (it is still Height -1 there), and with that the epoch reset for a chain that is
            // past the proof height with no private transaction since was silently skipped (fourth review, same cause as
            // the burn catch-up that never ran).
            var blocks = CollectBlocksWithPrivateTransactions(col.Query().OrderBy(x => x.Height).ToEnumerable(), out var storedTip, cancellationToken, progress);
            return TryReplayPrivateBlocksAsync(blocks, privacyDb, cancellationToken, storedTip);
        }

        /// <summary>The blocks of <paramref name="blocksInHeightOrder"/> that carry at least one private transaction.</summary>
        public static List<Block> CollectBlocksWithPrivateTransactions(IEnumerable<Block> blocksInHeightOrder, CancellationToken cancellationToken = default, Action<string>? progress = null) =>
            CollectBlocksWithPrivateTransactions(blocksInHeightOrder, out _, cancellationToken, progress);

        /// <summary>As above; <paramref name="highestHeight"/> is the height of the last block seen (-1 for an empty chain).</summary>
        public static List<Block> CollectBlocksWithPrivateTransactions(IEnumerable<Block> blocksInHeightOrder, out long highestHeight, CancellationToken cancellationToken = default, Action<string>? progress = null)
        {
            var kept = new List<Block>();
            long scanned = 0;
            highestHeight = -1;
            foreach (var block in blocksInHeightOrder)
            {
                cancellationToken.ThrowIfCancellationRequested();
                scanned++;
                if (block.Height > highestHeight)
                    highestHeight = block.Height;
                if (progress != null && scanned % 500_000 == 0)
                    progress($"privacy rebuild: scanned {scanned} blocks (height {block.Height}), {kept.Count} carry private transactions");
                if (block.Transactions != null && block.Transactions.Any(t => PrivateTransactionTypes.IsPrivateTransaction(t.TransactionType)))
                    kept.Add(block);
            }
            progress?.Invoke($"privacy rebuild: scanned {scanned} blocks, {kept.Count} carry private transactions");
            return kept;
        }

        /// <summary>Marker written next to the databases once this build has rebuilt the privacy store from the chain.</summary>
        /// <summary>
        /// Versioned: v1 was the 8.2.0 rebuild (stage 1); v2 forces one more rebuild for stage 3, whose epoch tree differs (the
        /// owner-bound dummy leaf), so a node that crossed the proof height on an earlier build gets the right tree.
        /// </summary>
        /// <remarks>v3 (fourth review): the v2 rebuild read the tip from Globals.LastBlock at startup (-1), so on a node past the
        /// proof height it could finish without resetting the pool; every node rebuilds once more with the stored tip.</remarks>
        public const string RebuiltMarkerFileName = "DB_Privacy.rebuilt-v3";

        /// <summary>
        /// Fund-loss audit item 1 (stage 1): the shielded pool supply becomes consensus state at
        /// Globals.PrivateTxSupplyRulesHeight, so every node must hold the figure the chain implies. DB_Privacy was never
        /// rebuilt in the field and older builds replayed state without wiping it, so nodes drifted (one mainnet node
        /// recorded -100,025.80 VFX where the chain implies -100,028.90). This runs the rebuild once per database
        /// folder, synchronously at startup before any networking, and leaves a marker so later starts skip it.
        /// A failed rebuild leaves no marker (retried next start) and is logged as an error.
        /// </summary>
        public static async Task EnsureRebuiltOnceAtStartupAsync(Action<string>? log = null)
        {
            log ??= Console.WriteLine;
            string markerPath;
            try { markerPath = Path.Combine(GetPathUtility.GetDatabasePath(), RebuiltMarkerFileName); }
            catch (Exception ex) { ErrorLogUtility.LogError($"Privacy rebuild: database path unavailable: {ex.Message}", "PrivacyDbRebuildService.EnsureRebuiltOnceAtStartupAsync()"); return; }
            if (File.Exists(markerPath))
                return;
            log("Privacy store: one-time rebuild from the chain (fund-loss audit item 1); this scans every block once...");
            var started = DateTime.UtcNow;
            var (ok, message) = await TryRebuildFromBlocksAsync(PrivacyDbContext.GetPrivacyDb(), default, log).ConfigureAwait(false);
            if (!ok)
            {
                ErrorLogUtility.LogError($"Privacy store rebuild FAILED (will retry at next start): {message}", "PrivacyDbRebuildService.EnsureRebuiltOnceAtStartupAsync()");
                log($"Privacy store rebuild FAILED: {message}");
                return;
            }
            try { File.WriteAllText(markerPath, $"{DateTime.UtcNow:O} {message}"); }
            catch (Exception ex) { ErrorLogUtility.LogError($"Privacy rebuild: could not write marker {markerPath}: {ex.Message}", "PrivacyDbRebuildService.EnsureRebuiltOnceAtStartupAsync()"); }
            // The rebuild rewrote the privacy collections outside block application, so every existing state snapshot now
            // holds a privacy store that differs from the live one; a fork recovery restoring one would bring the old pool
            // back, and nothing would rebuild it again. They are marked invalid and are re-taken by full copy from here.
            try
            {
                Services.StateSnapshotService.InvalidateAbove(-1);
                log("Privacy store: state snapshots taken before the rebuild were invalidated; new ones are taken from the rebuilt state.");
            }
            catch (Exception ex) { ErrorLogUtility.LogError($"Privacy rebuild: could not invalidate state snapshots: {ex.Message}", "PrivacyDbRebuildService.EnsureRebuiltOnceAtStartupAsync()"); }
            var vfx = ShieldedPoolService.GetState("VFX");
            log($"Privacy store rebuilt in {(DateTime.UtcNow - started).TotalSeconds:F0}s: {message} VFX pool supply {vfx?.TotalShieldedSupply ?? 0M}, commitments {vfx?.TotalCommitments ?? 0}.");
        }

        /// <summary>
        /// Wipes per-asset privacy rows, then replays private txs in block/tx order (nullifiers + spent marks + outputs per tx).
        /// </summary>
        public static Task<(bool Success, string Message)> TryReplayPrivateBlocksAsync(
            IReadOnlyList<Block> blocks,
            LiteDatabase privacyDb,
            CancellationToken cancellationToken = default,
            long? tipHeight = null)
        {
            if (privacyDb == null)
                return Task.FromResult((false, "privacyDb is required."));

            try
            {
                var affected = new HashSet<string>(StringComparer.Ordinal);

                foreach (var block in blocks.OrderBy(b => b.Height))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (block.Transactions == null || block.Transactions.Count == 0)
                        continue;

                    var ordered = PrivateTransactionTypes.OrderTransactionsForReplay(block.Transactions);
                    foreach (var tx in ordered)
                    {
                        if (!PrivateTransactionTypes.IsPrivateTransaction(tx.TransactionType))
                            continue;
                        if (!PrivateTxPayloadCodec.TryDecode(tx.Data, out var payload, out _))
                            continue;
                        if (payload == null || !payload.TryValidateStructure(out _))
                            continue;
                        affected.Add(payload.Asset);
                    }
                }

                var poolCol = privacyDb.GetCollection<ShieldedPoolState>(PrivacyDbContext.PRIV_POOL_STATE);

                foreach (var a in affected)
                    WipeAsset(privacyDb, a);

                long maxHeight = 0;
                var txCount = 0;
                var epochStarted = false;

                foreach (var block in blocks.OrderBy(b => b.Height))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (block.Transactions == null || block.Transactions.Count == 0)
                        continue;
                    // Stage 2: the pools restart at PrivateTxProofRulesHeight, before that block's transactions (as block
                    // application does). No private transaction is valid in the reset block itself.
                    if (!epochStarted && PrivacyEpoch.ProofRulesActive(block.Height))
                    {
                        PrivacyEpochService.ResetPools(privacyDb, Globals.PrivateTxProofRulesHeight, block.Timestamp);
                        epochStarted = true;
                    }
                    if (block.Height > maxHeight)
                        maxHeight = block.Height;

                    var ordered = PrivateTransactionTypes.OrderTransactionsForReplay(block.Transactions);
                    foreach (var tx in ordered)
                    {
                        if (!PrivateTransactionTypes.IsPrivateTransaction(tx.TransactionType))
                            continue;
                        if (!PrivateTxPayloadCodec.TryDecode(tx.Data, out var payload, out _))
                            continue;
                        if (payload == null || !payload.TryValidateStructure(out _))
                            continue;

                        PrivateTxLedgerService.ApplyPrivacyStore(tx, block, payload, privacyDb);
                        txCount++;
                    }
                }

                // A tip at/after the height with no private transaction since: the epoch still started there.
                if (!epochStarted && tipHeight.HasValue && PrivacyEpoch.ProofRulesActive(tipHeight.Value))
                {
                    PrivacyEpochService.ResetPools(privacyDb, Globals.PrivateTxProofRulesHeight, 0);
                    epochStarted = true;
                    if (maxHeight < Globals.PrivateTxProofRulesHeight) maxHeight = Globals.PrivateTxProofRulesHeight;
                }
                // Each pool is rooted in its own shape: the epoch's tree for the pools the epoch reset (VFX), the legacy
                // tree for every other (vBTC pools are history and stay as mined; rooting one as an epoch tree throws on its
                // legacy leaves and failed the whole rebuild).
                var assetsToRoot = new HashSet<string>(affected, StringComparer.Ordinal);
                if (epochStarted)
                    foreach (var a in PrivacyEpochService.ResetAssets) assetsToRoot.Add(a);
                bool FixedDepthFor(string asset) => epochStarted && PrivacyEpochService.IsEpochAsset(asset);

                foreach (var a in assetsToRoot)
                {
                    var row = poolCol.FindOne(x => x.AssetType == a);
                    var supply = row?.TotalShieldedSupply ?? 0m;
                    var store = new ShieldedMerkleStore(a, privacyDb, FixedDepthFor(a));
                    store.LoadLeavesFromCommitments();
                    store.RebuildAndPersistMerkleNodes();
                    store.UpdatePoolStateRoot(maxHeight, supply, store.LeafDigests.Count);
                }

                foreach (var a in assetsToRoot)
                {
                    if (privacyDb.GetCollection<CommitmentRecord>(PrivacyDbContext.PRIV_COMMITMENTS).Count(x => x.AssetType == a) > 0)
                        continue;
                    var row = poolCol.FindOne(x => x.AssetType == a);
                    var supply = row?.TotalShieldedSupply ?? 0m;
                    var empty = new ShieldedMerkleStore(a, privacyDb, FixedDepthFor(a));
                    empty.UpdatePoolStateRoot(maxHeight, supply, 0);
                }

                return Task.FromResult((true, $"Privacy replay: assets={affected.Count}, txs={txCount}."));
            }
            catch (Exception ex)
            {
                return Task.FromResult((false, ApiErrorText.For(ex)));
            }
        }

        private static void WipeAsset(LiteDatabase db, string assetType)
        {
            db.GetCollection<CommitmentRecord>(PrivacyDbContext.PRIV_COMMITMENTS).DeleteManySafe(x => x.AssetType == assetType);
            db.GetCollection<MerkleTreeNodeRecord>(PrivacyDbContext.PRIV_MERKLE_NODES).DeleteManySafe(x => x.AssetType == assetType);
            db.GetCollection<NullifierRecord>(PrivacyDbContext.PRIV_NULLIFIERS).DeleteManySafe(x => x.AssetType == assetType);
            db.GetCollection<ShieldedPoolState>(PrivacyDbContext.PRIV_POOL_STATE).DeleteManySafe(x => x.AssetType == assetType);
        }

        public static Task<(bool Success, string Message)> TryRebuildMerkleStateFromDbAsync(
            string assetType,
            LiteDatabase privacyDb,
            CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            if (string.IsNullOrWhiteSpace(assetType))
                return Task.FromResult((false, "assetType is required."));
            if (privacyDb == null)
                return Task.FromResult((false, "privacyDb is required."));

            try
            {
                var commitments = privacyDb.GetCollection<CommitmentRecord>(PrivacyDbContext.PRIV_COMMITMENTS);
                var count = commitments.Count(x => x.AssetType == assetType);

                var store = new ShieldedMerkleStore(assetType, privacyDb);
                store.LoadLeavesFromCommitments();
                store.RebuildAndPersistMerkleNodes();

                var poolCol = privacyDb.GetCollection<ShieldedPoolState>(PrivacyDbContext.PRIV_POOL_STATE);
                var existing = poolCol.FindOne(x => x.AssetType == assetType);
                var height = existing?.LastUpdateHeight ?? 0L;
                var supply = existing?.TotalShieldedSupply ?? 0m;
                store.UpdatePoolStateRoot(height, supply, count);

                var root = store.GetRootBytes();
                var rootB64 = root != null ? Convert.ToBase64String(root) : "";
                return Task.FromResult((true, $"Rebuilt Merkle for '{assetType}': commitments={count}, root={rootB64}"));
            }
            catch (Exception ex)
            {
                return Task.FromResult((false, ApiErrorText.For(ex)));
            }
        }
    }
}
