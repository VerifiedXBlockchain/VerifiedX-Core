using System;
using System.Collections.Generic;
using VerifiedXCore.Bitcoin.FROST.Models;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Denial-of-service regression: any address could open DKG sessions until the global cap was
    /// hit, blocking legitimate contract creation. Each leader is now capped independently.
    /// Session storage is process-global, so these tests run in the sequential collection and
    /// clean up after themselves.
    /// </summary>
    [Collection("DbContextSequential")]
    public class FrostDkgLeaderCapTests : IDisposable
    {
        private readonly List<string> _created = new();

        public void Dispose()
        {
            foreach (var id in _created) FrostSessionStorage.DKGSessions.TryRemove(id, out _);
        }

        private void Open(string leader)
        {
            var id = Guid.NewGuid().ToString();
            _created.Add(id);
            FrostSessionStorage.DKGSessions[id] = new DKGSession
            {
                SessionId = id,
                SmartContractUID = "sc-" + id,
                LeaderAddress = leader,
                ParticipantAddresses = new List<string> { "xV1", "xV2" },
                RequiredThreshold = 51,
                StartTimestamp = TimeUtil.GetTime(),
            };
        }

        [Fact]
        public void CapRule_AllowsBelowCap_RefusesAtCap()
        {
            Assert.True(FrostSessionStorage.LeaderMayOpenDkgSession(0));
            Assert.True(FrostSessionStorage.LeaderMayOpenDkgSession(FrostSessionStorage.MAX_DKG_SESSIONS_PER_LEADER - 1));
            Assert.False(FrostSessionStorage.LeaderMayOpenDkgSession(FrostSessionStorage.MAX_DKG_SESSIONS_PER_LEADER));
            Assert.False(FrostSessionStorage.LeaderMayOpenDkgSession(FrostSessionStorage.MAX_DKG_SESSIONS_PER_LEADER + 5));
        }

        [Fact]
        public void Counter_CountsOnlyThatLeader()
        {
            Open("xSpammer"); Open("xSpammer"); Open("xHonest");
            Assert.Equal(2, FrostSessionStorage.CountDkgSessionsForLeader("xSpammer"));
            Assert.Equal(1, FrostSessionStorage.CountDkgSessionsForLeader("xHonest"));
            Assert.Equal(0, FrostSessionStorage.CountDkgSessionsForLeader("xNobody"));
            Assert.Equal(0, FrostSessionStorage.CountDkgSessionsForLeader(""));
            Assert.Equal(0, FrostSessionStorage.CountDkgSessionsForLeader(null));
        }

        [Fact]
        public void SpammerAtCap_DoesNotBlockHonestLeader()
        {
            for (int i = 0; i < FrostSessionStorage.MAX_DKG_SESSIONS_PER_LEADER; i++) Open("xSpammer");
            Assert.False(FrostSessionStorage.LeaderMayOpenDkgSession(FrostSessionStorage.CountDkgSessionsForLeader("xSpammer")));
            Assert.True(FrostSessionStorage.LeaderMayOpenDkgSession(FrostSessionStorage.CountDkgSessionsForLeader("xHonest")));
        }
    }
}
