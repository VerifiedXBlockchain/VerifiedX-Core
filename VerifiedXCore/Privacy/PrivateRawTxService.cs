using System.Collections.Concurrent;
using LiteDB;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Models.Privacy;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Privacy
{
    /// <summary>
    /// Raw shielded-VFX flows for wallets that hold their own keys and keep no shielded wallet row on this node (the web
    /// wallet and other integrators). The node does only what a node can do (reads the pool, finds the caller's notes,
    /// builds the public inputs, runs the prover) and never needs a local account or password:
    /// <list type="bullet">
    /// <item>Shield: the node builds and proves; the caller signs the returned Hash with the transparent key and submits it.</item>
    /// <item>Unshield / private transfer: the caller sends the <c>zfx_</c> address and its 32-byte viewing key; the node
    /// finds the spendable notes, proves the spend and returns a complete ZK-authorised transaction (no signature) for the
    /// caller to submit.</item>
    /// </list>
    /// In this scheme the viewing key is spend authority (the nullifier key is the viewing key reduced into the field), so a caller sends it
    /// only to a node it trusts, over TLS. Built transactions wait in memory keyed by Hash for <see cref="PendingTtl"/>,
    /// as the vBTC raw routes do; a spend also goes stale when the pool's Merkle root moves, so submit promptly and rebuild
    /// on "merkle_root does not match".
    /// </summary>
    public static class PrivateRawTxService
    {
        public const string AssetVfx = "VFX";
        public static readonly TimeSpan PendingTtl = TimeSpan.FromMinutes(30);

        private static readonly ConcurrentDictionary<string, PendingRaw> Pending = new(StringComparer.Ordinal);
        private sealed record PendingRaw(Transaction Tx, DateTime CreatedUtc);

        // ── Results ───────────────────────────────────────────────────────────────────────────────────────────

        public sealed class RawNote
        {
            public string Commitment { get; set; } = "";
            public string? NoteHash { get; set; }
            public decimal Amount { get; set; }
            /// <summary>The note's 32-byte randomness (a spend secret; returned only to the viewing-key holder).</summary>
            public string RandomnessB64 { get; set; } = "";
            public long TreePosition { get; set; }
            public long BlockHeight { get; set; }
            public string? TxHash { get; set; }
            public bool Spent { get; set; }
            /// <summary>A transaction in this node's mempool already spends the note.</summary>
            public bool PendingSpend { get; set; }
            public bool Spendable { get; set; }
            public string? Reason { get; set; }
        }

        public sealed class ScanResult
        {
            public bool Success { get; set; }
            public string? Message { get; set; }
            public string ZfxAddress { get; set; } = "";
            public long FromHeight { get; set; }
            public long ToHeight { get; set; }
            public int BlocksScanned { get; set; }
            public List<RawNote> Notes { get; set; } = new();
            public decimal UnspentBalance { get; set; }
            public decimal SpendableBalance { get; set; }
            public bool ProofRulesActive { get; set; }
        }

        public sealed class BuildResult
        {
            public bool Success { get; set; }
            public string? Message { get; set; }
            public string? Hash { get; set; }
            public Transaction? Transaction { get; set; }
            public decimal Fee { get; set; }
            public List<string> SpentCommitments { get; set; } = new();
            public decimal ChangeAmount { get; set; }
            /// <summary>True for a shield: the caller signs <see cref="Hash"/> with the transparent key.</summary>
            public bool RequiresSignature { get; set; }
            public string? MerkleRoot { get; set; }
            public DateTime ExpiresUtc { get; set; }

            internal static BuildResult Fail(string message) => new() { Success = false, Message = message };
        }

        // ── Keys ──────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>A 32-byte key as Base64 or as 64 hex characters (optionally 0x-prefixed).</summary>
        public static bool TryParseKey32(string? text, out byte[] key, out string? error)
        {
            key = Array.Empty<byte>();
            error = null;
            if (string.IsNullOrWhiteSpace(text))
            {
                error = "Viewing key is required.";
                return false;
            }
            var t = text.Trim();
            if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                t = t[2..];
            if (t.Length == 64 && t.All(Uri.IsHexDigit))
            {
                key = Convert.FromHexString(t);
                return true;
            }
            try
            {
                key = Convert.FromBase64String(t);
            }
            catch
            {
                error = "Viewing key must be 32 bytes, as Base64 or 64 hex characters.";
                return false;
            }
            if (key.Length != 32)
            {
                key = Array.Empty<byte>();
                error = "Viewing key must be 32 bytes.";
                return false;
            }
            return true;
        }

        /// <summary>
        /// Key material for <paramref name="zfxAddress"/> from its viewing key alone: the encryption secret is derived from the
        /// viewing key, and its public key must be the one the <c>zfx_</c> address encodes, which proves the key belongs to
        /// the address before anything is decrypted or spent with it.
        /// </summary>
        public static bool TryKeyMaterial(string? zfxAddress, byte[] viewingKey32, out ShieldedKeyMaterial? keys, out string? error)
        {
            keys = null;
            error = null;
            if (viewingKey32 == null || viewingKey32.Length != 32)
            {
                error = "Viewing key must be 32 bytes.";
                return false;
            }
            if (!ShieldedAddressCodec.TryDecodeEncryptionKey(zfxAddress, out var pub33, out var derr) || pub33 == null)
            {
                error = derr ?? "Invalid zfx address.";
                return false;
            }
            byte[] encPriv;
            byte[] derivedPub;
            try
            {
                encPriv = ShieldedHdDerivation.DeriveEncryptionPrivateKeyFromViewingKey(viewingKey32);
                derivedPub = new NBitcoin.Key(encPriv).PubKey.ToBytes();
            }
            catch (Exception ex)
            {
                error = $"Viewing key is not usable: {ex.Message}";
                return false;
            }
            if (!derivedPub.AsSpan().SequenceEqual(pub33))
            {
                error = "The viewing key does not belong to this zfx address.";
                return false;
            }
            keys = new ShieldedKeyMaterial
            {
                SpendingKey32 = Array.Empty<byte>(),
                ViewingKey32 = viewingKey32,
                EncryptionPrivateKey32 = encPriv,
                EncryptionPublicKey33 = pub33,
                ZfxAddress = zfxAddress!
            };
            return true;
        }

        // ── Notes ─────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Stateless scan: every VFX note the viewing key can open in blocks [from, to], with its tree position, whether it
        /// is spent (nullifier on chain) or about to be (nullifier in this node's mempool), and whether it can be spent
        /// under the rules of the next block. Only blocks that put a commitment into the pool are read (the pool index
        /// says which), so the cost is the number of private blocks, not the chain length. Defaults: from the proof-rules
        /// height once the epoch is active (older notes left the tree at the reset), else from genesis; to the tip.
        /// </summary>
        public static ScanResult ScanNotes(string? zfxAddress, byte[] viewingKey32, long? fromHeight, long? toHeight,
            bool includeSpent, LiteDatabase? privacyDb = null)
        {
            var result = new ScanResult { ZfxAddress = zfxAddress ?? "" };
            if (!TryKeyMaterial(zfxAddress, viewingKey32, out var keys, out var kErr))
            {
                result.Message = kErr;
                return result;
            }

            var db = privacyDb ?? PrivacyDbContext.GetPrivacyDb();
            var tip = Globals.LastBlock?.Height ?? 0;
            var nextHeight = tip + 1;
            var epochNext = PrivacyEpoch.ProofRulesActive(nextHeight);
            var to = Math.Min(toHeight ?? tip, tip);
            var from = fromHeight ?? (epochNext ? Math.Max(0, Globals.PrivateTxProofRulesHeight) : 0);
            if (from < 0) from = 0;
            result.FromHeight = from;
            result.ToHeight = to;
            result.ProofRulesActive = epochNext;

            var records = db.GetCollection<CommitmentRecord>(PrivacyDbContext.PRIV_COMMITMENTS)
                .Find(x => x.AssetType == AssetVfx && x.BlockHeight >= from && x.BlockHeight <= to)
                .ToList();
            var byCommitment = new Dictionary<string, CommitmentRecord>(StringComparer.Ordinal);
            foreach (var r in records)
                if (!string.IsNullOrEmpty(r.Commitment) && !byCommitment.ContainsKey(r.Commitment))
                    byCommitment[r.Commitment] = r;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var h in records.Select(r => r.BlockHeight).Distinct().OrderBy(x => x))
            {
                var block = BlockchainData.GetBlockByHeight(h);
                if (block?.Transactions == null)
                    continue;
                result.BlocksScanned++;
                foreach (var tx in block.Transactions)
                {
                    if (tx?.Data == null || !PrivateTransactionTypes.IsPrivateTransaction(tx.TransactionType))
                        continue;
                    if (!PrivateTxPayloadCodec.TryDecode(tx.Data, out var payload, out _) || payload == null)
                        continue;

                    foreach (var o in payload.Outs ?? new List<PrivateShieldedOutput>())
                        TryCollect(o.CommitmentB64, o.EncryptedNoteB64, tx.Hash, h);
                    TryCollect(payload.FeeOutputCommitmentB64, payload.FeeOutputEncryptedNoteB64, tx.Hash, h);
                }
            }

            result.Notes = result.Notes.OrderBy(n => n.TreePosition).ToList();
            result.UnspentBalance = result.Notes.Where(n => !n.Spent && !n.PendingSpend).Sum(n => n.Amount);
            result.SpendableBalance = result.Notes.Where(n => n.Spendable).Sum(n => n.Amount);
            result.Success = true;
            return result;

            void TryCollect(string? commitmentB64, string? encryptedNoteB64, string? txHash, long height)
            {
                if (string.IsNullOrWhiteSpace(commitmentB64) || string.IsNullOrWhiteSpace(encryptedNoteB64) || !seen.Add(commitmentB64))
                    return;
                byte[] enc;
                try { enc = Convert.FromBase64String(encryptedNoteB64); } catch { return; }
                if (!ShieldedNoteEncryption.TryOpen(enc, keys!.EncryptionPrivateKey32, out var plain, out _))
                    return;
                if (!ShieldedPlainNoteCodec.TryDeserializeUtf8(plain!, out var note, out _) || note == null)
                    return;
                if (!string.Equals(note.AssetType ?? AssetVfx, AssetVfx, StringComparison.Ordinal))
                    return;
                byte[] r32 = Array.Empty<byte>();
                try { r32 = Convert.FromBase64String(note.RandomnessB64 ?? ""); } catch { }

                var raw = new RawNote
                {
                    Commitment = commitmentB64,
                    Amount = note.Amount,
                    RandomnessB64 = Convert.ToBase64String(r32),
                    BlockHeight = height,
                    TxHash = txHash
                };
                if (!byCommitment.TryGetValue(commitmentB64, out var rec))
                {
                    raw.Reason = "The note is not in this node's pool index.";
                }
                else
                {
                    raw.TreePosition = rec.TreePosition;
                    raw.NoteHash = rec.NoteHash;
                    var (spent, pending) = SpendState(viewingKey32, rec, note.Amount, r32, db);
                    raw.Spent = spent;
                    raw.PendingSpend = pending;
                    if (spent) raw.Reason = "Spent.";
                    else if (pending) raw.Reason = "A pending transaction spends this note.";
                    else if (r32.Length != PlonkNative.ScalarSize) raw.Reason = "The note has no 32-byte randomness.";
                    else if (epochNext && !PrivacyField.IsCanonicalLe(r32)) raw.Reason = "Pre-epoch randomness; the note cannot be proven.";
                    else if (epochNext && !OwnedByThisKey(rec, note.Amount, r32, keys)) raw.Reason = "This note names another owner key; only its owner can spend it.";
                    else raw.Spendable = true;
                }
                if (raw.Spent && !includeSpent)
                    return;
                result.Notes.Add(raw);
            }
        }

        /// <summary>Stage 3: the stored leaf must be the owner-bound note hash for OUR owner key, else the note was addressed to someone else's key and we cannot spend it.</summary>
        private static bool OwnedByThisKey(CommitmentRecord rec, decimal amount, byte[] r32, ShieldedKeyMaterial keys)
        {
            try
            {
                if (string.IsNullOrEmpty(rec.NoteHash) || !PrivacyPedersenAmount.TryToScaledU64(amount, out var scaled, out _)) return false;
                return string.Equals(rec.NoteHash, Convert.ToBase64String(PoseidonV1.NoteHashV2(scaled, r32, keys.OwnerPk32)), StringComparison.Ordinal);
            }
            catch { return false; }
        }

        /// <summary>Spent on chain / pending in the mempool, checking every nullifier derivation a spend of this note could have used.</summary>
        private static (bool spent, bool pending) SpendState(byte[] vk, CommitmentRecord rec, decimal amount, byte[] r32, LiteDatabase db)
        {
            var candidates = new List<string>();
            var pos = (ulong)Math.Max(0, rec.TreePosition);
            if (r32.Length == PlonkNative.ScalarSize && PrivacyPedersenAmount.TryToScaledU64(amount, out var scaled, out _))
            {
                try
                {
                    if (PoseidonV1.IsAvailable && PrivacyField.IsCanonicalLe(r32))
                    {
                        var nk = PrivacyField.ReduceLe(vk);
                        candidates.Add(Convert.ToBase64String(PoseidonV1.Nullifier(nk, PoseidonV1.NoteHashV2(scaled, r32, PoseidonV1.OwnerPk(nk)), pos)));
                    }
                }
                catch { /* native unavailable */ }
                try
                {
                    var nh = NoteHashService.Compute(scaled, r32);
                    candidates.Add(Convert.ToBase64String(NullifierService.DeriveFromNoteHash(vk, nh, pos)));
                }
                catch { /* native unavailable */ }
            }
            try
            {
                var g1 = Convert.FromBase64String(rec.Commitment);
                if (g1.Length == PlonkNative.G1CompressedSize)
                    candidates.Add(Convert.ToBase64String(NullifierService.DeriveNullifier(vk, g1, pos)));
            }
            catch { /* not a G1 commitment */ }

            var spent = candidates.Any(n => NullifierService.IsNullifierSpentInDb(n, AssetVfx, db));
            var pending = !spent && candidates.Any(n => Globals.MempoolNullifiers.ContainsKey(MempoolNullifierTracker.MakeKey(AssetVfx, n)));
            return (spent, pending);
        }

        private static UnspentCommitment ToUnspent(RawNote n) => new()
        {
            Commitment = n.Commitment,
            AssetType = AssetVfx,
            Amount = n.Amount,
            Randomness = Convert.FromBase64String(n.RandomnessB64),
            TreePosition = n.TreePosition,
            BlockHeight = n.BlockHeight,
            IsSpent = false
        };

        // ── Builders ──────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>T→Z for an address this node does not hold: built and proven here, signed by the caller.</summary>
        public static BuildResult BuildShield(string? fromAddress, string? recipientZfxAddress, decimal amount, string? memo,
            decimal? transparentFee, LiteDatabase? privacyDb = null, long? forHeight = null)
        {
            if (fromAddress != null && fromAddress.StartsWith("xRBX", StringComparison.Ordinal))
                return BuildResult.Fail("Privacy transactions cannot involve reserve (xRBX) addresses.");
            if (string.IsNullOrWhiteSpace(fromAddress) || !AddressValidateUtility.ValidateAddress(fromAddress))
                return BuildResult.Fail("A valid FromAddress is required.");
            if (!ShieldedAddressCodec.TryDecodeEncryptionKey(recipientZfxAddress, out _, out var zErr))
                return BuildResult.Fail(zErr ?? "Invalid recipient zfx address.");
            if (amount <= 0)
                return BuildResult.Fail("Shield amount must be positive.");
            if (transparentFee.HasValue && transparentFee.Value < 0)
                return BuildResult.Fail("TransparentFee cannot be negative.");

            var nonce = AccountStateTrei.GetNextNonce(fromAddress);
            var ts = TimeUtil.GetTime();
            if (!VfxPrivateTransactionBuilder.TryBuildShield(fromAddress, amount, Globals.MinFeePerKB, nonce, ts, recipientZfxAddress!, memo,
                    out var tx, out var err, privacyDb, forHeight))
                return BuildResult.Fail(err ?? "Build failed.");

            // The shield circuit binds amount and note hash, not the fee, so the fee can be settled after proving (as the
            // wallet routes do); the hash the caller signs is the final one.
            tx!.Fee = (transparentFee ?? FeeCalcService.CalculateTXFee(tx)).ToNormalizeDecimal();
            tx.Signature = "";
            tx.BuildPrivate();

            var expires = Remember(tx);
            return new BuildResult
            {
                Success = true,
                Hash = tx.Hash,
                Transaction = tx,
                Fee = tx.Fee,
                RequiresSignature = true,
                ExpiresUtc = expires,
                Message = "Sign the Hash with the FromAddress key and submit Hash + Signature via SendRawPrivateTx."
            };
        }

        /// <summary>Z→T: finds the caller's notes with the viewing key, selects inputs, proves, returns a complete transaction.</summary>
        public static BuildResult BuildUnshield(string? zfxAddress, byte[] viewingKey32, string? transparentToAddress, decimal amount,
            IReadOnlyList<string>? inputCommitments, LiteDatabase? privacyDb = null, long? forHeight = null)
        {
            if (transparentToAddress != null && transparentToAddress.StartsWith("xRBX", StringComparison.Ordinal))
                return BuildResult.Fail("Privacy transactions cannot involve reserve (xRBX) addresses.");
            if (string.IsNullOrWhiteSpace(transparentToAddress) || !AddressValidateUtility.ValidateAddress(transparentToAddress))
                return BuildResult.Fail("A valid TransparentToAddress is required.");
            if (amount <= 0)
                return BuildResult.Fail("TransparentAmount must be positive.");
            if (!TrySelect(zfxAddress, viewingKey32, amount, inputCommitments, privacyDb, out var keys, out var inputs, out var change, out var sErr))
                return BuildResult.Fail(sErr!);

            if (!VfxPrivateTransactionBuilder.TryBuildUnshield(inputs!, amount, transparentToAddress, keys!, TimeUtil.GetTime(),
                    out var tx, out var err, privacyDb, forHeight))
                return BuildResult.Fail(err ?? "Build failed.");
            return Built(tx!, inputs!, change);
        }

        /// <summary>Z→Z: as <see cref="BuildUnshield"/>, paying another <c>zfx_</c> address (or the caller's own, to consolidate).</summary>
        public static BuildResult BuildPrivateTransfer(string? zfxAddress, byte[] viewingKey32, string? recipientZfxAddress, decimal amount,
            IReadOnlyList<string>? inputCommitments, LiteDatabase? privacyDb = null, long? forHeight = null)
        {
            if (!ShieldedAddressCodec.TryDecodeEncryptionKey(recipientZfxAddress, out _, out var zErr))
                return BuildResult.Fail(zErr ?? "Invalid RecipientZfxAddress.");
            if (amount <= 0)
                return BuildResult.Fail("PaymentAmount must be positive.");
            if (!TrySelect(zfxAddress, viewingKey32, amount, inputCommitments, privacyDb, out var keys, out var inputs, out var change, out var sErr))
                return BuildResult.Fail(sErr!);

            if (!VfxPrivateTransactionBuilder.TryBuildPrivateTransfer(inputs!, amount, recipientZfxAddress!, keys!, TimeUtil.GetTime(),
                    out var tx, out var err, privacyDb, forHeight))
                return BuildResult.Fail(err ?? "Build failed.");
            return Built(tx!, inputs!, change);
        }

        private static bool TrySelect(string? zfxAddress, byte[] viewingKey32, decimal amount, IReadOnlyList<string>? inputCommitments,
            LiteDatabase? privacyDb, out ShieldedKeyMaterial? keys, out IReadOnlyList<UnspentCommitment>? inputs, out decimal change, out string? error)
        {
            inputs = null;
            change = 0;
            if (!TryKeyMaterial(zfxAddress, viewingKey32, out keys, out error))
                return false;

            var scan = ScanNotes(zfxAddress, viewingKey32, null, null, includeSpent: false, privacyDb);
            if (!scan.Success)
            {
                error = scan.Message ?? "Scan failed.";
                return false;
            }
            var candidates = scan.Notes.Where(n => n.Spendable).ToList();
            if (inputCommitments != null && inputCommitments.Count > 0)
            {
                var wanted = new HashSet<string>(inputCommitments.Where(c => !string.IsNullOrWhiteSpace(c)), StringComparer.Ordinal);
                if (wanted.Count > Globals.MaxPrivateTxInputs)
                {
                    error = $"At most {Globals.MaxPrivateTxInputs} inputs per transaction.";
                    return false;
                }
                foreach (var c in wanted)
                {
                    var note = scan.Notes.FirstOrDefault(n => n.Commitment == c);
                    if (note == null) { error = $"Input {c} is not a note of this address in the pool."; return false; }
                    if (!note.Spendable) { error = $"Input {c} cannot be spent: {note.Reason}"; return false; }
                }
                candidates = candidates.Where(n => wanted.Contains(n.Commitment)).ToList();
                var sum = candidates.Sum(n => n.Amount);
                var required = amount + Globals.PrivateTxFixedFee;
                if (sum < required)
                {
                    error = $"The chosen inputs hold {sum} VFX; {required} is needed (amount + fixed shielded fee {Globals.PrivateTxFixedFee}).";
                    return false;
                }
                inputs = candidates.OrderBy(n => n.TreePosition).Select(ToUnspent).ToList();
                change = sum - required;
                return true;
            }

            if (candidates.Count == 0)
            {
                error = scan.Notes.Count == 0
                    ? "No shielded VFX notes found for this address in the scanned range."
                    : "No spendable shielded VFX notes (all spent, pending, or from before the proof-rules epoch).";
                return false;
            }
            if (!CommitmentSelectionService.TrySelectInputs(candidates.Select(ToUnspent).ToList(), amount + Globals.PrivateTxFixedFee,
                    out var selected, out change, out var selErr))
            {
                error = selErr ?? "Input selection failed.";
                return false;
            }
            inputs = selected;
            return true;
        }

        private static BuildResult Built(Transaction tx, IReadOnlyList<UnspentCommitment> inputs, decimal change)
        {
            PrivateTxPayloadCodec.TryDecode(tx.Data, out var payload, out _);
            var expires = Remember(tx);
            return new BuildResult
            {
                Success = true,
                Hash = tx.Hash,
                Transaction = tx,
                Fee = Globals.PrivateTxFixedFee,
                SpentCommitments = inputs.Select(i => i.Commitment).ToList(),
                ChangeAmount = change,
                RequiresSignature = false,
                MerkleRoot = payload?.MerkleRootB64,
                ExpiresUtc = expires,
                Message = "The transaction is complete (ZK-authorised, no signature). Submit the Hash via SendRawPrivateTx promptly: it goes stale when the pool's Merkle root moves."
            };
        }

        // ── Pending cache, verify, send ───────────────────────────────────────────────────────────────────────

        private static DateTime Remember(Transaction tx)
        {
            var now = DateTime.UtcNow;
            foreach (var kv in Pending)
                if (now - kv.Value.CreatedUtc > PendingTtl)
                    Pending.TryRemove(kv.Key, out _);
            Pending[tx.Hash] = new PendingRaw(tx, now);
            return now + PendingTtl;
        }

        public static bool TryGetPending(string? hash, out Transaction? tx)
        {
            tx = null;
            if (string.IsNullOrWhiteSpace(hash) || !Pending.TryGetValue(hash, out var p))
                return false;
            if (DateTime.UtcNow - p.CreatedUtc > PendingTtl)
            {
                Pending.TryRemove(hash, out _);
                return false;
            }
            tx = p.Tx;
            return true;
        }

        /// <summary>Test / operator hook: forget every built transaction.</summary>
        public static void ClearPending() => Pending.Clear();

        private static (bool ok, string message, Transaction? tx) Prepare(string? hash, string? signature)
        {
            if (!TryGetPending(hash, out var tx) || tx == null)
                return (false, $"No pending raw private transaction for hash {hash}. Build it first (GetRawShieldTxData / GetRawUnshieldTxData / GetRawPrivateTransferTxData); builds expire after {PendingTtl.TotalMinutes:0} minutes.", null);
            if (PrivateTransactionTypes.IsTransparentShield(tx.TransactionType))
            {
                if (string.IsNullOrWhiteSpace(signature))
                    return (false, "A shield needs the FromAddress signature over the Hash.", null);
                tx.Signature = signature.Trim();
            }
            else
            {
                tx.Signature = PrivacyConstants.PlonkSignatureSentinel;
            }
            return (true, "", tx);
        }

        /// <summary>Runs the node's full verifier on the built transaction without broadcasting it.</summary>
        public static async Task<(bool ok, string message)> VerifyAsync(string? hash, string? signature)
        {
            var (ok, msg, tx) = Prepare(hash, signature);
            if (!ok || tx == null)
                return (false, msg);
            return await TransactionValidatorService.VerifyTX(tx);
        }

        /// <summary>Verifies, claims the build (once), admits it to the mempool and broadcasts it.</summary>
        public static async Task<(bool ok, string message, string? hash)> SendAsync(string? hash, string? signature)
        {
            var (ok, msg, tx) = Prepare(hash, signature);
            if (!ok || tx == null)
                return (false, msg, null);

            var verdict = await TransactionValidatorService.VerifyTX(tx);
            if (!verdict.Item1)
                return (false, $"Transaction verification failed: {verdict.Item2}", tx.Hash);

            // Claim only once it is known-good, so a wrong signature can be corrected and retried; the atomic remove
            // also stops a concurrent duplicate submit from broadcasting twice.
            if (!Pending.TryRemove(tx.Hash, out _))
                return (false, $"Transaction {tx.Hash} was already submitted.", tx.Hash);

            await PrivacyApiHelper.BroadcastAdmittedPrivateTxAsync(tx);
            return (true, "Broadcast.", tx.Hash);
        }
    }
}
