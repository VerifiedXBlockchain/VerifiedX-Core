using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.FROST.Models;
using VerifiedXCore.Data;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A peer's encrypted key-share backup exists for the day its owner's machine is gone. The periodic purge deletes an
    /// owner's backups only once the owner has been missing from the active validators for 30 days; an owner that comes
    /// back within that time keeps them.
    /// </summary>
    [Collection("DbContextSequential")]
    public class FrostPeerBackupGraceTests : IDisposable
    {
        private const long Day = 24 * 60 * 60;
        private const long T0 = 1_790_000_000;
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;

        public FrostPeerBackupGraceTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"peerbackup_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static void Backup(string owner, string contract) =>
            Assert.True(FrostPeerKeyBackup.SaveBackup(new FrostPeerKeyBackup
            {
                OwnerAddress = owner, SmartContractUID = contract, EncryptedBlob = "blob", PlaintextHash = "h", GroupPublicKey = "g-" + contract, StoredTimestamp = T0,
            }));

        [Fact]
        public void AnOwnerMissingForADay_KeepsItsBackups_OneMissingForAMonth_LosesThem()
        {
            Backup("xGone", "vault-1");
            Backup("xGone", "vault-2");
            Backup("xHere", "vault-1");
            var active = new List<string> { "xHere" };

            // The first sweep that misses the owner only stamps it.
            Assert.Equal(0, FrostPeerKeyBackup.DeleteStaleBackups(active, T0));
            Assert.Equal(3, FrostPeerKeyBackup.GetBackupCount());
            Assert.All(FrostPeerKeyBackup.GetBackupsForOwner("xGone"), b => Assert.Equal(T0, b.MissingSince));
            Assert.All(FrostPeerKeyBackup.GetBackupsForOwner("xHere"), b => Assert.Equal(0, b.MissingSince));

            // A day later (what the old purge deleted at), and at the grace itself: kept.
            Assert.Equal(0, FrostPeerKeyBackup.DeleteStaleBackups(active, T0 + Day));
            Assert.Equal(0, FrostPeerKeyBackup.DeleteStaleBackups(active, T0 + FrostPeerKeyBackup.StaleGraceSeconds));
            Assert.Equal(2, FrostPeerKeyBackup.GetBackupsForOwner("xGone").Count);

            // Missing for 31 days: deleted; the active owner's are untouched.
            Assert.Equal(2, FrostPeerKeyBackup.DeleteStaleBackups(active, T0 + 31 * Day));
            Assert.Empty(FrostPeerKeyBackup.GetBackupsForOwner("xGone"));
            Assert.Single(FrostPeerKeyBackup.GetBackupsForOwner("xHere"));
        }

        [Fact]
        public void AnOwnerThatComesBack_ClearsItsStamp_AndAFreshAbsenceStartsTheGraceAgain()
        {
            Backup("xFlaky", "vault-1");
            FrostPeerKeyBackup.DeleteStaleBackups(new List<string>(), T0);
            Assert.Equal(T0, FrostPeerKeyBackup.GetBackupsForOwner("xFlaky").Single().MissingSince);

            FrostPeerKeyBackup.DeleteStaleBackups(new List<string> { "xFlaky" }, T0 + 20 * Day);
            Assert.Equal(0, FrostPeerKeyBackup.GetBackupsForOwner("xFlaky").Single().MissingSince);

            // Gone again on day 25: its 30 days count from then, not from the first absence.
            FrostPeerKeyBackup.DeleteStaleBackups(new List<string>(), T0 + 25 * Day);
            Assert.Equal(0, FrostPeerKeyBackup.DeleteStaleBackups(new List<string>(), T0 + 40 * Day));
            Assert.Single(FrostPeerKeyBackup.GetBackupsForOwner("xFlaky"));
            Assert.Equal(1, FrostPeerKeyBackup.DeleteStaleBackups(new List<string>(), T0 + 56 * Day));
        }
    }
}
