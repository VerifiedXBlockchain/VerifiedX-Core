using LiteDB;
using VerifiedXCore.Models.Privacy;

namespace VerifiedXCore.Privacy
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
            {
                wallet.Id = existing.Id;

                // Preserve existing scan state so a repeat create call doesn't wipe scanned data
                if (wallet.LastScannedBlock == 0 && existing.LastScannedBlock > 0)
                    wallet.LastScannedBlock = existing.LastScannedBlock;

                if ((wallet.UnspentCommitments == null || wallet.UnspentCommitments.Count == 0)
                    && existing.UnspentCommitments != null && existing.UnspentCommitments.Count > 0)
                    wallet.UnspentCommitments = existing.UnspentCommitments;

                if ((wallet.ShieldedBalances == null || wallet.ShieldedBalances.Count == 0)
                    && existing.ShieldedBalances != null && existing.ShieldedBalances.Count > 0)
                    wallet.ShieldedBalances = existing.ShieldedBalances;

                // If existing wallet has spending key and incoming one doesn't, preserve it
                if ((wallet.SpendingKey == null || wallet.SpendingKey.Length == 0)
                    && existing.SpendingKey != null && existing.SpendingKey.Length > 0)
                {
                    wallet.SpendingKey = existing.SpendingKey;
                    wallet.IsViewOnly = existing.IsViewOnly;
                }
            }
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
            var col = target.GetCollection<ShieldedWallet>(PrivacyDbContext.PRIV_WALLETS);
            var exact = col.FindOne(x => x.ShieldedAddress == zfxAddress);
            if (exact != null)
                return exact;
            // Stage 3: the same keys have a v1 and a v2 address string; a row stored under one is found by the other.
            if (!ShieldedAddressCodec.TryDecodeEncryptionKey(zfxAddress, out var enc, out _))
                return null;
            foreach (var w in col.FindAll())
            {
                if (ShieldedAddressCodec.TryDecodeEncryptionKey(w.ShieldedAddress, out var e2, out _) && e2.AsSpan().SequenceEqual(enc))
                    return w;
            }
            return null;
        }

        /// <summary>
        /// The address this wallet row should be paid at now: the v2 encoding (encryption key + owner key) of its keys,
        /// whatever string the row was created with. Falls back to the stored string when the keys cannot be read.
        /// </summary>
        public static string CurrentAddress(ShieldedWallet w)
        {
            try
            {
                if (w?.ViewingKey == null || w.ViewingKey.Length != 32) return w?.ShieldedAddress ?? "";
                if (!ShieldedAddressCodec.TryDecodeEncryptionKey(w.ShieldedAddress, out var enc, out _)) return w.ShieldedAddress;
                return ShieldedAddressCodec.Encode(enc, ShieldedKeyMaterial.OwnerPkFromViewingKey(w.ViewingKey));
            }
            catch { return w?.ShieldedAddress ?? ""; }
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
