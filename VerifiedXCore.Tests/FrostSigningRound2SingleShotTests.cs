using System.Collections.Generic;
using VerifiedXCore.Bitcoin.FROST.Models;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Security regression: a FROST signing nonce is single-use. The round-2 handler used to call
    /// the native signer every time it was hit, with the same stored nonce secret. Anyone holding a
    /// replayable leader header could request three shares over three different commitment sets
    /// and solve for the validator's long-term key share. These tests pin the session-level
    /// guarantees the handler now relies on.
    /// </summary>
    public class FrostSigningRound2SingleShotTests
    {
        private const string Me = "xValidatorMe";
        private const string Peer = "xValidatorPeer";
        private const string MyCommitment = "{\"hiding\":\"aa\",\"binding\":\"bb\"}";

        private static SigningSession NewSession()
        {
            var s = new SigningSession
            {
                SessionId = "sess-1",
                MessageHash = "00ff",
                SmartContractUID = "sc-1",
                LeaderAddress = "xLeader",
                SignerAddresses = new List<string> { Me, Peer },
                RequiredThreshold = 100,
                MyKeyPackage = "kp",
                NonceSecret = "secret-nonce",
            };
            s.Round1Nonces.TryAdd(Me, MyCommitment);
            s.Round1Nonces.TryAdd(Peer, "{\"hiding\":\"cc\",\"binding\":\"dd\"}");
            return s;
        }

        [Fact]
        public void NonceSecret_IsHandedOutExactlyOnce_ThenWiped()
        {
            var s = NewSession();

            Assert.False(s.NonceConsumed);
            Assert.True(s.TryConsumeNonceSecret(out var first));
            Assert.Equal("secret-nonce", first);
            Assert.True(s.NonceConsumed);
            Assert.Null(s.NonceSecret);

            Assert.False(s.TryConsumeNonceSecret(out var second));
            Assert.Null(second);
        }

        [Fact]
        public void NonceSecret_MissingFromSession_CannotBeConsumed()
        {
            var s = NewSession();
            s.NonceSecret = null;
            Assert.False(s.TryConsumeNonceSecret(out var got));
            Assert.Null(got);
        }

        [Fact]
        public void NonceSecret_ConcurrentConsumers_OnlyOneWins()
        {
            var s = NewSession();
            int wins = 0;
            System.Threading.Tasks.Parallel.For(0, 32, _ =>
            {
                if (s.TryConsumeNonceSecret(out var v) && v != null)
                    System.Threading.Interlocked.Increment(ref wins);
            });
            Assert.Equal(1, wins);
        }

        [Fact]
        public void OwnCommitment_AcceptedWhenPostedUnchanged()
        {
            var s = NewSession();
            var posted = new Dictionary<string, string>
            {
                [Me] = MyCommitment,
                [Peer] = "{\"hiding\":\"cc\",\"binding\":\"dd\"}",
            };
            Assert.True(s.OwnCommitmentMatches(Me, posted));
        }

        [Fact]
        public void OwnCommitment_AcceptedWhenJsonEquivalentButReformatted()
        {
            var s = NewSession();
            var posted = new Dictionary<string, string>
            {
                [Me] = "{ \"binding\" : \"bb\", \"hiding\" : \"aa\" }",
                [Peer] = "x",
            };
            Assert.True(s.OwnCommitmentMatches(Me, posted));
        }

        [Fact]
        public void OwnCommitment_RefusedWhenSubstituted()
        {
            var s = NewSession();
            var posted = new Dictionary<string, string>
            {
                [Me] = "{\"hiding\":\"EVIL\",\"binding\":\"bb\"}",
                [Peer] = "x",
            };
            Assert.False(s.OwnCommitmentMatches(Me, posted));
        }

        [Fact]
        public void OwnCommitment_RefusedWhenDroppedOrNull()
        {
            var s = NewSession();
            Assert.False(s.OwnCommitmentMatches(Me, new Dictionary<string, string> { [Peer] = "x" }));
            Assert.False(s.OwnCommitmentMatches(Me, null));
            Assert.False(s.OwnCommitmentMatches("", new Dictionary<string, string> { [Me] = MyCommitment }));
        }

        [Fact]
        public void OwnCommitment_RefusedWhenSessionHasNoOwnRound1Entry()
        {
            var s = NewSession();
            s.Round1Nonces.TryRemove(Me, out _);
            Assert.False(s.OwnCommitmentMatches(Me, new Dictionary<string, string> { [Me] = MyCommitment }));
        }
    }
}
