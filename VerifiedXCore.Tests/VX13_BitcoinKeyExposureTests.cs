using System;
using System.IO;
using System.Net;
using System.Security;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
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
    /// VX-13 (HIGH): "Bitcoin keys are served in plaintext while the wallet is encrypted and locked".
    ///
    /// Audit PoC: with GetIsWalletEncrypted = true and GetEncryptLock = locked (control: SendTransaction 401,
    /// VFX key served as ciphertext), GetBitcoinAccountList, GetBitcoinAccount/{addr} and GetNewAddress
    /// returned the plaintext Bitcoin private key and an importable WIF. No keystore existed under Bitcoin/.
    /// </summary>
    [Collection("DbContextSequential")]
    public class VX13_BitcoinKeyExposureTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly bool _priorApiEnabled = Startup.APIEnabled;
        private readonly bool _priorEncrypted = Globals.IsWalletEncrypted;
        private readonly SecureString _priorPassword = Globals.EncryptPassword;
        private readonly NBitcoin.Network _priorNetwork = Globals.BTCNetwork;
        private readonly NBitcoin.ScriptPubKeyType _priorScriptType = Globals.ScriptPubKeyType;

        public VX13_BitcoinKeyExposureTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"vx13_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            TestWalletKeystore.Ensure("correct horse 12"); // VX-13 follow-up: sealing verifies the wallet password
            Startup.APIEnabled = true;
            Globals.IsWalletEncrypted = false;
            Globals.EncryptPassword = new SecureString();
            Globals.BTCNetwork = NBitcoin.Network.TestNet; // set at node startup
            Globals.ScriptPubKeyType = NBitcoin.ScriptPubKeyType.Segwit;
        }

        public void Dispose()
        {
            Startup.APIEnabled = _priorApiEnabled;
            Globals.IsWalletEncrypted = _priorEncrypted;
            Globals.EncryptPassword = _priorPassword;
            Globals.BTCNetwork = _priorNetwork;
            Globals.ScriptPubKeyType = _priorScriptType;
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static SecureString Pw(string s) { var ss = new SecureString(); foreach (var c in s) ss.AppendChar(c); return ss; }
        private static void Unlock() { Globals.IsWalletEncrypted = true; Globals.EncryptPassword = Pw("correct horse 12"); }
        private static void Lock() { Globals.IsWalletEncrypted = true; Globals.EncryptPassword = new SecureString(); }
        private static TestServer NewServer() => new TestServer(new WebHostBuilder().UseStartup<Startup>());
        private static BitcoinAccount Stored(string address) => BitcoinAccount.GetBitcoin()!.FindOne(x => x.Address == address);

        // ── Audit PoC: locked wallet, read routes ──────────────────────────────────────────

        [Fact]
        public async Task VX13_AuditPoC_LockedWallet_ReadRoutesServeNoKeyMaterial()
        {
            var account = BitcoinAccount.CreateAddress(save: true); // created before encryption: plaintext at rest
            var keyHex = account.PrivateKey;
            var wif = account.WifKey;
            Lock();

            using var server = NewServer();
            var client = server.CreateClient();
            foreach (var route in new[] { "/btcapi/BTCV2/GetBitcoinAccountList", $"/btcapi/BTCV2/GetBitcoinAccount/{account.Address}" })
            {
                var r = await client.GetAsync(route);
                var body = await r.Content.ReadAsStringAsync();
                Assert.Equal(HttpStatusCode.OK, r.StatusCode);
                Assert.Contains(account.Address, body);       // control: the account is listed
                Assert.DoesNotContain(keyHex, body);
                Assert.DoesNotContain(wif, body);
            }
        }

        [Fact]
        public async Task VX13_AuditPoC_LockedWallet_GetNewAddress_Refused()
        {
            Lock();
            using var server = NewServer();
            var r = await server.CreateClient().GetAsync("/btcapi/BTCV2/GetNewAddress");
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        }

        // ── Keys at rest follow the wallet's encryption ────────────────────────────────────

        [Fact]
        public void VX13_NewAccount_InEncryptedWallet_IsSealedAtRest_AndUsableOnlyWhileUnlocked()
        {
            Unlock();
            var account = BitcoinAccount.CreateAddress(save: false);
            var keyHex = account.PrivateKey;
            var wif = account.WifKey;
            Assert.True(BitcoinAccount.SaveBitcoinAddress(account));

            var stored = Stored(account.Address);
            Assert.True(stored.IsEncrypted);
            Assert.True(KeystoreCrypto.IsSealed(stored.PrivateKey));
            Assert.DoesNotContain(keyHex, stored.PrivateKey);
            Assert.True(string.IsNullOrEmpty(stored.WifKey));

            Assert.Equal(keyHex, BitcoinKeystore.GetPrivateKeyHex(stored));
            Assert.Equal(wif, BitcoinKeystore.GetWif(stored));

            Lock();
            Assert.Null(BitcoinKeystore.GetPrivateKeyHex(stored));
            Assert.Null(BitcoinKeystore.GetWif(stored));
        }

        [Fact]
        public void VX13_LockedEncryptedWallet_RefusesToStoreAPlaintextKey()
        {
            Lock();
            var account = BitcoinAccount.CreateAddress(save: false);
            Assert.False(BitcoinAccount.SaveBitcoinAddress(account));
            Assert.Null(Stored(account.Address));
        }

        [Fact]
        public void VX13_ExistingPlaintextAccounts_AreSealedOnceThePasswordIsPresent()
        {
            var a = BitcoinAccount.CreateAddress(save: true); // plaintext (wallet not yet encrypted)
            var keyHex = a.PrivateKey;
            Unlock();

            Assert.Equal(1, BitcoinKeystore.SealPlaintextAccountsIfUnlocked());
            var stored = Stored(a.Address);
            Assert.True(stored.IsEncrypted);
            Assert.Equal(keyHex, BitcoinKeystore.GetPrivateKeyHex(stored));
        }

        [Fact]
        public void VX13_LazyMigration_OnFirstUseWhileUnlocked()
        {
            var a = BitcoinAccount.CreateAddress(save: true);
            var keyHex = a.PrivateKey;
            Unlock();

            Assert.Equal(keyHex, BitcoinKeystore.GetPrivateKeyHex(Stored(a.Address)));
            Assert.True(Stored(a.Address).IsEncrypted);
        }

        [Fact]
        public void VX13_Control_UnencryptedWallet_IsUnchanged()
        {
            var a = BitcoinAccount.CreateAddress(save: true);
            var stored = Stored(a.Address);
            Assert.False(stored.IsEncrypted);
            Assert.Equal(a.PrivateKey, BitcoinKeystore.GetPrivateKeyHex(stored));
        }

        // ── Sealing primitive ──────────────────────────────────────────────────────────────

        [Fact]
        public void VX13_Keystore_RoundTrip_WrongPasswordAndTamperFail()
        {
            var sealedValue = KeystoreCrypto.Seal("secret-hex", "pw-1");
            Assert.True(KeystoreCrypto.TryOpen(sealedValue, "pw-1", out var pt));
            Assert.Equal("secret-hex", pt);
            Assert.False(KeystoreCrypto.TryOpen(sealedValue, "pw-2", out _));
            var tampered = sealedValue.Substring(0, sealedValue.Length - 4) + (sealedValue.EndsWith("AAAA") ? "BBBB" : "AAAA");
            Assert.False(KeystoreCrypto.TryOpen(tampered, "pw-1", out _));
            Assert.NotEqual(sealedValue, KeystoreCrypto.Seal("secret-hex", "pw-1")); // salted + random nonce
        }

        // ── Follow-up (independent review) ────────────────────────────────────────────────

        [Fact]
        public void VX13_MistypedPassword_NeverSealsKeysUnderTheTypo()
        {
            var a = BitcoinAccount.CreateAddress(save: true); // plaintext, pre-upgrade
            var keyHex = a.PrivateKey;
            Globals.IsWalletEncrypted = true;
            Globals.EncryptPassword = Pw("correct horse 21"); // e.g. a mistyped encpass= at startup

            Assert.Equal(0, BitcoinKeystore.SealPlaintextAccountsIfUnlocked());
            Assert.False(Stored(a.Address).IsEncrypted);

            Unlock(); // the real password
            Assert.Equal(1, BitcoinKeystore.SealPlaintextAccountsIfUnlocked());
            Assert.Equal(keyHex, BitcoinKeystore.GetPrivateKeyHex(Stored(a.Address)));
        }

        [Fact]
        public void VX13_WriteBackAfterLazySeal_DoesNotRestoreThePlaintextKey()
        {
            var a = BitcoinAccount.CreateAddress(save: true);
            var keyHex = a.PrivateKey;
            Unlock();

            var inMemory = Stored(a.Address);                 // what a send path holds
            Assert.Equal(keyHex, BitcoinKeystore.GetPrivateKeyHex(inMemory)); // lazy seal happens here
            inMemory.Balance += 1M;
            BitcoinAccount.GetBitcoin()!.UpdateSafe(inMemory); // the send path's balance write-back

            var stored = Stored(a.Address);
            Assert.True(stored.IsEncrypted);
            Assert.True(KeystoreCrypto.IsSealed(stored.PrivateKey));
            Assert.DoesNotContain(keyHex, stored.PrivateKey);
        }
    }
}
