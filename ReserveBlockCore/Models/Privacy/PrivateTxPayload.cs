using Newtonsoft.Json;
using ReserveBlockCore.Privacy;

namespace ReserveBlockCore.Models.Privacy
{
    /// <summary>
    /// On-chain privacy payload stored in <see cref="Transaction.Data"/> as JSON or Base64(JSON).
    /// Ordering for rebuild: block height → tx order (see <see cref="Privacy.PrivateTransactionTypes"/>) → output <see cref="PrivateShieldedOutput.Index"/>.
    /// </summary>
    public sealed class PrivateTxPayload
    {
        [JsonProperty("v")]
        public int Version { get; set; } = 1;

        /// <summary>Logical kind string (e.g. shield, z2z) — cross-check vs <see cref="Transaction.TransactionType"/>.</summary>
        [JsonProperty("kind")]
        public string? Kind { get; set; }

        /// <summary>Plan-style subtype / enum label (optional; may duplicate <see cref="Kind"/>).</summary>
        [JsonProperty("sub_type")]
        public string? SubType { get; set; }

        /// <summary>Asset key for pool state, e.g. VFX or a vBTC contract-specific id.</summary>
        [JsonProperty("asset")]
        public string Asset { get; set; } = "";

        [JsonProperty("outs")]
        public List<PrivateShieldedOutput> Outs { get; set; } = new();

        [JsonProperty("nulls")]
        public List<string> NullsB64 { get; set; } = new();

        /// <summary>
        /// Parallel to <see cref="NullsB64"/>: tree positions of commitments being spent (required when nullifiers present).
        /// </summary>
        [JsonProperty("spent_tree_positions")]
        public List<long> SpentCommitmentTreePositions { get; set; } = new();

        /// <summary>
        /// Parallel to <see cref="NullsB64"/>: Base64 commitment strings of the inputs being spent.
        /// Added in v1.1 for reliable local wallet spent-tracking (older TXs may not have this field).
        /// </summary>
        [JsonProperty("spent_commitments", NullValueHandling = NullValueHandling.Ignore)]
        public List<string>? SpentCommitmentB64s { get; set; }

        /// <summary>Expected shielded Merkle root (Base64), for proof wiring + recency checks.</summary>
        [JsonProperty("merkle_root")]
        public string? MerkleRootB64 { get; set; }

        /// <summary>Primary ZK proof bytes, Base64.</summary>
        [JsonProperty("proof_b64")]
        public string? ProofB64 { get; set; }

        /// <summary>Fee / auxiliary proof, Base64.</summary>
        [JsonProperty("fee_proof_b64")]
        public string? FeeProofB64 { get; set; }

        [JsonProperty("fee_input_nullifier_b64")]
        public string? FeeInputNullifierB64 { get; set; }

        [JsonProperty("fee_output_commitment_b64")]
        public string? FeeOutputCommitmentB64 { get; set; }

        [JsonProperty("fee_tree_merkle_root")]
        public string? FeeTreeMerkleRoot { get; set; }

        /// <summary>Tree position of the VFX commitment spent as the fixed ZK fee input (vBTC Z→Z / Z→T fee leg).</summary>
        [JsonProperty("fee_input_spent_tree_position")]
        public long? FeeInputSpentTreePosition { get; set; }

        /// <summary>Optional note hash (Base64, 32 bytes) for the VFX fee change output.</summary>
        [JsonProperty("fee_out_note_hash")]
        public string? FeeOutputNoteHashB64 { get; set; }

        /// <summary>Optional encrypted note for the VFX fee change output (Base64), enabling auto-scanner recovery.</summary>
        [JsonProperty("fee_out_note", NullValueHandling = NullValueHandling.Ignore)]
        public string? FeeOutputEncryptedNoteB64 { get; set; }

        [JsonProperty("transparent_input")]
        public string? TransparentInput { get; set; }

        [JsonProperty("transparent_output")]
        public string? TransparentOutput { get; set; }

        [JsonProperty("transparent_amount")]
        public decimal? TransparentAmount { get; set; }

        /// <summary>Fixed fee inside payload (mirrors on-chain fee rules when wired).</summary>
        [JsonProperty("fee")]
        public decimal? Fee { get; set; }

