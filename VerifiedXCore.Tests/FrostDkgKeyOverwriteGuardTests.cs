using System;
using System.IO;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.FROST;
using VerifiedXCore.Bitcoin.FROST.Models;
using VerifiedXCore.Data;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Security regression: a DKG for a contract ID that already has a vault key must be refused,
    /// and neither the key store nor the peer-backup store may ever replace a record holding one
    /// group public key with a record for a different one. Before this guard, a permissionless DKG
    /// start could overwrite the real key everywhere and permanently lock the BTC collateral.
    /// </summary>
    [Collection("DbContextSequential")]
    public class FrostDkgKeyOverwriteGuardTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly bool _priorSynced;
        private const string Me = "xValidatorMe";

        public FrostDkgKeyOverwriteGuardTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"dkgguard_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorSynced = Globals.IsChainSynced;
            Globals.IsChainSynced = true;
            DbContext.Initialize();
        }

        public void Dispose()
        {
            Globals.IsChainSynced = _priorSynced;
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        [Fact]
        public void CanStartDkg_RefusedWhileChainNotSynced()
        {
            Globals.IsChainSynced = false;
            var (ok, reason) = FrostDkgGuard.CanStartDkg("sc-any", Me);
            Assert.False(ok);
            Assert.Contains("not synced", reason);
        }

        private static FrostValidatorKeyStore Key(string sc, string group, string pkg = "keypkg") => new FrostValidatorKeyStore
        {
            SmartContractUID = sc,
            ValidatorAddress = Me,
            KeyPackage = pkg,
            PubkeyPackage = "pub",
            GroupPublicKey = group,
            CreatedTimestamp = TimeUtil.GetTime(),
        };

        // ── Pure replacement rules ──────────────────────────────────────────────

        [Fact]
        public void KeyStore_CanReplace_RefusesDifferentGroupKey_AllowsSameOrEmptyExisting()
        {
            var existing = Key("sc", "GROUP-A");
            Assert.False(FrostValidatorKeyStore.CanReplace(existing, Key("sc", "GROUP-B"), out var why));
            Assert.Contains("GROUP-A", why);

            Assert.True(FrostValidatorKeyStore.CanReplace(existing, Key("sc", "group-a"), out _)); // case-insensitive same key
            Assert.True(FrostValidatorKeyStore.CanReplace(Key("sc", ""), Key("sc", "GROUP-B"), out _)); // no prior group key
            Assert.True(FrostValidatorKeyStore.CanReplace(Key("sc", "GROUP-A", pkg: ""), Key("sc", "GROUP-B"), out _)); // no prior key material
        }

        [Fact]
        public void Backup_CanReplace_RefusesDifferentGroupKey()
        {
            var existing = new FrostPeerKeyBackup { OwnerAddress = Me, SmartContractUID = "sc", GroupPublicKey = "GROUP-A" };
            Assert.False(FrostPeerKeyBackup.CanReplace(existing, new FrostPeerKeyBackup { GroupPublicKey = "GROUP-B" }, out _));
            Assert.True(FrostPeerKeyBackup.CanReplace(existing, new FrostPeerKeyBackup { GroupPublicKey = "GROUP-A" }, out _));
            Assert.True(FrostPeerKeyBackup.CanReplace(new FrostPeerKeyBackup { GroupPublicKey = "" }, new FrostPeerKeyBackup { GroupPublicKey = "GROUP-B" }, out _));
        }

        // ── Persisted behaviour ─────────────────────────────────────────────────

        [Fact]
        public void SaveKeyPackage_RefusesToOverwriteDifferentGroupKey_OriginalIntact()
        {
            Assert.True(FrostValidatorKeyStore.SaveKeyPackage(Key("sc-1", "GROUP-A", "pkg-A")));
            Assert.False(FrostValidatorKeyStore.SaveKeyPackage(Key("sc-1", "GROUP-B", "pkg-B")));

            var stored = FrostValidatorKeyStore.GetKeyPackage("sc-1", Me);
            Assert.NotNull(stored);
            Assert.Equal("GROUP-A", stored!.GroupPublicKey);
            Assert.Equal("pkg-A", stored.KeyPackage);
        }

        [Fact]
        public void SaveKeyPackage_AllowsIdempotentResaveOfSameGroupKey()
        {
            Assert.True(FrostValidatorKeyStore.SaveKeyPackage(Key("sc-2", "GROUP-A", "pkg-A")));
            var again = Key("sc-2", "GROUP-A", "pkg-A");
            again.ParticipantOrderJson = "[\"a\",\"b\"]";
            Assert.True(FrostValidatorKeyStore.SaveKeyPackage(again));
            Assert.Equal("[\"a\",\"b\"]", FrostValidatorKeyStore.GetKeyPackage("sc-2", Me)!.ParticipantOrderJson);
        }

        [Fact]
        public void SaveBackup_RefusesToOverwriteDifferentGroupKey_OriginalIntact()
        {
            Assert.True(FrostPeerKeyBackup.SaveBackup(new FrostPeerKeyBackup { OwnerAddress = Me, SmartContractUID = "sc-3", EncryptedBlob = "blob-A", GroupPublicKey = "GROUP-A", StoredTimestamp = 1 }));
            Assert.False(FrostPeerKeyBackup.SaveBackup(new FrostPeerKeyBackup { OwnerAddress = Me, SmartContractUID = "sc-3", EncryptedBlob = "blob-B", GroupPublicKey = "GROUP-B", StoredTimestamp = 2 }));

            var stored = FrostPeerKeyBackup.GetBackup(Me, "sc-3");
            Assert.NotNull(stored);
            Assert.Equal("blob-A", stored!.EncryptedBlob);
        }

        [Fact]
        public void CanStartDkg_RefusedWhenLocalKeyExists_AllowedWhenNone()
        {
            Assert.True(FrostDkgGuard.CanStartDkg("sc-new", Me).Ok);

            Assert.True(FrostValidatorKeyStore.SaveKeyPackage(Key("sc-existing", "GROUP-A")));
            var (ok, reason) = FrostDkgGuard.CanStartDkg("sc-existing", Me);
            Assert.False(ok);
            Assert.Contains("re-key", reason);
        }

        [Fact]
        public void CanStartDkg_RefusedForEmptyContract()
        {
            Assert.False(FrostDkgGuard.CanStartDkg("", Me).Ok);
            Assert.False(FrostDkgGuard.CanStartDkg(null, Me).Ok);
        }
    }
}
