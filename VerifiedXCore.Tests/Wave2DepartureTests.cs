using VerifiedXCore.Services;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Wave 2 (/depart, /safe-update): pure decision helpers of DepartureService.
    /// </summary>
    public class Wave2DepartureTests
    {
        private const string Self = "xSelfValidatorAddress";
        private const string Other = "xOtherCasterAddress";

        // ── IsSelfAbsentFromMajority ─────────────────────────────────────

        [Fact]
        public void AbsentFromMajority_NoResponses_False()
        {
            Assert.False(DepartureService.IsSelfAbsentFromMajority(new List<List<string>>(), Self));
            Assert.False(DepartureService.IsSelfAbsentFromMajority(null!, Self));
        }

        [Fact]
        public void AbsentFromMajority_AllStillListSelf_False()
        {
            var lists = new List<List<string>>
            {
                new() { Self, Other },
                new() { Self },
                new() { Other, Self },
            };
            Assert.False(DepartureService.IsSelfAbsentFromMajority(lists, Self));
        }

        [Fact]
        public void AbsentFromMajority_ExactlyHalf_False()
        {
            // 1 of 2 absent = not a strict majority
            var lists = new List<List<string>>
            {
                new() { Other },
                new() { Self, Other },
            };
            Assert.False(DepartureService.IsSelfAbsentFromMajority(lists, Self));
        }

        [Fact]
        public void AbsentFromMajority_StrictMajorityAbsent_True()
        {
            var lists = new List<List<string>>
            {
                new() { Other },
                new() { Other },
                new() { Self, Other },
            };
            Assert.True(DepartureService.IsSelfAbsentFromMajority(lists, Self));
        }

        [Fact]
        public void AbsentFromMajority_SingleResponderWithoutSelf_True()
        {
            var lists = new List<List<string>> { new() { Other } };
            Assert.True(DepartureService.IsSelfAbsentFromMajority(lists, Self));
        }

        // ── SelectAssetByKeywords ────────────────────────────────────────

        [Fact]
        public void SelectAsset_SingleOsMatch_Selected()
        {
            var assets = new List<string> { "vfx-win-x64.zip", "vfx-linux-x64.zip", "vfx-osx-arm64.zip" };
            Assert.Equal("vfx-win-x64.zip", DepartureService.SelectAssetByKeywords(assets, new[] { "win" }));
            Assert.Equal("vfx-linux-x64.zip", DepartureService.SelectAssetByKeywords(assets, new[] { "linux" }));
            Assert.Equal("vfx-osx-arm64.zip", DepartureService.SelectAssetByKeywords(assets, new[] { "osx", "mac" }));
        }

        [Fact]
        public void SelectAsset_CaseInsensitive()
        {
            var assets = new List<string> { "VFX-WIN-x64.zip", "vfx-linux.zip" };
            Assert.Equal("VFX-WIN-x64.zip", DepartureService.SelectAssetByKeywords(assets, new[] { "win" }));
        }

        [Fact]
        public void SelectAsset_AmbiguousOrMissing_Null()
        {
            // Two windows assets → ambiguous → null (operator falls back to /update)
            var ambiguous = new List<string> { "vfx-win-x64.zip", "vfx-win-arm64.zip" };
            Assert.Null(DepartureService.SelectAssetByKeywords(ambiguous, new[] { "win" }));

            // No match → null
            var none = new List<string> { "vfx-linux-x64.zip" };
            Assert.Null(DepartureService.SelectAssetByKeywords(none, new[] { "win" }));

            // Empty / null input → null
            Assert.Null(DepartureService.SelectAssetByKeywords(new List<string>(), new[] { "win" }));
            Assert.Null(DepartureService.SelectAssetByKeywords(null!, new[] { "win" }));
        }
    }
}
