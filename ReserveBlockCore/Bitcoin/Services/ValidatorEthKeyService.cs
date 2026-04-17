using System.Numerics;
using System.Text;
using Nethereum.Signer;
using Nethereum.Util;
using ReserveBlockCore.Data;
using ReserveBlockCore.Utilities;

namespace ReserveBlockCore.Bitcoin.Services
{
    /// <summary>
    /// Derives a deterministic Base (Ethereum) address from the validator's existing secp256k1 private key
    /// and produces Ethereum personal_sign / ecrecover-compatible signatures for bridge attestations.
    /// </summary>
    public static class ValidatorEthKeyService
    {
        /// <summary>Derive Base address from raw 32-byte secp256k1 private key.</summary>
        public static string DeriveBaseAddress(byte[] privateKeyBytes)
        {
            var key = new EthECKey(privateKeyBytes, true);
            return key.GetPublicAddress();
        }

        /// <summary>Derive Base address from the validator VFX account stored on this node.</summary>
        public static string DeriveBaseAddressFromAccount(string validatorAddress)
        {
            var account = AccountData.GetSingleAccount(validatorAddress);
            if (account == null)
                return string.Empty;

            var privHex = account.GetKey;
            if (string.IsNullOrEmpty(privHex))
                return string.Empty;

            if (privHex.Length % 2 != 0)
                privHex = "0" + privHex;

            var bytes = HexByteUtility.HexToByte(privHex);
            return DeriveBaseAddress(bytes);
        }

        /// <summary>Derive Base address from uncompressed VFX public key hex (with optional 0x04 prefix).</summary>
        public static string DeriveBaseAddressFromVfxPublicKey(string publicKeyHex)
        {
            try
            {
                var hex = (publicKeyHex ?? "").Trim();
                if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    hex = hex[2..];
                if (!hex.StartsWith("04", StringComparison.OrdinalIgnoreCase))
                    hex = "04" + hex;
                var pubBytes = HexByteUtility.HexToByte(hex);
                if (pubBytes.Length != 65)
                    return string.Empty;
                var body = pubBytes.AsSpan(1, 64).ToArray();
                var hash = Sha3Keccack.Current.CalculateHash(body);
                var addrBytes = hash.AsSpan(12, 20).ToArray();
                return "0x" + BitConverter.ToString(addrBytes).Replace("-", "", StringComparison.Ordinal);
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>Sign a 32-byte keccak digest with Ethereum EIP-191 personal prefix for a 32-byte payload.</summary>
        public static byte[] EthSign(byte[] messageHash32, byte[] privateKeyBytes)
        {
            if (messageHash32 == null || messageHash32.Length != 32)
                throw new ArgumentException("messageHash32 must be 32 bytes", nameof(messageHash32));
            var signingHash = ToEthereumSignedMessageHash32(messageHash32);
            var ethKey = new EthECKey(privateKeyBytes, true);
            var sig = ethKey.SignAndCalculateV(signingHash);
            return SignatureTo65(sig);
        }

        public static string EthSignMessageHex(byte[] messageHash32, byte[] privateKeyBytes)
        {
            var sig = EthSign(messageHash32, privateKeyBytes);
            return "0x" + BitConverter.ToString(sig).Replace("-", "", StringComparison.Ordinal);
        }

        public static bool VerifyEthSignature(string baseAddress, byte[] messageHash32, byte[] signature65)
        {
            try
            {
                if (string.IsNullOrEmpty(baseAddress) || messageHash32.Length != 32 || signature65.Length != 65)
                    return false;
                var signingHash = ToEthereumSignedMessageHash32(messageHash32);
                var r = signature65.AsSpan(0, 32).ToArray();
                var s = signature65.AsSpan(32, 32).ToArray();
                var v = new[] { signature65[64] };
                var sig = EthECDSASignatureFactory.FromComponents(r, s, v);
                var recovered = EthECKey.RecoverFromSignature(sig, signingHash);
                return string.Equals(recovered.GetPublicAddress(), baseAddress, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Proof for heartbeat/register: EthSign(keccak256(abi.encodePacked(timestamp, blockHeight))).</summary>
        public static string CreateBaseProofSignature(long timestamp, long blockHeight, byte[] privateKeyBytes)
        {
            var hash = HashBaseProofPayload(timestamp, blockHeight);
            return EthSignMessageHex(hash, privateKeyBytes);
        }

        public static byte[] HashBaseProofPayload(long timestamp, long blockHeight)
        {
            var packed = LongToUint256BigEndian(timestamp).Concat(LongToUint256BigEndian(blockHeight)).ToArray();
            return Sha3Keccack.Current.CalculateHash(packed);
        }

        public static void TryInitializeGlobalsValidatorBaseAddress()
        {
            try
            {
                if (string.IsNullOrEmpty(Globals.ValidatorAddress))
                {
                    Globals.ValidatorBaseAddress = string.Empty;
                    return;
                }
                Globals.ValidatorBaseAddress = DeriveBaseAddressFromAccount(Globals.ValidatorAddress) ?? string.Empty;
                LogUtility.Log("Validator Base Address: " + Globals.ValidatorBaseAddress, "ValidatorEthKeyService.TryInitializeGlobalsValidatorBaseAddress");
            }
            catch
            {
                Globals.ValidatorBaseAddress = string.Empty;
            }
        }

        private static byte[] LongToUint256BigEndian(long v)
        {
            unchecked
            {
                var b = new byte[32];
                ulong uv = (ulong)v;
                for (var i = 31; i >= 0; i--)
                {
                    b[i] = (byte)(uv & 0xff);
                    uv >>= 8;
                }
                return b;
            }
        }

        private static byte[] ToEthereumSignedMessageHash32(byte[] keccak256Hash32)
        {
            var sb = new List<byte> { 0x19 };
            sb.AddRange(Encoding.UTF8.GetBytes("Ethereum Signed Message:\n32"));
            sb.AddRange(keccak256Hash32);
            return Sha3Keccack.Current.CalculateHash(sb.ToArray());
        }

        private static byte[] SignatureTo65(EthECDSASignature sig)
        {
            var r = Pad32(sig.R);
            var s = Pad32(sig.S);
            var vb = sig.V;
            var v = vb != null && vb.Length > 0 ? vb[0] : (byte)27;
            var result = new byte[65];
            Array.Copy(r, 0, result, 0, 32);
            Array.Copy(s, 0, result, 32, 32);
            result[64] = v;
            return result;
        }

        private static byte[] Pad32(byte[] value)
        {
            var out32 = new byte[32];
            if (value == null || value.Length == 0)
                return out32;
            var len = Math.Min(32, value.Length);
            Array.Copy(value, value.Length - len, out32, 32 - len, len);
            return out32;
        }
    }
}
