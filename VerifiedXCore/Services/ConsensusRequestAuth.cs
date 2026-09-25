using VerifiedXCore.Data;

namespace VerifiedXCore.Services
{
    /// <summary>
    /// BB-1 (serves VX-05, VX-07, VX-08, VX-15, VX-16, VX-23): shared authentication primitives for the
    /// validator/consensus API. Several routes accepted consensus input identified only by a public
    /// address string; these helpers make "who sent this" a cryptographic fact.
    ///
    /// Signatures are verified with <see cref="SignatureService.VerifySignature"/>, which already
    /// derives the address from the signing public key and rejects a mismatch — a valid result proves
    /// the sender holds the key for the claimed address.
    /// </summary>
    public static class ConsensusRequestAuth
    {
        /// <summary>Accepted clock skew / age for timestamped (non-height-bound) messages.</summary>
        public const long MaxSkewSeconds = 90;

        /// <summary>How far ahead of the local tip a height-bound message may be (tip+1 .. tip+N).</summary>
        public const long MaxHeightsAhead = 2;

        /// <summary>
        /// Freshness without overflow. <c>Math.Abs(now - ts)</c> threw OverflowException for
        /// ts ≈ long.MinValue (VX-23) before any authentication ran.
        /// </summary>
        public static bool IsFresh(long timestampUnix, long nowUnix)
        {
            if (timestampUnix <= 0) return false;                       // 0 used to mean "skip the check" (VX-07)
            if (timestampUnix > nowUnix + MaxSkewSeconds) return false;  // no subtraction that can overflow
            return nowUnix - timestampUnix <= MaxSkewSeconds;            // both positive: cannot overflow
        }

        /// <summary>Height-bound consensus input must be for the round in progress, not pre-seeded far ahead.</summary>
        public static bool IsHeightInWindow(long blockHeight, long tipHeight) =>
            blockHeight >= tipHeight + 1 && blockHeight <= tipHeight + MaxHeightsAhead;

        /// <summary>True when the signature over <paramref name="message"/> was produced by <paramref name="address"/>'s key.</summary>
        public static bool VerifySigner(string? address, string message, string? signature)
        {
            if (string.IsNullOrEmpty(address) || string.IsNullOrEmpty(signature)) return false;
            try { return SignatureService.VerifySignature(address, message, signature); }
            catch { return false; }
        }

        /// <summary>
        /// One-time use of a signature for timestamped messages. Shares <see cref="Globals.Signatures"/>
        /// (pruned after 300 s, longer than the freshness window) with the other signed-message paths.
        /// Call only after every other check passed.
        /// </summary>
        public static bool TryConsume(string signature, long nowUnix) =>
            !string.IsNullOrEmpty(signature) && Globals.Signatures.TryAdd(CanonicalSignatureKey(signature), nowUnix);

        /// <summary>
        /// VX-07 (follow-up): the one-use key for a signature is its decoded (r, low-s) pair, not the raw string. Base64
        /// decoding ignores whitespace and ECDSA accepts both s and n−s, so the same signature had many spellings and a
        /// re-encoded copy passed the replay check as "unused". Falls back to the raw string if it cannot be decoded.
        /// </summary>
        public static string CanonicalSignatureKey(string signature)
        {
            try
            {
                var sigPart = signature.Split('.', 2)[0];
                var sig = EllipticCurve.Signature.fromBase64(sigPart);
                var n = EllipticCurve.Curves.secp256k1.N;
                var s = sig.s > n / 2 ? n - sig.s : sig.s;
                return "sig:" + sig.r.ToString("x") + ":" + s.ToString("x");
            }
            catch
            {
                return signature;
            }
        }

        /// <summary>True when <paramref name="publicKeyHex"/> ("04…" uncompressed) derives <paramref name="address"/>.</summary>
        public static bool PublicKeyMatchesAddress(string? publicKeyHex, string? address)
        {
            if (string.IsNullOrEmpty(publicKeyHex) || string.IsNullOrEmpty(address)) return false;
            try { return AccountData.GetHumanAddress(publicKeyHex) == address; }
            catch { return false; }
        }
    }
}
