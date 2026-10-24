using System.Numerics;
using System.Security.Cryptography;

namespace VerifiedXCore.Privacy
{
    /// <summary>
    /// BLS12-381 scalar-field (Fr) encoding helpers for everything that crosses into the PLONK circuits. The circuits and
    /// the native library take field elements as 32 little-endian bytes in arkworks <b>canonical</b> form: a value at or
    /// above the modulus is refused by the prover (<c>ErrCrypto</c>). Randomness and viewing keys are therefore reduced
    /// here before use; a raw 32-byte random value is above the modulus about half the time.
    /// </summary>
    public static class PrivacyField
    {
        /// <summary>r = 0x73eda753299d7d483339d80809a1d80553bda402fffe5bfeffffffff00000001.</summary>
        public static readonly BigInteger Modulus = BigInteger.Parse("073eda753299d7d483339d80809a1d80553bda402fffe5bfeffffffff00000001",
            System.Globalization.NumberStyles.HexNumber);

        public static readonly byte[] Zero32 = new byte[PlonkNative.ScalarSize];

        /// <summary>Little-endian 32-byte encoding of a non-negative value below the modulus.</summary>
        public static byte[] ToLe32(BigInteger value)
        {
            if (value.Sign < 0 || value >= Modulus)
                throw new ArgumentOutOfRangeException(nameof(value), "Value must be in [0, r).");
            var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: false);
            var out32 = new byte[PlonkNative.ScalarSize];
            Buffer.BlockCopy(bytes, 0, out32, 0, Math.Min(bytes.Length, PlonkNative.ScalarSize));
            return out32;
        }

        /// <summary>The value of a 32-byte little-endian encoding (not reduced).</summary>
        public static BigInteger FromLe(ReadOnlySpan<byte> le32)
        {
            if (le32.Length != PlonkNative.ScalarSize)
                throw new ArgumentException("Field element must be 32 bytes.", nameof(le32));
            return new BigInteger(le32, isUnsigned: true, isBigEndian: false);
        }

        /// <summary>Whether <paramref name="le32"/> is a canonical field element (32 bytes, below the modulus).</summary>
        public static bool IsCanonicalLe(ReadOnlySpan<byte> le32) =>
            le32.Length == PlonkNative.ScalarSize && FromLe(le32) < Modulus;

        /// <summary>Reduces any 32-byte little-endian value modulo r (what the native <c>from_le_bytes_mod_order</c> does).</summary>
        public static byte[] ReduceLe(ReadOnlySpan<byte> le32) => ToLe32(FromLe(le32) % Modulus);

        /// <summary>The little-endian field element of a u64.</summary>
        public static byte[] FromUInt64(ulong value)
        {
            var out32 = new byte[PlonkNative.ScalarSize];
            BitConverter.TryWriteBytes(out32.AsSpan(0, 8), value);
            if (!BitConverter.IsLittleEndian) Array.Reverse(out32, 0, 8);
            return out32;
        }

        /// <summary>A uniformly random canonical field element (32 random bytes reduced modulo r).</summary>
        public static byte[] RandomCanonical()
        {
            var raw = new byte[PlonkNative.ScalarSize + 16]; // extra bytes make the reduction bias negligible
            RandomNumberGenerator.Fill(raw);
            var v = new BigInteger(raw, isUnsigned: true, isBigEndian: false) % Modulus;
            return ToLe32(v);
        }
    }
}
