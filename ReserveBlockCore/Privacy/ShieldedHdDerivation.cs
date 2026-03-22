using System.Security.Cryptography;
using System.Text;
using NBitcoin;

namespace ReserveBlockCore.Privacy
{
    /// <summary>
    /// Shielded HD derivation using the same BIP32 engine as transparent accounts (<see cref="BIP32"/> — hardened numeric paths only).
    /// Hierarchy: BIP32 spending scalar → viewing digest (32 B) → encryption secp256k1 keypair; <c>zfx_</c> encodes the encryption pubkey.
    /// </summary>
    public static class ShieldedHdDerivation
    {
        private static readonly byte[] ViewingDomain = Encoding.UTF8.GetBytes("VFX/shielded/viewing/v1");
        private static readonly byte[] EncPrivDomain = Encoding.UTF8.GetBytes("VFX/shielded/enc-priv/v1");
        private static readonly byte[] SpendFixDomain = Encoding.UTF8.GetBytes("VFX/shielded/spend-scalar/v1");

        /// <summary>
        /// Full hardened path: <c>m/44'/{coinType}'/0'/1'/{addressIndex}'</c>.
        /// Chain <c>1'</c> is the shielded external branch (parallel to transparent <c>0'</c> in common BIP44 wording).
        /// </summary>
        public static string FormatDerivationPath(uint coinType, uint addressIndex)
        {
            if (coinType >= 0x8000_0000)
                throw new ArgumentOutOfRangeException(nameof(coinType), "Coin type must be a non-hardened BIP44 index (< 2^31).");
            if (addressIndex >= 0x8000_0000)
                throw new ArgumentOutOfRangeException(nameof(addressIndex), "Address index must be a non-hardened BIP44 index (< 2^31).");

            return $"m/44'/{coinType}'/0'/1'/{addressIndex}'";
        }

        /// <summary>32-byte secret from HD wallet seed hex (same storage as <see cref="Models.HDWallet.WalletSeed"/>).</summary>
        public static byte[] DerivePrivateKeyBytes(string walletSeedHex, uint coinType, uint addressIndex)
        {
            if (string.IsNullOrWhiteSpace(walletSeedHex))
                throw new ArgumentException("Wallet seed hex is required.", nameof(walletSeedHex));

            var path = FormatDerivationPath(coinType, addressIndex);
            var bip = new global::ReserveBlockCore.BIP32.BIP32();
            return bip.DerivePath(path, walletSeedHex).Key;
        }

        /// <summary>Full shielded key bundle for <paramref name="addressIndex"/>.</summary>
        public static ShieldedKeyMaterial DeriveShieldedKeyMaterial(string walletSeedHex, uint coinType, uint addressIndex)
        {
            var rawSpend = DerivePrivateKeyBytes(walletSeedHex, coinType, addressIndex);
            var spendingKey = ToValidNbitcoinKey(rawSpend, SpendFixDomain);
            var spendBytes = spendingKey.ToBytes();

            var vbuf = new byte[ViewingDomain.Length + 32];
            ViewingDomain.CopyTo(vbuf, 0);
            Buffer.BlockCopy(spendBytes, 0, vbuf, ViewingDomain.Length, 32);
            var viewing = SHA256.HashData(vbuf);

            var encPriv = DeriveValidPrivateKeyScalar(viewing, EncPrivDomain);
            var encKey = new Key(encPriv);
            var pub33 = encKey.PubKey.ToBytes();
            var zfx = ShieldedAddressCodec.EncodeEncryptionKey(pub33);

            return new ShieldedKeyMaterial
            {
                SpendingKey32 = spendBytes,
                ViewingKey32 = viewing,
                EncryptionPrivateKey32 = encPriv,
                EncryptionPublicKey33 = pub33,
                ZfxAddress = zfx
            };
        }

