using ReserveBlockCore.Data;
using ReserveBlockCore.Extensions;
using ReserveBlockCore.Models;
using ReserveBlockCore.Models.Privacy;
using ReserveBlockCore.Privacy;
using ReserveBlockCore.Services;
using ReserveBlockCore.Utilities;

namespace ReserveBlockCore.BrowserWalletServices
{
    /// <summary>
    /// vBTC privacy operations: shield (T→Z), unshield (Z→T), private transfer (Z→Z),
    /// balance queries, note scanning, and pool state — all scoped to a specific vBTC contract UID.
    /// </summary>
    public static class WalletPrivacyVbtcService
    {
        public static object GetShieldedVbtcBalance(string zfxAddress, string scUID)
        {
            if (string.IsNullOrWhiteSpace(zfxAddress) || !zfxAddress.StartsWith("zfx_"))
                return new { success = false, message = "Invalid zfx_ address." };
            if (string.IsNullOrWhiteSpace(scUID))
                return new { success = false, message = "scUID (vBTC contract UID) is required." };

            var wallet = ShieldedWalletService.FindByZfxAddress(zfxAddress);
            if (wallet == null)
                return new { success = false, message = "Wallet not found." };

            var asset = VbtcPrivacyAsset.FormatAssetKey(scUID);
            var vbtcNotes = (wallet.UnspentCommitments ?? new List<UnspentCommitment>())
                .Where(c => c != null && !c.IsSpent && string.Equals(c.AssetType, asset, StringComparison.Ordinal)).ToList();

            return new
            {
                success = true,
                zfxAddress,
                scUID,
                assetKey = asset,
                vbtcShieldedBalance = vbtcNotes.Sum(c => c.Amount),
                unspentNotes = vbtcNotes.Count
            };
        }

        public static async Task<object> ShieldVBTC(string fromAddress, string zfxAddress, string scUID, decimal amount)
        {
            if (!AddressValidateUtility.ValidateAddress(fromAddress))
                return new { success = false, message = "Invalid FromAddress." };
            if (string.IsNullOrWhiteSpace(scUID))
                return new { success = false, message = "scUID (vBTC contract UID) is required." };

            var account = AccountData.GetSingleAccount(fromAddress);
            if (account == null)
                return new { success = false, message = $"Transparent address {fromAddress} not in local wallet." };

            var nonce = AccountStateTrei.GetNextNonce(fromAddress);
            var ts = TimeUtil.GetTime();

            if (!VbtcPrivateTransactionBuilder.TryBuildShield(
                    fromAddress,
                    scUID,
                    amount,
                    Globals.MinFeePerKB,
                    nonce,
                    ts,
                    zfxAddress,
                    null,
                    out var tx,
                    out var buildErr,
                    DbContext.DB_Privacy))
                return new { success = false, message = buildErr ?? "Failed to build vBTC shield TX." };

            tx!.Fee = FeeCalcService.CalculateTXFee(tx).ToNormalizeDecimal();
            tx.BuildPrivate();
            var pk = account.GetPrivKey;
            if (pk == null)
                return new { success = false, message = "Cannot sign (wallet locked?)." };
            var sig = SignatureService.CreateSignature(tx.Hash, pk, account.PublicKey);
            if (sig == "ERROR")
                return new { success = false, message = "Signature failed." };
            tx.Signature = sig;

            var (broadcastOk, json) = await PrivacyApiHelper.BroadcastVerifiedPrivateTxAsync(tx);
            return new { success = broadcastOk, hash = tx.Hash, type = "VBTC_SHIELD", amount, fromAddress, zfxAddress, scUID, detail = json };
        }

