using System;
using System.IO;
using System.Linq;
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
        public void ABidSavedBeforeTheTargetShopRule_IsNotCompletedAutomatically_EvenWithAContract()
        {
            var nft = ListedNft();
            // A bid row from an earlier build: its contract may have been read from another cached shop.
            Bid.SaveBid(new Bid
            {
                Id = Guid.NewGuid(), BidAddress = _victim.Address, PurchaseKey = "key-legacy", BidAmount = 500M, MaxBidAmount = 500M,
                BidStatus = BidStatus.Sent, BidSendReceive = BidSendReceive.Sent, BidSendTime = TimeUtil.GetTime(), ListingId = 40, CollectionId = 1,
                SmartContractUID = nft,
            });
            Assert.False(Bid.HasLocalSentBid(_victim.Address, "key-legacy", 500M, nft));

            // The same bid made now, through Build with the shop it is sent to, is.
            var shop = new DecShopData { Listings = new System.Collections.Generic.List<Listing> { new Listing { Id = 41, PurchaseKey = "key-new", SmartContractUID = nft } } };
            var fresh = new Bid { BidAddress = _victim.Address, PurchaseKey = "key-new", BidAmount = 500M, ListingId = 41, CollectionId = 1, RawBid = true };
            Assert.True(fresh.Build(targetShop: shop));
            Bid.SaveBid(fresh);
            Assert.True(Bid.HasLocalSentBid(_victim.Address, "key-new", 500M, nft));
        }

        [Fact]
        public void IncomingSale_WithoutALocalBid_IsNotCompleted()
        {
            var nft = ListedNft();
            Assert.False(Bid.HasLocalSentBid(_victim.Address, "key-7", 500M, nft));

            Bid.SaveBid(new Bid
            {
                Id = Guid.NewGuid(), BidAddress = _victim.Address, PurchaseKey = "key-7", BidAmount = 500M, MaxBidAmount = 500M,
                BidStatus = BidStatus.Sent, BidSendReceive = BidSendReceive.Sent, BidSendTime = TimeUtil.GetTime(), ListingId = 1, CollectionId = 1,
                SmartContractUID = nft, ContractFromTargetShop = true,
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
                SmartContractUID = bidOn, ContractFromTargetShop = true,
            });
            Assert.True(Bid.HasLocalSentBid(_victim.Address, "key-9", 500M, bidOn));
            Assert.False(Bid.HasLocalSentBid(_victim.Address, "key-9", 500M, other)); // the replay
            Assert.False(Bid.HasLocalSentBid(_victim.Address, "key-9", 500M, null));
            Assert.False(Bid.HasLocalSentBid(_victim.Address, "key-9", 500M, ""));
        }

        /// <summary>
        /// A bid row from before the field records no contract. It is never auto-completed (re-audit: resolving it from the
        /// connected shop's listing would trust data the seller controls); the buyer bids again. Build() still records the
        /// contract on a NEW bid from the listing the buyer is looking at when the client omits it.
        /// </summary>
        [Fact]
        public void LegacyBidRow_IsNeverAutoCompleted_AndNewBidsRecordTheirContract()
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
                Assert.False(Bid.HasLocalSentBid(_victim.Address, "key-10", 500M, nft));

                // Even when the (seller-served) shop data names the contract, a legacy row is not completed.
                Globals.DecShopData = new DecShopData { Listings = new System.Collections.Generic.List<Listing> { new Listing { Id = 10, PurchaseKey = "key-10", SmartContractUID = nft } } };
                Assert.False(Bid.HasLocalSentBid(_victim.Address, "key-10", 500M, nft));
                Assert.False(Bid.HasLocalSentBid(_victim.Address, "key-10", 500M, other));

                // Build() records the contract on a new bid from the listing of the shop it is sent to, when the client did
                // not send it: that listing's id and purchase key, in that shop's data alone.
                var target = Globals.DecShopData;
                Assert.Equal(nft, Bid.ListingContractFor(target, 10, "key-10"));
                Assert.Null(Bid.ListingContractFor(target, 11, "key-11"));
                Assert.Null(Bid.ListingContractFor(target, 10, "another-shops-key"));
                Assert.Null(Bid.ListingContractFor(target, 3, "key-10")); // the key under another listing id

                // Another cached shop that serves a listing with the key the buyer bid with does not decide the contract:
                // neither for a bid to the shop that lists it, nor for a bid to a shop whose data does not name it.
                var copycat = new DecShopData { Listings = new System.Collections.Generic.List<Listing> { new Listing { Id = 10, PurchaseKey = "key-10", SmartContractUID = other } } };
                Globals.MultiDecShopData["copycat"] = copycat;
                try
                {
                    Assert.Equal(nft, Bid.ListingContractFor(target, 10, "key-10"));
                    Assert.Null(Bid.ListingContractFor(new DecShopData { Listings = new System.Collections.Generic.List<Listing>() }, 10, "key-10"));
                    // A bid to a web shop whose data does not name the listing, with only the copycat's data cached as well.
                    Globals.DecShopData = null;
                    var toAnotherShop = new Bid { BidAddress = _victim.Address, PurchaseKey = "key-10", BidAmount = 500M, ListingId = 10, CollectionId = 1, RawBid = true };
                    Assert.True(toAnotherShop.Build(targetShop: new DecShopData { Listings = new System.Collections.Generic.List<Listing>() }));
                    Assert.Null(toAnotherShop.SmartContractUID); // not the copycat's contract; never completed automatically
                    Globals.DecShopData = target;
                }
                finally { Globals.MultiDecShopData.TryRemove("copycat", out _); }

                var built = new Bid { BidAddress = _victim.Address, PurchaseKey = "key-10", BidAmount = 500M, ListingId = 10, CollectionId = 1, RawBid = true };
                Assert.True(built.Build(targetShop: target));
                Assert.Equal(nft, built.SmartContractUID);
                Bid.SaveBid(built);
                Assert.True(Bid.HasLocalSentBid(_victim.Address, "key-10", 500M, nft));
                Assert.False(Bid.HasLocalSentBid(_victim.Address, "key-10", 500M, other));
            }
            finally
            {
                Globals.DecShopData = priorShop;
            }
        }

        // ── Wallet-local: a bid pays automatically only while fresh, only if not rejected, and only once ──────────────

        private Bid SaveSentBid(string key, string nft, BidStatus status, long sendTime, long? autoCompleteUntil = null)
        {
            var bid = new Bid
            {
                Id = Guid.NewGuid(), BidAddress = _victim.Address, PurchaseKey = key, BidAmount = 500M, MaxBidAmount = 500M,
                BidStatus = status, BidSendReceive = BidSendReceive.Sent, BidSendTime = sendTime, ListingId = 20, CollectionId = 1,
                SmartContractUID = nft, ContractFromTargetShop = true, AutoCompleteUntil = autoCompleteUntil,
            };
            Bid.SaveBid(bid);
            return bid;
        }

        /// <summary>
        /// D18: a buyer who completes a sale by hand uses the bid as the automatic completion does. Otherwise a burn and
        /// re-mint of the same UID (which clears the chain's purchase keys) let the old bid signature start a new sale that
        /// this wallet paid automatically, a second time.
        /// </summary>
        [Fact]
        public async Task ManualCompletion_UsesTheBid_SoARecycledUidCannotAutoPayItAgain()
        {
            TipAtGate();
            AccountData.GetAccounts().InsertSafe(new Account { Address = _victim.Address, PublicKey = _victim.Pub, PrivateKey = "0" + _victim.Key.secret.ToString("x") });
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _victim.Address, Balance = 1000M, Nonce = 0 });
            var nft = ListedNft();
            var state = SmartContractStateTrei.GetSmartContractState(nft)!;
            state.IsLocked = true;
            state.NextOwner = _victim.Address;
            state.PurchaseAmount = 500M;
            SmartContractStateTrei.UpdateSmartContract(state);
            SaveSentBid("key-manual", nft, BidStatus.Accepted, TimeUtil.GetTime());
            Assert.True(Bid.HasLocalSentBid(_victim.Address, "key-manual", 500M, nft));

            // The buyer completes by hand (DSTV1Controller.CompleteNFTPurchase's call).
            await SmartContractService.CompleteSaleSmartContractTX(nft, _seller.Address, 500M, "key-manual");

            Assert.False(Bid.HasLocalSentBid(_victim.Address, "key-manual", 500M, nft));
        }

        /// <summary>
        /// The other completions use the bid too: a Sale_Complete or M_Sale_Complete this wallet sends raw (signed
        /// elsewhere), marked by Bid.MarkUsedByCompletion on the raw send path, and the controller's transfer-sale
        /// completion. Another sale transaction, or another purchase key, leaves the bid as it is.
        /// </summary>
        [Fact]
        public void ACompletionSentRaw_UsesTheBid_AnotherSaleTransactionDoesNot()
        {
            var nft = ListedNft();
            SaveSentBid("key-raw", nft, BidStatus.Accepted, TimeUtil.GetTime());
            Assert.True(Bid.HasLocalSentBid(_victim.Address, "key-raw", 500M, nft));
            Transaction Sale(string function, string key) => new Transaction
            {
                FromAddress = _victim.Address, ToAddress = _seller.Address, TransactionType = TransactionType.NFT_SALE,
                Data = JsonConvert.SerializeObject(new { Function = function, ContractUID = nft, KeySign = key }),
            };

            Bid.MarkUsedByCompletion(Sale("Sale_Cancel()", "key-raw"));
            Bid.MarkUsedByCompletion(Sale("M_Sale_Complete()", "another-key"));
            Assert.True(Bid.HasLocalSentBid(_victim.Address, "key-raw", 500M, nft));

            Bid.MarkUsedByCompletion(Sale("M_Sale_Complete()", "key-raw"));
            Assert.False(Bid.HasLocalSentBid(_victim.Address, "key-raw", 500M, nft));
        }

        /// <summary>
        /// A completion this wallet sent, mined in a block, uses its bid whatever route it took (from this node, signed
        /// elsewhere, or relayed by another node): the wallet's processing of its own mined transactions marks it.
        /// </summary>
        [Fact]
        public async Task AMinedCompletionFromThisWallet_UsesItsBid_WhateverRouteItTook()
        {
            AccountData.GetAccounts().InsertSafe(new Account { Address = _victim.Address, PublicKey = _victim.Pub, PrivateKey = "0" + _victim.Key.secret.ToString("x") });
            var nft = ListedNft();
            SaveSentBid("key-mined", nft, BidStatus.Accepted, TimeUtil.GetTime());
            Assert.True(Bid.HasLocalSentBid(_victim.Address, "key-mined", 500M, nft));
            var completion = new Transaction
            {
                Hash = "mined-completion", FromAddress = _victim.Address, ToAddress = _seller.Address, Amount = 0M, Fee = 0M, Nonce = 0,
                Timestamp = TimeUtil.GetTime(), TransactionType = TransactionType.NFT_SALE,
                Data = JsonConvert.SerializeObject(new { Function = "Sale_Complete()", ContractUID = nft, KeySign = "key-mined", Transactions = new List<Transaction>() }),
            };
            await BlockTransactionValidatorService.ProcessOutgoingTransaction(completion, AccountData.GetSingleAccount(_victim.Address)!, Gate);
            Assert.False(Bid.HasLocalSentBid(_victim.Address, "key-mined", 500M, nft));
        }

        /// <summary>
        /// A completion this wallet sent that is replayed rather than processed live - a full state rebuild here, as also a
        /// snapshot restore's replay or an account import's rescan - uses its bid too.
        /// </summary>
        [Fact]
        public async Task AReplayedCompletionFromThisWallet_UsesItsBid()
        {
            AccountData.GetAccounts().InsertSafe(new Account { Address = _victim.Address, PublicKey = _victim.Pub, PrivateKey = "0" + _victim.Key.secret.ToString("x") });
            var nft = ListedNft();
            SaveSentBid("key-replayed", nft, BidStatus.Accepted, TimeUtil.GetTime());
            Assert.True(Bid.HasLocalSentBid(_victim.Address, "key-replayed", 500M, nft));
            var completion = new Transaction
            {
                Hash = "replayed-completion", FromAddress = _victim.Address, ToAddress = _seller.Address, Amount = 0M, Fee = 0M, Nonce = 0,
                Timestamp = TimeUtil.GetTime(), TransactionType = TransactionType.NFT_SALE,
                Data = JsonConvert.SerializeObject(new { Function = "Sale_Complete()", ContractUID = nft, KeySign = "key-replayed", Transactions = new List<Transaction>() }),
            };
            BlockchainData.GetBlocks().InsertSafe(new Block { Height = 0, Hash = "g0", Timestamp = TimeUtil.GetTime(), Transactions = new List<Transaction>() });
            BlockchainData.GetBlocks().InsertSafe(new Block { Height = 1, Hash = "b1", PrevHash = "g0", Timestamp = TimeUtil.GetTime(), Transactions = new List<Transaction> { completion } });

            await VerifiedXCore.Utilities.BlockRollbackUtility.ResetTreis();

            Assert.False(Bid.HasLocalSentBid(_victim.Address, "key-replayed", 500M, nft));
        }

        [Fact]
        public void IncomingSale_FromARejectedOrExpiredOrUsedBid_IsNotCompleted()
        {
            var nft = ListedNft();
            var now = TimeUtil.GetTime();

            SaveSentBid("key-rejected", nft, BidStatus.Rejected, now);
            Assert.False(Bid.HasLocalSentBid(_victim.Address, "key-rejected", 500M, nft));

            SaveSentBid("key-expired", nft, BidStatus.Accepted, now - 30 * 24 * 3600, autoCompleteUntil: now - 1);
            Assert.False(Bid.HasLocalSentBid(_victim.Address, "key-expired", 500M, nft));

            // A row from before the deadline field: judged from its bid time.
            SaveSentBid("key-legacy-old", nft, BidStatus.Sent, now - Bid.AutoCompleteGraceSeconds - 60);
            Assert.False(Bid.HasLocalSentBid(_victim.Address, "key-legacy-old", 500M, nft));

            // Completed once: a second Sale_Start under the same signature (after a cancel) is not paid again.
            SaveSentBid("key-used", nft, BidStatus.Accepted, now);
            Assert.True(Bid.HasLocalSentBid(_victim.Address, "key-used", 500M, nft));
            Bid.MarkAutoCompleted(_victim.Address, "key-used", nft);
            Assert.False(Bid.HasLocalSentBid(_victim.Address, "key-used", 500M, nft));
        }

        [Fact]
        public void IncomingSale_FromAFreshSentOrAcceptedBid_IsStillCompleted()
        {
            var nft = ListedNft();
            var other = ListedNft();
            var now = TimeUtil.GetTime();

            SaveSentBid("key-sent", nft, BidStatus.Sent, now);
            Assert.True(Bid.HasLocalSentBid(_victim.Address, "key-sent", 500M, nft));

            // An auction bid made long before the listing ends stays completable until the end plus the grace.
            SaveSentBid("key-long-auction", nft, BidStatus.Accepted, now - 20 * 24 * 3600, autoCompleteUntil: now + 3600);
            Assert.True(Bid.HasLocalSentBid(_victim.Address, "key-long-auction", 500M, nft));

            // Marking one contract's bid used leaves another contract's bid on the same key alone.
            SaveSentBid("key-shared", nft, BidStatus.Accepted, now);
            SaveSentBid("key-shared", other, BidStatus.Accepted, now);
            Bid.MarkAutoCompleted(_victim.Address, "key-shared", nft);
            Assert.False(Bid.HasLocalSentBid(_victim.Address, "key-shared", 500M, nft));
            Assert.True(Bid.HasLocalSentBid(_victim.Address, "key-shared", 500M, other));
        }

        [Fact]
        public void ShopResponse_CannotReviveARejectedBid_OrTouchABidThisWalletDidNotSend()
        {
            var nft = ListedNft();
            var now = TimeUtil.GetTime();

            var rejected = SaveSentBid("key-revive", nft, BidStatus.Rejected, now);
            Bid.ApplyShopResponse(rejected.Id, BidStatus.Accepted);
            Assert.Equal(BidStatus.Rejected, Bid.GetSingleBid(rejected.Id)!.BidStatus);
            Assert.False(Bid.HasLocalSentBid(_victim.Address, "key-revive", 500M, nft));

            var received = new Bid
            {
                Id = Guid.NewGuid(), BidAddress = _victim.Address, PurchaseKey = "key-recv", BidAmount = 500M, MaxBidAmount = 500M,
                BidStatus = BidStatus.Received, BidSendReceive = BidSendReceive.Received, BidSendTime = now, ListingId = 30, CollectionId = 1,
                SmartContractUID = nft, ContractFromTargetShop = true,
            };
            Bid.SaveBid(received);
            Bid.ApplyShopResponse(received.Id, BidStatus.Accepted);
            Assert.Equal(BidStatus.Received, Bid.GetSingleBid(received.Id)!.BidStatus);
        }

        [Fact]
        public void OwnerResend_ReopensARejectedBid_SoTheShopsAcceptanceCounts()
        {
            var nft = ListedNft();
            var rejected = SaveSentBid("key-retry", nft, BidStatus.Rejected, TimeUtil.GetTime() - 30 * 24 * 3600, autoCompleteUntil: TimeUtil.GetTime() - 1);

            var reopened = Bid.ReopenForResend(rejected.Id);
            Bid.ApplyShopResponse(rejected.Id, BidStatus.Accepted);

            Assert.Equal(BidStatus.Accepted, Bid.GetSingleBid(rejected.Id)!.BidStatus);
            Assert.True(reopened!.AutoCompleteUntil > TimeUtil.GetTime()); // the window runs from the resend
            Assert.True(Bid.HasLocalSentBid(_victim.Address, "key-retry", 500M, nft));

            // A used bid stays used, resent or not.
            Bid.MarkAutoCompleted(_victim.Address, "key-retry", nft);
            Bid.ReopenForResend(rejected.Id);
            Assert.False(Bid.HasLocalSentBid(_victim.Address, "key-retry", 500M, nft));
        }

        [Fact]
        public async Task AutoCompletion_ChargesOnlyTheBuyerWhoseBidWasChecked()
        {
            var nft = ListedNft();
            var state = SmartContractStateTrei.GetSmartContractState(nft)!;
            var other = NewKey();
            state.NextOwner = other.Address; // a competing Sale_Start moved the sale to another account
            SmartContractStateTrei.UpdateSmartContract(state);

            var (refused, why) = await SmartContractService.CompleteSaleSmartContractTX(nft, _seller.Address, 500M, "key-40", expectedBuyer: _victim.Address);
            Assert.Null(refused);
            Assert.Contains("not completing it automatically", why);

            // The buyer the sale names is not refused by this check (it then needs the account in this wallet).
            var (_, sameBuyer) = await SmartContractService.CompleteSaleSmartContractTX(nft, _seller.Address, 500M, "key-40", expectedBuyer: other.Address);
            Assert.DoesNotContain("not completing it automatically", sameBuyer);
        }

        [Fact]
        public void ShopResponse_StillUpdatesASentBid()
        {
            var nft = ListedNft();
            var sent = SaveSentBid("key-accept", nft, BidStatus.Sent, TimeUtil.GetTime());

            Bid.ApplyShopResponse(sent.Id, BidStatus.Accepted);

            Assert.Equal(BidStatus.Accepted, Bid.GetSingleBid(sent.Id)!.BidStatus);
            Assert.True(Bid.HasLocalSentBid(_victim.Address, "key-accept", 500M, nft));
        }

        [Fact]
        public void SavingACopyReadBeforeTheBidWasUsed_DoesNotMakeItUnusedAgain()
        {
            var nft = ListedNft();
            SaveSentBid("key-stale", nft, BidStatus.Accepted, TimeUtil.GetTime());
            var staleCopy = Bid.GetAllBids(BidSendReceive.Sent)!.Single(b => b.PurchaseKey == "key-stale");

            Bid.MarkAutoCompleted(_victim.Address, "key-stale", nft);
            staleCopy.BidStatus = BidStatus.Accepted;
            Bid.SaveBid(staleCopy);

            Assert.True(Bid.GetSingleBid(staleCopy.Id)!.AutoCompleted);
            Assert.False(Bid.HasLocalSentBid(_victim.Address, "key-stale", 500M, nft));
        }

        [Fact]
        public void NewBid_DeadlineIsTheListingEndPlusGrace_OrTheBidTimePlusGrace()
        {
            var priorShop = Globals.DecShopData;
            try
            {
                var nft = ListedNft();
                var end = DateTime.UtcNow.AddDays(10);
                Globals.DecShopData = new DecShopData { Listings = new System.Collections.Generic.List<Listing> { new Listing { Id = 21, PurchaseKey = "key-21", SmartContractUID = nft, EndDate = end } } };
                var auctionBid = new Bid { BidAddress = _victim.Address, PurchaseKey = "key-21", BidAmount = 500M, ListingId = 21, CollectionId = 1, RawBid = true };
                Assert.True(auctionBid.Build());
                Assert.Equal(new DateTimeOffset(end).ToUnixTimeSeconds() + Bid.AutoCompleteGraceSeconds, auctionBid.AutoCompleteUntil);

                // Unknown listing (or an end before the bid): the window runs from the bid.
                Globals.DecShopData = null;
                var unknown = new Bid { BidAddress = _victim.Address, PurchaseKey = "key-22", BidAmount = 500M, ListingId = 22, CollectionId = 1, RawBid = true, SmartContractUID = nft };
                Assert.True(unknown.Build());
                Assert.Equal(unknown.BidSendTime + Bid.AutoCompleteGraceSeconds, unknown.AutoCompleteUntil);
            }
            finally
            {
                Globals.DecShopData = priorShop;
            }
        }
    }
}
