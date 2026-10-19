using System;
using System.IO;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// After an operator reset a node refuses a genesis that takes effect before the reset: an old seed-signed genesis stays
    /// validly signed forever, and any peer could hand it back and re-arm the era the reset was meant to end.
    /// </summary>
    [Collection("DbContextSequential")]
    public class OperatorResetGenesisTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly long _priorCertHeight = Globals.CertEnforceHeight;

        public OperatorResetGenesisTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"reset_genesis_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            CasterMembershipStore.InvalidateCache();
        }

        public void Dispose()
        {
            Globals.LastBlock = _priorLastBlock;
            Globals.CertEnforceHeight = _priorCertHeight;
            CasterMembershipStore.InvalidateCache();
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            CasterMembershipStore.InvalidateCache();
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        [Fact]
        public void AfterAnOperatorReset_AGenesisFromBeforeIt_IsRefused_AndALaterOneIsJudgedAsBefore()
        {
            Assert.Equal(0, CasterMembershipStore.LastOperatorResetHeight());
            Globals.LastBlock = new Block { Height = 5_000 };
            CasterMembershipStore.ClearAllForOperatorReset();
            Assert.Equal(5_000, CasterMembershipStore.LastOperatorResetHeight());

            Assert.False(CasterMembershipStore.TryAppend(CasterMembershipStore.BuildGenesisRecord(4_000), out var stale));
            Assert.Contains("operator reset", stale);

            // A genesis from the coordinated restart after the reset goes on to the usual checks (here: no seed signatures).
            Assert.False(CasterMembershipStore.TryAppend(CasterMembershipStore.BuildGenesisRecord(5_001), out var fresh));
            Assert.DoesNotContain("operator reset", fresh);
        }
    }
}
