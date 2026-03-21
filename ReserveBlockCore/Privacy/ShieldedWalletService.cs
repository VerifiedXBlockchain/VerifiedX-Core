using LiteDB;
using ReserveBlockCore.Models.Privacy;

namespace ReserveBlockCore.Privacy
{
    /// <summary>
    /// Persists shielded wallet rows in <c>DB_Privacy</c> (<see cref="PrivacyDbContext.PRIV_WALLETS"/>).
    /// </summary>
    public static class ShieldedWalletService
    {
        /// <summary>Insert or replace by <see cref="ShieldedWallet.ShieldedAddress"/>.</summary>
        public static void Upsert(ShieldedWallet wallet, LiteDatabase? db = null)
        {
            var target = db ?? PrivacyDbContext.GetPrivacyDb();
            var col = target.GetCollection<ShieldedWallet>(PrivacyDbContext.PRIV_WALLETS);
            col.EnsureIndex(x => x.ShieldedAddress, true);
            var existing = col.FindOne(x => x.ShieldedAddress == wallet.ShieldedAddress);
            if (existing != null)
                wallet.Id = existing.Id;
            col.Upsert(wallet);
        }

        /// <summary>Returns all shielded wallet rows from the privacy DB.</summary>
        public static List<ShieldedWallet> GetAll(LiteDatabase? db = null)
        {
            var target = db ?? PrivacyDbContext.GetPrivacyDb();
            return target.GetCollection<ShieldedWallet>(PrivacyDbContext.PRIV_WALLETS)
                .FindAll().ToList();
        }

        public static ShieldedWallet? FindByZfxAddress(string zfxAddress, LiteDatabase? db = null)
        {
            var target = db ?? PrivacyDbContext.GetPrivacyDb();
            return target.GetCollection<ShieldedWallet>(PrivacyDbContext.PRIV_WALLETS)
                .FindOne(x => x.ShieldedAddress == zfxAddress);
        }

        /// <summary>Builds a wallet row from HD material; optionally wraps spending+encryption secrets with <paramref name="password"/>.</summary>
        public static ShieldedWallet CreateFromKeyMaterial(
            ShieldedKeyMaterial material,
            string? transparentSourceAddress = null,
            string? password = null)
        {
            byte[]? spendingEnc = null;
            if (!string.IsNullOrEmpty(password))
            {
                var bundle = new byte[material.SpendingKey32.Length + material.EncryptionPrivateKey32.Length];
                Buffer.BlockCopy(material.SpendingKey32, 0, bundle, 0, material.SpendingKey32.Length);
                Buffer.BlockCopy(material.EncryptionPrivateKey32, 0, bundle, material.SpendingKey32.Length, material.EncryptionPrivateKey32.Length);
                spendingEnc = ShieldedSpendingKeyProtector.Protect(bundle, password);
            }

            return new ShieldedWallet
            {
                TransparentSourceAddress = transparentSourceAddress,
                ShieldedAddress = material.ZfxAddress,
                SpendingKey = spendingEnc,
                ViewingKey = material.ViewingKey32,
                IsViewOnly = spendingEnc == null,
                ShieldedBalances = new Dictionary<string, decimal>(),
                UnspentCommitments = new List<UnspentCommitment>(),
                LastScannedBlock = 0
            };
        }

        /// <summary>Decrypts <see cref="ShieldedWallet.SpendingKey"/> when password-protected; returns (spending32, encPriv32).</summary>
        public static bool TryUnwrapSpendingBundle(ShieldedWallet wallet, string password, out byte[] spending32, out byte[] encPriv32, out string? error)
        {
            spending32 = Array.Empty<byte>();
            encPriv32 = Array.Empty<byte>();
            error = null;
            if (wallet.SpendingKey == null || wallet.SpendingKey.Length == 0)
            {
                error = "No spending key blob on wallet row.";
                return false;
            }
            if (!ShieldedSpendingKeyProtector.TryUnprotect(wallet.SpendingKey, password, out var plain, out var err))
            {
                error = err;
                return false;
            }
            if (plain!.Length != 64)
            {
                error = "Unexpected unwrapped key length.";
                return false;
            }
            spending32 = plain.AsSpan(0, 32).ToArray();
            encPriv32 = plain.AsSpan(32, 32).ToArray();
            return true;
        }
    }
}
