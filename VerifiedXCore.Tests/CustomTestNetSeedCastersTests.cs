using System;
using System.Linq;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Marigold (custom testnet) has its own seed casters. The live testnet and Marigold share the 'x' prefix, and the
    /// bridge's fallback committee tells networks apart by prefix only, so one list holding both networks' seeds would
    /// make each committee six with a threshold of four, out of reach of either network's three seeds.
    /// </summary>
    [Collection("GlobalCasterState")]
    public class CustomTestNetSeedCastersTests : IDisposable
    {
        private readonly bool _priorTestNet = Globals.IsTestNet;
        private readonly bool _priorCustom = Globals.IsCustomTestNet;

        public void Dispose()
        {
            Globals.IsTestNet = _priorTestNet;
            Globals.IsCustomTestNet = _priorCustom;
        }

        private static string[] SeedAddresses() =>
            SeedNodeService.GetBootstrapSeedPeers().Select(p => p.ValidatorAddress).OrderBy(a => a, StringComparer.Ordinal).ToArray();

        [Theory]
        [InlineData(true, false)]  // live testnet
        [InlineData(true, true)]   // Marigold
        public void EachTestnet_ItsFallbackCommitteeIsItsOwnThreeSeeds_AndTwoVotesCarry(bool testnet, bool custom)
        {
            Globals.IsTestNet = testnet;
            Globals.IsCustomTestNet = custom;

            var committee = BridgeCasterConsensus.SeedCommitteeForNetwork(Globals.IsTestNet);

            Assert.Equal(SeedAddresses(), committee.OrderBy(a => a, StringComparer.Ordinal).ToArray());
            Assert.Equal(2, BridgeCasterConsensus.RequiredVotesFor(committee));
        }

        [Fact]
        public void Marigold_DoesNotTrustTheLiveTestnetSeeds_NorTheOtherWayRound()
        {
            Globals.IsTestNet = true;
            Globals.IsCustomTestNet = false;
            var liveSeeds = SeedAddresses();
            Globals.IsCustomTestNet = true;
            var marigoldSeeds = SeedAddresses();

            Assert.All(liveSeeds, a => Assert.DoesNotContain(a, Globals.BootstrapCasterAddresses));
            Globals.IsCustomTestNet = false;
            Assert.All(marigoldSeeds, a => Assert.DoesNotContain(a, Globals.BootstrapCasterAddresses));
        }
    }
}
