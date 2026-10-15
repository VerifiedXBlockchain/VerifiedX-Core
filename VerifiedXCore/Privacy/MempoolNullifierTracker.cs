using VerifiedXCore.Models;

namespace VerifiedXCore.Privacy
{
    /// <summary>
    /// Mempool + per-block nullifier tracking for private transactions (<c>Privacy_Layer_Implementation_Plan.md</c>).
    /// </summary>
    public static class MempoolNullifierTracker
    {
        private static readonly object MempoolRegisterLock = new();

        public static string MakeKey(string assetType, string nullifierBase64) =>
            $"{assetType}\u001d{nullifierBase64}";

        /// <summary>
        /// Releases the claims a verification registered for a transaction that did not enter the mempool: dry runs
        /// (verify routes) and refused submissions registered claims nothing released, and the notes stayed claimed on
        /// this node until it restarted (fifth review, round 27). A transaction in the pool keeps its claims.
        /// </summary>
        public static void ReleaseClaimsIfNotPooled(string? txHash)
        {
            if (string.IsNullOrEmpty(txHash)) return;
            try
            {
                if (VerifiedXCore.Data.TransactionData.GetPool().FindOne(x => x.Hash == txHash) == null)
                    ReleaseClaimsForTxHash(txHash);
            }
            catch { }
        }

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
        public static bool TryRegisterForMempool(string txHash, string assetType, IReadOnlyList<string> nullifiersB64, out string? error, long? height = null)
        {
            error = null;
            // The dummy is passed over only where the proof rules make it the shared placeholder; before them it is a
            // nullifier like any other, as 8.x nodes judge it (round 35).
            var dummyShared = PrivacyEpoch.ProofRulesActive(height ?? (Globals.LastBlock?.Height ?? 0) + 1);
            bool Shared(string n) => dummyShared && PrivacyEpoch.IsDummyNullifier(n);
            if (string.IsNullOrEmpty(txHash) || nullifiersB64 == null || nullifiersB64.Count == 0)
                return true;

            lock (MempoolRegisterLock)
            {
                // One transaction naming one nullifier twice is refused by every block that holds it ("Duplicate nullifier
                // within block"): admitted, it was proposed block after block and every producer stopped (fifth review,
                // round 19). Not a change to any block's verdict.
                var own = new HashSet<string>(StringComparer.Ordinal);
                foreach (var n in nullifiersB64)
                    if (!string.IsNullOrWhiteSpace(n) && !Shared(n) && !own.Add(n))
                    {
                        error = "This transaction spends one nullifier twice.";
                        return false;
                    }
                foreach (var n in nullifiersB64)
                {
                    if (string.IsNullOrWhiteSpace(n))
                    {
                        error = "Nullifier entry is empty.";
                        return false;
                    }
                    // Stage 2: the public dummy note's nullifier is shared by every single-note spend; it never conflicts.
                    if (Shared(n))
                        continue;
                    var key = MakeKey(assetType, n);
                    if (Globals.MempoolNullifiers.TryGetValue(key, out var holder) && holder != txHash)
                    {
                        error = "A pending transaction already spends this nullifier.";
                        return false;
                    }
                }

                foreach (var n in nullifiersB64)
                {
                    if (Shared(n))
                        continue;
                    var key = MakeKey(assetType, n);
                    Globals.MempoolNullifiers[key] = txHash;
                }
            }

            return true;
        }

        /// <summary>
        /// Ensures the same nullifier is not spent twice within one block (validation scratch set), the block at
        /// <paramref name="height"/>. The public dummy is shared only from the proof-rules height: before it 9.x passed two
        /// spends of it in one block over and 8.x nodes refused the block (fifth review, round 35).
        /// </summary>
        public static bool TryAddBlockScopedNullifiers(Transaction tx, ISet<string> blockNullifierKeys, out string? error, long? height = null)
        {
            error = null;
            var dummyShared = PrivacyEpoch.ProofRulesActive(height ?? (Globals.LastBlock?.Height ?? 0) + 1);
            if (!PrivateTransactionTypes.IsPrivateTransaction(tx.TransactionType))
                return true;
            if (!PrivateTxPayloadCodec.TryDecode(tx.Data, out var payload, out _) || payload == null)
                return true;

            foreach (var n in payload.NullsB64)
            {
                if (dummyShared && PrivacyEpoch.IsDummyNullifier(n))
                    continue; // stage 2: shared by every single-note spend in the block
                var key = MakeKey(payload.Asset, n);
                if (!blockNullifierKeys.Add(key))
                {
                    error = "Duplicate nullifier within block.";
                    return false;
                }
            }

            if (!string.IsNullOrWhiteSpace(payload.FeeInputNullifierB64)
                && (tx.TransactionType == TransactionType.VBTC_V2_UNSHIELD || tx.TransactionType == TransactionType.VBTC_V2_PRIVATE_TRANSFER))
            {
                var feeKey = MakeKey("VFX", payload.FeeInputNullifierB64);
                if (!blockNullifierKeys.Add(feeKey))
                {
                    error = "Duplicate nullifier within block (VFX fee leg).";
                    return false;
                }
            }

            return true;
        }
    }
}
