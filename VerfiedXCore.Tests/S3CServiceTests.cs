using System;
using ReserveBlockCore;
using ReserveBlockCore.Bitcoin.Services;
using Xunit;

namespace VerfiedXCore.Tests
{
    /// <summary>
    /// S3C Phase 1 — config parsing/validation (S3CService.ParseAndValidate). Validates the
    /// minting-node S3C= parsing: min-3, public-only IPs, valid VFX addresses, and the §2.1
    /// fail-safe (invalid config sets S3CConfigInvalid and leaves S3CPool null — no silent
    /// fallback to the public pool). ParseAndValidate does not dedupe, so the success case reuses
    /// one known-valid VFX address across three entries.
    /// </summary>
    public class S3CServiceTests : IDisposable
    {
        // Known-valid canonical VFX address (from AddressValidationTests).
        private const string ValidAddr = "RAjtW2uDSEDW9mPVkKp2K2AAu4uJD9Zrn7";

        public S3CServiceTests()
        {
            Globals.S3CPool = null;
            Globals.S3CConfigInvalid = false;
        }

        public void Dispose()
        {
            Globals.S3CPool = null;
            Globals.S3CConfigInvalid = false;
        }

        [Fact]
        public void ValidThreeEntries_Parses_And_EnablesS3C()
        {
            var raw = $"8.8.8.8:{ValidAddr},8.8.4.4:{ValidAddr},1.1.1.1:{ValidAddr}";
            Assert.True(S3CService.ParseAndValidate(raw));
            Assert.NotNull(Globals.S3CPool);
            Assert.Equal(3, Globals.S3CPool!.Count);
            Assert.True(Globals.UseS3C);
            Assert.False(Globals.S3CConfigInvalid);
            Assert.Equal("8.8.8.8", Globals.S3CPool[0].IPAddress);
            Assert.Equal(ValidAddr, Globals.S3CPool[0].ValidatorAddress);
        }

        [Fact]
        public void FewerThanThree_Fails_AndFlagsInvalid()
        {
            var raw = $"8.8.8.8:{ValidAddr},8.8.4.4:{ValidAddr}";
            Assert.False(S3CService.ParseAndValidate(raw));
            Assert.Null(Globals.S3CPool);
            Assert.True(Globals.S3CConfigInvalid);   // §2.1: refuse mints, no silent fallback
            Assert.False(Globals.UseS3C);
        }

        [Fact]
        public void PrivateIP_Rejected()
        {
            var raw = $"192.168.1.10:{ValidAddr},8.8.4.4:{ValidAddr},1.1.1.1:{ValidAddr}";
            Assert.False(S3CService.ParseAndValidate(raw));
            Assert.Null(Globals.S3CPool);
            Assert.True(Globals.S3CConfigInvalid);
        }

        [Fact]
        public void InvalidAddress_Rejected()
        {
            var raw = "8.8.8.8:RBXnotAValidAddress,8.8.4.4:RBXnotAValidAddress,1.1.1.1:RBXnotAValidAddress";
            Assert.False(S3CService.ParseAndValidate(raw));
            Assert.Null(Globals.S3CPool);
            Assert.True(Globals.S3CConfigInvalid);
        }

        [Fact]
        public void MalformedEntry_NoColon_Rejected()
        {
            var raw = $"8.8.8.8:{ValidAddr},this-has-no-colon,1.1.1.1:{ValidAddr}";
            Assert.False(S3CService.ParseAndValidate(raw));
            Assert.True(Globals.S3CConfigInvalid);
        }

        [Fact]
        public void Empty_Fails()
        {
            Assert.False(S3CService.ParseAndValidate(""));
            Assert.Null(Globals.S3CPool);
        }
    }
}
