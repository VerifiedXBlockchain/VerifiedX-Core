using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.DST;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Engines;
using VerifiedXCore.Models;
using VerifiedXCore.Models.DST;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// VX-10 (HIGH): "Unsigned datagrams manipulate marketplace auction state".
    ///
    /// Audit PoC: one Type 4 (Bid) datagram with BidSignature "NOT-A-SIGNATURE", a wrong PurchaseKey,
    /// BidAddress holding 0, BidAmount 0 and MaxBidAmount 1,000,000 became the accepted winning bid; one
    /// Type 5 (Purchase) datagram ended a live auction a month early.
    /// </summary>
    [Collection("DbContextSequential")]
    public class VX10_UnsignedBidTests : IDisposable
    {
        private const string PurchaseKey = "PKEYLISTING";
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly (PrivateKey Key, string Pub, string Address) _bidder;
        private readonly IPEndPoint _endpoint = new IPEndPoint(IPAddress.Loopback, 45999);
        private int _listingId;

        public VX10_UnsignedBidTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"vx10_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            while (Globals.BidQueue.TryDequeue(out _)) { }
            while (Globals.BuyNowQueue.TryDequeue(out _)) { }

            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            _bidder = (key, pub, AccountData.GetHumanAddress(pub));

            var listing = new Listing
            {
                SmartContractUID = "sc:1", AddressOwner = "xShopOwner", PurchaseKey = PurchaseKey, BuyNowPrice = 100M,
                FloorPrice = 10M, IsAuctionStarted = true, StartDate = DateTime.UtcNow.AddDays(-1), EndDate = DateTime.UtcNow.AddDays(30),
                RequireBalanceCheck = false,
            };
            Listing.GetListingDb()!.Insert(listing);
            _listingId = listing.Id;
            Auction.GetAuctionDb()!.Insert(new Auction
            {
                ListingId = _listingId, CurrentBidPrice = 10M, MaxBidPrice = 10M, IncrementAmount = 1M, CurrentWinningAddress = "xShopOwner",
            });
        }

        public void Dispose()
        {
            while (Globals.BidQueue.TryDequeue(out _)) { }
            while (Globals.BuyNowQueue.TryDequeue(out _)) { }
            Globals.ConnectedClients.TryRemove(_endpoint.ToString(), out _);
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private string Sign(string purchaseKey, decimal amount) =>
            SignatureService.CreateSignature($"{purchaseKey}_{Convert.ToInt64(amount * Globals.BidModifier)}_{_bidder.Address}", _bidder.Key, _bidder.Pub);

        private BidQueue GenuineBid(decimal amount) => new BidQueue
        {
            Id = Guid.NewGuid(), BidAddress = _bidder.Address, BidAmount = amount, MaxBidAmount = amount, ListingId = _listingId,
            PurchaseKey = PurchaseKey, BidSignature = Sign(PurchaseKey, amount), EndPoint = _endpoint, BidSendReceive = BidSendReceive.Received,
        };

        private BidQueue AuditForgedBid() => new BidQueue
        {
            Id = Guid.NewGuid(), BidAddress = "xHEC8yrsK9ZpfUw2nXXqkZd3KFFtwbphvG", BidSignature = "NOT-A-SIGNATURE", PurchaseKey = "WRONGKEY",
            BidAmount = 0M, MaxBidAmount = 1_000_000M, ListingId = _listingId, EndPoint = _endpoint, BidSendReceive = BidSendReceive.Received,
        };

        private static string Wire(BidQueue b) { b.EndPoint = null!; return JsonConvert.SerializeObject(b); }

        private Auction CurrentAuction() => Auction.GetListingAuction(_listingId)!;
        private Listing CurrentListing() => Listing.GetListingDb()!.Query().Where(x => x.Id == _listingId).First();

        // ── Engine (the state change) ──────────────────────────────────────────────────────

        [Fact]
        public void VX10_AuditPoC_ForgedBid_DoesNotChangeTheAuction()
        {
            Globals.BidQueue.Enqueue(AuditForgedBid());
            AuctionEngine.ProcessBidQueue();

            var a = CurrentAuction();
            Assert.Equal(10M, a.CurrentBidPrice);
            Assert.Equal("xShopOwner", a.CurrentWinningAddress);
            Assert.Null(a.WinningBidId);
        }

        [Fact]
        public void VX10_Control_GenuineSignedBid_IsAccepted()
        {
            Globals.BidQueue.Enqueue(GenuineBid(20M));
            AuctionEngine.ProcessBidQueue();

            var a = CurrentAuction();
            Assert.Equal(20M, a.CurrentBidPrice);
            Assert.Equal(_bidder.Address, a.CurrentWinningAddress);
        }

        [Fact]
        public void VX10_SignedBid_WithInflatedUnsignedMaxBid_Refused()
        {
            var bid = GenuineBid(20M);
            bid.MaxBidAmount = 1_000_000M; // what the auction would record; not covered by the signature
            Globals.BidQueue.Enqueue(bid);
            AuctionEngine.ProcessBidQueue();
            Assert.Equal(10M, CurrentAuction().CurrentBidPrice);
        }

        [Fact]
        public void VX10_AuditPoC_ForgedBuyNow_DoesNotEndTheAuction()
        {
            var forged = AuditForgedBid();
            forged.IsBuyNow = true;
            Globals.BuyNowQueue.Enqueue(forged);
            AuctionEngine.ProcessBuyNowQueue();

            Assert.False(CurrentAuction().IsAuctionOver);
            Assert.False(CurrentListing().IsAuctionEnded);
        }

        [Fact]
        public void VX10_Control_GenuineBuyNow_SignedForTheBuyNowPrice_EndsTheAuction()
        {
            var buyNow = GenuineBid(100M);
            buyNow.IsBuyNow = true;
            Globals.BuyNowQueue.Enqueue(buyNow);
            AuctionEngine.ProcessBuyNowQueue();

            Assert.True(CurrentAuction().IsAuctionOver);
        }

        // ── UDP handler (the ingress) ──────────────────────────────────────────────────────

        [Fact]
        public void VX10_Handler_DatagramFromANonHandshakedSender_IsNotQueued()
        {
            using var udp = new UdpClient(0);
            var msg = new Message { Id = "m1", Type = MessageType.Bid, ComType = MessageComType.Request, Data = Wire(GenuineBid(20M)) };
            MessageService.ProcessBid(msg, _endpoint, udp);
            Assert.Empty(Globals.BidQueue);
        }

        [Fact]
        public void VX10_Handler_ForgedBidFromAHandshakedSender_IsNotQueued_GenuineIs()
        {
            Globals.ConnectedClients[_endpoint.ToString()] = new DSTConnection { IPAddress = "127.0.0.1", ConnectDate = VerifiedXCore.Utilities.TimeUtil.GetTime(), LastReceiveMessage = VerifiedXCore.Utilities.TimeUtil.GetTime() };
            using var udp = new UdpClient(0);

            MessageService.ProcessBid(new Message { Id = "m2", Type = MessageType.Bid, ComType = MessageComType.Request, Data = Wire(AuditForgedBid()) }, _endpoint, udp);
            Assert.Empty(Globals.BidQueue);

            MessageService.ProcessBid(new Message { Id = "m3", Type = MessageType.Bid, ComType = MessageComType.Request, Data = Wire(GenuineBid(20M)) }, _endpoint, udp);
            Assert.Single(Globals.BidQueue);
        }

        // ── Rule unit ──────────────────────────────────────────────────────────────────────

        [Fact]
        public void VX10_Rule_SignatureMustCoverThisListingsKeyAndThePrice()
        {
            var listing = CurrentListing();
            Assert.Null(DstBidAuthorization.Validate(GenuineBid(20M), listing, false, out var price));
            Assert.Equal(20M, price);

            var otherListingSig = GenuineBid(20M); otherListingSig.BidSignature = Sign("OTHERLISTING", 20M);
            Assert.NotNull(DstBidAuthorization.Validate(otherListingSig, listing, false, out _));

            var zero = GenuineBid(20M); zero.BidAmount = 0; zero.MaxBidAmount = 0;
            Assert.NotNull(DstBidAuthorization.Validate(zero, listing, false, out _)); // no throw on 0

            var buyNowWrongPrice = GenuineBid(50M); buyNowWrongPrice.IsBuyNow = true;
            Assert.NotNull(DstBidAuthorization.Validate(buyNowWrongPrice, listing, true, out _));
        }
    }
}
