using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.DST;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models.DST;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A shop answers DecShop requests only from senders that completed the handshake, a Bids reply is bounded, and the
    /// shop does not keep a copy of every reply it sends.
    /// </summary>
    [Collection("DbContextSequential")]
    public class DstDecShopRequestTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;

        public DstDecShopRequestTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"dst_decshop_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            Globals.ConnectedClients.Clear();
            Globals.ClientMessageDict.Clear();
        }

        public void Dispose()
        {
            Globals.ConnectedClients.Clear();
            Globals.ClientMessageDict.Clear();
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static Message Request(string data) => new Message { Id = Guid.NewGuid().ToString(), Type = MessageType.DecShop, ComType = MessageComType.Request, Data = data };

        private static async Task<string?> ReceiveOrNull(UdpClient client)
        {
            var receive = client.ReceiveAsync();
            return await Task.WhenAny(receive, Task.Delay(500)) == receive ? System.Text.Encoding.UTF8.GetString(receive.Result.Buffer) : null;
        }

        [Fact]
        public async Task RequestFromAnUnhandshakedSender_GetsNoReply_AndNothingIsCached()
        {
            using var shop = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var target = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var targetEndPoint = (IPEndPoint)target.Client.LocalEndPoint!;

            MessageService.DecShopMessage(Request("Bids,x"), targetEndPoint, shop);

            Assert.Null(await ReceiveOrNull(target));
            Assert.Empty(Globals.ClientMessageDict);
        }

        [Fact]
        public async Task RequestFromAHandshakedClient_IsAnswered_AndTheReplyIsNotCached()
        {
            using var shop = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var clientEndPoint = (IPEndPoint)client.Client.LocalEndPoint!;
            MessageService.ShopConnect(new Message { Type = MessageType.KeepAlive, Data = "helo", IPAddress = clientEndPoint.ToString() }, clientEndPoint, shop);
            Assert.True(Globals.ConnectedClients.ContainsKey(clientEndPoint.ToString()));
            await ReceiveOrNull(client); // the handshake's echo
            Globals.ClientMessageDict.Clear();

            var request = Request("Bids,x");
            MessageService.DecShopMessage(request, clientEndPoint, shop);

            var reply = await ReceiveOrNull(client);
            Assert.NotNull(reply);
            var replyMessage = JsonConvert.DeserializeObject<Message>(reply!)!;
            Assert.Equal(request.Id, replyMessage.ResponseMessageId);
            Assert.Empty(Globals.ClientMessageDict);
        }

        [Fact]
        public void BidsReply_CarriesOnlyTheNewestBids()
        {
            var now = TimeUtil.GetTime();
            for (var i = 0; i < DecShopMessageService.MaxBidsPerReply + 15; i++)
                Bid.SaveBid(new Bid
                {
                    Id = Guid.NewGuid(), BidAddress = "xBidder", PurchaseKey = "key-5", BidAmount = 100M + i, MaxBidAmount = 100M + i,
                    BidStatus = BidStatus.Received, BidSendReceive = BidSendReceive.Received, BidSendTime = now - 1000 + i, ListingId = 5, CollectionId = 1,
                });

            var reply = DecShopMessageService.ProcessMessage(Request("Bids,5"));

            var bids = JsonConvert.DeserializeObject<System.Collections.Generic.List<Bid>>(reply!.Data)!;
            Assert.Equal(DecShopMessageService.MaxBidsPerReply, bids.Count);
            Assert.Equal(now - 1000 + DecShopMessageService.MaxBidsPerReply + 14, bids.Max(b => b.BidSendTime)); // the newest is kept
        }

        [Fact]
        public void CollectionsReply_CarriesEveryLiveCollection()
        {
            // Clients ask for collections once and take the reply as complete, so the reply is not paged.
            for (var i = 1; i <= 60; i++)
                Collection.GetCollectionDb()!.InsertSafe(new Collection { Id = i, Name = $"c{i}", Description = "", CollectionLive = i <= 57 });

            var reply = JsonConvert.DeserializeObject<System.Collections.Generic.List<Collection>>(DecShopMessageService.ProcessMessage(Request("Collections"))!.Data)!;

            Assert.Equal(57, reply.Count);
        }

        [Fact]
        public void ListingsByCollectionReply_IsOnePage()
        {
            for (var i = 1; i <= 25; i++)
                Listing.GetListingDb()!.InsertSafe(new Listing { Id = i, CollectionId = 3, IsVisibleAfterEndDate = true, IsVisibleBeforeStartDate = true, PurchaseKey = $"k{i}" });

            var page0 = JsonConvert.DeserializeObject<System.Collections.Generic.List<Listing>>(DecShopMessageService.ProcessMessage(Request("ListingsByCollection,3,0"))!.Data)!;
            var page2 = JsonConvert.DeserializeObject<System.Collections.Generic.List<Listing>>(DecShopMessageService.ProcessMessage(Request("ListingsByCollection,3,2"))!.Data)!;

            Assert.Equal(10, page0.Count);
            Assert.Equal(new[] { 21, 22, 23, 24, 25 }, page2.Select(l => l.Id).ToArray());
        }

        [Fact]
        public void SentRequests_AreStillKeptForTheirResponses_AndTheTableIsBounded()
        {
            var request = new Message { Type = MessageType.DecShop, ComType = MessageComType.Request, Data = "Info" };
            MessageService.GenerateMessage(request, true);
            Assert.True(Globals.ClientMessageDict.ContainsKey(request.Id)); // a client can still match the shop's answer

            var now = TimeUtil.GetTime();
            for (var i = 0; i < MessageService.MaxClientMessages; i++)
                Globals.ClientMessageDict[$"old-{i}"] = new MessageState { MessageId = $"old-{i}", MessageSentTimestamp = now - MessageService.ClientMessageTtlSeconds - 1 };
            MessageService.GenerateMessage(new Message { Type = MessageType.DecShop, ComType = MessageComType.Request, Data = "Info" }, true);

            Assert.True(Globals.ClientMessageDict.Count < MessageService.MaxClientMessages);
            Assert.True(Globals.ClientMessageDict.ContainsKey(request.Id)); // recent entries survive the prune
        }
    }
}
