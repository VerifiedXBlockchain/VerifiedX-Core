using System;
using System.IO;
using System.Security;
using System.Threading.Tasks;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Models;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// NEW-01 (follow-up; found by the independent review): importing a key into an ENCRYPTED wallet.
    ///  - Locked (e.g. privkey= at startup without encpass=): the account was inserted with its plaintext key, and the
    ///    encryption step returned null with no password — the plaintext key stayed on disk (and GetKey served it).
    ///  - Unlocked: the key was encrypted but the keystore record holding its data key was never saved, so the wallet
    ///    could no longer decrypt the imported key.
    /// </summary>
    [Collection("DbContextSequential")]
    public class NEW01_EncryptedImportTests : IDisposable
    {
        private const string WalletPw = "wallet pw new01";
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly bool _priorEncrypted = Globals.IsWalletEncrypted;
        private readonly SecureString _priorPassword = Globals.EncryptPassword;

        public NEW01_EncryptedImportTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"new01b_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            Globals.IsWalletEncrypted = false;
            Globals.EncryptPassword = new SecureString();
            TestWalletKeystore.Ensure(WalletPw);
            Globals.IsWalletEncrypted = true;
        }

        public void Dispose()
        {
            Globals.IsWalletEncrypted = _priorEncrypted;
            Globals.EncryptPassword = _priorPassword;
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static SecureString Secure(string s) { var ss = new SecureString(); foreach (var c in s) ss.AppendChar(c); return ss; }

        private static string NewKeyHex() => new PrivateKey("secp256k1").secret.ToString("x").TrimStart('0').PadLeft(64, '0');

        private static async Task<Account?> StoredAfterImport(string address)
        {
            for (int i = 0; i < 50; i++) // AddToAccount is fire-and-forget
            {
                var a = AccountData.GetSingleAccount(address);
                if (a != null) return a;
                await Task.Delay(20);
            }
            return null;
        }

        [Fact]
        public async Task NEW01_ImportIntoLockedEncryptedWallet_StoresNoPlaintextKey()
        {
            var keyHex = NewKeyHex();
            var before = AccountData.GetAccounts().Count();
            var account = await AccountData.RestoreAccount(keyHex);

            if (account?.Address != null)
            {
                var stored = await StoredAfterImport(account.Address);
                Assert.True(stored == null || !stored.PrivateKey.Contains(keyHex.TrimStart('0')), "plaintext key stored in an encrypted wallet");
            }
            // Follow-up (second review): the refusal is reported (null), not returned as if the account were stored.
            Assert.Null(account);
            await Task.Delay(100);
            Assert.Equal(before, AccountData.GetAccounts().Count());
        }

        [Fact]
        public async Task NEW01_ImportIntoUnlockedEncryptedWallet_IsEncryptedAndStillUsable()
        {
            Globals.EncryptPassword = Secure(WalletPw);
            var keyHex = NewKeyHex();
            var account = await AccountData.RestoreAccount(keyHex);

            var stored = await StoredAfterImport(account.Address);
            Assert.NotNull(stored);
            Assert.DoesNotContain(keyHex.TrimStart('0'), stored!.PrivateKey);                   // encrypted at rest
            Assert.Equal(keyHex.TrimStart('0'), stored.GetKey.TrimStart('0'));                   // and the wallet can still use it
        }

        // ── Follow-up (second review): new addresses in an encrypted, non-HD wallet ────────

        [Fact]
        public async Task NEW01_FollowUp_NewAddressInLockedEncryptedWallet_NotCreated()
        {
            // GetNewAddress -> CreateNewAccount -> AddToAccount stored the plaintext key (its encryption step is
            // commented out). Locked: no address is made rather than one stored in plaintext.
            var created = AccountData.CreateNewAccount();
            if (created != null)
            {
                var stored = await StoredAfterImport(created.Address);
                Assert.True(stored == null || !stored.PrivateKey.Contains(created.PrivateKey.TrimStart('0')), "plaintext key stored in an encrypted wallet");
            }
            Assert.Null(created);
        }

        [Fact]
        public async Task NEW01_FollowUp_NewAddressInUnlockedEncryptedWallet_IsEncryptedAndUsable()
        {
            Globals.EncryptPassword = Secure(WalletPw);
            var created = AccountData.CreateNewAccount();
            Assert.NotNull(created);
            var keyHex = created!.GetKey;

            var stored = await StoredAfterImport(created.Address);
            Assert.NotNull(stored);
            Assert.DoesNotContain(keyHex.TrimStart('0'), stored!.PrivateKey);   // encrypted at rest
            Assert.Equal(keyHex.TrimStart('0'), stored.GetKey.TrimStart('0'));   // and usable
        }

        [Fact]
        public async Task NEW01_FollowUp_ReimportWithAStaleKeystoreRecord_StaysUsable()
        {
            // SaveKeystore keeps an existing record for the address and drops the new one; the import used to store
            // ciphertext under a data key no record held. An existing record that opens to the same key is reused.
            Globals.EncryptPassword = Secure(WalletPw);
            var keyHex = NewKeyHex();
            var first = await AccountData.RestoreAccount(keyHex);
            Assert.NotNull(await StoredAfterImport(first!.Address));

            AccountData.GetAccounts().DeleteMany(x => x.Address == first.Address); // account gone, keystore record stays
            Assert.NotNull(Keystore.GetKeystore()!.FindOne(x => x.Address == first.Address));

            var again = await AccountData.RestoreAccount(keyHex);
            Assert.NotNull(again);
            var stored = await StoredAfterImport(first.Address);
            Assert.NotNull(stored);
            Assert.Equal(keyHex.TrimStart('0'), stored!.GetKey.TrimStart('0'));
        }

        // ── Follow-up (third review): HD wallets inside an encrypted wallet ────────────────

        [Fact]
        public async Task NEW01_FollowUp_HdWalletInEncryptedWallet_Refused()
        {
            // Encrypting an HD wallet was refused, but creating/restoring one in an encrypted wallet was not; every
            // derived key (and the seed) was then stored in plaintext.
            Globals.EncryptPassword = Secure(WalletPw); // unlocked
            var before = AccountData.GetAccounts().Count();

            var created = VerifiedXCore.Models.HDWallet.HDWalletData.CreateHDWallet(12, VerifiedXCore.BIP39.BIP39Wordlist.English);
            Assert.False(created.Item1);
            Assert.Null(VerifiedXCore.Models.HDWallet.HDWalletData.GetHDWallet());

            const string mnemonic = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";
            var restored = await VerifiedXCore.Models.HDWallet.HDWalletData.RestoreHDWallet(mnemonic);
            Assert.DoesNotContain("Restored", restored);
            Assert.Null(VerifiedXCore.Models.HDWallet.HDWalletData.GetHDWallet());
            Assert.Equal(before, AccountData.GetAccounts().Count());
        }

        [Fact]
        public async Task NEW01_FollowUp_ExistingHdRecordInEncryptedWallet_DerivesNoPlaintextAddress()
        {
            // A wallet that already reached this state (HD record present, wallet encrypted) makes no more addresses.
            Globals.EncryptPassword = Secure(WalletPw);
            VerifiedXCore.Models.HDWallet.HDWalletData.GetHDWalletData().Insert(new VerifiedXCore.Models.HDWallet
            {
                Nonce = 0, Path = "m/0'/0'",
                WalletSeed = "5eb00bbddcf069084889a8ab9155568165f5c453ccb85e70811aaed6f6da5fc19a5ac40b389cd370d086206dec8aa6c43daea6690f20ad3d8d48b2d2ce9e38e4",
            });
            var before = AccountData.GetAccounts().Count();
            Assert.Null(await VerifiedXCore.Models.HDWallet.HDWalletData.GenerateAddress());
            await Task.Delay(100);
            Assert.Equal(before, AccountData.GetAccounts().Count());
        }
    }
}
