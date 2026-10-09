using System.Diagnostics.CodeAnalysis;
using NBitcoin.Crypto;
using NBitcoin.DataEncoders;

namespace VerifiedXCore.Privacy
{
    /// <summary>
    /// Encode/decode <c>zfx_</c> shielded addresses (Base58Check payload: version bytes + 33-byte encryption key, and since
    /// stage 3 the 32-byte owner key).
    /// <list type="bullet">
    /// <item>v1 (<see cref="ShieldedAddressConstants.VersionBytes"/>): encryption key only. Notes sent to it cannot name an
    /// owner, so from the proof-rules epoch it cannot receive.</item>
    /// <item>v2 (<see cref="ShieldedAddressConstants.VersionBytesV2"/>): encryption key + <c>owner_pk</c>
    /// (<see cref="PoseidonV1.OwnerPk"/> of the nullifier key). Every note sent to it carries the owner key the circuits
    /// bind, so only the holder of the nullifier key can spend it (fund-loss re-audit, Oct 2026).</item>
    /// </list>
    /// Both versions decode; the same keys give both strings, so a wallet row created under v1 keeps working and shows
    /// its v2 address (<see cref="ShieldedWalletService.CurrentAddress"/>).
    /// </summary>
    public static class ShieldedAddressCodec
    {
        /// <summary>The v1 encoding (encryption key only). Kept for the pre-epoch regime and for tests.</summary>
        public static string EncodeEncryptionKey(ReadOnlySpan<byte> encryptionKey33)
        {
            if (encryptionKey33.Length != ShieldedAddressConstants.EncryptionKeyLength)
                throw new ArgumentException($"Encryption key must be {ShieldedAddressConstants.EncryptionKeyLength} bytes.", nameof(encryptionKey33));
            var payload = new byte[ShieldedAddressConstants.PayloadLength];
            ShieldedAddressConstants.VersionBytes.CopyTo(payload);
            encryptionKey33.CopyTo(payload.AsSpan(ShieldedAddressConstants.VersionByteLength));
            return Finish(payload);
        }

        /// <summary>The v2 encoding: encryption key + owner key.</summary>
        public static string Encode(ReadOnlySpan<byte> encryptionKey33, ReadOnlySpan<byte> ownerPk32)
        {
            if (encryptionKey33.Length != ShieldedAddressConstants.EncryptionKeyLength)
                throw new ArgumentException($"Encryption key must be {ShieldedAddressConstants.EncryptionKeyLength} bytes.", nameof(encryptionKey33));
            if (ownerPk32.Length != ShieldedAddressConstants.OwnerKeyLength || !PrivacyField.IsCanonicalLe(ownerPk32))
                throw new ArgumentException($"Owner key must be a canonical {ShieldedAddressConstants.OwnerKeyLength}-byte field element.", nameof(ownerPk32));
            var payload = new byte[ShieldedAddressConstants.PayloadLengthV2];
            ShieldedAddressConstants.VersionBytesV2.CopyTo(payload);
            encryptionKey33.CopyTo(payload.AsSpan(ShieldedAddressConstants.VersionByteLength));
            ownerPk32.CopyTo(payload.AsSpan(ShieldedAddressConstants.VersionByteLength + ShieldedAddressConstants.EncryptionKeyLength));
            return Finish(payload);
        }

        private static string Finish(byte[] payload)
        {
            var checksum = Hashes.DoubleSHA256(payload).ToBytes();
            var withChecksum = new byte[payload.Length + 4];
            Buffer.BlockCopy(payload, 0, withChecksum, 0, payload.Length);
            Buffer.BlockCopy(checksum, 0, withChecksum, payload.Length, 4);
            return ShieldedAddressConstants.Prefix + Encoders.Base58.EncodeData(withChecksum);
        }

