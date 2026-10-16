using System;
using System.IO;
using System.Security;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using VerifiedXCore;
using VerifiedXCore.Controllers;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Unlocking an encrypted wallet needs its real password, even when the first account's key is still stored in
    /// plaintext (an account from before encryption), which the unlock's signature check could not tell apart.
    /// </summary>
    [Collection("DbContextSequential")]
    public class WalletUnlockPasswordTests : IDisposable
    {
        private const string WalletPw = "wallet pw unlock";
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly bool _priorEncrypted = Globals.IsWalletEncrypted;
        private readonly SecureString _priorPassword = Globals.EncryptPassword;

        public WalletUnlockPasswordTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"unlock_pw_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            WalletEncryptionService.ResetPasswordChecks();
            Globals.IsWalletEncrypted = false;
            Globals.EncryptPassword = new SecureString();

            // A plaintext account first in the wallet (from before encryption), then the wallet's keystore.
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            AccountData.GetAccounts().InsertSafe(new Account
            {
                Address = AccountData.GetHumanAddress(pub), PublicKey = pub,
                PrivateKey = key.secret.ToString("x"), Balance = 0M,
            });
            TestWalletKeystore.Ensure(WalletPw);
            Globals.IsWalletEncrypted = true;
            Globals.EncryptPassword = new SecureString();
        }

        public void Dispose()
        {
            WalletEncryptionService.ResetPasswordChecks();
            Globals.IsWalletEncrypted = _priorEncrypted;
            Globals.EncryptPassword = _priorPassword;
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        [Fact]
        public async Task AWrongPassword_DoesNotUnlock()
        {
            var result = JArray.Parse("[" + await new V1Controller().GetDecryptWallet("not the password") + "]")[0];

            Assert.Equal("Fail", (string?)result["Result"]);
            Assert.Equal(0, Globals.EncryptPassword.Length);
        }

        [Fact]
        public async Task TheWalletPassword_StillUnlocks()
        {
            var result = JArray.Parse("[" + await new V1Controller().GetDecryptWallet(WalletPw) + "]")[0];

            Assert.Equal("Success", (string?)result["Result"]);
            Assert.True(Globals.EncryptPassword.Length > 0);
        }

        [Fact]
        public void RefusesUnlock_TellsTheWalletPasswordFromAnyOther()
        {
            Assert.True(WalletEncryptionService.RefusesUnlock("not the password"));
            Assert.False(WalletEncryptionService.RefusesUnlock(WalletPw));

            // An unencrypted wallet has no password to check; its unlock paths are unchanged.
            Globals.IsWalletEncrypted = false;
            Assert.False(WalletEncryptionService.RefusesUnlock("anything"));
            Globals.IsWalletEncrypted = true;
        }

        [Fact]
        public void AnEncpassThatIsNotTheWalletPassword_LeavesTheWalletLocked()
        {
            Globals.EncryptPassword = "not the password".ToSecureString();
            StartupService.EncryptedWalletCheck();
            Assert.Equal(0, Globals.EncryptPassword.Length);

            Globals.EncryptPassword = WalletPw.ToSecureString();
            StartupService.EncryptedWalletCheck();
            Assert.True(Globals.EncryptPassword.Length > 0);
        }
    }
}