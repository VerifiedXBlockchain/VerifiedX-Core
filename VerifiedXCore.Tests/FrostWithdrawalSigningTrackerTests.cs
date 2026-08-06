using System.Collections.Generic;
using VerifiedXCore.Bitcoin.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// FIND-028 redesigned tracker: the invariant is ONE BITCOIN TRANSACTION PER WITHDRAWAL, not
    /// one ceremony. Covers the two production bug classes (multi-input withdrawals dead on input 1;
    /// coordinator-side failure after share generation unretryable for 24h) and the per-contract
    /// outpoint-conflict guard that closes the sign-withhold-expire-resign double-payout attack.
    /// Tracker state is process-global, so tests reset it and run sequentially.
    /// </summary>
    [Collection("DbContextSequential")]
    public class FrostWithdrawalSigningTrackerTests : System.IDisposable
    {
        private const string ScUID = "vbtc-contract-T";
        private const string Wrh = "withdrawal-hash-1";
        private const string Wrh2 = "withdrawal-hash-2";
        private const string SighashA = "aa11";
        private const string SighashB = "bb22";

        public FrostWithdrawalSigningTrackerTests()
        {
            FrostWithdrawalSigningTracker.ResetForTests();
        }

        public void Dispose()
        {
            FrostWithdrawalSigningTracker.ResetForTests();
        }

        // ---------- Multi-input: per-input independence (production bug A) ----------

        [Fact]
        public void SignedInput0_DoesNotBlockInput1()
        {
            // The old tracker keyed state per-withdrawal: input 0's Signed state 409'd input 1's
            // start, making EVERY multi-input withdrawal fail with "FROST signing failed for input 1".
            var sighashes = new List<string> { SighashA, SighashB };
            FrostWithdrawalSigningTracker.RecordSigningStarted(ScUID, Wrh, "s0", 0, SighashA, sighashes);
            FrostWithdrawalSigningTracker.RecordSigningCompleted(ScUID, Wrh, "s0", 0, SighashA);

            var (blocked, reason) = FrostWithdrawalSigningTracker.CheckWithdrawalSigning(ScUID, Wrh, 1, SighashB, sighashes);
            Assert.False(blocked, reason);
        }

        // ---------- Idempotent re-sign (production bug: unretryable aggregation failure) ----------

        [Fact]
        public void SignedInput_SameSighash_AllowsResign()
        {
            // The incident: validators generated shares (Signed, terminal 24h), coordinator-side
            // aggregation failed, every retry 409'd. Re-signing the IDENTICAL sighash can only
            // reproduce the same transaction, so it must be allowed.
            FrostWithdrawalSigningTracker.RecordSigningCompleted(ScUID, Wrh, "s0", 0, SighashA);

            var (blocked, reason) = FrostWithdrawalSigningTracker.CheckWithdrawalSigning(ScUID, Wrh, 0, SighashA);
            Assert.False(blocked, reason);
        }

        [Fact]
        public void SignedInput_DifferentSighash_Blocked()
        {
            // A DIFFERENT sighash for an already-signed input = a second Bitcoin transaction for
            // the same withdrawal — the actual double-spend attack. Must stay refused.
            FrostWithdrawalSigningTracker.RecordSigningCompleted(ScUID, Wrh, "s0", 0, SighashA);

            var (blocked, _) = FrostWithdrawalSigningTracker.CheckWithdrawalSigning(ScUID, Wrh, 0, SighashB);
            Assert.True(blocked);
        }

        // ---------- Sighash-set pinning (fake-input defense) ----------

        [Fact]
        public void PinnedSighashSet_RejectsMismatchedInputHash()
        {
            var sighashes = new List<string> { SighashA, SighashB };
            FrostWithdrawalSigningTracker.RecordSigningStarted(ScUID, Wrh, "s0", 0, SighashA, sighashes);

            // "Input 1" carrying a hash that is NOT the announced input-1 sighash → a different
            // transaction smuggled in under the same withdrawal.
            var (blocked, _) = FrostWithdrawalSigningTracker.CheckWithdrawalSigning(ScUID, Wrh, 1, "cc33", sighashes);
            Assert.True(blocked);
        }

        [Fact]
        public void PinnedSighashSet_RejectsInputIndexBeyondAnnouncedCount()
        {
            var sighashes = new List<string> { SighashA, SighashB };
            FrostWithdrawalSigningTracker.RecordSigningStarted(ScUID, Wrh, "s0", 0, SighashA, sighashes);

            var (blocked, _) = FrostWithdrawalSigningTracker.CheckWithdrawalSigning(ScUID, Wrh, 5, "dd44", sighashes);
            Assert.True(blocked);
        }

        // ---------- Failure cooldown ----------

        [Fact]
        public void FailedInput_BlockedDuringCooldown()
        {
            FrostWithdrawalSigningTracker.RecordSigningStarted(ScUID, Wrh, "s0", 0, SighashA, null);
            FrostWithdrawalSigningTracker.RecordSigningFailed(ScUID, Wrh, "s0", 0);

            var (blocked, reason) = FrostWithdrawalSigningTracker.CheckWithdrawalSigning(ScUID, Wrh, 0, SighashA);
            Assert.True(blocked);
            Assert.Contains("Retry allowed", reason);
        }

        [Fact]
        public void FailedInput_NeverDowngradesSigned()
        {
            FrostWithdrawalSigningTracker.RecordSigningCompleted(ScUID, Wrh, "s0", 0, SighashA);
            FrostWithdrawalSigningTracker.RecordSigningFailed(ScUID, Wrh, "s0", 0);

            // Still Signed: same sighash allowed, different sighash blocked.
            Assert.False(FrostWithdrawalSigningTracker.CheckWithdrawalSigning(ScUID, Wrh, 0, SighashA).Blocked);
            Assert.True(FrostWithdrawalSigningTracker.CheckWithdrawalSigning(ScUID, Wrh, 0, SighashB).Blocked);
        }

        // ---------- Per-contract outpoint conflict guard (double-withdrawal attack) ----------

        [Fact]
        public void ContractPin_DisjointTxForDifferentWithdrawal_Blocked()
        {
            // The attack: sign tx1, withhold it, wait out the 360-block gate, request withdrawal #2
            // spending DIFFERENT vault UTXOs → both txs could confirm → double payout. A disjoint
            // second tx must be refused while tx1 is outstanding.
            var tx1Outpoints = new List<string> { "txid1:0", "txid1:1" };
            FrostWithdrawalSigningTracker.RecordSigningCompleted(ScUID, Wrh, "s0", 0, SighashA, tx1Outpoints, "btc-txid-1");

            var disjoint = new List<string> { "txid9:0" };
            var (blocked, reason) = FrostWithdrawalSigningTracker.CheckWithdrawalSigning(ScUID, Wrh2, 0, SighashB, null, disjoint);
            Assert.True(blocked);
            Assert.Contains("PIN:", reason);
        }

        [Fact]
        public void ContractPin_ConflictingTxForDifferentWithdrawal_Allowed()
        {
            // A tx sharing >=1 input with the outstanding signed tx CONFLICTS with it — Bitcoin
            // guarantees at most one confirms, so no double payout is possible. Deterministic
            // largest-first selection makes legitimate successors take this shape naturally.
            var tx1Outpoints = new List<string> { "txid1:0", "txid1:1" };
            FrostWithdrawalSigningTracker.RecordSigningCompleted(ScUID, Wrh, "s0", 0, SighashA, tx1Outpoints, "btc-txid-1");

            var conflicting = new List<string> { "txid1:0", "txid5:2" };
            var (blocked, reason) = FrostWithdrawalSigningTracker.CheckWithdrawalSigning(ScUID, Wrh2, 0, SighashB, null, conflicting);
            Assert.False(blocked, reason);
        }

        [Fact]
        public void ContractPin_SameWithdrawalRetry_NotAffected()
        {
            // The pin only constrains OTHER withdrawals; the pinned withdrawal's own (idempotent)
            // retry stays allowed.
            var tx1Outpoints = new List<string> { "txid1:0" };
            FrostWithdrawalSigningTracker.RecordSigningCompleted(ScUID, Wrh, "s0", 0, SighashA, tx1Outpoints, "btc-txid-1");

            var (blocked, reason) = FrostWithdrawalSigningTracker.CheckWithdrawalSigning(ScUID, Wrh, 0, SighashA, null, tx1Outpoints);
            Assert.False(blocked, reason);
        }

        [Fact]
        public void ContractPin_ClearedAfterOnChainObservation_AllowsDisjointTx()
        {
            // Once the pinned tx (or a conflict) is observed on-chain, the pin releases and a new
            // withdrawal may spend the remaining (now genuinely disjoint) UTXOs.
            var tx1Outpoints = new List<string> { "txid1:0" };
            FrostWithdrawalSigningTracker.RecordSigningCompleted(ScUID, Wrh, "s0", 0, SighashA, tx1Outpoints, "btc-txid-1");
            FrostWithdrawalSigningTracker.ClearContractPin(ScUID, "test: observed confirmed");

            var disjoint = new List<string> { "txid9:0" };
            var (blocked, reason) = FrostWithdrawalSigningTracker.CheckWithdrawalSigning(ScUID, Wrh2, 0, SighashB, null, disjoint);
            Assert.False(blocked, reason);
        }

        [Fact]
        public void ContractPin_DoesNotCrossContracts()
        {
            var tx1Outpoints = new List<string> { "txid1:0" };
            FrostWithdrawalSigningTracker.RecordSigningCompleted(ScUID, Wrh, "s0", 0, SighashA, tx1Outpoints, "btc-txid-1");

            var disjoint = new List<string> { "txid9:0" };
            var (blocked, reason) = FrostWithdrawalSigningTracker.CheckWithdrawalSigning("other-contract", Wrh2, 0, SighashB, null, disjoint);
            Assert.False(blocked, reason);
        }

        // ---------- Legacy caller compatibility ----------

        [Fact]
        public void LegacyShape_NoSighashNoOutpoints_StillDedupesWithdrawal()
        {
            // Old coordinators send no InputIndex/sighash/outpoints. Their single-input ceremonies
            // must still dedupe: a Signed input with no known sighash blocks a hash-less re-check.
            FrostWithdrawalSigningTracker.RecordSigningStarted(ScUID, Wrh, "s0");
            FrostWithdrawalSigningTracker.RecordSigningCompleted(ScUID, Wrh, "s0");

            var (blocked, _) = FrostWithdrawalSigningTracker.CheckWithdrawalSigning(ScUID, Wrh);
            Assert.True(blocked);
        }

        [Fact]
        public void NonWithdrawalSigning_AlwaysAllowed()
        {
            var (blocked, _) = FrostWithdrawalSigningTracker.CheckWithdrawalSigning(ScUID, "");
            Assert.False(blocked);
        }
    }
}
