using ReserveBlockCore.Models;

namespace ReserveBlockCore.Privacy
{
    /// <summary>
    /// Mempool + per-block nullifier tracking for private transactions (<c>Privacy_Layer_Implementation_Plan.md</c>).
    /// </summary>
    public static class MempoolNullifierTracker
    {
        private static readonly object MempoolRegisterLock = new();

        public static string MakeKey(string assetType, string nullifierBase64) =>
            $"{assetType}\u001d{nullifierBase64}";

        /// <summary>Remove mempool claims held by <paramref name="txHash"/>.</summary>
        public static void ReleaseClaimsForTxHash(string txHash)
        {
            if (string.IsNullOrEmpty(txHash))
                return;
            var toRemove = Globals.MempoolNullifiers
                .Where(kv => kv.Value == txHash)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var k in toRemove)
                Globals.MempoolNullifiers.TryRemove(k, out _);
        }

        /// <summary>Registers payload nullifiers for a TX that passed validation and is being (or was) admitted to the mempool.</summary>
        public static bool TryRegisterForMempool(string txHash, string assetType, IReadOnlyList<string> nullifiersB64, out string? error)
        {
            error = null;
            if (string.IsNullOrEmpty(txHash) || nullifiersB64 == null || nullifiersB64.Count == 0)
                return true;

            lock (MempoolRegisterLock)
            {
                foreach (var n in nullifiersB64)
                {
                    if (string.IsNullOrWhiteSpace(n))
                    {
                        error = "Nullifier entry is empty.";
                        return false;
                    }
                    var key = MakeKey(assetType, n);
                    if (Globals.MempoolNullifiers.TryGetValue(key, out var holder) && holder != txHash)
                    {
                        error = "A pending transaction already spends this nullifier.";
                        return false;
                    }
                }

                foreach (var n in nullifiersB64)
                {
                    var key = MakeKey(assetType, n);
                    Globals.MempoolNullifiers[key] = txHash;
                }
            }

            return true;
        }

        /// <summary>Ensures the same nullifier is not spent twice within one block (validation scratch set).</summary>
        public static bool TryAddBlockScopedNullifiers(Transaction tx, ISet<string> blockNullifierKeys, out string? error)
        {
            error = null;
            if (!PrivateTransactionTypes.IsPrivateTransaction(tx.TransactionType))
                return true;
            if (!PrivateTxPayloadCodec.TryDecode(tx.Data, out var payload, out _) || payload == null)
                return true;

            foreach (var n in payload.NullsB64)
            {
                var key = MakeKey(payload.Asset, n);
                if (!blockNullifierKeys.Add(key))
                {
                    error = "Duplicate nullifier within block.";
                    return false;
                }
            }

            return true;
        }
    }
}
