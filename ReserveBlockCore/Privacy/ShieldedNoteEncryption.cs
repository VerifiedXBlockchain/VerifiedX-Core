using System.Security.Cryptography;
using System.Text;
using NBitcoin;
using NBitcoin.Crypto;
using ReserveBlockCore.Models.Privacy;

namespace ReserveBlockCore.Privacy
{
    /// <summary>
    /// AES-256-GCM note encryption using ECDH (secp256k1) + domain-separated key derivation.
    /// Wire v1: <c>version(1) | ephemeralPub33 | nonce12 | tag16 | ciphertext</c>.
    /// </summary>
    public static class ShieldedNoteEncryption
    {
        public const byte WireVersion1 = 1;
        public const int MinSealedLength = 1 + 33 + 12 + 16;

        private static readonly byte[] AesKeyDomain = Encoding.UTF8.GetBytes("VFX/shielded/note-aes/v1");
        private static readonly byte[] AadPrefix = Encoding.UTF8.GetBytes("VFX/shielded/note-aad/v1");

        /// <summary>Seal plaintext to a recipient <c>zfx_</c> address (decodes to encryption pubkey).</summary>
        public static byte[] SealUtf8(string plaintext, string recipientZfxAddress)
        {
            if (!ShieldedAddressCodec.TryDecodeEncryptionKey(recipientZfxAddress, out var pub33, out var err))
                throw new ArgumentException(err ?? "Invalid zfx address.", nameof(recipientZfxAddress));
            return Seal(Encoding.UTF8.GetBytes(plaintext), pub33);
        }

        /// <summary>Seal structured <see cref="ShieldedPlainNote"/> JSON (Phase 3) to a <c>zfx_</c> recipient.</summary>
        public static byte[] SealPlainNote(ShieldedPlainNote note, string recipientZfxAddress)
        {
            if (!ShieldedAddressCodec.TryDecodeEncryptionKey(recipientZfxAddress, out var pub33, out var err))
                throw new ArgumentException(err ?? "Invalid zfx address.", nameof(recipientZfxAddress));
            var plain = ShieldedPlainNoteCodec.SerializeToUtf8Bytes(note);
            return Seal(plain, pub33);
        }

        public static byte[] Seal(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> recipientEncryptionPubKey33)
        {
            if (recipientEncryptionPubKey33.Length != ShieldedAddressConstants.EncryptionKeyLength)
                throw new ArgumentException("Recipient encryption key must be 33 bytes (compressed secp256k1).", nameof(recipientEncryptionPubKey33));

            var ephemeral = new Key();
            var ephPriv = ephemeral.ToBytes();
            var ephPub = ephemeral.PubKey.ToBytes();
            var shared = ShieldedSecp256k1Ecdh.DeriveSharedSecret32(ephPriv, recipientEncryptionPubKey33);
            var aesKey = DeriveAes256Key(shared, ephPub, recipientEncryptionPubKey33);

            var nonce = new byte[12];
            RandomNumberGenerator.Fill(nonce);
            var tag = new byte[16];
            var cipher = new byte[plaintext.Length];
            var aad = BuildAad(ephPub, recipientEncryptionPubKey33);
            using (var gcm = new AesGcm(aesKey))
                gcm.Encrypt(nonce, plaintext, cipher, tag, aad);

            var outBuf = new byte[1 + 33 + 12 + 16 + cipher.Length];
            outBuf[0] = WireVersion1;
            Buffer.BlockCopy(ephPub, 0, outBuf, 1, 33);
            Buffer.BlockCopy(nonce, 0, outBuf, 34, 12);
            Buffer.BlockCopy(tag, 0, outBuf, 46, 16);
            Buffer.BlockCopy(cipher, 0, outBuf, 62, cipher.Length);
            return outBuf;
        }

        public static bool TryOpen(ReadOnlySpan<byte> sealedBlob, ReadOnlySpan<byte> recipientEncryptionPrivateKey32, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out byte[]? plaintext, out string? error)
        {
            plaintext = null;
            error = null;
            if (sealedBlob.Length < MinSealedLength)
            {
                error = "Sealed note too short.";
                return false;
            }
            if (sealedBlob[0] != WireVersion1)
            {
                error = "Unknown sealed-note version.";
                return false;
            }

            try
            {
                var ephPub = sealedBlob.Slice(1, 33);
                var nonce = sealedBlob.Slice(34, 12);
                var tag = sealedBlob.Slice(46, 16);
                var cipher = sealedBlob.Slice(62).ToArray();

                Key recipientKey;
                try
                {
                    recipientKey = new Key(recipientEncryptionPrivateKey32.ToArray());
                }
                catch (Exception ex)
                {
                    error = "Invalid recipient encryption private key: " + ex.Message;
                    return false;
                }

                var recipientPub = recipientKey.PubKey.ToBytes();
                var shared = ShieldedSecp256k1Ecdh.DeriveSharedSecret32(recipientKey.ToBytes(), ephPub);
                var aesKey = DeriveAes256Key(shared, ephPub.ToArray(), recipientPub);
                var aad = BuildAad(ephPub.ToArray(), recipientPub);

                var plain = new byte[cipher.Length];
                using (var gcm = new AesGcm(aesKey))
                    gcm.Decrypt(nonce.ToArray(), cipher, tag.ToArray(), plain, aad);

                plaintext = plain;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static bool TryOpenUtf8(ReadOnlySpan<byte> sealedBlob, ReadOnlySpan<byte> recipientEncryptionPrivateKey32, out string? text, out string? error)
        {
            if (!TryOpen(sealedBlob, recipientEncryptionPrivateKey32, out var plain, out error))
            {
                text = null;
                return false;
            }
            text = Encoding.UTF8.GetString(plain!);
            return true;
        }

        private static byte[] DeriveAes256Key(ReadOnlySpan<byte> ecdhShared32, ReadOnlySpan<byte> ephemeralPub33, ReadOnlySpan<byte> recipientPub33)
        {
            var buf = new byte[AesKeyDomain.Length + 32 + 33 + 33];
            AesKeyDomain.CopyTo(buf.AsSpan(0));
            ecdhShared32.CopyTo(buf.AsSpan(AesKeyDomain.Length));
            ephemeralPub33.CopyTo(buf.AsSpan(AesKeyDomain.Length + 32));
            recipientPub33.CopyTo(buf.AsSpan(AesKeyDomain.Length + 32 + 33));
            return Hashes.DoubleSHA256(buf).ToBytes();
        }

        private static byte[] BuildAad(ReadOnlySpan<byte> ephemeralPub33, ReadOnlySpan<byte> recipientPub33)
        {
            var aad = new byte[AadPrefix.Length + 33 + 33];
            AadPrefix.CopyTo(aad.AsSpan(0));
            ephemeralPub33.CopyTo(aad.AsSpan(AadPrefix.Length));
            recipientPub33.CopyTo(aad.AsSpan(AadPrefix.Length + 33));
            return aad;
        }
    }
}
