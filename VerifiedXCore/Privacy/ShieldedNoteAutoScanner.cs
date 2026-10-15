using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Models.Privacy;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Privacy
{
    /// <summary>
    /// Real-time shielded note detection triggered during block validation.
    /// Eliminates the need for manual <c>ScanShielded</c> calls for notes
    /// created/spent in blocks as they arrive.
    /// </summary>
    public static class ShieldedNoteAutoScanner
    {
        private static readonly System.Threading.Channels.Channel<Block> _queue =
            System.Threading.Channels.Channel.CreateUnbounded<Block>(new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true });
        private static int _workerStarted;

        /// <summary>
        /// Hands an accepted block to the wallet scanner, which works through blocks in order on its own thread. Block
        /// processing called the scanner directly and waited on every local wallet's lock: a manual scan or resync of a
        /// long range, or a lock left held, stopped this node's block processing (fifth review, round 26). Wallets are
        /// local; nothing the chain decides waits on them.
        /// </summary>
        public static void Enqueue(Block block)
        {
            // Every block, so the scanner's record moves on with the chain and a restart's catch-up is short (round 28:
            // recorded only at private blocks, a restart re-read every block since the last of them). A block without
            // private transactions is passed over at once by the worker.
            if (block == null)
                return;
            // A block without private transactions is queued as its height alone: while a wallet's lock holds the worker,
            // a sync's blocks stayed in memory whole (round 29).
            if (block.Transactions == null || !block.Transactions.Any(t => t != null && PrivateTransactionTypes.IsPrivateTransaction(t.TransactionType)))
                block = new Block { Height = block.Height, Transactions = new List<Transaction>() };
            if (Interlocked.Exchange(ref _workerStarted, 1) == 0)
                _ = Task.Run(Worker);
            _queue.Writer.TryWrite(block);
        }

        private static async Task Worker()
        {
            var caughtUp = false;
            await foreach (var block in _queue.Reader.ReadAllAsync())
            {
                if (!caughtUp)
                {
                    caughtUp = true;
                    CatchUp(block.Height);
                }
                Process(block);
            }
        }

        private static void Process(Block block)
        {
            try { ProcessBlockForLocalWallets(block); }
            catch (Exception ex) { ErrorLogUtility.LogError($"Shielded auto-scan error at block {block.Height}: {ex.Message}", "ShieldedNoteAutoScanner.Worker()"); }
            // Notes left marked spent by a spend never mined are restored (round 32).
            try { PrivacyApiHelper.RestoreUnminedSpends(block.Height); }
            catch (Exception ex) { ErrorLogUtility.LogError($"Restoring unmined spends at block {block.Height}: {ex.Message}", "ShieldedNoteAutoScanner.Worker()"); }
            NoteProcessed(block.Height);
        }

        /// <summary>
        /// Blocks queued when the node stopped were never scanned, and wallets' LastScannedBlock moved on past them with
        /// later blocks (round 26 moved the scan off the block thread; round 27). After a restart the blocks between the
        /// last one scanned and the first one queued are scanned first.
        /// </summary>
        private static void CatchUp(long firstQueued)
        {
            var from = ProcessedThrough();
            if (from < 0) return; // first run of this scanner: nothing recorded to catch up from
            for (var h = from + 1; h < firstQueued; h++)
            {
                Block? block;
                try { block = BlockchainData.GetBlockByHeight(h); } catch { block = null; }
                if (block?.Transactions != null && block.Transactions.Any(t => t != null && PrivateTransactionTypes.IsPrivateTransaction(t.TransactionType)))
                    Process(block);
            }
        }

        private const string StateCollection = "priv_scanner_state";
        private sealed class ScannerState { public int Id { get; set; } = 1; public long Height { get; set; } }

        internal static long ProcessedThrough()
        {
            try { return PrivacyDbContext.GetPrivacyDb().GetCollection<ScannerState>(StateCollection).FindById(1)?.Height ?? -1; }
            catch { return -1; }
        }

        private static void NoteProcessed(long height)
        {
            try
            {
                var col = PrivacyDbContext.GetPrivacyDb().GetCollection<ScannerState>(StateCollection);
                var state = col.FindById(1) ?? new ScannerState { Height = -1 };
                if (height <= state.Height) return;
                state.Height = height;
                col.Upsert(state);
            }
            catch { }
        }

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

            // Track incoming privacy TXs to create local TX records for the receiver
            var incomingPrivacyTxRecords = new List<(Transaction tx, string receiverTransparentAddress, string senderAddress, decimal noteAmount, long blockHeight)>();

            // Acquire per-wallet locks to prevent concurrent API writes from clobbering state. Inside the try whose finally
            // releases them: a fault reloading a wallet (a LiteDB lock timeout) left them held for good, and the scanner
            // and every spend, scan and resync of those wallets waited on them until a restart (fifth review, round 32).
            var heldLocks = new List<SemaphoreSlim>(walletKeys.Count);
            try
            {
            foreach (var (w, _) in walletKeys)
            {
                var sem = ShieldedWalletLock.For(w.ShieldedAddress);
                if (heldLocks.Contains(sem)) continue; // two rows of one wallet: one lock
                sem.Wait();
                heldLocks.Add(sem);
            }

            // Reload wallets inside the lock to get the freshest DB state
            var freshWalletKeys = new List<(ShieldedWallet wallet, ShieldedKeyMaterial keys)>(walletKeys.Count);
            foreach (var (w, keys) in walletKeys)
            {
                var fresh = ShieldedWalletService.FindByZfxAddress(w.ShieldedAddress);
                if (fresh != null)
                    freshWalletKeys.Add((fresh, keys));
            }
            walletKeys = freshWalletKeys;

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
                // By each note's own nullifier, which the transaction's proof exposes: the spent commitments and tree
                // positions a payload lists are not proven, and any private transaction could list other people's notes
                // (fifth review, round 21). Covers the vBTC fee leg's VFX note too (FeeInputNullifierB64).
                if ((payload.NullsB64 != null && payload.NullsB64.Count > 0) || !string.IsNullOrWhiteSpace(payload.FeeInputNullifierB64))
                {
                    foreach (var (w, keys) in walletKeys)
                    {
                        if (w.UnspentCommitments == null || w.UnspentCommitments.Count == 0)
                            continue;
                        foreach (var uc in w.UnspentCommitments)
                        {
                            if (uc == null || uc.IsSpent || string.IsNullOrEmpty(uc.Commitment))
                                continue;
                            if (!PrivacyApiHelper.SpendsWalletNote(tx, payload, uc, keys, block.Height))
                                continue;
                            uc.IsSpent = true;
                            var assetKey = uc.AssetType ?? "";
                            if (w.ShieldedBalances.ContainsKey(assetKey))
                                w.ShieldedBalances[assetKey] -= uc.Amount;
                            modifiedWallets.Add(w.ShieldedAddress);
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

                    if (feeEnc != null)
                    {
                        foreach (var (w, keys) in walletKeys)
                        {
                            if (!ShieldedNoteEncryption.TryOpen(feeEnc, keys.EncryptionPrivateKey32, out var feePlain, out _))
                                continue;
                            if (!ShieldedPlainNoteCodec.TryDeserializeUtf8(feePlain, out var feeNote, out _) || feeNote == null)
                                continue;
                            if (!PrivacyApiHelper.FeeNoteMatchesLeg(tx, payload, feeNote, keys, out var feeMatchedPos))
                                continue; // the leaf the ledger appended for this fee leg (round 23)

                            var feeC = payload.FeeOutputCommitmentB64;
                            if (string.IsNullOrEmpty(feeC))
                                continue;

                            w.UnspentCommitments ??= new List<UnspentCommitment>();
                            bool feeAlreadyExists = false;
                            foreach (var existing in w.UnspentCommitments)
                            {
                                if (existing != null && PrivacyApiHelper.NoteIdentity(existing) == PrivacyApiHelper.NoteIdentity(feeC, feeNote.Amount, feeNote.RandomnessB64))
                                { feeAlreadyExists = true; break; }
                            }
                            if (feeAlreadyExists)
                                break;

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

                            var feeAssetKey = feeNote.AssetType ?? "VFX";
                            if (!w.ShieldedBalances.ContainsKey(feeAssetKey))
                                w.ShieldedBalances[feeAssetKey] = 0;
                            w.ShieldedBalances[feeAssetKey] += feeNote.Amount;

                            modifiedWallets.Add(w.ShieldedAddress);
                            break; // matched this wallet
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

                        // Check if we already have this note (not this commitment: round 32)
                        w.UnspentCommitments ??= new List<UnspentCommitment>();
                        bool alreadyExists = false;
                        foreach (var existing in w.UnspentCommitments)
                        {
                            if (existing != null && PrivacyApiHelper.NoteIdentity(existing) == PrivacyApiHelper.NoteIdentity(c, note.Amount, note.RandomnessB64))
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

                        if (!PrivacyApiHelper.NoteMatchesOutput(o, note.Amount, r32, keys, note.AssetType, out var matchedPos)) continue; // the note is the output it came with (round 22)
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

                        var assetKey = note.AssetType ?? "";
                        if (!w.ShieldedBalances.ContainsKey(assetKey))
                            w.ShieldedBalances[assetKey] = 0;
                        w.ShieldedBalances[assetKey] += note.Amount;

                        modifiedWallets.Add(w.ShieldedAddress);

                        // Create a local TX record for the receiver if this is a privacy TX
                        // and the note is addressed to this wallet (not a change output back to sender)
                        if (PrivateTransactionTypes.IsPrivateTransaction(tx.TransactionType)
                            && !string.IsNullOrEmpty(w.TransparentSourceAddress))
                        {
                            // Determine a user-friendly sender address from the payload
                            string senderAddr = payload.TransparentInput ?? "zfx_private";

                            incomingPrivacyTxRecords.Add((tx, w.TransparentSourceAddress, senderAddr, note.Amount, block.Height));
                        }

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

            } // end try
            finally
            {
                // Release all per-wallet locks
                foreach (var sem in heldLocks)
                    ShieldedWalletLock.Release(sem);
            }

            // Save incoming privacy TX records for receivers (outside of wallet locks)
            if (incomingPrivacyTxRecords.Count > 0)
            {
                _ = Task.Run(async () =>
                {
                    foreach (var (tx, receiverAddr, senderAddr, amount, height) in incomingPrivacyTxRecords)
                    {
                        try
                        {
                            await PrivacyApiHelper.SaveIncomingPrivacyTxLocally(
                                tx, receiverAddr, senderAddr, amount, height);
                        }
                        catch { /* logged inside helper */ }
                    }
                });
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