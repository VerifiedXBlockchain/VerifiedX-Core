using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// The vBTC.b contract's own rule for a signature it counts (ECDSA.tryRecover: 65 bytes, v of 27 or 28, s in the lower
    /// half of the curve order), and the same signer's signature written in the forms it does not count or that cannot be
    /// sent as they stand.
    /// </summary>
    internal static class VbtcbSignatureForms
    {
        private static readonly BigInteger Order = BigInteger.Parse("0FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141", System.Globalization.NumberStyles.HexNumber);

        /// <summary>The forms, as theory data.</summary>
        public static IEnumerable<object[]> NotCounted =>
            new[] { "high-s", "v-plus-4", "leading-space", "trailing-newline" }.Select(form => new object[] { form });

        /// <summary>Whether the contract counts these bytes, whoever signed them.</summary>
        public static bool Counted(byte[] signature) =>
            signature.Length == 65 && signature[64] is 27 or 28
            && new BigInteger(signature.AsSpan(32, 32), isUnsigned: true, isBigEndian: true) <= Order / 2;

        /// <summary>A signature as 0x + r || s || v, written again in <paramref name="form"/>.</summary>
        public static string Reencode(string signatureHex, string form)
        {
            var sig = Convert.FromHexString(signatureHex[2..]);
            switch (form)
            {
                case "high-s":                                                    // the same signature with s in the upper half
                    var s = (Order - new BigInteger(sig.AsSpan(32, 32), isUnsigned: true, isBigEndian: true)).ToByteArray(isUnsigned: true, isBigEndian: true);
                    Array.Clear(sig, 32, 32);
                    s.CopyTo(sig, 64 - s.Length);
                    sig[64] = (byte)(sig[64] == 27 ? 28 : 27);
                    break;
                case "v-plus-4":                                                  // 31 or 32: the same key, as some libraries write it
                    sig[64] += 4;
                    break;
                case "leading-space": return " " + signatureHex;
                case "trailing-newline": return signatureHex + "\n";
                default: throw new ArgumentOutOfRangeException(nameof(form), form, "Unknown form");
            }
            return "0x" + Convert.ToHexString(sig).ToLowerInvariant();
        }
    }
}
