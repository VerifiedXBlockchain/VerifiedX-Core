using System.Globalization;
using System.Numerics;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;

namespace VerifiedXCore.Utilities
{
    /// <summary>
    /// VX-11: private-key hex parsing for IMPORTS.
    ///
    /// <c>BigInteger.Parse(hex, AllowHexSpecifier)</c> reads a leading digit 8–f as a two's-complement
    /// NEGATIVE number. An externally generated 64-digit key starting 8–f (about half of all keys) was
    /// therefore turned into <c>d − 2^256</c>, silently reduced mod n by the curve maths, and imported as a
    /// DIFFERENT key pair and address — with no error.
    ///
    /// Keys this wallet generates are unaffected: it stores <c>secret.ToString("x")</c>, which prefixes a 0
    /// whenever the top digit is 8–f, so every stored-key parse (all signing paths) reads them correctly.
    /// Those parses are therefore left alone: accounts created by a past mis-import are stored in the
    /// negative form and would change address if their stored key were re-parsed unsigned.
    ///
    /// Owner-approved design: imports use the correct parse by default, the old derivation is kept frozen
    /// as the "legacy" path (see <see cref="ParseLegacy"/>, AccountData.RestoreAccountLegacy), a legacy
    /// address with on-chain history is restored alongside automatically, and
    /// <see cref="CanonicalKeyHex"/> exports the scalar actually used so it imports correctly anywhere.
    /// </summary>
    public static class KeyParsing
    {
        public static BigInteger CurveOrder => Curves.secp256k1.N;

        /// <summary>
        /// Parses an externally supplied private key: optional "0x", 1–64 hex digits, read as UNSIGNED, and
        /// required to be a valid secp256k1 scalar (1 ≤ d &lt; n). Spaces are ignored (as before).
        /// </summary>
        public static bool TryParseExternalPrivateKeyHex(string? hex, out BigInteger scalar, out string error)
        {
            scalar = BigInteger.Zero;
            error = "";
            var s = (hex ?? "").Replace(" ", "").Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
            if (s.Length == 0 || !s.All(Uri.IsHexDigit))
            {
                error = "Private key must be hexadecimal.";
                return false;
            }
            // Leading zeros carry no value: the wallet itself shows a high-digit key with a leading 0
            // (65 characters), so strip them before the length check.
            s = s.TrimStart('0');
            if (s.Length == 0 || s.Length > 64)
            {
                error = "Private key must be 1 to 64 significant hexadecimal characters.";
                return false;
            }
            scalar = BigInteger.Parse("0" + s, NumberStyles.AllowHexSpecifier); // leading 0: never negative
            if (scalar.Sign <= 0 || scalar >= CurveOrder)
            {
                error = "Private key is not a valid secp256k1 key (must be between 1 and n-1).";
                return false;
            }
            return true;
        }

        /// <summary>
        /// FROZEN legacy interpretation (the pre-fix import behaviour). Used only to reproduce addresses that
        /// a previous wallet version derived. Do not change.
        /// </summary>
        public static BigInteger ParseLegacy(string hex) =>
            BigInteger.Parse(hex.Replace(" ", ""), NumberStyles.AllowHexSpecifier);

        /// <summary>Address derived from a scalar exactly as the wallet derives it.</summary>
        public static string DeriveAddress(BigInteger scalar)
        {
            var pk = new PrivateKey("secp256k1", scalar);
            return AccountData.GetHumanAddress("04" + AccountData.ByteToHex(pk.publicKey().toString()));
        }

        /// <summary>
        /// The address a pre-fix wallet would have derived from <paramref name="hex"/>, or null when the
        /// legacy interpretation gives the same scalar (top digit 0–7) or cannot be parsed.
        /// </summary>
        public static string? LegacyAddressIfDifferent(string hex)
        {
            try
            {
                var legacy = ParseLegacy(hex);
                if (!TryParseExternalPrivateKeyHex(hex, out var canonical, out _)) return null;
                if (Integer.modulo(legacy, CurveOrder) == canonical) return null;
                if (legacy.IsZero) return null;
                return DeriveAddress(legacy);
            }
            catch { return null; }
        }

        /// <summary>
        /// VX-11 single-key choice (used where one key yields one account, e.g. reserve restores): the correct
        /// scalar by default; the legacy scalar when forced, or when only the legacy address has on-chain
        /// history (the key was previously imported by an older wallet and used there).
        /// </summary>
        public static bool TryParseImportedKey(string hex, bool forceLegacy, Func<string, bool> hasFootprint,
            out BigInteger scalar, out bool usedLegacy, out string error)
        {
            usedLegacy = false;
            error = "";
            if (forceLegacy)
            {
                try { scalar = ParseLegacy(hex); usedLegacy = true; return !scalar.IsZero; }
                catch { scalar = BigInteger.Zero; error = "Private key is not valid hexadecimal."; return false; }
            }
            if (!TryParseExternalPrivateKeyHex(hex, out scalar, out error))
                return false;
            var legacyAddress = LegacyAddressIfDifferent(hex);
            if (legacyAddress != null && hasFootprint(legacyAddress) && !hasFootprint(DeriveAddress(scalar)))
            {
                scalar = ParseLegacy(hex);
                usedLegacy = true;
            }
            return true;
        }

        /// <summary>
        /// VX-11 canonical export: the scalar actually used for signing, reduced into [1, n-1] and written as
        /// exactly 64 lowercase hex digits. For wallet-generated keys this is the key itself; for an account
        /// created by a legacy mis-import it is the key that really controls that account, so any standard
        /// tool — and this wallet's corrected import — derives the same address from it.
        /// </summary>
        public static string CanonicalKeyHex(BigInteger secret)
        {
            var d = Integer.modulo(secret, CurveOrder);
            var hex = d.ToString("x");
            if (hex.Length > 64 && hex.StartsWith("0")) hex = hex.TrimStart('0');
            return hex.PadLeft(64, '0');
        }

        /// <summary>Canonical export of a stored key string (either stored form round-trips as before).</summary>
        public static string CanonicalKeyHexFromStored(string storedKeyHex) =>
            CanonicalKeyHex(BigInteger.Parse(storedKeyHex, NumberStyles.AllowHexSpecifier));
    }
}
