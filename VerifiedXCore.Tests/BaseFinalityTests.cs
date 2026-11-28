using System;
using System.Linq;
using System.Threading.Tasks;
using Nethereum.Util;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// What decides something of value is read from Base only once it is final there. The casters' burn watch reads no
    /// further than the provider's finalized block; a caster's check of a burn takes its receipt only from a block at or
    /// below that one whose hash is still the chain's; the contract's signers, admin nonce and code are read at that
    /// one block. What is newer is "not yet", asked again later, never a refusal of the burn. Driven through the watch's
    /// own poll, the casters' check of a burn and the operator's signing, against providers on loopback.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BaseFinalityTests : BaseProviderTestBase
    {
        // ── The casters' burn watch ──────────────────────────────────────────────────────

        [Fact]
        public async Task TheBurnWatch_ReadsNoFurtherThanTheFinalizedBlock_AndTakesABurnOnceItsBlockIsFinal()
        {
            var provider = Provider();
            provider.Head = 5_000;
            provider.Finalized = 4_900;
            Configure(provider);
            var state = BridgeExitSyncState.GetOrCreate();
            state.LastScannedBlock = 4_890;
            BridgeExitSyncState.Save(state);
            provider.AddBurn(NewBurn(), Contract, vfxExit: false, 400_000, "bc1qdestination", blockNumber: 4_950); // newer than the finalized block

            var (taken, scannedTo, _) = await BaseBridgeExitWatchService.PollOnce();

            Assert.Equal(0, taken);
            Assert.Equal(4_900, scannedTo);
            Assert.Equal(4_900, BridgeExitSyncState.GetOrCreate().LastScannedBlock);
            Assert.Equal(4_900, provider.HighestLogBlockAsked);          // nothing above the finalized block was read
            Assert.True(provider.FinalizedAsked > 0);                    // by its tag, as the provider reads it
            Assert.Equal(0, provider.CallsTo("eth_blockNumber"));        // the newest block is not what the watch reads to

            // Nothing more until Base finalizes further.
            (taken, scannedTo, _) = await BaseBridgeExitWatchService.PollOnce();
            Assert.Equal(0, taken);
            Assert.Equal(4_900, scannedTo);
            Assert.Equal(4_900, provider.HighestLogBlockAsked);

            // The burn's block is final: now it is read and taken.
            provider.Finalized = 4_960;
            (taken, scannedTo, _) = await BaseBridgeExitWatchService.PollOnce();
            Assert.Equal(1, taken);
            Assert.Equal(4_960, scannedTo);
            Assert.Equal(4_960, provider.HighestLogBlockAsked);
        }

        [Fact]
        public async Task TheBurnWatch_StartsItsCursorJustBelowTheFinalizedBlock_SoWhatIsNotFinalYetIsStillRead()
        {
            var provider = Provider();
            provider.Head = 5_000;
            provider.Finalized = 4_900;
            Configure(provider);
            provider.AddBurn(NewBurn(), Contract, vfxExit: true, 300_000, "xDestination", blockNumber: 4_950);

            await BaseBridgeExitWatchService.PollOnce();
            Assert.Equal(4_899, BridgeExitSyncState.GetOrCreate().LastScannedBlock);

            provider.Finalized = 5_000;
            var taken = 0;
            for (var i = 0; i < 5 && BridgeExitSyncState.GetOrCreate().LastScannedBlock < 5_000; i++)
                taken += (await BaseBridgeExitWatchService.PollOnce()).Processed;
            Assert.Equal(5_000, BridgeExitSyncState.GetOrCreate().LastScannedBlock);
            Assert.Equal(1, taken);
        }

        // ── A caster's check of a burn before it votes ───────────────────────────────────

        private static BurnExitConsensusService.ProcessedBurnRecord Record(string burn, decimal amount, string destination) => new()
        {
            BaseBurnTxHash = burn, ExitType = BurnExitConsensusService.BurnExitType.BtcExit, Amount = amount, BtcDestination = destination,
        };

        [Fact]
        public async Task ACastersCheckOfABurn_IsNotYet_UntilTheBurnsBlockIsFinal()
        {
            var provider = Provider();
            provider.Head = 1_000;
            provider.Finalized = 940;
            Configure(provider);
            var burn = NewBurn();
            provider.AddBurn(burn, Contract, vfxExit: false, 400_000, "bc1qdestination", blockNumber: 950);
            var record = Record(burn, 0.004M, "bc1qdestination");

            var (ok, transient, reason, evidence) = await BurnExitConsensusService.VerifyBurnEvidenceAsync(record);
            Assert.False(ok);
            Assert.True(transient, reason);                                  // asked again, not failed
            Assert.Null(evidence);
            Assert.Contains(BaseRpc.ReceiptNotFinal, reason);
            Assert.Equal(BurnExitConsensusService.BurnExitType.BtcExit, record.ExitType); // and not looked up as the other type

            // The typed answer behind it, and the reason an exit transaction would be refused with.
            var read = await BaseBridgeService.ReadBurnAsync(burn, vfxExit: false);
            Assert.Equal(BaseBridgeService.BurnVerdict.NotYet, read.Verdict);
            Assert.True(BaseBridgeService.IsNotYet(read.Reason), read.Reason);

            provider.Finalized = 950;
            (ok, transient, reason, evidence) = await BurnExitConsensusService.VerifyBurnEvidenceAsync(record);
            Assert.True(ok, reason);
            Assert.False(transient);
            Assert.Equal(400_000, evidence!.AmountSats);
        }

        [Fact]
        public async Task ACastersCheckOfABurn_IsNotYet_WhileItsReceiptNamesABlockTheChainDoesNotHold()
        {
            var provider = Provider();
            Configure(provider);
            var burn = NewBurn();
            provider.AddBurn(burn, Contract, vfxExit: false, 400_000, "bc1qdestination", blockNumber: 900);
            provider.BlockHashes[900] = FakeBaseRpc.BlockHash("another block at 900"); // the chain holds another block there
            var record = Record(burn, 0.004M, "bc1qdestination");

            var (ok, transient, reason, _) = await BurnExitConsensusService.VerifyBurnEvidenceAsync(record);
            Assert.False(ok);
            Assert.True(transient, reason);
            Assert.Contains(BaseRpc.ReceiptOffChain, reason);

            provider.BlockHashes.Clear();
            (ok, _, reason, _) = await BurnExitConsensusService.VerifyBurnEvidenceAsync(record);
            Assert.True(ok, reason);
        }

        [Fact]
        public async Task AFinalReceiptThatIsNotTheBurn_IsAnAnswer_NotNotYet()
        {
            var provider = Provider();
            Configure(provider);
            var burn = NewBurn();
            var log = FakeBaseRpc.BurnLog(burn, Contract, vfxExit: false, 400_000, "bc1qdestination", Globals.BaseEvmChainId, blockNumber: 900);
            provider.Receipts[burn] = FakeBaseRpc.Receipt(burn, succeeded: false, log);

            var (ok, transient, reason, _) = await BurnExitConsensusService.VerifyBurnEvidenceAsync(Record(burn, 0.004M, "bc1qdestination"));
            Assert.False(ok);
            Assert.False(transient, reason);
            var read = await BaseBridgeService.ReadBurnAsync(burn, vfxExit: false);
            Assert.Equal(BaseBridgeService.BurnVerdict.NotABurn, read.Verdict);
            Assert.False(BaseBridgeService.IsNotYet(read.Reason));
        }

        // ── The contract, as the operator's signing reads it ─────────────────────────────

        [Fact]
        public async Task TheOperatorsSigning_ReadsTheContractAtTheFinalizedBlock_OneBlockForEveryCall()
        {
            var myBase = ThisNodeIsAValidator();
            const string implementation = "0xd2bB4BdA89A23c63eaaD464F15658845a2114Ffa";
            var finalized = FakeBaseRpc.Hex(900);
            // The contract's admin nonce moved from 5 to 6, and the implementation's code changed, after the finalized block.
            var provider = ContractProvider(null, myBase,
                nonceAt: at => at == finalized ? 5 : 6,
                codeAt: at => at == finalized ? "0x6080604052" : "0xdeadbeef");
            provider.Head = 1_000;
            provider.Finalized = 900;
            Configure(provider);

            var cancel = await BaseAdminSigningService.SignAsync(new BaseAdminSigningService.Request { Action = BaseAdminSigningService.CancelPending });
            Assert.True(cancel.Success, cancel.Error);
            Assert.Equal("5", cancel.AdminNonce);
            Assert.Equal(6, provider.StateBlocksAsked.Count);
            Assert.All(provider.StateBlocksAsked, at => Assert.Equal(finalized, at));

            var upgrade = await BaseAdminSigningService.SignAsync(new BaseAdminSigningService.Request { Action = BaseAdminSigningService.ProposeUpgrade, Implementation = implementation });
            Assert.True(upgrade.Success, upgrade.Error);
            Assert.Equal("0x" + Convert.ToHexString(Sha3Keccack.Current.CalculateHash(Convert.FromHexString("6080604052"))).ToLowerInvariant(), upgrade.ImplementationCodeHash);
            Assert.All(provider.StateBlocksAsked, at => Assert.Equal(finalized, at));
            Assert.Equal(13, provider.StateBlocksAsked.Count); // six more views and the code

            // A provider that names no finalized block gives no answer to sign on.
            provider.On = (method, args) => method == "eth_getBlockByNumber" && args[0]!.ToString() == "finalized" ? Newtonsoft.Json.Linq.JValue.CreateNull() : null;
            var none = await BaseAdminSigningService.SignAsync(new BaseAdminSigningService.Request { Action = BaseAdminSigningService.CancelPending });
            Assert.False(none.Success);
            Assert.Equal("Could not read the Base contract", none.Error);
        }
    }
}
