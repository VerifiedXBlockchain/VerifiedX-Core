extern alias BouncyCryptography;
using System.Security.Cryptography;
using BouncyCryptography::Org.BouncyCastle.Crypto.Engines;
using BouncyCryptography::Org.BouncyCastle.Crypto.Modes;
using BouncyCryptography::Org.BouncyCastle.Crypto.Parameters;

namespace ReserveBlockCore.Privacy
{
    /// <summary>
    /// AES-256-GCM encrypt/decrypt. Uses <see cref="AesGcm"/> when the OS/runtime supports it;
    /// otherwise BouncyCastle (e.g. macOS ARM on .NET 6 where <c>AesGcm.IsSupported</c> is false).
    /// </summary>
    internal static class CrossPlatformAesGcm
    {
        private const int TagSizeBits = 128;

        public static void Encrypt(byte[] key, byte[] nonce, ReadOnlySpan<byte> plaintext, byte[] ciphertext, byte[] tag, byte[]? associatedData)
        {
            if (AesGcm.IsSupported)
            {
                using var aes = new AesGcm(key);
                var ad = associatedData != null ? new ReadOnlySpan<byte>(associatedData) : ReadOnlySpan<byte>.Empty;
                aes.Encrypt(nonce, plaintext, ciphertext, tag, ad);
                return;
            }

            var adBytes = associatedData ?? Array.Empty<byte>();
            var plain = plaintext.ToArray();
            var cipher = new GcmBlockCipher(new AesEngine());
            cipher.Init(true, new AeadParameters(new KeyParameter(key), TagSizeBits, nonce, adBytes));
            var combined = new byte[cipher.GetOutputSize(plain.Length)];
            var len = cipher.ProcessBytes(plain, 0, plain.Length, combined, 0);
            len += cipher.DoFinal(combined, len);
            if (len != plain.Length + tag.Length)
                throw new CryptographicException("Unexpected GCM encrypt output length.");
            Buffer.BlockCopy(combined, 0, ciphertext, 0, plain.Length);
            Buffer.BlockCopy(combined, plain.Length, tag, 0, tag.Length);
        }

        public static void Decrypt(byte[] key, byte[] nonce, byte[] ciphertext, byte[] tag, byte[] plaintext, byte[]? associatedData)
        {
            if (AesGcm.IsSupported)
            {
                using var aes = new AesGcm(key);
                var ad = associatedData != null ? new ReadOnlySpan<byte>(associatedData) : ReadOnlySpan<byte>.Empty;
                aes.Decrypt(nonce, ciphertext, tag, plaintext, ad);
                return;
            }

            var adBytes = associatedData ?? Array.Empty<byte>();
            var cipher = new GcmBlockCipher(new AesEngine());
            cipher.Init(false, new AeadParameters(new KeyParameter(key), TagSizeBits, nonce, adBytes));
            var ctPlusTag = new byte[ciphertext.Length + tag.Length];
            Buffer.BlockCopy(ciphertext, 0, ctPlusTag, 0, ciphertext.Length);
            Buffer.BlockCopy(tag, 0, ctPlusTag, ciphertext.Length, tag.Length);
            var len = cipher.ProcessBytes(ctPlusTag, 0, ctPlusTag.Length, plaintext, 0);
            len += cipher.DoFinal(plaintext, len);
            if (len != plaintext.Length)
                throw new CryptographicException("GCM decrypt failed or length mismatch.");
        }
    }
}
