using ReserveBlockCore.Models;
using ReserveBlockCore.Models.Privacy;

namespace ReserveBlockCore.Privacy
{
    /// <summary>
    /// vBTC V2 private TX payloads (Phase 5). Uses <see cref="VbtcPrivacyAsset"/> for <see cref="PrivateTxPayload.Asset"/>.
    /// Dual-proof fields (<c>fee_proof_b64</c>, <c>fee_tree_merkle_root</c>) are populated when <paramref name="privacyDb"/> is available. When <see cref="PlonkProverV0.IsProveAvailable"/>, <see cref="PrivateTxPlonkV0.TryPopulateV0Proofs"/> fills v0 <c>proof_b64</c> / <c>fee_proof_b64</c>.
    /// </summary>
    public static class VbtcPrivateTransactionBuilder
    {
        private const string AssetVfx = "VFX";

        public static bool TryBuildShield(
            string fromTransparentAddress,
            string vbtcContractUid,
            decimal vbtcShieldAmount,
            decimal transparentFee,
            long nonce,
            long timestamp,
            string recipientZfxAddress,
            string? memo,
            out Transaction? tx,
            out string? error,
            LiteDB.LiteDatabase? privacyDb = null)
        {
            tx = null;
            error = null;
            if (string.IsNullOrWhiteSpace(vbtcContractUid))
            {
                error = "vbtc_uid is required.";
                return false;
            }
            if (vbtcShieldAmount < Globals.MinShieldAmountVBTC)
            {
                error = $"vBTC shield amount must be at least {Globals.MinShieldAmountVBTC}.";
                return false;
            }
            if (transparentFee <= 0)
            {
                error = "Transparent fee must be positive.";
                return false;
            }
            if (!ShieldedAddressCodec.TryDecodeEncryptionKey(recipientZfxAddress, out _, out var zerr))
            {
                error = zerr ?? "Invalid recipient zfx address.";
                return false;
            }

            var asset = VbtcPrivacyAsset.FormatAssetKey(vbtcContractUid);
            if (!PrivacyPedersenAmount.TryCommitAmount(vbtcShieldAmount, out var r32, out var g1, out var perr))
            {
                error = perr;
                return false;
            }

            var plain = PrivacyPedersenAmount.CreatePlainNote(vbtcShieldAmount, r32, asset, memo);
            byte[] sealedNote;
            try
            {
                sealedNote = ShieldedNoteEncryption.SealPlainNote(plain, recipientZfxAddress);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }

            var merkle = VBTCPrivacyService.GetCurrentMerkleRootB64(vbtcContractUid, privacyDb);
            var payload = new PrivateTxPayload
            {
                Version = 1,
                Kind = "shield",
                SubType = "Shield",
                Asset = asset,
                VbtcContractUid = vbtcContractUid,
                VbtcTransparentAmount = vbtcShieldAmount,
                Outs =
                {
                    new PrivateShieldedOutput
                    {
                        Index = 0,
                        CommitmentB64 = Convert.ToBase64String(g1),
                        EncryptedNoteB64 = Convert.ToBase64String(sealedNote)
                    }
                },
                TransparentInput = fromTransparentAddress,
                TransparentAmount = vbtcShieldAmount,
                MerkleRootB64 = string.IsNullOrEmpty(merkle) ? null : merkle
            };

            tx = new Transaction
            {
                FromAddress = fromTransparentAddress,
                ToAddress = PrivacyConstants.ShieldedPoolAddress,
                Amount = 0,
                Fee = transparentFee,
                Nonce = nonce,
                Timestamp = timestamp,
                TransactionType = TransactionType.VBTC_V2_SHIELD,
                Data = PrivateTxPayloadCodec.SerializeToJson(payload),
                Signature = ""
            };
            tx.BuildPrivate();
            if (!PrivateTxPlonkV0.TryPopulateV0Proofs(tx, out var plonkErr))
            {
                tx = null;
                error = plonkErr;
                return false;
            }
            return true;
        }

