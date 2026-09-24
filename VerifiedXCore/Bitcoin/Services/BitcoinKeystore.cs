using NBitcoin;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Extensions;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Bitcoin.Services
{
    /// <summary>
    /// VX-13: Bitcoin private keys follow the wallet's encryption. There was no keystore under Bitcoin/ at all —
    /// keys (hex and WIF) were stored and served in plaintext even while the VFX wallet was encrypted and locked.
    ///
    /// When the wallet is encrypted, BitcoinAccount.PrivateKey holds a KeystoreCrypto-sealed value, WifKey is not
    /// stored (derived on demand), and IsEncrypted is true. The key is available only while the wallet password is
    /// in memory. Accounts created before encryption (or before this build) are sealed as soon as the password is
    /// present: at encryption/unlock time, at startup, and lazily on first use.
    /// </summary>
    public static class BitcoinKeystore
    {
        private static string? CurrentPassword() =>
            Globals.EncryptPassword != null && Globals.EncryptPassword.Length > 0 ? Globals.EncryptPassword.ToUnsecureString() : null;

        /// <summary>
        /// VX-13 (follow-up): the in-memory password only if it IS the wallet password. Sealing used the in-memory value
        /// unchecked, so a mistyped encpass= on the first start after upgrading sealed every Bitcoin key under the typo.
        /// </summary>
        private static string? VerifiedPassword()
        {
            var pw = CurrentPassword();
            return pw != null && VerifiedXCore.Services.WalletEncryptionService.IsWalletPassword(pw) ? pw : null;
        }

        /// <summary>The account's private key as hex, or null when it is sealed and the wallet is locked.</summary>
        public static string? GetPrivateKeyHex(BitcoinAccount account)
        {
            if (account == null) return null;
            // VX-18: an encrypted wallet that is locked signs nothing. A key not yet sealed (created before this build,
            // wallet not unlocked since) used to be returned here in plaintext, so every Bitcoin signing path could
            // still sign while locked.
            if (BitcoinFeePolicy.WalletIsLocked()) return null;
            if (!account.IsEncrypted)
            {
                var plain = account.PrivateKey;
                if (Globals.IsWalletEncrypted && VerifiedPassword() != null && TrySealAndSave(account))
                {
                    // VX-13 (follow-up): seal the caller's copy too. Send/RBF write this object back after signing
                    // (balance update), which put the plaintext key back over the freshly sealed record.
                    var sealedRec = BitcoinAccount.GetBitcoin()?.FindOne(x => x.Address == account.Address);
                    if (sealedRec != null && sealedRec.IsEncrypted)
                    {
                        account.PrivateKey = sealedRec.PrivateKey;
                        account.WifKey = sealedRec.WifKey;
                        account.IsEncrypted = true;
                    }
                }
                return plain;
            }
            var pw = CurrentPassword();
            if (pw == null) return null;
            return KeystoreCrypto.TryOpen(account.PrivateKey, pw, out var hex) ? hex : null;
        }

        /// <summary>The account's WIF, derived from the key; null when the key is unavailable.</summary>
        public static string? GetWif(BitcoinAccount account)
        {
            var hex = GetPrivateKeyHex(account);
            if (string.IsNullOrEmpty(hex)) return null;
            return new Key(NBitcoin.DataEncoders.Encoders.Hex.DecodeData(hex)).GetWif(Globals.BTCNetwork).ToString();
        }

        /// <summary>Seals a plaintext account in memory (does not save). False when the wallet is locked.</summary>
        public static bool TrySeal(BitcoinAccount account)
        {
            if (account.IsEncrypted) return true;
            var pw = VerifiedPassword(); // VX-13 (follow-up): never seal under an unverified password
            if (pw == null) return false;
            account.PrivateKey = KeystoreCrypto.Seal(account.PrivateKey, pw);
            account.WifKey = "";
            account.IsEncrypted = true;
            return true;
        }

        private static bool TrySealAndSave(BitcoinAccount account)
        {
            try
            {
                var db = BitcoinAccount.GetBitcoin();
                var stored = db?.FindOne(x => x.Address == account.Address);
                if (stored == null || stored.IsEncrypted) return false;
                if (!TrySeal(stored)) return false;
                db!.UpdateSafe(stored);
                return true;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Sealing Bitcoin key for {account.Address} failed: {ex.Message}", "BitcoinKeystore.TrySealAndSave()");
                return false;
            }
        }

        /// <summary>
        /// Seals every plaintext Bitcoin key when the wallet is encrypted and its password is in memory. Call after
        /// the password is set (encrypt, unlock, startup). Returns the number of accounts sealed.
        /// </summary>
        public static int SealPlaintextAccountsIfUnlocked()
        {
            if (!Globals.IsWalletEncrypted || VerifiedPassword() == null) return 0; // VX-13 (follow-up)
            int n = 0;
            try
            {
                var db = BitcoinAccount.GetBitcoin();
                if (db == null) return 0;
                foreach (var a in db.FindAll().Where(x => !x.IsEncrypted).ToList())
                {
                    if (TrySeal(a)) { db.UpdateSafe(a); n++; }
                }
                if (n > 0)
                    LogUtility.Log($"VX-13: sealed {n} plaintext Bitcoin key(s) under the wallet password.", "BitcoinKeystore.SealPlaintextAccountsIfUnlocked()");
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Sealing Bitcoin keys failed: {ex.Message}", "BitcoinKeystore.SealPlaintextAccountsIfUnlocked()");
            }
            return n;
        }
    }
}
