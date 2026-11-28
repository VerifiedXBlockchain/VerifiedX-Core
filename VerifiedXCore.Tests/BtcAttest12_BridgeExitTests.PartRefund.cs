using System;
using System.Linq;
using System.Threading.Tasks;
using NBitcoin;
using Newtonsoft.Json.Linq;
using VerifiedXCore.Bitcoin.Attestation;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;
using Xunit;
using VfxTx = VerifiedXCore.Models.Transaction;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A part of a split exit whose vault cannot pay it after its transaction was voided (Globals.ExitPartRefundHeight):
    /// once the voiding spend is buried and the part has waited ExitPartRefundBlocks more, it is closed void, its lock is
    /// given back once, the chain records a refund of exactly its satoshis to the burner, and the exit completes.
    /// </summary>
    public partial class BtcAttest12_BridgeExitTests
    {
        private readonly long _priorPartRefund = Globals.ExitPartRefundHeight;

        /// <summary>The exit a caster sends from the height: it names the Base address that burned.</summary>
        private VfxTx ExitWithBurner(string burn, params (string LockId, string Vault, decimal Amount)[] allocations)
        {
            var data = JObject.Parse(Exit(burn, BtcDest, allocations).Data);
            data["BaseBurner"] = Burner;
            return SignedBy(Caster, Caster.Address, TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC, data, free: true);
        }

        /// <summary>
        /// A two-vault exit: the first part is paid, the second's coin leaves in another transaction with nothing coming back
        /// to its vault, so its vault holds no coin to pay it from. Returns the burn, the second vault and its lock, and the
        /// Bitcoin height of the voiding spend.
        /// </summary>
        private NBitcoin.Script _paidVault = NBitcoin.Script.Empty;

        private async Task<(string Burn, string Vault, string LockId, long VoidedAt)> ASplitExitWhoseSecondVaultCannotPay(bool withBurner = true)
        {
            BtcAttestRules.ExitReopenBurialBlocksOverride = 1;
            BtcAttestRules.ExitPartRefundBlocksOverride = 1;
            await Start();
            var vault = await CreateVault(H1 + 1);
            var other = await CreateVault(H1 + 2);
            _paidVault = vault.Script;
            await Deposit(vault.Script, 1_000_000);
            var otherDeposit = await Deposit(other.Script, 1_000_000);
            var lockId = await Lock(vault.Uid, 0.004M);
            var otherLock = await Lock(other.Uid, 0.004M);
            var burn = NewBurn();
            var allocations = new[] { (lockId, vault.Uid, 0.004M), (otherLock, other.Uid, 0.004M) };
            await Mine(withBurner ? ExitWithBurner(burn, allocations) : Exit(burn, BtcDest, allocations));
            await SignAndConfirm(Part(burn, vault.Uid).Pins[0]);
            var voidedAt = _btc.Mine(Pay(new[] { new OutPoint(otherDeposit, 0) }, (Elsewhere(), 999_000)));
            await Report(voidedAt);
            Assert.Equal(VaultWithdrawalState.Waiting, Part(burn, other.Uid).State);
            return (burn, other.Uid, otherLock, voidedAt);
        }

        /// <summary>
        /// Bitcoin moves on past the burial and the wait (small deposits to the vault whose part was paid: the other vault
        /// gets nothing), and the chain reaches its next look at waiting parts.
        /// </summary>
        private async Task PastTheBurialAndTheWait()
        {
            for (var i = 0; i < BtcAttestRules.ExitReopenBurialBlocks + BtcAttestRules.ExitPartRefundBlocks + 1; i++)
                await Deposit(_paidVault, 10_000);
            await AdvanceTo(Looks(Tip + 1));
        }

        private void ResetPartRefundGlobals()
        {
            BtcAttestRules.ExitPartRefundBlocksOverride = null;
            Globals.ExitPartRefundHeight = _priorPartRefund;
        }

        [Fact]
        public async Task APaidExitsPartItsVaultCannotPay_IsRefundedToTheBurner_ItsLockGivenBackOnce_AndTheExitCompletes()
        {
            try
            {
                Globals.ExitPartRefundHeight = H1 + 1;
                var (burn, vault, lockId, _) = await ASplitExitWhoseSecondVaultCannotPay();
                Assert.Equal(Burner, Record(burn).BaseBurner);
                Assert.True(VBTCBridgeLockState.GetByLockId(lockId)!.IsUnlocked);           // still drawn while the part waits

                await PastTheBurialAndTheWait();
                var part = Part(burn, vault);
                Assert.Equal(VaultWithdrawalState.Void, part.State);
                Assert.True(part.ExitRefundedAtVfxHeight > 0);
                var restored = VBTCBridgeLockState.GetByLockId(lockId)!;
                Assert.False(restored.IsUnlocked);
                Assert.Equal(400_000, restored.RemainingAmountSats);                         // given back once

                var refund = VBTCBridgeRefundState.Get(VBTCBridgeRefundState.PartRefundId(burn, vault))!;
                Assert.Equal($"{burn}#{vault}", refund.RefundId);
                Assert.Equal(Burner, refund.BaseBurner);
                Assert.Equal(400_000, refund.AmountSats);
                Assert.Equal(VBTCBridgeRefundState.ExitPartUnpaidReason, refund.Reason);
                Assert.True(Record(burn).IsComplete);
                Assert.False(Record(burn).IsFailedRecord);

                // Nothing more happens to it later.
                await AdvanceTo(Tip + 2 * BtcAttestRules.TakeRetryBlocks);
                Assert.Equal(400_000, VBTCBridgeLockState.GetByLockId(lockId)!.RemainingAmountSats);
                Assert.Single(VBTCBridgeRefundState.GetCollection().FindAll());
            }
            finally { ResetPartRefundGlobals(); }
        }

        [Fact]
        public async Task BelowTheHeight_OrWithoutABurner_ThePartKeepsWaiting()
        {
            try
            {
                Globals.ExitPartRefundHeight = long.MaxValue;
                var (burn, vault, lockId, _) = await ASplitExitWhoseSecondVaultCannotPay();
                Assert.Null(Record(burn).BaseBurner);                                           // not recorded below the height
                await PastTheBurialAndTheWait();
                Assert.Equal(VaultWithdrawalState.Waiting, Part(burn, vault).State);
                Assert.True(VBTCBridgeLockState.GetByLockId(lockId)!.IsUnlocked);
                Assert.Equal(0, VBTCBridgeRefundState.GetCollection().Count());
                Assert.False(Record(burn).IsComplete);

                Globals.ExitPartRefundHeight = Tip + 1;                                         // the rule is live, but this exit names nobody
                await AdvanceTo(Looks(Tip + 1));
                Assert.Equal(VaultWithdrawalState.Waiting, Part(burn, vault).State);
                Assert.Equal(0, VBTCBridgeRefundState.GetCollection().Count());
            }
            finally { ResetPartRefundGlobals(); }
        }

        [Fact]
        public async Task FromTheHeight_AnExitWithoutABurner_KeepsWaiting()
        {
            try
            {
                Globals.ExitPartRefundHeight = H1 + 1;
                var (burn, vault, _, _) = await ASplitExitWhoseSecondVaultCannotPay(withBurner: false);
                Assert.Null(Record(burn).BaseBurner);
                await PastTheBurialAndTheWait();
                Assert.Equal(VaultWithdrawalState.Waiting, Part(burn, vault).State);
                Assert.Equal(0, VBTCBridgeRefundState.GetCollection().Count());
            }
            finally { ResetPartRefundGlobals(); }
        }

        [Fact]
        public async Task ARewindBeforeTheBurial_RevivesThePartsEarlierTransaction_AndRecordsNoRefund()
        {
            try
            {
                Globals.ExitPartRefundHeight = H1 + 1;
                var (burn, vault, _, voidedAt) = await ASplitExitWhoseSecondVaultCannotPay();
                var first = Part(burn, vault).Pins[0];
                await Reorganise(voidedAt - 1);                                                   // the voiding spend is gone
                var part = Part(burn, vault);
                Assert.Equal(VaultWithdrawalState.Pinned, part.State);
                Assert.Equal(first.Txid, Assert.Single(part.Pins).Txid);
                await PastTheBurialAndTheWait();
                Assert.Equal(VaultWithdrawalState.Pinned, Part(burn, vault).State);
                Assert.Equal(0, VBTCBridgeRefundState.GetCollection().Count());
            }
            finally { ResetPartRefundGlobals(); }
        }

        [Fact]
        public async Task AfterAnExitPartIsRefunded_ARewindLeavesItClosed_AndTheBurnIsNotRefundedAsAWhole()
        {
            try
            {
                Globals.ExitPartRefundHeight = H1 + 1;
                var (burn, vault, _, voidedAt) = await ASplitExitWhoseSecondVaultCannotPay();
                await PastTheBurialAndTheWait();
                var refundId = VBTCBridgeRefundState.PartRefundId(burn, vault);
                Assert.NotNull(VBTCBridgeRefundState.Get(refundId));

                // A rewind past the voiding spend now: the part was refunded, so it stays closed (its earlier transaction is not revived).
                await Reorganise(voidedAt - 1);
                Assert.Equal(VaultWithdrawalState.Void, Part(burn, vault).State);
                Assert.True(Part(burn, vault).Pins.All(p => p.Dead));

                // A refund transaction for the whole burn is refused: the burn was paid in part.
                SeatTheCommittee();
                Globals.BridgeBurnRefundHeight = Tip + 1;
                await RefusedInBlock(Refund(burn, BelowSmallestExit), "already redeemed");
            }
            finally { ResetPartRefundGlobals(); }
        }

        [Fact]
        public async Task FromTheHeight_TheExitVotesAreBoundToTheBurnerItCarries()
        {
            var priorBinding = Globals.BridgeBurnBindingHeight;
            try
            {
                await Start();
                SeatTheCommittee();
                Globals.BridgeBurnBindingHeight = 1;
                var vault = await CreateVault(Tip + 1);
                await Deposit(vault.Script, 1_000_000);
                var lockId = await Lock(vault.Uid, 0.004M);
                var burn = NewBurn();
                VfxTx Voted(string? carried, string? voted)
                {
                    var data = JObject.Parse(Exit(burn, BtcDest, (lockId, vault.Uid, 0.004M)).Data);
                    if (carried != null) data["BaseBurner"] = carried;
                    var ts = TimeUtil.GetTime();
                    data["CasterConsensusVotes"] = JArray.FromObject(Signers.Select(v => new CasterConsensusVote
                    {
                        CasterAddress = v.Address, BaseBurnTxHash = burn, BurnType = "BTC_EXIT", Timestamp = ts,
                        Signature = VerifiedXCore.Services.SignatureService.CreateSignature(
                            BridgeCasterConsensus.BuildExitVoteMessage(burn, 400_000, BtcDest, 0, Tip + 1, ts, voted), v.Key, v.Pub),
                    }));
                    return SignedBy(Caster, Caster.Address, TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC, data, free: true);
                }

                Globals.ExitPartRefundHeight = long.MaxValue;
                Assert.True((await Verify(Voted(Burner, null), Tip + 1)).Ok);                  // below: the burner is no part of it

                Globals.ExitPartRefundHeight = Tip + 1;
                await RefusedInBlock(Voted(Burner, "0x" + new string('c', 40)), "Caster consensus votes rejected");
                await RefusedInBlock(Voted(Burner.ToUpperInvariant().Replace("0X", "0x"), Burner), "BaseBurner");
                Assert.True((await Verify(Voted(Burner, Burner), Tip + 1)).Ok);
            }
            finally
            {
                Globals.BridgeBurnBindingHeight = priorBinding;
                Globals.ExitPartRefundHeight = _priorPartRefund;
            }
        }
    }
}
