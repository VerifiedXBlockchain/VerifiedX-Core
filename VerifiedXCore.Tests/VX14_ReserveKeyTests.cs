using System;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Newtonsoft.Json.Linq;
using VerifiedXCore;
using VerifiedXCore.Controllers;
using VerifiedXCore.Data;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// VX-14 (MEDIUM): "Reserve account keys are served on request".
    ///
    /// Audit PoC: GetAllReserveAccounts serialised the entity — while locked the response carried the keystore pair
    /// (PrivateKey ciphertext + EncryptedDecryptKey) wrapped with the zero-padded ASCII password (no salt, no KDF);
    /// after UnlockReserveAccount ("unlocked for 0 minutes") GetKey/GetPrivKey carried the plaintext key, because the
    /// accessor tested only dictionary membership and never the expiry.
    ///
    /// The same zero-padded wrap protected the VFX wallet keystore (Keystore.Key); it is covered here too.
    /// </summary>
    [Collection("DbContextSequential")]
    public class VX14_ReserveKeyTests : IDisposable
    {
        private const string Pw = "reserve pw 14";
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly bool _priorApiEnabled = Startup.APIEnabled;
        private readonly bool _priorEncrypted = Globals.IsWalletEncrypted;
        private readonly SecureString _priorPassword = Globals.EncryptPassword;
        private readonly int _priorUnlockTime = Globals.WalletUnlockTime;
        private readonly bool _priorHd = Globals.HDWallet;

        public VX14_ReserveKeyTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"vx14_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            Startup.APIEnabled = true;
            Globals.IsWalletEncrypted = false;
            Globals.EncryptPassword = new SecureString();
            Globals.WalletUnlockTime = 0; // the default when no wallet/API password is configured (the audit's setup)
            Globals.HDWallet = false;
            Globals.ReserveAccountUnlockKeys.Clear();
        }

        public void Dispose()
        {
            Globals.ReserveAccountUnlockKeys.Clear();
            Startup.APIEnabled = _priorApiEnabled;
            Globals.IsWalletEncrypted = _priorEncrypted;
            Globals.EncryptPassword = _priorPassword;
            Globals.WalletUnlockTime = _priorUnlockTime;
            Globals.HDWallet = _priorHd;
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static TestServer NewServer() => new TestServer(new WebHostBuilder().UseStartup<Startup>());

        private static SecureString Secure(string s) { var ss = new SecureString(); foreach (var c in s) ss.AppendChar(c); return ss; }

        private static ReserveAccount Stored(string address)
        {
            // SaveReserveAccount is fired without await by the create path; wait for the row.
            for (int i = 0; i < 100; i++)
            {
                var a = ReserveAccount.GetReserveAccountSingle(address);
                if (a != null) return a;
                Task.Delay(20).Wait();
            }
            throw new Exception("reserve account was not saved");
        }

        private static ReserveAccount.ReserveAccountInfo Create(string password = Pw)
        {
            var info = ReserveAccount.CreateNewReserveAccount(password);
            Stored(info.Address);
            return info;
        }

        /// <summary>The pre-fix wrap, reproduced independently: AES-CBC, random IV prefix, key = zero-padded ASCII password.</summary>
        private static string LegacyCbc(string plaintext, byte[] key)
        {
            using var aes = Aes.Create();
            aes.Key = key;
            aes.GenerateIV();
            aes.Mode = CipherMode.CBC;
            using var ms = new MemoryStream();
            using (var cs = new CryptoStream(ms, aes.CreateEncryptor(aes.Key, aes.IV), CryptoStreamMode.Write))
            using (var sw = new StreamWriter(cs))
                sw.Write(plaintext);
            return Convert.ToBase64String(aes.IV.Concat(ms.ToArray()).ToArray());
        }

        private static byte[] ZeroPadded(string password)
        {
            var pw = Encoding.ASCII.GetBytes(password);
            return new byte[32 - pw.Length].Concat(pw).ToArray();
        }

        /// <summary>Stores a reserve account exactly as the pre-fix code did (legacy password wrap).</summary>
        private static (string Address, string KeyHex) InsertLegacyReserve(string password)
        {
            var info = ReserveAccount.CreateNewReserveAccount("fixture-only-pw", false, skipSave: true); // key only; wrapped below
            var dataKey = RandomNumberGenerator.GetBytes(32);
            var rec = new ReserveAccount
            {
                Address = info.Address,
                PublicKey = "legacy",
                RecoveryAddress = info.RecoveryAddress,
                PrivateKey = LegacyCbc(info.PrivateKey, dataKey),
                EncryptedDecryptKey = LegacyCbc(Convert.ToBase64String(dataKey), ZeroPadded(password)),
            };
            ReserveAccount.GetReserveAccountsDb()!.Insert(rec);
            return (info.Address, info.PrivateKey);
        }

        private static void AssertNoKeyMaterial(string body, string keyHex, ReserveAccount stored)
        {
            Assert.DoesNotContain(keyHex, body);
            Assert.DoesNotContain(stored.PrivateKey, body);
            Assert.DoesNotContain(stored.EncryptedDecryptKey, body);
            Assert.DoesNotContain("\"PrivateKey\"", body);
            Assert.DoesNotContain("\"EncryptedDecryptKey\"", body);
            Assert.DoesNotContain("\"GetKey\"", body);
            Assert.DoesNotContain("\"GetPrivKey\"", body);
        }

        // ── Audit PoC ──────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task VX14_AuditPoC_Listings_CarryNoKeyMaterial_LockedOrUnlocked()
        {
            var info = Create();
            var stored = Stored(info.Address);
            using var server = NewServer();
            var client = server.CreateClient();
            var routes = new[] { "/rsapi/RSV1/GetAllReserveAccounts", $"/rsapi/RSV1/GetReserveAccountInfo/{info.Address}" };

            foreach (var route in routes) // LOCKED
            {
                var body = await client.GetStringAsync(route);
                Assert.Contains(info.Address, body); // control: the account is listed
                AssertNoKeyMaterial(body, info.PrivateKey, stored);
            }

            var unlock = await client.GetStringAsync($"/rsapi/RSV1/UnlockReserveAccount/{info.Address}/0/{Uri.EscapeDataString(Pw)}");
            Assert.Contains("\"Success\":true", unlock);

            foreach (var route in routes) // UNLOCKED
            {
                var body = await client.GetStringAsync(route);
                Assert.Contains(info.Address, body);
                AssertNoKeyMaterial(body, info.PrivateKey, stored);
            }
        }

        [Fact]
        public void VX14_AuditPoC_ExpiredUnlock_ServesNoKey_EvenBeforeTheSweepRuns()
        {
            var info = Create();
            Globals.ReserveAccountUnlockKeys[info.Address] = new ReserveAccountUnlockKey
            {
                Password = Secure(Pw),
                DeleteAfterTime = TimeUtil.GetTime() - 1, // expired; the one-minute sweep has not run
                UnlockTimeHours = 0
            };

            var stored = Stored(info.Address);
            Assert.Equal("0", stored.GetKey);
            Assert.Null(stored.GetPrivKey);
        }

        [Fact]
        public async Task VX14_AuditPoC_UnlockWindow_IsRealAndReportedTruthfully()
        {
            var info = Create();
            using var server = NewServer();
            var body = await server.CreateClient().GetStringAsync($"/rsapi/RSV1/UnlockReserveAccount/{info.Address}/0/{Uri.EscapeDataString(Pw)}");

            Assert.Contains("\"Success\":true", body);
            Assert.DoesNotContain("0 minutes", body); // the audit saw "unlocked for 0 minutes"
            Assert.Contains("15 minutes", body);     // config default when no wallet unlock time is loaded
            Assert.Equal(info.PrivateKey, Stored(info.Address).GetKey); // and the unlock is actually usable
        }

        [Fact]
        public async Task VX14_Control_WrongPasswordAndBogusAddress_DoNotUnlock()
        {
            var info = Create();
            using var server = NewServer();
            var client = server.CreateClient();

            var wrong = await client.GetStringAsync($"/rsapi/RSV1/UnlockReserveAccount/{info.Address}/0/not-the-password");
            Assert.Contains("\"Success\":false", wrong);
            Assert.Equal("0", Stored(info.Address).GetKey);

            var bogus = await client.GetStringAsync("/rsapi/RSV1/GetReserveAccountInfo/xRBXnotanaddress");
            Assert.Contains("No Account Found", bogus);
        }

        // ── Expiry at access ───────────────────────────────────────────────────────────────

        [Fact]
        public void VX14_ExpiredUnlock_IsRemovedAndReportedLocked()
        {
            var info = Create();
            Globals.ReserveAccountUnlockKeys[info.Address] = new ReserveAccountUnlockKey { Password = Secure(Pw), DeleteAfterTime = TimeUtil.GetTime() - 1 };

            Assert.False(ReserveAccount.TryGetActiveUnlock(info.Address, out var unlock));
            Assert.Null(unlock);
            Assert.False(Globals.ReserveAccountUnlockKeys.ContainsKey(info.Address));
        }

        [Fact]
        public async Task VX14_ExpiredUnlock_DoesNotCountAsAlreadyUnlocked()
        {
            var info = Create();
            Globals.ReserveAccountUnlockKeys[info.Address] = new ReserveAccountUnlockKey { Password = Secure(Pw), DeleteAfterTime = TimeUtil.GetTime() - 1 };
            using var server = NewServer();
            var body = await server.CreateClient().GetStringAsync($"/rsapi/RSV1/UnlockReserveAccount/{info.Address}/0/{Uri.EscapeDataString(Pw)}");
            Assert.Contains("\"AlreadyUnlocked\":false", body);
            Assert.True(ReserveAccount.TryGetActiveUnlock(info.Address, out _));
        }

        [Fact]
        public async Task VX14_Listing_ReportsUnlockStateExplicitly()
        {
            var info = Create();
            using var server = NewServer();
            var client = server.CreateClient();
            JObject Account(string json) => (JObject)JObject.Parse(json)["ReserveAccount"]!;

            var locked = Account(await client.GetStringAsync($"/rsapi/RSV1/GetReserveAccountInfo/{info.Address}"));
            Assert.False(locked.Value<bool>("IsUnlocked"));

            await client.GetStringAsync($"/rsapi/RSV1/UnlockReserveAccount/{info.Address}/0/{Uri.EscapeDataString(Pw)}");
            var unlocked = Account(await client.GetStringAsync($"/rsapi/RSV1/GetReserveAccountInfo/{info.Address}"));
            Assert.True(unlocked.Value<bool>("IsUnlocked"));
            Assert.True(unlocked.Value<long>("UnlockExpiresAt") > TimeUtil.GetTime());
        }

        // ── Password wrap: KDF for new records, legacy records re-wrapped ──────────────────

        [Fact]
        public void VX14_NewReserveAccount_UsesKdfWrap()
        {
            var info = Create();
            var stored = Stored(info.Address);
            Assert.True(KeystoreCrypto.IsSealed(stored.EncryptedDecryptKey));
            Assert.Equal(info.PrivateKey, ReserveAccount.GetPrivateKey(info.Address, Pw));
            Assert.Null(ReserveAccount.GetPrivateKey(info.Address, "wrong"));
            Assert.Null(ReserveAccount.GetPrivateKey(info.Address, ""));
        }

        [Fact]
        public void VX14_LegacyReserveRecord_Opens_IsRewrapped_AndStillOpensAfterwards()
        {
            var (address, keyHex) = InsertLegacyReserve(Pw);
            Assert.False(KeystoreCrypto.IsSealed(ReserveAccount.GetReserveAccountSingle(address)!.EncryptedDecryptKey));

            Assert.Null(ReserveAccount.GetPrivateKey(address, "wrong")); // wrong password: no key, no rewrite
            Assert.False(KeystoreCrypto.IsSealed(ReserveAccount.GetReserveAccountSingle(address)!.EncryptedDecryptKey));

            Assert.Equal(keyHex, ReserveAccount.GetPrivateKey(address, Pw));
            var after = ReserveAccount.GetReserveAccountSingle(address)!;
            Assert.True(KeystoreCrypto.IsSealed(after.EncryptedDecryptKey));
            Assert.Equal(keyHex, ReserveAccount.GetPrivateKey(address, Pw));
            Assert.Null(ReserveAccount.GetPrivateKey(address, "wrong"));
        }

        [Fact]
        public async Task VX14_LegacyReserveRecord_UnlocksThroughTheRoute()
        {
            var (address, keyHex) = InsertLegacyReserve(Pw);
            using var server = NewServer();
            var body = await server.CreateClient().GetStringAsync($"/rsapi/RSV1/UnlockReserveAccount/{address}/0/{Uri.EscapeDataString(Pw)}");
            Assert.Contains("\"Success\":true", body);
            Assert.Equal(keyHex, ReserveAccount.GetReserveAccountSingle(address)!.GetKey);
            Assert.True(KeystoreCrypto.IsSealed(ReserveAccount.GetReserveAccountSingle(address)!.EncryptedDecryptKey));
        }

        [Fact]
        public void VX14_PasswordLongerThan32Bytes_Works()
        {
            // The legacy wrap could not use such a password; creation threw inside a retry-on-any-exception loop and hung.
            var longPw = new string('x', 40) + "-long";
            var info = Create(longPw);
            Assert.Equal(info.PrivateKey, ReserveAccount.GetPrivateKey(info.Address, longPw));
        }

        [Fact]
        public void VX14_EmptyPassword_IsRejectedUpFront()
        {
            Assert.Throws<ArgumentException>(() => ReserveAccount.CreateNewReserveAccount(""));
        }

        [Fact]
        public void VX14_LegacyEmptyPasswordRecord_StillOpens()
        {
            // The old create path accepted "" (zero key); such accounts must stay usable.
            var (address, keyHex) = InsertLegacyReserve("");
            Assert.Equal(keyHex, ReserveAccount.GetPrivateKey(address, ""));
            Assert.Null(ReserveAccount.GetPrivateKey(address, "x"));
        }

        [Fact]
        public void VX14_LegacyWrap_WrongPasswords_NeverOpen()
        {
            // CBC with a wrong key passes padding ~1/256 of the time; the data-key shape check must reject those.
            var dataKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var wrapped = LegacyCbc(dataKey, ZeroPadded(Pw));
            Assert.True(PasswordKeyWrap.TryUnwrap(wrapped, Pw, out var opened, out var legacy));
            Assert.Equal(dataKey, opened);
            Assert.True(legacy);
            for (int i = 0; i < 2000; i++)
                Assert.False(PasswordKeyWrap.TryUnwrap(wrapped, "guess-" + i, out _, out _));
        }

        // ── VFX wallet keystore (same weak wrap) ───────────────────────────────────────────

        [Fact]
        public async Task VX14_WalletEncryption_NewKeystores_UseKdfWrap()
        {
            var account = AccountData.CreateNewAccount(skipSave: true);
            var keyHex = account.PrivateKey;
            AccountData.GetAccounts().Insert(account);

            var result = await new V1Controller().GetEncryptWallet("wallet pw 14");
            Assert.Contains("Success", result);

            var keystores = Keystore.GetKeystore()!.FindAll().ToList();
            Assert.NotEmpty(keystores);
            Assert.All(keystores, k => Assert.True(KeystoreCrypto.IsSealed(k.Key)));
            Assert.Equal(keyHex, AccountData.GetAccounts().FindOne(x => x.Address == account.Address).GetKey);

            Globals.EncryptPassword = new SecureString(); // lock
            Assert.NotEqual(keyHex, AccountData.GetAccounts().FindOne(x => x.Address == account.Address).GetKey);
        }

        private static (string Address, string KeyHex) InsertLegacyWalletAccount(string walletPassword)
        {
            var account = AccountData.CreateNewAccount(skipSave: true);
            var keyHex = account.PrivateKey;
            var dataKey = RandomNumberGenerator.GetBytes(32);
            account.PrivateKey = LegacyCbc(keyHex, dataKey);
            AccountData.GetAccounts().Insert(account);
            Keystore.GetKeystore()!.Insert(new Keystore
            {
                Address = account.Address,
                PublicKey = account.PublicKey,
                PrivateKey = account.PrivateKey,
                Key = LegacyCbc(Convert.ToBase64String(dataKey), ZeroPadded(walletPassword)),
                IsUsed = true
            });
            return (account.Address, keyHex);
        }

        [Fact]
        public void VX14_LegacyWalletKeystore_OpensAndIsRewrappedOnFirstUse()
        {
            var (address, keyHex) = InsertLegacyWalletAccount("wallet pw 14");
            Globals.IsWalletEncrypted = true;
            Globals.EncryptPassword = Secure("wallet pw 14");

            Assert.Equal(keyHex, AccountData.GetAccounts().FindOne(x => x.Address == address).GetKey);
            Assert.True(KeystoreCrypto.IsSealed(Keystore.GetKeystore()!.FindOne(x => x.Address == address).Key));
            Assert.Equal(keyHex, AccountData.GetAccounts().FindOne(x => x.Address == address).GetKey);
        }

        [Fact]
        public void VX14_LegacyWalletKeystores_BulkRewrap_OnlyWithTheRightPassword()
        {
            var a = InsertLegacyWalletAccount("wallet pw 14");
            var b = InsertLegacyWalletAccount("wallet pw 14");
            Globals.IsWalletEncrypted = true;

            Globals.EncryptPassword = Secure("wrong password");
            Assert.Equal(0, WalletEncryptionService.RewrapLegacyKeystoresIfUnlocked());
            Assert.All(Keystore.GetKeystore()!.FindAll(), k => Assert.False(KeystoreCrypto.IsSealed(k.Key)));

            Globals.EncryptPassword = Secure("wallet pw 14");
            Assert.Equal(2, WalletEncryptionService.RewrapLegacyKeystoresIfUnlocked());
            Assert.All(Keystore.GetKeystore()!.FindAll(), k => Assert.True(KeystoreCrypto.IsSealed(k.Key)));
            Assert.Equal(a.KeyHex, AccountData.GetAccounts().FindOne(x => x.Address == a.Address).GetKey);
            Assert.Equal(b.KeyHex, AccountData.GetAccounts().FindOne(x => x.Address == b.Address).GetKey);
        }
    }
}
