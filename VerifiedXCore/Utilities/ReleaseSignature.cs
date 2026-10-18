using System.Security.Cryptography;
using VerifiedXCore.Services;

namespace VerifiedXCore.Utilities
{
    /// <summary>
    /// A release asset is installed by self-update only when it carries a signature, from a release signer this build
    /// names (Globals.ReleaseSignerAddresses), over the SHA-256 of the asset's bytes. The signature ships beside the asset
    /// as "&lt;asset&gt;.sig". With no signer named, self-update
    /// refuses, so a release is installed only by an operator who downloaded it.
    /// </summary>
    public static class ReleaseSignature
    {
        public const string NoSignerReason = "Self-update is off until releases are signed: download the release manually from GitHub.";
        public const string NoSignatureReason = "The release asset carries no signature (.sig): download it manually from GitHub.";
        public const string BadSignatureReason = "The release asset's signature does not verify against this build's release signers: not installed.";

        /// <summary>The asset's SHA-256, lowercase hex: what the release signer signs.</summary>
        public static string Sha256Hex(byte[] asset) => Convert.ToHexString(SHA256.HashData(asset)).ToLowerInvariant();

        /// <summary>Why <paramref name="asset"/> is not installed, or null when <paramref name="signature"/> verifies.</summary>
        public static string? Refusal(byte[] asset, string? signature)
        {
            if (Globals.ReleaseSignerAddresses.Length == 0) return NoSignerReason;
            if (string.IsNullOrWhiteSpace(signature)) return NoSignatureReason;
            var hash = Sha256Hex(asset);
            var sig = signature.Trim();
            return Globals.ReleaseSignerAddresses.Any(a => !string.IsNullOrWhiteSpace(a) && SignatureService.VerifySignature(a, hash, sig))
                ? null
                : BadSignatureReason;
        }
    }
}
