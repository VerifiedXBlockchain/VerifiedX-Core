using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Controllers;
using VerifiedXCore.Data;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;
using VfxTx = VerifiedXCore.Models.Transaction;
using SignatureService = VerifiedXCore.Services.SignatureService;
using BurnExitType = VerifiedXCore.Bitcoin.Services.BurnExitConsensusService.BurnExitType;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Where a bridge exit's burn is read on Base, and where it is not. When an exit is first admitted (the node's own
    /// call, the raw route) the burn is looked up through the configured providers, and only through those that serve
    /// this network's Base chain; a burn event that names another chain is not a burn. As a transaction of a block the
    /// exit is judged on its committee votes alone and Base is not asked at all, whatever the providers would say.
    /// Judged by VerifyTX and the raw route against providers on loopback.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BridgeExitBaseReadTests : BtcAttestTestBase
    {
        private const string BtcDest = "bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4";
        private const string Contract = "0x09096c6eBf35F0aDAEeC02D06a0dE77E790F132D";
        private const string Vault = "0123456789abcdef0123456789abcdef:1790600000";
        private const int Casters = 5;
        private const long OtherChain = 1;

        private readonly long _priorBinding = Globals.BridgeBurnBindingHeight;
        private readonly long _priorGuard = Globals.BridgeIntraBlockGuardHeight;
        private readonly long _priorBound = Globals.BridgeVotesBoundToAttemptHeight;
        private readonly Network? _priorNetwork = Globals.BTCNetwork;
        private readonly string _priorRpc = BaseBridgeService.BaseRpcUrl;
        private readonly string _priorRpc2 = BaseBridgeService.BaseRpcUrl2;
        private readonly string _priorRpc3 = BaseBridgeService.BaseRpcUrl3;
        private readonly string _priorContract = BaseBridgeService.ContractAddress;
        private readonly List<V> _casters = Enumerable.Range(0, Casters).Select(_ => NewKey()).ToList();
        private readonly List<FakeBaseRpc> _providers = new();

        public BridgeExitBaseReadTests() : base(realMainnetRules: false)
        {
            Globals.BridgeBurnBindingHeight = 1;
            Globals.BridgeIntraBlockGuardHeight = 1;
            Globals.BridgeVotesBoundToAttemptHeight = long.MaxValue;
            Globals.BTCNetwork = Network.Main;
            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei { SmartContractUID = Vault, ContractData = VbtcTestContracts.VbtcV2ContractData, MinterAddress = Minter.Address, OwnerAddress = Minter.Address });
            foreach (var caster in _casters)
            {
                Globals.BootstrapCasterAddresses.Add(caster.Address);
                StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = caster.Address, Balance = 10M, Nonce = 0 });
            }
            BaseBridgeService.ContractAddress = Contract;
            BaseBridgeService.BaseRpcUrl = "";
            BaseBridgeService.BaseRpcUrl2 = "";
            BaseBridgeService.BaseRpcUrl3 = "";
            BaseRpc.ForgetChainChecksForTests();
        }

        public override void Dispose()
        {
            foreach (var p in _providers) p.Dispose();
            BaseRpc.ForgetChainChecksForTests();
            BaseBridgeService.BaseRpcUrl = _priorRpc;
            BaseBridgeService.BaseRpcUrl2 = _priorRpc2;
            BaseBridgeService.BaseRpcUrl3 = _priorRpc3;
            BaseBridgeService.ContractAddress = _priorContract;
            foreach (var caster in _casters) Globals.BootstrapCasterAddresses.Remove(caster.Address);
            Globals.BTCNetwork = _priorNetwork;
            Globals.BridgeVotesBoundToAttemptHeight = _priorBound;
            Globals.BridgeIntraBlockGuardHeight = _priorGuard;
            Globals.BridgeBurnBindingHeight = _priorBinding;
            base.Dispose();
        }

        private static long Tip => Globals.LastBlock.Height;
        private V Handler => _casters[0];

        private FakeBaseRpc Provider(long? chainId = null)
        {
            var p = new FakeBaseRpc { ChainId = chainId };
            _providers.Add(p);
            return p;
        }

        private static void Configure(params FakeBaseRpc[] providers)
        {
            BaseBridgeService.BaseRpcUrl = providers.Length > 0 ? providers[0].Url : "";
            BaseBridgeService.BaseRpcUrl2 = providers.Length > 1 ? providers[1].Url : "";
            BaseBridgeService.BaseRpcUrl3 = providers.Length > 2 ? providers[2].Url : "";
        }

        private static string NewBurn() => "0x" + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

        private string Lock(decimal amount)
        {
            var id = "lock-" + Guid.NewGuid().ToString("N");
            VBTCBridgeLockState.GetCollection().Insert(new VBTCBridgeLockState
            {
                LockId = id, SmartContractUID = Vault, OwnerAddress = Minter.Address, Amount = amount, AmountSats = (long)(amount * 100_000_000M),
                EvmDestination = "0x" + new string('a', 40), LockTimestamp = TimeUtil.GetTime(),
            });
            return id;
        }

        /// <summary>Every caster's vote for the burn as a round leaves it, naming the handler (BurnExitConsensusService.VotesOf).</summary>
        private List<CasterConsensusVote> Votes(string burn, string type, long sats, string destination, long timestamp) =>
            BurnExitConsensusService.VotesOf(_casters.Select(caster => new BurnExitConsensusService.BurnExitConfirmation
            {
                BaseBurnTxHash = burn,
                ConfirmingCasterAddress = caster.Address,
                AgreedHandlerAddress = Handler.Address,
                Timestamp = timestamp,
                Signature = SignatureService.CreateSignature(BridgeCasterConsensus.BuildBoundVoteMessage(burn, type, sats, destination, timestamp), caster.Key, caster.Pub),
                HandlerSignature = SignatureService.CreateSignature(
                    BridgeCasterConsensus.BuildConfirmationHandlerMessage(burn, caster.Address, Handler.Address, timestamp), caster.Key, caster.Pub),
            }), type == "BTC_EXIT" ? BurnExitType.BtcExit : BurnExitType.VfxPoolUnlock).ToList();

        private static VfxTx Signed(V sender, TransactionType type, JObject data)
        {
            var tx = new VfxTx
            {
                Timestamp = TimeUtil.GetTime(), FromAddress = sender.Address, ToAddress = sender.Address, Amount = 0.0M, Fee = 0.0M, Nonce = 0,
                TransactionType = type, Data = data.ToString(Formatting.None),
            };
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, sender.Key, sender.Pub);
            return tx;
        }

        /// <summary>The exit to Bitcoin the handler sends for a burn.</summary>
        private VfxTx Exit(string burn, decimal amount, string lockId, long voted)
        {
            var sats = (long)(amount * 100_000_000M);
            var allocations = new List<PoolUnlockAllocation> { new() { LockId = lockId, SmartContractUID = Vault, UnlockAmount = amount } };
            var data = new JObject
            {
                ["Function"] = "VBTCBridgeExitToBTC()", ["ContractUID"] = Vault, ["LockId"] = lockId,
                ["Amount"] = amount, ["AmountSats"] = sats, ["TotalAmount"] = amount, ["TotalAmountSats"] = sats,
                ["BtcDestination"] = BtcDest, ["BaseBurnTxHash"] = burn,
                ["Allocations"] = JArray.FromObject(allocations), ["BtcWithdrawals"] = JValue.CreateNull(),
                ["CasterConsensusVotes"] = JArray.FromObject(Votes(burn, "BTC_EXIT", sats, BtcDest, voted)),
            };
            return Signed(Handler, TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC, data);
        }

        /// <summary>The pool unlock the handler sends for a burn.</summary>
        private VfxTx Unlock(string burn, decimal amount, string lockId, long voted)
        {
            var sats = (long)(amount * 100_000_000M);
            var data = new JObject
            {
                ["Function"] = "VBTCBridgePoolUnlock()", ["TotalAmount"] = amount, ["TotalAmountSats"] = sats, ["VfxDestinationAddress"] = Minter.Address,
                ["ExitBurnTxHash"] = burn,
                ["Allocations"] = JArray.FromObject(new[] { new PoolUnlockAllocation { LockId = lockId, SmartContractUID = Vault, UnlockAmount = amount } }),
                ["CasterConsensusVotes"] = JArray.FromObject(Votes(burn, "POOL_EXIT", sats, Minter.Address, voted)),
            };
            return Signed(Handler, TransactionType.VBTC_V2_BRIDGE_POOL_UNLOCK, data);
        }

        /// <summary>Block validation's verdict on a transaction of the block at <paramref name="height"/> stamped <paramref name="blockTime"/>.</summary>
        private static Task<(bool, string)> InBlock(VfxTx tx, long height, long blockTime) =>
            TransactionValidatorService.VerifyTX(tx, false, true, false, null, false, height, blockTime);

        private static void Accepted((bool Ok, string Why) verdict) => Assert.True(verdict.Ok, verdict.Why);

        private static void Refused((bool Ok, string Why) verdict, string because)
        {
            Assert.False(verdict.Ok, "accepted");
            Assert.Contains(because, verdict.Why);
        }

        private static async Task<JObject> Raw(VfxTx tx)
        {
            var body = JObject.Parse(JsonConvert.SerializeObject(tx));
            body["Data"] = JToken.Parse(tx.Data!);
            return JObject.Parse(await new TXV1Controller().SendRawTransaction(body));
        }

        /// <summary>
        /// At admission the burn is read on Base; as a block's transaction the same exit gets the same verdict with no
        /// request to any provider, also when the providers have no such burn or do not answer.
        /// </summary>
        [Fact]
        public async Task AnExitsBurnIsReadOnBaseAtAdmission_AndAsABlocksTransactionBaseIsNotAsked()
        {
            await Start();
            var now = TimeUtil.GetTime();
            var provider = Provider();
            Configure(provider);
            var poolBurn = NewBurn();
            var exitBurn = NewBurn();
            provider.AddBurn(poolBurn, Contract, vfxExit: true, 400_000, Minter.Address);
            provider.AddBurn(exitBurn, Contract, vfxExit: false, 400_000, BtcDest);
            var unlock = Unlock(poolBurn, 0.004M, Lock(0.004M), now);
            var exit = Exit(exitBurn, 0.004M, Lock(0.004M), now);

            Accepted(await TransactionValidatorService.VerifyTX(unlock));
            Accepted(await TransactionValidatorService.VerifyTX(exit));
            Assert.Equal(2, provider.CallsTo("eth_getTransactionReceipt"));

            // As a block's transactions: accepted, and nothing is asked of the provider (not even its chain id).
            provider.ForgetCalls();
            BaseRpc.ForgetChainChecksForTests();
            Accepted(await InBlock(unlock, Tip + 1, now));
            Accepted(await InBlock(exit, Tip + 1, now));
            Assert.Equal(0, provider.TotalCalls);

            // The provider no longer has the burns, then does not answer at all: a block's verdict is the same and
            // still asks nothing, while admission, which reads Base, refuses.
            provider.Receipts.Clear();
            Accepted(await InBlock(unlock, Tip + 1, now));
            Accepted(await InBlock(exit, Tip + 1, now));
            Assert.Equal(0, provider.TotalCalls);
            Refused(await TransactionValidatorService.VerifyTX(unlock), "could not be verified on Base");
            Refused(await TransactionValidatorService.VerifyTX(exit), "could not be verified");
            provider.Down = true;
            provider.ForgetCalls();
            BaseRpc.ForgetChainChecksForTests();
            Accepted(await InBlock(unlock, Tip + 1, now));
            Accepted(await InBlock(exit, Tip + 1, now));
            Assert.Equal(0, provider.TotalCalls);
        }

        /// <summary>
        /// A provider that serves another chain is not asked for the burn: its receipt, however well formed, admits no
        /// exit, on the node's own path and on the raw route.
        /// </summary>
        [Fact]
        public async Task AtAdmission_ABurnHeldOnlyByAProviderOnAnotherChain_AdmitsNoExit()
        {
            await Start();
            var now = TimeUtil.GetTime();
            var elsewhere = Provider(OtherChain);
            var onBase = Provider();
            Configure(elsewhere, onBase);
            var burn = NewBurn();
            elsewhere.AddBurn(burn, Contract, vfxExit: true, 400_000, Minter.Address, chainId: Globals.BaseEvmChainId);
            var unlock = Unlock(burn, 0.004M, Lock(0.004M), now);

            Refused(await TransactionValidatorService.VerifyTX(unlock), "could not be verified on Base");
            Assert.Equal("Fail", (await Raw(unlock))["Result"]?.ToString());
            Assert.Empty(TransactionData.GetPool().FindAll());
            Assert.Equal(0, elsewhere.CallsTo("eth_getTransactionReceipt"));
            Assert.True(onBase.CallsTo("eth_getTransactionReceipt") >= 2);

            // Once a provider on Base has the burn, the same exit is admitted on both paths.
            onBase.AddBurn(burn, Contract, vfxExit: true, 400_000, Minter.Address);
            Accepted(await TransactionValidatorService.VerifyTX(unlock));
            Assert.Equal("Success", (await Raw(unlock))["Result"]?.ToString());
            Assert.Equal(0, elsewhere.CallsTo("eth_getTransactionReceipt"));
        }

        /// <summary>The contract writes its chain's id into every burn: one that names another chain admits no exit.</summary>
        [Fact]
        public async Task AtAdmission_ABurnEventThatNamesAnotherChain_AdmitsNoExit()
        {
            await Start();
            var now = TimeUtil.GetTime();
            var provider = Provider();
            Configure(provider);
            var burn = NewBurn();
            provider.AddBurn(burn, Contract, vfxExit: false, 400_000, BtcDest, chainId: OtherChain);
            var exit = Exit(burn, 0.004M, Lock(0.004M), now);

            Refused(await TransactionValidatorService.VerifyTX(exit), BaseBridgeService.BurnOnAnotherChain);
            Assert.Equal("Fail", (await Raw(exit))["Result"]?.ToString());
            Assert.Empty(TransactionData.GetPool().FindAll());

            provider.Receipts.Clear();
            provider.AddBurn(burn, Contract, vfxExit: false, 400_000, BtcDest);
            Accepted(await TransactionValidatorService.VerifyTX(exit));
            Assert.Equal("Success", (await Raw(exit))["Result"]?.ToString());
        }

        /// <summary>
        /// A burn in a block that is not final on Base yet admits no exit, on the handler's own send, on VerifyTX and on
        /// the raw route. It is "not yet", not "no": the handler's burn goes back to be planned again, and once the
        /// block is final the same exit is sent. A block's verdict does not depend on it.
        /// </summary>
        [Fact]
        public async Task AnExitWhoseBurnIsNotFinalOnBaseYet_IsNotSent_AndItsBurnIsPlannedAgain()
        {
            await Start();
            AccountData.GetAccounts().InsertSafe(new Account { Address = Handler.Address, PublicKey = Handler.Pub, PrivateKey = "0" + Handler.Key.secret.ToString("x") });
            var now = TimeUtil.GetTime();
            var provider = Provider();
            provider.Finalized = 890;
            Configure(provider);
            var burn = NewBurn();
            provider.AddBurn(burn, Contract, vfxExit: false, 400_000, BtcDest, blockNumber: 900);
            var lockId = Lock(0.004M);
            var votes = Votes(burn, "BTC_EXIT", 400_000, BtcDest, now);
            var allocations = new List<PoolUnlockAllocation> { new() { LockId = lockId, SmartContractUID = Vault, UnlockAmount = 0.004M } };

            var (sent, why) = await VBTCService.CreateBridgeExitToBTCTx(Handler.Address, 0.004M, BtcDest, burn, allocations, null, votes);
            Assert.False(sent);
            Assert.Contains(BaseRpc.ReceiptNotFinal, why);
            Assert.True(BaseBridgeService.IsNotYet(why), why);
            Assert.Empty(TransactionData.GetPool().FindAll());

            // What the caster makes of an exit not sent for that reason: its burn is planned again, not given up.
            var record = new BurnExitConsensusService.ProcessedBurnRecord
            {
                BaseBurnTxHash = burn, ExitType = BurnExitType.BtcExit, Status = BurnExitConsensusService.BurnExitStatus.ConsensusReached,
                Amount = 0.004M, BtcDestination = BtcDest, DetectedAtBlock = Tip, FieldsFromBase = true,
            };
            BurnExitConsensusService.NoteExitNotSent(record, why);
            Assert.Equal(BurnExitConsensusService.BurnExitStatus.Pending, record.Status);
            Assert.True(record.NextAttemptAt > TimeUtil.GetTime());

            var exit = Exit(burn, 0.004M, lockId, now);
            Refused(await TransactionValidatorService.VerifyTX(exit), BaseRpc.ReceiptNotFinal);
            Assert.Equal("Fail", (await Raw(exit))["Result"]?.ToString());
            Assert.Empty(TransactionData.GetPool().FindAll());
            Accepted(await InBlock(exit, Tip + 1, now));

            // Base finalizes the burn's block: the handler's exit is sent.
            provider.Finalized = 900;
            (sent, why) = await VBTCService.CreateBridgeExitToBTCTx(Handler.Address, 0.004M, BtcDest, burn, allocations, null, votes);
            Assert.True(sent, why);
            Assert.Single(TransactionData.GetPool().FindAll());
        }

        /// <summary>
        /// Block building asks Base about a pooled exit's burn again. While there is no final answer at that moment (this
        /// node's provider is behind, or does not answer) the exit stays in the pool for the next block; it was deleted,
        /// and its burn waited for the handler to make it again. An answer that the transaction is not the burn still
        /// removes it.
        /// </summary>
        [Fact]
        public async Task BlockBuilding_KeepsAnExitWhileBaseHasNoFinalAnswer_AndDropsOneThatIsNotABurn()
        {
            await Start();
            var now = TimeUtil.GetTime();
            var provider = Provider();
            Configure(provider);
            var burn = NewBurn();
            var log = provider.AddBurn(burn, Contract, vfxExit: true, 400_000, Minter.Address, blockNumber: 900);
            var unlock = Unlock(burn, 0.004M, Lock(0.004M), now);
            Accepted(await TransactionValidatorService.VerifyTX(unlock));
            unlock.TransactionRating = TransactionRating.A; // set on receipt; block building skips unrated transactions
            TransactionData.GetPool().InsertSafe(unlock);

            // The provider is behind: for it the burn's block is not final.
            provider.Finalized = 890;
            Assert.Empty(await TransactionData.ProcessTxPool());
            Assert.Single(TransactionData.GetPool().FindAll());

            // The provider does not answer.
            provider.Finalized = null;
            provider.Down = true;
            BaseRpc.ForgetChainChecksForTests();
            Assert.Empty(await TransactionData.ProcessTxPool());
            Assert.Single(TransactionData.GetPool().FindAll());

            // It answers again and the block is final: the exit is proposed.
            provider.Down = false;
            Assert.Equal(unlock.Hash, Assert.Single(await TransactionData.ProcessTxPool()).Hash);

            // A final receipt that is not the burn (the transaction failed on Base) is an answer: the exit is removed.
            provider.Receipts[burn] = FakeBaseRpc.Receipt(burn, succeeded: false, log);
            Assert.Empty(await TransactionData.ProcessTxPool());
            Assert.Empty(TransactionData.GetPool().FindAll());
        }
    }
}
