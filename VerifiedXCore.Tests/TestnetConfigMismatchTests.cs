using VerifiedXCore;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>A config that selects testnet is refused for a node started for mainnet (its heights are already mainnet's).</summary>
    [Collection("GlobalCasterState")]
    public class TestnetConfigMismatchTests
    {
        [Fact]
        public void TestnetInConfig_ForAMainnetStart_IsRefused()
        {
            var prior = Globals.IsTestNet;
            try
            {
                Globals.IsTestNet = false;
                Assert.NotNull(Config.Config.TestnetConfigMismatch(new Config.Config { TestNet = true }));
                Assert.NotNull(Config.Config.TestnetConfigMismatch(new Config.Config { TestNetName = "marigold" }));
                Assert.Null(Config.Config.TestnetConfigMismatch(new Config.Config { TestNet = false })); // mainnet as before
            }
            finally { Globals.IsTestNet = prior; }
        }

        [Fact]
        public void TestnetInConfig_ForATestnetStart_IsApplied()
        {
            var prior = Globals.IsTestNet;
            try
            {
                Globals.IsTestNet = true; // started with the testnet argument
                Assert.Null(Config.Config.TestnetConfigMismatch(new Config.Config { TestNet = true }));
            }
            finally { Globals.IsTestNet = prior; }
        }
    }
}
