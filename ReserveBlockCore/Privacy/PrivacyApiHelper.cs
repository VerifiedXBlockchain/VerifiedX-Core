using System.Diagnostics.CodeAnalysis;
using ReserveBlockCore.Data;
using ReserveBlockCore.Models;
using ReserveBlockCore.Models.Privacy;
using ReserveBlockCore.P2P;
using ReserveBlockCore.Services;

namespace ReserveBlockCore.Privacy
{
    /// <summary>Shared helpers for privacy REST controllers (Phase 7).</summary>
    public static class PrivacyApiHelper
    {
        /// <summary>
        /// Derives viewing-only key material (encryption private key) from the wallet's stored viewing key.
        /// Does NOT require the spending key password — suitable for scanning / note decryption.
        /// </summary>
        public static bool TryGetViewingKeyMaterial(ShieldedWallet w, [NotNullWhen(true)] out ShieldedKeyMaterial? keys, out string? error)
        {
            keys = null;
            error = null;
            if (w.ViewingKey == null || w.ViewingKey.Length != 32)
            {
                error = "No 32-byte viewing key on wallet row.";
                return false;
            }
            if (!ShieldedAddressCodec.TryDecodeEncryptionKey(w.ShieldedAddress, out var pub33, out var derr) || pub33 == null)
            {
                error = derr ?? "Invalid zfx address.";
                return false;
            }
            var encPriv = ShieldedHdDerivation.DeriveEncryptionPrivateKeyFromViewingKey(w.ViewingKey);
            keys = new ShieldedKeyMaterial
            {
                SpendingKey32 = Array.Empty<byte>(),   // not available without password
                ViewingKey32 = w.ViewingKey,
                EncryptionPrivateKey32 = encPriv,
                EncryptionPublicKey33 = pub33,
                ZfxAddress = w.ShieldedAddress
            };
            return true;
        }

        public static bool TryGetKeyMaterial(ShieldedWallet w, string? walletPassword, [NotNullWhen(true)] out ShieldedKeyMaterial? keys, out string? error)
        {
            keys = null;
            error = null;
            if (w.IsViewOnly || w.SpendingKey == null || w.SpendingKey.Length == 0)
            {
                error = "Wallet is view-only or has no spending key.";
                return false;
            }
            if (!ShieldedWalletService.TryUnwrapSpendingBundle(w, walletPassword ?? "", out var sp, out var enc, out var uerr))
            {
                error = uerr ?? "Could not unwrap spending key (wrong password?).";
                return false;
            }
            if (!ShieldedAddressCodec.TryDecodeEncryptionKey(w.ShieldedAddress, out var pub33, out var derr) || pub33 == null)
            {
                error = derr ?? "Invalid zfx address.";
                return false;
            }
            keys = new ShieldedKeyMaterial
            {
                SpendingKey32 = sp,
                ViewingKey32 = w.ViewingKey,
                EncryptionPrivateKey32 = enc,
                EncryptionPublicKey33 = pub33,
                ZfxAddress = w.ShieldedAddress
            };
            return true;
        }

        public static async Task<(bool ok, string json)> BroadcastVerifiedPrivateTxAsync(Transaction tx)
        {
            var result = await TransactionValidatorService.VerifyTX(tx);
            if (!result.Item1)
                return (false, Newtonsoft.Json.JsonConvert.SerializeObject(new { Success = false, Message = result.Item2 }));

            if (tx.TransactionRating == null)
            {
                var rating = await TransactionRatingService.GetTransactionRating(tx);
                tx.TransactionRating = rating;
            }

            await TransactionData.AddToPool(tx);
            await P2PClient.SendTXMempool(tx);
            return (true, Newtonsoft.Json.JsonConvert.SerializeObject(new { Success = true, Message = "Broadcast.", Hash = tx.Hash }));
        }

