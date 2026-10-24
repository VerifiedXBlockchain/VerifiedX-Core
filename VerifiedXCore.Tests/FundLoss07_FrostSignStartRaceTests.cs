using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VerifiedXCore.Bitcoin.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Fund-loss audit item 7 (validator-local): two sign/starts for one withdrawal input with different sighashes
    /// could both pass the tracker's check before either was recorded (separate lock acquisitions), and round 2
    /// re-checked nothing, so one withdrawal got two payable Bitcoin transactions. TryBeginSigning checks and records
    /// under one lock; ConfirmSessionHoldsInput is round 2's re-check.
    /// </summary>
    [Collection("DbContextSequential")]
    public class FundLoss07_FrostSignStartRaceTests : IDisposable
    {
        private const string ScUID = "vbtc-contract-race";
        private const string Wrh = "withdrawal-race-1";
        private const string SighashA = "aa11";
        private const string SighashB = "bb22";

        public FundLoss07_FrostSignStartRaceTests() => FrostWithdrawalSigningTracker.ResetForTests();
        public void Dispose() => FrostWithdrawalSigningTracker.ResetForTests();

        [Fact]
        public void SecondStart_WithADifferentSighash_IsRefusedAtRecordTime()
        {
            var (b1, r1) = FrostWithdrawalSigningTracker.TryBeginSigning(ScUID, Wrh, "s-A", 0, SighashA, new List<string> { SighashA });
            Assert.False(b1, r1);
            var (b2, r2) = FrostWithdrawalSigningTracker.TryBeginSigning(ScUID, Wrh, "s-B", 0, SighashB, new List<string> { SighashB });
            Assert.True(b2);
            Assert.Contains("a withdrawal signs exactly one Bitcoin transaction", r2); // the announced-sighash pin refuses it first

            // Only the winner is the ceremony round 2 will serve.
            Assert.True(FrostWithdrawalSigningTracker.ConfirmSessionHoldsInput(ScUID, Wrh, 0, "s-A", SighashA).Ok);
            var (okB, reasonB) = FrostWithdrawalSigningTracker.ConfirmSessionHoldsInput(ScUID, Wrh, 0, "s-B", SighashB);
            Assert.False(okB);
            Assert.Contains("is not the one", reasonB);
        }

        [Fact]
        public async Task ConcurrentStarts_ExactlyOneWins_EveryTime()
        {
            for (var round = 0; round < 50; round++)
            {
                FrostWithdrawalSigningTracker.ResetForTests();
                var wrh = $"{Wrh}-{round}";
                using var gate = new ManualResetEventSlim(false);
                var results = new (bool Blocked, string Reason)[8];
                var tasks = Enumerable.Range(0, results.Length).Select(i => Task.Run(() =>
                {
                    gate.Wait();
                    var sighash = $"{i:x2}{i:x2}";
                    results[i] = FrostWithdrawalSigningTracker.TryBeginSigning(ScUID, wrh, $"s-{i}", 0, sighash, new List<string> { sighash });
                })).ToArray();
                gate.Set();
                await Task.WhenAll(tasks);
                Assert.Equal(1, results.Count(r => !r.Blocked));

                var winner = Array.FindIndex(results, r => !r.Blocked);
                Assert.True(FrostWithdrawalSigningTracker.ConfirmSessionHoldsInput(ScUID, wrh, 0, $"s-{winner}", $"{winner:x2}{winner:x2}").Ok);
                for (var i = 0; i < results.Length; i++)
                    if (i != winner)
                        Assert.False(FrostWithdrawalSigningTracker.ConfirmSessionHoldsInput(ScUID, wrh, 0, $"s-{i}", $"{i:x2}{i:x2}").Ok);
            }
        }

        [Fact]
        public void Round2_RefusesASessionTheTrackerNeverAccepted()
        {
            var (ok, reason) = FrostWithdrawalSigningTracker.ConfirmSessionHoldsInput(ScUID, Wrh, 0, "s-ghost", SighashA);
            Assert.False(ok);
            Assert.Contains("No signing record", reason);

            FrostWithdrawalSigningTracker.TryBeginSigning(ScUID, Wrh, "s-A", 0, SighashA, new List<string> { SighashA, SighashB });
            Assert.False(FrostWithdrawalSigningTracker.ConfirmSessionHoldsInput(ScUID, Wrh, 1, "s-A", SighashB).Ok); // input 1 never started
            Assert.False(FrostWithdrawalSigningTracker.ConfirmSessionHoldsInput(ScUID, Wrh, 0, "s-A", SighashB).Ok); // wrong sighash for input 0
        }

        [Fact]
        public void IdempotentResignOfTheSameTransaction_StillWorks()
        {
            // A coordinator-side failure after share generation must stay retryable (the FIND-028 design).
            Assert.False(FrostWithdrawalSigningTracker.TryBeginSigning(ScUID, Wrh, "s-1", 0, SighashA, new List<string> { SighashA }).Blocked);
            FrostWithdrawalSigningTracker.RecordSigningCompleted(ScUID, Wrh, "s-1", 0, SighashA);
            Assert.False(FrostWithdrawalSigningTracker.TryBeginSigning(ScUID, Wrh, "s-2", 0, SighashA, new List<string> { SighashA }).Blocked);
            Assert.True(FrostWithdrawalSigningTracker.ConfirmSessionHoldsInput(ScUID, Wrh, 0, "s-2", SighashA).Ok);
            Assert.True(FrostWithdrawalSigningTracker.TryBeginSigning(ScUID, Wrh, "s-3", 0, SighashB, new List<string> { SighashB }).Blocked);
        }

        [Fact]
        public void NonWithdrawalSigning_IsUntouched()
        {
            Assert.False(FrostWithdrawalSigningTracker.TryBeginSigning(ScUID, "", "s-x", 0, SighashA).Blocked);
            Assert.True(FrostWithdrawalSigningTracker.ConfirmSessionHoldsInput(ScUID, "", 0, "s-x", SighashA).Ok);
            Assert.True(FrostWithdrawalSigningTracker.ConfirmSessionHoldsInput(null, null, 0, "s-x", SighashA).Ok);
        }
    }
}
