using System.Net;
using VerifiedXCore.Bitcoin.FROST;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Follow-up hardening: the leader's start signature is broadcast to every signer, so any peer
    /// (or plaintext observer) could replay it to round-2 (consuming a victim's single-shot nonce over
    /// a bogus commitment set) or to abort. Round/abort messages must now come from the endpoint that
    /// started the session.
    /// </summary>
    public class FrostLeaderEndpointBindingTests
    {
        [Fact]
        public void SameEndpoint_Accepted_DifferentEndpoint_Refused()
        {
            Assert.True(FrostSigningAuthorization.SameLeaderEndpoint("10.0.0.5", "10.0.0.5"));
            Assert.False(FrostSigningAuthorization.SameLeaderEndpoint("10.0.0.5", "10.0.0.6"));
            Assert.False(FrostSigningAuthorization.SameLeaderEndpoint("10.0.0.5", ""));
            Assert.False(FrostSigningAuthorization.SameLeaderEndpoint("", "10.0.0.5"));
        }

        [Fact]
        public void UnknownBothSides_CompareEqual()
        {
            // In-process test host has no remote address on either request.
            Assert.True(FrostSigningAuthorization.SameLeaderEndpoint("", ""));
            Assert.True(FrostSigningAuthorization.SameLeaderEndpoint(null, null));
        }

        [Fact]
        public void Normalize_CollapsesIPv4MappedIPv6()
        {
            var mapped = IPAddress.Parse("::ffff:192.168.1.10");
            Assert.Equal("192.168.1.10", FrostSigningAuthorization.NormalizeRemoteIp(mapped));
            Assert.Equal("192.168.1.10", FrostSigningAuthorization.NormalizeRemoteIp(IPAddress.Parse("192.168.1.10")));
            Assert.Equal("", FrostSigningAuthorization.NormalizeRemoteIp(null));
            Assert.True(FrostSigningAuthorization.SameLeaderEndpoint(
                FrostSigningAuthorization.NormalizeRemoteIp(mapped),
                FrostSigningAuthorization.NormalizeRemoteIp(IPAddress.Parse("192.168.1.10"))));
        }
    }
}
