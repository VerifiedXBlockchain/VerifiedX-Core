using System.Text;

namespace VerifiedXCore.Services
{
    /// <summary>
    /// VX-14: the password layer of the VFX keystore (Keystore.Key) and of reserve accounts
    /// (ReserveAccount.EncryptedDecryptKey).
    ///
    /// Both store the private key encrypted under a random 32-byte data key, and the data key encrypted under the
    /// user's password. Only the password layer was weak:
    ///   v0 (legacy): AES-CBC keyed directly by the ASCII password left-padded with zero bytes to 32 bytes — no salt,
    ///                no work factor, unauthenticated; a password longer than 32 bytes could not be used at all.
    ///   v1:          KeystoreCrypto "ks1:" (AES-256-GCM under PBKDF2-HMAC-SHA256, 600,000 iterations, random salt).
    ///
    /// New wraps are always v1. v0 is still read so existing wallets keep working; callers re-wrap a v0 record as v1
    /// the first time the correct password opens it (TryUnwrap reports wasLegacy).
    /// </summary>
    public static class PasswordKeyWrap
    {
        public static string Wrap(string dataKeyB64, string password) => KeystoreCrypto.Seal(dataKeyB64, password);

        public static bool IsLegacy(string? wrapped) => !string.IsNullOrEmpty(wrapped) && !KeystoreCrypto.IsSealed(wrapped);

        /// <summary>
        /// Opens a wrapped data key. False for a wrong password or a tampered or malformed record.
        /// A v0 record opened with a wrong password can occasionally pass CBC padding; the result is then rejected
        /// because it is not the base64 of a 32-byte key.
        /// </summary>
        public static bool TryUnwrap(string? wrapped, string? password, out string dataKeyB64, out bool wasLegacy)
        {
            dataKeyB64 = "";
            wasLegacy = false;
            if (string.IsNullOrEmpty(wrapped) || password == null)
                return false;

            // v1 never accepts an empty password (KeystoreCrypto refuses to seal or open with one). v0 did: the old
            // reserve create path accepted "", so such a legacy record must still open with "". It cannot be
            // re-wrapped (there is no password to strengthen) and stays v0; callers skip the re-wrap for "".
            if (KeystoreCrypto.IsSealed(wrapped))
                return KeystoreCrypto.TryOpen(wrapped, password, out dataKeyB64) && IsDataKey(dataKeyB64);

            try
            {
                var pw = Encoding.ASCII.GetBytes(password);
                if (pw.Length > 32)
                    return false; // v0 could never have wrapped with such a password
                var legacyKey = new byte[32 - pw.Length].Concat(pw).ToArray();
                var value = WalletEncryptionService.DecryptKey(Convert.FromBase64String(wrapped), legacyKey);
                if (!IsDataKey(value))
                    return false;
                dataKeyB64 = value;
                wasLegacy = true;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsDataKey(string? b64)
        {
            if (string.IsNullOrEmpty(b64)) return false;
            try { return Convert.FromBase64String(b64).Length == 32; }
            catch { return false; }
        }
    }
}
