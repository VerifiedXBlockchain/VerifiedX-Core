using System;
using VerifiedXCore.Bitcoin.FROST;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// The FROST startup check passes only for the library version this build pins: a library that loads and runs every
    /// function but reports another version fails the check, so <see cref="FrostLibraryGate"/> keeps the node out of
    /// ceremonies and signings. The library that ships with the build passes.
    /// </summary>
    [Collection("DbContextSequential")]
    public class FrostLibraryVersionPinTests
    {
        private static FrostFFICheckUtility.FrostFFICheckResult CheckReporting(string version)
        {
            var prior = FrostFFICheckUtility.Version;
            try
            {
                FrostFFICheckUtility.Version = () => version;
                return FrostFFICheckUtility.RunFullCheck();
            }
            finally
            {
                FrostFFICheckUtility.Version = prior;
            }
        }

        [Fact]
        public void TheShippedLibrary_IsThePinnedVersion_AndPassesTheCheck()
        {
            Assert.Equal(FrostFFICheckUtility.PinnedLibraryVersion, FrostNative.GetVersion());
            Assert.Null(FrostFFICheckUtility.VersionMismatch(FrostFFICheckUtility.PinnedLibraryVersion));

            var result = FrostFFICheckUtility.RunFullCheck();
            Assert.True(result.VersionCheckPassed);
            Assert.True(result.AllChecksPassed, result.Summary);
            Assert.Equal(FrostFFICheckUtility.PinnedLibraryVersion, result.LibraryVersion);
        }

        [Theory]
        [InlineData("0.3.0-frost-real")]
        [InlineData("0.1.0-frost-real")]
        [InlineData("0.2.0-frost-stub")]
        [InlineData("0.2.0-FROST-REAL")]
        [InlineData("0.2.0-frost-real ")]
        [InlineData("Unknown")]
        [InlineData("")]
        public void ALoadableLibraryOfAnotherVersion_FailsTheCheck(string version)
        {
            Assert.NotNull(FrostFFICheckUtility.VersionMismatch(version));

            var result = CheckReporting(version);
            Assert.True(result.LibraryFilePresent);
            Assert.True(result.DKGRound3Passed);        // every function still works
            Assert.True(result.SignAggregatePassed);
            Assert.False(result.VersionCheckPassed);
            Assert.False(result.AllChecksPassed);       // so the gate stays closed
            Assert.Contains(result.Errors, e => e.Contains(FrostFFICheckUtility.PinnedLibraryVersion, StringComparison.Ordinal));
        }

        [Fact]
        public void ANullVersion_FailsTheCheck()
        {
            Assert.NotNull(FrostFFICheckUtility.VersionMismatch(null));
        }
    }
}
