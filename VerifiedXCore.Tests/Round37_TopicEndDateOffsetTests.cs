using System;
using System.IO;
using System.Threading.Tasks;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Round 37: from Globals.PathAuditRulesHeight a vote topic's end of voting carries a UTC offset ("Z" or "+hh:mm").
    /// A date without one is stored as an instant that depends on the storing node's time zone. Refused for every
    /// function (the apply stores the topic whatever the function); below the height a block is judged as before. The
    /// date is refused at admission at every height. The rule reads only how the date was written, so the tests do not depend on this machine's time zone.
    /// </summary>
    [Collection("DbContextSequential")]
    public class Round37_TopicEndDateOffsetTests : IDisposable
    {
        private const long Gate = 1000;
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly long _priorGate;
        private readonly (PrivateKey Key, string Pub, string Address) _creator;

        public Round37_TopicEndDateOffsetTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"r37topic_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorLastBlock = Globals.LastBlock;
            _priorGate = Globals.PathAuditRulesHeight;
            Globals.PathAuditRulesHeight = Gate;
            DbContext.Initialize();
            Globals.LastBlock = new Block { Height = 10, Timestamp = TimeUtil.GetTime() }; // admission judges at tip + 1, below the gate

            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            _creator = (key, pub, AccountData.GetHumanAddress(pub));
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _creator.Address, Balance = 10_000M, Nonce = 0 });
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.LastBlock = _priorLastBlock;
            Globals.PathAuditRulesHeight = _priorGate;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private Transaction TopicTx(string function, string votingEndDate)
        {
            var uid = Guid.NewGuid().ToString("N");
            var tx = new Transaction
            {
                FromAddress = _creator.Address, ToAddress = "Topic_Base", Amount = 0M, Fee = 0.00000100M, Nonce = 0,
                Timestamp = TimeUtil.GetTime(), TransactionType = TransactionType.VOTE_TOPIC,
                Data = "{\"Function\":\"" + function + "\",\"Topic\":{\"TopicUID\":\"" + uid + "\",\"TopicName\":\"t\",\"TopicDescription\":\"d\",\"TopicOwnerAddress\":\""
                    + _creator.Address + "\",\"VotingEndDate\":\"" + votingEndDate + "\"}}",
            };
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, _creator.Key, _creator.Pub);
            return tx;
        }

        private static string In(TimeSpan from, string suffix) => DateTime.UtcNow.Add(from).ToString("yyyy-MM-ddTHH:mm:ss") + suffix;

        [Theory]
        [InlineData("X")]
        [InlineData("TopicAdd()")]
        public async Task AnEndDateWithoutAnOffset_IsRefused_AtAdmissionAndInBlocksFromTheHeight(string function)
        {
            var tx = TopicTx(function, In(TimeSpan.FromDays(2), ""));

            var (poolOk, poolMsg) = await TransactionValidatorService.VerifyTX(tx);
            Assert.False(poolOk);
            Assert.Equal(TransactionValidatorService.VotingEndDateNeedsOffsetReason, poolMsg);

            var (blockOk, blockMsg) = await TransactionValidatorService.VerifyTX(tx, false, true, false, null, false, Gate);
            Assert.False(blockOk);
            Assert.Equal(TransactionValidatorService.VotingEndDateNeedsOffsetReason, blockMsg);

            // A block below the height is judged as before (replay).
            var (_, belowMsg) = await TransactionValidatorService.VerifyTX(tx, true, true, false, null, false, Gate - 1);
            Assert.NotEqual(TransactionValidatorService.VotingEndDateNeedsOffsetReason, belowMsg);
        }

        [Theory]
        [InlineData("Z")]
        [InlineData("+09:00")]
        [InlineData("-05:00")]
        public async Task AnEndDateWithAnOffset_PassesAtTheHeight(string offset)
        {
            var tx = TopicTx("X", In(TimeSpan.FromDays(2), offset));
            var (blockOk, blockMsg) = await TransactionValidatorService.VerifyTX(tx, false, true, false, null, false, Gate);
            Assert.True(blockOk, blockMsg);
            // Admission now also refuses a VOTE_TOPIC whose Function is not TopicAdd() (VoteTopicRulesTests); the offset passes.
            var (_, poolMsg) = await TransactionValidatorService.VerifyTX(tx);
            Assert.NotEqual(TransactionValidatorService.VotingEndDateNeedsOffsetReason, poolMsg);
        }
        /// <summary>A topic made in a block below the height, as the chain stores it (the block kept, the topic applied).</summary>
        private async Task<string> TopicMadeBelowTheHeight(string votingEndDate, long height)
        {
            var tx = TopicTx("X", votingEndDate);
            tx.Height = height;
            var uid = Newtonsoft.Json.Linq.JObject.Parse(tx.Data)["Topic"]!["TopicUID"]!.ToString();
            var block = new Block { Height = height, Hash = $"r37topic{height}", Timestamp = TimeUtil.GetTime(), Transactions = new System.Collections.Generic.List<Transaction> { tx } };
            BlockchainData.GetBlocks().InsertSafe(block);
            await StateData.UpdateTreis(block);
            Assert.NotNull(TopicTrei.GetSpecificTopic(uid));
            return uid;
        }

        private Transaction VoteTx(string topicUid)
        {
            var tx = new Transaction
            {
                FromAddress = _creator.Address, ToAddress = "Vote_Base", Amount = 0M, Fee = 0.00000100M, Nonce = 0,
                Timestamp = TimeUtil.GetTime(), TransactionType = TransactionType.VOTE,
                Data = Newtonsoft.Json.JsonConvert.SerializeObject(new { Function = "TopicVote()", Vote = new Vote { TopicUID = topicUid, Address = _creator.Address, VoteType = VoteType.Yes } }),
            };
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, _creator.Key, _creator.Pub);
            return tx;
        }

        /// <summary>
        /// A topic made before the height with an end date written without an offset is stored at a different instant on
        /// each node; from the height no vote on it is taken. A topic made then with an offset is judged as before.
        /// </summary>
        [Fact]
        public async Task AVoteOnATopicMadeBeforeTheHeightWithoutAnOffset_IsRefusedFromTheHeight()
        {
            var offsetless = await TopicMadeBelowTheHeight(In(TimeSpan.FromDays(2), ""), Gate - 20);
            var withOffset = await TopicMadeBelowTheHeight(In(TimeSpan.FromDays(2), "Z"), Gate - 19);

            var (ok, msg) = await TransactionValidatorService.VerifyTX(VoteTx(offsetless), false, true, false, null, false, Gate);
            Assert.False(ok);
            Assert.Equal(TransactionValidatorService.TopicEndDateWithoutOffsetReason, msg);
            var (_, poolMsg) = await TransactionValidatorService.VerifyTX(VoteTx(offsetless));
            Assert.Equal(TransactionValidatorService.TopicEndDateWithoutOffsetReason, poolMsg);

            var (_, belowMsg) = await TransactionValidatorService.VerifyTX(VoteTx(offsetless), true, true, false, null, false, Gate - 1);
            Assert.NotEqual(TransactionValidatorService.TopicEndDateWithoutOffsetReason, belowMsg); // replay below the height

            var (_, honestMsg) = await TransactionValidatorService.VerifyTX(VoteTx(withOffset), false, true, false, null, false, Gate);
            Assert.NotEqual(TransactionValidatorService.TopicEndDateWithoutOffsetReason, honestMsg);
        }
    }
}
