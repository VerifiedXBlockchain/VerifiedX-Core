using System;
using System.Collections.Generic;
using System.Linq;
using VerifiedXCore.Bitcoin.FROST.Models;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Room for signing sessions on a validator (second path audit, 14 October 2026). One session signs one input of
    /// one Bitcoin transaction. Fifty, kept for an hour, were used up by a handful of withdrawals being signed at once
    /// (each led in turn by several signers), and the requester of one small pinned withdrawal could fill them with
    /// fresh session ids: every other signing on that validator was then refused for an hour.
    /// </summary>
    public class PathAudit2_SigningSessionTests
    {
        private static SigningSession Session(string leader, string txid, int input, long startedSecondsAgo = 0) => new()
        {
            SessionId = Guid.NewGuid().ToString("N"), LeaderAddress = leader, BtcTxId = txid, InputIndex = input,
            StartTimestamp = TimeUtil.GetTime() - startedSecondsAgo, SignerAddresses = new List<string>(),
        };

        [Fact]
        public void OneLeader_OrOneInput_CannotTakeAllTheRoom()
        {
            var open = new List<SigningSession>();
            // One leader, different transactions: stopped at its own limit, long before the validator is full.
            for (var i = 0; i < FrostSessionStorage.MAX_SIGNING_SESSIONS_PER_LEADER; i++)
            {
                Assert.Null(FrostSessionStorage.SigningStartRefusal(open, "leaderA", "tx" + i, 0));
                open.Add(Session("leaderA", "tx" + i, 0));
            }
            Assert.Contains("This leader already has", FrostSessionStorage.SigningStartRefusal(open, "leaderA", "another", 0));
            Assert.True(open.Count < FrostSessionStorage.MAX_SIGNING_SESSIONS / 2);
            // Everybody else still has room.
            Assert.Null(FrostSessionStorage.SigningStartRefusal(open, "leaderB", "another", 0));

            // One input of one transaction: its signers lead it in turn, so a few sessions, not any number.
            for (var i = 0; i < FrostSessionStorage.MAX_SIGNING_SESSIONS_PER_INPUT; i++)
                open.Add(Session("leader" + i, "pinned", 2));
            Assert.Contains("Input 2 of transaction pinned", FrostSessionStorage.SigningStartRefusal(open, "leaderZ", "pinned", 2));
            Assert.Null(FrostSessionStorage.SigningStartRefusal(open, "leaderZ", "pinned", 3));   // its other inputs are their own
        }

        [Fact]
        public void TheValidatorAsAWholeHasALimitToo()
        {
            var open = Enumerable.Range(0, FrostSessionStorage.MAX_SIGNING_SESSIONS).Select(i => Session("leader" + i, "tx" + i, 0)).ToList();
            Assert.Contains("Maximum concurrent signing sessions", FrostSessionStorage.SigningStartRefusal(open, "new", "new", 0));
            open.RemoveAt(0);
            Assert.Null(FrostSessionStorage.SigningStartRefusal(open, "new", "new", 0));
        }

        [Fact]
        public void ASigningSession_IsDroppedTenMinutesAfterItStarted()
        {
            var fresh = Session("leaderA", "txFresh", 0, startedSecondsAgo: FrostSessionStorage.SIGNING_SESSION_SECONDS - 30);
            var old = Session("leaderA", "txOld", 0, startedSecondsAgo: FrostSessionStorage.SIGNING_SESSION_SECONDS + 30);
            try
            {
                Assert.True(FrostSessionStorage.SigningSessions.TryAdd(fresh.SessionId, fresh));
                Assert.True(FrostSessionStorage.SigningSessions.TryAdd(old.SessionId, old));
                FrostSessionStorage.CleanupOldSessions();
                Assert.True(FrostSessionStorage.SigningSessions.ContainsKey(fresh.SessionId));
                Assert.False(FrostSessionStorage.SigningSessions.ContainsKey(old.SessionId));
            }
            finally
            {
                FrostSessionStorage.SigningSessions.TryRemove(fresh.SessionId, out _);
                FrostSessionStorage.SigningSessions.TryRemove(old.SessionId, out _);
            }
        }
    }
}