        /// <summary>
        /// Marks the given inputs as spent on the local wallet row and deducts from ShieldedBalances.
        /// Reloads the wallet fresh from DB to avoid overwriting concurrent auto-scanner changes.
        /// Persists immediately.
        /// </summary>
        public static void MarkInputsSpentLocally(string zfxAddress, IReadOnlyList<UnspentCommitment> spentInputs, string assetType)
        {
            if (string.IsNullOrEmpty(zfxAddress) || spentInputs == null || spentInputs.Count == 0)
                return;

            ShieldedWalletLock.Wait(zfxAddress);
            try
            {
                // Reload fresh from DB to pick up any changes made by the auto-scanner
                var fresh = ShieldedWalletService.FindByZfxAddress(zfxAddress);
                if (fresh == null)
                    return;

                var spentCommitments = new HashSet<string>(
                    spentInputs.Select(i => i.Commitment).Where(c => !string.IsNullOrEmpty(c)),
                    StringComparer.Ordinal);

                foreach (var uc in fresh.UnspentCommitments ?? new List<UnspentCommitment>())
                {
                    if (uc == null || uc.IsSpent || string.IsNullOrEmpty(uc.Commitment))
                        continue;
                    if (spentCommitments.Contains(uc.Commitment))
                    {
                        uc.IsSpent = true;
                        if (fresh.ShieldedBalances.ContainsKey(assetType))
                            fresh.ShieldedBalances[assetType] -= uc.Amount;
                    }
                }

                ShieldedWalletService.Upsert(fresh);
            }
            finally
            {
                ShieldedWalletLock.Release(zfxAddress);
            }
        }

        /// <summary>
        /// Rollback: unmarks previously spent inputs if build or broadcast fails.
        /// Reloads wallet fresh from DB.
        /// </summary>
        public static void UnmarkInputsSpentLocally(string zfxAddress, IReadOnlyList<UnspentCommitment> spentInputs, string assetType)
        {
            if (string.IsNullOrEmpty(zfxAddress) || spentInputs == null || spentInputs.Count == 0)
                return;

            ShieldedWalletLock.Wait(zfxAddress);
            try
            {
                var fresh = ShieldedWalletService.FindByZfxAddress(zfxAddress);
                if (fresh == null)
                    return;

                var commitmentSet = new HashSet<string>(
                    spentInputs.Select(i => i.Commitment).Where(c => !string.IsNullOrEmpty(c)),
                    StringComparer.Ordinal);

                foreach (var uc in fresh.UnspentCommitments ?? new List<UnspentCommitment>())
                {
                    if (uc == null || !uc.IsSpent || string.IsNullOrEmpty(uc.Commitment))
                        continue;
                    if (commitmentSet.Contains(uc.Commitment))
                    {
                        uc.IsSpent = false;
                        if (fresh.ShieldedBalances.ContainsKey(assetType))
                            fresh.ShieldedBalances[assetType] += uc.Amount;
                    }
                }

                ShieldedWalletService.Upsert(fresh);
            }
            finally
            {
                ShieldedWalletLock.Release(zfxAddress);
            }
        }

        /// <summary>
        /// Looks up commitment strings from the global CommitmentRecord table by tree positions.
        /// Used by manual scanner to match spent notes by commitment string.
        /// </summary>
        public static HashSet<string> LookupCommitmentStringsByTreePositions(
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
            catch { /* Privacy DB not available */ }
            return result;
        }

        /// <summary>
        /// Looks up the actual tree position for a commitment string from the global CommitmentRecord table.
        /// Returns 0 if not found.
        /// </summary>
        public static long LookupTreePositionByCommitment(string commitmentB64, string assetType)
        {
            try
            {
                var db = PrivacyDbContext.GetPrivacyDb();
                var col = db.GetCollection<CommitmentRecord>(PrivacyDbContext.PRIV_COMMITMENTS);
                var rec = col.FindOne(x => x.AssetType == assetType && x.Commitment == commitmentB64);
                if (rec != null)
                    return rec.TreePosition;
            }
            catch { /* Privacy DB not available */ }
            return 0;
        }

