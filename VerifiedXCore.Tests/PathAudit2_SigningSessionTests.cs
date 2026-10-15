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
        private static SigningSession Session(string leader, string txid, int input, long startedSecondsAgo = 0, string? hash = null) => new()
        {
            SessionId = Guid.NewGuid().ToString("N"), LeaderAddress = leader, BtcTxId = txid, InputIndex = input, MessageHash = hash ?? txid + ":" + input,
            StartTimestamp = TimeUtil.GetTime() - startedSecondsAgo, SignerAddresses = new List<string>(),
        };

        [Fact]
        public void OneLeader_CannotTakeAllTheRoom_NorKeepAnotherLeaderOffAnInput()
        {
            var open = new List<SigningSession>();
            // One leader, different transactions: stopped at its own limit, long before the validator is full.
            for (var i = 0; i < FrostSessionStorage.MAX_SIGNING_SESSIONS_PER_LEADER; i++)
            {
                Assert.Null(FrostSessionStorage.SigningStartRefusal(open, "leaderA", "hash" + i));
                open.Add(Session("leaderA", "tx" + i, 0, hash: "hash" + i));
            }
            Assert.Contains("This leader already has", FrostSessionStorage.SigningStartRefusal(open, "leaderA", "another"));
            Assert.True(open.Count < FrostSessionStorage.MAX_SIGNING_SESSIONS / 2);
            // Everybody else still has room.
            Assert.Null(FrostSessionStorage.SigningStartRefusal(open, "leaderB", "another"));

            // The same thing to sign: a leader may retry it once while its first session is open, no more...
            open.Clear();
            for (var i = 0; i < FrostSessionStorage.MAX_SIGNING_SESSIONS_PER_LEADER_AND_HASH; i++)
                open.Add(Session("requester", "pinned", 2, hash: "sighash"));
            Assert.Contains("for the same input", FrostSessionStorage.SigningStartRefusal(open, "requester", "SIGHASH"));
            // ...and what it holds open keeps nobody else from leading that input (three sessions of one leader used to).
            Assert.Null(FrostSessionStorage.SigningStartRefusal(open, "signer1", "sighash"));
            Assert.Null(FrostSessionStorage.SigningStartRefusal(open, "signer2", "sighash"));
            // Before the leader's signature is checked only the validator's total is judged.
            Assert.Null(FrostSessionStorage.SigningStartRefusal(open, null, null));
        }

        /// <summary>
        /// Counted per leader only, nine requester addresses with pinned withdrawals filled every validator's room and
        /// every payout stalled (fifth review, round 24). Requester-led ceremonies share a part of the room; the rest is
        /// kept for the key's holders.
        /// </summary>
        [Fact]
        public void RequesterLedCeremonies_ShareAPartOfTheRoom_TheRestIsKeptForTheKeysHolders()
        {
            var open = new List<SigningSession>();
            for (var r = 0; open.Count < FrostSessionStorage.MAX_REQUESTER_LED_SIGNING_SESSIONS; r++)
                for (var i = 0; i < FrostSessionStorage.MAX_SIGNING_SESSIONS_PER_LEADER && open.Count < FrostSessionStorage.MAX_REQUESTER_LED_SIGNING_SESSIONS; i++)
                {
                    Assert.Null(FrostSessionStorage.SigningStartRefusal(open, "requester" + r, "h" + r + ":" + i, ledByRequester: true));
                    var s = Session("requester" + r, "tx" + r + i, 0, hash: "h" + r + ":" + i);
                    s.LedByRequester = true;
                    open.Add(s);
                }
            Assert.Contains("requester-led", FrostSessionStorage.SigningStartRefusal(open, "fresh-requester", "x", ledByRequester: true));
            Assert.Null(FrostSessionStorage.SigningStartRefusal(open, "signer1", "x"));
            Assert.True(FrostSessionStorage.MAX_SIGNING_SESSIONS - FrostSessionStorage.MAX_REQUESTER_LED_SIGNING_SESSIONS >= 300);
        }

        /// <summary>A session whose share was given holds no place: a leader that just signed a 50-input transaction was refused for the next (round 25).</summary>
        [Fact]
        public void AFinishedSession_HoldsNoPlace_ButSessionsKeptAreBounded()
        {
            var open = new List<SigningSession>();
            for (var i = 0; i < FrostSessionStorage.MAX_SIGNING_SESSIONS_PER_LEADER; i++)
            {
                var s = Session("leaderA", "tx" + i, 0, hash: "h" + i);
                s.NonceSecret = "secret";
                Assert.True(s.TryConsumeNonceSecret(out _));
                open.Add(s);
            }
            Assert.Null(FrostSessionStorage.SigningStartRefusal(open, "leaderA", "next"));
            var many = Enumerable.Range(0, FrostSessionStorage.MAX_SIGNING_SESSIONS_HELD).Select(i => { var s = Session("l" + i, "t" + i, 0); s.NonceSecret = "x"; s.TryConsumeNonceSecret(out _); return s; }).ToList();
            Assert.Contains("Maximum concurrent", FrostSessionStorage.SigningStartRefusal(many, "new", "new"));
        }

        [Fact]
        public void TheValidatorAsAWholeHasALimitToo()
        {
            var open = Enumerable.Range(0, FrostSessionStorage.MAX_SIGNING_SESSIONS).Select(i => Session("leader" + i, "tx" + i, 0)).ToList();
            Assert.Contains("Maximum concurrent signing sessions", FrostSessionStorage.SigningStartRefusal(open, "new", "new"));
            Assert.Contains("Maximum concurrent signing sessions", FrostSessionStorage.SigningStartRefusal(open, null, null));
            open.RemoveAt(0);
            Assert.Null(FrostSessionStorage.SigningStartRefusal(open, "new", "new"));
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
