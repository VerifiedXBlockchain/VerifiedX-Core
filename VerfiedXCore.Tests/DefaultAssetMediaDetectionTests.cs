using Xunit;
using ReserveBlockCore.Utilities;

namespace VerfiedXCore.Tests
{
    /// <summary>
    /// Coverage for MD5Utility.HasMedia, which decides whether a vBTC v2 ownership transfer
    /// must ship a media file through a beacon.
    ///
    /// Default-asset contracts are stamped at mint with the built-in placeholder
    /// ("defaultvBTC.png::&lt;md5&gt;"), NOT the "NA" sentinel. Treating only "NA" as
    /// no-media left those transfers gated on a beacon connection for a file that
    /// never exists on disk, which stalled real mainnet transfers on 2026-08-02.
    /// </summary>
    public class DefaultAssetMediaDetectionTests
    {
        // The exact value the vBTC mint path stamps for Location == "default".
        private const string DefaultStamp = "defaultvBTC.png::150b90aa9d06f7e4fc5703ca6d7f01db";

        [Fact]
        public void HasMedia_NaSentinel_IsNoMedia()
        {
            Assert.False(MD5Utility.HasMedia("NA"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void HasMedia_EmptyOrNull_IsNoMedia(string? md5List)
        {
            Assert.False(MD5Utility.HasMedia(md5List));
        }

        [Fact]
        public void HasMedia_DefaultPlaceholderStamp_IsNoMedia()
        {
            // The regression: this is what a real default-asset token carries.
            Assert.False(MD5Utility.HasMedia(DefaultStamp));
        }

        [Fact]
        public void HasMedia_DefaultV2Placeholder_IsNoMedia()
        {
            Assert.False(MD5Utility.HasMedia("defaultvBTC_V2.png::abc123"));
        }

        [Fact]
        public void HasMedia_RealAsset_ShipsMedia()
        {
            Assert.True(MD5Utility.HasMedia("my-artwork.png::deadbeef"));
        }

        [Fact]
        public void HasMedia_RealAssetAlongsidePlaceholder_ShipsMedia()
        {
            // MD5ListCreator joins entries with "<>"; a genuine asset anywhere in the
            // list means the transfer still needs a beacon.
            Assert.True(MD5Utility.HasMedia(DefaultStamp + "<>my-artwork.png::deadbeef"));
        }

        [Fact]
        public void HasMedia_MultiplePlaceholdersOnly_IsNoMedia()
        {
            Assert.False(MD5Utility.HasMedia(DefaultStamp + "<>defaultvBTC_V2.png::abc123"));
        }
    }
}
