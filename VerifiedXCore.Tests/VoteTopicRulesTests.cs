using System;
using System.Collections.Generic;
using System.IO;
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

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// From Globals.VoteTopicRulesHeight (and at admission at every height) a VOTE_TOPIC is a TopicAdd(): one with another
    /// Function skipped every topic rule and was still stored. From the height a stored topic's owner, tally, creation time
    /// and validator count come from the chain, not from its creator.
    /// </summary>
    [Collection("DbContextSequential")]
    public class VoteTopicRulesTests : IDisposable
    {
        private const long Rule = 5_000;
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly long _priorRule = Globals.VoteTopicRulesHeight;
        private readonly (PrivateKey Key, string Pub, string Address) _sender;

        public VoteTopicRulesTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"vote_topic_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            Globals.VoteTopicRulesHeight = Rule;
            Globals.LastBlock = new Block { Height = Rule - 1 };
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            _sender = (key, pub, AccountData.GetHumanAddress(pub));
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _sender.Address, Balance = 1000M, Nonce = 0 });
        }

        public void Dispose()
        {
            Globals.VoteTopicRulesHeight = _priorRule;
            Globals.LastBlock = _priorLastBlock;
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static TopicTrei ForgedTopic(string uid) => new()
        {
            TopicUID = uid, TopicName = "t", TopicDescription = "d", TopicOwnerAddress = "xSomeoneElse", VoteYes = 9_999, VoteNo = 1,
            ValidatorCount = 1, TopicCreateDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            VotingEndDate = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        private Transaction Topic(string function, TopicTrei topic)
        {
            var tx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(), FromAddress = _sender.Address, ToAddress = "Topic_Base", Amount = Globals.TopicRequiredRBX, Nonce = 0,
                TransactionType = TransactionType.VOTE_TOPIC, Data = JsonConvert.SerializeObject(new { Function = function, Topic = topic }),
            };
            tx.Fee = FeeCalcService.CalculateTXFee(tx);
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, _sender.Key, _sender.Pub);
            return tx;
        }

        [Fact]
        public async Task AVoteTopicWithAnotherFunction_IsRefusedFromTheRule_AndAtAdmissionAtEveryHeight()
        {
            var forged = Topic("TopicUpdate()", ForgedTopic("uid-1"));

            Assert.Equal(TransactionValidatorService.VoteTopicIsTopicAddReason, (await TransactionValidatorService.VerifyTX(forged, false, true, false, null, false, Rule)).Item2);
            Assert.Equal(TransactionValidatorService.VoteTopicIsTopicAddReason, (await TransactionValidatorService.VerifyTX(forged)).Item2); // admission, tip below the rule
            Assert.NotEqual(TransactionValidatorService.VoteTopicIsTopicAddReason, (await TransactionValidatorService.VerifyTX(forged, false, true, false, null, false, Rule - 1)).Item2);
        }

        [Fact]
        public async Task FromTheRule_AStoredTopicTakesItsSystemFieldsFromTheChain_AndBelowItFromItsCreator()
        {
            var at = Topic("TopicAdd()", ForgedTopic("uid-at"));
            at.Height = Rule;
            Assert.True(await StateData.UpdateTreis(new Block { Height = Rule, StateRoot = "root", Timestamp = TimeUtil.GetTime(), Transactions = new List<Transaction> { at } }));
            var stored = TopicTrei.GetSpecificTopic("uid-at")!;
            Assert.Equal(0, stored.VoteYes);
            Assert.Equal(0, stored.VoteNo);
            Assert.Equal(_sender.Address, stored.TopicOwnerAddress);
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(at.Timestamp).UtcDateTime, stored.TopicCreateDate.ToUniversalTime());
            Assert.Equal(0, stored.ValidatorCount); // no validator is registered in this test's chain

            var below = Topic("TopicAdd()", ForgedTopic("uid-below"));
            below.Height = Rule - 1;
            Assert.True(await StateData.UpdateTreis(new Block { Height = Rule - 1, StateRoot = "root", Timestamp = TimeUtil.GetTime(), Transactions = new List<Transaction> { below } }));
            Assert.Equal(9_999, TopicTrei.GetSpecificTopic("uid-below")!.VoteYes); // as the previous release stored it
        }
    }
}
