using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Models.Privacy;

namespace VerifiedXCore.Privacy
{
    /// <summary>
    /// Constructs VFX private <see cref="Transaction"/> + <see cref="PrivateTxPayload"/>.
    ///
    /// Two regimes, chosen by the height the transaction will be mined into (<paramref name="forHeight"/>, default tip + 1):
    /// <list type="bullet">
    /// <item>before Globals.PrivateTxProofRulesHeight: the original Phase 3 shape (legacy note hashes and nullifiers, one or
    /// two inputs, v0 proof population when that prover exists - it never did in the field);</item>
    /// <item>from the height (fund-loss audit item 1, stage 2): circuit-compatible note hashes and nullifiers, exactly two
    /// spent notes (the public dummy note fills a single-note spend), always a change output, canonical randomness, and a
    /// real PLONK proof produced by <see cref="PrivateTxProverV1"/> and verified locally before the transaction leaves.</item>
    /// </list>
    /// </summary>
    public static class VfxPrivateTransactionBuilder
    {
        private const string AssetVfx = "VFX";

        private static long MineHeight(long? forHeight) => forHeight ?? ((Globals.LastBlock?.Height ?? 0) + 1);

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
            LiteDB.LiteDatabase? privacyDb = null,
            long? forHeight = null)
        {
            tx = null;
            error = null;
            var height = MineHeight(forHeight);
            var epoch = PrivacyEpoch.ProofRulesActive(height);

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

            // Note hash for the output (the Merkle leaf; in the epoch the value the shield circuit binds to the amount).
            string? noteHashB64 = null;
            ulong scaledAmt = 0;
            if (PrivacyPedersenAmount.TryToScaledU64(shieldAmount, out scaledAmt, out _))
                noteHashB64 = Convert.ToBase64String(NoteHashService.ComputeAt(height, scaledAmt, r32));

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
                        NoteHashB64 = noteHashB64,
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
                Amount = shieldAmount.ToNormalizeDecimal(),
                Fee = transparentFee.ToNormalizeDecimal(),
                Nonce = nonce,
                Timestamp = timestamp,
                TransactionType = TransactionType.VFX_SHIELD,
                Data = PrivateTxPayloadCodec.SerializeToJson(payload),
                Signature = ""
            };
            tx.BuildPrivate();

            if (epoch)
            {
                if (!PrivateTxProverV1.TryProveShield(scaledAmt, r32, out var proof, out var pErr)
                    || !PrivateTxProverV1.TryAttachAndVerify(tx, payload, proof!, out pErr))
                {
                    tx = null;
                    error = pErr;
                    return false;
                }
                return true;
            }

