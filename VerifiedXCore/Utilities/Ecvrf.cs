using System.Numerics;
using System.Security.Cryptography;
using VerifiedXCore.EllipticCurve;

namespace VerifiedXCore.Utilities
{
    /// <summary>
    /// The elliptic-curve VRF of RFC 9381, with encode-to-curve by try-and-increment (section 5.4.1.1), nonces from
    /// RFC 6979 (section 5.4.2.1), SHA-256, compressed points and a 16-octet challenge. A proof is 81 octets: Gamma, c, s.
    /// Only the holder of a secret key can make the proof for a message, every holder of the public key can check it, and
    /// the output (beta) is the same for every valid proof of a message: a prover cannot choose among outputs.
    /// <see cref="P256"/> is the RFC's ECVRF-P256-SHA256-TAI (suite 0x01), against which this code is tested with the RFC's
    /// own vectors; <see cref="Secp256k1"/> is the same construction on the curve of validator keys, under its own suite
    /// octet (0xFE), so no proof made for one suite is ever read as one of the other.
    /// </summary>
    public sealed class Ecvrf
    {
        public static readonly Ecvrf P256 = new(Curves.prime256v1, 0x01);
        public static readonly Ecvrf Secp256k1 = new(Curves.secp256k1, 0xFE);

        public const int ProofLength = PtLen + CLen + QLen;
        public const int OutputLength = 32;
        private const int PtLen = 33, CLen = 16, QLen = 32;

        private readonly CurveFp _c;
        private readonly byte _suite;

        private Ecvrf(CurveFp curve, byte suite) { _c = curve; _suite = suite; }

        /// <summary>The proof for <paramref name="alpha"/> under secret scalar <paramref name="x"/> (1 to q-1).</summary>
        public byte[] Prove(BigInteger x, byte[] alpha)
        {
            if (x <= 0 || x >= _c.N) throw new ArgumentOutOfRangeException(nameof(x));
            var y = Mul(_c.G, x);
            var h = EncodeToCurve(Encode(y), alpha);
            var hString = Encode(h);
            var gamma = Mul(h, x);
            var k = Nonce(x, hString);
            var c = Challenge(y, h, gamma, Mul(_c.G, k), Mul(h, k));
            var s = Mod(k + c * x, _c.N);
            return Concat(Encode(gamma), IntToString(c, CLen), IntToString(s, QLen));
        }

        /// <summary>
        /// Whether <paramref name="proof"/> is a valid proof for <paramref name="alpha"/> under the public key
        /// <paramref name="publicKey"/> (33-octet compressed or 65-octet uncompressed), and its output when it is.
        /// </summary>
        public bool Verify(byte[] publicKey, byte[] proof, byte[] alpha, out byte[] beta)
        {
            beta = Array.Empty<byte>();
            var y = Decode(publicKey);
            if (y == null || IsIdentity(y) || proof == null || proof.Length != ProofLength) return false;
            var gamma = Decode(proof.AsSpan(0, PtLen).ToArray());
            if (gamma == null) return false;
            var c = StringToInt(proof.AsSpan(PtLen, CLen));
            var s = StringToInt(proof.AsSpan(PtLen + CLen, QLen));
            if (s >= _c.N) return false;
            var h = EncodeToCurve(Encode(y), alpha);
            var u = Sub(Mul(_c.G, s), Mul(y, c));
            var v = Sub(Mul(h, s), Mul(gamma, c));
            // No valid proof gives the identity here (k is 1 to q-1): a proof that does is refused, not encoded.
            if (IsIdentity(u) || IsIdentity(v)) return false;
            if (Challenge(y, h, gamma, u, v) != c) return false;
            beta = OutputOf(gamma);
            return true;
        }

        /// <summary>The output of a proof (section 5.2), without checking it: only for a proof already verified.</summary>
        public byte[]? ProofToHash(byte[] proof)
        {
            if (proof == null || proof.Length != ProofLength) return null;
            var gamma = Decode(proof.AsSpan(0, PtLen).ToArray());
            return gamma == null ? null : OutputOf(gamma);
        }

        /// <summary>The compressed public key of secret scalar <paramref name="x"/>.</summary>
        public byte[] PublicKeyOf(BigInteger x) => Encode(Mul(_c.G, x));

        // ── RFC 9381 steps ──────────────────────────────────────────────────────────────────

        private byte[] OutputOf(Point gamma) => SHA256.HashData(Concat(new[] { _suite, (byte)0x03 }, Encode(gamma), new byte[] { 0x00 })); // cofactor 1

        internal Point EncodeToCurve(byte[] salt, byte[] alpha)
        {
            for (var ctr = 0; ctr < 256; ctr++)
            {
                var hash = SHA256.HashData(Concat(new[] { _suite, (byte)0x01 }, salt, alpha, new[] { (byte)ctr, (byte)0x00 }));
                var h = Decode(Concat(new byte[] { 0x02 }, hash));
                if (h != null && !IsIdentity(h)) return h;
            }
            throw new CryptographicException("encode_to_curve found no point"); // probability about 2^-256
        }

