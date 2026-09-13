using System;
using System.Collections.Generic;
using System.IO;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Follow-up hardening: signed-share evidence must outlive the in-memory tracker (24h expiry,
    /// lost on restart). A signed Bitcoin transaction never stops being broadcastable, so the
    /// cancellation guard and the "different tx for the same withdrawal" guard read a durable record.
    /// </summary>
    [Collection("DbContextSequential")]
    public class FrostSignedEvidenceTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;

        public FrostSignedEvidenceTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"signedev_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            FrostWithdrawalSigningTracker.ResetForTests();
        }

        public void Dispose()
        {
            FrostWithdrawalSigningTracker.ResetForTests();
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        [Fact]
        public void Record_Get_Upsert_Delete()
        {
            Assert.Null(FrostSignedWithdrawalEvidence.Get("sc", "wrh"));
            Assert.True(FrostSignedWithdrawalEvidence.Record("sc", "wrh", "AABB", new[] { "Tx1:0", "tx2:1" }));

            var e = FrostSignedWithdrawalEvidence.Get("sc", "wrh");
            Assert.NotNull(e);
            Assert.Equal("aabb", e!.BtcTxId);
            Assert.Equal(new List<string> { "tx1:0", "tx2:1" }, e.Outpoints);

            Assert.True(FrostSignedWithdrawalEvidence.Record("sc", "wrh", "ccdd", new[] { "tx3:0" }));
            Assert.Equal("ccdd", FrostSignedWithdrawalEvidence.Get("sc", "wrh")!.BtcTxId);

            Assert.True(FrostSignedWithdrawalEvidence.Delete("sc", "wrh"));
            Assert.Null(FrostSignedWithdrawalEvidence.Get("sc", "wrh"));
        }

        [Fact]
        public void TrackerCompletion_WritesDurableEvidence_ThatSurvivesTrackerReset()
        {
            FrostWithdrawalSigningTracker.RecordSigningStarted("sc", "wrh", "s0", 0, "aa11", new List<string> { "aa11" });
            FrostWithdrawalSigningTracker.RecordSigningCompleted("sc", "wrh", "s0", 0, "aa11", new List<string> { "txid:0" }, "btc-txid");
            Assert.True(FrostWithdrawalSigningTracker.HasSignedTransaction("sc", "wrh"));

            // Simulate restart / 24h expiry of the in-memory tracker.
            FrostWithdrawalSigningTracker.ResetForTests();
            Assert.True(FrostWithdrawalSigningTracker.HasSignedTransaction("sc", "wrh"));   // durable
            Assert.False(FrostWithdrawalSigningTracker.HasSignedTransaction("sc", "other"));
        }

        [Fact]
        public void EmptyKeys_AreIgnored()
        {
            Assert.False(FrostSignedWithdrawalEvidence.Record("", "wrh", "x", null));
            Assert.False(FrostSignedWithdrawalEvidence.Record("sc", "", "x", null));
            Assert.Null(FrostSignedWithdrawalEvidence.Get("", ""));
        }
    }
}
