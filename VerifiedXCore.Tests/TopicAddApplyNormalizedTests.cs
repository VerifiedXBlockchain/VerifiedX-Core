using System;
using System.IO;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// From TopicAddDeterministicHeight (as from VoteTopicRulesHeight) a new topic's tallies, owner, creation date and
    /// validator count are set by the apply from the chain, never taken from its creator. Below both heights the creator's
    /// values are stored as before.
    /// </summary>
    [Collection("DbContextSequential")]
    public class TopicAddApplyNormalizedTests : IDisposable
    {
        private const long Gate = 7_000;
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly long _priorVoteTopic = Globals.VoteTopicRulesHeight;
        private readonly long _priorDeterministic = Globals.TopicAddDeterministicHeight;

        public TopicAddApplyNormalizedTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"topic_norm_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            Globals.VoteTopicRulesHeight = 999_999_999_999;
            Globals.TopicAddDeterministicHeight = Gate;
        }

        public void Dispose()
        {
            Globals.VoteTopicRulesHeight = _priorVoteTopic;
            Globals.TopicAddDeterministicHeight = _priorDeterministic;
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        [Fact]
        public void FromTheDeterministicHeight_TheApplySetsTheSystemFields()
        {
            Assert.True(StateData.NormalizesNewTopic(Gate));
            Assert.False(StateData.NormalizesNewTopic(Gate - 1));

            var topic = new TopicTrei { VoteYes = 50, VoteNo = 7, TopicOwnerAddress = "ROTHER", ValidatorCount = 1, TopicCreateDate = new DateTime(2000, 1, 1) };
            var tx = new Transaction { FromAddress = "RSENDER", Timestamp = 1_700_000_000 };
            StateData.NormalizeNewTopic(topic, tx, Gate);
            Assert.Equal(0, topic.VoteYes);
            Assert.Equal(0, topic.VoteNo);
            Assert.Equal("RSENDER", topic.TopicOwnerAddress);
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000).UtcDateTime, topic.TopicCreateDate);
            Assert.Equal(0, topic.ValidatorCount); // no validators registered in this chain
        }

        [Fact]
        public void BelowBothHeights_TheCreatorsValuesStand()
        {
            Globals.TopicAddDeterministicHeight = 999_999_999_999;
            Assert.False(StateData.NormalizesNewTopic(Gate));
            Globals.VoteTopicRulesHeight = Gate;
            Assert.True(StateData.NormalizesNewTopic(Gate));
        }
    }
}
