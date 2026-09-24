using System;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Threading.Tasks;
using LiteDB;
using VerifiedXCore;
using VerifiedXCore.Controllers;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// NEW-01 (found during remediation; not in the audit): LiteDB persisted the computed Account.GetKey /
    /// GetPrivKey accessors. GetKey decrypts while the wallet is unlocked, so an ordinary account update
    /// on an encrypted, unlocked wallet wrote the plaintext private key to the wallet database.
    /// </summary>
    [Collection("DbContextSequential")]
    public class NEW01_PlaintextKeyPersistenceTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly bool _priorEncrypted = Globals.IsWalletEncrypted;
        private readonly SecureString _priorPassword = Globals.EncryptPassword;

        public NEW01_PlaintextKeyPersistenceTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"new01_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            Globals.IsWalletEncrypted = false;
            Globals.EncryptPassword = new SecureString();
            DbContext.Initialize();
        }

        public void Dispose()
        {
            Globals.IsWalletEncrypted = _priorEncrypted;
            Globals.EncryptPassword = _priorPassword;
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static string WalletPath => GetPathUtility.GetDatabasePath() + DbContext.RSRV_DB_WALLET_NAME;

        private static BsonDocument RawAccountDoc() => DbContext.DB_Wallet.GetCollection(DbContext.RSRV_ACCOUNTS).FindAll().First();

        private static bool FileContains(string path, string text)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            return Encoding.UTF8.GetString(ms.ToArray()).Contains(text);
        }

        [Fact]
        public async Task NEW01_EncryptedWallet_UpdatedWhileUnlocked_StoresNoPlaintextKey()
        {
            var account = AccountData.CreateNewAccount(skipSave: true);
            var plaintext = account.PrivateKey;
            AccountData.GetAccounts().Insert(account);

            await new V1Controller().GetEncryptWallet("correct horse 12"); // encrypt; password stays in memory

            var stored = AccountData.GetAccounts().FindOne(x => x.Address == account.Address);
            stored.Balance = 7M;
            AccountData.GetAccounts().Update(stored); // an ordinary update while unlocked

            var doc = RawAccountDoc();
            Assert.False(doc.ContainsKey("GetKey"));
            Assert.False(doc.ContainsKey("GetPrivKey"));
            Assert.DoesNotContain(plaintext, doc.ToString());

            DbContext.DB_Wallet.Checkpoint();
            Assert.False(FileContains(WalletPath, plaintext));

            // The accessor itself still works for signing while unlocked.
            Assert.Equal(plaintext, AccountData.GetAccounts().FindOne(x => x.Address == account.Address).GetKey);
        }

        [Fact]
        public void NEW01_Scrub_RemovesFieldsWrittenByOlderBuilds_AndLeavesNoCopy()
        {
            const string leaked = "6c9b193adace3ea9b35539d63e7964540d1bcd9edf370579e6eb5a33f95d2752";
            var col = DbContext.DB_Wallet.GetCollection(DbContext.RSRV_ACCOUNTS);
            col.Insert(new BsonDocument
            {
                ["PrivateKey"] = "hDfgqAGWBMutp1W5sonM9KItndtOmQIC0QPK",
                ["Address"] = "xScrubTest",
                ["GetKey"] = leaked,
                ["GetPrivKey"] = new BsonDocument { ["secret"] = "1" },
            });
            DbContext.DB_Wallet.Checkpoint();
            Assert.True(FileContains(WalletPath, leaked)); // what an older build left on disk

            var fixedCount = KeyPersistenceScrub.Run(DbContext.DB_Wallet, WalletPath);

            Assert.Equal(1, fixedCount);
            Assert.False(RawAccountDoc().ContainsKey("GetKey"));
            Assert.False(FileContains(WalletPath, leaked));
            var backup = Path.Combine(Path.GetDirectoryName(WalletPath)!, Path.GetFileNameWithoutExtension(WalletPath) + "-backup" + Path.GetExtension(WalletPath));
            Assert.False(File.Exists(backup));
            Assert.Equal(0, KeyPersistenceScrub.Run(DbContext.DB_Wallet, WalletPath)); // idempotent
        }
    }
}
