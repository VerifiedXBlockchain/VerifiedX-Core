using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
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
    /// The least payout a holder names on Base (Globals.ExitMinPayoutHeight): read with the burn, bound by the exit votes,
    /// recorded with the exit and shared over its parts. A part is first pinned only on a transaction paying its share,
    /// its fee is not raised below it, and an exit that could not be paid at the minimum fails by rule and is refunded.
    /// </summary>
    public partial class BtcAttest12_BridgeExitTests
    {
        /// <summary>The exit a caster sends for a burn that carried the holder's least payout.</summary>
        private VfxTx ExitWithMinimum(string burn, long minPayoutSats, params (string LockId, string Vault, decimal Amount)[] allocations)
        {
            var data = JObject.Parse(Exit(burn, BtcDest, allocations).Data);
            data["MinPayoutSats"] = minPayoutSats;
            return SignedBy(Caster, Caster.Address, TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC, data, free: true);
        }

        /// <summary>A failed exit that named its burner (Globals.ExitPartRefundHeight) is refunded only to that burner.</summary>
        [Fact]
        public async Task AFailedMinimumPayoutExit_ThatNamedItsBurner_IsRefundedOnlyToThatBurner()
        {
            var priorPartRefund = Globals.ExitPartRefundHeight;
            try
            {
                await Start();
                SeatTheCommittee();
                Globals.ExitMinPayoutHeight = Tip + 1;
                Globals.BridgeBurnRefundHeight = Tip + 1;
                Globals.ExitPartRefundHeight = Tip + 1;
                var vault = await CreateVault(Tip + 1);
                await Deposit(vault.Script, 1_000_000);
                var lockId = await Lock(vault.Uid, 0.004M);
                var burn = NewBurn();
                var data = JObject.Parse(ExitWithMinimum(burn, 399_000, (lockId, vault.Uid, 0.004M)).Data);
                data["BaseBurner"] = Burner;
                await Mine(SignedBy(Caster, Caster.Address, TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC, data, free: true));
                Assert.Equal(Burner, Record(burn).BaseBurner);
                await AdvanceTo(Tip + VBTCWithdrawalRequest.EXPIRY_BLOCKS + 1);
                Assert.True(Record(burn).IsFailedRecord);

                await RefusedInBlock(Refund(burn, 400_000, reason: VBTCBridgeRefundState.MinPayoutExitFailedReason, burner: "0x" + new string('c', 40)), "refunded to that address");
                await Mine(Refund(burn, 400_000, reason: VBTCBridgeRefundState.MinPayoutExitFailedReason));
                Assert.Equal(Burner, VBTCBridgeRefundState.Get(burn)!.BaseBurner);
            }
            finally
            {
                Globals.ExitPartRefundHeight = priorPartRefund;
            }
        }

        [Fact]
        public async Task AnExitPartPayingBelowItsShareOfTheMinimum_IsNeverPinned_TheExitExpires_AndTheBurnIsRefunded()
        {
            await Start();
            SeatTheCommittee();
            Globals.ExitMinPayoutHeight = Tip + 1;
            Globals.BridgeBurnRefundHeight = Tip + 1;
            var vault = await CreateVault(Tip + 1);
            await Deposit(vault.Script, 1_000_000);
            var lockId = await Lock(vault.Uid, 0.004M);
            var burn = NewBurn();

            // 400,000 sats less a 1,540-sat fee pays 398,460: below the 399,000 the holder accepts.
            await Mine(ExitWithMinimum(burn, 399_000, (lockId, vault.Uid, 0.004M)));
            Assert.Equal(399_000, Record(burn).MinPayoutSats);
            var part = Part(burn, vault.Uid);
            Assert.Equal(VaultWithdrawalState.Waiting, part.State);
            Assert.Equal(399_000, part.MinPayoutShareSats);
            Assert.Contains("minimum payout", part.WaitingReason);
            Assert.Empty(part.Pins);

            // Never pinned, it fails by rule; its lock comes back.
            await AdvanceTo(Tip + VBTCWithdrawalRequest.EXPIRY_BLOCKS + 1);
            Assert.True(Record(burn).IsFailedRecord);
            Assert.StartsWith(VBTCBridgeBtcExitState.FailedMarkerPrefix + "expired", Record(burn).BtcTxHash);
            Assert.False(VBTCBridgeLockState.GetByLockId(lockId)!.IsUnlocked);

            // The casters refund it instead of making it again.
            var record = new BurnExitConsensusService.ProcessedBurnRecord { BaseBurnTxHash = burn, ExitType = BurnExitConsensusService.BurnExitType.BtcExit, Amount = 0.004M };
            Assert.Equal(VBTCBridgeRefundState.MinPayoutExitFailedReason, BurnExitConsensusService.RefundReasonFor(record, Tip + 1));
            Globals.ExitMinPayoutHeight = long.MaxValue;
            Assert.Null(BurnExitConsensusService.RefundReasonFor(record, Tip + 1));
            Globals.ExitMinPayoutHeight = H1 + 1;

            // The whole burn, and only for a burn whose exit carried a minimum and failed.
            await RefusedInBlock(Refund(burn, 399_999, reason: VBTCBridgeRefundState.MinPayoutExitFailedReason), "the whole burn is refunded");
            await RefusedInBlock(Refund(NewBurn(), 400_000, reason: VBTCBridgeRefundState.MinPayoutExitFailedReason), "no exit that carried a minimum payout");
            var refund = await Mine(Refund(burn, 400_000, reason: VBTCBridgeRefundState.MinPayoutExitFailedReason));
            Assert.Equal(refund.Hash, VBTCBridgeRefundState.Get(burn)!.VfxTxHash);
            Assert.True(VBTCBridgeConsumedBurn.IsConsumed(burn));
        }

        [Fact]
        public async Task AFailedExitWithoutAMinimum_IsNotRefunded()
        {
            await Start();
            SeatTheCommittee();
            Globals.ExitMinPayoutHeight = Tip + 1;
            Globals.BridgeBurnRefundHeight = Tip + 1;
            var vault = await CreateVault(Tip + 1);
            await Deposit(vault.Script, 1_000_000);
            var lockId = await Lock(vault.Uid, 0.004M);
            var request = await Mine(Request(vault.Uid, 0.005M));                           // the vault's one coin goes to a withdrawal
            Assert.Equal(VaultWithdrawalState.Pinned, WithdrawalRow(request, vault.Uid).State);
            var burn = NewBurn();
            await Mine(Exit(burn, BtcDest, (lockId, vault.Uid, 0.004M)));
            await AdvanceTo(Tip + VBTCWithdrawalRequest.EXPIRY_BLOCKS + 1);
            Assert.True(Record(burn).IsFailedRecord);
            await RefusedInBlock(Refund(burn, 400_000, reason: VBTCBridgeRefundState.MinPayoutExitFailedReason), "no exit that carried a minimum payout");
        }

        [Fact]
        public async Task AnExitPartsFeeRaises_StopAtItsShareOfTheMinimum_AndItsTransactionSettlesTheExit()
        {
            await Start();
            Globals.ExitMinPayoutHeight = Tip + 1;
            var vault = await CreateVault(Tip + 1);
            await Deposit(vault.Script, 1_000_000);
            var lockId = await Lock(vault.Uid, 0.004M);
            var burn = NewBurn();

            // 398,460 at 10 sat/vB meets the 398,000 asked; at 20 sat/vB it would pay 396,920, which does not.
            await Mine(ExitWithMinimum(burn, 398_000, (lockId, vault.Uid, 0.004M)));
            var pin = Assert.Single(Part(burn, vault.Uid).Pins);
            Assert.Equal(398_460, pin.DestinationSats);
            await AdvanceTo(Looks(pin.PinnedAtVfxHeight + BtcAttestRules.StuckPinBlocks) + BtcAttestRules.TakeRetryBlocks);
            Assert.Single(Part(burn, vault.Uid).Pins);                                      // not raised below the minimum

            await SignAndConfirm(pin);
            Assert.Equal(VaultWithdrawalState.Settled, Part(burn, vault.Uid).State);
            Assert.True(Record(burn).IsComplete);
            Assert.False(Record(burn).IsFailedRecord);
        }

        [Fact]
        public async Task EachPart_BearsItsShareOfTheMinimum_RoundedUp()
        {
            Assert.Equal(75_001, BtcWithdrawals.MinPayoutShare(100_001, 300_000, 400_000));
            Assert.Equal(25_001, BtcWithdrawals.MinPayoutShare(100_001, 100_000, 400_000));
            Assert.Equal(0, BtcWithdrawals.MinPayoutShare(0, 100_000, 400_000));
            Assert.Equal(400_000, BtcWithdrawals.MinPayoutShare(400_000, 400_000, 400_000));

            await Start();
            Globals.ExitMinPayoutHeight = Tip + 1;
            var big = await CreateVault(Tip + 1);
            var small = await CreateVault(Tip + 1);
            await Deposit(big.Script, 1_000_000);
            await Deposit(small.Script, 1_000_000);
            var bigLock = await Lock(big.Uid, 0.003M);
            var smallLock = await Lock(small.Uid, 0.001M);
            var burn = NewBurn();
            await Mine(ExitWithMinimum(burn, 100_001, (bigLock, big.Uid, 0.003M), (smallLock, small.Uid, 0.001M)));
            Assert.Equal(75_001, Part(burn, big.Uid).MinPayoutShareSats);
            Assert.Equal(25_001, Part(burn, small.Uid).MinPayoutShareSats);
            Assert.Equal(VaultWithdrawalState.Pinned, Part(burn, big.Uid).State);
            Assert.Equal(VaultWithdrawalState.Pinned, Part(burn, small.Uid).State);
        }

        [Fact]
        public async Task BelowTheHeight_AMinimumPayoutIsIgnored()
        {
            await Start();
            Globals.ExitMinPayoutHeight = long.MaxValue;
            var vault = await CreateVault(Tip + 1);
            await Deposit(vault.Script, 1_000_000);
            var lockId = await Lock(vault.Uid, 0.004M);
            var burn = NewBurn();
            await Mine(ExitWithMinimum(burn, 399_000, (lockId, vault.Uid, 0.004M)));
            Assert.Equal(0, Record(burn).MinPayoutSats);
            Assert.Equal(0, Part(burn, vault.Uid).MinPayoutShareSats);
            Assert.Equal(VaultWithdrawalState.Pinned, Part(burn, vault.Uid).State);
        }

        [Fact]
        public async Task FromTheHeight_TheExitVotesAreBoundToTheMinimumItCarries()
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
                VfxTx Voted(long carried, Func<long, string> message)
                {
                    var data = JObject.Parse(ExitWithMinimum(burn, carried, (lockId, vault.Uid, 0.004M)).Data);
                    var ts = TimeUtil.GetTime();
                    data["CasterConsensusVotes"] = JArray.FromObject(Signers.Select(v => new CasterConsensusVote
                    {
                        CasterAddress = v.Address, BaseBurnTxHash = burn, BurnType = "BTC_EXIT", Timestamp = ts,
                        Signature = VerifiedXCore.Services.SignatureService.CreateSignature(message(ts), v.Key, v.Pub),
                    }));
                    return SignedBy(Caster, Caster.Address, TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC, data, free: true);
                }
                string Plain(long ts) => BridgeCasterConsensus.BuildBoundVoteMessage(burn, "BTC_EXIT", 400_000, BtcDest, ts);
                Func<long, string> WithMin(long min, long height) => ts => BridgeCasterConsensus.BuildExitVoteMessage(burn, 400_000, BtcDest, min, height, ts);

                // Below the height the minimum is not part of the vote.
                Globals.ExitMinPayoutHeight = long.MaxValue;
                Assert.True((await Verify(Voted(300_000, Plain), Tip + 1)).Ok);
                Assert.Equal(Plain(5), BridgeCasterConsensus.BuildExitVoteMessage(burn, 400_000, BtcDest, 300_000, Tip + 1, 5));

                Globals.ExitMinPayoutHeight = Tip + 1;
                await RefusedInBlock(Voted(300_000, Plain), "Caster consensus votes rejected");
                await RefusedInBlock(Voted(300_000, WithMin(0, Tip + 1)), "Caster consensus votes rejected");
                await RefusedInBlock(Voted(400_001, WithMin(400_001, Tip + 1)), "at most its amount");
                Assert.True((await Verify(Voted(300_000, WithMin(300_000, Tip + 1)), Tip + 1)).Ok);
            }
            finally { Globals.BridgeBurnBindingHeight = priorBinding; }
        }

        [Fact]
        public void AMinimumPayoutLog_BelongsToTheBurnItFollows_AndIsNotABurn()
        {
            var a = new BaseBridgeService.BurnEventInfo { AmountSats = 100_000, Burner = "0xAAAA" };
            var b = new BaseBridgeService.BurnEventInfo { AmountSats = 100_000, Burner = "0xBBBB" };
            var c = new BaseBridgeService.BurnEventInfo { AmountSats = 100_000, Burner = "0xCCCC" };
            BaseBridgeService.AttachMinPayouts(new List<(BigInteger, BaseBridgeService.BurnEventInfo?)> { (5, a), (9, b), (20, c) }, new[]
            {
                (new BigInteger(6), "0xaaaa", new BigInteger(90_000)),   // right after a, same burner (any case)
                (new BigInteger(10), "0xCCCC", new BigInteger(1)),       // right after b, another burner's
                (new BigInteger(22), "0xCCCC", new BigInteger(1)),       // not right after c
            });
            Assert.Equal(90_000, a.MinPayoutSats);
            Assert.Equal(0, b.MinPayoutSats);
            Assert.Equal(0, c.MinPayoutSats);

            var d = new BaseBridgeService.BurnEventInfo { AmountSats = 100, Burner = "0xDDDD" };
            BaseBridgeService.AttachMinPayouts(new List<(BigInteger, BaseBridgeService.BurnEventInfo?)> { (1, d) }, new[] { (new BigInteger(2), "0xDDDD", new BigInteger(101)) });
            Assert.Equal(0, d.MinPayoutSats);                                               // above the amount: not one
        }
    }
}
