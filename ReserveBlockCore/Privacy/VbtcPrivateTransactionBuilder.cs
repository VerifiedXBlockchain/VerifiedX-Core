using ReserveBlockCore.Extensions;
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

            // Compute note hash for the output (v2 Merkle leaf)
            string? noteHashB64 = null;
            if (PrivacyPedersenAmount.TryToScaledU64(vbtcShieldAmount, out var scaledAmt, out _))
                noteHashB64 = NoteHashService.ComputeBase64(scaledAmt, r32);

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
                        NoteHashB64 = noteHashB64,
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
                Amount = 0M.ToNormalizeDecimal(),
                Fee = transparentFee.ToNormalizeDecimal(),
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
            if (sumIn < transparentVbtcOut)
            {
                error = "vBTC input sum must cover the unshield amount.";
                return false;
            }
            if (vfxFeeInput == null)
            {
                error = "A VFX fee input is required to cover the fixed ZK fee.";
                return false;
            }
            var change = sumIn - transparentVbtcOut;
            if (change < 0)
            {
                error = "Negative change.";
                return false;
            }

            var nulls = new List<string>();
            var positions = new List<long>();
            foreach (var inp in inputs.OrderBy(x => x.TreePosition))
            {
                DeriveNullifierFromInput(inp, keys.ViewingKey32, out var nB64, out var nErr);
                if (nB64 == null)
                {
                    error = nErr ?? "Nullifier derivation failed.";
                    return false;
                }
                nulls.Add(nB64);
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
                string? chNoteHash = null;
                if (PrivacyPedersenAmount.TryToScaledU64(change, out var chScaled, out _))
                    chNoteHash = NoteHashService.ComputeBase64(chScaled, rCh);

                var plainCh = PrivacyPedersenAmount.CreatePlainNote(change, rCh, asset);
                try
                {
                    var sealedCh = ShieldedNoteEncryption.SealPlainNote(plainCh, keys.ZfxAddress);
                    outs.Add(new PrivateShieldedOutput
                    {
                        Index = idx++,
                        CommitmentB64 = Convert.ToBase64String(gCh),
                        NoteHashB64 = chNoteHash,
                        EncryptedNoteB64 = Convert.ToBase64String(sealedCh)
                    });
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return false;
                }
            }

            var spentCommitments = inputs.OrderBy(x => x.TreePosition).Select(x => x.Commitment).ToList();
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
                SpentCommitmentB64s = spentCommitments,
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
                Amount = 0M.ToNormalizeDecimal(),
                Fee = 0M.ToNormalizeDecimal(),
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
            if (sumIn < paymentAmount)
            {
                error = "vBTC input sum must cover the payment amount.";
                return false;
            }
            if (vfxFeeInput == null)
            {
                error = "A VFX fee input is required to cover the fixed ZK fee.";
                return false;
            }
            var change = sumIn - paymentAmount;

            var nulls = new List<string>();
            var positions = new List<long>();
            foreach (var inp in inputs.OrderBy(x => x.TreePosition))
            {
                DeriveNullifierFromInput(inp, keys.ViewingKey32, out var nB64, out var nErr);
                if (nB64 == null)
                {
                    error = nErr ?? "Nullifier derivation failed.";
                    return false;
                }
                nulls.Add(nB64);
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

            string? payNoteHash = null;
            if (PrivacyPedersenAmount.TryToScaledU64(paymentAmount, out var payScaled, out _))
                payNoteHash = NoteHashService.ComputeBase64(payScaled, rPay);

            var outs = new List<PrivateShieldedOutput>
            {
                new()
                {
                    Index = 0,
                    CommitmentB64 = Convert.ToBase64String(gPay),
                    NoteHashB64 = payNoteHash,
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
                string? chNoteHash = null;
                if (PrivacyPedersenAmount.TryToScaledU64(change, out var chScaled, out _))
                    chNoteHash = NoteHashService.ComputeBase64(chScaled, rCh);

                var plainCh = PrivacyPedersenAmount.CreatePlainNote(change, rCh, asset);
                try
                {
                    var sealedCh = ShieldedNoteEncryption.SealPlainNote(plainCh, keys.ZfxAddress);
                    outs.Add(new PrivateShieldedOutput
                    {
                        Index = 1,
                        CommitmentB64 = Convert.ToBase64String(gCh),
                        NoteHashB64 = chNoteHash,
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

            var spentCommitments = inputs.OrderBy(x => x.TreePosition).Select(x => x.Commitment).ToList();
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
                SpentCommitmentB64s = spentCommitments,
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
                Amount = 0M.ToNormalizeDecimal(),
                Fee = 0M.ToNormalizeDecimal(),
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

            // v2: note-hash nullifier for fee input
            DeriveNullifierFromInput(vfxFeeInput, keys.ViewingKey32, out var feeNullB64, out var feeNullErr);
            if (feeNullB64 == null)
            {
                error = feeNullErr ?? "Fee nullifier derivation failed.";
                return false;
            }
            payload.FeeInputNullifierB64 = feeNullB64;
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

                // v2: note hash for fee change output
                if (PrivacyPedersenAmount.TryToScaledU64(vfxChange, out var feeChScaled, out _))
                    payload.FeeOutputNoteHashB64 = NoteHashService.ComputeBase64(feeChScaled, rCh);

                // Seal an encrypted note so the auto-scanner can recover VFX fee change
                try
                {
                    var plainFeeChange = PrivacyPedersenAmount.CreatePlainNote(vfxChange, rCh, AssetVfx);
                    var sealedFeeChange = ShieldedNoteEncryption.SealPlainNote(plainFeeChange, keys.ZfxAddress);
                    payload.FeeOutputEncryptedNoteB64 = Convert.ToBase64String(sealedFeeChange);
                }
                catch
                {
                    // Non-fatal: the commitment is still recorded; scanner just won't auto-detect the change
                    payload.FeeOutputEncryptedNoteB64 = null;
                }
            }
            else
            {
                payload.FeeOutputCommitmentB64 = null;
                payload.FeeOutputEncryptedNoteB64 = null;
            }

            return true;
        }

        // ─── Shared helpers ────────────────────────────────────────────

        /// <summary>
        /// Derives a nullifier using v2 note-hash derivation (preferred) with
        /// fallback to legacy G1-based derivation.
        /// </summary>
        private static byte[]? DeriveNullifierFromInput(UnspentCommitment inp, byte[] viewingKey32, out string? nullifierB64, out string? error)
        {
            nullifierB64 = null;
            error = null;

            if (inp.Randomness.Length == PlonkNative.ScalarSize
                && PrivacyPedersenAmount.TryToScaledU64(inp.Amount, out var scaled, out _))
            {
                var nh = NoteHashService.Compute(scaled, inp.Randomness);
                if (nh != null && nh.Length == PlonkNative.ScalarSize)
                {
                    var n = NullifierService.DeriveFromNoteHash(viewingKey32, nh, (ulong)inp.TreePosition);
                    nullifierB64 = Convert.ToBase64String(n);
                    return n;
                }
            }

            byte[] g1;
            try
            {
                g1 = Convert.FromBase64String(inp.Commitment);
            }
            catch
            {
                error = "Input commitment Base64 invalid.";
                return null;
            }
            if (g1.Length != PlonkNative.G1CompressedSize)
            {
                error = "Input commitment has wrong length.";
                return null;
            }
            var nLegacy = NullifierService.DeriveNullifier(viewingKey32, g1, (ulong)inp.TreePosition);
            nullifierB64 = Convert.ToBase64String(nLegacy);
            return nLegacy;
        }
    }
}