        public static bool TryBuildUnshield(
            string vbtcContractUid,
            IReadOnlyList<UnspentCommitment> inputs,
            decimal transparentVbtcOut,
            string transparentToAddress,
            ShieldedKeyMaterial keys,
            long timestamp,
            out Transaction? tx,
            out string? error,
            UnspentCommitment? vfxFeeInput = null,
            LiteDB.LiteDatabase? privacyDb = null)
        {
            tx = null;
            error = null;
            if (string.IsNullOrWhiteSpace(vbtcContractUid))
            {
                error = "vbtc_uid is required.";
                return false;
            }
            var asset = VbtcPrivacyAsset.FormatAssetKey(vbtcContractUid);
            if (inputs == null || inputs.Count == 0 || inputs.Count > Globals.MaxPrivateTxInputs)
            {
                error = $"Need 1–{Globals.MaxPrivateTxInputs} inputs.";
                return false;
            }
            foreach (var i in inputs)
            {
                if (!string.Equals(i.AssetType, asset, StringComparison.Ordinal))
                {
                    error = "All inputs must match the vBTC contract asset key.";
                    return false;
                }
            }
            if (transparentVbtcOut <= 0)
            {
                error = "vBTC unshield amount must be positive.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(transparentToAddress))
            {
                error = "Recipient address is required.";
                return false;
            }

            var fee = Globals.PrivateTxFixedFee;
            var sumIn = inputs.Sum(i => i.Amount);
            if (sumIn < transparentVbtcOut + fee)
            {
                error = "Input sum must cover vBTC out + fixed VFX fee.";
                return false;
            }
            var change = sumIn - transparentVbtcOut - fee;
            if (change < 0)
            {
                error = "Negative change.";
                return false;
            }

            var nulls = new List<string>();
            var positions = new List<long>();
            foreach (var inp in inputs.OrderBy(x => x.TreePosition))
            {
                byte[] g1;
                try
                {
                    g1 = Convert.FromBase64String(inp.Commitment);
                }
                catch
                {
                    error = "Input commitment Base64 invalid.";
                    return false;
                }
                if (g1.Length != PlonkNative.G1CompressedSize)
                {
                    error = "Input commitment has wrong length.";
                    return false;
                }
                var n = NullifierService.DeriveNullifier(keys.ViewingKey32, g1, (ulong)inp.TreePosition);
                nulls.Add(Convert.ToBase64String(n));
                positions.Add(inp.TreePosition);
            }

            var outs = new List<PrivateShieldedOutput>();
            var idx = 0;
            if (change > 0)
            {
                if (!PrivacyPedersenAmount.TryCommitAmount(change, out var rCh, out var gCh, out var perr))
                {
                    error = perr;
                    return false;
                }
                var plainCh = PrivacyPedersenAmount.CreatePlainNote(change, rCh, asset);
                try
                {
                    var sealedCh = ShieldedNoteEncryption.SealPlainNote(plainCh, keys.ZfxAddress);
                    outs.Add(new PrivateShieldedOutput
                    {
                        Index = idx++,
                        CommitmentB64 = Convert.ToBase64String(gCh),
                        EncryptedNoteB64 = Convert.ToBase64String(sealedCh)
                    });
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return false;
                }
            }

            var merkleVbtc = VBTCPrivacyService.GetCurrentMerkleRootB64(vbtcContractUid, privacyDb);
            var merkleVfxFee = ShieldedPoolService.GetCurrentMerkleRootB64(AssetVfx, privacyDb);
            var payload = new PrivateTxPayload
            {
                Version = 1,
                Kind = "unshield",
                SubType = "Unshield",
                Asset = asset,
                VbtcContractUid = vbtcContractUid,
                NullsB64 = nulls,
                SpentCommitmentTreePositions = positions,
                Outs = outs,
                TransparentOutput = transparentToAddress,
                TransparentAmount = transparentVbtcOut,
                VbtcTransparentAmount = transparentVbtcOut,
                Fee = fee,
                MerkleRootB64 = string.IsNullOrEmpty(merkleVbtc) ? null : merkleVbtc,
                FeeTreeMerkleRoot = string.IsNullOrEmpty(merkleVfxFee) ? null : merkleVfxFee
            };

            if (vfxFeeInput != null)
            {
                if (!TryPopulateVfxFeeLeg(payload, keys, vfxFeeInput, fee, out var feeErr))
                {
                    error = feeErr;
                    return false;
                }
            }

            tx = new Transaction
            {
                FromAddress = PrivacyConstants.ShieldedPoolAddress,
                ToAddress = transparentToAddress,
                Amount = 0,
                Fee = 0,
                Nonce = 0,
                Timestamp = timestamp,
                TransactionType = TransactionType.VBTC_V2_UNSHIELD,
                Data = PrivateTxPayloadCodec.SerializeToJson(payload),
                Signature = PrivacyConstants.PlonkSignatureSentinel
            };
            tx.BuildPrivate();
            if (!PrivateTxPlonkV0.TryPopulateV0Proofs(tx, out var plonkErr))
            {
                tx = null;
                error = plonkErr;
                return false;
            }
            return true;
        }

        public static bool TryBuildPrivateTransfer(
            string vbtcContractUid,
            IReadOnlyList<UnspentCommitment> inputs,
            decimal paymentAmount,
            string recipientZfxAddress,
            ShieldedKeyMaterial keys,
            long timestamp,
            out Transaction? tx,
            out string? error,
            UnspentCommitment? vfxFeeInput = null,
            LiteDB.LiteDatabase? privacyDb = null)
        {
            tx = null;
            error = null;
            if (string.IsNullOrWhiteSpace(vbtcContractUid))
            {
                error = "vbtc_uid is required.";
                return false;
            }
            var asset = VbtcPrivacyAsset.FormatAssetKey(vbtcContractUid);
            if (inputs == null || inputs.Count == 0 || inputs.Count > Globals.MaxPrivateTxInputs)
            {
                error = $"Need 1–{Globals.MaxPrivateTxInputs} inputs.";
                return false;
            }
            foreach (var i in inputs)
            {
                if (!string.Equals(i.AssetType, asset, StringComparison.Ordinal))
                {
                    error = "All inputs must match the vBTC contract asset key.";
                    return false;
                }
            }
            if (paymentAmount <= 0)
            {
                error = "Payment amount must be positive.";
                return false;
            }
            if (!ShieldedAddressCodec.TryDecodeEncryptionKey(recipientZfxAddress, out _, out var zerr))
            {
                error = zerr ?? "Invalid recipient zfx address.";
                return false;
            }

            var fee = Globals.PrivateTxFixedFee;
            var sumIn = inputs.Sum(i => i.Amount);
            if (sumIn < paymentAmount + fee)
            {
                error = "Input sum must cover payment + fixed VFX fee.";
                return false;
            }
            var change = sumIn - paymentAmount - fee;

            var nulls = new List<string>();
            var positions = new List<long>();
            foreach (var inp in inputs.OrderBy(x => x.TreePosition))
            {
                byte[] g1;
                try
                {
                    g1 = Convert.FromBase64String(inp.Commitment);
                }
                catch
                {
                    error = "Input commitment Base64 invalid.";
                    return false;
                }
                if (g1.Length != PlonkNative.G1CompressedSize)
                {
                    error = "Input commitment has wrong length.";
                    return false;
                }
                var n = NullifierService.DeriveNullifier(keys.ViewingKey32, g1, (ulong)inp.TreePosition);
                nulls.Add(Convert.ToBase64String(n));
                positions.Add(inp.TreePosition);
            }

            if (!PrivacyPedersenAmount.TryCommitAmount(paymentAmount, out var rPay, out var gPay, out var perr))
            {
                error = perr;
                return false;
            }
            var plainPay = PrivacyPedersenAmount.CreatePlainNote(paymentAmount, rPay, asset);
            byte[] sealedPay;
            try
            {
                sealedPay = ShieldedNoteEncryption.SealPlainNote(plainPay, recipientZfxAddress);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }

            var outs = new List<PrivateShieldedOutput>
            {
                new()
                {
                    Index = 0,
                    CommitmentB64 = Convert.ToBase64String(gPay),
                    EncryptedNoteB64 = Convert.ToBase64String(sealedPay)
                }
            };

            if (change > 0)
            {
                if (!PrivacyPedersenAmount.TryCommitAmount(change, out var rCh, out var gCh, out var perr2))
                {
                    error = perr2;
                    return false;
                }
                var plainCh = PrivacyPedersenAmount.CreatePlainNote(change, rCh, asset);
                try
                {
                    var sealedCh = ShieldedNoteEncryption.SealPlainNote(plainCh, keys.ZfxAddress);
                    outs.Add(new PrivateShieldedOutput
                    {
                        Index = 1,
                        CommitmentB64 = Convert.ToBase64String(gCh),
                        EncryptedNoteB64 = Convert.ToBase64String(sealedCh)
                    });
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return false;
                }
            }

            if (outs.Count > Globals.MaxPrivateTxOutputs)
            {
                error = "Too many outputs.";
                return false;
            }

            var merkleVbtc = VBTCPrivacyService.GetCurrentMerkleRootB64(vbtcContractUid, privacyDb);
            var merkleVfxFee = ShieldedPoolService.GetCurrentMerkleRootB64(AssetVfx, privacyDb);
            var payload = new PrivateTxPayload
            {
                Version = 1,
                Kind = "private_transfer",
                SubType = "PrivateTransfer",
                Asset = asset,
                VbtcContractUid = vbtcContractUid,
                NullsB64 = nulls,
                SpentCommitmentTreePositions = positions,
                Outs = outs,
                Fee = fee,
                MerkleRootB64 = string.IsNullOrEmpty(merkleVbtc) ? null : merkleVbtc,
                FeeTreeMerkleRoot = string.IsNullOrEmpty(merkleVfxFee) ? null : merkleVfxFee
            };

            if (vfxFeeInput != null)
            {
                if (!TryPopulateVfxFeeLeg(payload, keys, vfxFeeInput, fee, out var feeErr))
                {
                    error = feeErr;
                    return false;
                }
            }

            tx = new Transaction
            {
                FromAddress = PrivacyConstants.ShieldedPoolAddress,
                ToAddress = PrivacyConstants.ShieldedPoolAddress,
                Amount = 0,
                Fee = 0,
                Nonce = 0,
                Timestamp = timestamp,
                TransactionType = TransactionType.VBTC_V2_PRIVATE_TRANSFER,
                Data = PrivateTxPayloadCodec.SerializeToJson(payload),
                Signature = PrivacyConstants.PlonkSignatureSentinel
            };
            tx.BuildPrivate();
            if (!PrivateTxPlonkV0.TryPopulateV0Proofs(tx, out var plonkErr))
            {
                tx = null;
                error = plonkErr;
                return false;
            }
            return true;
        }

        /// <summary>Single VFX note &gt;= fixed fee; optional Pedersen change commitment to self.</summary>
        private static bool TryPopulateVfxFeeLeg(
            PrivateTxPayload payload,
            ShieldedKeyMaterial keys,
            UnspentCommitment vfxFeeInput,
            decimal fee,
            out string? error)
        {
            error = null;
            if (!string.Equals(vfxFeeInput.AssetType, AssetVfx, StringComparison.Ordinal))
            {
                error = "VFX fee input must use asset VFX.";
                return false;
            }
            if (vfxFeeInput.Amount < fee)
            {
                error = "VFX fee note amount must cover the fixed ZK fee.";
                return false;
            }
            byte[] g1;
            try
            {
                g1 = Convert.FromBase64String(vfxFeeInput.Commitment);
            }
            catch
            {
                error = "VFX fee input commitment is not valid Base64.";
                return false;
            }
            if (g1.Length != PlonkNative.G1CompressedSize)
            {
                error = "VFX fee input commitment has wrong length.";
                return false;
            }

            var n = NullifierService.DeriveNullifier(keys.ViewingKey32, g1, (ulong)vfxFeeInput.TreePosition);
            payload.FeeInputNullifierB64 = Convert.ToBase64String(n);
            payload.FeeInputSpentTreePosition = vfxFeeInput.TreePosition;

            var vfxChange = vfxFeeInput.Amount - fee;
            if (vfxChange > 0)
            {
                if (!PrivacyPedersenAmount.TryCommitAmount(vfxChange, out var rCh, out var gCh, out var perr))
                {
                    error = perr;
                    return false;
                }
                payload.FeeOutputCommitmentB64 = Convert.ToBase64String(gCh);
            }
            else
                payload.FeeOutputCommitmentB64 = null;

            return true;
        }
    }
}
