using System;
using System.IO;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// The validator and blockcaster hubs replace a session on a peer IP only with a connection that has authenticated:
    /// an unauthenticated socket from the same IP (a shared NAT or host) aborted the established session before its own
    /// signature was checked. Pinned on the handshake source, as ConsensusRequestAuthTests pins the replay key.
    /// </summary>
    public class SameIpEvictionAfterAuthTests
    {
        private static string HubSource(string hub)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "VerifiedXCore", "P2P", hub)))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(dir!.FullName, "VerifiedXCore", "P2P", hub));
        }

        [Theory]
        [InlineData("P2PValidatorServer.cs", "P2PValDict")]
        [InlineData("P2PBlockcasterServer.cs", "P2PCasterDict")]
        public void ASessionOnAnIp_IsReplacedOnlyAfterTheNewConnectionAuthenticated(string hub, string registry)
        {
            var source = HubSource(hub);
            var verify = source.IndexOf("SignatureService.VerifySignature(address, SignedMessage, signature)", StringComparison.Ordinal);
            var balance = source.IndexOf("stateAddress.Balance < ValidatorService.ValidatorRequiredAmount()", StringComparison.Ordinal);
            var register = source.IndexOf($"Globals.{registry}[peerIP] = Context;", StringComparison.Ordinal);

            Assert.True(verify > 0 && balance > 0 && register > 0);
            Assert.True(register > verify && register > balance); // after the signature and the balance checks
            Assert.DoesNotContain($"Globals.{registry}.TryAdd(peerIP, Context)", source);
        }
    }
}
