using ReserveBlockCore.Models;
using ReserveBlockCore.Models.Privacy;
using ReserveBlockCore.Utilities;

namespace ReserveBlockCore.Privacy
{
    /// <summary>
    /// Real-time shielded note detection triggered during block validation.
    /// Eliminates the need for manual <c>ScanShielded</c> calls for notes
    /// created/spent in blocks as they arrive.
    /// </summary>
    public static class ShieldedNoteAutoScanner
    {
        /// <summary>
        /// Scans a newly accepted block for shielded notes belonging to any local
        /// shielded wallet. Detects both incoming notes (via trial decryption) and
        /// spent notes (via commitment-string matching from global pool).
        /// <para>This is safe to call even when no shielded wallets exist — it returns immediately.</para>
        /// </summary>
        public static void ProcessBlockForLocalWallets(Block block)
        {
            if (block?.Transactions == null || block.Transactions.Count == 0)
                return;

            // Quick check: does this block contain any private transactions?
            bool hasPrivateTx = false;
            foreach (var tx in block.Transactions)
            {
                if (PrivateTransactionTypes.IsPrivateTransaction(tx.TransactionType))
                {
                    hasPrivateTx = true;
                    break;
                }
            }
            if (!hasPrivateTx)
                return;

            // Load all local shielded wallets
            List<ShieldedWallet> wallets;
            try
            {
                wallets = ShieldedWalletService.GetAll();
            }
            catch
            {
                return; // Privacy DB not available
            }

            if (wallets == null || wallets.Count == 0)
                return;

            // Pre-derive viewing key material for each wallet
            var walletKeys = new List<(ShieldedWallet wallet, ShieldedKeyMaterial keys)>();
            foreach (var w in wallets)
            {
                if (PrivacyApiHelper.TryGetViewingKeyMaterial(w, out var keys, out _))
                    walletKeys.Add((w, keys));
            }

            if (walletKeys.Count == 0)
                return;

            // Track which wallets were modified so we only persist those
            var modifiedWallets = new HashSet<string>(StringComparer.Ordinal);

            foreach (var tx in block.Transactions)
            {
                if (!PrivateTransactionTypes.IsPrivateTransaction(tx.TransactionType))
                    continue;
                if (tx.Data == null)
                    continue;
                if (!PrivateTxPayloadCodec.TryDecode(tx.Data, out var payload, out _))
                    continue;
                if (payload == null)
                    continue;

                // --- Mark wallet notes consumed by this tx's nullifiers ---
                // Prefer SpentCommitmentB64s (v1.1+) for direct matching; fall back to tree-position lookup.
                if (payload.SpentCommitmentTreePositions != null && payload.SpentCommitmentTreePositions.Count > 0)
                {
                    HashSet<string> spentCommitmentStrings;
                    if (payload.SpentCommitmentB64s != null && payload.SpentCommitmentB64s.Count > 0)
                    {
                        // New TX format: commitment strings stored directly in payload
                        spentCommitmentStrings = new HashSet<string>(payload.SpentCommitmentB64s, StringComparer.Ordinal);
                    }
                    else
                    {
                        // Legacy TX: look up by tree position (may fail for corrupted positions)
                        spentCommitmentStrings = LookupCommitmentStringsByTreePositions(
                            payload.SpentCommitmentTreePositions, payload.Asset);
                    }

                    if (spentCommitmentStrings.Count > 0)
                    {
                        foreach (var (w, _) in walletKeys)
                        {
                            if (w.UnspentCommitments == null || w.UnspentCommitments.Count == 0)
                                continue;

                            foreach (var uc in w.UnspentCommitments)
                            {
                                if (uc == null || uc.IsSpent || string.IsNullOrEmpty(uc.Commitment))
                                    continue;

                                if (spentCommitmentStrings.Contains(uc.Commitment))
                                {
                                    uc.IsSpent = true;
                                    var assetKey = uc.AssetType ?? "";
                                    if (w.ShieldedBalances.ContainsKey(assetKey))
                                        w.ShieldedBalances[assetKey] -= uc.Amount;
                                    modifiedWallets.Add(w.ShieldedAddress);
                                }
                            }
                        }
                    }
                }

                // Also check the fee input spent tree position (vBTC fee leg)
                if (payload.FeeInputSpentTreePosition.HasValue)
                {
                    var feeSpentStrings = LookupCommitmentStringsByTreePositions(
                        new List<long> { payload.FeeInputSpentTreePosition.Value }, "VFX");

                    if (feeSpentStrings.Count > 0)
                    {
                        foreach (var (w, _) in walletKeys)
                        {
                            if (w.UnspentCommitments == null || w.UnspentCommitments.Count == 0)
                                continue;

                            foreach (var uc in w.UnspentCommitments)
                            {
                                if (uc == null || uc.IsSpent || string.IsNullOrEmpty(uc.Commitment))
                                    continue;

                                if (feeSpentStrings.Contains(uc.Commitment))
                                {
                                    uc.IsSpent = true;
                                    var assetKey = uc.AssetType ?? "";
                                    if (w.ShieldedBalances.ContainsKey(assetKey))
                                        w.ShieldedBalances[assetKey] -= uc.Amount;
                                    modifiedWallets.Add(w.ShieldedAddress);
                                }
                            }
                        }
                    }
                }

                // --- Detect new incoming notes via trial decryption ---
                if (payload.Outs == null || payload.Outs.Count == 0)
                    continue;

                foreach (var o in payload.Outs)
                {
                    if (string.IsNullOrWhiteSpace(o.EncryptedNoteB64))
                        continue;

                    byte[] enc;
                    try
                    {
                        enc = Convert.FromBase64String(o.EncryptedNoteB64);
                    }
                    catch
                    {
                        continue;
                    }

                    // Try each wallet's encryption key
                    foreach (var (w, keys) in walletKeys)
                    {
                        if (!ShieldedNoteEncryption.TryOpen(enc, keys.EncryptionPrivateKey32, out var plain, out _))
                            continue;

                        if (!ShieldedPlainNoteCodec.TryDeserializeUtf8(plain, out var note, out _) || note == null)
                            continue;

                        var c = o.CommitmentB64;
                        if (string.IsNullOrEmpty(c))
                            continue;

                        // Check if we already have this commitment
                        w.UnspentCommitments ??= new List<UnspentCommitment>();
                        bool alreadyExists = false;
                        foreach (var existing in w.UnspentCommitments)
                        {
                            if (existing != null && string.Equals(existing.Commitment, c, StringComparison.Ordinal))
                            {
                                alreadyExists = true;
                                break;
                            }
                        }
                        if (alreadyExists)
                            continue;

                        byte[] r32 = Array.Empty<byte>();
                        if (!string.IsNullOrEmpty(note.RandomnessB64))
                        {
                            try { r32 = Convert.FromBase64String(note.RandomnessB64); }
                            catch { /* ignore */ }
                        }

                        // Look up the actual tree position from the global CommitmentRecord
                        long treePos = LookupTreePositionByCommitment(c, note.AssetType ?? "");

                        w.UnspentCommitments.Add(new UnspentCommitment
                        {
                            Commitment = c,
                            AssetType = note.AssetType ?? "",
                            Amount = note.Amount,
                            Randomness = r32,
                            TreePosition = treePos,
                            BlockHeight = block.Height,
                            IsSpent = false
                        });

                        var assetKey = note.AssetType ?? "";
                        if (!w.ShieldedBalances.ContainsKey(assetKey))
                            w.ShieldedBalances[assetKey] = 0;
                        w.ShieldedBalances[assetKey] += note.Amount;

                        modifiedWallets.Add(w.ShieldedAddress);

                        // Note matched this wallet — no need to try other wallets for this output
                        break;
                    }
                }
            }

            // Persist any modified wallets and update LastScannedBlock
            foreach (var (w, _) in walletKeys)
            {
                if (!modifiedWallets.Contains(w.ShieldedAddress))
                {
                    // Even if not modified, update LastScannedBlock if it's behind
                    if (w.LastScannedBlock < block.Height)
                    {
                        w.LastScannedBlock = block.Height;
                        ShieldedWalletService.Upsert(w);
                    }
                    continue;
                }

                w.LastScannedBlock = Math.Max(w.LastScannedBlock, block.Height);
                ShieldedWalletService.Upsert(w);
            }
        }

