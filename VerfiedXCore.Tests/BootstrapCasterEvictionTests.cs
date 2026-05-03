using ReserveBlockCore;
using ReserveBlockCore.Models;
using ReserveBlockCore.Services;
using System.Collections.Concurrent;
using Xunit;

namespace VerfiedXCore.Tests
{
    /// <summary>
    /// Tests for the "Verify Before You Cast" eviction awareness system.
    /// Covers the scenario where a bootstrap caster is briefly disconnected, gets replaced by other casters,
    /// and then comes back online — it should NOT blindly re-add itself as a caster.
    /// </summary>
    [Collection("GlobalCasterState")]
    public class BootstrapCasterEvictionTests
    {
        /// <summary>
        /// When the caster pool is already at MaxCasters (5/5), the atomic add helper
        /// should reject any additional caster — preventing the 6/5 overflow.
        /// </summary>
        [Fact]
        public void AddBlockCasterIfRoomAndUnique_RejectsWhenPoolFull()
        {
            // Arrange: fill the pool to MaxCasters
            var originalBag = Globals.BlockCasters;
            try
            {
                Globals.BlockCasters = new ConcurrentBag<Peers>();
                for (int i = 0; i < CasterDiscoveryService.MaxCasters; i++)
                {
                    Globals.BlockCasters.Add(new Peers
                    {
                        ValidatorAddress = $"TestAddr{i}",
                        PeerIP = $"10.0.0.{i}",
                        ValidatorPublicKey = $"pk{i}",
                    });
                }

                Assert.Equal(CasterDiscoveryService.MaxCasters, Globals.BlockCasters.Count);

                // Act: try to add one more
                var overflow = new Peers
                {
                    ValidatorAddress = "OverflowAddr",
                    PeerIP = "10.0.0.99",
                    ValidatorPublicKey = "pkOverflow",
                };
                var result = CasterDiscoveryService.AddBlockCasterIfRoomAndUnique(overflow);

                // Assert: should be rejected
                Assert.False(result);
                Assert.Equal(CasterDiscoveryService.MaxCasters, Globals.BlockCasters.Count);
                Assert.DoesNotContain(Globals.BlockCasters, c => c.ValidatorAddress == "OverflowAddr");
            }
            finally
            {
                Globals.BlockCasters = originalBag;
            }
        }

        /// <summary>
        /// Duplicate addresses should be rejected by the atomic add helper.
        /// </summary>
        [Fact]
        public void AddBlockCasterIfRoomAndUnique_RejectsDuplicate()
        {
            var originalBag = Globals.BlockCasters;
            try
            {
                Globals.BlockCasters = new ConcurrentBag<Peers>();
                Globals.BlockCasters.Add(new Peers
                {
                    ValidatorAddress = "ExistingAddr",
                    PeerIP = "10.0.0.1",
                    ValidatorPublicKey = "pk1",
                });

                // Act: try to add same address again
                var duplicate = new Peers
                {
                    ValidatorAddress = "ExistingAddr",
                    PeerIP = "10.0.0.2", // different IP, same address
                    ValidatorPublicKey = "pk1",
                };
                var result = CasterDiscoveryService.AddBlockCasterIfRoomAndUnique(duplicate);

                // Assert
                Assert.False(result);
                Assert.Single(Globals.BlockCasters);
            }
            finally
            {
                Globals.BlockCasters = originalBag;
            }
        }

        /// <summary>
        /// When the pool has room and the address is new, the add should succeed.
        /// </summary>
        [Fact]
        public void AddBlockCasterIfRoomAndUnique_AcceptsWhenRoomAndUnique()
        {
            var originalBag = Globals.BlockCasters;
            var originalValidators = new ConcurrentDictionary<string, NetworkValidator>(Globals.NetworkValidators);
            try
            {
                Globals.BlockCasters = new ConcurrentBag<Peers>();
                Globals.BlockCasters.Add(new Peers
                {
                    ValidatorAddress = "CasterA",
                    PeerIP = "10.0.0.1",
                    ValidatorPublicKey = "pkA",
                });

                var newCaster = new Peers
                {
                    ValidatorAddress = "CasterB",
                    PeerIP = "10.0.0.2",
                    ValidatorPublicKey = "pkB",
                };
                var result = CasterDiscoveryService.AddBlockCasterIfRoomAndUnique(newCaster);

                Assert.True(result);
                Assert.Equal(2, Globals.BlockCasters.Count);
                Assert.Contains(Globals.BlockCasters, c => c.ValidatorAddress == "CasterB");
            }
            finally
            {
                Globals.BlockCasters = originalBag;
                Globals.NetworkValidators = originalValidators;
            }
        }

