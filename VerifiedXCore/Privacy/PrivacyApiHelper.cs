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
                // Only an amount the chain's 8-decimal scale holds exactly: scaling truncates, so amounts that differ below
                // the 8th decimal all matched one leaf, each was a different note to the wallet, and each was credited.
                if (decimal.Round(amount, 8) != amount) return false;
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

        public const string ViewingKeySpendWarning =
            "This key can spend the wallet's shielded funds: anyone holding it can move them through the raw transaction routes. Share it only as you would the wallet itself.";

        /// <summary>
        /// The wallet's viewing key, for export. With the current key derivation the viewing key is spend authority (the raw
        /// routes build and prove a spend from it and the zfx address), so it is released on the same bar as a spend: the
        /// wallet's spending key must unwrap with <paramref name="walletPassword"/>. A row without a spending key (view-only)
        /// cannot meet that bar and does not export.
        /// </summary>
        public static bool TryExportSpendCapableViewingKey(ShieldedWallet w, string? walletPassword, [NotNullWhen(true)] out string? viewingKeyBase64, out string? error)
        {
            viewingKeyBase64 = null;
            if (w.ViewingKey == null || w.ViewingKey.Length == 0)
            {
                error = "No viewing key on wallet row.";
                return false;
            }
            if (!TryGetKeyMaterial(w, walletPassword, out _, out error))
            {
                error = "The viewing key can spend this wallet's funds, so exporting it needs the wallet password, as a spend does: " + error;
                return false;
            }
            viewingKeyBase64 = Convert.ToBase64String(w.ViewingKey);
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
        /// <summary>
        /// A wallet's name for one note: its commitment, amount and randomness. Not the commitment alone: in the epoch no
        /// proof covers the commitment, and an output carrying the commitment of a note on its way to this wallet, applied
        /// first, made the real note "already known" to every scan and resync, and marking one spent marked both (fifth
        /// review, round 32).
        /// </summary>
        public static string NoteIdentity(string? commitment, decimal amount, byte[]? randomness) =>
            $"{commitment}|{amount.ToString("0.############################", System.Globalization.CultureInfo.InvariantCulture)}|{Convert.ToBase64String(randomness ?? Array.Empty<byte>())}";

        /// <summary><see cref="NoteIdentity(string, decimal, byte[])"/> of a sealed note's randomness as carried (Base64).</summary>
        public static string NoteIdentity(string? commitment, decimal amount, string? randomnessB64)
        {
            byte[] r = Array.Empty<byte>();
            if (!string.IsNullOrEmpty(randomnessB64))
            {
                try { r = Convert.FromBase64String(randomnessB64); }
                catch { /* as the scans take it: none */ }
            }
            return NoteIdentity(commitment, amount, r);
        }

        /// <summary><see cref="NoteIdentity(string, decimal, byte[])"/> of a wallet note.</summary>
        public static string NoteIdentity(UnspentCommitment note) => NoteIdentity(note.Commitment, note.Amount, note.Randomness);

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
                    spentInputs.Where(i => i != null && !string.IsNullOrEmpty(i.Commitment)).Select(NoteIdentity),
                    StringComparer.Ordinal);

                foreach (var uc in fresh.UnspentCommitments ?? new List<UnspentCommitment>())
                {
                    if (uc == null || uc.IsSpent || string.IsNullOrEmpty(uc.Commitment))
                        continue;
                    if (spentCommitments.Contains(NoteIdentity(uc)))
                    {
                        uc.IsSpent = true;
                        uc.SpendPendingSince = Globals.LastBlock?.Height ?? 0;
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

        /// <summary>Blocks after which a note this wallet marked spent, for a spend neither mined nor pending here, is restored.</summary>
        internal const int UnminedSpendBlocks = 30;

        /// <summary>
        /// Notes a wallet marked spent for a transaction it sent are restored when that transaction was never mined: its
        /// nullifier is not spent on chain, no transaction in this node's mempool claims it, and
        /// <see cref="UnminedSpendBlocks"/> blocks have passed. A spend that missed its block (its Merkle root went stale
        /// as soon as another private transaction was mined) was dropped from the mempool and its notes stayed spent until
        /// a manual resync (fifth review, round 32). A note the chain spent is settled as spent.
        /// </summary>
        public static void RestoreUnminedSpends(long tipHeight)
        {
            List<ShieldedWallet> wallets;
            try { wallets = ShieldedWalletService.GetAll(); }
            catch { return; }
            foreach (var listed in wallets ?? new List<ShieldedWallet>())
            {
                if (listed?.UnspentCommitments == null || !listed.UnspentCommitments.Any(n => n != null && n.IsSpent && n.SpendPendingSince != null))
                    continue;
                var walletLock = ShieldedWalletLock.Acquire(listed.ShieldedAddress);
                try
                {
                    var w = ShieldedWalletService.FindByZfxAddress(listed.ShieldedAddress);
                    if (w?.UnspentCommitments == null || !TryGetViewingKeyMaterial(w, out var keys, out _) || keys == null)
                        continue;
                    var epoch = PrivacyEpoch.ProofRulesActive(tipHeight + 1);
                    var changed = false;
                    foreach (var uc in w.UnspentCommitments)
                    {
                        if (uc == null || !uc.IsSpent || uc.SpendPendingSince is not { } since)
                            continue;
                        var asset = uc.AssetType ?? "";
                        var nullifier = VfxPrivateTransactionBuilder.NullifierOf(uc, keys, epoch);
                        if (nullifier == null)
                            continue;
                        if (NullifierService.IsNullifierSpentInDb(nullifier, asset))
                        {
                            uc.SpendPendingSince = null; // mined: spent for good
                            changed = true;
                            continue;
                        }
                        if (tipHeight - since < UnminedSpendBlocks
                            || MempoolNullifierTracker.IsClaimedByPendingTx(MempoolNullifierTracker.MakeKey(asset, nullifier)))
                            continue;
                        uc.IsSpent = false;
                        uc.SpendPendingSince = null;
                        w.ShieldedBalances ??= new Dictionary<string, decimal>();
                        w.ShieldedBalances[asset] = (w.ShieldedBalances.TryGetValue(asset, out var b) ? b : 0M) + uc.Amount;
                        changed = true;
                        LogUtility.Log($"Shielded wallet {w.ShieldedAddress}: a {uc.Amount} {asset} note marked spent at block {since} by a transaction never mined is spendable again.", "PrivacyApiHelper.RestoreUnminedSpends()");
                    }
                    if (changed)
                        ShieldedWalletService.Upsert(w);
                }
                catch (Exception ex)
                {
                    ErrorLogUtility.LogError($"Restoring unmined spends of {listed.ShieldedAddress}: {ex.Message}", "PrivacyApiHelper.RestoreUnminedSpends()");
                }
                finally
                {
                    ShieldedWalletLock.Release(walletLock);
                }
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
                    spentInputs.Where(i => i != null && !string.IsNullOrEmpty(i.Commitment)).Select(NoteIdentity),
                    StringComparer.Ordinal);
                TryGetViewingKeyMaterial(fresh, out var keys, out _);
                var epoch = PrivacyEpoch.ProofRulesActive((Globals.LastBlock?.Height ?? 0) + 1);

                foreach (var uc in fresh.UnspentCommitments ?? new List<UnspentCommitment>())
                {
                    if (uc == null || !uc.IsSpent || string.IsNullOrEmpty(uc.Commitment))
                        continue;
                    // Not a note the chain has spent meanwhile (another request's spend of it mined while this one was
                    // being built): restoring it showed a balance that was gone (round 26).
                    if (keys != null && VfxPrivateTransactionBuilder.NullifierOf(uc, keys, epoch) is { } nullifier
                        && NullifierService.IsNullifierSpentInDb(nullifier, uc.AssetType ?? assetType))
                        continue;
                    if (commitmentSet.Contains(NoteIdentity(uc)))
                    {
                        uc.IsSpent = false;
                        uc.SpendPendingSince = null;
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
                            if (!string.IsNullOrEmpty(feeC) && !merged.Contains(NoteIdentity(feeC, feeNote.Amount, feeNote.RandomnessB64)))
                            {
                                merged.Add(NoteIdentity(feeC, feeNote.Amount, feeNote.RandomnessB64));
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
                        if (string.IsNullOrEmpty(c) || merged.Contains(PrivacyApiHelper.NoteIdentity(c, note.Amount, note.RandomnessB64)))
                            continue;

                        byte[] r32 = Array.Empty<byte>();
                        if (!string.IsNullOrEmpty(note.RandomnessB64))
                        {
                            try { r32 = Convert.FromBase64String(note.RandomnessB64); }
                            catch { /* ignore */ }
                        }


                        if (!NoteMatchesOutput(o, note.Amount, r32, keys, note.AssetType, out var matchedPos)) continue; // the note is the output it came with (round 22)
                        merged.Add(PrivacyApiHelper.NoteIdentity(c, note.Amount, note.RandomnessB64)); // seen once it matched: a copy that does not match hid the real output (round 24)
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
