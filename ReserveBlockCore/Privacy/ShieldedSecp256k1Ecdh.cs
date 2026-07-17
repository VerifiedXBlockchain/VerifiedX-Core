using System.Numerics;
using System.Security.Cryptography;
using NBitcoin;
using ReserveBlockCore.EllipticCurve;
using ReserveBlockCore.Extensions;

namespace ReserveBlockCore.Privacy
{
    /// <summary>
    /// secp256k1 ECDH for shielded notes: shared secret = SHA-256(x-coordinate of ephemeralSecret * recipientPub), x encoded as 32-byte big-endian field element.
    /// </summary>
    internal static class ShieldedSecp256k1Ecdh
    {
        internal static byte[] DeriveSharedSecret32(ReadOnlySpan<byte> ephemeralPrivateKey32, ReadOnlySpan<byte> recipientEncryptionPubKey33Compressed)
        {
            if (ephemeralPrivateKey32.Length != 32)
                throw new ArgumentException("Ephemeral private key must be 32 bytes.", nameof(ephemeralPrivateKey32));

            var nbPub = new PubKey(recipientEncryptionPubKey33Compressed.ToArray());
            var curve = Curves.getCurveByName("secp256k1");
            var p = DecompressToPoint(nbPub, curve);
            var privHex = ephemeralPrivateKey32.ToArray().ToStringHex();
            var bi = BigInteger.Parse("00" + privHex, System.Globalization.NumberStyles.AllowHexSpecifier);
            bi %= curve.N;
            if (bi < 0)
                bi += curve.N;
            if (bi == 0 || bi >= curve.N)
                throw new ArgumentException("Ephemeral private key is out of range for secp256k1.");

            var shared = EcdsaMath.multiply(p, bi, curve.N, curve.A, curve.P);
            var xBytes = BinaryAscii.stringFromNumber(shared.x, curve.length());
            return SHA256.HashData(xBytes);
        }

        private static Point DecompressToPoint(PubKey pubKey, CurveFp curve)
        {
            var unc = pubKey.Decompress().ToBytes();
            if (unc.Length != 65 || unc[0] != 0x04)
                throw new InvalidOperationException("Expected uncompressed secp256k1 pubkey (65 bytes).");

            var baseLen = curve.length();
            var xs = BinaryAscii.hexFromBinary(Bytes.sliceByteArray(unc, 1, baseLen));
            var ys = BinaryAscii.hexFromBinary(Bytes.sliceByteArray(unc, 1 + baseLen, baseLen));
            var p = new Point(BinaryAscii.numberFromHex(xs), BinaryAscii.numberFromHex(ys));
            if (!curve.contains(p))
                throw new ArgumentException("Recipient encryption pubkey is not on secp256k1.");
            return p;
        }
    }
}
