using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using ReserveBlockCore.Models;
using ReserveBlockCore.Models.Privacy;

namespace ReserveBlockCore.Privacy
{
    /// <summary>
    /// Canonical public-input blob for <see cref="PlonkNative.plonk_verify"/> (v1 wire format).
    /// Must stay aligned with the <c>plonk</c> workspace circuits when those ship.
    /// </summary>
    public static class PlonkPublicInputsV1
    {
        public const int Version = 1;
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("VFXPI1");

        /// <summary>32-byte domain separator per asset string (e.g. <c>VFX</c> or <c>VBTC:uid</c>).</summary>
        public static byte[] AssetTag32(string asset)
        {
            if (string.IsNullOrEmpty(asset))
                return new byte[32];
            return SHA256.HashData(Encoding.UTF8.GetBytes(asset));
        }

        public static bool TryBuild(Transaction tx, PrivateTxPayload payload, out byte[] publicInputs, out string? error)
        {
            publicInputs = Array.Empty<byte>();
            error = null;
            try
            {
                publicInputs = tx.TransactionType switch
                {
                    TransactionType.VFX_SHIELD or TransactionType.VBTC_V2_SHIELD => BuildShield(tx, payload),
                    TransactionType.VFX_UNSHIELD or TransactionType.VBTC_V2_UNSHIELD => BuildUnshield(tx, payload),
                    TransactionType.VFX_PRIVATE_TRANSFER or TransactionType.VBTC_V2_PRIVATE_TRANSFER => BuildTransfer(tx, payload),
                    _ => throw new InvalidOperationException("Not a PLONK-backed private transaction type.")
                };
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static bool TryBuildFeeCircuit(Transaction tx, PrivateTxPayload payload, out byte[] publicInputs, out string? error)
        {
            publicInputs = Array.Empty<byte>();
            error = null;
            try
            {
                publicInputs = BuildFee(tx, payload);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static byte[] BuildShield(Transaction tx, PrivateTxPayload payload)
        {
            if (payload.Outs.Count < 1)
                throw new InvalidOperationException("Shield payload requires at least one output commitment.");
            var merkle = DecodeRoot32(payload.MerkleRootB64);
            var g1 = Convert.FromBase64String(payload.Outs[0].CommitmentB64);
            if (g1.Length != PlonkNative.G1CompressedSize)
                throw new InvalidOperationException("Output commitment length invalid.");
            var amt = ToScaledU64(tx.TransactionType == TransactionType.VFX_SHIELD ? tx.Amount : payload.VbtcTransparentAmount ?? 0m);
            return Concat(
                Header(PlonkCircuitType.Shield),
                AssetTag32(payload.Asset),
                merkle,
                WriteU64(amt),
                g1);
        }

        private static byte[] BuildUnshield(Transaction tx, PrivateTxPayload payload)
        {
            var merkle = DecodeRoot32(payload.MerkleRootB64);
            var fee = ToScaledU64(payload.Fee ?? Globals.PrivateTxFixedFee);
            var transparentOut = ToScaledU64(tx.TransactionType == TransactionType.VFX_UNSHIELD ? tx.Amount : payload.VbtcTransparentAmount ?? 0m);
            var nulls = PadNullifiers(payload.NullsB64);
            byte[] change = new byte[PlonkNative.G1CompressedSize];
            if (payload.Outs.Count > 0)
            {
                var g = Convert.FromBase64String(payload.Outs.OrderBy(o => o.Index).First().CommitmentB64);
                if (g.Length == PlonkNative.G1CompressedSize)
                    Buffer.BlockCopy(g, 0, change, 0, PlonkNative.G1CompressedSize);
            }
            return Concat(
                Header(PlonkCircuitType.Unshield),
                AssetTag32(payload.Asset),
                merkle,
                WriteU64(fee),
                WriteU64(transparentOut),
                nulls,
                change);
        }

        private static byte[] BuildTransfer(Transaction tx, PrivateTxPayload payload)
        {
            var merkle = DecodeRoot32(payload.MerkleRootB64);
            var fee = ToScaledU64(payload.Fee ?? Globals.PrivateTxFixedFee);
            var nulls = PadNullifiers(payload.NullsB64);
            var outs = PadOutputs(payload.Outs);
            return Concat(
                Header(PlonkCircuitType.Transfer),
                AssetTag32(payload.Asset),
                merkle,
                WriteU64(fee),
                nulls,
                outs);
        }

        /// <summary>VFX fee circuit (vBTC Z→Z / Z→T fee leg): single nullifier + change + fee.</summary>
        private static byte[] BuildFee(Transaction tx, PrivateTxPayload payload)
        {
            var merkle = DecodeRoot32(payload.FeeTreeMerkleRoot);
            var fee = ToScaledU64(payload.Fee ?? Globals.PrivateTxFixedFee);
            var null32 = new byte[32];
            if (!string.IsNullOrWhiteSpace(payload.FeeInputNullifierB64))
            {
                var n = Convert.FromBase64String(payload.FeeInputNullifierB64);
                if (n.Length == 32)
                    Buffer.BlockCopy(n, 0, null32, 0, 32);
            }
            var change = new byte[PlonkNative.G1CompressedSize];
            if (!string.IsNullOrWhiteSpace(payload.FeeOutputCommitmentB64))
            {
                var g = Convert.FromBase64String(payload.FeeOutputCommitmentB64);
                if (g.Length == PlonkNative.G1CompressedSize)
                    Buffer.BlockCopy(g, 0, change, 0, PlonkNative.G1CompressedSize);
            }
            return Concat(
                Header(PlonkCircuitType.Fee),
                AssetTag32("VFX"),
                merkle,
                WriteU64(fee),
                null32,
                change);
        }

        private static byte[] Header(PlonkCircuitType circuit)
        {
            var h = new byte[Magic.Length + 2];
            Buffer.BlockCopy(Magic, 0, h, 0, Magic.Length);
            h[Magic.Length] = Version;
            h[Magic.Length + 1] = (byte)circuit;
            return h;
        }

        private static byte[] DecodeRoot32(string? b64)
        {
            if (string.IsNullOrWhiteSpace(b64))
                return new byte[32];
            var r = Convert.FromBase64String(b64.Trim());
            if (r.Length != 32)
                throw new InvalidOperationException("merkle_root must decode to 32 bytes.");
            return r;
        }

        private static ulong ToScaledU64(decimal amount)
        {
            if (!PrivacyPedersenAmount.TryToScaledU64(amount, out var s, out var err))
                throw new InvalidOperationException(err ?? "amount range");
            return s;
        }

        private static byte[] WriteU64(ulong v)
        {
            var b = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(b, v);
            return b;
        }

        /// <summary>Exactly 2 nullifier slots (zero-padded).</summary>
        private static byte[] PadNullifiers(IReadOnlyList<string> nullsB64)
        {
            var buf = new byte[64];
            for (var i = 0; i < Math.Min(2, nullsB64.Count); i++)
            {
                var n = Convert.FromBase64String(nullsB64[i]);
                if (n.Length != 32)
                    throw new InvalidOperationException("Nullifier must be 32 bytes.");
                Buffer.BlockCopy(n, 0, buf, i * 32, 32);
            }
            return buf;
        }

        /// <summary>Exactly 2 output commitments (G1 compressed, zero-padded).</summary>
        private static byte[] PadOutputs(IReadOnlyList<PrivateShieldedOutput> outs)
        {
            var buf = new byte[PlonkNative.G1CompressedSize * 2];
            var ordered = outs.OrderBy(o => o.Index).ToList();
            for (var i = 0; i < Math.Min(2, ordered.Count); i++)
            {
                var g = Convert.FromBase64String(ordered[i].CommitmentB64);
                if (g.Length != PlonkNative.G1CompressedSize)
                    throw new InvalidOperationException("Output commitment length invalid.");
                Buffer.BlockCopy(g, 0, buf, i * PlonkNative.G1CompressedSize, PlonkNative.G1CompressedSize);
            }
            return buf;
        }

        private static byte[] Concat(params byte[][] parts)
        {
            var len = parts.Sum(p => p.Length);
            var o = new byte[len];
            var pos = 0;
            foreach (var p in parts)
            {
                Buffer.BlockCopy(p, 0, o, pos, p.Length);
                pos += p.Length;
            }
            return o;
        }
    }
}
