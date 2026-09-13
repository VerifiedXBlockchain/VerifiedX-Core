using System;
using System.Collections.Generic;
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
            var h1 = BridgeCasterConsensus.ComputeProposalHash("xA", Burn);
            var h2 = BridgeCasterConsensus.ComputeProposalHash("xA", Burn);
            var h3 = BridgeCasterConsensus.ComputeProposalHash("xB", Burn);
            Assert.Equal(h1, h2);
            Assert.NotEqual(h1, h3);
            Assert.False(string.IsNullOrEmpty(h1));
        }

        [Fact]
        public void Proposal_FromCommitteeCaster_WithCorrectHashAndSignature_Accepted()
        {
            var c = new Caster(); long now = 1_700_000_000;
            var hash = BridgeCasterConsensus.ComputeProposalHash(c.Address, Burn);
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
            var hash = BridgeCasterConsensus.ComputeProposalHash(outsider.Address, Burn);
            var sig = outsider.Sign(BridgeCasterConsensus.BuildProposalMessage(Burn, outsider.Address, hash, now));
            var (ok, reason) = BridgeCasterConsensus.VerifyProposal(Burn, outsider.Address, hash, now, sig, Committee(member), now);
            Assert.False(ok);
            Assert.Contains("not a committee caster", reason);
        }

        [Fact]
        public void Proposal_StaleOrUnsigned_Refused()
        {
            var c = new Caster(); long now = 1_700_000_000;
            var hash = BridgeCasterConsensus.ComputeProposalHash(c.Address, Burn);
            var sig = c.Sign(BridgeCasterConsensus.BuildProposalMessage(Burn, c.Address, hash, now));
            Assert.False(BridgeCasterConsensus.VerifyProposal(Burn, c.Address, hash, now, sig, Committee(c), now + BridgeCasterConsensus.ALERT_MAX_SKEW_SECONDS + 1).Ok);
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
