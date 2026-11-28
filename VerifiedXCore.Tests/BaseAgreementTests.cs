using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Nethereum.Util;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// What decides something of value on Base is not one provider's word. Every provider here serves this network's
    /// Base chain; one of them does not tell the truth. A burn only it serves gets no vote from this caster; a receipt
    /// only it denies does not fail a burn; the contract's admin nonce and an implementation's code are signed for only
    /// as two providers give them alike; the burn watch passes a range only once two have read it, and takes a burn
    /// any of them serves as one to look into. Driven through the watch's own poll, the casters' check of a burn and
    /// the operator's signing, against providers on loopback.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BaseAgreementTests : BaseProviderTestBase
    {
        private static object StaticOf(string field) =>
            typeof(BurnExitConsensusService).GetField(field, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        /// <summary>Whether this node holds a confirmation of its own (its signed vote) for the burn.</summary>
        private static bool VotedFor(string burn) =>
            ((IDictionary)StaticOf("_confirmations")).Contains(burn)
            && ((IDictionary)((IDictionary)StaticOf("_confirmations"))[burn]!).Contains(Globals.ValidatorAddress);

        private static void Forget(string burn)
        {
            foreach (var field in new[] { "_processedBurns", "_confirmations", "_proposals" })
                ((IDictionary)StaticOf(field)).Remove(burn);
        }

        private static BurnExitConsensusService.ProcessedBurnRecord Record(string burn, decimal amount, string destination) => new()
        {
            BaseBurnTxHash = burn, ExitType = BurnExitConsensusService.BurnExitType.BtcExit, Amount = amount, BtcDestination = destination,
        };

        // ── A burn only one provider serves ──────────────────────────────────────────────

        /// <summary>
        /// One provider serves a burn's log and its receipt, in a final block; the other two have no such transaction.
        /// The watch of a caster in the committee takes it as a burn to look into, and the caster gives it no vote.
        /// </summary>
        [Fact]
        public async Task ABurnOnlyOneProviderServes_IsLookedInto_AndGetsNoVote()
        {
            ThisNodeIsAValidator();
            var me = Globals.ValidatorAddress;
            Globals.BootstrapCasterAddresses.Add(me);
            Globals.IsBlockCaster = true;
            var priorCasters = Globals.BlockCasters;
            Globals.BlockCasters = new System.Collections.Concurrent.ConcurrentBag<VerifiedXCore.Models.Peers>(); // no other caster to send the burn's alert to
            var burn = NewBurn();
            try
            {
                var all = Providers(3);
                all.At(5_000);
                var state = BridgeExitSyncState.GetOrCreate();
                state.LastScannedBlock = 4_990;
                BridgeExitSyncState.Save(state);
                all[0].AddBurn(burn, Contract, vfxExit: false, 400_000, "bc1qdestination", blockNumber: 4_995);

                var (taken, scannedTo, _) = await BaseBridgeExitWatchService.PollOnce();
                Assert.Equal(1, taken);          // handed to the casters' consensus
                Assert.Equal(5_000, scannedTo);  // and the range is passed: two providers read it

                // The round this caster runs for it ends at its own check of the burn, before any vote.
                BurnExitConsensusService.ProcessedBurnRecord? record = null;
                for (var i = 0; i < 150 && !VotedFor(burn); i++)
                {
                    record = BurnExitConsensusService.ProcessedBurnForTests(burn);
                    if (record is { NextAttemptAt: > 0 }) break;
                    await Task.Delay(100);
                }
                Assert.False(VotedFor(burn), "the caster signed a vote for a burn one provider serves");
                Assert.NotNull(record);
                Assert.Equal(BurnExitConsensusService.BurnExitStatus.Pending, record!.Status); // asked again later, not failed
                Assert.False(record.FoundOnBase);
                Assert.Empty(record.ConsensusVotes);

                var (ok, transient, reason, _) = await BurnExitConsensusService.VerifyBurnEvidenceAsync(Record(burn, 0.004M, "bc1qdestination"));
                Assert.False(ok);
                Assert.True(transient, reason);
                Assert.Contains(BaseRpc.NoAgreement, reason);
            }
            finally
            {
                Globals.BlockCasters = priorCasters;
                Globals.BootstrapCasterAddresses.Remove(me);
                Forget(burn);
            }
        }

        /// <summary>A second provider serving the same burn in the same block is what makes it one; a different amount is not the same burn.</summary>
        [Fact]
        public async Task ABurn_IsOne_OnceTwoProvidersServeTheSameBurnInTheSameBlock()
        {
            var all = Providers(3);
            var burn = NewBurn();
            all[0].AddBurn(burn, Contract, vfxExit: false, 400_000, "bc1qdestination");
            var record = Record(burn, 0.004M, "bc1qdestination");

            // A second provider serves a burn of another amount for the transaction: two answers, not the same one.
            all[1].AddBurn(burn, Contract, vfxExit: false, 900_000, "bc1qdestination");
            var (ok, transient, reason, _) = await BurnExitConsensusService.VerifyBurnEvidenceAsync(record);
            Assert.False(ok);
            Assert.True(transient, reason);

            // The same burn, in another block than the first provider names: not the same answer either.
            all[1].Receipts.Clear();
            all[1].AddBurn(burn, Contract, vfxExit: false, 400_000, "bc1qdestination", blockNumber: 901);
            (ok, transient, reason, _) = await BurnExitConsensusService.VerifyBurnEvidenceAsync(record);
            Assert.False(ok);
            Assert.True(transient, reason);

            // The same burn in the same block from a second provider: it is one.
            all[2].AddBurn(burn, Contract, vfxExit: false, 400_000, "bc1qdestination");
            (ok, transient, reason, var evidence) = await BurnExitConsensusService.VerifyBurnEvidenceAsync(record);
            Assert.True(ok, reason);
            Assert.Equal(400_000, evidence!.AmountSats);
        }

        /// <summary>"It is not a burn" needs two providers as well: one provider denying a burn does not fail it.</summary>
        [Fact]
        public async Task OneProviderDenyingABurn_DoesNotFailIt()
        {
            var all = Providers(3);
            var burn = NewBurn();
            var log = FakeBaseRpc.BurnLog(burn, Contract, vfxExit: false, 400_000, "bc1qdestination", Globals.BaseEvmChainId);
            all.Skip(1).AddBurn(burn, Contract, vfxExit: false, 400_000, "bc1qdestination");
            all[0].Receipts[burn] = FakeBaseRpc.Receipt(burn, succeeded: false, log); // this one says the transaction failed
            var record = Record(burn, 0.004M, "bc1qdestination");

            var (ok, _, reason, _) = await BurnExitConsensusService.VerifyBurnEvidenceAsync(record);
            Assert.True(ok, reason); // the two that agree decide

            // With only one other provider answering, the denial is still no answer: the burn waits, it is not failed.
            all[2].Down = true;
            BaseRpc.ForgetChainChecksForTests();
            (ok, var transient, reason, _) = await BurnExitConsensusService.VerifyBurnEvidenceAsync(record);
            Assert.False(ok);
            Assert.True(transient, reason);
            Assert.Equal(BaseBridgeService.BurnVerdict.NotYet, (await BaseBridgeService.ReadBurnAsync(burn, vfxExit: false)).Verdict);

            // Two providers giving the denial alike is an answer.
            all[1].Receipts[burn] = FakeBaseRpc.Receipt(burn, succeeded: false, log);
            (ok, transient, reason, _) = await BurnExitConsensusService.VerifyBurnEvidenceAsync(record);
            Assert.False(ok);
            Assert.False(transient, reason);
        }

        /// <summary>The same provider written into two of the node's settings is one provider: it does not agree with itself.</summary>
        [Fact]
        public async Task TheSameProviderConfiguredTwice_DoesNotAgreeWithItself()
        {
            var one = Provider();
            var burn = NewBurn();
            one.AddBurn(burn, Contract, vfxExit: false, 400_000, "bc1qdestination");
            BaseBridgeService.BaseRpcUrl = one.Url;
            BaseBridgeService.BaseRpcUrl2 = one.Url;
            BaseBridgeService.BaseRpcUrl3 = one.Url + "?key=another";

            var read = await BaseBridgeService.ReadBurnAsync(burn, vfxExit: false);
            Assert.Equal(BaseBridgeService.BurnVerdict.NotYet, read.Verdict);
            Assert.Equal(1, one.CallsTo("eth_getTransactionReceipt"));

            var other = Provider();
            other.AddBurn(burn, Contract, vfxExit: false, 400_000, "bc1qdestination");
            BaseBridgeService.BaseRpcUrl3 = other.Url;
            Assert.Equal(BaseBridgeService.BurnVerdict.Burned, (await BaseBridgeService.ReadBurnAsync(burn, vfxExit: false)).Verdict);
        }

        // ── The burn watch ───────────────────────────────────────────────────────────────

        /// <summary>A provider that leaves a burn out of its logs does not hide it: another provider's log is enough to look into it.</summary>
        [Fact]
        public async Task TheBurnWatch_TakesABurnAnyProviderServes_SoOneProviderCannotHideIt()
        {
            var all = Providers(3);
            all.At(5_000);
            var state = BridgeExitSyncState.GetOrCreate();
            state.LastScannedBlock = 4_990;
            BridgeExitSyncState.Save(state);
            all.Skip(1).AddBurn(NewBurn(), Contract, vfxExit: true, 300_000, "xDestination", blockNumber: 4_995); // the first provider leaves it out

            var (taken, scannedTo, _) = await BaseBridgeExitWatchService.PollOnce();
            Assert.Equal(1, taken);
            Assert.Equal(5_000, scannedTo);
        }

        /// <summary>
        /// The watch passes a range only once two providers have read it. With one provider left to read from it reads
        /// nothing, so a burn that provider leaves out is not left behind the cursor; it is read once a second answers.
        /// </summary>
        [Fact]
        public async Task TheBurnWatch_PassesARangeOnlyOnceTwoProvidersHaveReadIt()
        {
            var all = Providers(3);
            all.At(5_000);
            var state = BridgeExitSyncState.GetOrCreate();
            state.LastScannedBlock = 4_990;
            BridgeExitSyncState.Save(state);
            all.Skip(1).AddBurn(NewBurn(), Contract, vfxExit: true, 300_000, "xDestination", blockNumber: 4_995); // the first provider leaves it out
            all[1].Down = true;
            all[2].Down = true;

            for (var i = 0; i < 3; i++)
                await Assert.ThrowsAsync<BaseProviderException>(() => BaseBridgeExitWatchService.PollOnce());
            Assert.Equal(4_990, BridgeExitSyncState.GetOrCreate().LastScannedBlock);
            Assert.Equal(0, all[0].CallsTo("eth_getLogs"));

            all[1].Down = false;
            var (taken, scannedTo, _) = await BaseBridgeExitWatchService.PollOnce();
            Assert.Equal(1, taken);
            Assert.Equal(5_000, scannedTo);
        }

        // ── The contract, as the operator's signing reads it ─────────────────────────────

        [Fact]
        public async Task TheOperatorsSigning_TakesTheContractsNonceAndAnUpgradesCode_OnlyAsTwoProvidersGiveThemAlike()
        {
            var myBase = ThisNodeIsAValidator();
            const string implementation = "0xd2bB4BdA89A23c63eaaD464F15658845a2114Ffa";
            // One provider misstates the contract (admin nonce 99) and the implementation's code; the others give nonce 5.
            var misstating = ContractProvider(null, myBase, nonce: 99, codeHex: "0xdeadbeef");
            var first = ContractProvider(null, myBase, nonce: 5, codeHex: "0x6080604052");
            var second = ContractProvider(null, myBase, nonce: 5, codeHex: "0x6080604052");

            // Beside one other provider there is no answer to sign on, whichever of the two is asked first.
            foreach (var order in new[] { new[] { misstating, first }, new[] { first, misstating } })
            {
                Configure(order);
                var cancel = await BaseAdminSigningService.SignAsync(new BaseAdminSigningService.Request { Action = BaseAdminSigningService.CancelPending });
                Assert.False(cancel.Success);
                Assert.Equal("Could not read the Base contract", cancel.Error);
                Assert.Null(cancel.Signature);
            }

            // Beside two that agree, what is signed for is what those two give.
            Configure(misstating, first, second);
            var agreed = await BaseAdminSigningService.SignAsync(new BaseAdminSigningService.Request { Action = BaseAdminSigningService.CancelPending });
            Assert.True(agreed.Success, agreed.Error);
            Assert.Equal("5", agreed.AdminNonce);
            var upgrade = await BaseAdminSigningService.SignAsync(new BaseAdminSigningService.Request { Action = BaseAdminSigningService.ProposeUpgrade, Implementation = implementation });
            Assert.True(upgrade.Success, upgrade.Error);
            Assert.Equal("0x" + Convert.ToHexString(Sha3Keccack.Current.CalculateHash(Convert.FromHexString("6080604052"))).ToLowerInvariant(), upgrade.ImplementationCodeHash);

            // The contract agreed, the code not: two providers give the views alike, one of them another code.
            var otherCode = ContractProvider(null, myBase, nonce: 5, codeHex: "0xdeadbeef");
            Configure(otherCode, first);
            var noCode = await BaseAdminSigningService.SignAsync(new BaseAdminSigningService.Request { Action = BaseAdminSigningService.ProposeUpgrade, Implementation = implementation });
            Assert.False(noCode.Success);
            Assert.Equal("Could not read the implementation's code", noCode.Error);
            Assert.Null(noCode.Signature);
        }
    }
}
