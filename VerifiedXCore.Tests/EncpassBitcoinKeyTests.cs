using System;
using System.IO;
using System.Security;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.Extensions;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A password given with the encpass start argument unlocks the wallet only if it is the wallet's password. Any other
    /// value leaves the wallet locked, so a Bitcoin key not yet sealed is not handed out for signing and is not sealed
    /// under the wrong password.
    /// </summary>
    [Collection("DbContextSequential")]
    public class EncpassBitcoinKeyTests : IDisposable
    {
        private const string WalletPw = "wallet pw encpass";
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly bool _priorEncrypted = Globals.IsWalletEncrypted;
        private readonly SecureString _priorPassword = Globals.EncryptPassword;
        private readonly NBitcoin.Network _priorNetwork = Globals.BTCNetwork;
        private readonly NBitcoin.ScriptPubKeyType _priorScriptType = Globals.ScriptPubKeyType;

        public EncpassBitcoinKeyTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"encpass_btc_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            WalletEncryptionService.ResetPasswordChecks();
            Globals.IsWalletEncrypted = false;
            Globals.EncryptPassword = new SecureString();
            Globals.BTCNetwork = NBitcoin.Network.TestNet;
            Globals.ScriptPubKeyType = NBitcoin.ScriptPubKeyType.Segwit;
            TestWalletKeystore.Ensure(WalletPw);
        }

        public void Dispose()
        {
            WalletEncryptionService.ResetPasswordChecks();
            Globals.IsWalletEncrypted = _priorEncrypted;
            Globals.EncryptPassword = _priorPassword;
            Globals.BTCNetwork = _priorNetwork;
            Globals.ScriptPubKeyType = _priorScriptType;
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static BitcoinAccount Stored(string address) => BitcoinAccount.GetBitcoin()!.FindOne(x => x.Address == address);

        /// <summary>A Bitcoin account from before the wallet was encrypted (plaintext at rest), then a start with encpass=.</summary>
        private static BitcoinAccount StartWith(string encpass)
        {
            var account = BitcoinAccount.CreateAddress(save: true);
            Globals.IsWalletEncrypted = true;
            Globals.EncryptPassword = encpass.ToSecureString();
            StartupService.EncryptedWalletCheck();
            return account;
        }

        [Fact]
        public void AnEncpassThatIsNotTheWalletPassword_LeavesBitcoinKeysUnusable_AndUnsealed()
        {
            var account = StartWith("not the password");
            var keyHex = account.PrivateKey;

            Assert.Equal(0, Globals.EncryptPassword.Length);
            Assert.Null(BitcoinKeystore.GetPrivateKeyHex(Stored(account.Address)));
            Assert.Null(BitcoinKeystore.GetWif(Stored(account.Address)));

            // Not sealed under the wrong password: the wallet password still opens it.
            Assert.False(Stored(account.Address).IsEncrypted);
            Globals.EncryptPassword = WalletPw.ToSecureString();
            Assert.Equal(keyHex, BitcoinKeystore.GetPrivateKeyHex(Stored(account.Address)));
        }

        [Fact]
        public void AnyOtherPasswordInMemory_DoesNotHandOutAnUnsealedBitcoinKey()
        {
            var account = BitcoinAccount.CreateAddress(save: true);
            Globals.IsWalletEncrypted = true;
            Globals.EncryptPassword = "not the password".ToSecureString(); // in memory without passing a startup or unlock check

            Assert.Null(BitcoinKeystore.GetPrivateKeyHex(Stored(account.Address)));
            Assert.Null(BitcoinKeystore.GetWif(Stored(account.Address)));
            Assert.False(Stored(account.Address).IsEncrypted);
        }

        [Fact]
        public void TheWalletPasswordAsEncpass_UnlocksBitcoinKeys_AndSealsThem()
        {
            var account = StartWith(WalletPw);
            var keyHex = account.PrivateKey;

            Assert.True(Globals.EncryptPassword.Length > 0);
            Assert.Equal(keyHex, BitcoinKeystore.GetPrivateKeyHex(Stored(account.Address)));
            Assert.True(Stored(account.Address).IsEncrypted);
        }
    }
}
