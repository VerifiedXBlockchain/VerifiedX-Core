using ReserveBlockCore.Bitcoin.FROST.Models;
using ReserveBlockCore.Bitcoin.Services;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace VerfiedXCore.Tests
{
    /// <summary>
    /// Unit tests for FROST peer key backup encryption/decryption.
    /// Tests AES-256-GCM round-trip, tamper detection, and edge cases.
    /// Note: These tests verify the cryptographic primitives in isolation.
    /// Full integration tests (broadcast → wipe → recovery) require a running validator network.
    /// </summary>
    public class FrostKeyBackupTests
    {
        [Fact]
        public void DeriveNonce_DeterministicForSameInputs()
        {
            // Verify that the same encryption key + contract UID always produces the same nonce
            var key = new byte[32];
            RandomNumberGenerator.Fill(key);
            var scUID = "test-contract-uid-12345";

            var nonce1 = DeriveNonceHelper(key, scUID);
            var nonce2 = DeriveNonceHelper(key, scUID);

            Assert.Equal(nonce1, nonce2);
        }

        [Fact]
        public void DeriveNonce_DifferentForDifferentContracts()
        {
            // Verify different contract UIDs produce different nonces
            var key = new byte[32];
            RandomNumberGenerator.Fill(key);

            var nonce1 = DeriveNonceHelper(key, "contract-A");
            var nonce2 = DeriveNonceHelper(key, "contract-B");

            Assert.NotEqual(nonce1, nonce2);
        }

        [Fact]
        public void AesGcm_RoundTrip_ProducesOriginalPlaintext()
        {
            // Verify AES-256-GCM encrypt → decrypt round-trip
            var key = new byte[32];
            RandomNumberGenerator.Fill(key);
            var nonce = new byte[12];
            RandomNumberGenerator.Fill(nonce);

            var plaintext = Encoding.UTF8.GetBytes("{\"KeyPackage\":\"test-secret\",\"GroupPublicKey\":\"abcdef\"}");
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[16];

#pragma warning disable SYSLIB0053
            using (var aesGcm = new AesGcm(key))
#pragma warning restore SYSLIB0053
            {
                aesGcm.Encrypt(nonce, plaintext, ciphertext, tag);
            }

            // Decrypt
            var decrypted = new byte[ciphertext.Length];
#pragma warning disable SYSLIB0053
            using (var aesGcm = new AesGcm(key))
#pragma warning restore SYSLIB0053
            {
                aesGcm.Decrypt(nonce, ciphertext, tag, decrypted);
            }

            Assert.Equal(plaintext, decrypted);
            Assert.Equal("{\"KeyPackage\":\"test-secret\",\"GroupPublicKey\":\"abcdef\"}", Encoding.UTF8.GetString(decrypted));
        }

        [Fact]
        public void AesGcm_TamperedCiphertext_ThrowsCryptographicException()
        {
            // Verify that GCM detects tampering
            var key = new byte[32];
            RandomNumberGenerator.Fill(key);
            var nonce = new byte[12];
            RandomNumberGenerator.Fill(nonce);

            var plaintext = Encoding.UTF8.GetBytes("sensitive data");
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[16];

#pragma warning disable SYSLIB0053
            using (var aesGcm = new AesGcm(key))
#pragma warning restore SYSLIB0053
            {
                aesGcm.Encrypt(nonce, plaintext, ciphertext, tag);
            }

            // Tamper with ciphertext
            ciphertext[0] ^= 0xFF;

            // Decryption should fail
            var decrypted = new byte[ciphertext.Length];
            Assert.Throws<CryptographicException>(() =>
            {
#pragma warning disable SYSLIB0053
                using (var aesGcm = new AesGcm(key))
#pragma warning restore SYSLIB0053
                {
                    aesGcm.Decrypt(nonce, ciphertext, tag, decrypted);
                }
            });
        }

        [Fact]
        public void AesGcm_WrongKey_ThrowsCryptographicException()
        {
            // Verify that wrong key produces auth failure
            var key1 = new byte[32];
            var key2 = new byte[32];
            RandomNumberGenerator.Fill(key1);
            RandomNumberGenerator.Fill(key2);
            var nonce = new byte[12];
            RandomNumberGenerator.Fill(nonce);

            var plaintext = Encoding.UTF8.GetBytes("key material");
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[16];

#pragma warning disable SYSLIB0053
            using (var aesGcm = new AesGcm(key1))
#pragma warning restore SYSLIB0053
            {
                aesGcm.Encrypt(nonce, plaintext, ciphertext, tag);
            }

            // Decrypt with wrong key
            var decrypted = new byte[ciphertext.Length];
            Assert.Throws<CryptographicException>(() =>
            {
#pragma warning disable SYSLIB0053
                using (var aesGcm = new AesGcm(key2))
#pragma warning restore SYSLIB0053
                {
                    aesGcm.Decrypt(nonce, ciphertext, tag, decrypted);
                }
            });
        }

        [Fact]
        public void AesGcm_TamperedTag_ThrowsCryptographicException()
        {
            // Verify that tampered GCM tag causes failure
            var key = new byte[32];
            RandomNumberGenerator.Fill(key);
            var nonce = new byte[12];
            RandomNumberGenerator.Fill(nonce);

            var plaintext = Encoding.UTF8.GetBytes("important secret");
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[16];

#pragma warning disable SYSLIB0053
            using (var aesGcm = new AesGcm(key))
#pragma warning restore SYSLIB0053
            {
                aesGcm.Encrypt(nonce, plaintext, ciphertext, tag);
            }

            // Tamper with tag
            tag[0] ^= 0xFF;

            var decrypted = new byte[ciphertext.Length];
            Assert.Throws<CryptographicException>(() =>
            {
#pragma warning disable SYSLIB0053
                using (var aesGcm = new AesGcm(key))
#pragma warning restore SYSLIB0053
                {
                    aesGcm.Decrypt(nonce, ciphertext, tag, decrypted);
                }
            });
        }

        [Fact]
        public void BlobFormat_TagPlusCiphertext_RoundTrips()
        {
            // Verify the tag+ciphertext combination format used for storage
            var key = new byte[32];
            RandomNumberGenerator.Fill(key);
            var nonce = new byte[12];
            RandomNumberGenerator.Fill(nonce);

            var plaintext = Encoding.UTF8.GetBytes("{\"KeyPackage\":\"pkg\",\"PubkeyPackage\":\"pubpkg\",\"GroupPublicKey\":\"gpk\",\"ParticipantOrderJson\":\"[]\"}");
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[16];

#pragma warning disable SYSLIB0053
            using (var aesGcm = new AesGcm(key))
#pragma warning restore SYSLIB0053
            {
                aesGcm.Encrypt(nonce, plaintext, ciphertext, tag);
            }

            // Combine as tag + ciphertext (storage format)
            var combined = new byte[tag.Length + ciphertext.Length];
            Buffer.BlockCopy(tag, 0, combined, 0, tag.Length);
            Buffer.BlockCopy(ciphertext, 0, combined, tag.Length, ciphertext.Length);
            var base64 = Convert.ToBase64String(combined);

            // Reconstruct from base64
            var decoded = Convert.FromBase64String(base64);
            var extractedTag = new byte[16];
            var extractedCiphertext = new byte[decoded.Length - 16];
            Buffer.BlockCopy(decoded, 0, extractedTag, 0, 16);
            Buffer.BlockCopy(decoded, 16, extractedCiphertext, 0, extractedCiphertext.Length);

            // Decrypt
            var decrypted = new byte[extractedCiphertext.Length];
#pragma warning disable SYSLIB0053
            using (var aesGcm = new AesGcm(key))
#pragma warning restore SYSLIB0053
            {
                aesGcm.Decrypt(nonce, extractedCiphertext, extractedTag, decrypted);
            }

            Assert.Equal(plaintext, decrypted);
        }

        [Fact]
        public void PlaintextHash_MatchesExpected()
        {
            // Verify SHA-256 hash computation matches
            var data = "{\"KeyPackage\":\"secret\",\"PubkeyPackage\":\"pub\",\"GroupPublicKey\":\"gpk\",\"ParticipantOrderJson\":\"[]\"}";
            var bytes = Encoding.UTF8.GetBytes(data);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLower();

            Assert.Equal(64, hash.Length); // SHA-256 = 32 bytes = 64 hex chars
            Assert.Matches("^[0-9a-f]+$", hash);

            // Same data produces same hash (deterministic)
            var hash2 = Convert.ToHexString(SHA256.HashData(bytes)).ToLower();
            Assert.Equal(hash, hash2);
        }

        [Fact]
        public void HmacSha256_KeyDerivation_Deterministic()
        {
            // Verify HMAC-SHA256 key derivation is deterministic
            var privateKeyBytes = new byte[32];
            RandomNumberGenerator.Fill(privateKeyBytes);

            byte[] key1, key2;
            using (var hmac = new HMACSHA256(privateKeyBytes))
            {
                key1 = hmac.ComputeHash(Encoding.UTF8.GetBytes("frost-key-backup-v1"));
            }
            using (var hmac = new HMACSHA256(privateKeyBytes))
            {
                key2 = hmac.ComputeHash(Encoding.UTF8.GetBytes("frost-key-backup-v1"));
            }

            Assert.Equal(key1, key2);
            Assert.Equal(32, key1.Length); // HMAC-SHA256 output = 32 bytes = AES-256 key
        }

        [Fact]
        public void HmacSha256_DifferentKeys_ProduceDifferentOutput()
        {
            // Different private keys produce different encryption keys
            var pk1 = new byte[32];
            var pk2 = new byte[32];
            RandomNumberGenerator.Fill(pk1);
            RandomNumberGenerator.Fill(pk2);

            byte[] key1, key2;
            using (var hmac = new HMACSHA256(pk1))
                key1 = hmac.ComputeHash(Encoding.UTF8.GetBytes("frost-key-backup-v1"));
            using (var hmac = new HMACSHA256(pk2))
                key2 = hmac.ComputeHash(Encoding.UTF8.GetBytes("frost-key-backup-v1"));

            Assert.NotEqual(key1, key2);
        }

        [Fact]
        public void FrostPeerKeyBackup_ModelProperties()
        {
            // Verify model properties have correct defaults
            var backup = new FrostPeerKeyBackup();
            Assert.Equal(string.Empty, backup.OwnerAddress);
            Assert.Equal(string.Empty, backup.SmartContractUID);
            Assert.Equal(string.Empty, backup.EncryptedBlob);
            Assert.Equal(string.Empty, backup.PlaintextHash);
            Assert.Equal(1, backup.Version);
            Assert.Equal(0, backup.StoredTimestamp);
        }

        [Fact]
        public void FrostKeyBackupPlaintext_ModelProperties()
        {
            // Verify plaintext DTO has correct defaults
            var plaintext = new FrostKeyBackupService.FrostKeyBackupPlaintext();
            Assert.Equal(string.Empty, plaintext.KeyPackage);
            Assert.Equal(string.Empty, plaintext.PubkeyPackage);
            Assert.Equal(string.Empty, plaintext.GroupPublicKey);
            Assert.Equal(string.Empty, plaintext.ParticipantOrderJson);
        }

        /// <summary>
        /// Helper to derive nonce (mirrors FrostKeyBackupService.DeriveNonce logic)
        /// </summary>
        private static byte[] DeriveNonceHelper(byte[] encryptionKey, string smartContractUID)
        {
            using (var hmac = new HMACSHA256(encryptionKey))
            {
                var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(smartContractUID));
                var nonce = new byte[12];
                Buffer.BlockCopy(hash, 0, nonce, 0, 12);
                return nonce;
            }
        }
    }
}
