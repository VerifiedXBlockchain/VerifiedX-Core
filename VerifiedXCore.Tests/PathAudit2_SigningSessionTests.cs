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
        private static SigningSession Session(string leader, string txid, int input, long startedSecondsAgo = 0, string? hash = null, string? requesterOfVault = null) => new()
        {
            SessionId = Guid.NewGuid().ToString("N"), LeaderAddress = leader, BtcTxId = txid, InputIndex = input, MessageHash = hash ?? txid + ":" + input,
            StartTimestamp = TimeUtil.GetTime() - startedSecondsAgo, SignerAddresses = new List<string>(),
            LedByRequester = requesterOfVault != null, VaultUID = requesterOfVault,
        };

        private static SigningSession Finished(SigningSession s)
        {
            s.NonceSecret = "secret";
            Assert.True(s.TryConsumeNonceSecret(out _));
            return s;
        }

        /// <summary>
        /// Finished sessions make way for a new start when the validator's total is reached: one leader starting and
        /// finishing over and over filled the total and every start was refused (round 35). Open sessions stay.
        /// </summary>
        [Fact]
        public void FinishedSessions_MakeWayAtTheTotal_OpenOnesStay()
        {
            var tag = "r35-" + Guid.NewGuid().ToString("N");
            try
            {
                for (var i = 0; i < FrostSessionStorage.MAX_SIGNING_SESSIONS_HELD; i++)
                {
                    var done = new SigningSession { SessionId = $"{tag}-done-{i}", LeaderAddress = "flooder", NonceSecret = "n", StartTimestamp = 1_000 + i };
                    Assert.True(done.TryConsumeNonceSecret(out _));                                       // its share given
                    FrostSessionStorage.SigningSessions[$"{tag}-done-{i}"] = done;
                }
                FrostSessionStorage.SigningSessions[$"{tag}-open"] = new SigningSession { SessionId = $"{tag}-open", LeaderAddress = "holder", NonceSecret = "n", StartTimestamp = 500 };
                Assert.NotNull(FrostSessionStorage.SigningStartRefusal(FrostSessionStorage.SigningSessions.Values.ToList(), null, null));

                FrostSessionStorage.DropFinishedForRoom();
                Assert.Null(FrostSessionStorage.SigningStartRefusal(FrostSessionStorage.SigningSessions.Values.ToList(), "holder", "next"));
                Assert.True(FrostSessionStorage.SigningSessions.ContainsKey($"{tag}-open"));
                Assert.False(FrostSessionStorage.SigningSessions.ContainsKey($"{tag}-done-0"));          // the oldest finished went
                Assert.True(FrostSessionStorage.SigningSessions.ContainsKey($"{tag}-done-{FrostSessionStorage.MAX_SIGNING_SESSIONS_HELD - 1}"));
            }
            finally
            {
                foreach (var key in FrostSessionStorage.SigningSessions.Keys.Where(k => k.StartsWith(tag)).ToList())
                    FrostSessionStorage.SigningSessions.TryRemove(key, out _);
            }
        }

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

            // The same thing to sign: a leader has one session open for it, no more...
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
        /// every payout stalled (fifth review, round 24). A requester-led ceremony's place is its vault's: a vault's
        /// requesters, however many, hold that vault's places, and neither another vault's nor the key holders'.
        /// </summary>
        [Fact]
        public void RequesterLedCeremonies_HoldTheirOwnVaultsPlaces_AndNoOthers()
        {
            var open = new List<SigningSession>();
            for (var r = 0; r < FrostSessionStorage.MAX_REQUESTER_LED_SIGNING_SESSIONS_PER_VAULT; r++)
            {
                Assert.Null(FrostSessionStorage.SigningStartRefusal(open, "requester" + r, "h" + r, ledByRequester: true, vaultUid: "vault1"));
                open.Add(Session("requester" + r, "tx" + r, 0, hash: "h" + r, requesterOfVault: "vault1"));
            }
            Assert.Contains("for this vault", FrostSessionStorage.SigningStartRefusal(open, "fresh-requester", "x", ledByRequester: true, vaultUid: "vault1"));
            Assert.Null(FrostSessionStorage.SigningStartRefusal(open, "fresh-requester", "x", ledByRequester: true, vaultUid: "vault2"));
            Assert.Null(FrostSessionStorage.SigningStartRefusal(open, "signer1", "x", vaultUid: "vault1"));

            // All of them together are bounded too (what they may hold in memory): that refuses requesters, never a holder.
            for (var v = 2; open.Count < FrostSessionStorage.MAX_REQUESTER_LED_SIGNING_SESSIONS; v++)
                for (var r = 0; r < FrostSessionStorage.MAX_REQUESTER_LED_SIGNING_SESSIONS_PER_VAULT; r++)
                    open.Add(Session($"requester{v}-{r}", $"tx{v}-{r}", 0, requesterOfVault: "vault" + v));
            Assert.Contains("requester-led", FrostSessionStorage.SigningStartRefusal(open, "fresh-requester", "x", ledByRequester: true, vaultUid: "a-new-vault"));
            Assert.Null(FrostSessionStorage.SigningStartRefusal(open, "signer1", "x", vaultUid: "a-new-vault"));
            Assert.Null(FrostSessionStorage.SigningStartRefusal(open, null, null));
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

        /// <summary>
        /// The room of the keys' holders and of casters has a limit too: never less than MAX_SIGNING_SESSIONS, and
        /// every validator's own limit as the validator set grows. Before the leader is known a start is refused only
        /// when there is no place in any room.
        /// </summary>
        [Fact]
        public void TheRoomOfTheKeysHoldersHasALimitToo_AndFollowsTheValidatorSet()
        {
            var open = Enumerable.Range(0, FrostSessionStorage.MAX_SIGNING_SESSIONS).Select(i => Session("leader" + i, "tx" + i, 0)).ToList();
            Assert.Contains("Maximum concurrent signing sessions", FrostSessionStorage.SigningStartRefusal(open, "new", "new", validators: 0));
            Assert.Contains("Maximum concurrent signing sessions", FrostSessionStorage.SigningStartRefusal(open, "new", "new", validators: FrostSessionStorage.MAX_SIGNING_SESSIONS / FrostSessionStorage.MAX_SIGNING_SESSIONS_PER_LEADER));
            Assert.Null(FrostSessionStorage.SigningStartRefusal(open, "new", "new", validators: FrostSessionStorage.MAX_SIGNING_SESSIONS / FrostSessionStorage.MAX_SIGNING_SESSIONS_PER_LEADER + 1));
            Assert.Equal(FrostSessionStorage.PlannedMaxValidators * FrostSessionStorage.MAX_SIGNING_SESSIONS_PER_LEADER, FrostSessionStorage.SigningRoomForValidators(FrostSessionStorage.PlannedMaxValidators));
            // A requester still has its vault's place, and nobody is refused before its leader is known.
            Assert.Null(FrostSessionStorage.SigningStartRefusal(open, "requester", "new", ledByRequester: true, vaultUid: "vault", validators: 0));
            Assert.Null(FrostSessionStorage.SigningStartRefusal(open, null, null, validators: 0));
            for (var v = 0; open.Count < FrostSessionStorage.MAX_SIGNING_SESSIONS + FrostSessionStorage.MAX_REQUESTER_LED_SIGNING_SESSIONS; v++)
                open.Add(Session("requester" + v, "rtx" + v, 0, requesterOfVault: "vault" + v));
            Assert.Contains("Maximum concurrent signing sessions", FrostSessionStorage.SigningStartRefusal(open, null, null, validators: 0));
            open.RemoveAt(0);
            Assert.Null(FrostSessionStorage.SigningStartRefusal(open, "new", "new", validators: 0));
        }

        /// <summary>A session whose Round 2 has not come holds its place for two minutes, whoever counts.</summary>
        [Fact]
        public void ASessionWithoutItsRound2_HoldsNoPlaceAfterTwoMinutes()
        {
            var open = Enumerable.Range(0, FrostSessionStorage.MAX_SIGNING_SESSIONS_PER_LEADER)
                .Select(i => Session("leaderA", "tx" + i, 0, startedSecondsAgo: FrostSessionStorage.SIGNING_UNPROGRESSED_SECONDS - 10)).ToList();
            Assert.Contains("This leader already has", FrostSessionStorage.SigningStartRefusal(open, "leaderA", "next"));
            foreach (var s in open) s.StartTimestamp -= 20;
            Assert.Null(FrostSessionStorage.SigningStartRefusal(open, "leaderA", "next"));
        }

        [Fact]
        public void ASigningSession_IsDroppedTenMinutesAfterItStarted_AndAfterTwoWithoutItsRound2()
        {
            var fresh = Finished(Session("leaderA", "txFresh", 0, startedSecondsAgo: FrostSessionStorage.SIGNING_SESSION_SECONDS - 30));
            var old = Finished(Session("leaderA", "txOld", 0, startedSecondsAgo: FrostSessionStorage.SIGNING_SESSION_SECONDS + 30));
            var open = Session("leaderA", "txOpen", 0, startedSecondsAgo: FrostSessionStorage.SIGNING_UNPROGRESSED_SECONDS - 30);
            var idle = Session("leaderA", "txIdle", 0, startedSecondsAgo: FrostSessionStorage.SIGNING_UNPROGRESSED_SECONDS + 30);
            var all = new[] { fresh, old, open, idle };
            try
            {
                foreach (var s in all) Assert.True(FrostSessionStorage.SigningSessions.TryAdd(s.SessionId, s));
                FrostSessionStorage.CleanupOldSessions();
                Assert.True(FrostSessionStorage.SigningSessions.ContainsKey(fresh.SessionId));
                Assert.False(FrostSessionStorage.SigningSessions.ContainsKey(old.SessionId));
                Assert.True(FrostSessionStorage.SigningSessions.ContainsKey(open.SessionId));
                Assert.False(FrostSessionStorage.SigningSessions.ContainsKey(idle.SessionId));
            }
            finally
            {
                foreach (var s in all) FrostSessionStorage.SigningSessions.TryRemove(s.SessionId, out _);
            }
        }
    }
}
