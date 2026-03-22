using ReserveBlockCore.Data;
using ReserveBlockCore.Models;
using ReserveBlockCore.Models.Privacy;
using ReserveBlockCore.Privacy;
using ReserveBlockCore.Services;
using ReserveBlockCore.Utilities;

namespace ReserveBlockCore.BrowserWalletServices
{
    public static class WalletPrivacyVfxService
    {
        public static object GetShieldedAddresses()
        {
            var wallets = ShieldedWalletService.GetAll();
            if (wallets == null || !wallets.Any())
                return Array.Empty<object>();

            return wallets.Select(w =>
            {
                var vfxBal = (w.UnspentCommitments ?? new List<UnspentCommitment>())
                    .Where(c => c != null && !c.IsSpent && string.Equals(c.AssetType, "VFX", StringComparison.Ordinal))
                    .Sum(c => c.Amount);

                return new
                {
                    zfxAddress = w.ShieldedAddress,
                    transparentSourceAddress = w.TransparentSourceAddress ?? "",
                    vfxShieldedBalance = vfxBal,
                    unspentNotes = (w.UnspentCommitments ?? new List<UnspentCommitment>())
                        .Count(c => c != null && !c.IsSpent && string.Equals(c.AssetType, "VFX", StringComparison.Ordinal)),
                    lastScannedBlock = w.LastScannedBlock,
                    isViewOnly = w.IsViewOnly
                };
            }).ToList();
        }

        public static object CreateShieldedAddress(string address, string password)
        {
            var account = AccountData.GetSingleAccount(address);
            if (account == null)
                return new { success = false, message = $"Transparent address {address} not in local wallet." };

            var accountKey = account.GetKey;
            if (string.IsNullOrWhiteSpace(accountKey))
                return new { success = false, message = "Cannot access account private key. Is the wallet locked?" };

            var keyMat = ShieldedHdDerivation.DeriveFromPrivateKey(accountKey);
            var wallet = ShieldedWalletService.CreateFromKeyMaterial(keyMat, address, password);
            ShieldedWalletService.Upsert(wallet);

            return new { success = true, zfxAddress = keyMat.ZfxAddress, transparentSourceAddress = address };
        }

        public static object GetShieldedBalance(string zfxAddress)
        {
            if (string.IsNullOrWhiteSpace(zfxAddress) || !zfxAddress.StartsWith("zfx_"))
                return new { success = false, message = "Invalid zfx_ address." };

            var wallet = ShieldedWalletService.FindByZfxAddress(zfxAddress);
            if (wallet == null)
                return new { success = false, message = "Wallet not found." };

            var vfxNotes = (wallet.UnspentCommitments ?? new List<UnspentCommitment>())
                .Where(c => c != null && !c.IsSpent && string.Equals(c.AssetType, "VFX", StringComparison.Ordinal)).ToList();

            return new
            {
                success = true,
                zfxAddress,
                vfxShieldedBalance = vfxNotes.Sum(c => c.Amount),
                unspentNotes = vfxNotes.Count
            };
        }

        public static async Task<object> ShieldVFX(string fromAddress, string zfxAddress, decimal amount)
        {
            if (!AddressValidateUtility.ValidateAddress(fromAddress))
                return new { success = false, message = "Invalid FromAddress." };
            var account = AccountData.GetSingleAccount(fromAddress);
            if (account == null)
                return new { success = false, message = $"Transparent address {fromAddress} not in local wallet." };

            var nonce = AccountStateTrei.GetNextNonce(fromAddress);
            var ts = TimeUtil.GetTime();
            if (!VfxPrivateTransactionBuilder.TryBuildShield(
                    fromAddress,
                    amount,
                    Globals.MinFeePerKB,
                    nonce,
                    ts,
                    zfxAddress,
                    null,
                    out var tx,
                    out var buildErr,
                    DbContext.DB_Privacy))
                return new { success = false, message = buildErr ?? "Failed to build shield TX." };

            tx!.Fee = FeeCalcService.CalculateTXFee(tx);
            tx.BuildPrivate();
            var pk = account.GetPrivKey;
            if (pk == null)
                return new { success = false, message = "Cannot sign (wallet locked?)." };
            var sig = SignatureService.CreateSignature(tx.Hash, pk, account.PublicKey);
            if (sig == "ERROR")
                return new { success = false, message = "Signature failed." };
            tx.Signature = sig;

            var (broadcastOk, json) = await PrivacyApiHelper.BroadcastVerifiedPrivateTxAsync(tx);
            return new { success = broadcastOk, hash = tx.Hash, type = "VFX_SHIELD", amount, fromAddress, zfxAddress, detail = json };
        }

        public static async Task<object> UnshieldVFX(string zfxAddress, string toAddress, decimal amount, string? password)
        {
            if (!AddressValidateUtility.ValidateAddress(toAddress))
                return new { success = false, message = $"Invalid transparent to-address: {toAddress}" };

            var w = ShieldedWalletService.FindByZfxAddress(zfxAddress);
            if (w == null)
                return new { success = false, message = "No shielded wallet row for this zfx address." };
            if (!PrivacyApiHelper.TryGetKeyMaterial(w, password, out var keys, out var kmErr))
                return new { success = false, message = kmErr ?? "Cannot unwrap keys." };

            var fee = Globals.PrivateTxFixedFee;
            if (!CommitmentSelectionService.TrySelectInputs(
                    w.UnspentCommitments ?? (IReadOnlyList<UnspentCommitment>)Array.Empty<UnspentCommitment>(),
                    amount + fee,
                    out var inputs,
                    out _,
                    out var selErr))
                return new { success = false, message = selErr ?? "Input selection failed." };

            var ts = TimeUtil.GetTime();
            if (!VfxPrivateTransactionBuilder.TryBuildUnshield(
                    inputs,
                    amount,
                    toAddress,
                    keys,
                    ts,
                    out var tx,
                    out var buildErr,
                    DbContext.DB_Privacy))
                return new { success = false, message = buildErr ?? "Failed to build unshield TX." };

            var (broadcastOk, json) = await PrivacyApiHelper.BroadcastVerifiedPrivateTxAsync(tx!);
            return new { success = broadcastOk, hash = tx!.Hash, type = "VFX_UNSHIELD", amount, zfxAddress, toAddress, detail = json };
        }

