using System;
using System.Collections.Generic;
using System.IO;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Coverage for the V2WithdrawalOwnerAddBackFixHeight consensus gate.
    ///
    /// The owner's vBTC balance is implicit: live BTC deposit balance + the owner's signed ledger
    /// rows. Every completed withdrawal shrinks the shared deposit pot, but only the requestor gets
    /// a burn row — so the owner's add-back must cancel the pot-shrinkage of ALL completed
    /// withdrawals on the contract. The historical owner-only add-back understated the owner by the
    /// sum of non-owner completed withdrawals (observed live on testnet 2026-08-21: owner short by
    /// exactly that sum on every contract with non-owner withdrawal history).
    /// </summary>
    [Collection("DbContextSequential")]
    public class VBTCOwnerAddBackFixTests : IDisposable
    {
        private const long ActivationHeight = 500;

        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly long _priorFixHeight;

        public VBTCOwnerAddBackFixTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"vbtcaddback_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorFixHeight = Globals.V2WithdrawalOwnerAddBackFixHeight;
            Globals.V2WithdrawalOwnerAddBackFixHeight = ActivationHeight;
            DbContext.Initialize();
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.V2WithdrawalOwnerAddBackFixHeight = _priorFixHeight;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static void SaveRow(string scUid, string requestor, decimal amount, string txHash, VBTCWithdrawalStatus status, bool isCompleted)
        {
            var req = new VBTCWithdrawalRequest
            {
                SmartContractUID = scUid,
                RequestorAddress = requestor,
                BTCDestination = "tb1q066af78la3rqmnchc396keujllva6turs52749",
                Amount = amount,
                TransactionHash = txHash,
                Status = status,
                IsCompleted = isCompleted,
                Timestamp = TimeUtil.GetTime(),
                RequestBlockHeight = 100,
            };
            Assert.True(VBTCWithdrawalRequest.Save(req));
        }

        private static SmartContractStateTrei NewState(string scUid, string owner, params (string From, string To, decimal Amount)[] rows)
        {
            var list = new List<SmartContractStateTreiTokenizationTX>();
            foreach (var (from, to, amount) in rows)
                list.Add(new SmartContractStateTreiTokenizationTX { FromAddress = from, ToAddress = to, Amount = amount });

            return new SmartContractStateTrei
            {
                SmartContractUID = scUid,
                OwnerAddress = owner,
                SCStateTreiTokenizationTXes = list,
            };
        }

        // ── The add-back itself, both sides of the gate ──────────────────────────────

        [Fact]
        public void AddBack_PostActivation_CountsAllCompletedOnContract()
        {
            const string sc = "ownerfix:1";
            const string owner = "xOwnerA";
            const string other = "xHolderB";

            SaveRow(sc, owner, 0.5M, "of1-owner-done", VBTCWithdrawalStatus.Completed, isCompleted: true);
            SaveRow(sc, other, 0.3M, "of1-other-done", VBTCWithdrawalStatus.Completed, isCompleted: true);
            // Cancelled rows never wrote a burn / never shrank the pot — excluded on BOTH sides.
            SaveRow(sc, other, 1.0M, "of1-other-cancelled", VBTCWithdrawalStatus.Cancelled, isCompleted: true);
            // A different contract's completed withdrawal shrank a different pot — never counted.
            SaveRow("ownerfix:other", other, 0.7M, "of1-foreign-done", VBTCWithdrawalStatus.Completed, isCompleted: true);

            // Pre-activation: legacy owner-only behavior, byte-identical to the historical formula.
            Assert.Equal(0.5M, VBTCWithdrawalRequest.GetCompletedWithdrawalAmount(owner, sc, ActivationHeight - 1));
            // At/after activation: every completed withdrawal on the contract counts.
            Assert.Equal(0.8M, VBTCWithdrawalRequest.GetCompletedWithdrawalAmount(owner, sc, ActivationHeight));
        }

        [Fact]
        public void AddBack_CancelledRows_ContributeZero_BothSidesOfGate()
        {
            const string sc = "ownerfix:2";
            const string owner = "xOwnerC";

            SaveRow(sc, owner, 2.5M, "of2-owner-cancelled", VBTCWithdrawalStatus.Cancelled, isCompleted: true);
            SaveRow(sc, "xHolderD", 1.5M, "of2-other-cancelled", VBTCWithdrawalStatus.Cancelled, isCompleted: true);

            Assert.Equal(0M, VBTCWithdrawalRequest.GetCompletedWithdrawalAmount(owner, sc, ActivationHeight - 1));
            Assert.Equal(0M, VBTCWithdrawalRequest.GetCompletedWithdrawalAmount(owner, sc, ActivationHeight));
        }

        // ── The exact observed regression shape ──────────────────────────────────────
        //
        // Owner mints (pot 1.0, no ledger rows), transfers 0.4 to B (owner −0.4, B +0.4), then B
        // withdraws its 0.4: pot 0.6, burn row on B only. The owner's true entitlement is 0.6
        // throughout — a non-owner's withdrawal must not move the owner's number.

        [Fact]
        public void OwnerBalance_UnchangedByNonOwnerCompletedWithdrawal()
        {
            const string sc = "ownerfix:3";
            const string owner = "xOwnerE";
            const string holder = "xHolderF";

            var stateBefore = NewState(sc, owner,
                ("+", holder, 0.4M),
                (owner, "-", -0.4M));
            const decimal potBefore = 1.0M;
            var ownerBefore = potBefore + VBTCService.GetOwnerLedgerBalance(stateBefore, owner, ActivationHeight);
            Assert.Equal(0.6M, ownerBefore);

            // B's withdrawal completes: pot shrinks, burn row lands on B, Completed row recorded.
            var stateAfter = NewState(sc, owner,
                ("+", holder, 0.4M),
                (owner, "-", -0.4M),
                (holder, "-", -0.4M));
            const decimal potAfter = 0.6M;
            SaveRow(sc, holder, 0.4M, "of3-holder-done", VBTCWithdrawalStatus.Completed, isCompleted: true);

            var ownerAfterFixed = potAfter + VBTCService.GetOwnerLedgerBalance(stateAfter, owner, ActivationHeight);
            Assert.Equal(ownerBefore, ownerAfterFixed);

            // Pre-activation the owner is short by exactly the non-owner's withdrawal — the bug
            // this gate exists to fix, kept asserted so the legacy branch stays honest.
            var ownerAfterLegacy = potAfter + VBTCService.GetOwnerLedgerBalance(stateAfter, owner, ActivationHeight - 1);
            Assert.Equal(ownerBefore - 0.4M, ownerAfterLegacy);
        }

        // ── Conservation: no vBTC belongs to nobody, none is double-owned ────────────

        [Fact]
        public void Conservation_SumOfHolderBalancesEqualsPot()
        {
            const string sc = "ownerfix:4";
            const string owner = "xOwnerG";
            const string holder = "xHolderH";

            // Mint pot 1.0; owner transfers 0.4 to B; B withdraws 0.25; owner withdraws 0.1.
            var state = NewState(sc, owner,
                ("+", holder, 0.4M),
                (owner, "-", -0.4M),
                (holder, "-", -0.25M),
                (owner, "-", -0.1M));
            SaveRow(sc, holder, 0.25M, "of4-holder-done", VBTCWithdrawalStatus.Completed, isCompleted: true);
            SaveRow(sc, owner, 0.1M, "of4-owner-done", VBTCWithdrawalStatus.Completed, isCompleted: true);
            const decimal pot = 1.0M - 0.25M - 0.1M;

            // Non-owner balance is the pure signed ledger sum (matches every production path).
            const decimal holderBalance = 0.4M - 0.25M;
            var ownerBalance = pot + VBTCService.GetOwnerLedgerBalance(state, owner, ActivationHeight);

            Assert.Equal(pot, holderBalance + ownerBalance);
        }

        // ── The gate must ride the height threaded through the helper, not the tip ───

        [Fact]
        public void GetOwnerLedgerBalance_ThreadsHeightThroughToGate()
        {
            const string sc = "ownerfix:5";
            const string owner = "xOwnerI";

            SaveRow(sc, "xHolderJ", 0.2M, "of5-holder-done", VBTCWithdrawalStatus.Completed, isCompleted: true);
            var state = NewState(sc, owner);

            Assert.Equal(0M, VBTCService.GetOwnerLedgerBalance(state, owner, ActivationHeight - 1));
            Assert.Equal(0.2M, VBTCService.GetOwnerLedgerBalance(state, owner, ActivationHeight));
        }
    }
}
