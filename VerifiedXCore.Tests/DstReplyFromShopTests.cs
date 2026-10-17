using System.Linq;
using System.Net;
using VerifiedXCore.DST;
using VerifiedXCore.Models.DST;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A DST datagram is handled as a shop's reply only when it comes from that shop's endpoint, and a request id, which
    /// alone matches a reply to its request, cannot be guessed: any reachable sender could otherwise write into a shop's
    /// cached listings, collections and bids in the wallet.
    /// </summary>
    public class DstReplyFromShopTests
    {
        [Fact]
        public void OnlyTheConnectedShopsEndpoint_IsTheShop()
        {
            var shop = new IPEndPoint(IPAddress.Parse("198.51.100.7"), 13341);

            Assert.True(DSTMultiClient.IsFromShop(new IPEndPoint(IPAddress.Parse("198.51.100.7"), 13341), shop));
            Assert.True(DSTMultiClient.IsFromShop(new IPEndPoint(IPAddress.Parse("::ffff:198.51.100.7"), 13341), shop)); // the same address, IPv4-mapped
            Assert.False(DSTMultiClient.IsFromShop(new IPEndPoint(IPAddress.Parse("203.0.113.9"), 13341), shop));
            Assert.False(DSTMultiClient.IsFromShop(new IPEndPoint(IPAddress.Parse("198.51.100.7"), 13342), shop));
            Assert.False(DSTMultiClient.IsFromShop(null, shop));
        }

        [Fact]
        public void ARequestId_Is128RandomBits()
        {
            var ids = Enumerable.Range(0, 200).Select(_ => { var m = new Message { Data = "Info" }; m.Build(); return m.Id; }).ToList();

            Assert.All(ids, id => Assert.Equal(32, id.Length));
            Assert.Equal(ids.Count, ids.Distinct().Count());
        }
    }
}
