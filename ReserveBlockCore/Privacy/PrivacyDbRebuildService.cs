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
        private sealed class ReplayItem : IComparable<ReplayItem>
        {
            public long Height;
            public int TxIndex;
            public int Phase;
            public int SubIndex;
            public string Asset = "";
            public long Timestamp;
            public string? NullB64;
            public byte[]? G1;

            public int CompareTo(ReplayItem? other)
            {
                if (other == null)
                    return 1;
                int c = Height.CompareTo(other.Height);
                if (c != 0) return c;
                c = TxIndex.CompareTo(other.TxIndex);
                if (c != 0) return c;
                c = Phase.CompareTo(other.Phase);
                if (c != 0) return c;
                return SubIndex.CompareTo(other.SubIndex);
            }
        }

        /// <summary>
        /// Replays all private transactions from <c>DB_Blockchain</c> into the default <c>DB_Privacy</c> instance.
        /// </summary>
        public static Task<(bool Success, string Message)> TryRebuildFromBlocksAsync(CancellationToken cancellationToken = default) =>
            TryRebuildFromBlocksAsync(PrivacyDbContext.GetPrivacyDb(), cancellationToken);

        /// <summary>
        /// Replays all private transactions from <c>DB_Blockchain</c> into <paramref name="privacyDb"/>.
        /// </summary>
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
        /// Replays private transactions from the given blocks (e.g. tests). Wipes and rebuilds per affected <c>asset</c> in <paramref name="privacyDb"/>.
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
                var items = new List<ReplayItem>();
                var affected = new HashSet<string>(StringComparer.Ordinal);

                foreach (var block in blocks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (block.Transactions == null || block.Transactions.Count == 0)
                        continue;

                    var ordered = PrivateTransactionTypes.OrderTransactionsForReplay(block.Transactions);
                    for (var ti = 0; ti < ordered.Count; ti++)
                    {
                        var tx = ordered[ti];
                        if (!PrivateTransactionTypes.IsPrivateTransaction(tx.TransactionType))
                            continue;
                        if (!PrivateTxPayloadCodec.TryDecode(tx.Data, out var payload, out _))
                            continue;
                        if (!payload!.TryValidateStructure(out _))
                            continue;

                        affected.Add(payload.Asset);

                        for (var ni = 0; ni < payload.NullsB64.Count; ni++)
                        {
                            items.Add(new ReplayItem
                            {
                                Height = block.Height,
                                TxIndex = ti,
                                Phase = 0,
                                SubIndex = ni,
                                Asset = payload.Asset,
                                Timestamp = tx.Timestamp,
                                NullB64 = payload.NullsB64[ni]
                            });
                        }

                        foreach (var o in payload.Outs.OrderBy(x => x.Index))
                        {
                            byte[] g1;
                            try
                            {
                                g1 = Convert.FromBase64String(o.CommitmentB64);
                            }
                            catch
                            {
                                continue;
                            }
                            if (g1.Length != PlonkNative.G1CompressedSize)
                                continue;

                            items.Add(new ReplayItem
                            {
                                Height = block.Height,
                                TxIndex = ti,
                                Phase = 1,
                                SubIndex = o.Index,
                                Asset = payload.Asset,
                                Timestamp = tx.Timestamp,
                                G1 = g1
                            });
                        }
                    }
                }

                items.Sort();

                var poolCol = privacyDb.GetCollection<ShieldedPoolState>(PrivacyDbContext.PRIV_POOL_STATE);
                var supplyBackup = new Dictionary<string, decimal>(StringComparer.Ordinal);
                foreach (var a in affected)
                {
                    var existing = poolCol.FindOne(x => x.AssetType == a);
                    supplyBackup[a] = existing?.TotalShieldedSupply ?? 0m;
                }

                foreach (var a in affected)
                    WipeAsset(privacyDb, a);

                var stores = new Dictionary<string, ShieldedMerkleStore>(StringComparer.Ordinal);
                long maxHeight = 0;

                foreach (var it in items)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (it.Height > maxHeight)
                        maxHeight = it.Height;

                    if (it.Phase == 0)
                    {
                        if (string.IsNullOrEmpty(it.NullB64))
                            continue;
                        NullifierService.TryRecordNullifier(it.NullB64, it.Asset, it.Height, it.Timestamp, privacyDb);
                    }
                    else
                    {
                        if (it.G1 == null)
                            continue;
                        if (!stores.TryGetValue(it.Asset, out var store))
                        {
                            store = new ShieldedMerkleStore(it.Asset, privacyDb);
                            stores[it.Asset] = store;
                        }
                        store.AppendG1Commitment(it.G1, it.Height, it.Timestamp);
                    }
                }

                foreach (var kv in stores)
                {
                    var asset = kv.Key;
                    var store = kv.Value;
                    var count = store.LeafDigests.Count;
                    var supply = supplyBackup.TryGetValue(asset, out var s) ? s : 0m;
                    store.UpdatePoolStateRoot(maxHeight, supply, count);
                }

                foreach (var a in affected)
                {
                    if (stores.ContainsKey(a))
                        continue;
                    var supply = supplyBackup.TryGetValue(a, out var s) ? s : 0m;
                    var empty = new ShieldedMerkleStore(a, privacyDb);
                    empty.UpdatePoolStateRoot(maxHeight, supply, 0);
                }

                return Task.FromResult((true, $"Privacy replay: assets={affected.Count}, ops={items.Count}."));
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

        /// <summary>
        /// Reloads leaf digests from <see cref="CommitmentRecord"/> (ordered by <see cref="CommitmentRecord.TreePosition"/>),
        /// persists Merkle node rows, and refreshes pool root / commitment count. Does not recompute shielded supply.
        /// </summary>
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