        /// <summary>RFC 6979 section 3.2 with m = h_string and the ECDSA suitability check of step h.3 left out.</summary>
        internal BigInteger Nonce(BigInteger x, byte[] hString)
        {
            var h1 = SHA256.HashData(hString);
            var xOctets = IntToString(x, QLen);
            var h1Octets = IntToString(Mod(BitsToInt(h1), _c.N), QLen); // bits2octets
            var v = Enumerable.Repeat((byte)0x01, 32).ToArray();
            var k = new byte[32];
            k = HMACSHA256.HashData(k, Concat(v, new byte[] { 0x00 }, xOctets, h1Octets));
            v = HMACSHA256.HashData(k, v);
            k = HMACSHA256.HashData(k, Concat(v, new byte[] { 0x01 }, xOctets, h1Octets));
            v = HMACSHA256.HashData(k, v);
            while (true)
            {
                v = HMACSHA256.HashData(k, v); // qlen 256 bits: one block of T
                var candidate = BitsToInt(v);
                if (candidate >= 1 && candidate < _c.N) return candidate;
                k = HMACSHA256.HashData(k, Concat(v, new byte[] { 0x00 }));
                v = HMACSHA256.HashData(k, v);
            }
        }

        private BigInteger Challenge(Point p1, Point p2, Point p3, Point p4, Point p5)
        {
            var hash = SHA256.HashData(Concat(new[] { _suite, (byte)0x02 }, Encode(p1), Encode(p2), Encode(p3), Encode(p4), Encode(p5), new byte[] { 0x00 }));
            return StringToInt(hash.AsSpan(0, CLen));
        }

        // ── Points and integers ─────────────────────────────────────────────────────────────

        private static bool IsIdentity(Point p) => p.isAtInfinity();

        private Point Mul(Point p, BigInteger n) => EcdsaMath.multiply(p, Mod(n, _c.N), _c.N, _c.A, _c.P);

        private Point Sub(Point a, Point b) =>
            IsIdentity(b) ? a : EcdsaMath.add(a, new Point(b.x, Mod(-b.y, _c.P)), _c.A, _c.P);

        /// <summary>SEC1 2.3.3 with compression. The identity has no encoding here: no step encodes it for a valid key.</summary>
        internal byte[] Encode(Point p)
        {
            if (IsIdentity(p)) throw new CryptographicException("the identity has no compressed encoding");
            return Concat(new[] { (byte)(p.y.IsEven ? 0x02 : 0x03) }, IntToString(p.x, PtLen - 1));
        }

        /// <summary>SEC1 2.3.4: a compressed (33) or uncompressed (65) point on the curve; null when it is not one.</summary>
        internal Point? Decode(byte[]? s)
        {
            if (s == null) return null;
            if (s.Length == PtLen && (s[0] == 0x02 || s[0] == 0x03))
            {
                var x = StringToInt(s.AsSpan(1));
                if (x >= _c.P) return null;
                var rhs = Mod(x * x * x + _c.A * x + _c.B, _c.P);
                var y = BigInteger.ModPow(rhs, (_c.P + 1) / 4, _c.P); // p = 3 mod 4 on both curves
                if (Mod(y * y, _c.P) != rhs) return null;
                if (y.IsEven != (s[0] == 0x02)) y = _c.P - y;
                if (y.IsZero) return null;
                return new Point(x, y);
            }
            if (s.Length == 2 * (PtLen - 1) + 1 && s[0] == 0x04)
            {
                var p = new Point(StringToInt(s.AsSpan(1, PtLen - 1)), StringToInt(s.AsSpan(PtLen, PtLen - 1)));
                return p.x < _c.P && p.y < _c.P && !p.y.IsZero && _c.contains(p) ? p : null;
            }
            return null;
        }

        private BigInteger BitsToInt(byte[] b)
        {
            var v = StringToInt(b);
            var qlen = (int)Math.Ceiling(BigInteger.Log(_c.N + 1, 2));
            var blen = b.Length * 8;
            return blen > qlen ? v >> (blen - qlen) : v;
        }

        private static BigInteger Mod(BigInteger a, BigInteger m) { var r = a % m; return r.Sign < 0 ? r + m : r; }

        internal static BigInteger StringToInt(ReadOnlySpan<byte> s) => new(s, isUnsigned: true, isBigEndian: true);

        internal static byte[] IntToString(BigInteger a, int length)
        {
            var bytes = a.ToByteArray(isUnsigned: true, isBigEndian: true);
            if (bytes.Length > length) throw new ArgumentOutOfRangeException(nameof(a));
            var o = new byte[length];
            bytes.CopyTo(o, length - bytes.Length);
            return o;
        }

        private static byte[] Concat(params byte[][] parts)
        {
            var o = new byte[parts.Sum(p => p.Length)];
            var at = 0;
            foreach (var p in parts) { p.CopyTo(o, at); at += p.Length; }
            return o;
        }
    }
}