        /// <summary>
        /// PerformEvictionAwarenessCheckAsync should return false (not evicted) when there
        /// are no peer casters to check against (solo caster / empty peer list).
        /// This is the "cold start exception" — a solo bootstrap caster can't be evicted.
        /// </summary>
        [Fact]
        public async Task PerformEvictionCheck_ReturnsFalse_WhenNoPeersToCheck()
        {
            var originalBag = Globals.BlockCasters;
            var originalAddress = Globals.ValidatorAddress;
            var originalIsCaster = Globals.IsBlockCaster;
            try
            {
                Globals.ValidatorAddress = "SoloBootstrapCaster";
                Globals.IsBlockCaster = true;
                Globals.BlockCasters = new ConcurrentBag<Peers>();
                // Only self in the list — no peers to check against
                Globals.BlockCasters.Add(new Peers
                {
                    ValidatorAddress = "SoloBootstrapCaster",
                    PeerIP = "10.0.0.1",
                });

                var wasEvicted = await CasterDiscoveryService.PerformEvictionAwarenessCheckAsync();

                Assert.False(wasEvicted);
                Assert.True(Globals.IsBlockCaster); // should remain a caster
            }
            finally
            {
                Globals.BlockCasters = originalBag;
                Globals.ValidatorAddress = originalAddress;
                Globals.IsBlockCaster = originalIsCaster;
            }
        }

        /// <summary>
        /// PerformEvictionAwarenessCheckAsync should return false when IsBlockCaster is false.
        /// </summary>
        [Fact]
        public async Task PerformEvictionCheck_ReturnsFalse_WhenNotACaster()
        {
            var originalIsCaster = Globals.IsBlockCaster;
            try
            {
                Globals.IsBlockCaster = false;

                var wasEvicted = await CasterDiscoveryService.PerformEvictionAwarenessCheckAsync();

                Assert.False(wasEvicted);
            }
            finally
            {
                Globals.IsBlockCaster = originalIsCaster;
            }
        }

        /// <summary>
        /// VerifySelfInRemoteCasterListsAsync with empty peer IPs should return
        /// (false, null, 0) — no peers to query.
        /// </summary>
        [Fact]
        public async Task VerifySelf_ReturnsNoConfirmation_WhenNoPeerIPs()
        {
            var originalAddress = Globals.ValidatorAddress;
            try
            {
                Globals.ValidatorAddress = "TestAddress";
                var (selfConfirmed, liveCasterList, peersReached) =
                    await CasterDiscoveryService.VerifySelfInRemoteCasterListsAsync(new List<string>());

                Assert.False(selfConfirmed);
                Assert.Null(liveCasterList);
                Assert.Equal(0, peersReached);
            }
            finally
            {
                Globals.ValidatorAddress = originalAddress;
            }
        }

        /// <summary>
        /// VerifySelfInRemoteCasterListsAsync should return (false, null, 0) when
        /// ValidatorAddress is empty.
        /// </summary>
        [Fact]
        public async Task VerifySelf_ReturnsNoConfirmation_WhenNoValidatorAddress()
        {
            var originalAddress = Globals.ValidatorAddress;
            try
            {
                Globals.ValidatorAddress = "";
                var (selfConfirmed, liveCasterList, peersReached) =
                    await CasterDiscoveryService.VerifySelfInRemoteCasterListsAsync(new List<string> { "10.0.0.1" });

                Assert.False(selfConfirmed);
                Assert.Null(liveCasterList);
                Assert.Equal(0, peersReached);
            }
            finally
            {
                Globals.ValidatorAddress = originalAddress;
            }
        }

