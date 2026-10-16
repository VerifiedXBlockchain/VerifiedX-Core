using NBitcoin;

namespace VerifiedXCore.Bitcoin.Services
{
    /// <summary>
    /// The Bitcoin-signed messages a BTC name carries from Globals.BtcNameSignerOwnsAddressHeight. Each one is
    /// "&lt;unix seconds&gt;:&lt;VFX owner&gt;:&lt;name.btc&gt;" and is signed by the key of the Bitcoin address the name
    /// points at, so a signature only fits the one owner and name it was made for.
    /// </summary>
    public static class BtcNameSignatures
    {
        /// <summary>How long the receiver's signature on a transfer stays usable; long enough to hand it to the sender.</summary>
        public const long TransferAcceptanceMaxAgeSeconds = 24 * 60 * 60;

        public static string Suffixed(string name) =>
            name.EndsWith(".btc", StringComparison.OrdinalIgnoreCase) ? name : name + ".btc";

        public static string Message(long timestamp, string owner, string name) => $"{timestamp}:{owner}:{Suffixed(name)}";

        public static bool TryParse(string message, out long timestamp, out string owner, out string name)
        {
            timestamp = 0; owner = ""; name = "";
            var parts = message.Split(':');
            if (parts.Length != 3 || !long.TryParse(parts[0], out timestamp))
                return false;
            owner = parts[1];
            name = parts[2];
            return owner.Length > 0 && name.Length > 0;
        }

        /// <summary>The name a well-formed message signs, or null.</summary>
        public static string? SignedName(string? message) =>
            message != null && TryParse(message, out _, out _, out var name) ? name : null;

        /// <summary>
        /// Null when <paramref name="signature"/> over <paramref name="message"/> is from the key of
        /// <paramref name="btcAddress"/> and the message names <paramref name="owner"/> and exactly
        /// <paramref name="fullName"/> (the stored name, with its ".btc"; case aside); otherwise the reason. The signed
        /// timestamp comes back for the caller's age rule.
        /// </summary>
        public static string? Check(string message, string signature, string btcAddress, string owner, string fullName, Network network, out long timestamp)
        {
            timestamp = 0;
            if (!SignatureService.VerifySignature(message, signature))
                return "Invalid BTC Signature";
            if (!SignatureService.SignerOwnsAddress(signature, btcAddress, network))
                return "The BTC signature is not from the key of the BTC address.";
            if (!TryParse(message, out timestamp, out var signedOwner, out var signedName))
                return "Improper Message Format. Message must be <timestamp>:<VFX address>:<name>.";
            if (signedOwner != owner)
                return "The BTC signature message names a different VFX address.";
            if (!string.Equals(signedName, fullName, StringComparison.OrdinalIgnoreCase))
                return "The BTC signature message names a different name.";
            return null;
        }
    }
}
