using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace VerifiedXCore.Bitcoin.FROST
{
    /// <summary>
    /// NEW-19: end-to-end confidentiality for FROST DKG round-2 shares.
    ///
    /// A round-2 package carries the dealer's secret polynomial evaluated at the recipient ("signing_share", plaintext in
    /// the native library's output). The coordinator - any wallet that starts a vBTC V2 contract DKG, or the node acting
    /// for a web wallet - collected every dealer's packages for every recipient over plain HTTP and redistributed them.
    /// Holding f_j(k) for all k != j, it could interpolate every dealer polynomial whenever the threshold is below the
    /// participant count, and so reconstruct the vault's group secret key (as could anyone observing the traffic).
    ///
    /// Each package is now sealed by its dealer to the recipient's registered validator public key (ECIES over secp256k1:
    /// ephemeral ECDH, SHA-256 key derivation, AES-256-GCM, with the session id and recipient identifier as associated
    /// data). The coordinator relays ciphertext only; the recipient opens its own package and refuses plaintext. Share
    /// authenticity is unchanged: FROST part3 verifies every package against its dealer's round-1 commitment.
    /// </summary>
    public static class FrostShareCrypto
    {
        public const string Prefix = "enc1:";
        private static readonly byte[] Domain = Encoding.UTF8.GetBytes("VFX-FROST-DKG-ROUND2-SHARE-v1");

        private static byte[] Aad(string sessionId, string recipientIdentifier) => Encoding.UTF8.GetBytes($"{sessionId}|{recipientIdentifier}");

        private static byte[] DeriveKey(NBitcoin.PubKey shared, NBitcoin.PubKey ephemeral, NBitcoin.PubKey recipient)
        {
            var material = Domain
                .Concat(shared.Compress().ToBytes())
                .Concat(ephemeral.Compress().ToBytes())
                .Concat(recipient.Compress().ToBytes())
                .ToArray();
            return SHA256.HashData(material);
        }

        /// <summary>Seals one round-2 package for the holder of <paramref name="recipientPublicKeyHex"/>.</summary>
        public static string Seal(string packageJson, string recipientPublicKeyHex, string sessionId, string recipientIdentifier)
        {
            var recipient = new NBitcoin.PubKey(Convert.FromHexString(recipientPublicKeyHex));
            using var ephemeralKey = new NBitcoin.Key();
            var ephemeral = ephemeralKey.PubKey.Compress();
            var key = DeriveKey(recipient.GetSharedPubkey(ephemeralKey), ephemeral, recipient);

            var nonce = RandomNumberGenerator.GetBytes(12);
            var plaintext = Encoding.UTF8.GetBytes(packageJson);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[16];
            VerifiedXCore.Privacy.CrossPlatformAesGcm.Encrypt(key, nonce, plaintext, ciphertext, tag, Aad(sessionId, recipientIdentifier)); // works on macOS / .NET 6

            return Prefix + Convert.ToBase64String(ephemeral.ToBytes().Concat(nonce).Concat(tag).Concat(ciphertext).ToArray());
        }

        /// <summary>Opens a package sealed to this validator. False for plaintext, tampered or foreign ciphertext.</summary>
        public static bool TryOpen(string? sealedPackage, byte[] myPrivateKey32, string sessionId, string myIdentifier, out string packageJson)
        {
            packageJson = "";
            if (string.IsNullOrEmpty(sealedPackage) || !sealedPackage.StartsWith(Prefix, StringComparison.Ordinal))
                return false;
            try
            {
                var blob = Convert.FromBase64String(sealedPackage.Substring(Prefix.Length));
                if (blob.Length < 33 + 12 + 16 + 1) return false;
                var ephemeral = new NBitcoin.PubKey(blob.AsSpan(0, 33).ToArray());
                var nonce = blob.AsSpan(33, 12).ToArray();
                var tag = blob.AsSpan(45, 16).ToArray();
                var ciphertext = blob.AsSpan(61).ToArray();

                using var myKey = new NBitcoin.Key(myPrivateKey32);
                var key = DeriveKey(ephemeral.GetSharedPubkey(myKey), ephemeral, myKey.PubKey);
                var plaintext = new byte[ciphertext.Length];
                VerifiedXCore.Privacy.CrossPlatformAesGcm.Decrypt(key, nonce, ciphertext, tag, plaintext, Aad(sessionId, myIdentifier));
                packageJson = Encoding.UTF8.GetString(plaintext);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Seals every package of a native round-2 output (identifier -> package) to its recipient. Null (with the reason)
        /// when a recipient has no usable registered public key - the dealer then refuses to release its shares.
        /// </summary>
        public static string? SealAll(string sharesJson, string sessionId, IReadOnlyDictionary<string, string> identifierToAddress,
            Func<string, string?> publicKeyOfAddress, out string? error)
        {
            error = null;
            var packages = JObject.Parse(sharesJson);
            var sealedMap = new JObject();
            foreach (var p in packages.Properties())
            {
                if (!identifierToAddress.TryGetValue(p.Name, out var address))
                {
                    error = $"No participant for identifier {p.Name}.";
                    return null;
                }
                var pub = publicKeyOfAddress(address);
                if (string.IsNullOrEmpty(pub))
                {
                    error = $"No verified public key for participant {address}.";
                    return null;
                }
                sealedMap[p.Name] = Seal(p.Value.ToString(Newtonsoft.Json.Formatting.None), pub, sessionId, p.Name);
            }
            return sealedMap.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>
        /// The registered public key of a vBTC validator, only when it derives the validator's address (a registry entry
        /// with a placeholder or foreign key is not used to seal shares).
        /// </summary>
        public static string? VerifiedPublicKey(string address)
        {
            var pub = Services.VBTCValidatorRegistry.GetValidator(address)?.FrostPublicKey;
            if (string.IsNullOrEmpty(pub)) return null;
            try { return VerifiedXCore.Data.AccountData.GetHumanAddress(pub) == address ? pub : null; }
            catch { return null; }
        }

        /// <summary>This validator's own private key as 32 big-endian bytes (for opening shares), or null.</summary>
        public static byte[]? LocalValidatorPrivateKey()
        {
            var secret = VerifiedXCore.Data.AccountData.GetLocalValidator()?.GetPrivKey?.secret;
            if (secret == null) return null;
            var bytes = secret.Value.ToByteArray(isUnsigned: true, isBigEndian: true);
            if (bytes.Length > 32) return null;
            var padded = new byte[32];
            Buffer.BlockCopy(bytes, 0, padded, 32 - bytes.Length, bytes.Length);
            return padded;
        }
    }
}