        public static async Task<object> PrivateTransferVFX(string fromZfxAddress, string toZfxAddress, decimal amount, string? password)
        {
            if (string.IsNullOrWhiteSpace(toZfxAddress) || !toZfxAddress.StartsWith("zfx_"))
                return new { success = false, message = "Invalid recipient zfx_ address." };

            var w = ShieldedWalletService.FindByZfxAddress(fromZfxAddress);
            if (w == null)
                return new { success = false, message = "No shielded wallet row for this zfx address." };
            if (!PrivacyApiHelper.TryGetKeyMaterial(w, password, out var keys, out var kmErr))
                return new { success = false, message = kmErr ?? "Cannot unwrap keys." };

            var fee = Globals.PrivateTxFixedFee;
            if (!CommitmentSelectionService.TrySelectInputs(
                    w.UnspentCommitments ?? (IReadOnlyList<UnspentCommitment>)Array.Empty<UnspentCommitment>(),
                    amount + fee,
                    out var inputs,
                    out _,
                    out var selErr))
                return new { success = false, message = selErr ?? "Input selection failed." };

            var ts = TimeUtil.GetTime();
            if (!VfxPrivateTransactionBuilder.TryBuildPrivateTransfer(
                    inputs,
                    amount,
                    toZfxAddress,
                    keys,
                    ts,
                    out var tx,
                    out var buildErr,
                    DbContext.DB_Privacy))
                return new { success = false, message = buildErr ?? "Failed to build private transfer TX." };

            var (broadcastOk, json) = await PrivacyApiHelper.BroadcastVerifiedPrivateTxAsync(tx!);
            return new { success = broadcastOk, hash = tx!.Hash, type = "VFX_PRIVATE_TRANSFER", amount, fromZfxAddress, toZfxAddress, detail = json };
        }

        public static object ScanShieldedVFX(string zfxAddress, string? password, long? fromBlock, long? toBlock)
        {
            if (string.IsNullOrWhiteSpace(zfxAddress) || !zfxAddress.StartsWith("zfx_"))
                return new { success = false, message = "Invalid zfx_ address." };

            var w = ShieldedWalletService.FindByZfxAddress(zfxAddress);
            if (w == null)
                return new { success = false, message = "No shielded wallet row for this zfx address." };

            if (!PrivacyApiHelper.TryGetViewingKeyMaterial(w, out var keys, out var kmErr))
                return new { success = false, message = kmErr ?? "Cannot derive viewing keys." };

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

            return new { success = true, zfxAddress, blocksScanned, transactionsScanned = txsScanned, newNotesFound = newNotes, fromHeight = scanFrom, toHeight = scanTo };
        }

        public static object ResyncShieldedWallet(string zfxAddress, long fromHeight, long? toHeight)
        {
            if (string.IsNullOrWhiteSpace(zfxAddress) || !zfxAddress.StartsWith("zfx_"))
                return new { success = false, message = "Invalid zfx_ address." };

            long to = toHeight ?? Globals.LastBlock.Height;

            var result = PrivacyApiHelper.ResyncShieldedWallet(zfxAddress, fromHeight, to);

            if (!result.Success)
                return new { success = false, message = result.Error ?? "Resync failed." };

            return new
            {
                success = true,
                zfxAddress,
                blocksScanned = result.BlocksScanned,
                transactionsScanned = result.TransactionsScanned,
                notesFound = result.NotesFound,
                notesMarkedSpent = result.NotesMarkedSpent,
                lastScannedBlock = result.LastScannedBlock,
                finalBalance = result.FinalBalance,
                finalUnspentNotes = result.FinalUnspentCount,
                fromHeight,
                toHeight = to
            };
        }

        public static object GetPlonkStatus()
        {
            PLONKSetup.RefreshVerificationCapability();
            uint caps = 0;
            try { caps = PlonkNative.plonk_capabilities(); } catch { }

            return new
            {
                success = true,
                proofVerificationImplemented = PLONKSetup.IsProofVerificationImplemented,
                proofProvingImplemented = PLONKSetup.IsProofProvingImplemented,
                enforcePlonkProofsForZk = Globals.EnforcePlonkProofsForZk,
                nativeCapabilities = caps,
                paramsBytesMirrored = Globals.PLONKParamsFileSize
            };
        }

        public static object GetShieldedPoolState()
        {
            var pool = ShieldedPoolService.GetOrCreateState("VFX");
            return new
            {
                success = true,
                assetType = pool.AssetType,
                totalCommitments = pool.TotalCommitments,
                totalShieldedSupply = pool.TotalShieldedSupply,
                currentMerkleRoot = pool.CurrentMerkleRoot,
                lastUpdateHeight = pool.LastUpdateHeight
            };
        }
    }
}