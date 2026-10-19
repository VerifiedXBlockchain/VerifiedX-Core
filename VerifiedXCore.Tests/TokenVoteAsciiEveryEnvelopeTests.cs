using Newtonsoft.Json;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// From Globals.PathAuditRulesHeight a token vote's TopicUID is plain ASCII under every contract-call type that runs
    /// the token functions, not under FTKN_TX alone: the vote is applied by Data.Function, and its duplicate check goes
    /// through LiteDB's collation.
    /// </summary>
    public class TokenVoteAsciiEveryEnvelopeTests
    {
        private static Transaction Vote(TransactionType type, string topicUid) => new()
        {
            TransactionType = type, FromAddress = "xVoter", ToAddress = "Token_Base",
            Data = JsonConvert.SerializeObject(new { Function = "TokenVoteTopicCast()", ContractUID = "uid:1", TopicUID = topicUid, VoteType = 1 }),
        };

        [Theory]
        [InlineData(TransactionType.FTKN_TX)]
        [InlineData(TransactionType.NFT_TX)]
        [InlineData(TransactionType.SC_TX)]
        [InlineData(TransactionType.TKNZ_TX)]
        public void ANonAsciiTopicUid_IsRefusedUnderEveryEnvelope_FromTheRule(TransactionType type)
        {
            var rule = Globals.PathAuditRulesHeight;
            Assert.NotNull(LedgerIntegrityRules.IdentifiersArePlainAscii(Vote(type, "topicꟀ"), rule));
            Assert.Null(LedgerIntegrityRules.IdentifiersArePlainAscii(Vote(type, "topic-1"), rule));
            Assert.Null(LedgerIntegrityRules.IdentifiersArePlainAscii(Vote(type, "topicꟀ"), rule - 1));
        }
    }
}
