using System.Linq;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>The warden starts its child node with each argument intact and logs no credential.</summary>
    public class WardenChildArgsTests
    {
        [Fact]
        public void EachArgument_ReachesTheChildWhole()
        {
            var info = WardenService.ChildStartInfo("dotnet", new[] { "VerifiedXCore.dll", "apitoken=two words", "encpass=p a=ss", "testnet" }, "/tmp");

            Assert.Equal(new[] { "VerifiedXCore.dll", "apitoken=two words", "encpass=p a=ss", "testnet" }, info.ArgumentList.ToArray());
            Assert.Equal("", info.Arguments);
        }

        [Fact]
        public void TheLoggedArguments_HideTheCredentials()
        {
            var logged = WardenService.RedactCredentials(new[] { "apitoken=secret", "--encpass=pw", "testnet" }).ToArray();

            Assert.Equal(new[] { "apitoken=***", "encpass=***", "testnet" }, logged);
        }
    }
}
