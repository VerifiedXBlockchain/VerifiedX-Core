using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.FROST;
using VerifiedXCore.Bitcoin.FROST.Models;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.Privacy;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// macOS compatibility (owner feedback, also in the short audit PDF): KeystoreCrypto (VX-13/VX-14: wallet encryption,
    /// Bitcoin key sealing, reserve wraps, Mother verifier), FrostShareCrypto (NEW-19) and FrostKeyBackupService called
    /// System.Security.Cryptography.AesGcm directly, which is not supported on macOS under .NET 6. They now go through
    /// CrossPlatformAesGcm, which falls back to BouncyCastle there. These tests force the fallback (the path a Mac runs) and
    /// check that it interoperates both ways with the native path, so records sealed on one platform open on the other.
    /// </summary>
    [Collection("DbContextSequential")]
    public class MacCompat_AesGcmFallbackTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;

        public MacCompat_AesGcmFallbackTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"macaes_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
        }

        public void Dispose()
        {
            CrossPlatformAesGcm.ForceManaged = false;
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static T With<T>(bool managed, Func<T> f)
        {
            CrossPlatformAesGcm.ForceManaged = managed;
            try { return f(); } finally { CrossPlatformAesGcm.ForceManaged = false; }
        }

        [Theory]
        [InlineData(false, true)]   // sealed natively (Windows/Linux), opened on a Mac
        [InlineData(true, false)]   // sealed on a Mac, opened natively
        [InlineData(true, true)]    // Mac only
        public void Keystore_SealAndOpen_AcrossBothPaths(bool sealManaged, bool openManaged)
        {
            var sealedValue = With(sealManaged, () => KeystoreCrypto.Seal("secret-key-material", "correct horse"));
            Assert.True(With(openManaged, () => KeystoreCrypto.TryOpen(sealedValue, "correct horse", out var pt) && pt == "secret-key-material"));
            Assert.False(With(openManaged, () => KeystoreCrypto.TryOpen(sealedValue, "wrong password", out _)));
        }

        [Fact]
        public void Keystore_TamperedRecord_RefusedOnTheManagedPath()
        {
            var sealedValue = KeystoreCrypto.Seal("secret-key-material", "pw");
            var blob = Convert.FromBase64String(sealedValue.Substring(KeystoreCrypto.V1Prefix.Length));
            blob[^1] ^= 0x01;
            var tampered = KeystoreCrypto.V1Prefix + Convert.ToBase64String(blob);
            Assert.False(With(true, () => KeystoreCrypto.TryOpen(tampered, "pw", out _)));
        }

        [Theory]
        [InlineData(false, true)]
        [InlineData(true, false)]
        public void FrostShares_SealAndOpen_AcrossBothPaths(bool sealManaged, bool openManaged)
        {
            var recipient = new NBitcoin.Key();
            var pubHex = Convert.ToHexString(recipient.PubKey.Decompress().ToBytes()).ToLowerInvariant();
            var id = new string('0', 63) + "1";
            var sealedPackage = With(sealManaged, () => FrostShareCrypto.Seal("{\"signing_share\":\"ab\"}", pubHex, "sess", id));
            Assert.True(With(openManaged, () => FrostShareCrypto.TryOpen(sealedPackage, recipient.ToBytes(), "sess", id, out var pkg) && pkg.Contains("signing_share")));
            Assert.False(With(openManaged, () => FrostShareCrypto.TryOpen(sealedPackage, recipient.ToBytes(), "other-session", id, out _)));   // AAD bound
        }

        [Theory]
        [InlineData(false, true)]
        [InlineData(true, false)]
        public void FrostKeyBackup_EncryptAndDecrypt_AcrossBothPaths(bool encryptManaged, bool decryptManaged)
        {
            var account = AccountData.CreateNewAccount(skipSave: true);
            AccountData.GetAccounts().Insert(account);
            var store = new FrostValidatorKeyStore { KeyPackage = "{\"k\":1}", PubkeyPackage = "{\"p\":1}", GroupPublicKey = "02" + new string('a', 64), ParticipantOrderJson = "[]" };
            var (blob, _) = With(encryptManaged, () => FrostKeyBackupService.EncryptKeyPackage(account.Address, "uid:1", store));
            Assert.NotNull(blob);
            var opened = With(decryptManaged, () => FrostKeyBackupService.DecryptKeyPackage(account.Address, "uid:1", blob!));
            Assert.NotNull(opened);
            Assert.Null(With(decryptManaged, () => FrostKeyBackupService.DecryptKeyPackage(account.Address, "uid:2", blob!)));     // other contract
        }

        [Fact]
        public void ManagedPath_ReportsAFailedTagCheckAsCryptographicException()
        {
            var key = RandomNumberGenerator.GetBytes(32);
            var nonce = RandomNumberGenerator.GetBytes(12);
            var ct = new byte[4]; var tag = new byte[16];
            CrossPlatformAesGcm.Encrypt(key, nonce, new byte[] { 1, 2, 3, 4 }, ct, tag, null);
            tag[0] ^= 1;
            CrossPlatformAesGcm.ForceManaged = true;
            Assert.Throws<CryptographicException>(() => CrossPlatformAesGcm.Decrypt(key, nonce, ct, tag, new byte[4], null));
        }

        [Fact]
        public void NoDirectAesGcmOutsideTheCrossPlatformHelper()
        {
            var root = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(ThisFile()))!, "VerifiedXCore");
            var offenders = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
                .Where(f => !f.EndsWith("CrossPlatformAesGcm.cs") && File.ReadAllText(f).Contains("new AesGcm("))
                .ToList();
            Assert.True(offenders.Count == 0, "AesGcm used directly (fails on macOS / .NET 6): " + string.Join(", ", offenders));
        }

        private static string ThisFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
    }
}
