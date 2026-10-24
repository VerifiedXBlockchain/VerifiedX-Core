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
            MessageService.ShopConnect(new Message { Type = MessageType.KeepAlive, Data = "helo", IPAddress = clientEndPoint.ToString(), Auth = DstSessionAuth.Cookie(clientEndPoint.ToString(), TimeUtil.GetTime()) }, clientEndPoint, shop);
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
        public void CollectionsReply_CarriesEveryLiveCollection_WhenTheyFitOneDatagram()
        {
            // Clients ask for collections once and take the reply as complete.
            for (var i = 1; i <= 60; i++)
                Collection.GetCollectionDb()!.InsertSafe(new Collection { Id = i, Name = $"c{i}", Description = "", CollectionLive = i <= 57 });

            var reply = JsonConvert.DeserializeObject<System.Collections.Generic.List<Collection>>(DecShopMessageService.ProcessMessage(Request("Collections"))!.Data)!;

            Assert.Equal(57, reply.Count);
        }

        [Fact]
        public void CollectionsReply_StaysWithinOneDatagram_AndKeepsTheFirstCollections()
        {
            var description = new string('"', 2_000); // escaped as \" in the message: the budget counts it as sent
            for (var i = 1; i <= 40; i++)
                Collection.GetCollectionDb()!.InsertSafe(new Collection { Id = i, Name = $"c{i}", Description = description, CollectionLive = true });

            var replyMessage = DecShopMessageService.ProcessMessage(Request("Collections"))!;
            var reply = JsonConvert.DeserializeObject<System.Collections.Generic.List<Collection>>(replyMessage.Data)!;

            Assert.InRange(reply.Count, 1, 39);
            Assert.Equal(Enumerable.Range(1, reply.Count).ToArray(), reply.Select(c => c.Id).ToArray());
            Assert.True(DecShopMessageService.ReplyDataBytes(replyMessage.Data) <= DecShopMessageService.MaxReplyDataBytes);
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(MessageService.GenerateMessage(replyMessage, false)) <= 65_507);
        }

        [Fact]
        public void UpdateReply_TooLargeForOneDatagram_IsNotSentCutShort()
        {
            Collection.GetCollectionDb()!.InsertBulk(Enumerable.Range(1, 12_000).Select(i => new Collection { Id = i, Name = $"c{i}", Description = "", CollectionLive = true }));
            Assert.Null(DecShopMessageService.ProcessMessage(Request("Update")));

            Collection.GetCollectionDb()!.DeleteMany(c => c.Id > 100);
            var update = JsonConvert.DeserializeObject<DecShopUpdateData>(DecShopMessageService.ProcessMessage(Request("Update"))!.Data)!;
            Assert.Equal(100, update.CollectionList!.Count);
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

        [Fact]
        public async Task PunchHandlers_IgnoreSendersTheyDoNotKnow_AndServeThoseTheyDo()
        {
            var priorStun = Globals.STUNServer;
            try
            {
                using var node = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                using var stranger = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                var strangerEp = (IPEndPoint)stranger.Client.LocalEndPoint!;
                // A fresh listener per check: a timed-out receive stays pending and would take the next datagram.
                async Task<string?> Punch(Action<IPEndPoint> send)
                {
                    using var target = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                    send((IPEndPoint)target.Client.LocalEndPoint!);
                    return await ReceiveOrNull(target);
                }
                Globals.STUNServer = null;

                // A datagram from no STUN server, naming a third party: nothing is sent.
                Assert.Null(await Punch(t => MessageService.PunchClient(new Message { Data = t.ToString() }, strangerEp, node)));

                // From the STUN server this node registered with, unsigned: nothing is sent; signed with the secret it
                // issued at registration: the punch goes out.
                Globals.STUNServer = new DSTConnection { IPAddress = strangerEp.ToString() };
                Assert.Null(await Punch(t => MessageService.PunchClient(new Message { Data = t.ToString() }, strangerEp, node)));
                var secret = DstSessionAuth.NewSecret();
                DstSessionAuth.StunSecrets[strangerEp.ToString()] = secret;
                Assert.Equal("ack", await Punch(t => MessageService.PunchClient(DstSessionAuth.SignedPunch(secret, t.ToString()), strangerEp, node)));

                // An asset punch only for a sender that completed the handshake.
                Assert.Null(await Punch(t => MessageService.AssetPunchClient(new Message { Data = t.Port.ToString() }, strangerEp, node)));
                Globals.ConnectedClients[strangerEp.ToString()] = new DSTConnection { IPAddress = strangerEp.ToString() };
                Assert.Equal("echo", await Punch(t => MessageService.AssetPunchClient(new Message { Data = t.Port.ToString() }, strangerEp, node)));

                // Junk targets are dropped, not thrown.
                MessageService.PunchClient(new Message { Data = "not an endpoint" }, strangerEp, node);
                MessageService.AssetPunchClient(new Message { Data = "99999" }, strangerEp, node);
            }
            finally { Globals.STUNServer = priorStun; DstSessionAuth.StunSecrets.Clear(); }
        }
    }
}