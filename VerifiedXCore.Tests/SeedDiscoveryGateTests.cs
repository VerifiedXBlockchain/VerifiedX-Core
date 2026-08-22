using VerifiedXCore;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using System.Collections.Concurrent;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// SEED-DISCOVERY FIX: a seed restarting into a live network is blocked from hardcoded
    /// self-injection by the Phase E agreement gate, but must still be allowed to DISCOVER the
    /// live caster list as an observer (otherwise its BlockCasters stays empty forever and it
    /// serves signed empty caster lists that poison other nodes' discovery). These tests pin the
    /// gate pairing GetBlockcasters relies on, plus the operator membership-record reset.
    /// </summary>
    [Collection("GlobalCasterState")]
    public class SeedDiscoveryGateTests
    {
        [Fact]
        public void ShouldDiscoverLiveCasters_TracksCasterBagContents()
        {
            var originalBag = Globals.BlockCasters;
            try
            {
                Globals.BlockCasters = new ConcurrentBag<Peers>();
                Assert.True(SeedNodeService.ShouldDiscoverLiveCasters());

                // Entries without a validator address don't count as a usable caster list
                Globals.BlockCasters.Add(new Peers { PeerIP = "10.0.0.1" });
                Assert.True(SeedNodeService.ShouldDiscoverLiveCasters());

                Globals.BlockCasters.Add(new Peers { ValidatorAddress = "LiveCasterA", PeerIP = "10.0.0.2" });
                Assert.False(SeedNodeService.ShouldDiscoverLiveCasters());
            }
            finally
            {
                Globals.BlockCasters = originalBag;
            }
        }

        /// <summary>
        /// The restarted-seed-on-live-network state: hardcoded injection stays blocked (Phase E —
        /// no bootstrap agreement), but observer discovery is allowed because the bag is empty.
        /// This is the combination that previously left a restarted seed with an empty caster
        /// list forever.
        /// </summary>
        [Fact]
        public void RestartedSeed_OnLiveNetwork_MayDiscoverButNotSelfInject()
        {
            var originalBag = Globals.BlockCasters;
            var originalAddress = Globals.ValidatorAddress;
            var originalSynced = Globals.IsChainSynced;
            try
            {
                Globals.ValidatorAddress = Globals.BootstrapCasterAddresses.First();
                Globals.IsChainSynced = true;
                Globals.BlockCasters = new ConcurrentBag<Peers>();

                Assert.False(SeedNodeService.ShouldInjectHardcodedBootstrapPeers()); // agreement gate holds
                Assert.True(SeedNodeService.ShouldDiscoverLiveCasters());           // observer discovery allowed
            }
            finally
            {
                Globals.BlockCasters = originalBag;
                Globals.ValidatorAddress = originalAddress;
                Globals.IsChainSynced = originalSynced;
            }
        }

        /// <summary>
        /// Steady state after discovery succeeds: bag populated (self not in it) → both gates
        /// closed, GetBlockcasters converges to the cheap sync-and-return path.
        /// </summary>
        [Fact]
        public void RestartedSeed_AfterDiscovery_BothGatesClosed()
        {
            var originalBag = Globals.BlockCasters;
            var originalAddress = Globals.ValidatorAddress;
            var originalSynced = Globals.IsChainSynced;
            try
            {
                Globals.ValidatorAddress = Globals.BootstrapCasterAddresses.First();
                Globals.IsChainSynced = true;
                Globals.BlockCasters = new ConcurrentBag<Peers>
                {
                    new Peers { ValidatorAddress = "LiveCasterA", PeerIP = "10.0.0.1" },
                    new Peers { ValidatorAddress = "LiveCasterB", PeerIP = "10.0.0.2" },
                };

                Assert.False(SeedNodeService.ShouldInjectHardcodedBootstrapPeers());
                Assert.False(SeedNodeService.ShouldDiscoverLiveCasters());
            }
            finally
            {
                Globals.BlockCasters = originalBag;
                Globals.ValidatorAddress = originalAddress;
                Globals.IsChainSynced = originalSynced;
            }
        }

        /// <summary>
        /// Non-seed behavior is unchanged: an empty bag already opens the hardcoded-injection
        /// gate, so the discovery gate adds nothing new for regular validators.
        /// </summary>
        [Fact]
        public void NonSeed_EmptyBag_InjectionGateAlreadyOpen()
        {
            var originalBag = Globals.BlockCasters;
            var originalAddress = Globals.ValidatorAddress;
            var originalSynced = Globals.IsChainSynced;
            try
            {
                Globals.ValidatorAddress = "RegularValidatorAddress";
                Globals.IsChainSynced = true;
                Globals.BlockCasters = new ConcurrentBag<Peers>();

                Assert.True(SeedNodeService.ShouldInjectHardcodedBootstrapPeers());
                Assert.True(SeedNodeService.ShouldDiscoverLiveCasters());
            }
            finally
            {
                Globals.BlockCasters = originalBag;
                Globals.ValidatorAddress = originalAddress;
                Globals.IsChainSynced = originalSynced;
            }
        }

        /// <summary>
        /// Operator reset returns the node to the legacy era: record chain gone, cached head
        /// cleared, cert enforcement disarmed back to MaxValue.
        /// </summary>
        [Fact]
        public void ClearAllForOperatorReset_DisarmsCertEnforcementAndRecordEra()
        {
            var originalCertHeight = Globals.CertEnforceHeight;
            try
            {
                Globals.CertEnforceHeight = 712940;

                CasterMembershipStore.ClearAllForOperatorReset();

                Assert.Equal(long.MaxValue, Globals.CertEnforceHeight);
                Assert.False(CasterMembershipStore.RecordEraActive);
                Assert.Null(CasterMembershipStore.GetCurrent());
            }
            finally
            {
                Globals.CertEnforceHeight = originalCertHeight;
            }
        }
    }
}
