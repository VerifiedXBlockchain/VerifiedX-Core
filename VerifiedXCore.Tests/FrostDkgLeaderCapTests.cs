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

        private void Open(string leader, bool completed = false, long? startedAt = null)
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
                StartTimestamp = startedAt ?? TimeUtil.GetTime(),
                IsCompleted = completed,
            };
        }

        [Fact]
        public void CompletedSessions_DoNotCountAgainstTheLeader()
        {
            // A user who already created contracts (DKGs completed) must be able to create another.
            Open("xUser", completed: true); Open("xUser", completed: true); Open("xUser", completed: true); Open("xUser", completed: true);
            Assert.Equal(0, FrostSessionStorage.CountDkgSessionsForLeader("xUser"));
            Open("xUser"); // one in progress
            Assert.Equal(1, FrostSessionStorage.CountDkgSessionsForLeader("xUser"));
        }

        [Fact]
        public void StaleAbandonedSessions_DoNotCountAgainstTheLeader()
        {
            var now = TimeUtil.GetTime();
            for (int i = 0; i < FrostSessionStorage.MAX_DKG_SESSIONS_PER_LEADER; i++)
                Open("xUser", startedAt: now - FrostSessionStorage.DKG_LEADER_CAP_WINDOW_SECONDS - 1);
            Assert.Equal(0, FrostSessionStorage.CountDkgSessionsForLeader("xUser", now));
        }

        [Fact]
        public void CapExceedsCoordinatorRetryCount()
        {
            // The coordinator retries a DKG start up to MAX_DKG_START_RETRIES (2) times with a fresh
            // session id under the same leader; the cap must leave room for the final attempt.
            Assert.True(FrostSessionStorage.MAX_DKG_SESSIONS_PER_LEADER >= 3);
        }

        [Fact]
        public void InProgressDkgForContract_DetectedUnlessCompletedOrSameSession()
        {
            var id = Guid.NewGuid().ToString();
            _created.Add(id);
            FrostSessionStorage.DKGSessions[id] = new DKGSession { SessionId = id, SmartContractUID = "sc-dup", LeaderAddress = "xA", ParticipantAddresses = new List<string>(), StartTimestamp = TimeUtil.GetTime() };

            Assert.True(FrostSessionStorage.HasInProgressDkgForContract("sc-dup", exceptSessionId: "other-session"));
            Assert.False(FrostSessionStorage.HasInProgressDkgForContract("sc-dup", exceptSessionId: id)); // same session re-checks itself
            Assert.False(FrostSessionStorage.HasInProgressDkgForContract("sc-other", exceptSessionId: null));

            FrostSessionStorage.DKGSessions[id].IsCompleted = true;
            Assert.False(FrostSessionStorage.HasInProgressDkgForContract("sc-dup", exceptSessionId: null));
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
