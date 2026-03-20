using LiteDB;
using ReserveBlockCore.Data;
using ReserveBlockCore.Extensions;
using ReserveBlockCore.Models;
using ReserveBlockCore.Models.Privacy;

namespace ReserveBlockCore.Privacy
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
            CancellationToken cancellationToken = default)
        {
            var col = BlockchainData.GetBlocks();
            if (col == null)
                return Task.FromResult((false, "Blockchain blocks collection unavailable."));
            var blocks = col.Query().OrderBy(x => x.Height).ToList();
            return TryReplayPrivateBlocksAsync(blocks, privacyDb, cancellationToken);
        }

        /// <summary>
        /// Wipes per-asset privacy rows, then replays private txs in block/tx order (nullifiers + spent marks + outputs per tx).
        /// </summary>
        public static Task<(bool Success, string Message)> TryReplayPrivateBlocksAsync(
            IReadOnlyList<Block> blocks,
            LiteDatabase privacyDb,
            CancellationToken cancellationToken = default)
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
                var supplyBackup = new Dictionary<string, decimal>(StringComparer.Ordinal);
                foreach (var a in affected)
                {
                    var existing = poolCol.FindOne(x => x.AssetType == a);
                    supplyBackup[a] = existing?.TotalShieldedSupply ?? 0m;
                }

                foreach (var a in affected)
                    WipeAsset(privacyDb, a);

                long maxHeight = 0;
                var txCount = 0;

                foreach (var block in blocks.OrderBy(b => b.Height))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (block.Transactions == null || block.Transactions.Count == 0)
                        continue;
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

                foreach (var a in affected)
                {
                    var supply = supplyBackup.TryGetValue(a, out var s) ? s : 0m;
                    var store = new ShieldedMerkleStore(a, privacyDb);
                    store.LoadLeavesFromCommitments();
                    store.RebuildAndPersistMerkleNodes();
                    store.UpdatePoolStateRoot(maxHeight, supply, store.LeafDigests.Count);
                }

                foreach (var a in affected)
                {
                    if (privacyDb.GetCollection<CommitmentRecord>(PrivacyDbContext.PRIV_COMMITMENTS).Count(x => x.AssetType == a) > 0)
                        continue;
                    var supply = supplyBackup.TryGetValue(a, out var s) ? s : 0m;
                    var empty = new ShieldedMerkleStore(a, privacyDb);
                    empty.UpdatePoolStateRoot(maxHeight, supply, 0);
                }

                return Task.FromResult((true, $"Privacy replay: assets={affected.Count}, txs={txCount}."));
            }
            catch (Exception ex)
            {
                return Task.FromResult((false, ex.Message));
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
                return Task.FromResult((false, ex.Message));
            }
        }
    }
}
