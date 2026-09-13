using System.Collections.Generic;
using VerifiedXCore.Bitcoin.FROST;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Security regression: "sign, cancel, then broadcast the BTC". vBTC is only burned when the
    /// completion transaction lands, so a requester who gets the BTC transaction signed, then gets a
    /// cancellation approved, keeps the vBTC and still receives the BTC. Two independent guards:
    /// validators refuse to SIGN while a cancellation is pending, and refuse to APPROVE a
    /// cancellation once they have signed (or a signed/pinned tx exists).
    /// </summary>
    [Collection("DbContextSequential")]
    public class FrostCancelAfterSignTests : System.IDisposable
    {
        private const string Sc = "sc-cancel";
        private const string Wrh = "wrh-cancel-1";

        public FrostCancelAfterSignTests() { FrostWithdrawalSigningTracker.ResetForTests(); }
        public void Dispose() { FrostWithdrawalSigningTracker.ResetForTests(); }

        private static VBTCWithdrawalRequest Row(VBTCWithdrawalStatus status, bool completed = false) => new VBTCWithdrawalRequest
        {
            SmartContractUID = Sc,
            RequestorAddress = "xOwner",
            BTCDestination = "tb1q066af78la3rqmnchc396keujllva6turs52749",
            Amount = 0.01M,
            TransactionHash = Wrh,
            Status = status,
            IsCompleted = completed,
        };

        // ── Signing side ────────────────────────────────────────────────────────

        [Fact]
        public void Signable_OnlyWhilePlainlyRequested()
        {
            Assert.True(FrostSigningAuthorization.IsWithdrawalSignable(Row(VBTCWithdrawalStatus.Requested), Sc).Ok);
            Assert.True(FrostSigningAuthorization.IsWithdrawalSignable(Row(VBTCWithdrawalStatus.Pending_BTC), Sc).Ok);

            var (ok, reason) = FrostSigningAuthorization.IsWithdrawalSignable(Row(VBTCWithdrawalStatus.Cancellation_Requested), Sc);
            Assert.False(ok);
            Assert.Contains("cancellation vote in progress", reason);

            Assert.False(FrostSigningAuthorization.IsWithdrawalSignable(Row(VBTCWithdrawalStatus.Cancelled), Sc).Ok);
            Assert.False(FrostSigningAuthorization.IsWithdrawalSignable(Row(VBTCWithdrawalStatus.Completed), Sc).Ok);
            Assert.False(FrostSigningAuthorization.IsWithdrawalSignable(Row(VBTCWithdrawalStatus.Requested, completed: true), Sc).Ok);
        }

        [Fact]
        public void Signable_RefusesMissingRow_AndForeignContract()
        {
            Assert.False(FrostSigningAuthorization.IsWithdrawalSignable(null, Sc).Ok);
            Assert.False(FrostSigningAuthorization.IsWithdrawalSignable(Row(VBTCWithdrawalStatus.Requested), "other-contract").Ok);
        }

        // ── Tracker evidence ────────────────────────────────────────────────────

        [Fact]
        public void Tracker_StartedButNotSigned_IsNotEvidence()
        {
            FrostWithdrawalSigningTracker.RecordSigningStarted(Sc, Wrh, "s0", 0, "aa11", new List<string> { "aa11" });
            Assert.False(FrostWithdrawalSigningTracker.HasSignedTransaction(Sc, Wrh));
        }

        [Fact]
        public void Tracker_SignedInput_IsEvidence()
        {
            FrostWithdrawalSigningTracker.RecordSigningStarted(Sc, Wrh, "s0", 0, "aa11", new List<string> { "aa11" });
            FrostWithdrawalSigningTracker.RecordSigningCompleted(Sc, Wrh, "s0", 0, "aa11", new List<string> { "txid:0" }, "btc-txid");
            Assert.True(FrostWithdrawalSigningTracker.HasSignedTransaction(Sc, Wrh));
            Assert.False(FrostWithdrawalSigningTracker.HasSignedTransaction(Sc, "some-other-withdrawal"));
        }

        [Fact]
        public void Tracker_UnknownWithdrawal_NoEvidence()
        {
            Assert.False(FrostWithdrawalSigningTracker.HasSignedTransaction(Sc, Wrh));
            Assert.False(FrostWithdrawalSigningTracker.HasSignedTransaction("", Wrh));
        }

        // ── Vote guard ──────────────────────────────────────────────────────────

        [Fact]
        public void VoteGuard_ApproveAllowed_WhenNothingSigned()
        {
            Assert.True(CancellationVoteGuard.CanApprove(false, Row(VBTCWithdrawalStatus.Cancellation_Requested)).Ok);
            Assert.True(CancellationVoteGuard.CanApprove(false, null).Ok);
        }

        [Fact]
        public void VoteGuard_RefusesWhenTrackerSigned()
        {
            var (ok, reason) = CancellationVoteGuard.CanApprove(true, null);
            Assert.False(ok);
            Assert.Contains("double-pay", reason);
        }

        [Fact]
        public void VoteGuard_RefusesWhenLocalRowShowsSignedOrPinnedTx()
        {
            var signed = Row(VBTCWithdrawalStatus.Cancellation_Requested);
            signed.LastSignedBtcTxId = "deadbeef";
            Assert.False(CancellationVoteGuard.CanApprove(false, signed).Ok);

            var pinned = Row(VBTCWithdrawalStatus.Cancellation_Requested);
            pinned.PinnedUnsignedTxHex = "0200000001...";
            Assert.False(CancellationVoteGuard.CanApprove(false, pinned).Ok);

            Assert.False(CancellationVoteGuard.CanApprove(false, Row(VBTCWithdrawalStatus.Completed, completed: true)).Ok);
        }
    }
}
