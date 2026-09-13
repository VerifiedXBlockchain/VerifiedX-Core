using System.Collections.Concurrent;
using VerifiedXCore.Models;
using VerifiedXCore.Nodes;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Sep 12 2026 bootstrap-caster stall, round-level fixes:
    ///  • HeightGate no longer admits a producer that is behind the tip (the elected winner was at
    ///    tip-2 and "returned no block" for three ~16 s rounds while the other casters moved on);
    ///  • the caster block-fetch fallback adopts a certified majority block from a different
    ///    producer instead of discarding it;
    ///  • live tip+1 blocks reaching a caster by P2P gossip / the caster hub go through the same
    ///    agreed-hash / attestation admission as message 7 instead of straight into ValidateBlocks.
    /// </summary>
    [Collection("GlobalCasterState")]
    public class CasterRoundResilienceTests
    {
        private static Block B(long height, string hash, string validator, string prev, ConsensusCertificate? cert = null)
            => new Block { Height = height, Hash = hash, Validator = validator, PrevHash = prev, ConsensusCertificate = cert };

        // ── HeightGate ──────────────────────────────────────────────────────────

        [Fact]
        public void HeightGate_RejectsAnyLag_AllowsTipOrAhead()
        {
            Assert.Equal(0, ProofUtility.HEIGHT_GATE_MAX_BEHIND);
            Assert.True(ProofUtility.IsWithinHeightGate(7286401, 7286401));  // at tip
            Assert.True(ProofUtility.IsWithinHeightGate(7286400, 7286403));  // ahead of us (we are the laggard)
            Assert.False(ProofUtility.IsWithinHeightGate(7286401, 7286400)); // one behind
            Assert.False(ProofUtility.IsWithinHeightGate(7286401, 7286399)); // the incident: behind=2
        }

        // ── Majority-block adoption in the fetch fallback ───────────────────────

        [Fact]
        public void IsAdoptableMajorityBlock_AcceptsCertifiedDifferentProducerExtendingTip()
        {
            var b = B(7286402, "ec42eee8", "RLZLJ", "78ba5ad4");
            Assert.True(BlockcasterNode.IsAdoptableMajorityBlock(b, 7286402, "78ba5ad4", "RD5H", _ => true));
        }

        [Fact]
        public void IsAdoptableMajorityBlock_RejectsWithoutValidCertificate()
        {
            var b = B(7286402, "ec42eee8", "RLZLJ", "78ba5ad4");
            Assert.False(BlockcasterNode.IsAdoptableMajorityBlock(b, 7286402, "78ba5ad4", "RD5H", _ => false));
        }

        [Fact]
        public void IsAdoptableMajorityBlock_RejectsWrongHeightOrParent()
        {
            Assert.False(BlockcasterNode.IsAdoptableMajorityBlock(B(7286403, "x", "RLZLJ", "78ba5ad4"), 7286402, "78ba5ad4", "RD5H", _ => true));
            Assert.False(BlockcasterNode.IsAdoptableMajorityBlock(B(7286402, "x", "RLZLJ", "other-parent"), 7286402, "78ba5ad4", "RD5H", _ => true));
            Assert.False(BlockcasterNode.IsAdoptableMajorityBlock(B(7286402, "x", "RLZLJ", "78ba5ad4"), 7286402, null, "RD5H", _ => true));
        }

        [Fact]
        public void IsAdoptableMajorityBlock_LeavesSameProducerToNormalPath()
        {
            // Same producer as the agreed winner is the ordinary fallback success path, not adoption.
            Assert.False(BlockcasterNode.IsAdoptableMajorityBlock(B(7286402, "x", "RD5H", "78ba5ad4"), 7286402, "78ba5ad4", "RD5H", _ => true));
            Assert.False(BlockcasterNode.IsAdoptableMajorityBlock(null, 7286402, "78ba5ad4", "RD5H", _ => true));
        }

        [Fact]
        public void HasValidCertificate_FalseWhenNoCertificate()
        {
            // Strict check: a block with no certificate is never adoptable on its own authority,
            // regardless of whether certificates are enforced at that height.
            Assert.False(ConsensusCertificateVerifier.HasValidCertificate(B(1, "h", "v", "p")));
            Assert.False(ConsensusCertificateVerifier.HasValidCertificate(null));
        }

        // ── Gossip admission gate ──────────────────────────────────────────────

        [Fact]
        public async Task GossipGate_NonCaster_AlwaysAdmits()
        {
            var savedCaster = Globals.IsBlockCaster;
            try
            {
                Globals.IsBlockCaster = false;
                Assert.True(await BlockcasterNode.TryAdmitLiveBlockAsCasterAsync(B(5, "h", "v", "p"), "test"));
            }
            finally { Globals.IsBlockCaster = savedCaster; }
        }

        [Fact]
        public async Task GossipGate_Caster_UsesAgreedHashWhenPresent()
        {
            var savedCaster = Globals.IsBlockCaster;
            var savedLast = Globals.LastBlock;
            var savedDict = Globals.CasterApprovedBlockHashDict;
            try
            {
                Globals.IsBlockCaster = true;
                Globals.LastBlock = new Block { Height = 7286401, Hash = "78ba5ad4" };
                Globals.CasterApprovedBlockHashDict = new ConcurrentDictionary<long, string>();
                Globals.CasterApprovedBlockHashDict[7286402] = "ec42eee8";

                Assert.True(await BlockcasterNode.TryAdmitLiveBlockAsCasterAsync(B(7286402, "ec42eee8", "v", "78ba5ad4"), "test"));
                Assert.False(await BlockcasterNode.TryAdmitLiveBlockAsCasterAsync(B(7286402, "other", "v", "78ba5ad4"), "test"));

                // Not a live tip+1 block: the download / gap paths own it — always admitted here.
                Assert.True(await BlockcasterNode.TryAdmitLiveBlockAsCasterAsync(B(7286405, "far", "v", "p"), "test"));
                Assert.True(await BlockcasterNode.TryAdmitLiveBlockAsCasterAsync(null, "test"));
            }
            finally
            {
                Globals.IsBlockCaster = savedCaster;
                Globals.LastBlock = savedLast;
                Globals.CasterApprovedBlockHashDict = savedDict;
            }
        }
    }
}
