using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using ReserveBlockCore.Controllers;
using ReserveBlockCore.Data;
using ReserveBlockCore.Models;
using ReserveBlockCore.Models.Privacy;
using ReserveBlockCore.Privacy;
using ReserveBlockCore.Services;
using ReserveBlockCore.Utilities;

namespace ReserveBlockCore.Controllers
{
    /// <summary>VFX shielded pool operations (Phase 7). Uses local <see cref="Account"/> + <c>DB_Privacy</c>; identities are VFX addresses, not numeric user ids.</summary>
    [ActionFilterController]
    [Route("privacyapi/[controller]")]
    [Route("privacyapi/[controller]/{somePassword?}")]
    [ApiController]
    public class PrivacyV1Controller : ControllerBase
    {
        private static string Fail(string msg) =>
            JsonConvert.SerializeObject(new { Success = false, Message = msg });

        private static string Ok(object payload) =>
            JsonConvert.SerializeObject(new { Success = true, Result = payload });

        /// <summary>Transparent VFX → shielded (T→Z). Signs with local account keys for <paramref name="req"/>.FromAddress.</summary>
        [HttpPost("ShieldVFX")]
        public async Task<string> ShieldVFX([FromBody] ShieldVfxRequest req)
        {
            try
            {
                if (req == null || string.IsNullOrWhiteSpace(req.FromAddress))
                    return Fail("FromAddress is required.");
                if (!AddressValidateUtility.ValidateAddress(req.FromAddress))
                    return Fail("Invalid FromAddress.");
                var account = AccountData.GetSingleAccount(req.FromAddress);
                if (account == null)
                    return Fail("FromAddress not found in local wallet.");

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
                        null))
                    return Fail(err ?? "Build failed.");

                tx!.Fee = req.TransparentFee ?? FeeCalcService.CalculateTXFee(tx);
                tx.BuildPrivate();
                var pk = account.GetPrivKey;
                if (pk == null)
                    return Fail("Cannot sign (wallet locked?).");
                var sig = SignatureService.CreateSignature(tx.Hash, pk, account.PublicKey);
                if (sig == "ERROR")
                    return Fail("Signature failed.");
                tx.Signature = sig;

