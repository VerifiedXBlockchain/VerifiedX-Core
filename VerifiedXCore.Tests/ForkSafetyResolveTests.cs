using System;
using System.Collections.Generic;
using System.Linq;
using VerifiedXCore;
using VerifiedXCore.Models;
using VerifiedXCore.Nodes;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Mainnet fork at 7,414,815 (Oct 1 2026), the recovery side. RFoK held the wrong block and knew the majority held
    /// another, but: its fork resolution always used the first majority caster as reference, which served a stale block
    /// (resolution never tried the others); every live caster's vote counted, including one outside the record; its own
    /// block-hash sync acted on the plurality, could not replace a committed block, gave up after three tries and kept
    /// casting; and nothing stopped a caster from signing two different blocks at one height.
    /// </summary>
    [Collection("DbContextSequential")]
    public class ForkSafetyResolveTests : IDisposable
    {
        private const string Agreed = "8d0b75df14f65da2";
        private const string Stale = "71aa3a89563a7dae";

        public ForkSafetyResolveTests() => AttestationGuard.ResetForTests();
        public void Dispose() => AttestationGuard.ResetForTests();

        // ── A.6: one attestation per height ───────────────────────────────────────────────────────────

        [Fact]
        public void ACaster_SignsOnlyOneBlockPerHeight()
        {
            Assert.True(AttestationGuard.TryClaim(7_414_815, Agreed));
            Assert.True(AttestationGuard.TryClaim(7_414_815, Agreed));      // the same block again (attach, then publish)
            Assert.False(AttestationGuard.TryClaim(7_414_815, Stale));      // a second, different block: refused
            Assert.True(AttestationGuard.TryClaim(7_414_816, Stale));       // other heights are independent
            Assert.Equal(Agreed, AttestationGuard.SignedAt(7_414_815));
            Assert.False(AttestationGuard.TryClaim(7_414_817, null));
        }

        [Fact]
        public void ACommittedTipOtherThanTheSignedBlock_IsAWrongBlock()
        {
            Assert.Null(BlockcasterNode.SignedElsewhere(7_414_815, Stale));                // nothing signed there
            AttestationGuard.TryClaim(7_414_815, Agreed);
            Assert.Equal(Agreed, BlockcasterNode.SignedElsewhere(7_414_815, Stale));       // RFoK: signed 8d0b…, holds 71aa…
            Assert.Null(BlockcasterNode.SignedElsewhere(7_414_815, Agreed));
        }

        // ── A.4 / A.5: who counts and when a caster holds ─────────────────────────────────────────────

        [Fact]
        public void MinorityFork_NeedsAQuorumOfTheCommittee()
        {
            // Committee of 5, quorum 3. Peer hashes are the OTHER members' answers; our own vote counts for ours.
            Assert.Equal(Agreed, BlockcasterNode.MinorityForkHash(Stale, new[] { Agreed, Agreed, Agreed }, 3));
            // The Oct 1 split as RFoK saw it: 2 against (RH9X, RK28), RCwBk with us — a tie, not a quorum.
            Assert.Null(BlockcasterNode.MinorityForkHash(Stale, new[] { Agreed, Agreed, Stale }, 3));
            // Plurality without a quorum is not enough (this used to trigger "MISMATCH" on two votes).
            Assert.Null(BlockcasterNode.MinorityForkHash(Stale, new[] { Agreed, Agreed }, 3));
            // We hold the majority.
            Assert.Null(BlockcasterNode.MinorityForkHash(Agreed, new[] { Agreed, Agreed, Stale }, 3));
        }

        [Fact]
        public void OnlyCommitteeMembers_CountAsCasters_InTheForkVote()
        {
            var committee = new HashSet<string> { "RCwBk", "RDABc", "RFoKr", "RH9XA", "RK28y" };
            var live = new List<Peers>
            {
                new() { ValidatorAddress = "RH9XA", PeerIP = "66.175.236.113" },
                new() { ValidatorAddress = "RVGy4", PeerIP = "95.217.239.40" },     // casting, not in the record
                new() { ValidatorAddress = "RCwBk", PeerIP = "::ffff:35.183.216.32" },
            };
            var record = new List<CasterInfo> { new() { Address = "RDABc", PeerIP = "18.171.99.200" }, new() { Address = "RK28y", PeerIP = "15.204.9.193" } };

            var ips = ForkDetectionService.CasterIpsFor(committee, live, record);

            Assert.Contains("66.175.236.113", ips);
            Assert.Contains("35.183.216.32", ips);
            Assert.Contains("18.171.99.200", ips);      // a committee member reachable only through the record
            Assert.Contains("15.204.9.193", ips);
            Assert.DoesNotContain("95.217.239.40", ips); // the non-member is an ordinary peer

            // Without a record (legacy era) every live caster counts, as before.
            Assert.Contains("95.217.239.40", ForkDetectionService.CasterIpsFor(null, live, null));
        }

        // ── A.3: every majority peer is a candidate reference ─────────────────────────────────────────

        [Fact]
        public void Resolution_TriesEveryMajorityPeer_CastersFirst()
        {
            var order = ForkDetectionService.ReferenceOrder(
                new[] { "10.0.0.9", "95.217.239.40", "66.175.236.113", "15.204.9.193", "66.175.236.113" },
                new HashSet<string> { "66.175.236.113", "15.204.9.193" });
            Assert.Equal(4, order.Count);
            Assert.Equal(new[] { "66.175.236.113", "15.204.9.193" }, order.Take(2).OrderByDescending(x => x));
            Assert.Contains("95.217.239.40", order);
            Assert.Contains("10.0.0.9", order);
        }
    }
}