        /// <summary>
        /// Wipes the wallet's UnspentCommitments and ShieldedBalances, then rescans from
        /// <paramref name="fromHeight"/> to <paramref name="toHeight"/> rebuilding from scratch.
        /// Returns a result object with stats.
        /// </summary>
        public static ResyncResult ResyncShieldedWallet(string zfxAddress, long fromHeight, long toHeight)
        {
            var result = new ResyncResult();

            var w = ShieldedWalletService.FindByZfxAddress(zfxAddress);
            if (w == null)
            {
                result.Error = "No shielded wallet row for this zfx address.";
                return result;
            }

            if (!TryGetViewingKeyMaterial(w, out var keys, out var kmErr))
            {
                result.Error = kmErr ?? "Cannot derive viewing keys.";
                return result;
            }

            ShieldedWalletLock.Wait(zfxAddress);
            try
            {
            // Re-fetch inside lock for freshest state
            w = ShieldedWalletService.FindByZfxAddress(zfxAddress)!;

            // Wipe existing state
            w.UnspentCommitments = new List<UnspentCommitment>();
            w.ShieldedBalances = new Dictionary<string, decimal>();

            var merged = new HashSet<string>(StringComparer.Ordinal);

            for (long h = fromHeight; h <= toHeight; h++)
            {
                var block = BlockchainData.GetBlockByHeight(h);
                if (block?.Transactions == null)
                    continue;
                result.BlocksScanned++;

                foreach (var tx in block.Transactions)
                {
                    result.TransactionsScanned++;
                    if (tx?.Data == null || !PrivateTxPayloadCodec.TryDecode(tx.Data, out var payload, out _))
                        continue;
                    if (payload == null)
                        continue;

                    // --- Mark wallet notes consumed by this tx's spent positions ---
                    if (payload.SpentCommitmentTreePositions != null && payload.SpentCommitmentTreePositions.Count > 0)
                    {
                        HashSet<string> spentCommitmentStrings;
                        if (payload.SpentCommitmentB64s != null && payload.SpentCommitmentB64s.Count > 0)
                        {
                            // v1.1+ TX: commitment strings stored directly in payload
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
                            foreach (var uc in w.UnspentCommitments)
                            {
                                if (uc == null || uc.IsSpent || string.IsNullOrEmpty(uc.Commitment))
                                    continue;
                                if (spentCommitmentStrings.Contains(uc.Commitment))
                                {
                                    uc.IsSpent = true;
                                    result.NotesMarkedSpent++;
                                    var assetKey = uc.AssetType ?? "";
                                    if (w.ShieldedBalances.ContainsKey(assetKey))
                                        w.ShieldedBalances[assetKey] -= uc.Amount;
                                }
                            }
                        }
                    }

                    // --- Detect VFX fee change notes (vBTC Z→Z / Z→T fee leg) ---
                    if (!string.IsNullOrWhiteSpace(payload.FeeOutputEncryptedNoteB64)
                        && !string.IsNullOrWhiteSpace(payload.FeeOutputCommitmentB64))
                    {
                        byte[] feeEnc;
                        try { feeEnc = Convert.FromBase64String(payload.FeeOutputEncryptedNoteB64); }
                        catch { feeEnc = null!; }

                        if (feeEnc != null
                            && ShieldedNoteEncryption.TryOpen(feeEnc, keys.EncryptionPrivateKey32, out var feePlain, out _)
                            && ShieldedPlainNoteCodec.TryDeserializeUtf8(feePlain, out var feeNote, out _)
                            && feeNote != null)
                        {
                            var feeC = payload.FeeOutputCommitmentB64;
                            if (!string.IsNullOrEmpty(feeC) && !merged.Contains(feeC))
                            {
                                merged.Add(feeC);
                                result.NotesFound++;

                                byte[] feeR32 = Array.Empty<byte>();
                                if (!string.IsNullOrEmpty(feeNote.RandomnessB64))
                                {
                                    try { feeR32 = Convert.FromBase64String(feeNote.RandomnessB64); }
                                    catch { /* ignore */ }
                                }

                                long feeTreePos = LookupTreePositionByCommitment(feeC, feeNote.AssetType ?? "VFX");

                                w.UnspentCommitments.Add(new UnspentCommitment
                                {
                                    Commitment = feeC,
                                    AssetType = feeNote.AssetType ?? "VFX",
                                    Amount = feeNote.Amount,
                                    Randomness = feeR32,
                                    TreePosition = feeTreePos,
                                    BlockHeight = block.Height,
                                    IsSpent = false
                                });

                                var feeKey = feeNote.AssetType ?? "VFX";
                                if (!w.ShieldedBalances.ContainsKey(feeKey))
                                    w.ShieldedBalances[feeKey] = 0;
                                w.ShieldedBalances[feeKey] += feeNote.Amount;
                            }
                        }
                    }

                    // --- Detect new incoming notes ---
                    if (payload.Outs == null)
                        continue;

                    foreach (var o in payload.Outs)
                    {
                        if (string.IsNullOrWhiteSpace(o.EncryptedNoteB64))
                            continue;
                        byte[] enc;
                        try { enc = Convert.FromBase64String(o.EncryptedNoteB64); }
                        catch { continue; }

                        if (!ShieldedNoteEncryption.TryOpen(enc, keys.EncryptionPrivateKey32, out var plain, out _))
                            continue;
                        if (!ShieldedPlainNoteCodec.TryDeserializeUtf8(plain, out var note, out _) || note == null)
                            continue;

                        var c = o.CommitmentB64;
                        if (string.IsNullOrEmpty(c) || merged.Contains(c))
                            continue;
                        merged.Add(c);
                        result.NotesFound++;

                        byte[] r32 = Array.Empty<byte>();
                        if (!string.IsNullOrEmpty(note.RandomnessB64))
                        {
                            try { r32 = Convert.FromBase64String(note.RandomnessB64); }
                            catch { /* ignore */ }
                        }

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

                        var key = note.AssetType ?? "";
                        if (!w.ShieldedBalances.ContainsKey(key))
                            w.ShieldedBalances[key] = 0;
                        w.ShieldedBalances[key] += note.Amount;
                    }
                }
            }

            w.LastScannedBlock = Math.Max(w.LastScannedBlock, toHeight);
            ShieldedWalletService.Upsert(w);

            result.Success = true;
            result.LastScannedBlock = w.LastScannedBlock;
            result.FinalBalance = w.ShieldedBalances;
            result.FinalUnspentCount = w.UnspentCommitments.Count(uc => uc != null && !uc.IsSpent);

            } // end try
            finally
            {
                ShieldedWalletLock.Release(zfxAddress);
            }

            return result;
        }

        public class ResyncResult
        {
            public bool Success { get; set; }
            public string? Error { get; set; }
            public int BlocksScanned { get; set; }
            public int TransactionsScanned { get; set; }
            public int NotesFound { get; set; }
            public int NotesMarkedSpent { get; set; }
            public long LastScannedBlock { get; set; }
            public Dictionary<string, decimal>? FinalBalance { get; set; }
            public int FinalUnspentCount { get; set; }
        }

        /// <summary>Approximate VFX shielded balance for co-shield UX warnings.</summary>
        public static decimal SumVfxUnspent(ShieldedWallet? w)
        {
            if (w?.UnspentCommitments == null)
                return 0;
            return w.UnspentCommitments
                .Where(c => c != null && !c.IsSpent && string.Equals(c.AssetType, "VFX", StringComparison.Ordinal))
                .Sum(c => c.Amount);
        }
    }
}
