using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// From VoteTopicRulesHeight a block carries one TokenVoteTopicCast() per holder and topic: each is judged against
    /// the votes stored before the block, and the apply counts every one. Below the height a block is judged as before.
    /// </summary>
    [Collection("DbContextSequential")]
    public class TokenVoteOncePerBlockTests : IDisposable
    {
        private const long Gate = 3_000;
        private readonly long _prior = Globals.VoteTopicRulesHeight;
        private readonly Block _priorLastBlock = Globals.LastBlock;

        public TokenVoteOncePerBlockTests() => Globals.VoteTopicRulesHeight = Gate;

        public void Dispose()
        {
            Globals.VoteTopicRulesHeight = _prior;
            Globals.LastBlock = _priorLastBlock;
        }

        private static Transaction Vote(string holder, string topic, string hash) => new()
        {
            Hash = hash, FromAddress = holder, ToAddress = "Token_Base", TransactionType = TransactionType.FTKN_TX,
            Data = JsonConvert.SerializeObject(new { Function = "TokenVoteTopicCast()", ContractUID = "tok:1", FromAddress = holder, TopicUID = topic, VoteType = 0 }),
        };

        [Fact]
        public void ASecondVoteOfOneHolderOnOneTopic_IsRefusedInTheBlock()
        {
            var state = new LedgerIntegrityRules.NftStepState();
            Assert.Null(LedgerIntegrityRules.RegisterNftStepInBlock(Vote("xA", "topic-1", "v1"), state, Gate));
            Assert.StartsWith(LedgerIntegrityRules.TokenVotedTwiceInBlock, LedgerIntegrityRules.RegisterNftStepInBlock(Vote("xA", "topic-1", "v2"), state, Gate));
        }

        [Fact]
        public void OtherTopicsAndOtherHolders_StillVoteInOneBlock()
        {
            var state = new LedgerIntegrityRules.NftStepState();
            Assert.Null(LedgerIntegrityRules.RegisterNftStepInBlock(Vote("xA", "topic-1", "v1"), state, Gate));
            Assert.Null(LedgerIntegrityRules.RegisterNftStepInBlock(Vote("xA", "topic-2", "v2"), state, Gate));
            Assert.Null(LedgerIntegrityRules.RegisterNftStepInBlock(Vote("xB", "topic-1", "v3"), state, Gate));
        }

        [Fact]
        public void BelowTheHeight_TwoVotesInABlock_AreJudgedAsBefore()
        {
            var state = new LedgerIntegrityRules.NftStepState();
            Assert.Null(LedgerIntegrityRules.RegisterNftStepInBlock(Vote("xA", "topic-1", "v1"), state, Gate - 1));
            Assert.Null(LedgerIntegrityRules.RegisterNftStepInBlock(Vote("xA", "topic-1", "v2"), state, Gate - 1));
        }

        [Fact]
        public void TheProducer_LeavesTheSecondVoteForALaterBlock()
        {
            Globals.LastBlock = new Block { Height = Gate - 1 };
            var kept = TransactionData.DropDuplicateContractCreations(new List<Transaction> { Vote("xA", "topic-1", "v1"), Vote("xA", "topic-1", "v2") });
            Assert.Equal(new[] { "v1" }, kept.Select(t => t.Hash));
        }
    }
}
