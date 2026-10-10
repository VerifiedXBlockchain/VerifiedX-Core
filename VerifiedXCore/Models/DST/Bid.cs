using LiteDB;
using VerifiedXCore.Data;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using System.Net;
using System.Security.Cryptography;
using static VerifiedXCore.Models.Mother;

namespace VerifiedXCore.Models.DST
{
    public class Bid
    {
        [BsonId]
        public Guid Id { get; set; }
        public string BidAddress { get; set; }
        public string BidSignature { get; set; }
        public decimal BidAmount { get; set; }
        public decimal MaxBidAmount { get; set; }
        public bool IsBuyNow { get; set; }
        public bool IsAutoBid { get; set; }
        public bool RawBid { get; set; }
        public string PurchaseKey { get; set; }
        public BidStatus BidStatus { get; set; }
        public BidSendReceive BidSendReceive { get; set; }
        public long BidSendTime { get; set; }
        public bool? IsProcessed { get; set; }// Bid Queue Item
        public int ListingId { get; set; }
        public int CollectionId { get; set; }
        /// <summary>
        /// Re-audit (9 Oct 2026): the contract the listing sells. The bid signature covers purchase key, amount and bidder
        /// only, so the wallet records which contract it bid on and completes a sale only for that contract
        /// (<see cref="HasLocalSentBid"/>). Filled by <see cref="Build"/> from the connected shop's listing when the client
        /// did not send it; null on rows from before this field existed.
        /// </summary>
        public string? SmartContractUID { get; set; }

        public bool Build(bool thirdParty = false)
        {
            if (BidAmount < Globals.BidMinimum)
                return false;

            if(string.IsNullOrEmpty(PurchaseKey))
                return false;

            Id = Guid.NewGuid();
            BidStatus = thirdParty ? BidStatus : BidStatus.Sent;
            IsAutoBid = false;
            BidSendTime = TimeUtil.GetTime();
            MaxBidAmount = BidAmount;
            BidSendReceive = BidSendReceive.Sent;
            if (string.IsNullOrWhiteSpace(SmartContractUID))
                SmartContractUID = ListingContractFor(ListingId, PurchaseKey);

            var bidModifier = (BidAmount * Globals.BidModifier);
            var bidAmount = Convert.ToInt64(bidModifier);

            if(!RawBid)
            {
                var account = AccountData.GetSingleAccount(BidAddress);
                if (account == null)
                    return false;

                if (account.GetPrivKey == null)
                    return false;
                var message = $"{PurchaseKey}_{bidAmount}_{BidAddress}";


                var signature = SignatureService.CreateSignature(message, account.GetPrivKey, account.PublicKey);

                BidSignature = signature;
            }
            
            return true;
        }

        #region Get Bid Db
        public static LiteDB.ILiteCollection<Bid>? GetBidDb()
        {
            try
            {
                var bidDb = DbContext.DB_DST.GetCollection<Bid>(DbContext.RSRV_BID);
                return bidDb;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError(ex.ToString(), "Bid.GetBidDb()");
                return null;
            }

        }

        #endregion

        #region Get All Bids
        public static IEnumerable<Bid>? GetAllBids(BidSendReceive? bidSendReceive = null)
        {
            var bidDb = GetBidDb();

            if (bidDb != null)
            {
                var bids = bidSendReceive == null ? bidDb.Query().Where(x => true).ToEnumerable() : bidDb.Query().Where(x => x.BidSendReceive == bidSendReceive).ToEnumerable();
                if (bids.Count() == 0)
                {
                    return null;
                }

                return bids;
            }
            else
            {
                return null;
            }
        }

        #endregion

        #region Get Single Bid
        public static Bid? GetSingleBid(Guid bidId)
        {
            var bidDb = GetBidDb();

            if (bidDb != null)
            {
                var bid = bidDb.Query().Where(x => x.Id == bidId).FirstOrDefault();
                if (bid == null)
                {
                    return null;
                }

                return bid;
            }
            else
            {
                return null;
            }
        }

        #endregion

        #region Get Listing Bids
        public static IEnumerable<Bid>? GetListingBids(int listingId, BidSendReceive? bidSendReceive = null)
        {
            var bidDb = GetBidDb();

            if (bidDb != null)
            {
                var bids = bidSendReceive == null ? bidDb.Query().Where(x => x.ListingId == listingId).ToEnumerable() :
                    bidDb.Query().Where(x => x.ListingId == listingId && x.BidSendReceive == bidSendReceive).ToEnumerable();
                if (bids.Count() == 0)
                {
                    return null;
                }

                return bids;
            }
            else
            {
                return null;
            }
        }

        #endregion

        #region Get Bid By Status
        public static IEnumerable<Bid>? GetBidByStatus(BidStatus bidStatus, BidSendReceive? bidSendReceive = null)
        {
            var bidDb = GetBidDb();

            if (bidDb != null)
            {
                var bids = bidSendReceive == null ? bidDb.Query().Where(x => x.BidStatus == bidStatus).ToEnumerable() : 
                    bidDb.Query().Where(x => x.BidStatus == bidStatus && x.BidSendReceive == bidSendReceive).ToEnumerable();
                if (bids.Count() == 0)
                {
                    return null;
                }

                return bids;
            }
            else
            {
                return null;
            }
        }

        #endregion

        #region Save Bid
        public static (bool, string) SaveBid(Bid bid, bool dontUpdate = false)
        {
            var singleBid = GetSingleBid(bid.Id);
            var bidDb = GetBidDb();
            if (singleBid == null)
            {
                if (bidDb != null)
                {
                    bidDb.InsertSafe(bid);
                    return (true, "Bid saved.");
                }
            }
            else
            {
                if (bidDb != null && !dontUpdate)
                {
                    bidDb.UpdateSafe(bid);
                    return (true, "Bid updated.");
                }
            }
            return (false, "Bid DB was null.");
        }