        /// <summary>
        /// Simulates the exact scenario: a bootstrap caster pool is full (5/5),
        /// and a returning caster tries to inject itself via hardcoded list.
        /// The MaxCasters cap should prevent the 6/5 overflow.
        /// </summary>
        [Fact]
        public void BootstrapReconnect_CannotExceedMaxCasters()
        {
            var originalBag = Globals.BlockCasters;
            var originalValidators = new ConcurrentDictionary<string, NetworkValidator>(Globals.NetworkValidators);
            try
            {
                // Set up: 5 active casters (the replacements)
                Globals.BlockCasters = new ConcurrentBag<Peers>();
                var activeAddresses = new[] { "CasterA", "CasterB", "CasterC", "CasterD", "NewReplacement" };
                for (int i = 0; i < activeAddresses.Length; i++)
                {
                    Globals.BlockCasters.Add(new Peers
                    {
                        ValidatorAddress = activeAddresses[i],
                        PeerIP = $"10.0.0.{i + 1}",
                        ValidatorPublicKey = $"pk{i}",
                    });
                }

                Assert.Equal(5, Globals.BlockCasters.Count);

                // The evicted bootstrap caster tries to re-add itself
                var evictedCaster = new Peers
                {
                    ValidatorAddress = "EvictedBootstrap",
                    PeerIP = "40.160.225.225",
                    ValidatorPublicKey = "pk_evicted",
                };

                var result = CasterDiscoveryService.AddBlockCasterIfRoomAndUnique(evictedCaster);

                // Should be rejected — pool is full
                Assert.False(result);
                Assert.Equal(5, Globals.BlockCasters.Count);
                Assert.DoesNotContain(Globals.BlockCasters, c => c.ValidatorAddress == "EvictedBootstrap");
            }
            finally
            {
                Globals.BlockCasters = originalBag;
                Globals.NetworkValidators = originalValidators;
            }
        }

        /// <summary>
        /// FetchLiveCasterListFromPeersAsync should return null when no peers respond
        /// (simulating a true cold start where all bootstrap peers are down).
        /// </summary>
        [Fact]
        public async Task FetchLiveCasterList_ReturnsNull_WhenNoPeersRespond()
        {
            // Use unreachable IPs to simulate no peers responding
            var result = await CasterDiscoveryService.FetchLiveCasterListFromPeersAsync(
                new List<string> { "192.168.255.254", "192.168.255.253" });

            Assert.Null(result);
        }

        /// <summary>
        /// ShouldInjectHardcodedBootstrapPeers should return true when BlockCasters is empty
        /// (cold start scenario).
        /// </summary>
        [Fact]
        public void ShouldInjectBootstrapPeers_ReturnsTrue_WhenBlockCastersEmpty()
        {
            var originalBag = Globals.BlockCasters;
            var originalSynced = Globals.IsChainSynced;
            try
            {
                Globals.BlockCasters = new ConcurrentBag<Peers>();
                Globals.IsChainSynced = true;

                var result = SeedNodeService.ShouldInjectHardcodedBootstrapPeers();

                Assert.True(result);
            }
            finally
            {
                Globals.BlockCasters = originalBag;
                Globals.IsChainSynced = originalSynced;
            }
        }

        /// <summary>
        /// ShouldInjectHardcodedBootstrapPeers should return false when chain is synced
        /// and BlockCasters already has active entries.
        /// </summary>
        [Fact]
        public void ShouldInjectBootstrapPeers_ReturnsFalse_WhenChainSyncedAndCastersExist()
        {
            var originalBag = Globals.BlockCasters;
            var originalSynced = Globals.IsChainSynced;
            var originalBlock = Globals.LastBlock;
            try
            {
                Globals.IsChainSynced = true;
                Globals.LastBlock = new Block { Height = 100, Timestamp = ReserveBlockCore.Utilities.TimeUtil.GetTime() };
                Globals.BlockCasters = new ConcurrentBag<Peers>();
                Globals.BlockCasters.Add(new Peers
                {
                    ValidatorAddress = "ActiveCaster1",
                    PeerIP = "10.0.0.1",
                });

                var result = SeedNodeService.ShouldInjectHardcodedBootstrapPeers();

                Assert.False(result);
            }
            finally
            {
                Globals.BlockCasters = originalBag;
                Globals.IsChainSynced = originalSynced;
                Globals.LastBlock = originalBlock;
            }
        }
    }
}
