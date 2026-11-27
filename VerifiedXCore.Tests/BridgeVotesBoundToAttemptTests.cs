using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VerifiedXCore.Bitcoin.Attestation;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Controllers;
using VerifiedXCore.Data;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;
using VfxBlock = VerifiedXCore.Models.Block;
using VfxTx = VerifiedXCore.Models.Transaction;
using SignatureService = VerifiedXCore.Services.SignatureService;
using BurnExitType = VerifiedXCore.Bitcoin.Services.BurnExitConsensusService.BurnExitType;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// From Globals.BridgeVotesBoundToAttemptHeight the committee votes a bridge exit to Bitcoin or a pool unlock carries
    /// count only for the caster they elected and only for the attempt they were given for: each counted vote names the
    /// transaction's sender as the handler its caster agreed to, under that caster's handler signature; it is no older
    /// than BridgeCasterConsensus.VoteMaxAgeSeconds before its block and no later than the block's skew allowance; and
    /// when the burn's earlier exit failed, it is newer than the block that held that exit. Admission judges the same
    /// against the clock with a margin. Below the height nothing changes. Judged by VerifyTX (at admission and as a
    /// block's, with the block's timestamp), by the raw route, by ProcessTxPool, and applied with UpdateTreis, under the
    /// bridge rules live on the network.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BridgeVotesBoundToAttemptTests : BtcAttestTestBase
    {
        private const string BtcDest = "bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4";
        /// <summary>A vault from before the chain watched vaults' Bitcoin: its exits are judged by the bridge's own rules.</summary>
        private const string Vault = "0123456789abcdef0123456789abcdef:1790600000";
        /// <summary>With this network's own seed casters they form the committee; these are a majority of it.</summary>
        private const int Casters = 5;
        private readonly long _priorBinding = Globals.BridgeBurnBindingHeight;
        private readonly long _priorGuard = Globals.BridgeIntraBlockGuardHeight;
        private readonly long _priorBound = Globals.BridgeVotesBoundToAttemptHeight;
        private readonly Network? _priorNetwork = Globals.BTCNetwork;
        private readonly List<V> _casters = Enumerable.Range(0, Casters).Select(_ => NewKey()).ToList();

        public BridgeVotesBoundToAttemptTests() : base(realMainnetRules: false)
        {
            Globals.BridgeBurnBindingHeight = 1;
            Globals.BridgeIntraBlockGuardHeight = 1;
            Globals.BridgeVotesBoundToAttemptHeight = long.MaxValue; // as released: unset until a test sets it
            Globals.BTCNetwork = Network.Main;
            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei { SmartContractUID = Vault, ContractData = VbtcTestContracts.VbtcV2ContractData, MinterAddress = Minter.Address, OwnerAddress = Minter.Address });
            foreach (var caster in _casters)
            {
                Globals.BootstrapCasterAddresses.Add(caster.Address);
                StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = caster.Address, Balance = 10M, Nonce = 0 });
            }
        }

        public override void Dispose()
        {
            foreach (var caster in _casters) Globals.BootstrapCasterAddresses.Remove(caster.Address);
            Globals.BTCNetwork = _priorNetwork;
            Globals.BridgeVotesBoundToAttemptHeight = _priorBound;
            Globals.BridgeIntraBlockGuardHeight = _priorGuard;
            Globals.BridgeBurnBindingHeight = _priorBinding;
            base.Dispose();
        }

        private static long Tip => Globals.LastBlock.Height;
        private V Handler => _casters[0];
        private V Other => _casters[1];

        private static string NewBurn() => "0x" + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

        private string Lock(decimal amount, string vault = Vault)
        {
            var id = "lock-" + Guid.NewGuid().ToString("N");
            VBTCBridgeLockState.GetCollection().Insert(new VBTCBridgeLockState
            {
                LockId = id, SmartContractUID = vault, OwnerAddress = Minter.Address, Amount = amount, AmountSats = (long)(amount * 100_000_000M),
                EvmDestination = "0x" + new string('a', 40), LockTimestamp = TimeUtil.GetTime(),
            });
            return id;
        }

        /// <summary>
        /// Every caster's vote for the burn as a round leaves it: the bound vote, and when <paramref name="handler"/> is given
        /// the handler the caster agreed to with its signature over it (BurnExitConsensusService.VotesOf). The confirmations
        /// are made the way RunConsensusRound makes its own.
        /// </summary>
        private List<CasterConsensusVote> Votes(string burn, string type, long sats, string destination, long timestamp, V? handler) =>
            BurnExitConsensusService.VotesOf(_casters.Select(caster => new BurnExitConsensusService.BurnExitConfirmation
            {
                BaseBurnTxHash = burn,
                ConfirmingCasterAddress = caster.Address,
                AgreedHandlerAddress = handler?.Address ?? "",
                Timestamp = timestamp,
                Signature = SignatureService.CreateSignature(BridgeCasterConsensus.BuildBoundVoteMessage(burn, type, sats, destination, timestamp), caster.Key, caster.Pub),
                HandlerSignature = handler == null ? "" : SignatureService.CreateSignature(
                    BridgeCasterConsensus.BuildConfirmationHandlerMessage(burn, caster.Address, handler.Address, timestamp), caster.Key, caster.Pub),
            }), type == "BTC_EXIT" ? BurnExitType.BtcExit : BurnExitType.VfxPoolUnlock)
            .Select(v => handler == null ? new CasterConsensusVote { CasterAddress = v.CasterAddress, BaseBurnTxHash = v.BaseBurnTxHash, BurnType = v.BurnType, Timestamp = v.Timestamp, Signature = v.Signature } : v)
            .ToList();

        private static VfxTx Signed(V sender, TransactionType type, JObject data, long? timestamp = null)
        {
            var tx = new VfxTx
            {
                Timestamp = timestamp ?? TimeUtil.GetTime(), FromAddress = sender.Address, ToAddress = sender.Address, Amount = 0.0M, Fee = 0.0M, Nonce = 0,
                TransactionType = type, Data = data.ToString(Formatting.None),
            };
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, sender.Key, sender.Pub);
            return tx;
        }

        /// <summary>The exit a caster sends for a burn: the given allocations (one lock of the older vault unless given) and votes.</summary>
        private VfxTx Exit(string burn, decimal amount, List<CasterConsensusVote> votes, V sender, string? lockId = null, string vault = Vault,
            long? timestamp = null, List<PoolUnlockAllocation>? allocations = null)
        {
            var sats = (long)(amount * 100_000_000M);
            allocations ??= new List<PoolUnlockAllocation> { new() { LockId = lockId!, SmartContractUID = vault, UnlockAmount = amount } };
            var data = new JObject
            {
                ["Function"] = "VBTCBridgeExitToBTC()", ["ContractUID"] = allocations[0].SmartContractUID, ["LockId"] = allocations[0].LockId,
                ["Amount"] = amount, ["AmountSats"] = sats, ["TotalAmount"] = amount, ["TotalAmountSats"] = sats,
                ["BtcDestination"] = BtcDest, ["BaseBurnTxHash"] = burn,
                ["Allocations"] = JArray.FromObject(allocations), ["BtcWithdrawals"] = JValue.CreateNull(),
                ["CasterConsensusVotes"] = JArray.FromObject(votes),
            };
            return Signed(sender, TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC, data, timestamp);
        }

        private VfxTx ExitVotedFor(string burn, decimal amount, string lockId, V sender, long voted, V? handler, long? timestamp = null) =>
            Exit(burn, amount, Votes(burn, "BTC_EXIT", (long)(amount * 100_000_000M), BtcDest, voted, handler), sender, lockId, timestamp: timestamp);

        private VfxTx Unlock(string burn, decimal amount, string lockId, V sender, long voted, V? handler)
        {
            var sats = (long)(amount * 100_000_000M);
            var data = new JObject
            {
                ["Function"] = "VBTCBridgePoolUnlock()", ["TotalAmount"] = amount, ["TotalAmountSats"] = sats, ["VfxDestinationAddress"] = Minter.Address,
                ["ExitBurnTxHash"] = burn,
                ["Allocations"] = JArray.FromObject(new[] { new PoolUnlockAllocation { LockId = lockId, SmartContractUID = Vault, UnlockAmount = amount } }),
                ["CasterConsensusVotes"] = JArray.FromObject(Votes(burn, "POOL_EXIT", sats, Minter.Address, voted, handler)),
            };
            return Signed(sender, TransactionType.VBTC_V2_BRIDGE_POOL_UNLOCK, data);
        }

        /// <summary>Block validation's verdict on a transaction of the block at <paramref name="height"/> stamped <paramref name="blockTime"/>.</summary>
        private static Task<(bool, string)> InBlock(VfxTx tx, long height, long blockTime) =>
            TransactionValidatorService.VerifyTX(tx, false, true, false, null, false, height, blockTime);

        /// <summary>Applies a block stamped <paramref name="timestamp"/> the way the node does, and keeps it in the block store as a committed block is.</summary>
        private static async Task ApplyStored(long height, long timestamp, params VfxTx[] txs)
        {
            var block = new VfxBlock { Height = height, Hash = $"b{height}", StateRoot = "root" + height, Timestamp = timestamp, Transactions = txs.ToList() };
            Assert.True(await StateData.UpdateTreis(block));
            BlockchainData.GetBlocks().InsertSafe(block);
            Globals.LastBlock = block;
            AppliedBlocks.Add(block);
        }

        private static void Refused((bool Ok, string Why) verdict, string because, string where)
        {
            Assert.False(verdict.Ok, $"accepted {where}");
            Assert.Contains(because, verdict.Why);
        }

        private static void Accepted((bool Ok, string Why) verdict) => Assert.True(verdict.Ok, verdict.Why);

        /// <summary>Runs <paramref name="check"/> with the rule switched off (the height unset), then sets it back.</summary>
        private static async Task WithoutTheRule(Func<Task> check)
        {
            var gate = Globals.BridgeVotesBoundToAttemptHeight;
            Globals.BridgeVotesBoundToAttemptHeight = long.MaxValue;
            try { await check(); }
            finally { Globals.BridgeVotesBoundToAttemptHeight = gate; }
        }

        [Fact]
        public async Task TheCastersMadeHere_AreAMajorityOfTheCommittee()
        {
            await Start();
            var committee = BridgeCasterConsensus.GetCommitteeForHeight(Tip + 1);
            Assert.All(_casters, c => Assert.Contains(c.Address, committee));
            Assert.True(BridgeCasterConsensus.RequiredVotesFor(committee) <= Casters);
        }

        /// <summary>1. An exit counts its votes only for the caster they name as handler, from the height; below it as before.</summary>
        [Fact]
        public async Task FromTheHeight_AnExitsVotesCountOnlyForTheCasterTheyElected()
        {
            await Start();
            var gate = Tip + 1;
            Globals.BridgeVotesBoundToAttemptHeight = gate;
            var now = TimeUtil.GetTime();
            var burn = NewBurn();
            var lockId = Lock(0.004M);

            // The handler's own exit is accepted at admission and in a block.
            var byHandler = ExitVotedFor(burn, 0.004M, lockId, Handler, now, Handler);
            Accepted(await TransactionValidatorService.VerifyTX(byHandler));
            Accepted(await InBlock(byHandler, gate, now));

            // The same votes sent by another committee caster are refused at admission and in a block.
            var byOther = ExitVotedFor(burn, 0.004M, lockId, Other, now, Handler);
            Refused(await TransactionValidatorService.VerifyTX(byOther), BridgeCasterConsensus.VoteForOtherHandlerReason, "at admission");
            Refused(await InBlock(byOther, gate, now), BridgeCasterConsensus.VoteForOtherHandlerReason, "in a block");
            await WithoutTheRule(async () => Accepted(await InBlock(byOther, gate, now)));

            // Votes carrying no handler (as builders before the height wrote them) count for nobody from it.
            var bare = ExitVotedFor(burn, 0.004M, lockId, Handler, now, handler: null);
            Refused(await InBlock(bare, gate, now), BridgeCasterConsensus.VoteForOtherHandlerReason, "without handlers");
            Refused(await TransactionValidatorService.VerifyTX(bare), BridgeCasterConsensus.VoteForOtherHandlerReason, "without handlers at admission");
            await WithoutTheRule(async () => Accepted(await InBlock(bare, gate, now)));

            // A handler name rewritten to the sender, over a signature for the real handler, counts for nobody.
            var renamed = Votes(burn, "BTC_EXIT", 400_000, BtcDest, now, Handler);
            foreach (var v in renamed) v.AgreedHandler = Other.Address;
            var forged = Exit(burn, 0.004M, renamed, Other, lockId);
            Refused(await InBlock(forged, gate, now), BridgeCasterConsensus.VoteForOtherHandlerReason, "with a rewritten handler");

            // Below the height every one of them is judged as before.
            foreach (var tx in new[] { byHandler, byOther, bare, forged })
                Accepted(await InBlock(tx, gate - 1, now));
        }

        /// <summary>3. A vote counts only within the age and the skew the attempt allows, from the block's timestamp; admission is stricter.</summary>
        [Fact]
        public async Task FromTheHeight_AVoteCountsOnlyWithinItsAgeAndSkew_AdmissionStricterThanTheBlock()
        {
            await Start();
            var gate = Tip + 1;
            Globals.BridgeVotesBoundToAttemptHeight = gate;
            var block = TimeUtil.GetTime();
            var maxAge = BridgeCasterConsensus.VoteMaxAgeSeconds;
            var skew = Globals.MaxFutureSkewSeconds + LedgerIntegrityRules.BlockTimeMarginSeconds;
            var lockId = Lock(0.004M);
            VfxTx At(long voted) => ExitVotedFor(NewBurn(), 0.004M, lockId, Handler, voted, Handler, timestamp: block);

            Accepted(await InBlock(At(block - maxAge), gate, block));
            Refused(await InBlock(At(block - maxAge - 1), gate, block), BridgeCasterConsensus.VoteOutsideAttemptTimeReason, "too old");
            Accepted(await InBlock(At(block + skew), gate, block));
            Refused(await InBlock(At(block + skew + 1), gate, block), BridgeCasterConsensus.VoteOutsideAttemptTimeReason, "too far ahead");
            await WithoutTheRule(async () =>
            {
                Accepted(await InBlock(At(block - maxAge - 1), gate, block));
                Accepted(await InBlock(At(block + skew + 1), gate, block));
            });

            // Admission takes the margin off the age and the block margin off the skew: what it admits, a block a little
            // later still accepts.
            var admissionAge = maxAge - BridgeCasterConsensus.VoteAdmissionMarginSeconds;
            var now = TimeUtil.GetTime();
            var nearLimit = At(now - admissionAge + 30);
            Accepted(await TransactionValidatorService.VerifyTX(nearLimit));
            Accepted(await InBlock(nearLimit, gate, now + BridgeCasterConsensus.VoteAdmissionMarginSeconds - 30));
            var pastAdmission = At(now - admissionAge - 30);
            Refused(await TransactionValidatorService.VerifyTX(pastAdmission), BridgeCasterConsensus.VoteOutsideAttemptTimeReason, "at admission");
            Accepted(await InBlock(pastAdmission, gate, now));
            Refused(await TransactionValidatorService.VerifyTX(At(now + Globals.MaxFutureSkewSeconds + 30)), BridgeCasterConsensus.VoteOutsideAttemptTimeReason, "ahead at admission");
        }

        /// <summary>4. A pool unlock is held to the handler and to the vote's age the same way.</summary>
        [Fact]
        public async Task FromTheHeight_APoolUnlocksVotesCountOnlyForTheirHandler_WhileRecent()
        {
            await Start();
            var gate = Tip + 1;
            Globals.BridgeVotesBoundToAttemptHeight = gate;
            var now = TimeUtil.GetTime();
            var burn = NewBurn();
            var lockId = Lock(0.004M);

            var byHandler = Unlock(burn, 0.004M, lockId, Handler, now, Handler);
            Accepted(await TransactionValidatorService.VerifyTX(byHandler));
            Accepted(await InBlock(byHandler, gate, now));

            var byOther = Unlock(burn, 0.004M, lockId, Other, now, Handler);
            Refused(await TransactionValidatorService.VerifyTX(byOther), BridgeCasterConsensus.VoteForOtherHandlerReason, "at admission");
            Refused(await InBlock(byOther, gate, now), BridgeCasterConsensus.VoteForOtherHandlerReason, "in a block");
            var old = Unlock(burn, 0.004M, lockId, Handler, now - BridgeCasterConsensus.VoteMaxAgeSeconds - 1, Handler);
            Refused(await InBlock(old, gate, now), BridgeCasterConsensus.VoteOutsideAttemptTimeReason, "with old votes");
            await WithoutTheRule(async () =>
            {
                Accepted(await InBlock(byOther, gate, now));
                Accepted(await InBlock(old, gate, now));
            });
            Accepted(await InBlock(byOther, gate - 1, now));
            Accepted(await InBlock(old, gate - 1, now));

            await Apply(gate, byHandler);
            Assert.True(VBTCBridgeConsumedBurn.IsConsumed(burn));
        }

        /// <summary>
        /// 2. An exit on a watched vault that could not be pinned is failed by rule (ExpireExit) and its locks restored.
        /// Its votes, public in its block, then count for no later attempt, even its own handler's: a new attempt carries
        /// votes given after the failed exit's block. Fresh votes from a new round are accepted and the exit made again.
        /// </summary>
        [Fact]
        public async Task AfterAnExitIsFailedByRule_ItsVotesCountForNoLaterAttempt_AndFreshVotesMakeItAgain()
        {
            await Start();
            var vault = await CreateVault(H1 + 1); // watched, with no Bitcoin yet: nothing can be pinned for it
            var gate = Tip + 1;
            Globals.BridgeVotesBoundToAttemptHeight = gate;
            var lockId = Lock(0.004M, vault.Uid);
            var burn = NewBurn();
            var now = TimeUtil.GetTime();
            var firstBlock = now - 200;
            var firstVotes = Votes(burn, "BTC_EXIT", 400_000, BtcDest, firstBlock - 10, Handler);
            var first = Exit(burn, 0.004M, firstVotes, Handler, lockId, vault.Uid, timestamp: firstBlock - 10);
            Accepted(await InBlock(first, Tip + 1, firstBlock));
            await ApplyStored(Tip + 1, firstBlock, first);
            var mined = Tip;
            Assert.Equal(mined, VBTCBridgeBtcExitState.GetByBurnHash(burn)!.ExitVfxHeight);
            Assert.True(VBTCBridgeLockState.GetByLockId(lockId)!.IsUnlocked);

            await Apply(mined + VBTCWithdrawalRequest.EXPIRY_BLOCKS + 1);
            var failed = VBTCBridgeBtcExitState.GetByBurnHash(burn)!;
            Assert.True(failed.IsFailedRecord);
            Assert.StartsWith("FAILED:expired@", failed.BtcTxHash);
            Assert.False(VBTCBridgeLockState.GetByLockId(lockId)!.IsUnlocked);
            Assert.False(VBTCBridgeConsumedBurn.IsBurnUsedAnywhere(burn));

            // The failed exit's votes, well within their age, sent again by its own handler or by another caster.
            var again = Exit(burn, 0.004M, firstVotes, Handler, lockId, vault.Uid);
            Refused(await InBlock(again, Tip + 1, now), BridgeCasterConsensus.VoteOutsideAttemptTimeReason, "with the failed exit's votes");
            Refused(await TransactionValidatorService.VerifyTX(again), BridgeCasterConsensus.VoteOutsideAttemptTimeReason, "at admission");
            await WithoutTheRule(async () => Accepted(await InBlock(again, Tip + 1, now)));

            // The failed exit's block cannot be read: the exit is refused rather than judged without it.
            var stored = BlockchainData.GetBlockByHeight(mined)!;
            BlockchainData.GetBlocks().DeleteMany(b => b.Height == mined);
            Refused(await InBlock(again, Tip + 1, now), "could not be read", "without the failed exit's block");
            BlockchainData.GetBlocks().InsertSafe(stored);

            // A new round's votes, given after it, make the exit again.
            var fresh = Exit(burn, 0.004M, Votes(burn, "BTC_EXIT", 400_000, BtcDest, now - 5, Handler), Handler, lockId, vault.Uid);
            Accepted(await TransactionValidatorService.VerifyTX(fresh));
            Accepted(await InBlock(fresh, Tip + 1, now));
            await ApplyStored(Tip + 1, now, fresh);
            var record = VBTCBridgeBtcExitState.GetByBurnHash(burn)!;
            Assert.False(record.IsFailedRecord);
            Assert.Equal(fresh.Hash, record.ExitTxHash);
            Assert.Equal(Tip, record.ExitVfxHeight);
            Assert.True(VBTCBridgeLockState.GetByLockId(lockId)!.IsUnlocked);
        }

        /// <summary>5. The exit the handler's own node builds from its round's confirmations is admitted, pooled, proposed and accepted in the block.</summary>
        [Fact]
        public async Task TheExitTheHandlerBuildsFromItsRound_PassesAdmissionTheBlockAndBlockBuilding()
        {
            await Start();
            var gate = Tip + 1;
            Globals.BridgeVotesBoundToAttemptHeight = gate;
            AccountData.GetAccounts().InsertSafe(new Account { Address = Handler.Address, PublicKey = Handler.Pub, PrivateKey = "0" + Handler.Key.secret.ToString("x") });
            var now = TimeUtil.GetTime();
            var burn = NewBurn();
            var lockId = Lock(0.004M);
            var votes = Votes(burn, "BTC_EXIT", 400_000, BtcDest, now, Handler);
            Assert.All(votes, v => { Assert.Equal(Handler.Address, v.AgreedHandler); Assert.False(string.IsNullOrEmpty(v.HandlerSignature)); });

            var (sent, hashOrError) = await VBTCService.CreateBridgeExitToBTCTx(Handler.Address, 0.004M, BtcDest, burn,
                new List<PoolUnlockAllocation> { new() { LockId = lockId, SmartContractUID = Vault, UnlockAmount = 0.004M } }, null, votes);
            Assert.True(sent, hashOrError);
            var pooled = TransactionData.GetPool().FindOne(x => x.Hash == hashOrError);
            Assert.NotNull(pooled);
            // The strict Data reader takes the fields the votes now carry.
            var strict = Globals.StrictDataReaderHeight;
            Globals.StrictDataReaderHeight = 1;
            try
            {
                Assert.Null(ConsensusData.Refusal(pooled!, gate));
                Accepted(await InBlock(pooled!, gate, now));
            }
            finally { Globals.StrictDataReaderHeight = strict; }

            var proposed = await TransactionData.ProcessTxPool();
            Assert.Equal(hashOrError, Assert.Single(proposed).Hash);
            Accepted(await InBlock(proposed[0], gate, TimeUtil.GetTime()));
            await Apply(gate, proposed.ToArray());
            Assert.Equal(hashOrError, VBTCBridgeBtcExitState.GetByBurnHash(burn)!.ExitTxHash);
        }

        /// <summary>
        /// 1 (raw path). The raw route admits the handler's exit and refuses another caster's carrying the same votes.
        /// </summary>
        [Fact]
        public async Task FromTheHeight_TheRawRouteRefusesAnExitFromACasterTheVotesDidNotElect()
        {
            await Start();
            var gate = Tip + 1;
            Globals.BridgeVotesBoundToAttemptHeight = gate;
            var now = TimeUtil.GetTime();
            var burn = NewBurn();
            var lockId = Lock(0.004M);

            async Task<JObject> Raw(VfxTx tx)
            {
                var body = JObject.Parse(JsonConvert.SerializeObject(tx));
                body["Data"] = JToken.Parse(tx.Data!);
                return JObject.Parse(await new TXV1Controller().SendRawTransaction(body));
            }

            var otherTx = ExitVotedFor(burn, 0.004M, lockId, Other, now, Handler);
            Refused(await TransactionValidatorService.VerifyTX(otherTx), BridgeCasterConsensus.VoteForOtherHandlerReason, "at admission");
            var byOther = await Raw(otherTx);
            Assert.Equal("Fail", byOther["Result"]?.ToString());
            Assert.Empty(TransactionData.GetPool().FindAll());
            await WithoutTheRule(async () => Assert.Equal("Success", (await Raw(otherTx))["Result"]?.ToString()));
            TransactionData.GetPool().DeleteAll();

            var byHandler = await Raw(ExitVotedFor(burn, 0.004M, lockId, Handler, now, Handler));
            Assert.Equal("Success", byHandler["Result"]?.ToString());
            Assert.Single(TransactionData.GetPool().FindAll());
        }

        /// <summary>
        /// 8. An exit admitted below the height (its votes carry no handler) that waits in the pool past it is dropped by
        /// block building, not proposed into a block that would refuse it; one whose votes grew too old there likewise.
        /// </summary>
        [Fact]
        public async Task BlockBuilding_DropsAnExitWhoseVotesNoLongerCount()
        {
            await Start();
            var gate = Tip + 2;
            Globals.BridgeVotesBoundToAttemptHeight = gate;
            var now = TimeUtil.GetTime();
            var bare = ExitVotedFor(NewBurn(), 0.004M, Lock(0.004M), Handler, now, handler: null);
            var aging = ExitVotedFor(NewBurn(), 0.004M, Lock(0.004M), Other, now - BridgeCasterConsensus.VoteMaxAgeSeconds + BridgeCasterConsensus.VoteAdmissionMarginSeconds - 30, Other);
            Assert.Equal(gate - 1, Tip + 1);
            Accepted(await TransactionValidatorService.VerifyTX(bare)); // the next block is below the height
            await TransactionData.AddToPool(bare);
            aging.TransactionRating = TransactionRating.A; // set on receipt; block building skips unrated transactions
            TransactionData.GetPool().InsertSafe(aging); // as a peer's delivery left it, admitted when its votes were younger

            await Apply(gate - 1);
            Refused(await InBlock(bare, gate, TimeUtil.GetTime()), BridgeCasterConsensus.VoteForOtherHandlerReason, "at the height");
            var proposed = await TransactionData.ProcessTxPool();

            Assert.Empty(proposed);
            Assert.Empty(TransactionData.GetPool().FindAll());
        }

        /// <summary>7. The largest plan a caster makes, with every vote of the largest committee, stays under the size cap.</summary>
        [Fact]
        public async Task TheLargestPlan_WithTheLargestCommitteesVotes_StaysUnderTheSizeCap()
        {
            await Start();
            var gate = Tip + 1;
            Globals.BridgeVotesBoundToAttemptHeight = gate;
            var now = TimeUtil.GetTime();
            var burn = NewBurn();
            const decimal part = 0.0001M;
            // As many locks of the longest id a plan takes as fit in the room a plan has.
            var allocations = new List<PoolUnlockAllocation>();
            while (true)
            {
                var next = allocations.Append(new PoolUnlockAllocation { LockId = LongLock(part), SmartContractUID = Vault, UnlockAmount = part }).ToList();
                if (BridgePoolUnlockService.PlanBytes(next) > BridgePoolUnlockService.MaxPlanBytes) break;
                allocations = next;
            }
            var total = allocations.Sum(a => a.UnlockAmount);
            var sats = (long)(total * 100_000_000M);

            // A committee larger than any the casters form (CasterDiscoveryService.MaxCasters, seeds included): this
            // network's three seeds and the five casters here. The seeds' votes are as long as these, so the list holds a
            // vote for every member.
            var committee = BridgeCasterConsensus.GetCommitteeForHeight(gate).Count;
            Assert.True(committee >= CasterDiscoveryService.MaxCasters + 3, $"a committee of {committee}");
            var votes = Votes(burn, "BTC_EXIT", sats, BtcDest, now, Handler);
            var everyVote = votes.Concat(Enumerable.Range(0, committee - votes.Count).Select(i => votes[i % votes.Count])).ToList();
            var largest = Exit(burn, total, everyVote, Handler, allocations: allocations);
            var carried = JsonConvert.SerializeObject(TransactionValidatorService.AsCarriedInABlock(largest)).Length;
            Assert.True(carried <= LedgerIntegrityRules.DefaultMaxTxSizeBytes, $"{carried} bytes with {everyVote.Count} votes and {allocations.Count} allocations");
            // In a block (where a caster's vote may be listed again below BridgeVoteOncePerCasterHeight) it is accepted.
            Accepted(await InBlock(largest, gate, now));
            // And the list a round gives, each caster once, at admission and in a block.
            var real = Exit(burn, total, votes, Handler, allocations: allocations);
            Accepted(await TransactionValidatorService.VerifyTX(real));
            Accepted(await InBlock(real, gate, now));
        }

        /// <summary>A lock whose id is as long as a plan's lock id can be.</summary>
        private string LongLock(decimal amount)
        {
            var id = ("lock-" + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"))[..BridgePoolUnlockService.MaxLockIdLength];
            VBTCBridgeLockState.GetCollection().Insert(new VBTCBridgeLockState
            {
                LockId = id, SmartContractUID = Vault, OwnerAddress = Minter.Address, Amount = amount, AmountSats = (long)(amount * 100_000_000M),
                EvmDestination = "0x" + new string('a', 40), LockTimestamp = TimeUtil.GetTime(),
            });
            return id;
        }

        /// <summary>A caster's node gives a round up for a new one when its votes would no longer be admitted (from the height only).</summary>
        [Fact]
        public void AHandlerHoldsANewRound_WhenItsVotesWouldNoLongerBeAdmitted()
        {
            var now = TimeUtil.GetTime();
            var limit = BridgeCasterConsensus.VoteMaxAgeSeconds - BridgeCasterConsensus.VoteAdmissionMarginSeconds - BurnExitConsensusService.VoteAgeSlackSeconds;
            var votes = new List<CasterConsensusVote>
            {
                new() { CasterAddress = "a", Timestamp = now - limit },
                new() { CasterAddress = "b", Timestamp = now - limit - 1 },
            };
            Globals.BridgeVotesBoundToAttemptHeight = 50;
            Assert.Equal(new[] { "a" }, BurnExitConsensusService.VotesStillAdmitted(votes, 50, now).Select(v => v.CasterAddress));
            Assert.Equal(new[] { "a", "b" }, BurnExitConsensusService.VotesStillAdmitted(votes, 49, now).Select(v => v.CasterAddress));
        }
    }
}
