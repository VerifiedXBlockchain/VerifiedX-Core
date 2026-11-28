using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using VerifiedXCore.Bitcoin.Attestation;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;
using Xunit;
using VfxTx = VerifiedXCore.Models.Transaction;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A Base burn VFX cannot pay is refunded (Globals.BridgeBurnRefundHeight): the caster committee agrees on the burn,
    /// the refunded amount and the burner, the handler sends a VBTC_V2_BRIDGE_BURN_REFUND, and its apply records the
    /// refund and consumes the burn. The validators sign that record, and only it, for the contract's refundBurn.
    /// </summary>
    public partial class BtcAttest12_BridgeExitTests
    {
        private static readonly string Burner = "0x" + new string('b', 40);
        private static long BelowSmallestExit => BtcWithdrawals.MinExitPartSats - 1;

        /// <summary>All four signers sit on the committee with the three mainnet seeds: four votes carry it.</summary>
        private void SeatTheCommittee()
        {
            foreach (var signer in Signers) Globals.BootstrapCasterAddresses.Add(signer.Address);
            Assert.Equal(4, BridgeCasterConsensus.RequiredVotesFor(BridgeCasterConsensus.GetCommitteeForHeight(Tip + 1)));
        }

        /// <summary>The refund a handler sends, with the votes of <paramref name="voters"/> (by default the whole committee) over what they agreed on.</summary>
        private VfxTx Refund(string burn, long sats, string? burner = null, string reason = VBTCBridgeRefundState.BelowSmallestExitReason,
            IEnumerable<V>? voters = null, string? votedBurner = null, long? votedSats = null, V? sender = null,
            Func<string, long, string, long, string>? voteMessage = null)
        {
            var ts = TimeUtil.GetTime();
            var message = voteMessage ?? BridgeCasterConsensus.BuildRefundVoteMessage;
            var votes = (voters ?? Signers).Select(v => new CasterConsensusVote
            {
                CasterAddress = v.Address, BaseBurnTxHash = burn, BurnType = BridgeCasterConsensus.RefundBurnType, Timestamp = ts,
                Signature = VerifiedXCore.Services.SignatureService.CreateSignature(message(burn, votedSats ?? sats, votedBurner ?? burner ?? Burner, ts), v.Key, v.Pub),
            }).ToList();
            var from = sender ?? Caster;
            return SignedBy(from, from.Address, TransactionType.VBTC_V2_BRIDGE_BURN_REFUND, new
            {
                Function = BridgeBurnRefundRules.Function, BaseBurnTxHash = burn, BaseBurner = burner ?? Burner, AmountSats = sats, Reason = reason,
                CasterConsensusVotes = votes,
            }, free: true);
        }

        [Fact]
        public async Task ABurnBelowTheSmallestExit_IsRefundedToItsBurner_Once_AndIsNotExitedAfterwards()
        {
            await Start();
            SeatTheCommittee();
            Globals.BridgeBurnRefundHeight = Tip + 1;
            var burn = NewBurn();

            // The smallest exit itself can be paid: it is not refunded.
            await RefusedInBlock(Refund(burn, BtcWithdrawals.MinExitPartSats), "can be paid by an exit");

            var refund = await Mine(Refund(burn, BelowSmallestExit));
            var recorded = VBTCBridgeRefundState.Get(burn)!;
            Assert.Equal(burn, recorded.BaseBurnTxHash);
            Assert.Equal(Burner, recorded.BaseBurner);
            Assert.Equal(BelowSmallestExit, recorded.AmountSats);
            Assert.Equal(VBTCBridgeRefundState.BelowSmallestExitReason, recorded.Reason);
            Assert.Equal(refund.Hash, recorded.VfxTxHash);
            Assert.Equal(Tip, recorded.VfxHeight);
            Assert.True(VBTCBridgeConsumedBurn.IsConsumed(burn));

            // Once: not again under any spelling, and no exit draws on the burn afterwards.
            await RefusedInBlock(Refund(burn, BelowSmallestExit), "already redeemed");
            var vault = await CreateVault(Tip + 1);
            await Deposit(vault.Script, 1_000_000);
            var lockId = await Lock(vault.Uid, 0.004M);
            await RefusedInBlock(Exit(burn, BtcDest, (lockId, vault.Uid, 0.004M)), "Duplicate Base burn");
        }

        [Fact]
        public async Task BelowTheHeight_ARefundIsNotAccepted()
        {
            await Start();
            SeatTheCommittee();
            Globals.BridgeBurnRefundHeight = long.MaxValue;
            var (ok, _) = await Verify(Refund(NewBurn(), BelowSmallestExit), Tip + 1);
            Assert.False(ok);

            Globals.BridgeBurnRefundHeight = Tip + 2;
            Assert.False((await Verify(Refund(NewBurn(), BelowSmallestExit), Tip + 1)).Ok);
            Assert.True((await Verify(Refund(NewBurn(), BelowSmallestExit), Tip + 2)).Ok);
        }

        [Fact]
        public async Task ARefund_CountsOnlyCommitteeVotesForItsAmountAndBurner_FromACommitteeCaster()
        {
            await Start();
            SeatTheCommittee();
            Globals.BridgeBurnRefundHeight = Tip + 1;
            var burn = NewBurn();
            const string votesRejected = "Caster consensus votes rejected for the bridge burn refund";

            await RefusedInBlock(Refund(burn, BelowSmallestExit, votedBurner: "0x" + new string('c', 40)), votesRejected);
            await RefusedInBlock(Refund(burn, BelowSmallestExit, votedSats: BelowSmallestExit - 1), votesRejected);
            await RefusedInBlock(Refund(burn, BelowSmallestExit, voters: Signers.Take(3)), votesRejected);
            // An exit vote for the same burn is not a refund vote.
            await RefusedInBlock(Refund(burn, BelowSmallestExit,
                voteMessage: (b, sats, _, ts) => BridgeCasterConsensus.BuildBoundVoteMessage(b, "BTC_EXIT", sats, BtcDest, ts)), votesRejected);

            await RefusedInBlock(Refund(burn, BelowSmallestExit, burner: "0x" + new string('B', 40)), "BaseBurner");
            await RefusedInBlock(Refund("0x" + burn[2..].ToUpperInvariant(), BelowSmallestExit), "one spelling");
            await RefusedInBlock(Refund(burn, 0), "positive amount");
            await RefusedInBlock(Refund(burn, BelowSmallestExit, reason: "OTHER"), "is not refunded for the reason");
            await RefusedInBlock(Refund(burn, BelowSmallestExit, sender: _holder), "committee caster");

            Assert.True((await Verify(Refund(burn, BelowSmallestExit), Tip + 1)).Ok);
        }

        [Fact]
        public async Task AnExitedBurn_IsNotRefunded_AndAnExitAndARefundOfOneBurnDoNotShareABlock()
        {
            await Start();
            SeatTheCommittee();
            Globals.BridgeBurnRefundHeight = Tip + 1;
            var vault = await CreateVault(Tip + 1);
            await Deposit(vault.Script, 1_000_000);
            var first = await Lock(vault.Uid, 0.002M);
            var second = await Lock(vault.Uid, 0.002M);

            var exited = NewBurn();
            await Mine(Exit(exited, BtcDest, (first, vault.Uid, 0.002M)));
            await RefusedInBlock(Refund(exited, BelowSmallestExit), "already redeemed");

            // Each passes on the stored state; whichever comes first in the block takes the burn.
            var burn = NewBurn();
            var exit = Exit(burn, BtcDest, (second, vault.Uid, 0.002M));
            var refund = Refund(burn, BelowSmallestExit);
            Assert.True((await Verify(exit, Tip + 1)).Ok);
            Assert.True((await Verify(refund, Tip + 1)).Ok);
            foreach (var (one, two) in new[] { (exit, refund), (refund, exit) })
            {
                var guard = new BridgeIntraBlockGuard.State();
                Assert.True(BridgeIntraBlockGuard.TryRegister(one, guard, Tip + 1).Ok);
                var (ok, why) = BridgeIntraBlockGuard.TryRegister(two, guard, Tip + 1);
                Assert.False(ok);
                Assert.Contains("Duplicate Base burn", why);
            }
        }

        [Fact]
        public async Task ARefund_IsRebuiltRowForRow_WhenTheStateIsReplayed()
        {
            await Start();
            SeatTheCommittee();
            Globals.BridgeBurnRefundHeight = Tip + 1;
            var burn = NewBurn();
            await Mine(Refund(burn, BelowSmallestExit));
            string Rows() => string.Join("|", VBTCBridgeRefundState.GetCollection().FindAll().Select(r => $"{r.RefundId} {r.BaseBurner} {r.AmountSats} {r.Reason} {r.VfxTxHash} {r.VfxHeight}"))
                + "#" + string.Join("|", VBTCBridgeConsumedBurn.GetCollection().FindAll().Select(c => $"{c.BaseBurnTxHash} {c.TxType} {c.VfxTxHash} {c.BlockHeight}"));
            var live = Rows();
            Assert.Contains(burn, live);

            BlockRollbackUtility.WipeChainDerivedState();
            Assert.Equal(0, VBTCBridgeRefundState.GetCollection().Count());
            SeedAccounts();
            StateWriteContext.Clear();
            Globals.LastBlock = new VerifiedXCore.Models.Block { Height = H1 - 2 };
            foreach (var block in AppliedBlocks.ToList())
            {
                Assert.True(await StateData.UpdateTreis(block), $"block {block.Height} did not apply on replay");
                Globals.LastBlock = block;
            }
            Assert.Equal(live, Rows());
        }

        [Fact]
        public async Task ARefundAttestation_IsGivenOnlyForTheRecordedRefund_BeyondTheReorgWindow_InTheTypedForm()
        {
            await Start();
            SeatTheCommittee();
            Globals.BridgeBurnRefundHeight = Tip + 1;
            var burn = NewBurn();
            await Mine(Refund(burn, BelowSmallestExit));

            var validator = NewKey();
            AccountData.GetAccounts().InsertSafe(new Account { Address = validator.Address, PublicKey = validator.Pub, PrivateKey = "0" + validator.Key.secret.ToString("x") });
            var validatorBase = ValidatorEthKeyService.DeriveBaseAddressFromVfxPublicKey(validator.Pub);
            const string configured = "0x09096c6eBf35F0aDAEeC02D06a0dE77E790F132D";
            var priorValidator = Globals.ValidatorAddress;
            var priorSynced = Globals.IsChainSynced;
            var priorResyncing = Globals.IsResyncing;
            var priorStopTimers = Globals.StopAllTimers;
            var priorContract = BaseBridgeService.ContractAddress;
            var priorBurnEvents = BaseBridgeService.BurnEventsForTests;
            var onBase = new BaseBridgeService.BurnEventInfo { AmountSats = BelowSmallestExit, Destination = "bc1q", Burner = Burner };
            BaseBridgeService.BurnEventsForTests = (id, vfx) => id == burn && !vfx ? (true, onBase, "") : (false, null, "No matching burn event emitted by the bridge contract");
            Globals.ValidatorAddress = validator.Address;
            Globals.IsChainSynced = true;
            Globals.IsResyncing = false;
            Globals.StopAllTimers = false;
            BaseBridgeService.ContractAddress = configured;
            try
            {
                (bool Ok, string? Sig, string? Error) Ask(string? id = null, string? to = null, long? sats = null, string? contract = null, int version = VbtcbTypedData.MessageVersion) =>
                    BaseBridgeAttestationService.HandleRefundAttestationRequestAsync(new RefundAttestationRequest
                    {
                        RefundId = id ?? burn, To = to ?? Burner, AmountSats = sats ?? BelowSmallestExit,
                        ChainId = Globals.BaseEvmChainId, ContractAddress = contract ?? configured, MessageVersion = version,
                    }).GetAwaiter().GetResult();

                Assert.Equal(BaseBridgeAttestationService.RefundInsideReorgWindowReason, Ask().Error);
                for (var i = 0; i < BaseBridgeAttestationService.MintAttestationDepth; i++) await Apply(Tip + 1);

                Assert.Equal(BaseBridgeAttestationService.RefundNotFoundReason, Ask(id: NewBurn()).Error);
                Assert.Equal(BaseBridgeAttestationService.RefundNotFoundReason, Ask(id: "0x" + burn[2..].ToUpperInvariant()).Error);
                Assert.Equal("Refund recipient mismatch", Ask(to: "0x" + new string('c', 40)).Error);
                Assert.Equal("Amount mismatch", Ask(sats: BelowSmallestExit + 1).Error);
                Assert.Equal(BaseBridgeAttestationService.ContractOrChainMismatchReason, Ask(contract: "0x" + new string('d', 40)).Error);
                Assert.Equal(BaseBridgeAttestationService.UnsupportedMessageVersionReason, Ask(version: 3).Error);

                // Recorded by the casters alone, so each validator reads the burn on Base before signing: a burn Base does not
                // show, or one by another burner or of another amount, is refused.
                BaseBridgeService.BurnEventsForTests = (_, _) => (false, null, "No matching burn event emitted by the bridge contract");
                Assert.StartsWith(BaseBridgeAttestationService.BurnNotOnBaseReason, Ask().Error);
                BaseBridgeService.BurnEventsForTests = (_, _) => (true, new BaseBridgeService.BurnEventInfo { AmountSats = BelowSmallestExit, Burner = "0x" + new string('e', 40) }, "");
                Assert.Equal(BaseBridgeAttestationService.BurnDoesNotMatchRefundReason, Ask().Error);
                BaseBridgeService.BurnEventsForTests = (_, _) => (true, new BaseBridgeService.BurnEventInfo { AmountSats = BelowSmallestExit + 1, Burner = Burner }, "");
                Assert.Equal(BaseBridgeAttestationService.BurnDoesNotMatchRefundReason, Ask().Error);
                BaseBridgeService.BurnEventsForTests = (id, vfx) => id == burn && !vfx ? (true, onBase, "") : (false, null, "none");

                var (ok, sig, error) = Ask();
                Assert.True(ok, error);
                var digest = VbtcbTypedData.RefundDigest(Burner, BelowSmallestExit, burn, Globals.BaseEvmChainId, configured);
                Assert.Equal(validatorBase, ValidatorEthKeyService.RecoverSigner(digest, sig, personalMessage: false), StringComparer.OrdinalIgnoreCase);
            }
            finally
            {
                Globals.ValidatorAddress = priorValidator;
                Globals.IsChainSynced = priorSynced;
                Globals.IsResyncing = priorResyncing;
                Globals.StopAllTimers = priorStopTimers;
                BaseBridgeService.ContractAddress = priorContract;
                BaseBridgeService.BurnEventsForTests = priorBurnEvents;
            }
        }

        [Fact]
        public async Task AtAdmission_ARefundIsCheckedAgainstTheBurnOnBase()
        {
            await Start();
            SeatTheCommittee();
            Globals.BridgeBurnRefundHeight = Tip + 1;
            var burn = NewBurn();
            var priorContract = BaseBridgeService.ContractAddress;
            var priorEvents = BaseBridgeService.BurnEventsForTests;
            BaseBridgeService.ContractAddress = "0x" + new string('d', 40);
            var onBase = new BaseBridgeService.BurnEventInfo { AmountSats = BelowSmallestExit, Destination = BtcDest, Burner = Burner };
            BaseBridgeService.BurnEventsForTests = (id, vfx) => id == burn && !vfx ? (true, onBase, "") : (false, null, "No matching burn event emitted by the bridge contract");
            try
            {
                async Task<string> Admit(VfxTx tx) => (await VerifiedXCore.Services.TransactionValidatorService.VerifyTX(tx)).Item2;
                Assert.Contains("could not be verified", await Admit(Refund(NewBurn(), BelowSmallestExit)));
                onBase.Burner = "0x" + new string('c', 40);
                Assert.Contains("burner does not match", await Admit(Refund(burn, BelowSmallestExit)));
                onBase.Burner = Burner.ToUpperInvariant().Replace("0X", "0x");
                onBase.AmountSats = BelowSmallestExit - 5;
                Assert.Contains("amount does not match", await Admit(Refund(burn, BelowSmallestExit)));
                onBase.AmountSats = BelowSmallestExit;
                // A block does not ask Base: the votes decide it there.
                BaseBridgeService.BurnEventsForTests = (_, _) => (false, null, "Could not read Base receipt");
                Assert.True((await Verify(Refund(burn, BelowSmallestExit), Tip + 1)).Ok);
            }
            finally
            {
                BaseBridgeService.ContractAddress = priorContract;
                BaseBridgeService.BurnEventsForTests = priorEvents;
            }
        }
    }
}