        /// <summary>Optional vBTC contract UID when asset is vBTC-derived.</summary>
        [JsonProperty("vbtc_uid")]
        public string? VbtcContractUid { get; set; }

        /// <summary>Transparent vBTC amount for T→Z / Z→T (consensus); shield/unshield SC ledger uses this field.</summary>
        [JsonProperty("vbtc_amt")]
        public decimal? VbtcTransparentAmount { get; set; }

        public bool TryValidateStructure([System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
        {
            error = null;
            if (Version != 1)
            {
                error = "PrivateTxPayload version must be 1.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(Asset) || Asset.Length > 64)
            {
                error = "PrivateTxPayload asset is required (max 64 chars).";
                return false;
            }

            if (!ValidateOptionalStringField(SubType, "sub_type", out error)) return false;
            if (!ValidateOptionalStringField(MerkleRootB64, "merkle_root", out error)) return false;
            if (!ValidateOptionalStringField(FeeTreeMerkleRoot, "fee_tree_merkle_root", out error)) return false;
            if (!ValidateOptionalStringField(TransparentInput, "transparent_input", out error)) return false;
            if (!ValidateOptionalStringField(TransparentOutput, "transparent_output", out error)) return false;

            if (!ValidateOptionalBase64Large(ProofB64, "proof_b64", out error)) return false;
            if (!ValidateOptionalBase64Large(FeeProofB64, "fee_proof_b64", out error)) return false;
            if (!ValidateOptionalBase64Scalar(FeeInputNullifierB64, "fee_input_nullifier_b64", out error)) return false;
            if (!ValidateOptionalBase64G1(FeeOutputCommitmentB64, "fee_output_commitment_b64", out error)) return false;

            if (NullsB64.Count > 0)
            {
                if (SpentCommitmentTreePositions.Count != NullsB64.Count)
                {
                    error = "PrivateTxPayload spent_tree_positions must have the same length as nulls when nullifiers are present.";
                    return false;
                }
                foreach (var p in SpentCommitmentTreePositions)
                {
                    if (p < 0)
                    {
                        error = "PrivateTxPayload spent_tree_positions entries must be non-negative.";
                        return false;
                    }
                }
            }
            else if (SpentCommitmentTreePositions.Count > 0)
            {
                error = "PrivateTxPayload spent_tree_positions must be empty when nulls is empty.";
                return false;
            }

            var seen = new HashSet<int>();
            foreach (var o in Outs)
            {
                if (o == null)
                {
                    error = "PrivateTxPayload outs contains a null entry.";
                    return false;
                }
                if (o.Index < 0)
                {
                    error = "PrivateTxPayload output index must be non-negative.";
                    return false;
                }
                if (!seen.Add(o.Index))
                {
                    error = "PrivateTxPayload duplicate output index.";
                    return false;
                }
                if (string.IsNullOrWhiteSpace(o.CommitmentB64))
                {
                    error = "PrivateTxPayload output commitment is required.";
                    return false;
                }
                byte[] g1;
                try
                {
                    g1 = Convert.FromBase64String(o.CommitmentB64);
                }
                catch
                {
                    error = "PrivateTxPayload output commitment is not valid Base64.";
                    return false;
                }
                if (g1.Length != PlonkNative.G1CompressedSize)
                {
                    error = $"PrivateTxPayload output commitment must decode to {PlonkNative.G1CompressedSize} bytes (G1 compressed).";
                    return false;
                }
                if (!string.IsNullOrWhiteSpace(o.EncryptedNoteB64))
                {
                    if (!ValidateSealedNoteB64(o.EncryptedNoteB64, out error))
                        return false;
                }
            }

            for (var i = 0; i < NullsB64.Count; i++)
            {
                var nb = NullsB64[i];
                if (string.IsNullOrWhiteSpace(nb))
                {
                    error = "PrivateTxPayload nullifier entry is empty.";
                    return false;
                }
                try
                {
                    var n = Convert.FromBase64String(nb);
                    if (n.Length != PlonkNative.ScalarSize)
                    {
                        error = $"PrivateTxPayload nullifier must decode to {PlonkNative.ScalarSize} bytes.";
                        return false;
                    }
                }
                catch
                {
                    error = "PrivateTxPayload nullifier is not valid Base64.";
                    return false;
                }
            }

            var hasFeeNull = !string.IsNullOrWhiteSpace(FeeInputNullifierB64);
            var hasFeeRoot = !string.IsNullOrWhiteSpace(FeeTreeMerkleRoot);
            var hasFeePos = FeeInputSpentTreePosition.HasValue;
            if (hasFeeNull || hasFeeRoot || hasFeePos)
            {
                if (!hasFeeNull || !hasFeeRoot || !hasFeePos)
                {
                    error = "PrivateTxPayload VFX fee leg requires fee_input_nullifier_b64, fee_tree_merkle_root, and fee_input_spent_tree_position together.";
                    return false;
                }
                if (FeeInputSpentTreePosition!.Value < 0)
                {
                    error = "PrivateTxPayload fee_input_spent_tree_position must be non-negative.";
                    return false;
                }
            }

            return true;
        }

        private static bool ValidateOptionalStringField(string? value, string field, out string? error)
        {
            error = null;
            if (string.IsNullOrEmpty(value))
                return true;
            if (value.Length > PrivacyConstants.MaxPayloadStringFieldLength)
            {
                error = $"PrivateTxPayload {field} exceeds max length.";
                return false;
            }
            return true;
        }

        private static bool ValidateOptionalBase64Large(string? b64, string field, out string? error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(b64))
                return true;
            try
            {
                var raw = Convert.FromBase64String(b64);
                if (raw.Length > PrivacyConstants.MaxProofFieldDecodedBytes)
                {
                    error = $"PrivateTxPayload {field} exceeds max decoded size.";
                    return false;
                }
            }
            catch
            {
                error = $"PrivateTxPayload {field} is not valid Base64.";
                return false;
            }
            return true;
        }

        private static bool ValidateOptionalBase64Scalar(string? b64, string field, out string? error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(b64))
                return true;
            try
            {
                var raw = Convert.FromBase64String(b64);
                if (raw.Length != PlonkNative.ScalarSize)
                {
                    error = $"PrivateTxPayload {field} must decode to {PlonkNative.ScalarSize} bytes.";
                    return false;
                }
            }
            catch
            {
                error = $"PrivateTxPayload {field} is not valid Base64.";
                return false;
            }
            return true;
        }

        private static bool ValidateOptionalBase64G1(string? b64, string field, out string? error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(b64))
                return true;
            try
            {
                var raw = Convert.FromBase64String(b64);
                if (raw.Length != PlonkNative.G1CompressedSize)
                {
                    error = $"PrivateTxPayload {field} must decode to {PlonkNative.G1CompressedSize} bytes.";
                    return false;
                }
            }
            catch
            {
                error = $"PrivateTxPayload {field} is not valid Base64.";
                return false;
            }
            return true;
        }