        public static async Task<object> UnshieldVBTC(string zfxAddress, string toAddress, string scUID, decimal amount, string? password)
        {
            if (!AddressValidateUtility.ValidateAddress(toAddress))
                return new { success = false, message = $"Invalid transparent to-address: {toAddress}" };
            if (string.IsNullOrWhiteSpace(scUID))
                return new { success = false, message = "scUID (vBTC contract UID) is required." };

            var w = ShieldedWalletService.FindByZfxAddress(zfxAddress);
            if (w == null)
                return new { success = false, message = "No shielded wallet row for this zfx address." };
            if (!PrivacyApiHelper.TryGetKeyMaterial(w, password, out var keys, out var kmErr))
                return new { success = false, message = kmErr ?? "Cannot unwrap keys." };

            var asset = VbtcPrivacyAsset.FormatAssetKey(scUID);
            var fee = Globals.PrivateTxFixedFee;

            // Select vBTC inputs for the amount
            var vbtcCommitments = (w.UnspentCommitments ?? new List<UnspentCommitment>())
                .Where(c => c != null && !c.IsSpent && string.Equals(c.AssetType, asset, StringComparison.Ordinal))
                .ToList();

            if (!CommitmentSelectionService.TrySelectInputs(
                    vbtcCommitments,
                    amount,
                    out var inputs,
                    out _,
                    out var selErr))
                return new { success = false, message = selErr ?? "vBTC input selection failed." };

            // Optionally select a VFX fee input (single note >= fee)
            UnspentCommitment? vfxFeeInput = null;
            var vfxNotes = (w.UnspentCommitments ?? new List<UnspentCommitment>())
                .Where(c => c != null && !c.IsSpent && string.Equals(c.AssetType, "VFX", StringComparison.Ordinal))
                .OrderByDescending(c => c.Amount)
                .ToList();
            if (vfxNotes.Any(n => n.Amount >= fee))
                vfxFeeInput = vfxNotes.First(n => n.Amount >= fee);

            // Mark spent inputs BEFORE broadcast to prevent race with auto-scanner
            PrivacyApiHelper.MarkInputsSpentLocally(zfxAddress, inputs, asset);

            var ts = TimeUtil.GetTime();
            if (!VbtcPrivateTransactionBuilder.TryBuildUnshield(
                    scUID,
                    inputs,
                    amount,
                    toAddress,
                    keys,
                    ts,
                    out var tx,
                    out var buildErr,
                    vfxFeeInput,
                    DbContext.DB_Privacy))
            {
                // Rollback: unmark spent inputs on build failure
                PrivacyApiHelper.UnmarkInputsSpentLocally(zfxAddress, inputs, asset);
                return new { success = false, message = buildErr ?? "Failed to build vBTC unshield TX." };
            }

            var (broadcastOk, json) = await PrivacyApiHelper.BroadcastVerifiedPrivateTxAsync(tx!);

            if (!broadcastOk)
            {
                // Rollback: unmark spent inputs on broadcast failure
                PrivacyApiHelper.UnmarkInputsSpentLocally(zfxAddress, inputs, asset);
            }

            return new { success = broadcastOk, hash = tx!.Hash, type = "VBTC_UNSHIELD", amount, zfxAddress, toAddress, scUID, detail = json };
        }

        public static async Task<object> PrivateTransferVBTC(string fromZfxAddress, string toZfxAddress, string scUID, decimal amount, string? password)
        {
            if (string.IsNullOrWhiteSpace(toZfxAddress) || !toZfxAddress.StartsWith("zfx_"))
                return new { success = false, message = "Invalid recipient zfx_ address." };
            if (string.IsNullOrWhiteSpace(scUID))
                return new { success = false, message = "scUID (vBTC contract UID) is required." };

            var w = ShieldedWalletService.FindByZfxAddress(fromZfxAddress);
            if (w == null)
                return new { success = false, message = "No shielded wallet row for this zfx address." };
            if (!PrivacyApiHelper.TryGetKeyMaterial(w, password, out var keys, out var kmErr))
                return new { success = false, message = kmErr ?? "Cannot unwrap keys." };

            var asset = VbtcPrivacyAsset.FormatAssetKey(scUID);
            var fee = Globals.PrivateTxFixedFee;

            // Select vBTC inputs
            var vbtcCommitments = (w.UnspentCommitments ?? new List<UnspentCommitment>())
                .Where(c => c != null && !c.IsSpent && string.Equals(c.AssetType, asset, StringComparison.Ordinal))
                .ToList();

            if (!CommitmentSelectionService.TrySelectInputs(
                    vbtcCommitments,
                    amount,
                    out var inputs,
                    out _,
                    out var selErr))
                return new { success = false, message = selErr ?? "vBTC input selection failed." };

            // Optionally select a VFX fee input
            UnspentCommitment? vfxFeeInput = null;
            var vfxNotes = (w.UnspentCommitments ?? new List<UnspentCommitment>())
                .Where(c => c != null && !c.IsSpent && string.Equals(c.AssetType, "VFX", StringComparison.Ordinal))
                .OrderByDescending(c => c.Amount)
                .ToList();
            if (vfxNotes.Any(n => n.Amount >= fee))
                vfxFeeInput = vfxNotes.First(n => n.Amount >= fee);

            // Mark spent inputs BEFORE broadcast to prevent race with auto-scanner
            PrivacyApiHelper.MarkInputsSpentLocally(fromZfxAddress, inputs, asset);

            var ts = TimeUtil.GetTime();
            if (!VbtcPrivateTransactionBuilder.TryBuildPrivateTransfer(
                    scUID,
                    inputs,
                    amount,
                    toZfxAddress,
                    keys,
                    ts,
                    out var tx,
                    out var buildErr,
                    vfxFeeInput,
                    DbContext.DB_Privacy))
            {
                // Rollback: unmark spent inputs on build failure
                PrivacyApiHelper.UnmarkInputsSpentLocally(fromZfxAddress, inputs, asset);
                return new { success = false, message = buildErr ?? "Failed to build vBTC private transfer TX." };
            }

            var (broadcastOk, json) = await PrivacyApiHelper.BroadcastVerifiedPrivateTxAsync(tx!);

            if (!broadcastOk)
            {
                // Rollback: unmark spent inputs on broadcast failure
                PrivacyApiHelper.UnmarkInputsSpentLocally(fromZfxAddress, inputs, asset);
            }

            return new { success = broadcastOk, hash = tx!.Hash, type = "VBTC_PRIVATE_TRANSFER", amount, fromZfxAddress, toZfxAddress, scUID, detail = json };
        }

