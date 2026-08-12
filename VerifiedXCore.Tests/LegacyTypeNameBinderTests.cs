using System;
using VerifiedXCore.Data;
using VerifiedXCore.Models.SmartContracts;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// LiteDB records written by pre-rename (ReserveBlockCore) builds carry "_type" metadata like
    /// "ReserveBlockCore.Models.SmartContracts.TokenizationV2Feature, ReserveBlockCore".
    /// LegacyTypeNameBinder must rewrite those to VerifiedXCore names on read while leaving
    /// current names and writes untouched.
    /// </summary>
    public class LegacyTypeNameBinderTests
    {
        private readonly LegacyTypeNameBinder _binder = LegacyTypeNameBinder.Instance;

        [Fact]
        public void ResolvesLegacyTokenizationV2FeatureName()
        {
            var type = _binder.GetType("ReserveBlockCore.Models.SmartContracts.TokenizationV2Feature, ReserveBlockCore");
            Assert.Equal(typeof(TokenizationV2Feature), type);
        }

        [Theory]
        [InlineData("ReserveBlockCore.Models.SmartContracts.RoyaltyFeature, ReserveBlockCore", typeof(RoyaltyFeature))]
        [InlineData("ReserveBlockCore.Models.SmartContracts.TokenizationFeature, ReserveBlockCore", typeof(TokenizationFeature))]
        [InlineData("ReserveBlockCore.Models.SmartContracts.EvolvingFeature, ReserveBlockCore", typeof(EvolvingFeature))]
        [InlineData("ReserveBlockCore.Models.SmartContracts.MultiAssetFeature, ReserveBlockCore", typeof(MultiAssetFeature))]
        public void ResolvesAllLegacyFeatureTypes(string legacyName, Type expected)
        {
            Assert.Equal(expected, _binder.GetType(legacyName));
        }

        [Fact]
        public void ResolvesLegacyGenericListForm()
        {
            var legacy = "System.Collections.Generic.List`1[[ReserveBlockCore.Models.SmartContracts.EvolvingFeature, ReserveBlockCore]]";
            var type = _binder.GetType(legacy);
            Assert.Equal(typeof(System.Collections.Generic.List<EvolvingFeature>), type);
        }

        [Fact]
        public void CurrentNamesStillResolve()
        {
            var type = _binder.GetType("VerifiedXCore.Models.SmartContracts.TokenizationV2Feature, VerifiedXCore");
            Assert.Equal(typeof(TokenizationV2Feature), type);
        }

        [Fact]
        public void UnknownTypeReturnsNull()
        {
            Assert.Null(_binder.GetType("Some.Nonexistent.Type, NoSuchAssembly"));
            Assert.Null(_binder.GetType("ReserveBlockCore.Models.NoSuchType, ReserveBlockCore"));
            Assert.Null(_binder.GetType(""));
            Assert.Null(_binder.GetType(null));
        }

        [Fact]
        public void GetNameEmitsCurrentAssemblyName()
        {
            var name = _binder.GetName(typeof(TokenizationV2Feature));
            Assert.Contains("VerifiedXCore", name);
            Assert.DoesNotContain("ReserveBlockCore", name);
            // Round-trip: the emitted name must resolve back to the same type.
            Assert.Equal(typeof(TokenizationV2Feature), _binder.GetType(name));
        }
    }
}
