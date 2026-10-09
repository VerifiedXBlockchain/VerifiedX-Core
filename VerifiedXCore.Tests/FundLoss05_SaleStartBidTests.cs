using System;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Models.DST;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Fund-loss audit item 5. Consensus (Globals.SaleStartBidRulesHeight): the "manual" bid-signature bypass is honoured
    /// for M_Sale_Start() only; a plain Sale_Start() must carry a bid signature that verifies for the named buyer.
    /// Wallet-local (ungated): the incoming-sale handler completes a sale only against a bid this wallet placed.
    /// </summary>
    [Collection("DbContextSequential")]
    public class FundLoss05_SaleStartBidTests : IDisposable
    {
        private const long Gate = 1000;
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly long _priorGate;
        private readonly (PrivateKey Key, string Pub, string Address) _seller;
        private readonly (PrivateKey Key, string Pub, string Address) _victim;

        public FundLoss05_SaleStartBidTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"fl05_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorLastBlock = Globals.LastBlock;
            _priorGate = Globals.SaleStartBidRulesHeight;
            Globals.SaleStartBidRulesHeight = Gate;
            DbContext.Initialize();
            _seller = NewKey();
            _victim = NewKey();
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _seller.Address, Balance = 1000M, Nonce = 0 });
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.LastBlock = _priorLastBlock;
            Globals.SaleStartBidRulesHeight = _priorGate;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static (PrivateKey Key, string Pub, string Address) NewKey()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            return (key, pub, AccountData.GetHumanAddress(pub));
        }

        private string ListedNft()
        {
            var uid = Guid.NewGuid().ToString("N") + ":" + TimeUtil.GetTime();
            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
            {
                SmartContractUID = uid, ContractData = VbtcTestContracts.PlainNftContractData,
                MinterAddress = _seller.Address, OwnerAddress = _seller.Address, IsLocked = false, Nonce = 0,
            });
            return uid;
        }

        private Transaction SaleStart(string function, string scUid, string buyer, decimal soldFor, string keySign, string bidSignature)
        {
            var tx = new Transaction
            {
                FromAddress = _seller.Address, ToAddress = buyer, Amount = 0M, Fee = 0.00000100M, Nonce = 0,
                Timestamp = TimeUtil.GetTime(), TransactionType = TransactionType.NFT_SALE,
                Data = JsonConvert.SerializeObject(new { Function = function, ContractUID = scUid, NextOwner = buyer, SoldFor = soldFor, KeySign = keySign, BidSignature = bidSignature }),
            };
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, _seller.Key, _seller.Pub);
            return tx;
        }

        private string RealBidSignature(string keySign, decimal amount)
        {
            var bidAmount = Convert.ToInt64((amount * Globals.BidModifier).ToString("#"));
            return SignatureService.CreateSignature($"{keySign}_{bidAmount}_{_victim.Address}", _victim.Key, _victim.Pub);
        }

        private static void TipBelowGate() => Globals.LastBlock = new Block { Height = Gate - 2 };
        private static void TipAtGate() => Globals.LastBlock = new Block { Height = Gate - 1 };

        // ── Consensus ─────────────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task SaleStartWithManual_PassesBelowGate_RefusedAtGate()
        {
            TipBelowGate();
            var (okBelow, msgBelow) = await TransactionValidatorService.VerifyTX(SaleStart("Sale_Start()", ListedNft(), _victim.Address, 500M, "key-1", "manual"));
            Assert.True(okBelow, msgBelow); // the hole: any address named as buyer, no bid signature

            TipAtGate();
            var (okAt, msgAt) = await TransactionValidatorService.VerifyTX(SaleStart("Sale_Start()", ListedNft(), _victim.Address, 500M, "key-2", "manual"));
            Assert.False(okAt);
            Assert.Equal("Bid signature did not verify.", msgAt);
        }

        [Fact]
        public async Task ManualSaleStart_StillPassesAtGate()
        {
            TipAtGate();
            var (ok, msg) = await TransactionValidatorService.VerifyTX(SaleStart("M_Sale_Start()", ListedNft(), _victim.Address, 500M, "key-3", "manual"));
            Assert.True(ok, msg);
        }

        [Fact]
        public async Task SaleStartWithARealBidSignature_PassesAtGate()
        {
            TipAtGate();
            var (ok, msg) = await TransactionValidatorService.VerifyTX(SaleStart("Sale_Start()", ListedNft(), _victim.Address, 500M, "key-4", RealBidSignature("key-4", 500M)));
            Assert.True(ok, msg);
        }

        [Fact]
        public async Task BlockPath_JudgesAtTheBlocksHeight()
        {
            Globals.LastBlock = new Block { Height = Gate + 5000 };
            var (okHistorical, msg) = await TransactionValidatorService.VerifyTX(SaleStart("Sale_Start()", ListedNft(), _victim.Address, 500M, "key-5", "manual"), blockDownloads: true, blockVerify: true, blockHeight: Gate - 1);
            Assert.True(okHistorical, msg);

            Globals.LastBlock = new Block { Height = 10 };
            var (okAtGate, msgAtGate) = await TransactionValidatorService.VerifyTX(SaleStart("Sale_Start()", ListedNft(), _victim.Address, 500M, "key-6", "manual"), blockVerify: true, blockHeight: Gate);
            Assert.False(okAtGate);
            Assert.Equal("Bid signature did not verify.", msgAtGate);
        }

        // ── Wallet-local: complete only a sale this wallet bid on ─────────────────────────────────────────────────

        [Fact]
        public void IncomingSale_WithoutALocalBid_IsNotCompleted()
        {
            var nft = ListedNft();
            Assert.False(Bid.HasLocalSentBid(_victim.Address, "key-7", 500M, nft));

            Bid.SaveBid(new Bid
            {
                Id = Guid.NewGuid(), BidAddress = _victim.Address, PurchaseKey = "key-7", BidAmount = 500M, MaxBidAmount = 500M,
                BidStatus = BidStatus.Sent, BidSendReceive = BidSendReceive.Sent, BidSendTime = TimeUtil.GetTime(), ListingId = 1, CollectionId = 1,
                SmartContractUID = nft,
            });
            Assert.True(Bid.HasLocalSentBid(_victim.Address, "key-7", 500M, nft));
            Assert.True(Bid.HasLocalSentBid(_victim.Address, "key-7", 499M, nft));   // sold for less than the bid ceiling
            Assert.False(Bid.HasLocalSentBid(_victim.Address, "key-7", 501M, nft));  // sold for more than this wallet ever bid
            Assert.False(Bid.HasLocalSentBid(_victim.Address, "key-8", 500M, nft));  // a different purchase key (replayed signature)
            Assert.False(Bid.HasLocalSentBid(NewKey().Address, "key-7", 500M, nft)); // a different buyer
            Assert.False(Bid.HasLocalSentBid(_victim.Address, "key-7", 0M, nft));
        }

        /// <summary>
        /// Re-audit (9 Oct 2026): the bid signature is over purchase key, amount and bidder only, so a seller can replay it in a
        /// Sale_Start for ANOTHER contract and the buyer's wallet (which auto-completes from its own balance) must refuse.
        /// </summary>
        [Fact]
        public void IncomingSale_ForAnotherContract_WithAReplayedBidSignature_IsNotCompleted()
        {
            var bidOn = ListedNft();
            var other = ListedNft();
            Bid.SaveBid(new Bid
            {
                Id = Guid.NewGuid(), BidAddress = _victim.Address, PurchaseKey = "key-9", BidAmount = 500M, MaxBidAmount = 500M,
                BidStatus = BidStatus.Sent, BidSendReceive = BidSendReceive.Sent, BidSendTime = TimeUtil.GetTime(), ListingId = 9, CollectionId = 1,
                SmartContractUID = bidOn,
            });
            Assert.True(Bid.HasLocalSentBid(_victim.Address, "key-9", 500M, bidOn));
            Assert.False(Bid.HasLocalSentBid(_victim.Address, "key-9", 500M, other)); // the replay
            Assert.False(Bid.HasLocalSentBid(_victim.Address, "key-9", 500M, null));
            Assert.False(Bid.HasLocalSentBid(_victim.Address, "key-9", 500M, ""));
        }

        /// <summary>A bid row from before the field is tied to its contract through the shop's listing; with no listing it is not auto-completed.</summary>
        [Fact]
        public void LegacyBidRow_IsTiedToItsContractThroughTheShopListing_OrNotCompleted()
        {
            var priorShop = Globals.DecShopData;
            try
            {
                var nft = ListedNft();
                var other = ListedNft();
                Bid.SaveBid(new Bid
                {
                    Id = Guid.NewGuid(), BidAddress = _victim.Address, PurchaseKey = "key-10", BidAmount = 500M, MaxBidAmount = 500M,
                    BidStatus = BidStatus.Sent, BidSendReceive = BidSendReceive.Sent, BidSendTime = TimeUtil.GetTime(), ListingId = 10, CollectionId = 1,
                    SmartContractUID = null,
                });

                Globals.DecShopData = null;
                Assert.False(Bid.HasLocalSentBid(_victim.Address, "key-10", 500M, nft)); // nothing to tie it to: not completed

                Globals.DecShopData = new DecShopData { Listings = new System.Collections.Generic.List<Listing> { new Listing { Id = 10, PurchaseKey = "key-10", SmartContractUID = nft } } };
                Assert.Equal(nft, Bid.ListingContractFor(10, "key-10"));
                Assert.Equal(nft, Bid.ListingContractFor(0, "key-10"));   // by purchase key alone
                Assert.Equal(nft, Bid.ListingContractFor(10, null));      // by listing id alone
                Assert.Null(Bid.ListingContractFor(11, "key-11"));
                Assert.True(Bid.HasLocalSentBid(_victim.Address, "key-10", 500M, nft));
                Assert.False(Bid.HasLocalSentBid(_victim.Address, "key-10", 500M, other));

                // Build() records the contract on a new bid from the shop listing when the client did not send it.
                var built = new Bid { BidAddress = _victim.Address, PurchaseKey = "key-10", BidAmount = 500M, ListingId = 10, CollectionId = 1, RawBid = true };
                Assert.True(built.Build());
                Assert.Equal(nft, built.SmartContractUID);
            }
            finally
            {
                Globals.DecShopData = priorShop;
            }
        }
    }
}
