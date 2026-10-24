using System.Security.Cryptography;
using System.Text;
using VerifiedXCore.Models;
using VerifiedXCore.Models.Privacy;

namespace VerifiedXCore.Privacy
{
    /// <summary>
    /// VFXPI1 <b>version 3</b> (stage 3; the class keeps its name): the public-input blob the v2 owner-bound circuits are verified against, rebuilt by every validator from
    /// the transaction's own fields (fund-loss audit item 1, stage 2). Must stay byte-identical to
    /// <c>plonk-ffi/src/vfxpi1.rs</c>, which parses it and places the values at the circuits' public-input positions.
    ///
    /// Header `VFXPI1` + version 3 + circuit byte (8), asset tag (32, SHA-256 of the asset string), Merkle root (32), then
    /// <list type="bullet">
    /// <item>Shield (112): amount u64 LE, note_hash</item>
    /// <item>Unshield (216): fee u64, transparent u64, nullifier0, nullifier1, change_note_hash, recipient_tag</item>
    /// <item>Transfer (208): fee u64, nullifier0, nullifier1, out_note_hash0, out_note_hash1</item>
    /// <item>Fee (144): fee u64, nullifier, change_note_hash</item>
    /// </list>
    /// Field elements are canonical little-endian; amounts are scaled by 10^8. The circuits bind note hashes, so every
    /// output must carry one (<see cref="PrivateShieldedOutput.NoteHashB64"/>); Pedersen commitments are not bound.
    /// </summary>
    public static class PlonkPublicInputsV2
    {
        /// <summary>Version 3 (stage 3, v2 circuits): version 2 plus the Unshield recipient tag. Must match <c>plonk-ffi/src/vfxpi1.rs</c> VERSION_3.</summary>
        public const byte Version = 3;
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("VFXPI1");
        private const int HeaderLen = 8;

        public static int TotalLength(PlonkCircuitType c) => c switch
        {
            PlonkCircuitType.Shield => HeaderLen + 32 + 32 + 8 + 32,
            PlonkCircuitType.Unshield => HeaderLen + 32 + 32 + 8 + 8 + 32 + 32 + 32 + 32, // + recipient_tag (v3)
            PlonkCircuitType.Transfer => HeaderLen + 32 + 32 + 8 + 32 + 32 + 32 + 32,
            PlonkCircuitType.Fee => HeaderLen + 32 + 32 + 8 + 32 + 32,
            _ => throw new ArgumentOutOfRangeException(nameof(c)),
        };

        /// <summary>
        /// The Unshield recipient as a field element: SHA-256 of the address text, reduced into the field. Consensus builds
        /// it from the transaction's ToAddress, the wallet from the address it pays; a copied unshield with another
        /// recipient has a different tag and its proof fails (stage 3).
        /// </summary>
        public static byte[] RecipientTag32(string? address)
        {
            if (string.IsNullOrWhiteSpace(address)) throw new InvalidOperationException("The unshield recipient (ToAddress) is required.");
            return PrivacyField.ReduceLe(SHA256.HashData(Encoding.UTF8.GetBytes(address.Trim())));
        }

        /// <summary>32-byte domain separator per asset string (same as version 1).</summary>
        public static byte[] AssetTag32(string asset) =>
            string.IsNullOrEmpty(asset) ? new byte[32] : SHA256.HashData(Encoding.UTF8.GetBytes(asset));