        /// <summary>
        /// Derives the 32-byte encryption private key from a 32-byte viewing key.
        /// This allows note decryption (scanning) without needing the spending key password.
        /// </summary>
        public static byte[] DeriveEncryptionPrivateKeyFromViewingKey(byte[] viewingKey32)
        {
            if (viewingKey32 == null || viewingKey32.Length != 32)
                throw new ArgumentException("Viewing key must be 32 bytes.", nameof(viewingKey32));
            return DeriveValidPrivateKeyScalar(viewingKey32, EncPrivDomain);
        }

        /// <summary>33-byte compressed secp256k1 encryption pubkey (same bytes as inside <c>zfx_</c>).</summary>
        public static byte[] DeriveZfxEncryptionKeyMaterial33(string walletSeedHex, uint coinType, uint addressIndex) =>
            DeriveShieldedKeyMaterial(walletSeedHex, coinType, addressIndex).EncryptionPublicKey33;

        public static string DeriveZfxAddress(string walletSeedHex, uint coinType, uint addressIndex) =>
            DeriveShieldedKeyMaterial(walletSeedHex, coinType, addressIndex).ZfxAddress;

        /// <summary>
        /// Derive shielded key material directly from an account's private key (hex).
        /// Works for both single accounts and HD-derived accounts — no HD wallet seed required.
        /// </summary>
        public static ShieldedKeyMaterial DeriveFromPrivateKey(string privateKeyHex)
        {
            if (string.IsNullOrWhiteSpace(privateKeyHex))
                throw new ArgumentException("Private key hex is required.", nameof(privateKeyHex));

            // Ensure even-length hex string (leading zero may be dropped by BigInteger.ToString("x"))
            if (privateKeyHex.Length % 2 != 0)
                privateKeyHex = "0" + privateKeyHex;

            var rawBytes = Convert.FromHexString(privateKeyHex);
            var spendingKey = ToValidNbitcoinKey(rawBytes, SpendFixDomain);
            var spendBytes = spendingKey.ToBytes();

            var vbuf = new byte[ViewingDomain.Length + 32];
            ViewingDomain.CopyTo(vbuf, 0);
            Buffer.BlockCopy(spendBytes, 0, vbuf, ViewingDomain.Length, 32);
            var viewing = SHA256.HashData(vbuf);

            var encPriv = DeriveValidPrivateKeyScalar(viewing, EncPrivDomain);
            var encKey = new Key(encPriv);
            var pub33 = encKey.PubKey.ToBytes();
            var zfx = ShieldedAddressCodec.EncodeEncryptionKey(pub33);

            return new ShieldedKeyMaterial
            {
                SpendingKey32 = spendBytes,
                ViewingKey32 = viewing,
                EncryptionPrivateKey32 = encPriv,
                EncryptionPublicKey33 = pub33,
                ZfxAddress = zfx
            };
        }

        private static Key ToValidNbitcoinKey(byte[] candidate32, ReadOnlySpan<byte> fixDomain)
        {
            try
            {
                return new Key(candidate32);
            }
            catch
            {
                var fixedScalar = DeriveValidPrivateKeyScalar(candidate32, fixDomain);
                return new Key(fixedScalar);
            }
        }

        private static byte[] DeriveValidPrivateKeyScalar(ReadOnlySpan<byte> parentMaterial, ReadOnlySpan<byte> labelUtf8)
        {
            var seed = parentMaterial.ToArray();
            for (var round = 0; round < 128; round++)
            {
                var buf = new byte[labelUtf8.Length + seed.Length + 1];
                labelUtf8.CopyTo(buf);
                Buffer.BlockCopy(seed, 0, buf, labelUtf8.Length, seed.Length);
                buf[^1] = (byte)round;
                seed = SHA256.HashData(buf);
                try
                {
                    _ = new Key(seed);
                    return seed;
                }
                catch
                {
                    // try next round
                }
            }

            throw new InvalidOperationException("Could not derive valid secp256k1 private key for shielded branch.");
        }
    }
}
