using System.Diagnostics.CodeAnalysis;
using NBitcoin.Crypto;
using NBitcoin.DataEncoders;

namespace ReserveBlockCore.Privacy
{
    /// <summary>
    /// Encode/decode <c>zfx_</c> shielded addresses (Base58Check payload: 2-byte version + 33-byte encryption key).
    /// </summary>
    public static class ShieldedAddressCodec
    {
        public static string EncodeEncryptionKey(ReadOnlySpan<byte> encryptionKey33)
        {
            if (encryptionKey33.Length != ShieldedAddressConstants.EncryptionKeyLength)
                throw new ArgumentException($"Encryption key must be {ShieldedAddressConstants.EncryptionKeyLength} bytes.", nameof(encryptionKey33));

            var payload = new byte[ShieldedAddressConstants.PayloadLength];
            ShieldedAddressConstants.VersionBytes.CopyTo(payload);
            encryptionKey33.CopyTo(payload.AsSpan(ShieldedAddressConstants.VersionByteLength));

            var checksum = Hashes.DoubleSHA256(payload).ToBytes();
            var withChecksum = new byte[payload.Length + 4];
            Buffer.BlockCopy(payload, 0, withChecksum, 0, payload.Length);
            Buffer.BlockCopy(checksum, 0, withChecksum, payload.Length, 4);

            return ShieldedAddressConstants.Prefix + Encoders.Base58.EncodeData(withChecksum);
        }

        public static bool TryDecodeEncryptionKey(string? address, [NotNullWhen(true)] out byte[]? encryptionKey33, [NotNullWhen(false)] out string? error)
        {
            encryptionKey33 = null;
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

            if (withChecksum.Length != ShieldedAddressConstants.PayloadLength + 4)
            {
                error = "Shielded address has invalid length.";
                return false;
            }

            var payload = withChecksum.AsSpan(0, ShieldedAddressConstants.PayloadLength).ToArray();
            var check = withChecksum.AsSpan(ShieldedAddressConstants.PayloadLength, 4);
            var expected = Hashes.DoubleSHA256(payload).ToBytes().AsSpan(0, 4);
            if (!check.SequenceEqual(expected))
            {
                error = "Shielded address checksum invalid.";
                return false;
            }

            if (!ShieldedAddressConstants.VersionBytes.SequenceEqual(payload.AsSpan(0, ShieldedAddressConstants.VersionByteLength)))
            {
                error = "Unknown shielded address version.";
                return false;
            }

            encryptionKey33 = new byte[ShieldedAddressConstants.EncryptionKeyLength];
            Buffer.BlockCopy(payload, ShieldedAddressConstants.VersionByteLength, encryptionKey33, 0, ShieldedAddressConstants.EncryptionKeyLength);
            return true;
        }

        public static bool IsWellFormed(string? address) =>
            TryDecodeEncryptionKey(address, out _, out _);
    }
}
