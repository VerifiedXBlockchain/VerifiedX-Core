using VerifiedXCore.Models;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A direct connection proves a validator's key, not that it moved: the handshake names no recipient and replays to
    /// another node within its window, so it does not re-point a trusted validator seen recently at another IP.
    /// </summary>
    [Collection("GlobalCasterState")]
    public class DirectConnectIpHoldTests : System.IDisposable
    {
        public DirectConnectIpHoldTests() => Globals.NetworkValidators.Clear();
        public void Dispose() => Globals.NetworkValidators.Clear();

        private static NetworkValidator Connecting(string ip) => new() { Address = "xValidator", IPAddress = ip, PublicKey = "pk" };

        [Fact]
        public void AReplayedHandshakeFromAnotherIp_DoesNotMoveARecentlySeenValidator()
        {
            Globals.NetworkValidators["xValidator"] = new NetworkValidator { Address = "xValidator", IPAddress = "10.0.0.1", IsFullyTrusted = true, LastSeen = TimeUtil.GetTime() };

            NetworkValidator.UpsertTrustedOnDirectConnect(Connecting("203.0.113.9"));

            Assert.Equal("10.0.0.1", Globals.NetworkValidators["xValidator"].IPAddress);
        }

        [Fact]
        public void AValidatorWhoseOldEntryWentQuiet_IsTakenAtItsNewIp()
        {
            Globals.NetworkValidators["xValidator"] = new NetworkValidator { Address = "xValidator", IPAddress = "10.0.0.1", IsFullyTrusted = true,
                LastSeen = TimeUtil.GetTime() - NetworkValidator.DirectConnectIpHoldSeconds - 1 };

            NetworkValidator.UpsertTrustedOnDirectConnect(Connecting("10.0.0.2"));

            Assert.Equal("10.0.0.2", Globals.NetworkValidators["xValidator"].IPAddress);
        }

        [Fact]
        public void AFirstConnection_IsTakenAtItsIp()
        {
            NetworkValidator.UpsertTrustedOnDirectConnect(Connecting("10.0.0.3"));
            Assert.Equal("10.0.0.3", Globals.NetworkValidators["xValidator"].IPAddress);
        }
    }
}
