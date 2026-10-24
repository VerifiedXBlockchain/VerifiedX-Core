using System.Numerics;
using System.Security.Cryptography;

namespace VerifiedXCore.Bitcoin.Attestation
{
    /// <summary>
    /// One Bitcoin block header (80 bytes), parsed without any library so that what consensus accepts depends on nothing
    /// but these lines. Hashes are held in the byte order block explorers print (big-endian hex).
    /// docs/PLAN-vBTC-Bitcoin-Attestation.md, section 3.1.
    /// </summary>
    public sealed class BtcHeader
    {
        public const int Size = 80;

        public int Version { get; }
        /// <summary>Hash of the previous block, explorer byte order, lowercase hex.</summary>
        public string PrevHash { get; }
        /// <summary>Merkle root of the block's transactions, explorer byte order, lowercase hex.</summary>
        public string MerkleRoot { get; }
        /// <summary>Block time, seconds since the Unix epoch.</summary>
        public uint Time { get; }
        /// <summary>The compact target the block claims to meet.</summary>
        public uint Bits { get; }
        public uint Nonce { get; }
        /// <summary>Double SHA-256 of the 80 bytes, explorer byte order, lowercase hex.</summary>
        public string Hash { get; }
        /// <summary>The 80 bytes, lowercase hex.</summary>
        public string Hex { get; }

        private readonly BigInteger _hashValue;

        private BtcHeader(byte[] raw)
        {
            Version = BitConverter.ToInt32(raw, 0);
            PrevHash = ReversedHex(raw, 4, 32);
            MerkleRoot = ReversedHex(raw, 36, 32);
            Time = BitConverter.ToUInt32(raw, 68);
            Bits = BitConverter.ToUInt32(raw, 72);
            Nonce = BitConverter.ToUInt32(raw, 76);
            Hex = Convert.ToHexString(raw).ToLowerInvariant();
            var digest = SHA256.HashData(SHA256.HashData(raw));
            Hash = ReversedHex(digest, 0, 32);
            // The digest is a 256-bit little-endian number; BigInteger reads little-endian, unsigned.
            _hashValue = new BigInteger(digest, isUnsigned: true, isBigEndian: false);
        }

        /// <summary>Parses exactly 80 bytes of hex. Anything else is not a header.</summary>
        public static bool TryParse(string? hex, out BtcHeader? header)
        {
            header = null;
            if (string.IsNullOrEmpty(hex) || hex.Length != Size * 2)
                return false;
            byte[] raw;
            try { raw = Convert.FromHexString(hex); }
            catch { return false; }
            if (!BitConverter.IsLittleEndian)
                return false; // every platform this node runs on is little-endian; refuse rather than misread
            header = new BtcHeader(raw);
            return true;
        }

        /// <summary>
        /// The target a compact value stands for, exactly as Bitcoin decodes it (arith_uint256::SetCompact). False when the
        /// encoding is negative, zero or overflows 256 bits: no block can meet such a target.
        /// </summary>
        public static bool TryDecodeCompact(uint bits, out BigInteger target)
        {
            target = BigInteger.Zero;
            var size = (int)(bits >> 24);
            var word = bits & 0x007fffffu;
            if (size <= 3)
                target = new BigInteger(word >> (8 * (3 - size)));
            else
                target = new BigInteger(word) << (8 * (size - 3));

            var negative = word != 0 && (bits & 0x00800000u) != 0;
            var overflow = word != 0 && (size > 34 || (word > 0xff && size > 33) || (word > 0xffff && size > 32));
            return !negative && !overflow && !target.IsZero;
        }

        /// <summary>The compact form of a target, exactly as Bitcoin encodes it (arith_uint256::GetCompact, unsigned).</summary>
        public static uint EncodeCompact(BigInteger target)
        {
            if (target.Sign <= 0) return 0;
            var size = (int)((target.GetBitLength() + 7) / 8);
            uint compact;
            if (size <= 3)
                compact = (uint)(ulong)(target << (8 * (3 - size)));
            else
                compact = (uint)(ulong)((target >> (8 * (size - 3))) & 0xffffffffUL);
            // The 0x00800000 bit is the sign: when it is set, shift the mantissa down a byte and grow the exponent.
            if ((compact & 0x00800000u) != 0)
            {
                compact >>= 8;
                size++;
            }
            return (compact & 0x007fffffu) | ((uint)size << 24);
        }

        /// <summary>Whether this header's hash is at or below the target its own <see cref="Bits"/> encode.</summary>
        public bool MeetsOwnTarget() => TryDecodeCompact(Bits, out var target) && _hashValue <= target;

        /// <summary>
        /// The work this header represents: 2^256 / (target + 1), as Bitcoin's GetBlockProof computes it. Zero for an
        /// undecodable target.
        /// </summary>
        public BigInteger Work() =>
            TryDecodeCompact(Bits, out var target) ? (BigInteger.One << 256) / (target + BigInteger.One) : BigInteger.Zero;

        private static string ReversedHex(byte[] source, int offset, int count)
        {
            var copy = new byte[count];
            Array.Copy(source, offset, copy, 0, count);
            Array.Reverse(copy);
            return Convert.ToHexString(copy).ToLowerInvariant();
        }
    }
}