        private static bool ValidateSealedNoteB64(string b64, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
        {
            error = null;
            const int maxNoteDecodedBytes = 65536;
            try
            {
                var raw = Convert.FromBase64String(b64);
                if (raw.Length < ShieldedNoteEncryption.MinSealedLength)
                {
                    error = "PrivateTxPayload output note blob is too short to be a sealed note.";
                    return false;
                }
                if (raw.Length > maxNoteDecodedBytes)
                {
                    error = "PrivateTxPayload output note blob exceeds max decoded size.";
                    return false;
                }
            }
            catch
            {
                error = "PrivateTxPayload output note is not valid Base64.";
                return false;
            }
            return true;
        }
    }

    /// <summary>Per-output shielded data (plan: OutputCommitmentData).</summary>
    public sealed class PrivateShieldedOutput
    {
        [JsonProperty("i")]
        public int Index { get; set; }

        /// <summary>G1 compressed Pedersen commitment, Base64.</summary>
        [JsonProperty("c")]
        public string CommitmentB64 { get; set; } = "";

        /// <summary>
        /// Poseidon note hash (32 bytes, Base64): <c>Poseidon(amount_scaled, randomness)</c>.
        /// Used as the Merkle leaf and for in-circuit amount binding.
        /// </summary>
        [JsonProperty("nh")]
        public string? NoteHashB64 { get; set; }

        /// <summary>Optional sealed note ciphertext for this output (Base64) — <see cref="ShieldedNoteEncryption"/>.</summary>
        [JsonProperty("note")]
        public string? EncryptedNoteB64 { get; set; }
    }
}
