using System;
using System.Net;
using System.Net.Sockets;
using VerifiedXCore;
using VerifiedXCore.DST;
using VerifiedXCore.Models.DST;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A shop acks a punch only when the STUN server it registered with signed it (fresh, once), and records a buyer only
    /// after the buyer returned the cookie the shop sent to its source endpoint. A STUN server forwards punches only to a
    /// shop that returned the secret issued to its endpoint.
    /// </summary>
    [Collection("DbContextSequential")]
    public class DstSessionAuthTests : IDisposable
    {
        private const string Stun = "203.0.113.5:3340";
        private const string Buyer = "198.51.100.20:50001";
        private readonly UdpClient _udp = new(0);

        public DstSessionAuthTests()
        {
            DstSessionAuth.StunSecrets.Clear();
            Globals.ConnectedClients.Clear();
            Globals.ConnectedShops.Clear();
        }

        public void Dispose()
        {
            DstSessionAuth.StunSecrets.Clear();
            Globals.ConnectedClients.Clear();
            Globals.ConnectedShops.Clear();
            _udp.Dispose();
        }

        [Fact]
        public void APunchSignedWithTheRegistrationSecret_IsAckedOnce()
        {
            var secret = DstSessionAuth.NewSecret();
            DstSessionAuth.StunSecrets[Stun] = secret;
            var punch = DstSessionAuth.SignedPunch(secret, Buyer);
            var now = TimeUtil.GetTime();
            Assert.True(DstSessionAuth.PunchIsAuthentic(punch, Stun, now));
            Assert.False(DstSessionAuth.PunchIsAuthentic(punch, Stun, now)); // single use
        }

        [Fact]
        public void APunchThatIsUnsignedForgedRedirectedOrStale_IsRefused()
        {
            var secret = DstSessionAuth.NewSecret();
            DstSessionAuth.StunSecrets[Stun] = secret;
            var now = TimeUtil.GetTime();

            var unsigned = new Message { Type = MessageType.PunchClient, Data = Buyer };
            unsigned.Build();
            Assert.False(DstSessionAuth.PunchIsAuthentic(unsigned, Stun, now));

            Assert.False(DstSessionAuth.PunchIsAuthentic(DstSessionAuth.SignedPunch(DstSessionAuth.NewSecret(), Buyer), Stun, now));

            var redirected = DstSessionAuth.SignedPunch(secret, Buyer);
            redirected.Data = "192.0.2.99:53";
            Assert.False(DstSessionAuth.PunchIsAuthentic(redirected, Stun, now));

            Assert.False(DstSessionAuth.PunchIsAuthentic(DstSessionAuth.SignedPunch(secret, Buyer), Stun, now + DstSessionAuth.FreshSeconds + 1));
            Assert.False(DstSessionAuth.PunchIsAuthentic(DstSessionAuth.SignedPunch(secret, Buyer), "203.0.113.6:3340", now)); // no registration there
        }

        [Fact]
        public void ACookie_IsValidOnlyForItsEndpoint_Once_AndWhileFresh()
        {
            var now = TimeUtil.GetTime();
            var cookie = DstSessionAuth.Cookie(Buyer, now);
            Assert.False(DstSessionAuth.CookieIsValid(cookie, "198.51.100.21:50001", now));
            Assert.False(DstSessionAuth.CookieIsValid(cookie, Buyer, now + 3 * DstSessionAuth.FreshSeconds));
            Assert.True(DstSessionAuth.CookieIsValid(cookie, Buyer, now));
            Assert.False(DstSessionAuth.CookieIsValid(cookie, Buyer, now));
            Assert.False(DstSessionAuth.CookieIsValid(null, Buyer, now));
        }

        [Fact]
        public void AHeloWithoutTheCookie_RecordsNoClient_AndWithItTheClientIsRecorded()
        {
            var source = IPEndPoint.Parse("127.0.0.1:50999");
            var helo = new Message { Type = MessageType.ShopConnect, Data = "helo", Address = "NA" };
            helo.Build();
            MessageService.ShopConnect(helo, source, _udp);
            Assert.False(Globals.ConnectedClients.ContainsKey(source.ToString()));

            helo.Auth = DstSessionAuth.Cookie(source.ToString(), TimeUtil.GetTime());
            MessageService.ShopConnect(helo, source, _udp);
            Assert.True(Globals.ConnectedClients.ContainsKey(source.ToString()));
        }

        [Fact]
        public void AShopRegistration_IsConfirmedOnlyByTheSecretIssuedToItsEndpoint()
        {
            var shopEndPoint = IPEndPoint.Parse("127.0.0.1:50998");
            var helo = new Message { Type = MessageType.STUNConnect, Data = "helo" };
            helo.Build();
            MessageService.STUNConnect(helo, shopEndPoint, _udp);
            var shop = Globals.ConnectedShops[shopEndPoint.ToString()];
            Assert.False(string.IsNullOrEmpty(shop.StunSecret));
            Assert.False(shop.StunSecretConfirmed);

            MessageService.ConfirmStunSecret(new Message { Data = DstSessionAuth.NewSecret() }, shopEndPoint);
            Assert.False(shop.StunSecretConfirmed);
            MessageService.ConfirmStunSecret(new Message { Data = shop.StunSecret }, IPEndPoint.Parse("127.0.0.1:50997"));
            Assert.False(shop.StunSecretConfirmed);
            MessageService.ConfirmStunSecret(new Message { Data = shop.StunSecret }, shopEndPoint);
            Assert.True(shop.StunSecretConfirmed);
        }
    }
}
