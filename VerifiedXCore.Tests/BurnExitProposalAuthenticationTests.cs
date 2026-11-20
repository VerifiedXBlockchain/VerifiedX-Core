using System;
using System.Collections.Generic;
using System.Linq;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Security regression: exit handler proposals were unauthenticated, so anyone could inject the
    /// lowest hash under an address that would never execute and stall every exit. Proposals must
    /// now be signed by a committee caster, carry the deterministic (recomputable) hash, be fresh,
    /// and only committee proposers can be elected.
    /// </summary>
    public class BurnExitProposalAuthenticationTests
    {
        private sealed class Caster
        {
            public PrivateKey Key { get; }
            public string PubKeyHex { get; }
            public string Address { get; }
            public Caster()
            {
                Key = new PrivateKey("secp256k1");
                PubKeyHex = "04" + Convert.ToHexString(Key.publicKey().toString()).ToLowerInvariant();
                Address = AccountData.GetHumanAddress(PubKeyHex);
            }
            public string Sign(string message) => VerifiedXCore.Services.SignatureService.CreateSignature(message, Key, PubKeyHex);
        }

        private const string Burn = "0xburn01";
        private static HashSet<string> Committee(params Caster[] cs) { var s = new HashSet<string>(StringComparer.Ordinal); foreach (var c in cs) s.Add(c.Address); return s; }

        [Fact]
        public void ProposalHash_IsDeterministicAndProposerBound()
        {
            const long t = 1_700_000_000;
            var h1 = BridgeCasterConsensus.ComputeProposalHash("xA", Burn, t);
            var h2 = BridgeCasterConsensus.ComputeProposalHash("xA", Burn, t + 1);
            var h3 = BridgeCasterConsensus.ComputeProposalHash("xB", Burn, t);
            Assert.Equal(h1, h2);
            Assert.NotEqual(h1, h3);
            Assert.False(string.IsNullOrEmpty(h1));
        }

        /// <summary>
        /// One caster had the lowest hash for a burn in every retry: elected, it could leave the burn unpaid for as long
        /// as it sat on the committee (fifth review, round 23). A retry in a later window draws again.
        /// </summary>
        [Fact]
        public void Election_DrawsAgainInALaterWindow()
        {
            var casters = new[] { "xA", "xB", "xC", "xD", "xE" };
            var committee = new HashSet<string>(casters, StringComparer.Ordinal);
            string Winner(long at) => BridgeCasterConsensus.SelectHandler(casters.Select(c => (c, BridgeCasterConsensus.ComputeProposalHash(c, Burn, at))), committee)!;
            const long t = 1_700_000_000;
            var winners = Enumerable.Range(0, 40).Select(i => Winner(t + i * BridgeCasterConsensus.ELECTION_WINDOW_SECONDS)).ToHashSet();
            Assert.True(winners.Count > 1, "the same caster won every window");
            Assert.Equal(Winner(t), Winner(t + 1));   // one round agrees on its winner
        }

        /// <summary>
        /// A caster's first confirmation for a burn was kept for good: a round's confirmations replayed after it blocked
        /// every later winner (fifth review, round 24). The newest is held, and a round counts only its own.
        /// </summary>
        [Fact]
        public void Confirmations_TheNewestIsHeld_AndARoundCountsOnlyItsOwn()
        {
            var held = new System.Collections.Concurrent.ConcurrentDictionary<string, BurnExitConsensusService.BurnExitConfirmation>();
            BurnExitConsensusService.BurnExitConfirmation Conf(string handler, long at) =>
                new() { BaseBurnTxHash = Burn, ConfirmingCasterAddress = "xB", AgreedHandlerAddress = handler, Timestamp = at };
            const long t = 1_700_000_000;
            Assert.True(BurnExitConsensusService.KeepNewestConfirmation(held, Conf("xM", t)));
            Assert.True(BurnExitConsensusService.KeepNewestConfirmation(held, Conf("xW", t + 900)));   // the next round's
            Assert.False(BurnExitConsensusService.KeepNewestConfirmation(held, Conf("xM", t)));        // the old one replayed
            Assert.Equal("xW", held["xB"].AgreedHandlerAddress);
            Assert.Single(BurnExitConsensusService.ConfirmationsOfThisRound(held.Values, "xW", t + 890));
            Assert.Empty(BurnExitConsensusService.ConfirmationsOfThisRound(held.Values, "xW", t + 900 + 3 * BridgeCasterConsensus.PROPOSAL_MAX_SKEW_SECONDS));
        }

        /// <summary>
        /// A confirmation can be taken before this node read the burn on Base, against the record a peer's alert made. Once
        /// the record is corrected from Base, one for the alert's amount is not counted: counted, it made up the threshold,
        /// the exit was refused before broadcast and the handler gave the burn up.
        /// </summary>
        [Fact]
        public void Confirmations_CountOnlyVotesForTheBurnAsTheRecordNowHoldsIt()
        {
            var handler = new Caster(); var liar = new Caster(); var honest = new Caster();
            var committee = Committee(handler, liar, honest);
            const long t = 1_700_000_000;
            const string dest = "bc1qexampledestination";
            BurnExitConsensusService.BurnExitConfirmation Vote(Caster c, decimal amount) => new()
            {
                BaseBurnTxHash = Burn, ConfirmingCasterAddress = c.Address, AgreedHandlerAddress = handler.Address, Timestamp = t,
                Signature = c.Sign(BridgeCasterConsensus.BuildBoundVoteMessage(Burn, "BTC_EXIT", (long)(amount * 100_000_000M), dest, t)),
            };
            // The alert said 0.02; Base says 0.01.
            var record = new BurnExitConsensusService.ProcessedBurnRecord { BaseBurnTxHash = Burn, ExitType = BurnExitConsensusService.BurnExitType.BtcExit, Amount = 0.02M, BtcDestination = dest };
            var held = new[] { Vote(liar, 0.02M), Vote(handler, 0.01M) };
            Assert.Equal(new[] { liar.Address }, BurnExitConsensusService.CountableConfirmations(record, held, handler.Address, t, committee).Select(c => c.ConfirmingCasterAddress));

            record.Amount = 0.01M; // corrected from Base
            Assert.Equal(2, BurnExitConsensusService.ConfirmationsOfThisRound(held, handler.Address, t).Count);   // what was counted
            Assert.Equal(new[] { handler.Address }, BurnExitConsensusService.CountableConfirmations(record, held, handler.Address, t, committee).Select(c => c.ConfirmingCasterAddress));
            // The honest caster's vote for the burn as it is makes the threshold.
            var withHonest = held.Append(Vote(honest, 0.01M));
            Assert.Equal(2, BurnExitConsensusService.CountableConfirmations(record, withHonest, handler.Address, t, committee).Count);
            // A vote from outside the committee is not counted either.
            var outsider = new Caster();
            Assert.Single(BurnExitConsensusService.CountableConfirmations(record, held.Append(Vote(outsider, 0.01M)), handler.Address, t, committee));
        }

        [Fact]
        public void Proposal_ForAnotherWindow_Refused()
        {
            var c = new Caster(); long now = 1_700_000_000;
            var other = BridgeCasterConsensus.ComputeProposalHash(c.Address, Burn, now + 5 * BridgeCasterConsensus.ELECTION_WINDOW_SECONDS);
            var sig = c.Sign(BridgeCasterConsensus.BuildProposalMessage(Burn, c.Address, other, now));
            Assert.False(BridgeCasterConsensus.VerifyProposal(Burn, c.Address, other, now, sig, Committee(c), now).Ok);
        }

        [Fact]
        public void Proposal_FromCommitteeCaster_WithCorrectHashAndSignature_Accepted()
        {
            var c = new Caster(); long now = 1_700_000_000;
            var hash = BridgeCasterConsensus.ComputeProposalHash(c.Address, Burn, now);
            var sig = c.Sign(BridgeCasterConsensus.BuildProposalMessage(Burn, c.Address, hash, now));
            var (ok, reason) = BridgeCasterConsensus.VerifyProposal(Burn, c.Address, hash, now, sig, Committee(c), now + 1);
            Assert.True(ok, reason);
        }

        [Fact]
        public void Proposal_WithForgedLowHash_Refused_EvenIfSigned()
        {
            var c = new Caster(); long now = 1_700_000_000;
            var forgedHash = "0000000000000000000000000000000000000000000000000000000000000001";
            var sig = c.Sign(BridgeCasterConsensus.BuildProposalMessage(Burn, c.Address, forgedHash, now));
            var (ok, reason) = BridgeCasterConsensus.VerifyProposal(Burn, c.Address, forgedHash, now, sig, Committee(c), now);
            Assert.False(ok);
            Assert.Contains("deterministic hash", reason);
        }

        [Fact]
        public void Proposal_FromNonCommittee_Refused()
        {
            var outsider = new Caster(); var member = new Caster(); long now = 1_700_000_000;
            var hash = BridgeCasterConsensus.ComputeProposalHash(outsider.Address, Burn, now);
            var sig = outsider.Sign(BridgeCasterConsensus.BuildProposalMessage(Burn, outsider.Address, hash, now));
            var (ok, reason) = BridgeCasterConsensus.VerifyProposal(Burn, outsider.Address, hash, now, sig, Committee(member), now);
            Assert.False(ok);
            Assert.Contains("not a committee caster", reason);
        }

        [Fact]
        public void Proposal_StaleOrUnsigned_Refused()
        {
            var c = new Caster(); long now = 1_700_000_000;
            var hash = BridgeCasterConsensus.ComputeProposalHash(c.Address, Burn, now);
            var sig = c.Sign(BridgeCasterConsensus.BuildProposalMessage(Burn, c.Address, hash, now));
            Assert.False(BridgeCasterConsensus.VerifyProposal(Burn, c.Address, hash, now, sig, Committee(c), now + BridgeCasterConsensus.PROPOSAL_MAX_SKEW_SECONDS + 1).Ok);
            Assert.False(BridgeCasterConsensus.VerifyProposal(Burn, c.Address, hash, now, "", Committee(c), now).Ok);
        }

        [Fact]
        public void SelectHandler_IgnoresNonCommitteeLowestHash()
        {
            var committee = new HashSet<string>(StringComparer.Ordinal) { "xA", "xB" };
            var proposals = new List<(string, string)>
            {
                ("xAttacker", "0000"), // lowest hash, not a caster
                ("xB", "5555"),
                ("xA", "9999"),
            };
            Assert.Equal("xB", BridgeCasterConsensus.SelectHandler(proposals, committee));
        }

        [Fact]
        public void SelectHandler_NoCommitteeProposer_ReturnsNull()
        {
            var committee = new HashSet<string>(StringComparer.Ordinal) { "xA" };
            Assert.Null(BridgeCasterConsensus.SelectHandler(new List<(string, string)> { ("xZ", "0000") }, committee));
            Assert.Null(BridgeCasterConsensus.SelectHandler(new List<(string, string)>(), committee));
        }
    }
}