            if (!PrivateTxPlonkV0.TryPopulateV0Proofs(tx, out var plonkErr))
            {
                tx = null;
                error = plonkErr;
                return false;
            }
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
            LiteDB.LiteDatabase? privacyDb = null,
            long? forHeight = null)
        {
            tx = null;
            error = null;
            var height = MineHeight(forHeight);
            var epoch = PrivacyEpoch.ProofRulesActive(height);

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

            var ordered = inputs.OrderBy(x => x.TreePosition).ToList();
            var nulls = new List<string>();
            var positions = new List<long>();
            byte[]? vkCanonical = epoch ? PrivacyField.ReduceLe(keys.ViewingKey32) : null;
            var spentNotes = new List<PrivateTxProverV1.SpentNote>();
            foreach (var inp in ordered)
            {
                if (epoch)
                {
                    if (!TryEpochNote(inp, out var note, out var nErr)) { error = nErr; return false; }
                    spentNotes.Add(note!);
                    nulls.Add(Convert.ToBase64String(PrivateTxProverV1.NullifierFor(note!, vkCanonical!)));
                }
                else
                {
                    DeriveNullifierFromInput(inp, keys.ViewingKey32, out var nB64, out var nErr);
                    if (nB64 == null)
                    {
                        error = nErr ?? "Nullifier derivation failed.";
                        return false;
                    }
                    nulls.Add(nB64);
                }
                positions.Add(inp.TreePosition);
            }
            if (epoch && spentNotes.Count == 1)
            {
                // The circuits spend exactly two notes: the public dummy note fills the second input.
                spentNotes.Add(PrivateTxProverV1.Dummy);
                nulls.Add(PrivacyEpoch.DummyNullifierB64);
                positions.Add(0);
            }

            var outs = new List<PrivateShieldedOutput>();
            PrivateTxProverV1.OutputNote? changeNote = null;
            // In the epoch the circuit always has a change output (its amount may be zero).
            if (change > 0 || epoch)
            {
                if (!PrivacyPedersenAmount.TryCommitAmount(change, out var rCh, out var gCh, out var perr))
                {
                    error = perr;
                    return false;
                }
                string? chNoteHash = null;
                if (PrivacyPedersenAmount.TryToScaledU64(change, out var chScaled, out _))
                {
                    chNoteHash = Convert.ToBase64String(NoteHashService.ComputeAt(height, chScaled, rCh));
                    changeNote = new PrivateTxProverV1.OutputNote(chScaled, rCh);
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
                    Index = 0,
                    CommitmentB64 = Convert.ToBase64String(gCh),
                    NoteHashB64 = chNoteHash,
                    EncryptedNoteB64 = Convert.ToBase64String(sealedCh)
                });
            }

            var spentCommitments = ordered.Select(x => x.Commitment).ToList();
            var merkle = ShieldedPoolService.GetCurrentMerkleRootB64(AssetVfx, privacyDb);
            var payload = new PrivateTxPayload
            {
                Version = 1,
                Kind = "unshield",
                SubType = "Unshield",
                Asset = AssetVfx,
                NullsB64 = nulls,
                SpentCommitmentTreePositions = positions,
                SpentCommitmentB64s = spentCommitments,
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
                Amount = transparentAmountOut.ToNormalizeDecimal(),
                Fee = 0M.ToNormalizeDecimal(),
                Nonce = 0,
                Timestamp = timestamp,
                TransactionType = TransactionType.VFX_UNSHIELD,
                Data = PrivateTxPayloadCodec.SerializeToJson(payload),
                Signature = PrivacyConstants.PlonkSignatureSentinel
            };
            tx.BuildPrivate();

            if (epoch)
            {
                if (!TryBuildWitnesses(spentNotes, vkCanonical!, privacyDb, out var witnesses, out var root, out var wErr))
                {
                    tx = null; error = wErr; return false;
                }
                if (!PrivacyPedersenAmount.TryToScaledU64(transparentAmountOut, out var transparentScaled, out var sErr)
                    || !PrivacyPedersenAmount.TryToScaledU64(fee, out var feeScaled, out sErr))
                {
                    tx = null; error = sErr; return false;
                }
                if (!PrivateTxProverV1.TryProveUnshield(witnesses!, transparentScaled, changeNote!, feeScaled, root!, out var proof, out var pErr)
                    || !PrivateTxProverV1.TryAttachAndVerify(tx, payload, proof!, out pErr))
                {
                    tx = null; error = pErr; return false;
                }
                return true;
            }

            if (!PrivateTxPlonkV0.TryPopulateV0Proofs(tx, out var plonkErr))
            {
                tx = null;
                error = plonkErr;
                return false;
            }
            return true;
        }

        /// <summary>Z→Z: payment to <paramref name="recipientZfxAddress"/> + change to self (always present in the epoch); fixed fee burned.</summary>
        public static bool TryBuildPrivateTransfer(
            IReadOnlyList<UnspentCommitment> inputs,
            decimal paymentAmount,
            string recipientZfxAddress,
            ShieldedKeyMaterial keys,
            long timestamp,
            out Transaction? tx,
            out string? error,
            LiteDB.LiteDatabase? privacyDb = null,
            long? forHeight = null)
        {
            tx = null;
            error = null;
            var height = MineHeight(forHeight);
            var epoch = PrivacyEpoch.ProofRulesActive(height);

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

            var ordered = inputs.OrderBy(x => x.TreePosition).ToList();
            var nulls = new List<string>();
            var positions = new List<long>();
            byte[]? vkCanonical = epoch ? PrivacyField.ReduceLe(keys.ViewingKey32) : null;
            var spentNotes = new List<PrivateTxProverV1.SpentNote>();
            foreach (var inp in ordered)
            {
                if (epoch)
                {
                    if (!TryEpochNote(inp, out var note, out var nErr)) { error = nErr; return false; }
                    spentNotes.Add(note!);
                    nulls.Add(Convert.ToBase64String(PrivateTxProverV1.NullifierFor(note!, vkCanonical!)));
                }
                else
                {
                    DeriveNullifierFromInput(inp, keys.ViewingKey32, out var nB64, out var nErr);
                    if (nB64 == null)
                    {
                        error = nErr ?? "Nullifier derivation failed.";
                        return false;
                    }
                    nulls.Add(nB64);
                }
                positions.Add(inp.TreePosition);
            }
            if (epoch && spentNotes.Count == 1)
            {
                spentNotes.Add(PrivateTxProverV1.Dummy);
                nulls.Add(PrivacyEpoch.DummyNullifierB64);
                positions.Add(0);
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
            string? payNoteHash = null;
            ulong payScaled = 0;
            if (PrivacyPedersenAmount.TryToScaledU64(paymentAmount, out payScaled, out _))
                payNoteHash = Convert.ToBase64String(NoteHashService.ComputeAt(height, payScaled, rPay));

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
            var outputNotes = new List<PrivateTxProverV1.OutputNote> { new(payScaled, rPay) };

            if (change > 0 || epoch)
            {
                if (!PrivacyPedersenAmount.TryCommitAmount(change, out var rCh, out var gCh, out var perr2))
                {
                    error = perr2;
                    return false;
                }
                string? chNoteHash = null;
                ulong chScaled = 0;
                if (PrivacyPedersenAmount.TryToScaledU64(change, out chScaled, out _))
                    chNoteHash = Convert.ToBase64String(NoteHashService.ComputeAt(height, chScaled, rCh));
                var plainCh = PrivacyPedersenAmount.CreatePlainNote(change, rCh, AssetVfx);
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
                    outputNotes.Add(new PrivateTxProverV1.OutputNote(chScaled, rCh));
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

            var spentCommitments = ordered.Select(x => x.Commitment).ToList();
            var merkle = ShieldedPoolService.GetCurrentMerkleRootB64(AssetVfx, privacyDb);
            var payload = new PrivateTxPayload
            {
                Version = 1,
                Kind = "private_transfer",
                SubType = "PrivateTransfer",
                Asset = AssetVfx,
                NullsB64 = nulls,
                SpentCommitmentTreePositions = positions,
                SpentCommitmentB64s = spentCommitments,
                Outs = outs,
                Fee = fee,
                MerkleRootB64 = string.IsNullOrEmpty(merkle) ? null : merkle
            };

            tx = new Transaction
            {
                FromAddress = PrivacyConstants.ShieldedPoolAddress,
                ToAddress = PrivacyConstants.ShieldedPoolAddress,
                Amount = 0M.ToNormalizeDecimal(),
                Fee = 0M.ToNormalizeDecimal(),
                Nonce = 0,
                Timestamp = timestamp,
                TransactionType = TransactionType.VFX_PRIVATE_TRANSFER,
                Data = PrivateTxPayloadCodec.SerializeToJson(payload),
                Signature = PrivacyConstants.PlonkSignatureSentinel
            };
            tx.BuildPrivate();

            if (epoch)
            {
                if (!TryBuildWitnesses(spentNotes, vkCanonical!, privacyDb, out var witnesses, out var root, out var wErr))
                {
                    tx = null; error = wErr; return false;
                }
                if (!PrivacyPedersenAmount.TryToScaledU64(fee, out var feeScaled, out var sErr))
                {
                    tx = null; error = sErr; return false;
                }
                if (!PrivateTxProverV1.TryProveTransfer(witnesses!, outputNotes.ToArray(), feeScaled, root!, out var proof, out var pErr)
                    || !PrivateTxProverV1.TryAttachAndVerify(tx, payload, proof!, out pErr))
                {
                    tx = null; error = pErr; return false;
                }
                return true;
            }

            if (!PrivateTxPlonkV0.TryPopulateV0Proofs(tx, out var plonkErr))
            {
                tx = null;
                error = plonkErr;
                return false;
            }
            return true;
        }

        // ─── Shared helpers ────────────────────────────────────────────

        /// <summary>An unspent note as a circuit input (canonical randomness, scaled amount).</summary>
        private static bool TryEpochNote(UnspentCommitment inp, out PrivateTxProverV1.SpentNote? note, out string? error)
        {
            note = null;
            error = null;
            if (inp.Randomness == null || inp.Randomness.Length != PlonkNative.ScalarSize)
            {
                error = $"Note at tree position {inp.TreePosition} has no 32-byte randomness; it cannot be proven.";
                return false;
            }
            if (!PrivacyField.IsCanonicalLe(inp.Randomness))
            {
                error = $"Note at tree position {inp.TreePosition} has non-canonical randomness (a pre-epoch note); it cannot be proven.";
                return false;
            }
            if (!PrivacyPedersenAmount.TryToScaledU64(inp.Amount, out var scaled, out var sErr))
            {
                error = sErr;
                return false;
            }
            note = new PrivateTxProverV1.SpentNote(scaled, inp.Randomness, inp.TreePosition);
            return true;
        }

        private static bool TryBuildWitnesses(List<PrivateTxProverV1.SpentNote> notes, byte[] vkCanonical, LiteDB.LiteDatabase? privacyDb,
            out PlonkProverV1.TransferInputWitness[]? witnesses, out byte[]? root, out string? error)
        {
            witnesses = null;
            root = null;
            error = null;
            var list = new PlonkProverV1.TransferInputWitness[notes.Count];
            for (var i = 0; i < notes.Count; i++)
            {
                var isDummy = notes[i].AmountScaled == 0 && notes[i].TreePosition == 0;
                if (!PrivateTxProverV1.TryBuildInput(AssetVfx, notes[i], isDummy ? PrivacyField.Zero32 : vkCanonical, privacyDb, out var w, out var r, out error))
                    return false;
                list[i] = w!;
                root ??= r;
            }
            witnesses = list;
            return true;
        }

        /// <summary>
        /// Legacy (pre-epoch) nullifier: v2 note-hash derivation with fallback to the G1-based derivation.
        /// </summary>
        private static byte[]? DeriveNullifierFromInput(UnspentCommitment inp, byte[] viewingKey32, out string? nullifierB64, out string? error)
        {
            nullifierB64 = null;
            error = null;

            // v2 path: derive from note hash (amount + randomness)
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

            // Legacy fallback: derive from G1 commitment bytes
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
