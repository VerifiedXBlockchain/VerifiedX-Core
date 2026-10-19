using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>A credential start argument keeps its value exactly as given.</summary>
    public class StartArgsTests
    {
        [Theory]
        [InlineData("apitoken=AbC123==", "apitoken", "AbC123==")]
        [InlineData("encpass=MyPa=ss", "encpass", "MyPa=ss")]
        public void AMixedCaseValue_OrOneContainingEquals_IsKeptWhole(string arg, string key, string expected)
        {
            Assert.True(StartArgs.TryGetValue(arg, key, out var value));
            Assert.Equal(expected, value);
        }

        [Theory]
        [InlineData("apitoken=lowertoken")]
        [InlineData("APITOKEN=lowertoken")]
        [InlineData("--apitoken=lowertoken")]
        public void TheDocumentedForms_StillWork(string arg)
        {
            Assert.True(StartArgs.TryGetValue(arg, "apitoken", out var value));
            Assert.Equal("lowertoken", value);
        }

        [Theory]
        [InlineData("testnet")]
        [InlineData("apitoken")]
        [InlineData("encpass=secret")]
        public void OtherArguments_DoNotMatch(string arg) => Assert.False(StartArgs.TryGetValue(arg, "apitoken", out _));
    }
}
