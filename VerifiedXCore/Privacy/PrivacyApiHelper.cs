using System.Diagnostics.CodeAnalysis;
using VerifiedXCore.Data;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Models.Privacy;
using VerifiedXCore.P2P;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Privacy
{
    /// <summary>Shared helpers for privacy REST controllers (Phase 7).</summary>
    public static class PrivacyApiHelper
    {
        /// <summary>
        /// Derives viewing-only key material (encryption private key) from the wallet's stored viewing key.
        /// Does NOT require the spending key password — suitable for scanning / note decryption.
        /// </summary>
        /// <summary>
        /// Whether a decrypted note is the output it came with: the output's commitment is a leaf the chain applied, and its
        /// stored note hash (owner-bound in the epoch) or, for older leaves, its Pedersen commitment is made from the
        /// note's amount and randomness. A sealed note's amount and asset are what its sender wrote: wallets credited them
        /// as written, from any transaction, and a fake note showed a balance that did not exist (fifth review, round 22).
        /// The leaf is in the pool of the note's <paramref name="asset"/>: no note hash covers the asset, and a VFX note
        /// sealed as vBTC showed vBTC that was never sent (fifth review, round 23). Every leaf with the commitment is
        /// looked at, so a copy of an output's commitment applied before it does not hide the honest one.
        /// </summary>
        internal static bool NoteMatchesOutput(PrivateShieldedOutput? o, decimal amount, byte[]? r32, ShieldedKeyMaterial keys, string? asset) =>
            NoteMatchesOutput(o, amount, r32, keys, asset, out _);

        /// <summary>
        /// As above, with the tree position of the leaf that matched: the note's own leaf. The first leaf with the
        /// commitment (LookupTreePositionByCommitment) could be a copy applied before it, whose position the note cannot
        /// be spent from; the browser scans took position 0 (fifth review, round 25).
        /// </summary>
        internal static bool NoteMatchesOutput(PrivateShieldedOutput? o, decimal amount, byte[]? r32, ShieldedKeyMaterial keys, string? asset, out long treePosition)
        {
            treePosition = 0;
            try
            {
                if (o == null || string.IsNullOrEmpty(o.CommitmentB64) || string.IsNullOrEmpty(asset)) return false;
                var c = o.CommitmentB64;
                var leaf = PrivacyDbContext.GetPrivacyDb().GetCollection<CommitmentRecord>(PrivacyDbContext.PRIV_COMMITMENTS)
                    .Find(x => x.Commitment == c).ToList()
                    .Where(rec => string.Equals(rec.Commitment, c, StringComparison.Ordinal) && string.Equals(rec.AssetType, asset, StringComparison.Ordinal)
                        && NoteMatchesRecord(rec, amount, r32, keys))
                    .OrderBy(rec => rec.TreePosition)
                    .FirstOrDefault();
                if (leaf == null) return false;
                treePosition = leaf.TreePosition;
                return true;
            }
            catch { return false; }
        }

        /// <summary>Whether a leaf's note hash (owner-bound or V1) or, for older leaves, its Pedersen commitment is made from this amount and randomness.</summary>
        internal static bool NoteMatchesRecord(CommitmentRecord rec, decimal amount, byte[]? r32, ShieldedKeyMaterial keys)
        {
            try
            {
                if (r32 == null || r32.Length != PlonkNative.ScalarSize) return false;
                if (!PrivacyPedersenAmount.TryToScaledU64(amount, out var scaled, out _)) return false;
                if (!string.IsNullOrEmpty(rec.NoteHash))
                {
                    if (PoseidonV1.IsAvailable && keys.OwnerPk32 is { Length: 32 } ownerPk
                        && string.Equals(rec.NoteHash, Convert.ToBase64String(PoseidonV1.NoteHashV2(scaled, r32, ownerPk)), StringComparison.Ordinal))
                        return true;
                    var v1 = NoteHashService.Compute(scaled, r32);
                    return v1 != null && string.Equals(rec.NoteHash, Convert.ToBase64String(v1), StringComparison.Ordinal);
                }
                var g1 = new byte[PlonkNative.G1CompressedSize];
                return PlonkNative.pedersen_commit(scaled, r32, g1) == PlonkNative.Success
                    && string.Equals(Convert.ToBase64String(g1), rec.Commitment, StringComparison.Ordinal);
            }
            catch { return false; }
        }

        /// <summary>
        /// Whether a mined private transaction spends this wallet note: its own nullifier is among the transaction's. The
        /// spent commitments and tree positions a payload lists are not proven: any private transaction could list other
        /// people's notes and have every node's wallet mark them spent (fifth review, round 21).
        /// </summary>
        internal static bool SpendsWalletNote(Transaction tx, PrivateTxPayload payload, UnspentCommitment note, ShieldedKeyMaterial keys, long blockHeight)
        {
            var nullifier = VfxPrivateTransactionBuilder.NullifierOf(note, keys, PrivacyEpoch.ProofRulesActive(blockHeight));
            if (string.IsNullOrEmpty(nullifier)) return false;
            return (payload.NullsB64 != null && payload.NullsB64.Contains(nullifier, StringComparer.Ordinal))
                || (HasFeeLeg(tx, payload) && string.Equals(payload.FeeInputNullifierB64, nullifier, StringComparison.Ordinal));
        }

        /// <summary>
        /// Whether the ledger applies this transaction's VFX fee leg (spends its fee nullifier, appends its fee change leaf):
        /// a vBTC unshield or private transfer carrying the whole leg. Any other transaction's fee fields are applied by
        /// nothing, and wallets credited the fee change note they carried and marked spent the note their fee nullifier
        /// named (fifth review, round 23).
        /// </summary>
        internal static bool HasFeeLeg(Transaction tx, PrivateTxPayload payload) =>
            (tx.TransactionType == TransactionType.VBTC_V2_UNSHIELD || tx.TransactionType == TransactionType.VBTC_V2_PRIVATE_TRANSFER)
            && (payload.Fee ?? Globals.PrivateTxFixedFee) > 0
            && !string.IsNullOrWhiteSpace(payload.FeeInputNullifierB64)
            && !string.IsNullOrWhiteSpace(payload.FeeTreeMerkleRoot)
            && payload.FeeInputSpentTreePosition.HasValue;

        /// <summary>Whether a decrypted fee change note is the VFX leaf the ledger appended for this transaction's fee leg.</summary>
        internal static bool FeeNoteMatchesLeg(Transaction tx, PrivateTxPayload payload, ShieldedPlainNote note, ShieldedKeyMaterial keys) =>
            FeeNoteMatchesLeg(tx, payload, note, keys, out _);

        internal static bool FeeNoteMatchesLeg(Transaction tx, PrivateTxPayload payload, ShieldedPlainNote note, ShieldedKeyMaterial keys, out long treePosition)
        {
            treePosition = 0;
            if (!HasFeeLeg(tx, payload) || !string.Equals(note.AssetType ?? "VFX", "VFX", StringComparison.Ordinal)) return false;
            byte[]? r32;
            try { r32 = Convert.FromBase64String(note.RandomnessB64 ?? ""); } catch { return false; }
            return NoteMatchesOutput(new PrivateShieldedOutput { CommitmentB64 = payload.FeeOutputCommitmentB64 ?? "" }, note.Amount, r32, keys, "VFX", out treePosition);
        }

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

            await BroadcastAdmittedPrivateTxAsync(tx);
            return (true, Newtonsoft.Json.JsonConvert.SerializeObject(new { Success = true, Message = "Broadcast.", Hash = tx.Hash }));
        }

        /// <summary>The tail of <see cref="BroadcastVerifiedPrivateTxAsync"/>: rating, local mempool, peers. The caller has verified the transaction.</summary>
        public static async Task BroadcastAdmittedPrivateTxAsync(Transaction tx)
        {
            if (tx.TransactionRating == null)
            {
                var rating = await TransactionRatingService.GetTransactionRating(tx);
                tx.TransactionRating = rating;
            }

            await TransactionData.AddToPool(tx);
            await P2PClient.SendTXMempool(tx);
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

            var walletLock = ShieldedWalletLock.Acquire(zfxAddress);
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
                ShieldedWalletLock.Release(walletLock);
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

            var walletLock = ShieldedWalletLock.Acquire(zfxAddress);
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
                ShieldedWalletLock.Release(walletLock);
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

            // Track incoming privacy TXs to create local TX records for the receiver
            var incomingPrivacyTxRecords = new List<(Transaction tx, string senderAddress, decimal noteAmount, long blockHeight)>();

            var walletLock = ShieldedWalletLock.Acquire(zfxAddress);
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
                    if (tx?.Data == null || !PrivateTransactionTypes.IsPrivateTransaction(tx.TransactionType) || !PrivateTxPayloadCodec.TryDecode(tx.Data, out var payload, out _))
                        continue;
                    if (payload == null)
                        continue;

                    // --- Mark wallet notes consumed by this tx's nullifiers (each note's own; round 21) ---
                    if ((payload.NullsB64 != null && payload.NullsB64.Count > 0) || !string.IsNullOrWhiteSpace(payload.FeeInputNullifierB64))
                    {
                        foreach (var uc in w.UnspentCommitments)
                        {
                            if (uc == null || uc.IsSpent || string.IsNullOrEmpty(uc.Commitment))
                                continue;
                            if (!SpendsWalletNote(tx, payload, uc, keys, block.Height))
                                continue;
                            uc.IsSpent = true;
                            result.NotesMarkedSpent++;
                            var assetKey = uc.AssetType ?? "";
                            if (w.ShieldedBalances.ContainsKey(assetKey))
                                w.ShieldedBalances[assetKey] -= uc.Amount;
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
                            && feeNote != null
                            && FeeNoteMatchesLeg(tx, payload, feeNote, keys, out var feeMatchedPos)) // the leaf the ledger appended (round 23)
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

                                long feeTreePos = feeMatchedPos; // the matched leaf's (round 25)

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

                        byte[] r32 = Array.Empty<byte>();
                        if (!string.IsNullOrEmpty(note.RandomnessB64))
                        {
                            try { r32 = Convert.FromBase64String(note.RandomnessB64); }
                            catch { /* ignore */ }
                        }


                        if (!NoteMatchesOutput(o, note.Amount, r32, keys, note.AssetType, out var matchedPos)) continue; // the note is the output it came with (round 22)
                        merged.Add(c); // seen once it matched: a copy that does not match hid the real output (round 24)
                        result.NotesFound++;
                        w.UnspentCommitments.Add(new UnspentCommitment
                        {
                            Commitment = c,
                            AssetType = note.AssetType ?? "",
                            Amount = note.Amount,
                            Randomness = r32,
                            TreePosition = matchedPos, // the matched leaf's (round 25)
                            BlockHeight = block.Height,
                            IsSpent = false
                        });

                        var key = note.AssetType ?? "";
                        if (!w.ShieldedBalances.ContainsKey(key))
                            w.ShieldedBalances[key] = 0;
                        w.ShieldedBalances[key] += note.Amount;

                        // Track incoming privacy TX for local record creation
                        if (PrivateTransactionTypes.IsPrivateTransaction(tx.TransactionType))
                        {
                            string senderAddr = payload.TransparentInput ?? "zfx_private";
                            incomingPrivacyTxRecords.Add((tx, senderAddr, note.Amount, block.Height));
                        }
                    }
                }
            }

            w.LastScannedBlock = Math.Max(w.LastScannedBlock, toHeight);
            ShieldedWalletService.Upsert(w, notesAsGiven: true); // what the chain shows, even nothing (round 25)

            result.Success = true;
            result.LastScannedBlock = w.LastScannedBlock;
            result.FinalBalance = w.ShieldedBalances;
            result.FinalUnspentCount = w.UnspentCommitments.Count(uc => uc != null && !uc.IsSpent);

            } // end try
            finally
            {
                ShieldedWalletLock.Release(walletLock);
            }

            // Save incoming privacy TX records for the receiver (outside of wallet lock)
            if (incomingPrivacyTxRecords.Count > 0 && !string.IsNullOrEmpty(w.TransparentSourceAddress))
            {
                _ = Task.Run(async () =>
                {
                    foreach (var (tx, senderAddr, amount, height) in incomingPrivacyTxRecords)
                    {
                        try
                        {
                            await SaveIncomingPrivacyTxLocally(
                                tx, w.TransparentSourceAddress, senderAddr, amount, height);
                        }
                        catch { /* logged inside helper */ }
                    }
                });
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

        /// <summary>
        /// Saves a privacy transaction as a local wallet TX record so it appears in
        /// GetAllLocalTX / GetPendingLocalTX / GetSuccessfulLocalTX endpoints.
        /// The record uses user-facing addresses (zfx_ or transparent) rather than the
        /// on-chain ShieldedPoolAddress so it can be found by address-based queries.
        /// </summary>
        public static async Task SavePrivacyTxLocally(
            Transaction onChainTx,
            string fromAddress,
            string toAddress,
            decimal displayAmount,
            TransactionStatus status = TransactionStatus.Pending,
            long height = 0)
        {
            try
            {
                var localTx = new Transaction
                {
                    Hash = onChainTx.Hash,
                    FromAddress = fromAddress,
                    ToAddress = toAddress,
                    Amount = displayAmount.ToNormalizeDecimal(),
                    Fee = onChainTx.Fee,
                    Nonce = onChainTx.Nonce,
                    Timestamp = onChainTx.Timestamp,
                    TransactionType = onChainTx.TransactionType,
                    TransactionStatus = status,
                    Height = height,
                    Signature = onChainTx.Signature,
                    Data = onChainTx.Data
                };

                var txDb = TransactionData.GetAll();
                var existing = txDb.FindOne(x => x.Hash == localTx.Hash);
                if (existing == null)
                {
                    await txDb.InsertSafeAsync(localTx);
                }
                else if (status != TransactionStatus.Pending)
                {
                    // Update existing record (e.g. Pending → Success)
                    existing.TransactionStatus = status;
                    existing.Height = height;
                    await txDb.UpdateSafeAsync(existing);
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError(
                    $"Failed to save privacy TX locally: {ex.Message}",
                    "PrivacyApiHelper.SavePrivacyTxLocally()");
            }
        }

        /// <summary>
        /// Saves an incoming privacy transaction record for the receiver.
        /// Only creates the record if one doesn't already exist for this hash.
        /// </summary>
        public static async Task SaveIncomingPrivacyTxLocally(
            Transaction onChainTx,
            string receiverTransparentAddress,
            string senderAddress,
            decimal noteAmount,
            long blockHeight)
        {
            try
            {
                var txDb = TransactionData.GetAll();
                var existing = txDb.FindOne(x => x.Hash == onChainTx.Hash);
                if (existing != null)
                    return; // Already tracked (sender or previous scan)

                var localTx = new Transaction
                {
                    Hash = onChainTx.Hash,
                    FromAddress = senderAddress,
                    ToAddress = receiverTransparentAddress,
                    Amount = noteAmount.ToNormalizeDecimal(),
                    Fee = 0M.ToNormalizeDecimal(),
                    Nonce = 0,
                    Timestamp = onChainTx.Timestamp,
                    TransactionType = onChainTx.TransactionType,
                    TransactionStatus = TransactionStatus.Success,
                    Height = blockHeight,
                    Signature = onChainTx.Signature,
                    Data = onChainTx.Data
                };

                await txDb.InsertSafeAsync(localTx);
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError(
                    $"Failed to save incoming privacy TX locally: {ex.Message}",
                    "PrivacyApiHelper.SaveIncomingPrivacyTxLocally()");
            }
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