        public static object ScanShieldedVBTC(string zfxAddress, string scUID, string? password, long? fromBlock, long? toBlock)
        {
            if (string.IsNullOrWhiteSpace(zfxAddress) || !zfxAddress.StartsWith("zfx_"))
                return new { success = false, message = "Invalid zfx_ address." };
            if (string.IsNullOrWhiteSpace(scUID))
                return new { success = false, message = "scUID (vBTC contract UID) is required." };

            var w = ShieldedWalletService.FindByZfxAddress(zfxAddress);
            if (w == null)
                return new { success = false, message = "No shielded wallet row for this zfx address." };

            if (!PrivacyApiHelper.TryGetViewingKeyMaterial(w, out var keys, out var kmErr))
                return new { success = false, message = kmErr ?? "Cannot derive viewing keys." };

            var asset = VbtcPrivacyAsset.FormatAssetKey(scUID);
            long scanFrom = fromBlock ?? w.LastScannedBlock;
            long scanTo = toBlock ?? Globals.LastBlock.Height;
            int blocksScanned = 0, txsScanned = 0, newNotes = 0;

            var merged = new HashSet<string>(StringComparer.Ordinal);
            foreach (var u in w.UnspentCommitments ?? new List<UnspentCommitment>())
            {
                if (!string.IsNullOrEmpty(u.Commitment))
                    merged.Add(u.Commitment);
            }

            for (long h = scanFrom; h <= scanTo; h++)
            {
                var block = BlockchainData.GetBlockByHeight(h);
                if (block?.Transactions == null) continue;
                blocksScanned++;
                foreach (var tx in block.Transactions)
                {
                    txsScanned++;
                    if (tx?.Data == null || !PrivateTxPayloadCodec.TryDecode(tx.Data, out var payload, out _))
                        continue;
                    if (payload?.Outs == null)
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
                        // Only collect notes matching this vBTC asset key
                        if (!string.Equals(note.AssetType, asset, StringComparison.Ordinal))
                            continue;
                        var c = o.CommitmentB64;
                        if (string.IsNullOrEmpty(c) || merged.Contains(c))
                            continue;
                        merged.Add(c);
                        newNotes++;
                        byte[] r32 = Array.Empty<byte>();
                        if (!string.IsNullOrEmpty(note.RandomnessB64))
                        {
                            try { r32 = Convert.FromBase64String(note.RandomnessB64); }
                            catch { /* ignore */ }
                        }
                        w.UnspentCommitments ??= new List<UnspentCommitment>();
                        w.UnspentCommitments.Add(new UnspentCommitment
                        {
                            Commitment = c,
                            AssetType = note.AssetType ?? "",
                            Amount = note.Amount,
                            Randomness = r32,
                            TreePosition = 0,
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
            w.LastScannedBlock = Math.Max(w.LastScannedBlock, scanTo);
            ShieldedWalletService.Upsert(w);

            // Return only the vBTC balance for this contract
            decimal vbtcBalance = 0;
            if (w.ShieldedBalances.ContainsKey(asset))
                vbtcBalance = w.ShieldedBalances[asset];

            return new
            {
                success = true,
                zfxAddress,
                scUID,
                assetKey = asset,
                blocksScanned,
                transactionsScanned = txsScanned,
                newNotesFound = newNotes,
                vbtcShieldedBalance = vbtcBalance,
                fromHeight = scanFrom,
                toHeight = scanTo
            };
        }

        public static object GetVbtcShieldedPoolState(string scUID)
        {
            if (string.IsNullOrWhiteSpace(scUID))
                return new { success = false, message = "scUID (vBTC contract UID) is required." };

            var asset = VbtcPrivacyAsset.FormatAssetKey(scUID);
            var pool = ShieldedPoolService.GetOrCreateState(asset);
            return new
            {
                success = true,
                scUID,
                assetType = pool.AssetType,
                totalCommitments = pool.TotalCommitments,
                totalShieldedSupply = pool.TotalShieldedSupply,
                currentMerkleRoot = pool.CurrentMerkleRoot,
                lastUpdateHeight = pool.LastUpdateHeight
            };
        }
    }
}