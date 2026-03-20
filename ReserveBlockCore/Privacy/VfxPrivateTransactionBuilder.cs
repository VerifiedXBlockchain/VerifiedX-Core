using ReserveBlockCore.Models;
using ReserveBlockCore.Models.Privacy;

namespace ReserveBlockCore.Privacy
{
    /// <summary>
    /// Constructs VFX private <see cref="Transaction"/> + <see cref="PrivateTxPayload"/> (Phase 3). PLONK <c>proof_b64</c> is left empty until Phase 4.
    /// </summary>
    public static class VfxPrivateTransactionBuilder
    {
        private const string AssetVfx = "VFX";

        /// <summary>T→Z: one Pedersen output + sealed structured note. Caller signs with transparent key after <see cref="Transaction.BuildPrivate"/>.</summary>
        public static bool TryBuildShield(
            string fromTransparentAddress,
            decimal shieldAmount,
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
            if (string.IsNullOrWhiteSpace(fromTransparentAddress))
            {
                error = "fromTransparentAddress is required.";
                return false;
            }
            if (shieldAmount < Globals.MinShieldAmountVFX)
            {
                error = $"Shield amount must be at least {Globals.MinShieldAmountVFX}.";
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

            if (!PrivacyPedersenAmount.TryCommitAmount(shieldAmount, out var r32, out var g1, out var perr))
            {
                error = perr;
                return false;
            }

            var plain = PrivacyPedersenAmount.CreatePlainNote(shieldAmount, r32, AssetVfx, memo);
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

            var merkle = ShieldedPoolService.GetCurrentMerkleRootB64(AssetVfx, privacyDb);
            var payload = new PrivateTxPayload
            {
                Version = 1,
                Kind = "shield",
                SubType = "Shield",
                Asset = AssetVfx,
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
                TransparentAmount = shieldAmount,
                MerkleRootB64 = string.IsNullOrEmpty(merkle) ? null : merkle
            };

            tx = new Transaction
            {
                FromAddress = fromTransparentAddress,
                ToAddress = PrivacyConstants.ShieldedPoolAddress,
                Amount = shieldAmount,
                Fee = transparentFee,
                Nonce = nonce,
                Timestamp = timestamp,
                TransactionType = TransactionType.VFX_SHIELD,
                Data = PrivateTxPayloadCodec.SerializeToJson(payload),
                Signature = ""
            };
            tx.BuildPrivate();
            return true;
        }

        /// <summary>Z→T: spends selected notes; transparent credit = <paramref name="transparentAmountOut"/>; shielded fee burned via <see cref="Globals.PrivateTxFixedFee"/>.</summary>
        public static bool TryBuildUnshield(
            IReadOnlyList<UnspentCommitment> inputs,
            decimal transparentAmountOut,
            string transparentToAddress,
            ShieldedKeyMaterial keys,
            long timestamp,
            out Transaction? tx,
            out string? error,
            LiteDB.LiteDatabase? privacyDb = null)
        {
            tx = null;
            error = null;
            if (inputs == null || inputs.Count == 0 || inputs.Count > Globals.MaxPrivateTxInputs)
            {
                error = $"Need 1–{Globals.MaxPrivateTxInputs} inputs.";
                return false;
            }
            if (transparentAmountOut <= 0)
            {
                error = "Unshield transparent amount must be positive.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(transparentToAddress))
            {
                error = "Recipient address is required.";
                return false;
            }

            var fee = Globals.PrivateTxFixedFee;
            var sumIn = inputs.Sum(i => i.Amount);
            if (sumIn < transparentAmountOut + fee)
            {
                error = "Input sum must cover transparent out + fixed shielded fee.";
                return false;
            }

            var change = sumIn - transparentAmountOut - fee;
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
                var plainCh = PrivacyPedersenAmount.CreatePlainNote(change, rCh, AssetVfx);
                byte[] sealedCh;
                try
                {
                    sealedCh = ShieldedNoteEncryption.SealPlainNote(plainCh, keys.ZfxAddress);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return false;
                }
                outs.Add(new PrivateShieldedOutput
                {
                    Index = idx++,
                    CommitmentB64 = Convert.ToBase64String(gCh),
                    EncryptedNoteB64 = Convert.ToBase64String(sealedCh)
                });
            }

            var merkle = ShieldedPoolService.GetCurrentMerkleRootB64(AssetVfx, privacyDb);
            var payload = new PrivateTxPayload
            {
                Version = 1,
                Kind = "unshield",
                SubType = "Unshield",
                Asset = AssetVfx,
                NullsB64 = nulls,
                SpentCommitmentTreePositions = positions,
                Outs = outs,
                TransparentOutput = transparentToAddress,
                TransparentAmount = transparentAmountOut,
                Fee = fee,
                MerkleRootB64 = string.IsNullOrEmpty(merkle) ? null : merkle
            };

            tx = new Transaction
            {
                FromAddress = PrivacyConstants.ShieldedPoolAddress,
                ToAddress = transparentToAddress,
                Amount = transparentAmountOut,
                Fee = 0,
                Nonce = 0,
                Timestamp = timestamp,
                TransactionType = TransactionType.VFX_UNSHIELD,
                Data = PrivateTxPayloadCodec.SerializeToJson(payload),
                Signature = PrivacyConstants.PlonkSignatureSentinel
            };
            tx.BuildPrivate();
            return true;
        }

        /// <summary>Z→Z: payment to <paramref name="recipientZfx"/> + optional change to self; fixed fee burned.</summary>
        public static bool TryBuildPrivateTransfer(
            IReadOnlyList<UnspentCommitment> inputs,
            decimal paymentAmount,
            string recipientZfxAddress,
            ShieldedKeyMaterial keys,
            long timestamp,
            out Transaction? tx,
            out string? error,
            LiteDB.LiteDatabase? privacyDb = null)
        {
            tx = null;
            error = null;
            if (inputs == null || inputs.Count == 0 || inputs.Count > Globals.MaxPrivateTxInputs)
            {
                error = $"Need 1–{Globals.MaxPrivateTxInputs} inputs.";
                return false;
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
                error = "Input sum must cover payment + fixed fee.";
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
            var plainPay = PrivacyPedersenAmount.CreatePlainNote(paymentAmount, rPay, AssetVfx);
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
                var plainCh = PrivacyPedersenAmount.CreatePlainNote(change, rCh, AssetVfx);
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

            var merkle = ShieldedPoolService.GetCurrentMerkleRootB64(AssetVfx, privacyDb);
            var payload = new PrivateTxPayload
            {
                Version = 1,
                Kind = "private_transfer",
                SubType = "PrivateTransfer",
                Asset = AssetVfx,
                NullsB64 = nulls,
                SpentCommitmentTreePositions = positions,
                Outs = outs,
                Fee = fee,
                MerkleRootB64 = string.IsNullOrEmpty(merkle) ? null : merkle
            };

            tx = new Transaction
            {
                FromAddress = PrivacyConstants.ShieldedPoolAddress,
                ToAddress = PrivacyConstants.ShieldedPoolAddress,
                Amount = 0,
                Fee = 0,
                Nonce = 0,
                Timestamp = timestamp,
                TransactionType = TransactionType.VFX_PRIVATE_TRANSFER,
                Data = PrivateTxPayloadCodec.SerializeToJson(payload),
                Signature = PrivacyConstants.PlonkSignatureSentinel
            };
            tx.BuildPrivate();
            return true;
        }
    }
}