        #endregion

        #region Delete Bid
        public static (bool, string) DeleteBid(Guid bidId)
        {
            var singleBid = GetSingleBid(bidId);
            if (singleBid != null)
            {
                var bidDb = GetBidDb();
                if (bidDb != null)
                {
                    bidDb.DeleteSafe(bidId);
                    return (true, "Bid deleted.");
                }
                else
                {
                    return (false, "Bid DB was null.");
                }
            }
            return (false, "Bid was not present.");

        }

        #endregion

        #region Delete All Bids By Collection
        public static async Task<(bool, string)> DeleteAllBidsByCollection(int collectionId)
        {
            try
            {
                var bidDb = GetBidDb();
                if (bidDb != null)
                {
                    bidDb.DeleteManySafe(x => x.CollectionId == collectionId);
                    return (true, "Bids deleted.");
                }
                else
                {
                    return (false, "Bid DB was null.");
                }
            }
            catch (Exception ex)
            {
                return (false, $"Failed to delete. Error: {ApiErrorText.For(ex)}");
            }

        }

        #endregion

        #region Delete All Bids By Listing
        public static async Task<(bool, string)> DeleteAllBidsByListing(int listingId)
        {
            try
            {
                var bidDb = GetBidDb();
                if (bidDb != null)
                {
                    bidDb.DeleteManySafe(x => x.ListingId ==  listingId);
                    return (true, "Bids deleted.");
                }
                else
                {
                    return (false, "Bid DB was null.");
                }
            }
            catch (Exception ex)
            {
                return (false, $"Failed to delete. Error: {ApiErrorText.For(ex)}");
            }

        }

        #endregion

        #region Local bid lookup (fund-loss audit item 5)
        /// <summary>
        /// Whether THIS wallet placed a bid that a Sale_Start naming <paramref name="bidderAddress"/> as the next owner can
        /// legitimately conclude: a sent bid from that address under <paramref name="purchaseKey"/> whose amount (or
        /// auto-bid ceiling) covers <paramref name="amount"/>. The incoming-sale handler auto-signed the completion from
        /// the wallet with no such check, so a Sale_Start that skipped the bid signature made the named wallet pay.
        /// </summary>
        /// <summary>
        /// Whether this wallet placed a bid that the incoming sale can complete: same bidder, same purchase key, an amount
        /// at or below what it bid, and (re-audit, 9 Oct 2026) on the same contract. The bid signature does not name the
        /// contract, so a Sale_Start for another contract carrying a replayed signature must not make this wallet pay.
        /// A bid row that records no contract (from before the field) is not completed automatically: resolving it from
        /// the connected shop's listing would trust data the seller serves.
        /// </summary>
        public static bool HasLocalSentBid(string? bidderAddress, string? purchaseKey, decimal amount, string? smartContractUID)
        {
            if (string.IsNullOrEmpty(bidderAddress) || string.IsNullOrEmpty(purchaseKey) || amount <= 0M || string.IsNullOrWhiteSpace(smartContractUID))
                return false;
            try
            {
                var bids = GetAllBids(BidSendReceive.Sent);
                if (bids == null) return false;
                return bids.Any(b => b != null
                    && string.Equals(b.BidAddress, bidderAddress, StringComparison.Ordinal)
                    && string.Equals(b.PurchaseKey, purchaseKey, StringComparison.Ordinal)
                    && Math.Max(b.BidAmount, b.MaxBidAmount) >= amount
                    && string.Equals(b.SmartContractUID, smartContractUID, StringComparison.Ordinal)); // no fallback: shop data is the seller's (re-audit)
            }
            catch { return false; }
        }

        /// <summary>
        /// The contract a listing sells, from the shop data this node holds (the connected shop and any multi-shop
        /// sessions): by purchase key first (random per listing), else by listing id. Null when unknown.
        /// </summary>
        public static string? ListingContractFor(int listingId, string? purchaseKey)
        {
            try
            {
                var sources = new List<IEnumerable<Listing>?> { Globals.DecShopData?.Listings };
                foreach (var shop in Globals.MultiDecShopData.Values)
                    sources.Add(shop?.Listings);
                foreach (var listings in sources)
                {
                    if (listings == null) continue;
                    var byKey = !string.IsNullOrEmpty(purchaseKey)
                        ? listings.FirstOrDefault(l => l != null && string.Equals(l.PurchaseKey, purchaseKey, StringComparison.Ordinal))
                        : null;
                    var hit = byKey ?? (listingId > 0 ? listings.FirstOrDefault(l => l != null && l.Id == listingId) : null);
                    if (hit != null && !string.IsNullOrWhiteSpace(hit.SmartContractUID))
                        return hit.SmartContractUID;
                }
            }
            catch { }
            return null;
        }
        #endregion

        #region Verify Bid Signature
        public static bool VerifyBidSignature(string keySign, decimal amount, string address, string bidSignature)
        {
            //public const decimal BidModifier = 100000000M
            var bidModifier = (amount * Globals.BidModifier).ToString("#");
            var bidAmount = Convert.ToInt64(bidModifier);

            var bidMessage = $"{keySign}_{bidAmount}_{address}";
            var signatureVerify = SignatureService.VerifySignature(address, bidMessage, bidSignature);

            return signatureVerify;
        }
        #endregion

    }

    public enum BidStatus
    { 
        Accepted,
        Rejected,
        Sent,
        Received
    }

    public enum BidSendReceive
    {
        Sent,
        Received
    }


}