        /// <summary>The blob for the transaction's primary circuit. False with a reason when a field the circuit binds is missing or malformed.</summary>
        public static bool TryBuild(Transaction tx, PrivateTxPayload payload, out byte[] blob, out string? error)
        {
            blob = Array.Empty<byte>();
            error = null;
            try
            {
                var circuit = PlonkCircuitHelper.GetPrimaryCircuit(tx.TransactionType);
                var root = RootOrZero(payload.MerkleRootB64, circuit == PlonkCircuitType.Shield);
                var fee = Scaled(payload.Fee ?? Globals.PrivateTxFixedFee, "fee");
                var body = new List<byte[]>();
                switch (circuit)
                {
                    case PlonkCircuitType.Shield:
                    {
                        var amount = tx.TransactionType == TransactionType.VFX_SHIELD ? tx.Amount : payload.VbtcTransparentAmount ?? 0M;
                        if (payload.Outs.Count != 1) throw new InvalidOperationException("A shield carries exactly one output.");
                        body.Add(U64(Scaled(amount, "amount")));
                        body.Add(NoteHash(payload.Outs[0], "shield output"));
                        break;
                    }
                    case PlonkCircuitType.Unshield:
                    {
                        var transparent = tx.TransactionType == TransactionType.VFX_UNSHIELD ? tx.Amount : payload.VbtcTransparentAmount ?? 0M;
                        var nulls = TwoNullifiers(payload);
                        if (payload.Outs.Count != 1) throw new InvalidOperationException("An unshield carries exactly one change output (its amount may be zero).");
                        body.Add(U64(fee));
                        body.Add(U64(Scaled(transparent, "transparent amount")));
                        body.Add(nulls[0]);
                        body.Add(nulls[1]);
                        body.Add(NoteHash(payload.Outs[0], "change output"));
                        body.Add(RecipientTag32(tx.ToAddress)); // v3: the transparent recipient is a public input
                        break;
                    }
                    case PlonkCircuitType.Transfer:
                    {
                        var nulls = TwoNullifiers(payload);
                        if (payload.Outs.Count != 2) throw new InvalidOperationException("A private transfer carries exactly two outputs (payment and change; the change may be zero).");
                        var outs = payload.Outs.OrderBy(o => o.Index).ToList();
                        body.Add(U64(fee));
                        body.Add(nulls[0]);
                        body.Add(nulls[1]);
                        body.Add(NoteHash(outs[0], "output 0"));
                        body.Add(NoteHash(outs[1], "output 1"));
                        break;
                    }
                    default:
                        throw new InvalidOperationException("Not a primary private circuit.");
                }
                blob = Assemble(circuit, payload.Asset, root, body);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>The Fee-circuit blob for a vBTC private transaction's VFX fee leg.</summary>
        public static bool TryBuildFeeCircuit(Transaction tx, PrivateTxPayload payload, out byte[] blob, out string? error)
        {
            blob = Array.Empty<byte>();
            error = null;
            try
            {
                var root = RootOrZero(payload.FeeTreeMerkleRoot, false);
                var fee = Scaled(payload.Fee ?? Globals.PrivateTxFixedFee, "fee");
                var body = new List<byte[]>
                {
                    U64(fee),
                    Field(payload.FeeInputNullifierB64, "fee input nullifier"),
                    Field(payload.FeeOutputNoteHashB64, "fee change note hash"),
                };
                blob = Assemble(PlonkCircuitType.Fee, "VFX", root, body);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static byte[] Assemble(PlonkCircuitType circuit, string asset, byte[] root, List<byte[]> body)
        {
            var total = TotalLength(circuit);
            var blob = new byte[total];
            var o = 0;
            Buffer.BlockCopy(Magic, 0, blob, o, Magic.Length); o += Magic.Length;
            blob[o++] = Version;
            blob[o++] = (byte)circuit;
            Buffer.BlockCopy(AssetTag32(asset), 0, blob, o, 32); o += 32;
            Buffer.BlockCopy(root, 0, blob, o, 32); o += 32;
            foreach (var part in body)
            {
                Buffer.BlockCopy(part, 0, blob, o, part.Length);
                o += part.Length;
            }
            if (o != total) throw new InvalidOperationException($"VFXPI1 v3 layout error: wrote {o} of {total} bytes.");
            return blob;
        }

        private static byte[] RootOrZero(string? rootB64, bool optional)
        {
            if (string.IsNullOrWhiteSpace(rootB64))
            {
                if (optional) return new byte[32];
                throw new InvalidOperationException("merkle_root is required.");
            }
            return Field(rootB64, "merkle_root");
        }

        private static byte[] Field(string? b64, string name)
        {
            if (string.IsNullOrWhiteSpace(b64)) throw new InvalidOperationException($"{name} is required.");
            byte[] bytes;
            try { bytes = Convert.FromBase64String(b64); }
            catch { throw new InvalidOperationException($"{name} is not valid Base64."); }
            if (!PrivacyField.IsCanonicalLe(bytes)) throw new InvalidOperationException($"{name} must be a canonical 32-byte field element.");
            return bytes;
        }

        private static byte[] NoteHash(PrivateShieldedOutput output, string name) =>
            Field(output?.NoteHashB64, $"{name} note_hash");

        private static byte[][] TwoNullifiers(PrivateTxPayload payload)
        {
            if (payload.NullsB64.Count != 2)
                throw new InvalidOperationException("The circuits spend exactly two notes: a single-note spend carries the public dummy note as its second input.");
            return new[] { Field(payload.NullsB64[0], "nullifier 0"), Field(payload.NullsB64[1], "nullifier 1") };
        }

        private static ulong Scaled(decimal amount, string name)
        {
            if (!PrivacyPedersenAmount.TryToScaledU64(amount, out var scaled, out var err))
                throw new InvalidOperationException($"{name}: {err}");
            return scaled;
        }

        private static byte[] U64(ulong v)
        {
            var b = new byte[8];
            BitConverter.TryWriteBytes(b, v);
            if (!BitConverter.IsLittleEndian) Array.Reverse(b);
            return b;
        }
    }
}
