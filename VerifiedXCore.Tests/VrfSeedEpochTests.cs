using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// From VrfSeedHeight a producer's VRF for height h is seeded from its epoch's anchor block, VrfAnchorLagBlocks below
    /// the epoch's first height, and the height alone: the parent's hash, which its producer chose, no longer enters it.
    /// Only a validator registered before the anchor produces in the epoch. Below the height the VRF is as before.
    /// </summary>
    [Collection("GlobalCasterState")]
    public class VrfSeedEpochTests : IDisposable
    {
        private const long SeedHeight = 100_000;
        private const string PublicKey = "04aabbcc";
        private readonly long _priorSeedHeight = Globals.VrfSeedHeight;
        private readonly Func<long, string?> _priorHashAt = VrfSeed.BlockHashAt;
        private readonly Func<long, List<VBTCValidator>> _priorActiveAt = VrfSeed.ActiveAt;
        private readonly Dictionary<long, string> _hashes = new();

        public VrfSeedEpochTests()
        {
            Globals.VrfSeedHeight = SeedHeight;
            VrfSeed.BlockHashAt = h => _hashes.TryGetValue(h, out var hash) ? hash : $"block-{h}";
            VrfSeed.ClearCacheForTests();
        }

        public void Dispose()
        {
            Globals.VrfSeedHeight = _priorSeedHeight;
            VrfSeed.BlockHashAt = _priorHashAt;
            VrfSeed.ActiveAt = _priorActiveAt;
            VrfSeed.ClearCacheForTests();
        }

        [Fact]
        public void Epochs_AndAnchors_FollowTheConstants()
        {
            Assert.Equal(0, VrfSeed.Epoch(SeedHeight));
            Assert.Equal(0, VrfSeed.Epoch(SeedHeight + Globals.VrfEpochBlocks - 1));
            Assert.Equal(1, VrfSeed.Epoch(SeedHeight + Globals.VrfEpochBlocks));
            Assert.Equal(SeedHeight + Globals.VrfEpochBlocks, VrfSeed.EpochStart(SeedHeight + Globals.VrfEpochBlocks + 5));
            // The first epoch's anchor is below the height.
            Assert.Equal(SeedHeight - Globals.VrfAnchorLagBlocks, VrfSeed.AnchorHeight(SeedHeight));
            Assert.True(VrfSeed.AnchorHeight(SeedHeight) < SeedHeight);
            Assert.True(Globals.VrfAnchorLagBlocks > ForkChoiceUtility.MAX_REORG_DEPTH);
        }

        [Fact]
        public void TheSeed_IsAFunctionOfTheAnchorAndTheHeight()
        {
            var h = SeedHeight + 42;
            var seed = VrfSeed.SeedFor(h);
            Assert.NotNull(seed);
            Assert.Equal(seed, VrfSeed.SeedFor(h)); // every node with the anchor computes the same seed
            Assert.NotEqual(seed, VrfSeed.SeedFor(h + 1));

            _hashes[VrfSeed.AnchorHeight(h)] = "another-anchor";
            Assert.NotEqual(seed, VrfSeed.SeedFor(h));
        }

        [Fact]
        public void AWithheldOrVariedBlock_DoesNotChangeTheNextSeed()
        {
            var h = SeedHeight + 500;
            var before = VrfSeed.SeedFor(h + 1);
            _hashes[h] = "a-different-block-at-h";
            _hashes[h - 1] = "another-different-parent";
            Assert.Equal(before, VrfSeed.SeedFor(h + 1));
            Assert.Equal(ProofUtility.ComputeVrf(PublicKey, h + 1, "parent-a"), ProofUtility.ComputeVrf(PublicKey, h + 1, "parent-b"));
        }

        [Fact]
        public void BelowTheHeight_TheVrfHashesTheParent_AsBefore()
        {
            var h = SeedHeight - 1;
            Assert.NotEqual(ProofUtility.ComputeVrf(PublicKey, h, "parent-a"), ProofUtility.ComputeVrf(PublicKey, h, "parent-b"));
            var expectedSeed = PublicKey + h + "parent-a";
            var (vrf, hash) = ProofUtility.ComputeVrf(PublicKey, h, "parent-a");
            Assert.Equal(ProofUtility.CalculateSHA256Hash(expectedSeed + vrf), hash);
            Assert.Null(VrfSeed.Ineligibility("anyone", h));
        }

        [Fact]
        public async Task AProofAtTheHeight_DiffersFromTheParentSeededOne_AndVerifies()
        {
            var h = SeedHeight + 3;
            var (_, legacyHash) = (0u, ProofUtility.CalculateSHA256Hash(PublicKey + h + "parent"));
            var (vrf, hash) = ProofUtility.ComputeVrf(PublicKey, h, "parent");
            Assert.NotEqual(ProofUtility.CalculateSHA256Hash(PublicKey + h + "parent" + vrf), hash);
            Assert.NotEqual(legacyHash, hash);
            Assert.True(await ProofUtility.VerifyProofAsync(PublicKey, h, "parent", hash));
            Assert.True(await ProofUtility.VerifyProofAsync(PublicKey, h, "any-other-parent", hash));

            var below = ProofUtility.ComputeVrf(PublicKey, SeedHeight - 1, "parent");
            Assert.True(await ProofUtility.VerifyProofAsync(PublicKey, SeedHeight - 1, "parent", below.ProofHash));
            Assert.False(await ProofUtility.VerifyProofAsync(PublicKey, SeedHeight - 1, "another-parent", below.ProofHash));
        }

        [Fact]
        public async Task WithoutTheAnchorBlock_NoProofIsMadeOrAccepted()
        {
            var h = SeedHeight + 3;
            var hash = ProofUtility.ComputeVrf(PublicKey, h, "parent").ProofHash;
            VrfSeed.BlockHashAt = _ => null;
            Assert.Equal((0u, ""), ProofUtility.ComputeVrf(PublicKey, h, "parent"));
            Assert.False(await ProofUtility.VerifyProofAsync(PublicKey, h, "parent", hash));
            Assert.Equal(VrfSeed.IneligibleReason, VrfSeed.Ineligibility("anyone", h));
        }

        [Fact]
        public void AKeyRegisteredAtOrAfterTheAnchor_ProducesOnlyFromTheNextEpoch()
        {
            var anchor0 = VrfSeed.AnchorHeight(SeedHeight);
            var anchor1 = VrfSeed.AnchorHeight(SeedHeight + Globals.VrfEpochBlocks);
            VrfSeed.ActiveAt = _ => new List<VBTCValidator>
            {
                new() { ValidatorAddress = "old", RegistrationBlockHeight = anchor0 - 1 },
                new() { ValidatorAddress = "atAnchor", RegistrationBlockHeight = anchor0 },
                new() { ValidatorAddress = "after", RegistrationBlockHeight = anchor0 + 50 },
            };

            Assert.Null(VrfSeed.Ineligibility("old", SeedHeight + 1));
            Assert.Equal(VrfSeed.IneligibleReason, VrfSeed.Ineligibility("atAnchor", SeedHeight + 1));
            Assert.Equal(VrfSeed.IneligibleReason, VrfSeed.Ineligibility("after", SeedHeight + 1));
            Assert.Equal(VrfSeed.IneligibleReason, VrfSeed.Ineligibility("unknown", SeedHeight + 1));

            var nextEpoch = SeedHeight + Globals.VrfEpochBlocks;
            Assert.True(anchor1 > anchor0 + 50);
            Assert.Null(VrfSeed.Ineligibility("atAnchor", nextEpoch));
            Assert.Null(VrfSeed.Ineligibility("after", nextEpoch));
        }
    }
}
