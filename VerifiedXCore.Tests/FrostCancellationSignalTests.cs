using System;
using System.IO;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.FROST;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Data;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Follow-up hardening: the "cancellation in progress" signal must come from the consensus
    /// cancellation record. The cancel tx marks Cancellation_Requested on the owner's LOCAL contract
    /// record, never on the consensus withdrawal row, so the row-status check alone was inert on
    /// validators.
    /// </summary>
    [Collection("DbContextSequential")]
    public class FrostCancellationSignalTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;

        public FrostCancellationSignalTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"cancelsig_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        [Fact]
        public void PendingCancellation_DetectedFromConsensusRecord_NotRowStatus()
        {
            var row = new VBTCWithdrawalRequest { SmartContractUID = "sc", RequestorAddress = "xO", BTCDestination = "tb1q066af78la3rqmnchc396keujllva6turs52749", Amount = 0.01M, TransactionHash = "wrh-1", Status = VBTCWithdrawalStatus.Requested };

            Assert.True(FrostSigningAuthorization.IsWithdrawalSignable(row, "sc", hasPendingCancellation: false).Ok);
            var (ok, reason) = FrostSigningAuthorization.IsWithdrawalSignable(row, "sc", hasPendingCancellation: true);
            Assert.False(ok);
            Assert.Contains("cancellation vote in progress", reason);
        }

        [Fact]
        public void HasPendingCancellation_TrueOnlyForUnprocessedRecords()
        {
            VBTCWithdrawalCancellation.SaveCancellation(new VBTCWithdrawalCancellation { CancellationUID = "c-old", SmartContractUID = "sc", OwnerAddress = "xO", WithdrawalRequestHash = "wrh-2", IsProcessed = true, IsApproved = false, ValidatorVotes = new() });
            Assert.False(VBTCWithdrawalCancellation.HasPendingCancellation("wrh-2"));

            VBTCWithdrawalCancellation.SaveCancellation(new VBTCWithdrawalCancellation { CancellationUID = "c-new", SmartContractUID = "sc", OwnerAddress = "xO", WithdrawalRequestHash = "wrh-2", IsProcessed = false, ValidatorVotes = new() });
            Assert.True(VBTCWithdrawalCancellation.HasPendingCancellation("wrh-2"));

            Assert.False(VBTCWithdrawalCancellation.HasPendingCancellation("wrh-none"));
            Assert.False(VBTCWithdrawalCancellation.HasPendingCancellation(""));
        }
    }
}
