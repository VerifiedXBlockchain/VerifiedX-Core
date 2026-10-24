using System;
using System.Collections.Generic;
using System.IO;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A block committed with some state writes not applied leaves the state missing those writes until a rebuild or a
    /// snapshot restore: probation's clean-block streak does not mark it synced. A state flagged unsynced for another
    /// reason still passes probation after the streak.
    /// </summary>
    [Collection("DbContextSequential")]
    public class ProbationIncompleteApplyTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;

        public ProbationIncompleteApplyTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"probation_incomplete_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            BlockValidatorService.ResetProbationForTests();
        }

        public void Dispose()
        {
            BlockValidatorService.ResetProbationForTests();
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static void CleanBlocks(long from, int count)
        {
            for (var h = from; h < from + count; h++)
                BlockValidatorService.AdvanceProbation(new Block { Height = h, Transactions = new List<Transaction>() }, stateApplied: true);
        }

        [Fact]
        public void AnIncompleteApply_IsNotClearedByCleanBlocks()
        {
            StateTreiStatusService.SetIncompleteApply("Incomplete state application at block 10.");
            CleanBlocks(11, BlockValidatorService.STATE_PROBATION_BLOCKS * 2);
            Assert.False(StateTreiStatusService.IsSynced());
            Assert.True(StateTreiStatusService.NeedsRepair());

            StateTreiStatusService.SetFailed("a later, unrelated failure");
            Assert.True(StateTreiStatusService.NeedsRepair()); // a later failure does not repair it
        }

        [Fact]
        public void ARebuildOrASyncedState_ClearsIt()
        {
            StateTreiStatusService.SetIncompleteApply("Incomplete state application at block 10.");
            StateTreiStatusService.SetRebuildStarted();
            Assert.False(StateTreiStatusService.NeedsRepair());

            StateTreiStatusService.SetIncompleteApply("Incomplete state application at block 20.");
            StateTreiStatusService.SetSynced(30);
            Assert.False(StateTreiStatusService.NeedsRepair());
        }

        [Fact]
        public void AnotherFailure_StillPassesProbationAfterTheStreak()
        {
            StateTreiStatusService.SetFailed("Unverified after a restart.");
            CleanBlocks(11, BlockValidatorService.STATE_PROBATION_BLOCKS);
            Assert.True(StateTreiStatusService.IsSynced());
        }
    }
}
