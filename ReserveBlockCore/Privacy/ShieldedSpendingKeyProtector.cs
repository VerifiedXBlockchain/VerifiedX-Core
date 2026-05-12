using System.Security.Cryptography;

namespace ReserveBlockCore.Privacy
{
    /// <summary>
    /// Password-wraps sensitive shielded key material (AES-256-GCM + PBKDF2-SHA256) for local persistence.
    /// Format v1: <c>0x01 | salt16 | nonce12 | tag16 | ciphertext</c>.
    /// </summary>
    public static class ShieldedSpendingKeyProtector
    {
        public const byte FormatV1 = 1;
        private const int SaltSize = 16;
        private const int Pbkdf2Iterations = 120_000;

        public static byte[] Protect(ReadOnlySpan<byte> plaintext32OrMore, string password)
        {
            if (password.Length < 8)
                throw new ArgumentException("Password must be at least 8 characters.", nameof(password));
            if (plaintext32OrMore.Length < 32)
                throw new ArgumentException("Key material must be at least 32 bytes.", nameof(plaintext32OrMore));

            var salt = new byte[SaltSize];
            RandomNumberGenerator.Fill(salt);
            var key = Pbkdf2Key(password, salt);
            var nonce = new byte[12];
            RandomNumberGenerator.Fill(nonce);
            var tag = new byte[16];
            var cipher = new byte[plaintext32OrMore.Length];
            CrossPlatformAesGcm.Encrypt(key, nonce, plaintext32OrMore, cipher, tag, associatedData: null);

            var outBuf = new byte[1 + SaltSize + 12 + 16 + cipher.Length];
            outBuf[0] = FormatV1;
            Buffer.BlockCopy(salt, 0, outBuf, 1, SaltSize);
            Buffer.BlockCopy(nonce, 0, outBuf, 1 + SaltSize, 12);
            Buffer.BlockCopy(tag, 0, outBuf, 1 + SaltSize + 12, 16);
            Buffer.BlockCopy(cipher, 0, outBuf, 1 + SaltSize + 12 + 16, cipher.Length);
            return outBuf;
        }

        public static bool TryUnprotect(ReadOnlySpan<byte> blob, string password, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out byte[]? plaintext, out string? error)
        {
            plaintext = null;
            error = null;
            if (blob.Length < 1 + SaltSize + 12 + 16 + 32)
            {
                error = "Wrapped key blob too short.";
                return false;
            }
            if (blob[0] != FormatV1)
            {
                error = "Unknown wrapped-key format.";
                return false;
            }

            try
            {
                var salt = blob.Slice(1, SaltSize).ToArray();
                var nonce = blob.Slice(1 + SaltSize, 12).ToArray();
                var tag = blob.Slice(1 + SaltSize + 12, 16).ToArray();
                var cipher = blob.Slice(1 + SaltSize + 12 + 16).ToArray();
                var key = Pbkdf2Key(password, salt);
                var plain = new byte[cipher.Length];
                CrossPlatformAesGcm.Decrypt(key, nonce, cipher, tag, plain, associatedData: null);
                plaintext = plain;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static byte[] Pbkdf2Key(string password, byte[] salt)
        {
            using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256);
            return pbkdf2.GetBytes(32);
        }
    }
}
