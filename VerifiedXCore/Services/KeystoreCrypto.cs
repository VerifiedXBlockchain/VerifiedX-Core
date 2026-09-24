using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace VerifiedXCore.Services
{
    /// <summary>
    /// VX-13 / VX-14: password-based sealing for private keys at rest.
    ///
    /// Format "ks1:" + base64(salt[16] | nonce[12] | tag[16] | ciphertext): AES-256-GCM (authenticated) under a
    /// key derived with PBKDF2-HMAC-SHA256 (600,000 iterations, per-record random salt). The older wallet
    /// scheme used the raw ASCII password zero-padded to 32 bytes as an AES-CBC key — no salt, no work factor —
    /// so one stolen record allowed fast offline password guessing.
    ///
    /// Derived keys are cached per (salt, password) for the life of the process so repeated signing does not
    /// repeat the KDF; the cache is keyed by a hash of both, so it is useless without the password.
    /// </summary>
    public static class KeystoreCrypto
    {
        public const string V1Prefix = "ks1:";
        public const int Iterations = 600_000;
        private const int SaltSize = 16, NonceSize = 12, TagSize = 16, KeySize = 32;

        private static readonly ConcurrentDictionary<string, byte[]> _derived = new();

        public static bool IsSealed(string? value) => value != null && value.StartsWith(V1Prefix, StringComparison.Ordinal);

        private static byte[] DeriveKey(string password, byte[] salt)
        {
            var id = Convert.ToBase64String(SHA256.HashData(salt.Concat(Encoding.UTF8.GetBytes(password)).ToArray()));
            return _derived.GetOrAdd(id, _ => Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, KeySize));
        }

        public static string Seal(string plaintext, string password)
        {
            if (string.IsNullOrEmpty(password)) throw new ArgumentException("A password is required to seal a key.");
            var salt = RandomNumberGenerator.GetBytes(SaltSize);
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var pt = Encoding.UTF8.GetBytes(plaintext);
            var ct = new byte[pt.Length];
            var tag = new byte[TagSize];
            using (var gcm = new AesGcm(DeriveKey(password, salt)))
                gcm.Encrypt(nonce, pt, ct, tag);
            return V1Prefix + Convert.ToBase64String(salt.Concat(nonce).Concat(tag).Concat(ct).ToArray());
        }

        /// <summary>False for a wrong password, a tampered record, or a value that is not sealed.</summary>
        public static bool TryOpen(string? sealedValue, string password, out string plaintext)
        {
            plaintext = "";
            if (!IsSealed(sealedValue) || string.IsNullOrEmpty(password)) return false;
            try
            {
                var blob = Convert.FromBase64String(sealedValue!.Substring(V1Prefix.Length));
                if (blob.Length < SaltSize + NonceSize + TagSize) return false;
                var salt = blob.AsSpan(0, SaltSize).ToArray();
                var nonce = blob.AsSpan(SaltSize, NonceSize).ToArray();
                var tag = blob.AsSpan(SaltSize + NonceSize, TagSize).ToArray();
                var ct = blob.AsSpan(SaltSize + NonceSize + TagSize).ToArray();
                var pt = new byte[ct.Length];
                using (var gcm = new AesGcm(DeriveKey(password, salt)))
                    gcm.Decrypt(nonce, ct, tag, pt);
                plaintext = Encoding.UTF8.GetString(pt);
                return true;
            }
            catch { return false; }
        }
    }
}
