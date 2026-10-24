using System;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;
using System.IO;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// From TopicAddDeterministicHeight a TopicAdd() is judged on the transaction, the state and the block's timestamp only,
    /// so live apply and a replay reach the same verdict: the sender need not be in this node's recent block producers or
    /// recent-block window, the validator balance and the one-open-topic rule hold in a replay too, and the adjudicator
    /// pool (node-local) is judged at admission only. Below the height the previous rules stand.
    /// </summary>
    [Collection("DbContextSequential")]
    public class TopicAddDeterministicTests : IDisposable
    {
        private const long Rule = 6_000;
        private static readonly long BlockTime = TimeUtil.GetTime();

        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly long _priorRule = Globals.TopicAddDeterministicHeight;
        private readonly (PrivateKey Key, string Pub, string Address) _sender;

        public TopicAddDeterministicTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"topic_det_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            Globals.TopicAddDeterministicHeight = Rule;
            Globals.LastBlock = new Block { Height = Rule - 1, Timestamp = BlockTime - 30 };
            Globals.ActiveValidatorDict.Clear();
            Globals.MemBlocks.Clear();
            Globals.AdjNodes.Clear();
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            _sender = (key, pub, AccountData.GetHumanAddress(pub));
        }

        public void Dispose()
        {
            Globals.TopicAddDeterministicHeight = _priorRule;
            Globals.LastBlock = _priorLastBlock;
            Globals.ActiveValidatorDict.Clear();
            Globals.MemBlocks.Clear();
            Globals.AdjNodes.Clear();
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private void Fund(decimal balance) =>
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _sender.Address, Balance = balance, Nonce = 0 });

        private Transaction TopicAdd(string uid, TopicVoterType voterType = TopicVoterType.Validator)
        {
            var topic = new TopicTrei
            {
                TopicUID = uid, TopicName = "t", TopicDescription = "d", TopicOwnerAddress = _sender.Address, VoterType = voterType,
                BlockHeight = Rule - 50, VotingEndDate = new DateTime(2040, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                TopicOwnerSignature = SignatureService.CreateSignature(uid, _sender.Key, _sender.Pub),
            };
            var tx = new Transaction
            {
                Timestamp = BlockTime - 10, FromAddress = _sender.Address, ToAddress = "Topic_Base", Amount = Globals.TopicRequiredRBX, Nonce = 0,
                TransactionType = TransactionType.VOTE_TOPIC, Data = JsonConvert.SerializeObject(new { Function = "TopicAdd()", Topic = topic }),
            };
            tx.Fee = FeeCalcService.CalculateTXFee(tx);
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, _sender.Key, _sender.Pub);
            return tx;
        }

        private static Task<(bool, string)> Live(Transaction tx, long height) =>
            TransactionValidatorService.VerifyTX(tx, false, true, false, null, false, height, BlockTime);

        private static Task<(bool, string)> Replay(Transaction tx, long height) =>
            TransactionValidatorService.VerifyTX(tx, true, true, false, null, false, height, BlockTime);

        private void StoreTopic(string uid, DateTime votingEnd) =>
            TopicTrei.GetTopics()!.Insert(new TopicTrei { TopicUID = uid, TopicName = "old", TopicDescription = "d", TopicOwnerAddress = _sender.Address, VotingEndDate = votingEnd });

        [Fact]
        public async Task FromTheRule_AFundedSenderOutsideThisNodesRecentProducers_IsAcceptedLiveAndInReplay()
        {
            Fund(6_000M);
            var tx = TopicAdd("uid-a");

            var live = await Live(tx, Rule);
            var replay = await Replay(tx, Rule);

            Assert.True(live.Item1, live.Item2);
            Assert.True(replay.Item1, replay.Item2);
        }

        [Fact]
        public async Task BelowTheRule_LiveStillAsksForARecentBlockAndTheRecentWindow_AsTheLastReleaseDid()
        {
            Fund(6_000M);
            var tx = TopicAdd("uid-b");

            var live = await Live(tx, Rule - 1);
            var replay = await Replay(tx, Rule - 1);

            Assert.False(live.Item1);
            Assert.Contains("memblocks", live.Item2);
            Assert.True(replay.Item1, replay.Item2); // replay skipped the rules below the height
        }

        [Fact]
        public async Task FromTheRule_TheValidatorBalance_IsRequiredLiveAndInReplay()
        {
            Fund(5_005M); // under 5,000 once the topic amount and fee are paid
            var tx = TopicAdd("uid-c");

            var live = await Live(tx, Rule);
            var replay = await Replay(tx, Rule);

            Assert.False(live.Item1);
            Assert.Contains("Balance is under", live.Item2);
            Assert.Equal(live, replay);
        }

        [Fact]
        public async Task FromTheRule_ATopicStillOpenAtTheBlocksTime_BlocksASecond_AndOneThatEndedDoesNot()
        {
            Fund(6_000M);
            var blockMoment = DateTimeOffset.FromUnixTimeSeconds(BlockTime).UtcDateTime;
            StoreTopic("open", blockMoment.AddHours(1));

            var live = await Live(TopicAdd("uid-d"), Rule);
            var replay = await Replay(TopicAdd("uid-d"), Rule);
            Assert.Equal(TransactionValidatorService.TopicOwnerHasActiveTopicReason, live.Item2);
            Assert.Equal(TransactionValidatorService.TopicOwnerHasActiveTopicReason, replay.Item2);

            TopicTrei.GetTopics()!.DeleteAll();
            StoreTopic("ended", blockMoment.AddHours(-1));
            var afterEnd = await Live(TopicAdd("uid-e"), Rule);
            Assert.True(afterEnd.Item1, afterEnd.Item2);
        }

        [Fact]
        public async Task FromTheRule_TheAdjudicatorPool_IsJudgedAtAdmissionOnly()
        {
            Fund(100M);
            Globals.LastBlock = new Block { Height = Rule, Timestamp = BlockTime - 30 };
            var tx = TopicAdd("uid-f", TopicVoterType.Adjudicator);

            var admission = await TransactionValidatorService.VerifyTX(tx);
            Assert.False(admission.Item1);
            Assert.Contains("adjudicator pool", admission.Item2);

            var inBlock = await Live(tx, Rule + 1);
            Assert.True(inBlock.Item1, inBlock.Item2); // this node's pool does not decide a block

            Globals.AdjNodes["10.0.0.9"] = new AdjNodeInfo { Address = _sender.Address };
            var fromPoolMember = await TransactionValidatorService.VerifyTX(tx);
            Assert.True(fromPoolMember.Item1, fromPoolMember.Item2);
        }

        [Fact]
        public void FromTheRule_AnAdjVoteInTopicWithoutItsRequirements_IsRefused()
        {
            Fund(6_000M);
            var topic = new TopicTrei { TopicUID = "u", TopicName = "t", TopicDescription = "not json", VoteTopicCategory = VoteTopicCategories.AdjVoteIn, VoterType = TopicVoterType.Validator };
            var tx = new Transaction { FromAddress = _sender.Address, Amount = 10M, Fee = 0.1M };

            Assert.Equal(TransactionValidatorService.TopicAdjVoteInMissingReason, TransactionValidatorService.TopicAddRefusal(tx, topic, BlockTime, admission: false));
        }
    }
}