                var (_, json) = await PrivacyApiHelper.BroadcastVerifiedPrivateTxAsync(tx);
                return json;
            }
            catch (Exception ex)
            {
                return Fail(ex.Message);
            }
        }

        /// <summary>Shielded VFX → transparent (Z→T).</summary>
        [HttpPost("UnshieldVFX")]
        public async Task<string> UnshieldVFX([FromBody] UnshieldVfxRequest req)
        {
            try
            {
                if (req == null || string.IsNullOrWhiteSpace(req.ZfxAddress))
                    return Fail("ZfxAddress is required.");
                var w = ShieldedWalletService.FindByZfxAddress(req.ZfxAddress);
                if (w == null)
                    return Fail("No shielded wallet row for this zfx address.");
                if (!PrivacyApiHelper.TryGetKeyMaterial(w, req.WalletPassword, out var keys, out var kmErr))
                    return Fail(kmErr ?? "Keys");

                var fee = Globals.PrivateTxFixedFee;
                if (!CommitmentSelectionService.TrySelectInputs(
                        w.UnspentCommitments,
                        req.TransparentAmount + fee,
                        out var inputs,
                        out _,
                        out var selErr))
                    return Fail(selErr ?? "Input selection failed.");

                var ts = TimeUtil.GetTime();
                if (!VfxPrivateTransactionBuilder.TryBuildUnshield(
                        inputs,
                        req.TransparentAmount,
                        req.TransparentToAddress,
                        keys,
                        ts,
                        out var tx,
                        out var berr,
                        null))
                    return Fail(berr ?? "Build failed.");

                var br = await PrivacyApiHelper.BroadcastVerifiedPrivateTxAsync(tx!);
                return br.json;
            }
            catch (Exception ex)
            {
                return Fail(ex.Message);
            }
        }

        /// <summary>Shielded VFX → shielded VFX (Z→Z).</summary>
        [HttpPost("PrivateTransferVFX")]
        public async Task<string> PrivateTransferVFX([FromBody] PrivateTransferVfxRequest req)
        {
            try
            {
                if (req == null || string.IsNullOrWhiteSpace(req.ZfxAddress))
                    return Fail("ZfxAddress is required.");
                var w = ShieldedWalletService.FindByZfxAddress(req.ZfxAddress);
                if (w == null)
                    return Fail("No shielded wallet row for this zfx address.");
                if (!PrivacyApiHelper.TryGetKeyMaterial(w, req.WalletPassword, out var keys, out var kmErr))
                    return Fail(kmErr ?? "Keys");

                var fee = Globals.PrivateTxFixedFee;
                if (!CommitmentSelectionService.TrySelectInputs(
                        w.UnspentCommitments,
                        req.PaymentAmount + fee,
                        out var inputs,
                        out _,
                        out var selErr))
                    return Fail(selErr ?? "Input selection failed.");

                var ts = TimeUtil.GetTime();
                if (!VfxPrivateTransactionBuilder.TryBuildPrivateTransfer(
                        inputs,
                        req.PaymentAmount,
                        req.RecipientZfxAddress,
                        keys,
                        ts,
                        out var tx,
                        out var berr,
                        null))
                    return Fail(berr ?? "Build failed.");

                var br = await PrivacyApiHelper.BroadcastVerifiedPrivateTxAsync(tx!);
                return br.json;
            }
            catch (Exception ex)
            {
                return Fail(ex.Message);
            }
        }

        /// <summary>Per-asset shielded balances from local wallet row.</summary>
        [HttpGet("GetShieldedBalance")]
        public Task<string> GetShieldedBalance([FromQuery] string zfxAddress)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(zfxAddress))
                    return Task.FromResult(Fail("zfxAddress query required."));
                var w = ShieldedWalletService.FindByZfxAddress(zfxAddress);
                if (w == null)
                    return Task.FromResult(Ok(new { ShieldedBalances = new Dictionary<string, decimal>(), UnspentCommitments = 0 }));

                var sum = w.UnspentCommitments?.Where(c => c != null && !c.IsSpent).Sum(c => c.Amount) ?? 0;
                return Task.FromResult(Ok(new
                {
                    w.ShieldedBalances,
                    UnspentCommitments = w.UnspentCommitments?.Count ?? 0,
                    UnspentSum = sum,
                    w.LastScannedBlock,
                    w.IsViewOnly
                }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(Fail(ex.Message));
            }
        }

        /// <summary>Pool state for an asset (e.g. <c>VFX</c> or <c>VBTC:uid</c>).</summary>
        [HttpGet("GetShieldedPoolState")]
        public Task<string> GetShieldedPoolState([FromQuery] string asset = "VFX")
        {
            try
            {
                var st = ShieldedPoolService.GetState(asset);
                if (st == null)
                    return Task.FromResult(Ok(new { asset, CurrentMerkleRoot = (string?)null, TotalCommitments = 0L, TotalShieldedSupply = 0.0M, LastUpdateHeight = 0L }));
                return Task.FromResult(Ok(new
                {
                    st.AssetType,
                    st.CurrentMerkleRoot,
                    st.TotalCommitments,
                    st.TotalShieldedSupply,
                    st.LastUpdateHeight
                }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(Fail(ex.Message));
            }
        }

        /// <summary>Derive a <c>zfx_</c> address from HD seed (local HD wallet or explicit hex).</summary>
        [HttpPost("GenerateShieldedAddress")]
        public Task<string> GenerateShieldedAddress([FromBody] GenerateShieldedAddressRequest req)
        {
            try
            {
                if (req == null)
                    return Task.FromResult(Fail("Body required."));
                string? seedHex = null;
                if (req.UseLocalHdWallet)
                {
                    var hdw = HDWallet.HDWalletData.GetHDWallet();
                    if (hdw == null)
                        return Task.FromResult(Fail("No local HD wallet."));
                    seedHex = hdw.WalletSeed;
                }
                else
                {
                    seedHex = req.WalletSeedHex;
                }
                if (string.IsNullOrWhiteSpace(seedHex))
                    return Task.FromResult(Fail("WalletSeedHex or UseLocalHdWallet required."));

                var m = ShieldedHdDerivation.DeriveShieldedKeyMaterial(seedHex, req.CoinType, req.AddressIndex);
                return Task.FromResult(Ok(new
                {
                    m.ZfxAddress,
                    DerivationPath = ShieldedHdDerivation.FormatDerivationPath(req.CoinType, req.AddressIndex),
                    CoinType = req.CoinType,
                    AddressIndex = req.AddressIndex
                }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(Fail(ex.Message));
            }
        }

        /// <summary>Scan blocks for notes decryptable with wallet encryption key; updates local wallet row.</summary>
        [HttpPost("ScanShielded")]
        public Task<string> ScanShielded([FromBody] ScanShieldedRequest req)
        {
            try
            {
                if (req == null || string.IsNullOrWhiteSpace(req.ZfxAddress))
                    return Task.FromResult(Fail("ZfxAddress required."));
                if (req.ToHeight < req.FromHeight)
                    return Task.FromResult(Fail("ToHeight must be >= FromHeight."));
                var w = ShieldedWalletService.FindByZfxAddress(req.ZfxAddress);
                if (w == null)
                    return Task.FromResult(Fail("No shielded wallet row for this zfx address."));
                if (!PrivacyApiHelper.TryGetKeyMaterial(w, req.WalletPassword, out var keys, out var kmErr))
                    return Task.FromResult(Fail(kmErr ?? "Keys"));

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
                foreach (var block in blocks)
                {
                    if (block.Transactions == null)
                        continue;
                    foreach (var tx in block.Transactions)
                    {
                        if (tx?.Data == null || !PrivateTxPayloadCodec.TryDecode(tx.Data, out var payload, out _))
                            continue;
                        if (payload?.Outs == null)
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
                w.LastScannedBlock = Math.Max(w.LastScannedBlock, req.ToHeight);
                ShieldedWalletService.Upsert(w);

                return Task.FromResult(Ok(new { NotesFound = notesFound, LastScannedBlock = w.LastScannedBlock }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(Fail(ex.Message));
            }
        }

        /// <summary>Export 32-byte viewing key (Base64) for watch-only import.</summary>
        [HttpPost("ExportViewingKey")]
        public Task<string> ExportViewingKey([FromBody] ExportViewingKeyRequest req)
        {
            try
            {
                if (req == null || string.IsNullOrWhiteSpace(req.ZfxAddress))
                    return Task.FromResult(Fail("ZfxAddress required."));
                var w = ShieldedWalletService.FindByZfxAddress(req.ZfxAddress);
                if (w == null)
                    return Task.FromResult(Fail("Wallet not found."));
                if (w.ViewingKey == null || w.ViewingKey.Length == 0)
                    return Task.FromResult(Fail("No viewing key on wallet row."));
                var vk = Convert.ToBase64String(w.ViewingKey);
                return Task.FromResult(Ok(new { ViewingKeyBase64 = vk }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(Fail(ex.Message));
            }
        }

        /// <summary>Import view-only wallet from viewing key + zfx address.</summary>
        [HttpPost("ImportViewingKey")]
        public Task<string> ImportViewingKey([FromBody] ImportViewingKeyRequest req)
        {
            try
            {
                if (req == null || string.IsNullOrWhiteSpace(req.ZfxAddress))
                    return Task.FromResult(Fail("ZfxAddress required."));
                byte[] vk;
                try
                {
                    vk = Convert.FromBase64String(req.ViewingKeyBase64);
                }
                catch
                {
                    return Task.FromResult(Fail("ViewingKeyBase64 invalid."));
                }
                if (vk.Length != 32)
                    return Task.FromResult(Fail("Viewing key must be 32 bytes."));

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
                return Task.FromResult(Ok(new { Message = "Imported view-only wallet." }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(Fail(ex.Message));
            }
        }
    }
}
