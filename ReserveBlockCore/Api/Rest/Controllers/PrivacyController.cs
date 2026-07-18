using Microsoft.AspNetCore.Mvc;
using ReserveBlockCore.Api.Rest.Infrastructure;
using ReserveBlockCore.Data;
using ReserveBlockCore.Models;
using ReserveBlockCore.Models.Privacy;
using ReserveBlockCore.Privacy;
using ReserveBlockCore.Services;
using ReserveBlockCore.Utilities;

namespace ReserveBlockCore.Api.Rest.Controllers
{
    /// <summary>
    /// VFX + vBTC shielded pool (PLONK) REST API.
    /// Parallel reimplementation of Controllers/PrivacyV1Controller.cs over the same
    /// service/data layer. Identities are VFX addresses / zfx_ shielded addresses.
    /// </summary>
    public class PrivacyController : RestBaseController
    {
        #region Node status

        /// <summary>
        /// Native PLONK capabilities, strict proof flag, params mirror size
        /// </summary>
        [HttpGet("plonk-status")]
        public IActionResult GetPlonkStatus()
        {
            PLONKSetup.RefreshVerificationCapability();
            uint caps = 0;
            try
            {
                caps = PlonkNative.plonk_capabilities();
            }
            catch
            {
                /* older plonk_ffi without capabilities */
            }

            var envSet = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PLONKSetup.ParamsPathEnvironmentVariable));
            return Ok(new
            {
                ProofVerificationImplemented = PLONKSetup.IsProofVerificationImplemented,
                ProofProvingImplemented = PLONKSetup.IsProofProvingImplemented,
                EnforcePlonkProofsForZk = Globals.EnforcePlonkProofsForZk,
                ParamsBytesMirrored = Globals.PLONKParamsFileSize,
                ParamsPathEnvSet = envSet,
                NativeCapabilities = caps,
                CapVerifyV1 = (caps & PlonkNative.CapVerifyV1) != 0,
                CapParsePublicInputsV1 = (caps & PlonkNative.CapParsePublicInputsV1) != 0,
                CapProveV1 = (caps & PlonkNative.CapProveV1) != 0
            });
        }

        #endregion

        #region Shielded VFX

        /// <summary>
        /// Transparent VFX → shielded (T→Z). Signs with the local account key.
        /// </summary>
        [HttpPost("shield")]
        public async Task<IActionResult> ShieldVfx([FromBody] ShieldVfxRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.FromAddress))
                return Fail("VALIDATION", "FromAddress is required.");
            if (!AddressValidateUtility.ValidateAddress(req.FromAddress))
                return Fail("INVALID_ADDRESS", "Invalid FromAddress.");
            var account = AccountData.GetSingleAccount(req.FromAddress);
            if (account == null)
                return Fail("NOT_FOUND", "FromAddress not found in local wallet.", 404);

            var nonce = AccountStateTrei.GetNextNonce(req.FromAddress);
            var ts = TimeUtil.GetTime();
            if (!VfxPrivateTransactionBuilder.TryBuildShield(
                    req.FromAddress,
                    req.ShieldAmount,
                    Globals.MinFeePerKB,
                    nonce,
                    ts,
                    req.RecipientZfxAddress,
                    req.Memo,
                    out var tx,
                    out var err,
                    DbContext.DB_Privacy))
                return Fail("BUILD_FAILED", err ?? "Build failed.");

            tx!.Fee = req.TransparentFee ?? FeeCalcService.CalculateTXFee(tx);
            tx.BuildPrivate();
            var pk = account.GetPrivKey;
            if (pk == null)
                return Fail("WALLET_LOCKED", "Cannot sign (wallet locked?).", 401);
            var sig = SignatureService.CreateSignature(tx.Hash, pk, account.PublicKey);
            if (sig == "ERROR")
                return Fail("SIGNATURE_FAILED", "Signature failed.", 500);
            tx.Signature = sig;

            var (broadcastOk, json) = await PrivacyApiHelper.BroadcastVerifiedPrivateTxAsync(tx);

            // Save local TX record so it appears in local TX queries immediately
            if (broadcastOk)
            {
                await PrivacyApiHelper.SavePrivacyTxLocally(
                    tx, req.FromAddress, req.RecipientZfxAddress, req.ShieldAmount, TransactionStatus.Pending);
            }

            return FromLegacyJson(json, "BROADCAST_FAILED", 201);
        }

        /// <summary>
        /// Shielded VFX → transparent (Z→T)
        /// </summary>
        [HttpPost("unshield")]
        public async Task<IActionResult> UnshieldVfx([FromBody] UnshieldVfxRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.ZfxAddress))
                return Fail("VALIDATION", "ZfxAddress is required.");
            var w = ShieldedWalletService.FindByZfxAddress(req.ZfxAddress);
            if (w == null)
                return Fail("NOT_FOUND", "No shielded wallet row for this zfx address.", 404);
            if (!PrivacyApiHelper.TryGetKeyMaterial(w, req.WalletPassword, out var keys, out var kmErr))
                return Fail("KEY_MATERIAL", kmErr ?? "Keys", 401);

            var fee = Globals.PrivateTxFixedFee;
            if (!CommitmentSelectionService.TrySelectInputs(
                    w.UnspentCommitments,
                    req.TransparentAmount + fee,
                    out var inputs,
                    out _,
                    out var selErr))
                return Fail("INPUT_SELECTION_FAILED", selErr ?? "Input selection failed.");

            // Mark spent inputs BEFORE broadcast to prevent race with auto-scanner
            PrivacyApiHelper.MarkInputsSpentLocally(req.ZfxAddress, inputs, "VFX");

            var ts = TimeUtil.GetTime();
            if (!VfxPrivateTransactionBuilder.TryBuildUnshield(
                    inputs,
                    req.TransparentAmount,
                    req.TransparentToAddress,
                    keys,
                    ts,
                    out var tx,
                    out var berr,
                    DbContext.DB_Privacy))
            {
                PrivacyApiHelper.UnmarkInputsSpentLocally(req.ZfxAddress, inputs, "VFX");
                return Fail("BUILD_FAILED", berr ?? "Build failed.");
            }

            var br = await PrivacyApiHelper.BroadcastVerifiedPrivateTxAsync(tx!);

            if (!br.ok)
            {
                PrivacyApiHelper.UnmarkInputsSpentLocally(req.ZfxAddress, inputs, "VFX");
            }
            else
            {
                await PrivacyApiHelper.SavePrivacyTxLocally(
                    tx!, req.ZfxAddress, req.TransparentToAddress, req.TransparentAmount, TransactionStatus.Pending);
            }

            return FromLegacyJson(br.json, "BROADCAST_FAILED", 201);
        }

        /// <summary>
        /// Shielded VFX → shielded VFX (Z→Z)
        /// </summary>
        [HttpPost("private-transfer")]
        public async Task<IActionResult> PrivateTransferVfx([FromBody] PrivateTransferVfxRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.ZfxAddress))
                return Fail("VALIDATION", "ZfxAddress is required.");
            var w = ShieldedWalletService.FindByZfxAddress(req.ZfxAddress);
            if (w == null)
                return Fail("NOT_FOUND", "No shielded wallet row for this zfx address.", 404);
            if (!PrivacyApiHelper.TryGetKeyMaterial(w, req.WalletPassword, out var keys, out var kmErr))
                return Fail("KEY_MATERIAL", kmErr ?? "Keys", 401);

            var fee = Globals.PrivateTxFixedFee;
            if (!CommitmentSelectionService.TrySelectInputs(
                    w.UnspentCommitments,
                    req.PaymentAmount + fee,
                    out var inputs,
                    out _,
                    out var selErr))
                return Fail("INPUT_SELECTION_FAILED", selErr ?? "Input selection failed.");

            PrivacyApiHelper.MarkInputsSpentLocally(req.ZfxAddress, inputs, "VFX");

            var ts = TimeUtil.GetTime();
            if (!VfxPrivateTransactionBuilder.TryBuildPrivateTransfer(
                    inputs,
                    req.PaymentAmount,
                    req.RecipientZfxAddress,
                    keys,
                    ts,
                    out var tx,
                    out var berr,
                    DbContext.DB_Privacy))
            {
                PrivacyApiHelper.UnmarkInputsSpentLocally(req.ZfxAddress, inputs, "VFX");
                return Fail("BUILD_FAILED", berr ?? "Build failed.");
            }

            var br = await PrivacyApiHelper.BroadcastVerifiedPrivateTxAsync(tx!);

            if (!br.ok)
            {
                PrivacyApiHelper.UnmarkInputsSpentLocally(req.ZfxAddress, inputs, "VFX");
            }
            else
            {
                await PrivacyApiHelper.SavePrivacyTxLocally(
                    tx!, req.ZfxAddress, req.RecipientZfxAddress, req.PaymentAmount, TransactionStatus.Pending);
            }

            return FromLegacyJson(br.json, "BROADCAST_FAILED", 201);
        }

        /// <summary>
        /// Combine the two smallest unspent VFX notes into one (Z→Z to self)
        /// </summary>
        [HttpPost("consolidate")]
        public async Task<IActionResult> ConsolidateShieldedVfx([FromBody] ConsolidateShieldedVfxRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.ZfxAddress))
                return Fail("VALIDATION", "ZfxAddress is required.");
            var w = ShieldedWalletService.FindByZfxAddress(req.ZfxAddress);
            if (w == null)
                return Fail("NOT_FOUND", "No shielded wallet row for this zfx address.", 404);
            if (!PrivacyApiHelper.TryGetKeyMaterial(w, req.WalletPassword, out var keys, out var kmErr))
                return Fail("KEY_MATERIAL", kmErr ?? "Keys", 401);

            var vfx = (w.UnspentCommitments ?? new List<UnspentCommitment>())
                .Where(c => c != null && !c.IsSpent && string.Equals(c.AssetType, "VFX", StringComparison.Ordinal))
                .OrderBy(c => c!.Amount)
                .ThenBy(c => c!.TreePosition)
                .Cast<UnspentCommitment>()
                .ToList();
            if (vfx.Count < 2)
                return Fail("VALIDATION", "Need at least two unspent VFX notes to consolidate.");

            var first = vfx[0];
            var second = vfx[1];
            var sumAmt = first.Amount + second.Amount;
            var fee = Globals.PrivateTxFixedFee;
            if (sumAmt <= fee)
                return Fail("VALIDATION", "Combined VFX amount must exceed the fixed shielded fee.");
            var payment = sumAmt - fee;
            var inputs = first.TreePosition <= second.TreePosition
                ? new[] { first, second }
                : new[] { second, first };

            PrivacyApiHelper.MarkInputsSpentLocally(req.ZfxAddress, inputs.ToArray(), "VFX");

            var ts = TimeUtil.GetTime();
            if (!VfxPrivateTransactionBuilder.TryBuildPrivateTransfer(
                    inputs,
                    payment,
                    w.ShieldedAddress,
                    keys,
                    ts,
                    out var tx,
                    out var berr,
                    DbContext.DB_Privacy))
            {
                PrivacyApiHelper.UnmarkInputsSpentLocally(req.ZfxAddress, inputs.ToArray(), "VFX");
                return Fail("BUILD_FAILED", berr ?? "Build failed.");
            }

            var br = await PrivacyApiHelper.BroadcastVerifiedPrivateTxAsync(tx!);

            if (!br.ok)
            {
                PrivacyApiHelper.UnmarkInputsSpentLocally(req.ZfxAddress, inputs.ToArray(), "VFX");
            }
            else
            {
                await PrivacyApiHelper.SavePrivacyTxLocally(
                    tx!, req.ZfxAddress, w.ShieldedAddress, payment, TransactionStatus.Pending);
            }

            return FromLegacyJson(br.json, "BROADCAST_FAILED", 201);
        }

        /// <summary>
        /// Per-asset shielded balances from the local wallet row
        /// </summary>
        [HttpGet("balance")]
        public IActionResult GetShieldedBalance([FromQuery] string zfxAddress, [FromQuery] bool includeCommitments = false)
        {
            if (string.IsNullOrWhiteSpace(zfxAddress))
                return Fail("VALIDATION", "zfxAddress query required.");
            var w = ShieldedWalletService.FindByZfxAddress(zfxAddress);
            if (w == null)
                return Ok(new { ShieldedBalances = new Dictionary<string, decimal>(), UnspentCommitments = 0 });

            var unspentOnly = w.UnspentCommitments?.Where(c => c != null && !c.IsSpent).ToList() ?? new List<UnspentCommitment>();
            var sum = unspentOnly.Sum(c => c.Amount);
            object payload = includeCommitments
                ? new
                {
                    w!.ShieldedBalances,
                    UnspentCommitments = unspentOnly.Count,
                    UnspentSum = sum,
                    w.LastScannedBlock,
                    w.IsViewOnly,
                    Commitments = unspentOnly
                        .Select(c => new
                        {
                            c!.Commitment,
                            c.AssetType,
                            c.Amount,
                            c.TreePosition,
                            c.BlockHeight,
                            c.IsSpent
                        })
                        .ToList()
                }
                : (object)new
                {
                    w!.ShieldedBalances,
                    UnspentCommitments = unspentOnly.Count,
                    UnspentSum = sum,
                    w.LastScannedBlock,
                    w.IsViewOnly
                };

            return Ok(payload);
        }

        /// <summary>
        /// Pool state for an asset (e.g. VFX or VBTC:uid)
        /// </summary>
        [HttpGet("pool-state")]
        public IActionResult GetShieldedPoolState([FromQuery] string asset = "VFX")
        {
            var st = ShieldedPoolService.GetState(asset);
            if (st == null)
                return Ok(new { Asset = asset, CurrentMerkleRoot = (string?)null, TotalCommitments = 0L, TotalShieldedSupply = 0.0M, LastUpdateHeight = 0L });
            return Ok(new
            {
                st.AssetType,
                st.CurrentMerkleRoot,
                st.TotalCommitments,
                st.TotalShieldedSupply,
                st.LastUpdateHeight
            });
        }

        #endregion

        #region Addresses / keys

        /// <summary>
        /// List local shielded (zfx_) addresses
        /// </summary>
        [HttpGet("addresses")]
        public IActionResult GetShieldedAddresses()
        {
            var result = ReserveBlockCore.BrowserWalletServices.WalletPrivacyVfxService.GetShieldedAddresses();
            return Ok(result);
        }

        /// <summary>
        /// Create a zfx_ shielded address from a transparent account's private key
        /// </summary>
        [HttpPost("addresses/from-account")]
        public IActionResult CreateShieldedAddressFromAccount([FromBody] CreateShieldedAddressFromAccountRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.TransparentAddress))
                return Fail("VALIDATION", "TransparentAddress is required.");
            if (!AddressValidateUtility.ValidateAddress(req.TransparentAddress))
                return Fail("INVALID_ADDRESS", "Invalid TransparentAddress.");
            var account = AccountData.GetSingleAccount(req.TransparentAddress);
            if (account == null)
                return Fail("NOT_FOUND", "TransparentAddress not found in local wallet.", 404);

            var accountKey = account.GetKey;
            if (string.IsNullOrWhiteSpace(accountKey))
                return Fail("WALLET_LOCKED", "Cannot access account private key. Is the wallet locked?", 401);

            var keyMat = ShieldedHdDerivation.DeriveFromPrivateKey(accountKey);
            var wallet = ShieldedWalletService.CreateFromKeyMaterial(keyMat, req.TransparentAddress, req.WalletPassword);
            ShieldedWalletService.Upsert(wallet);

            return Created(new
            {
                keyMat.ZfxAddress,
                TransparentSourceAddress = req.TransparentAddress
            });
        }

        /// <summary>
        /// Derive a zfx_ address from an HD seed (local HD wallet or explicit hex)
        /// </summary>
        [HttpPost("addresses/generate")]
        public IActionResult GenerateShieldedAddress([FromBody] GenerateShieldedAddressRequest req)
        {
            if (req == null)
                return Fail("VALIDATION", "Body required.");
            string? seedHex = null;
            if (req.UseLocalHdWallet)
            {
                var hdw = HDWallet.HDWalletData.GetHDWallet();
                if (hdw == null)
                    return Fail("NOT_FOUND", "No local HD wallet.", 404);
                seedHex = hdw.WalletSeed;
            }
            else
            {
                seedHex = req.WalletSeedHex;
            }
            if (string.IsNullOrWhiteSpace(seedHex))
                return Fail("VALIDATION", "WalletSeedHex or UseLocalHdWallet required.");

            var m = ShieldedHdDerivation.DeriveShieldedKeyMaterial(seedHex, req.CoinType, req.AddressIndex);
            return Created(new
            {
                m.ZfxAddress,
                DerivationPath = ShieldedHdDerivation.FormatDerivationPath(req.CoinType, req.AddressIndex),
                CoinType = req.CoinType,
                AddressIndex = req.AddressIndex
            });
        }

        /// <summary>
        /// Export the 32-byte viewing key (Base64) for watch-only import
        /// </summary>
        [HttpPost("viewing-key/export")]
        public IActionResult ExportViewingKey([FromBody] ExportViewingKeyRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.ZfxAddress))
                return Fail("VALIDATION", "ZfxAddress required.");
            var w = ShieldedWalletService.FindByZfxAddress(req.ZfxAddress);
            if (w == null)
                return Fail("NOT_FOUND", "Wallet not found.", 404);
            if (w.ViewingKey == null || w.ViewingKey.Length == 0)
                return Fail("NO_VIEWING_KEY", "No viewing key on wallet row.");
            var vk = Convert.ToBase64String(w.ViewingKey);
            return Ok(new { ViewingKeyBase64 = vk });
        }

        /// <summary>
        /// Import a view-only wallet from a viewing key + zfx address
        /// </summary>
        [HttpPost("viewing-key/import")]
        public IActionResult ImportViewingKey([FromBody] ImportViewingKeyRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.ZfxAddress))
                return Fail("VALIDATION", "ZfxAddress required.");
            byte[] vk;
            try
            {
                vk = Convert.FromBase64String(req.ViewingKeyBase64);
            }
            catch
            {
                return Fail("VALIDATION", "ViewingKeyBase64 invalid.");
            }
            if (vk.Length != 32)
                return Fail("VALIDATION", "Viewing key must be 32 bytes.");

            var row = new ShieldedWallet
            {
                ShieldedAddress = req.ZfxAddress,
                TransparentSourceAddress = req.TransparentSourceAddress,
                ViewingKey = vk,
                IsViewOnly = true,
                SpendingKey = null,
                ShieldedBalances = new Dictionary<string, decimal>(),
                UnspentCommitments = new List<UnspentCommitment>(),
                LastScannedBlock = 0
            };
            ShieldedWalletService.Upsert(row);
            return Created(new { Message = "Imported view-only wallet." });
        }

        #endregion

        #region Scan / resync (VFX)

        /// <summary>
        /// Scan blocks for notes decryptable with the wallet encryption key
        /// </summary>
        [HttpPost("scan")]
        public IActionResult ScanShielded([FromBody] ScanShieldedRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.ZfxAddress))
                return Fail("VALIDATION", "ZfxAddress required.");
            if (req.ToHeight < req.FromHeight)
                return Fail("VALIDATION", "ToHeight must be >= FromHeight.");
            var w = ShieldedWalletService.FindByZfxAddress(req.ZfxAddress);
            if (w == null)
                return Fail("NOT_FOUND", "No shielded wallet row for this zfx address.", 404);

            // Scanning only needs the encryption private key (derived from viewing key).
            // Fall back to full key material if password is provided (backwards compat).
            ShieldedKeyMaterial keys;
            if (!string.IsNullOrEmpty(req.WalletPassword))
            {
                if (!PrivacyApiHelper.TryGetKeyMaterial(w, req.WalletPassword, out var fullKeys, out var kmErr))
                    return Fail("KEY_MATERIAL", kmErr ?? "Keys", 401);
                keys = fullKeys;
            }
            else
            {
                if (!PrivacyApiHelper.TryGetViewingKeyMaterial(w, out var viewKeys, out var vkErr))
                    return Fail("KEY_MATERIAL", vkErr ?? "Cannot derive viewing keys.", 401);
                keys = viewKeys;
            }

            var blocks = new List<Block>();
            for (var h = req.FromHeight; h <= req.ToHeight; h++)
            {
                var b = BlockchainData.GetBlockByHeight(h);
                if (b != null)
                    blocks.Add(b);
            }

            var merged = new HashSet<string>(StringComparer.Ordinal);
            foreach (var u in w.UnspentCommitments ?? new List<UnspentCommitment>())
            {
                if (!string.IsNullOrEmpty(u.Commitment))
                    merged.Add(u.Commitment);
            }

            var notesFound = 0;
            var notesMarkedSpent = 0;
            var transactionsScanned = 0;
            foreach (var block in blocks)
            {
                if (block.Transactions == null)
                    continue;
                foreach (var tx in block.Transactions)
                {
                    transactionsScanned++;
                    if (tx?.Data == null || !PrivateTxPayloadCodec.TryDecode(tx.Data, out var payload, out _))
                        continue;
                    if (payload == null)
                        continue;

                    // Mark wallet notes consumed by this tx's nullifiers (commitment-string matching)
                    if (payload.SpentCommitmentTreePositions != null && payload.SpentCommitmentTreePositions.Count > 0)
                    {
                        var spentCommitmentStrings = PrivacyApiHelper.LookupCommitmentStringsByTreePositions(
                            payload.SpentCommitmentTreePositions, payload.Asset);

                        if (spentCommitmentStrings.Count > 0)
                        {
                            foreach (var uc in w.UnspentCommitments ?? new List<UnspentCommitment>())
                            {
                                if (uc == null || uc.IsSpent || string.IsNullOrEmpty(uc.Commitment))
                                    continue;
                                if (spentCommitmentStrings.Contains(uc.Commitment))
                                {
                                    uc.IsSpent = true;
                                    notesMarkedSpent++;
                                    var assetKey = uc.AssetType ?? "";
                                    if (w.ShieldedBalances.ContainsKey(assetKey))
                                        w.ShieldedBalances[assetKey] -= uc.Amount;
                                }
                            }
                        }
                    }

                    // Detect new incoming notes
                    if (payload.Outs == null)
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
                        if (!ShieldedNoteEncryption.TryOpen(enc, keys.EncryptionPrivateKey32, out var plain, out _))
                            continue;
                        if (!ShieldedPlainNoteCodec.TryDeserializeUtf8(plain, out var note, out _) || note == null)
                            continue;
                        var c = o.CommitmentB64;
                        if (string.IsNullOrEmpty(c) || merged.Contains(c))
                            continue;
                        merged.Add(c);
                        notesFound++;
                        byte[] r32 = Array.Empty<byte>();
                        if (!string.IsNullOrEmpty(note.RandomnessB64))
                        {
                            try
                            {
                                r32 = Convert.FromBase64String(note.RandomnessB64);
                            }
                            catch { /* ignore */ }
                        }

                        long treePos = PrivacyApiHelper.LookupTreePositionByCommitment(c, note.AssetType ?? "");

                        w.UnspentCommitments ??= new List<UnspentCommitment>();
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
            w.LastScannedBlock = Math.Max(w.LastScannedBlock, req.ToHeight);
            ShieldedWalletService.Upsert(w);

            return Ok(new
            {
                NotesFound = notesFound,
                NotesMarkedSpent = notesMarkedSpent,
                LastScannedBlock = w.LastScannedBlock,
                BlocksScanned = blocks.Count,
                TransactionsScanned = transactionsScanned,
                FromHeight = req.FromHeight,
                ToHeight = req.ToHeight
            });
        }

        /// <summary>
        /// Wipe cached notes/balances and rescan from FromHeight (fixes corrupted balances)
        /// </summary>
        [HttpPost("resync")]
        public IActionResult ResyncShieldedWallet([FromBody] ResyncShieldedWalletRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.ZfxAddress))
                return Fail("VALIDATION", "ZfxAddress required.");

            long toHeight = req.ToHeight > 0 ? req.ToHeight : Globals.LastBlock.Height;
            long fromHeight = req.FromHeight >= 0 ? req.FromHeight : 0;

            var result = PrivacyApiHelper.ResyncShieldedWallet(req.ZfxAddress, fromHeight, toHeight);

            if (!result.Success)
                return Fail("RESYNC_FAILED", result.Error ?? "Resync failed.");

            return Ok(new
            {
                result.BlocksScanned,
                result.TransactionsScanned,
                result.NotesFound,
                result.NotesMarkedSpent,
                result.LastScannedBlock,
                result.FinalBalance,
                result.FinalUnspentCount,
                FromHeight = fromHeight,
                ToHeight = toHeight
            });
        }

        #endregion

        #region Shielded vBTC

        /// <summary>
        /// Transparent vBTC → shielded (T→Z). Signs with the local account key.
        /// </summary>
        [HttpPost("vbtc/shield")]
        public async Task<IActionResult> ShieldVbtc([FromBody] ShieldVbtcRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.FromAddress))
                return Fail("VALIDATION", "FromAddress is required.");
            if (string.IsNullOrWhiteSpace(req.VbtcContractUid))
                return Fail("VALIDATION", "VbtcContractUid is required.");
            if (!AddressValidateUtility.ValidateAddress(req.FromAddress))
                return Fail("INVALID_ADDRESS", "Invalid FromAddress.");
            var account = AccountData.GetSingleAccount(req.FromAddress);
            if (account == null)
                return Fail("NOT_FOUND", "FromAddress not found in local wallet.", 404);

            var nonce = AccountStateTrei.GetNextNonce(req.FromAddress);
            var ts = TimeUtil.GetTime();
            if (!VbtcPrivateTransactionBuilder.TryBuildShield(
                    req.FromAddress,
                    req.VbtcContractUid,
                    req.VbtcAmount,
                    Globals.MinFeePerKB,
                    nonce,
                    ts,
                    req.RecipientZfxAddress,
                    req.Memo,
                    out var tx,
                    out var err,
                    DbContext.DB_Privacy))
                return Fail("BUILD_FAILED", err ?? "Build failed.");

            tx!.Fee = req.TransparentFee ?? FeeCalcService.CalculateTXFee(tx);
            tx.BuildPrivate();
            var pk = account.GetPrivKey;
            if (pk == null)
                return Fail("WALLET_LOCKED", "Cannot sign (wallet locked?).", 401);
            var sig = SignatureService.CreateSignature(tx.Hash, pk, account.PublicKey);
            if (sig == "ERROR")
                return Fail("SIGNATURE_FAILED", "Signature failed.", 500);
            tx.Signature = sig;

            var (broadcastOk, json) = await PrivacyApiHelper.BroadcastVerifiedPrivateTxAsync(tx);

            if (broadcastOk)
            {
                await PrivacyApiHelper.SavePrivacyTxLocally(
                    tx, req.FromAddress, req.RecipientZfxAddress, req.VbtcAmount, TransactionStatus.Pending);
            }

            return FromLegacyJson(json, "BROADCAST_FAILED", 201);
        }

        /// <summary>
        /// Shielded vBTC → transparent (Z→T). Requires vBTC inputs plus a VFX fee note.
        /// </summary>
        [HttpPost("vbtc/unshield")]
        public async Task<IActionResult> UnshieldVbtc([FromBody] UnshieldVbtcRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.ZfxAddress))
                return Fail("VALIDATION", "ZfxAddress is required.");
            if (string.IsNullOrWhiteSpace(req.VbtcContractUid))
                return Fail("VALIDATION", "VbtcContractUid is required.");
            if (string.IsNullOrWhiteSpace(req.TransparentToAddress))
                return Fail("VALIDATION", "TransparentToAddress is required.");
            var w = ShieldedWalletService.FindByZfxAddress(req.ZfxAddress);
            if (w == null)
                return Fail("NOT_FOUND", "No shielded wallet row for this zfx address.", 404);
            if (!PrivacyApiHelper.TryGetKeyMaterial(w, req.WalletPassword, out var keys, out var kmErr))
                return Fail("KEY_MATERIAL", kmErr ?? "Keys", 401);

            var asset = VbtcPrivacyAsset.FormatAssetKey(req.VbtcContractUid);
            var fee = Globals.PrivateTxFixedFee;

            var vbtcCommitments = (w.UnspentCommitments ?? new List<UnspentCommitment>())
                .Where(c => c != null && !c.IsSpent && string.Equals(c.AssetType, asset, StringComparison.Ordinal))
                .ToList();

            if (!CommitmentSelectionService.TrySelectInputs(
                    vbtcCommitments,
                    req.TransparentVbtcAmount,
                    out var inputs,
                    out _,
                    out var selErr))
                return Fail("INPUT_SELECTION_FAILED", selErr ?? "vBTC input selection failed.");

            var vfxNotes = (w.UnspentCommitments ?? new List<UnspentCommitment>())
                .Where(c => c != null && !c.IsSpent && string.Equals(c.AssetType, "VFX", StringComparison.Ordinal))
                .OrderByDescending(c => c.Amount)
                .ToList();
            if (!vfxNotes.Any(n => n.Amount >= fee))
                return Fail("NO_FEE_NOTE", $"No VFX note with sufficient balance to cover the fixed ZK fee of {fee} VFX.");
            var vfxFeeInput = vfxNotes.First(n => n.Amount >= fee);

            PrivacyApiHelper.MarkInputsSpentLocally(req.ZfxAddress, inputs, asset);
            PrivacyApiHelper.MarkInputsSpentLocally(req.ZfxAddress, new[] { vfxFeeInput }, "VFX");

            var ts = TimeUtil.GetTime();
            if (!VbtcPrivateTransactionBuilder.TryBuildUnshield(
                    req.VbtcContractUid,
                    inputs,
                    req.TransparentVbtcAmount,
                    req.TransparentToAddress,
                    keys,
                    ts,
                    out var tx,
                    out var berr,
                    vfxFeeInput,
                    DbContext.DB_Privacy))
            {
                PrivacyApiHelper.UnmarkInputsSpentLocally(req.ZfxAddress, inputs, asset);
                PrivacyApiHelper.UnmarkInputsSpentLocally(req.ZfxAddress, new[] { vfxFeeInput }, "VFX");
                return Fail("BUILD_FAILED", berr ?? "Build failed.");
            }

            var br = await PrivacyApiHelper.BroadcastVerifiedPrivateTxAsync(tx!);

            if (!br.ok)
            {
                PrivacyApiHelper.UnmarkInputsSpentLocally(req.ZfxAddress, inputs, asset);
                PrivacyApiHelper.UnmarkInputsSpentLocally(req.ZfxAddress, new[] { vfxFeeInput }, "VFX");
            }
            else
            {
                await PrivacyApiHelper.SavePrivacyTxLocally(
                    tx!, req.ZfxAddress, req.TransparentToAddress, req.TransparentVbtcAmount, TransactionStatus.Pending);
            }

            return FromLegacyJson(br.json, "BROADCAST_FAILED", 201);
        }

        /// <summary>
        /// Shielded vBTC → shielded vBTC (Z→Z). Requires vBTC inputs plus a VFX fee note.
        /// </summary>
        [HttpPost("vbtc/private-transfer")]
        public async Task<IActionResult> PrivateTransferVbtc([FromBody] PrivateTransferVbtcRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.ZfxAddress))
                return Fail("VALIDATION", "ZfxAddress is required.");
            if (string.IsNullOrWhiteSpace(req.VbtcContractUid))
                return Fail("VALIDATION", "VbtcContractUid is required.");
            if (string.IsNullOrWhiteSpace(req.RecipientZfxAddress))
                return Fail("VALIDATION", "RecipientZfxAddress is required.");
            var w = ShieldedWalletService.FindByZfxAddress(req.ZfxAddress);
            if (w == null)
                return Fail("NOT_FOUND", "No shielded wallet row for this zfx address.", 404);
            if (!PrivacyApiHelper.TryGetKeyMaterial(w, req.WalletPassword, out var keys, out var kmErr))
                return Fail("KEY_MATERIAL", kmErr ?? "Keys", 401);

            var asset = VbtcPrivacyAsset.FormatAssetKey(req.VbtcContractUid);
            var fee = Globals.PrivateTxFixedFee;

            var vbtcCommitments = (w.UnspentCommitments ?? new List<UnspentCommitment>())
                .Where(c => c != null && !c.IsSpent && string.Equals(c.AssetType, asset, StringComparison.Ordinal))
                .ToList();

            if (!CommitmentSelectionService.TrySelectInputs(
                    vbtcCommitments,
                    req.PaymentAmount,
                    out var inputs,
                    out _,
                    out var selErr))
                return Fail("INPUT_SELECTION_FAILED", selErr ?? "vBTC input selection failed.");

            var vfxNotes = (w.UnspentCommitments ?? new List<UnspentCommitment>())
                .Where(c => c != null && !c.IsSpent && string.Equals(c.AssetType, "VFX", StringComparison.Ordinal))
                .OrderByDescending(c => c.Amount)
                .ToList();
            if (!vfxNotes.Any(n => n.Amount >= fee))
                return Fail("NO_FEE_NOTE", $"No VFX note with sufficient balance to cover the fixed ZK fee of {fee} VFX.");
            var vfxFeeInput = vfxNotes.First(n => n.Amount >= fee);

            PrivacyApiHelper.MarkInputsSpentLocally(req.ZfxAddress, inputs, asset);
            PrivacyApiHelper.MarkInputsSpentLocally(req.ZfxAddress, new[] { vfxFeeInput }, "VFX");

            var ts = TimeUtil.GetTime();
            if (!VbtcPrivateTransactionBuilder.TryBuildPrivateTransfer(
                    req.VbtcContractUid,
                    inputs,
                    req.PaymentAmount,
                    req.RecipientZfxAddress,
                    keys,
                    ts,
                    out var tx,
                    out var berr,
                    vfxFeeInput,
                    DbContext.DB_Privacy))
            {
                PrivacyApiHelper.UnmarkInputsSpentLocally(req.ZfxAddress, inputs, asset);
                PrivacyApiHelper.UnmarkInputsSpentLocally(req.ZfxAddress, new[] { vfxFeeInput }, "VFX");
                return Fail("BUILD_FAILED", berr ?? "Build failed.");
            }

            var br = await PrivacyApiHelper.BroadcastVerifiedPrivateTxAsync(tx!);

            if (!br.ok)
            {
                PrivacyApiHelper.UnmarkInputsSpentLocally(req.ZfxAddress, inputs, asset);
                PrivacyApiHelper.UnmarkInputsSpentLocally(req.ZfxAddress, new[] { vfxFeeInput }, "VFX");
            }
            else
            {
                await PrivacyApiHelper.SavePrivacyTxLocally(
                    tx!, req.ZfxAddress, req.RecipientZfxAddress, req.PaymentAmount, TransactionStatus.Pending);
            }

            return FromLegacyJson(br.json, "BROADCAST_FAILED", 201);
        }

        /// <summary>
        /// Combine the two smallest unspent vBTC notes into one (Z→Z to self)
        /// </summary>
        [HttpPost("vbtc/consolidate")]
        public async Task<IActionResult> ConsolidateShieldedVbtc([FromBody] ConsolidateShieldedVbtcRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.ZfxAddress))
                return Fail("VALIDATION", "ZfxAddress is required.");
            if (string.IsNullOrWhiteSpace(req.VbtcContractUid))
                return Fail("VALIDATION", "VbtcContractUid is required.");
            var w = ShieldedWalletService.FindByZfxAddress(req.ZfxAddress);
            if (w == null)
                return Fail("NOT_FOUND", "No shielded wallet row for this zfx address.", 404);
            if (!PrivacyApiHelper.TryGetKeyMaterial(w, req.WalletPassword, out var keys, out var kmErr))
                return Fail("KEY_MATERIAL", kmErr ?? "Keys", 401);

            var asset = VbtcPrivacyAsset.FormatAssetKey(req.VbtcContractUid);
            var fee = Globals.PrivateTxFixedFee;

            var vbtcNotes = (w.UnspentCommitments ?? new List<UnspentCommitment>())
                .Where(c => c != null && !c.IsSpent && string.Equals(c.AssetType, asset, StringComparison.Ordinal))
                .OrderBy(c => c!.Amount)
                .ThenBy(c => c!.TreePosition)
                .Cast<UnspentCommitment>()
                .ToList();
            if (vbtcNotes.Count < 2)
                return Fail("VALIDATION", "Need at least two unspent vBTC notes to consolidate.");

            var first = vbtcNotes[0];
            var second = vbtcNotes[1];
            var sumAmt = first.Amount + second.Amount;
            var inputs = first.TreePosition <= second.TreePosition
                ? new[] { first, second }
                : new[] { second, first };

            var vfxNotes = (w.UnspentCommitments ?? new List<UnspentCommitment>())
                .Where(c => c != null && !c.IsSpent && string.Equals(c.AssetType, "VFX", StringComparison.Ordinal))
                .OrderByDescending(c => c.Amount)
                .ToList();
            if (!vfxNotes.Any(n => n.Amount >= fee))
                return Fail("NO_FEE_NOTE", $"No VFX note with sufficient balance to cover the fixed ZK fee of {fee} VFX.");
            var vfxFeeInput = vfxNotes.First(n => n.Amount >= fee);

            PrivacyApiHelper.MarkInputsSpentLocally(req.ZfxAddress, inputs.ToArray(), asset);
            PrivacyApiHelper.MarkInputsSpentLocally(req.ZfxAddress, new[] { vfxFeeInput }, "VFX");

            var ts = TimeUtil.GetTime();
            if (!VbtcPrivateTransactionBuilder.TryBuildPrivateTransfer(
                    req.VbtcContractUid,
                    inputs,
                    sumAmt,
                    w.ShieldedAddress,
                    keys,
                    ts,
                    out var tx,
                    out var berr,
                    vfxFeeInput,
                    DbContext.DB_Privacy))
            {
                PrivacyApiHelper.UnmarkInputsSpentLocally(req.ZfxAddress, inputs.ToArray(), asset);
                PrivacyApiHelper.UnmarkInputsSpentLocally(req.ZfxAddress, new[] { vfxFeeInput }, "VFX");
                return Fail("BUILD_FAILED", berr ?? "Build failed.");
            }

            var br = await PrivacyApiHelper.BroadcastVerifiedPrivateTxAsync(tx!);

            if (!br.ok)
            {
                PrivacyApiHelper.UnmarkInputsSpentLocally(req.ZfxAddress, inputs.ToArray(), asset);
                PrivacyApiHelper.UnmarkInputsSpentLocally(req.ZfxAddress, new[] { vfxFeeInput }, "VFX");
            }
            else
            {
                await PrivacyApiHelper.SavePrivacyTxLocally(
                    tx!, req.ZfxAddress, w.ShieldedAddress, sumAmt, TransactionStatus.Pending);
            }

            return FromLegacyJson(br.json, "BROADCAST_FAILED", 201);
        }

        /// <summary>
        /// Shielded vBTC balance for a specific contract UID
        /// </summary>
        [HttpGet("vbtc/balance")]
        public IActionResult GetShieldedVbtcBalance([FromQuery] string zfxAddress, [FromQuery] string vbtcContractUid, [FromQuery] bool includeCommitments = false)
        {
            if (string.IsNullOrWhiteSpace(zfxAddress))
                return Fail("VALIDATION", "zfxAddress query required.");
            if (string.IsNullOrWhiteSpace(vbtcContractUid))
                return Fail("VALIDATION", "vbtcContractUid query required.");
            var w = ShieldedWalletService.FindByZfxAddress(zfxAddress);
            if (w == null)
                return Ok(new { ShieldedVbtcBalance = 0M, UnspentCommitments = 0 });

            var asset = VbtcPrivacyAsset.FormatAssetKey(vbtcContractUid);
            var unspentOnly = (w.UnspentCommitments ?? new List<UnspentCommitment>())
                .Where(c => c != null && !c.IsSpent && string.Equals(c.AssetType, asset, StringComparison.Ordinal))
                .ToList();
            var sum = unspentOnly.Sum(c => c.Amount);

            object payload = includeCommitments
                ? new
                {
                    VbtcContractUid = vbtcContractUid,
                    AssetKey = asset,
                    ShieldedVbtcBalance = sum,
                    UnspentCommitments = unspentOnly.Count,
                    w.LastScannedBlock,
                    w.IsViewOnly,
                    Commitments = unspentOnly
                        .Select(c => new
                        {
                            c!.Commitment,
                            c.AssetType,
                            c.Amount,
                            c.TreePosition,
                            c.BlockHeight,
                            c.IsSpent
                        })
                        .ToList()
                }
                : (object)new
                {
                    VbtcContractUid = vbtcContractUid,
                    AssetKey = asset,
                    ShieldedVbtcBalance = sum,
                    UnspentCommitments = unspentOnly.Count,
                    w.LastScannedBlock,
                    w.IsViewOnly
                };

            return Ok(payload);
        }

        /// <summary>
        /// Pool state for a vBTC contract (asset key VBTC:{uid})
        /// </summary>
        [HttpGet("vbtc/pool-state")]
        public IActionResult GetVbtcShieldedPoolState([FromQuery] string vbtcContractUid)
        {
            if (string.IsNullOrWhiteSpace(vbtcContractUid))
                return Fail("VALIDATION", "vbtcContractUid query required.");
            var asset = VbtcPrivacyAsset.FormatAssetKey(vbtcContractUid);
            var st = ShieldedPoolService.GetState(asset);
            if (st == null)
                return Ok(new { VbtcContractUid = vbtcContractUid, AssetType = asset, CurrentMerkleRoot = (string?)null, TotalCommitments = 0L, TotalShieldedSupply = 0.0M, LastUpdateHeight = 0L });
            return Ok(new
            {
                VbtcContractUid = vbtcContractUid,
                st.AssetType,
                st.CurrentMerkleRoot,
                st.TotalCommitments,
                st.TotalShieldedSupply,
                st.LastUpdateHeight
            });
        }

        /// <summary>
        /// Scan blocks for vBTC notes for a specific contract UID
        /// </summary>
        [HttpPost("vbtc/scan")]
        public IActionResult ScanShieldedVbtc([FromBody] ScanShieldedVbtcRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.ZfxAddress))
                return Fail("VALIDATION", "ZfxAddress required.");
            if (string.IsNullOrWhiteSpace(req.VbtcContractUid))
                return Fail("VALIDATION", "VbtcContractUid required.");
            if (req.ToHeight < req.FromHeight)
                return Fail("VALIDATION", "ToHeight must be >= FromHeight.");
            var w = ShieldedWalletService.FindByZfxAddress(req.ZfxAddress);
            if (w == null)
                return Fail("NOT_FOUND", "No shielded wallet row for this zfx address.", 404);

            ShieldedKeyMaterial keys;
            if (!string.IsNullOrEmpty(req.WalletPassword))
            {
                if (!PrivacyApiHelper.TryGetKeyMaterial(w, req.WalletPassword, out var fullKeys, out var kmErr))
                    return Fail("KEY_MATERIAL", kmErr ?? "Keys", 401);
                keys = fullKeys;
            }
            else
            {
                if (!PrivacyApiHelper.TryGetViewingKeyMaterial(w, out var viewKeys, out var vkErr))
                    return Fail("KEY_MATERIAL", vkErr ?? "Cannot derive viewing keys.", 401);
                keys = viewKeys;
            }

            var asset = VbtcPrivacyAsset.FormatAssetKey(req.VbtcContractUid);
            var blocks = new List<Block>();
            for (var h = req.FromHeight; h <= req.ToHeight; h++)
            {
                var b = BlockchainData.GetBlockByHeight(h);
                if (b != null)
                    blocks.Add(b);
            }

            var merged = new HashSet<string>(StringComparer.Ordinal);
            foreach (var u in w.UnspentCommitments ?? new List<UnspentCommitment>())
            {
                if (!string.IsNullOrEmpty(u.Commitment))
                    merged.Add(u.Commitment);
            }

            var notesFound = 0;
            var notesMarkedSpent = 0;
            var transactionsScanned = 0;
            foreach (var block in blocks)
            {
                if (block.Transactions == null)
                    continue;
                foreach (var tx in block.Transactions)
                {
                    transactionsScanned++;
                    if (tx?.Data == null || !PrivateTxPayloadCodec.TryDecode(tx.Data, out var payload, out _))
                        continue;
                    if (payload == null)
                        continue;

                    // Mark wallet notes consumed by this tx's nullifiers
                    if (payload.SpentCommitmentTreePositions != null && payload.SpentCommitmentTreePositions.Count > 0)
                    {
                        var spentCommitmentStrings = PrivacyApiHelper.LookupCommitmentStringsByTreePositions(
                            payload.SpentCommitmentTreePositions, payload.Asset);

                        if (spentCommitmentStrings.Count > 0)
                        {
                            foreach (var uc in w.UnspentCommitments ?? new List<UnspentCommitment>())
                            {
                                if (uc == null || uc.IsSpent || string.IsNullOrEmpty(uc.Commitment))
                                    continue;
                                if (!string.Equals(uc.AssetType, asset, StringComparison.Ordinal))
                                    continue;
                                if (spentCommitmentStrings.Contains(uc.Commitment))
                                {
                                    uc.IsSpent = true;
                                    notesMarkedSpent++;
                                    var assetKey = uc.AssetType ?? "";
                                    if (w.ShieldedBalances.ContainsKey(assetKey))
                                        w.ShieldedBalances[assetKey] -= uc.Amount;
                                }
                            }
                        }
                    }

                    // Detect new incoming vBTC notes
                    if (payload.Outs == null)
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
                        notesFound++;
                        byte[] r32 = Array.Empty<byte>();
                        if (!string.IsNullOrEmpty(note.RandomnessB64))
                        {
                            try
                            {
                                r32 = Convert.FromBase64String(note.RandomnessB64);
                            }
                            catch { /* ignore */ }
                        }

                        long treePos = PrivacyApiHelper.LookupTreePositionByCommitment(c, note.AssetType ?? "");

                        w.UnspentCommitments ??= new List<UnspentCommitment>();
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
            w.LastScannedBlock = Math.Max(w.LastScannedBlock, req.ToHeight);
            ShieldedWalletService.Upsert(w);

            decimal vbtcBalance = 0;
            if (w.ShieldedBalances.ContainsKey(asset))
                vbtcBalance = w.ShieldedBalances[asset];

            return Ok(new
            {
                VbtcContractUid = req.VbtcContractUid,
                AssetKey = asset,
                NotesFound = notesFound,
                NotesMarkedSpent = notesMarkedSpent,
                LastScannedBlock = w.LastScannedBlock,
                BlocksScanned = blocks.Count,
                TransactionsScanned = transactionsScanned,
                ShieldedVbtcBalance = vbtcBalance,
                FromHeight = req.FromHeight,
                ToHeight = req.ToHeight
            });
        }

        /// <summary>
        /// Wipe cached vBTC notes for a contract and rescan from FromHeight
        /// </summary>
        [HttpPost("vbtc/resync")]
        public IActionResult ResyncShieldedVbtc([FromBody] ResyncShieldedVbtcRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.ZfxAddress))
                return Fail("VALIDATION", "ZfxAddress required.");
            if (string.IsNullOrWhiteSpace(req.VbtcContractUid))
                return Fail("VALIDATION", "VbtcContractUid required.");

            long toHeight = req.ToHeight > 0 ? req.ToHeight : Globals.LastBlock.Height;
            long fromHeight = req.FromHeight >= 0 ? req.FromHeight : 0;

            var w = ShieldedWalletService.FindByZfxAddress(req.ZfxAddress);
            if (w == null)
                return Fail("NOT_FOUND", "No shielded wallet row for this zfx address.", 404);

            var asset = VbtcPrivacyAsset.FormatAssetKey(req.VbtcContractUid);

            // Wipe only the vBTC notes for this contract
            if (w.UnspentCommitments != null)
                w.UnspentCommitments.RemoveAll(c => string.Equals(c.AssetType, asset, StringComparison.Ordinal));
            if (w.ShieldedBalances.ContainsKey(asset))
                w.ShieldedBalances.Remove(asset);
            ShieldedWalletService.Upsert(w);

            // Rescan using the vBTC scan logic
            var scanReq = new ScanShieldedVbtcRequest
            {
                ZfxAddress = req.ZfxAddress,
                VbtcContractUid = req.VbtcContractUid,
                FromHeight = fromHeight,
                ToHeight = toHeight
            };
            return ScanShieldedVbtc(scanReq);
        }

        #endregion
    }
}
