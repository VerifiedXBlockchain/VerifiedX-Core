using System;
using System.Collections.Generic;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Security regression: the caster burn-consensus messages (alert, confirmation) were accepted
    /// from anyone, and casters signed bound votes straight from a peer's alert without checking
    /// Base. Alerts must now be signed by a committee caster and fresh; confirmations must be a
    /// committee caster's bound-vote signature for the burn as locally recorded; and the burn
    /// evidence read from Base must match amount and destination exactly.
    /// </summary>
    public class BurnConsensusAuthenticationTests
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

        private const string Burn = "0xabc123";
        private const string Type = "POOL_EXIT";
        private const long Sats = 12_345_678;
        private const string Dest = "RDestinationAddress0000000000000001";

        private static HashSet<string> Committee(params Caster[] casters)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in casters) set.Add(c.Address);
            return set;
        }

        // ── Alerts ──────────────────────────────────────────────────────────────

        [Fact]
        public void Alert_FromCommitteeCaster_WithValidFreshSignature_Accepted()
        {
            var c = new Caster();
            long now = 1_700_000_000;
            var sig = c.Sign(BridgeCasterConsensus.BuildAlertMessage(Burn, Type, Sats, Dest, c.Address, now));
            var (ok, reason) = BridgeCasterConsensus.VerifyBurnAlert(Burn, Type, Sats, Dest, c.Address, now, sig, Committee(c), now + 5);
            Assert.True(ok, reason);
        }

        [Fact]
        public void Alert_FromNonCommitteeAddress_Refused_EvenWithValidSignature()
        {
            var outsider = new Caster();
            var member = new Caster();
            long now = 1_700_000_000;
            var sig = outsider.Sign(BridgeCasterConsensus.BuildAlertMessage(Burn, Type, Sats, Dest, outsider.Address, now));
            var (ok, reason) = BridgeCasterConsensus.VerifyBurnAlert(Burn, Type, Sats, Dest, outsider.Address, now, sig, Committee(member), now);
            Assert.False(ok);
            Assert.Contains("not a committee caster", reason);
        }

        [Fact]
        public void Alert_TamperedAmountOrDestination_Refused()
        {
            var c = new Caster();
            long now = 1_700_000_000;
            var sig = c.Sign(BridgeCasterConsensus.BuildAlertMessage(Burn, Type, Sats, Dest, c.Address, now));
            Assert.False(BridgeCasterConsensus.VerifyBurnAlert(Burn, Type, Sats + 1, Dest, c.Address, now, sig, Committee(c), now).Ok);
            Assert.False(BridgeCasterConsensus.VerifyBurnAlert(Burn, Type, Sats, "ROtherDest", c.Address, now, sig, Committee(c), now).Ok);
            Assert.False(BridgeCasterConsensus.VerifyBurnAlert(Burn, "BTC_EXIT", Sats, Dest, c.Address, now, sig, Committee(c), now).Ok);
        }

        [Fact]
        public void Alert_StaleTimestamp_Refused()
        {
            var c = new Caster();
            long signedAt = 1_700_000_000;
            var sig = c.Sign(BridgeCasterConsensus.BuildAlertMessage(Burn, Type, Sats, Dest, c.Address, signedAt));
            var (ok, reason) = BridgeCasterConsensus.VerifyBurnAlert(Burn, Type, Sats, Dest, c.Address, signedAt, sig, Committee(c),
                signedAt + BridgeCasterConsensus.ALERT_MAX_SKEW_SECONDS + 1);
            Assert.False(ok);
            Assert.Contains("timestamp", reason);
        }

        [Fact]
        public void Alert_MissingSignatureOrNonPositiveAmount_Refused()
        {
            var c = new Caster();
            long now = 1_700_000_000;
            Assert.False(BridgeCasterConsensus.VerifyBurnAlert(Burn, Type, Sats, Dest, c.Address, now, "", Committee(c), now).Ok);
            var sig = c.Sign(BridgeCasterConsensus.BuildAlertMessage(Burn, Type, 0, Dest, c.Address, now));
            Assert.False(BridgeCasterConsensus.VerifyBurnAlert(Burn, Type, 0, Dest, c.Address, now, sig, Committee(c), now).Ok);
        }

        // ── Confirmations ───────────────────────────────────────────────────────

        [Fact]
        public void Confirmation_BoundVoteFromCommitteeCaster_Accepted()
        {
            var c = new Caster();
            long ts = 1_700_000_000;
            var sig = c.Sign(BridgeCasterConsensus.BuildBoundVoteMessage(Burn, Type, Sats, Dest, ts));
            var (ok, reason) = BridgeCasterConsensus.VerifyConfirmation(c.Address, Burn, Type, Sats, Dest, ts, sig, Committee(c));
            Assert.True(ok, reason);
        }

        [Fact]
        public void Confirmation_SignedForDifferentAmount_Refused()
        {
            var c = new Caster();
            long ts = 1_700_000_000;
            var sig = c.Sign(BridgeCasterConsensus.BuildBoundVoteMessage(Burn, Type, Sats * 2, Dest, ts));
            // Local record says Sats; the peer signed 2*Sats -> refuse.
            Assert.False(BridgeCasterConsensus.VerifyConfirmation(c.Address, Burn, Type, Sats, Dest, ts, sig, Committee(c)).Ok);
        }

        [Fact]
        public void Confirmation_FromNonCommittee_Refused()
        {
            var c = new Caster();
            var other = new Caster();
            long ts = 1_700_000_000;
            var sig = c.Sign(BridgeCasterConsensus.BuildBoundVoteMessage(Burn, Type, Sats, Dest, ts));
            var (ok, reason) = BridgeCasterConsensus.VerifyConfirmation(c.Address, Burn, Type, Sats, Dest, ts, sig, Committee(other));
            Assert.False(ok);
            Assert.Contains("not a committee member", reason);
        }

        // ── Base evidence ───────────────────────────────────────────────────────

        [Fact]
        public void Evidence_MustMatchAmountAndDestinationExactly()
        {
            var ev = new BaseBridgeService.BurnEventInfo { AmountSats = Sats, Destination = Dest, Burner = "0xburner" };
            Assert.True(BridgeCasterConsensus.BurnEvidenceMatches(ev, Sats, Dest).Ok);
            Assert.True(BridgeCasterConsensus.BurnEvidenceMatches(ev, Sats, "  " + Dest + " ").Ok); // trimmed compare
            Assert.False(BridgeCasterConsensus.BurnEvidenceMatches(ev, Sats + 1, Dest).Ok);
            Assert.False(BridgeCasterConsensus.BurnEvidenceMatches(ev, Sats, "ROtherDest").Ok);
            Assert.False(BridgeCasterConsensus.BurnEvidenceMatches(null, Sats, Dest).Ok);
        }
    }
}