        /// <summary>
        /// Looks up commitment strings from the global CommitmentRecord table by tree positions.
        /// Returns a set of commitment strings that correspond to those positions.
        /// </summary>
        private static HashSet<string> LookupCommitmentStringsByTreePositions(
            IReadOnlyList<long> treePositions, string assetType)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                var db = PrivacyDbContext.GetPrivacyDb();
                var col = db.GetCollection<CommitmentRecord>(PrivacyDbContext.PRIV_COMMITMENTS);
                foreach (var pos in treePositions)
                {
                    var rec = col.FindOne(x => x.AssetType == assetType && x.TreePosition == pos);
                    if (rec != null && !string.IsNullOrEmpty(rec.Commitment))
                        result.Add(rec.Commitment);
                }
            }
            catch
            {
                // Privacy DB not available
            }
            return result;
        }

        /// <summary>
        /// Looks up the actual tree position for a commitment string from the global CommitmentRecord table.
        /// Returns 0 if not found (legacy fallback).
        /// </summary>
        private static long LookupTreePositionByCommitment(string commitmentB64, string assetType)
        {
            try
            {
                var db = PrivacyDbContext.GetPrivacyDb();
                var col = db.GetCollection<CommitmentRecord>(PrivacyDbContext.PRIV_COMMITMENTS);
                var rec = col.FindOne(x => x.AssetType == assetType && x.Commitment == commitmentB64);
                if (rec != null)
                    return rec.TreePosition;
            }
            catch
            {
                // Privacy DB not available
            }
            return 0;
        }
    }
}