        /// <summary>Decodes either version. <paramref name="ownerPk32"/> is null for a v1 address.</summary>
        public static bool TryDecode(string? address, [NotNullWhen(true)] out byte[]? encryptionKey33, out byte[]? ownerPk32, [NotNullWhen(false)] out string? error)
        {
            encryptionKey33 = null;
            ownerPk32 = null;
            error = null;
            if (string.IsNullOrWhiteSpace(address))
            {
                error = "Shielded address is empty.";
                return false;
            }
            if (!address.StartsWith(ShieldedAddressConstants.Prefix, StringComparison.Ordinal))
            {
                error = "Shielded address must start with zfx_.";
                return false;
            }
            var body = address.Substring(ShieldedAddressConstants.Prefix.Length);
            if (string.IsNullOrEmpty(body))
            {
                error = "Shielded address body is missing.";
                return false;
            }
            byte[] withChecksum;
            try
            {
                withChecksum = Encoders.Base58.DecodeData(body);
            }
            catch (Exception ex)
            {
                error = $"Invalid Base58Check data: {ex.Message}";
                return false;
            }
            int payloadLength;
            if (withChecksum.Length == ShieldedAddressConstants.PayloadLength + 4) payloadLength = ShieldedAddressConstants.PayloadLength;
            else if (withChecksum.Length == ShieldedAddressConstants.PayloadLengthV2 + 4) payloadLength = ShieldedAddressConstants.PayloadLengthV2;
            else
            {
                error = "Shielded address has invalid length.";
                return false;
            }
            var payload = withChecksum.AsSpan(0, payloadLength).ToArray();
            var check = withChecksum.AsSpan(payloadLength, 4);
            var expected = Hashes.DoubleSHA256(payload).ToBytes().AsSpan(0, 4);
            if (!check.SequenceEqual(expected))
            {
                error = "Shielded address checksum invalid.";
                return false;
            }
            var version = payload.AsSpan(0, ShieldedAddressConstants.VersionByteLength);
            var isV1 = ShieldedAddressConstants.VersionBytes.SequenceEqual(version);
            var isV2 = ShieldedAddressConstants.VersionBytesV2.SequenceEqual(version);
            if ((isV1 && payloadLength != ShieldedAddressConstants.PayloadLength) || (isV2 && payloadLength != ShieldedAddressConstants.PayloadLengthV2) || (!isV1 && !isV2))
            {
                error = "Unknown shielded address version.";
                return false;
            }
            encryptionKey33 = new byte[ShieldedAddressConstants.EncryptionKeyLength];
            Buffer.BlockCopy(payload, ShieldedAddressConstants.VersionByteLength, encryptionKey33, 0, ShieldedAddressConstants.EncryptionKeyLength);
            if (isV2)
            {
                ownerPk32 = new byte[ShieldedAddressConstants.OwnerKeyLength];
                Buffer.BlockCopy(payload, ShieldedAddressConstants.VersionByteLength + ShieldedAddressConstants.EncryptionKeyLength, ownerPk32, 0, ShieldedAddressConstants.OwnerKeyLength);
                if (!PrivacyField.IsCanonicalLe(ownerPk32))
                {
                    encryptionKey33 = null;
                    ownerPk32 = null;
                    error = "Shielded address owner key is not a canonical field element.";
                    return false;
                }
            }
            return true;
        }

        /// <summary>The encryption key of either version (what note sealing and scanning need).</summary>
        public static bool TryDecodeEncryptionKey(string? address, [NotNullWhen(true)] out byte[]? encryptionKey33, [NotNullWhen(false)] out string? error) =>
            TryDecode(address, out encryptionKey33, out _, out error);

        /// <summary>The owner key of a v2 address; false (with the reason) for a v1 address, which cannot receive in the proof-rules epoch.</summary>
        public static bool TryDecodeOwnerKey(string? address, [NotNullWhen(true)] out byte[]? ownerPk32, [NotNullWhen(false)] out string? error)
        {
            if (!TryDecode(address, out _, out ownerPk32, out error))
                return false;
            if (ownerPk32 == null)
            {
                error = "This zfx_ address carries no owner key (a pre-stage-3 address); ask the recipient for their current zfx_ address.";
                return false;
            }
            return true;
        }

        /// <summary>Whether the address carries an owner key (v2).</summary>
        public static bool HasOwnerKey(string? address) => TryDecode(address, out _, out var owner, out _) && owner != null;

        public static bool IsWellFormed(string? address) =>
            TryDecodeEncryptionKey(address, out _, out _);
    }
}
