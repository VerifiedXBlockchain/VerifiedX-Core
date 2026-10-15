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

        /// <summary>
        /// For a vBTC vault the chain watches: the owner's spendable vBTC when the bid was made. A sale is completed from
        /// this wallet's balance automatically when the seller starts it, and the seller could empty the vault's owner
        /// balance before (or with) its Sale_Start: the buyer paid in full for an empty vault (fifth review, round 30).
        /// </summary>
        public decimal? VaultOwnerBalanceAtBid { get; set; }

        /// <summary>The owner's spendable vBTC on <paramref name="smartContractUID"/> now, when it is a watched vault; else null.</summary>
        internal static decimal? VaultOwnerFigure(string? smartContractUID)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(smartContractUID) || !VerifiedXCore.Bitcoin.Attestation.BtcAttestBalance.IsAttestedVault(smartContractUID)) return null;
                var state = SmartContractStateTrei.GetSmartContractState(smartContractUID);
                if (state == null || string.IsNullOrEmpty(state.OwnerAddress)) return null;
                return VerifiedXCore.Bitcoin.Services.VBTCService.AttestedOwnerSpendable(state, state.OwnerAddress, (Globals.LastBlock?.Height ?? 0) + 1);
            }
            catch { return null; }
        }

        /// <summary>
        /// Why a sale of a watched vault is not completed automatically for this bid: the owner's spendable vBTC is less
        /// than when the bid was made (or was not recorded). Null when the vault is not watched or kept what was bid for.
        /// </summary>
        public static string? VaultShrankSinceBid(string? bidderAddress, string? purchaseKey, string? smartContractUID)
        {
            var now = VaultOwnerFigure(smartContractUID);
            // A vault the chain does not watch (created before attestation) has no attested owner figure, and nothing holds
            // its owner balance still while it is for sale: it is never paid for automatically (fifth review, round 32).
            if (now == null)
            {
                try
                {
                    var state = string.IsNullOrWhiteSpace(smartContractUID) ? null : SmartContractStateTrei.GetSmartContractState(smartContractUID);
                    if (state != null && VerifiedXCore.Bitcoin.Services.VBTCService.IsVbtcV2Contract(state))
                        return "The vault is not watched by the chain, so its owner balance is not held while it is for sale; complete the sale by hand after checking it.";
                }
                catch { return "The contract could not be checked; complete the sale by hand."; }
                return null;
            }
            // Every bid of this buyer on the listing (re-bids share its purchase key): the highest figure recorded, so a
            // later, higher bid made when the vault held more is the one held to (round 31).
            var recorded = (GetAllBids(BidSendReceive.Sent) ?? new List<Bid>()).Where(b => b != null
                && string.Equals(b.BidAddress, bidderAddress, StringComparison.Ordinal)
                && string.Equals(b.PurchaseKey, purchaseKey, StringComparison.Ordinal)
                && string.Equals(b.SmartContractUID, smartContractUID, StringComparison.Ordinal)).ToList();
            if (recorded.Count == 0 || recorded.Any(b => b.VaultOwnerBalanceAtBid == null))
                return $"The vault's owner balance at the time of the bid was not recorded; complete the sale by hand after checking the vault (it holds {now} vBTC for its owner now).";
            var atBid = recorded.Max(b => b.VaultOwnerBalanceAtBid!.Value);
            return now.Value < atBid
                ? $"The vault's owner balance fell from {atBid} to {now} vBTC since the bid; the sale is not completed."
                : null;
        }

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
            VaultOwnerBalanceAtBid = VaultOwnerFigure(SmartContractUID);

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
        /// sessions), by purchase key only, and only when every shop holding that key names one contract. Listing ids
        /// repeat from shop to shop and a key can be copied: a bid sent to one shop was bound to another shop's contract,
        /// whose owner could then start a sale of it under the bid's signature (fifth review, round 31). Null when unknown.
        /// </summary>
        public static string? ListingContractFor(int listingId, string? purchaseKey)
        {
            try
            {
                if (string.IsNullOrEmpty(purchaseKey)) return null;
                var sources = new List<IEnumerable<Listing>?> { Globals.DecShopData?.Listings };
                foreach (var shop in Globals.MultiDecShopData.Values)
                    sources.Add(shop?.Listings);
                var contracts = sources.Where(s => s != null)
                    .SelectMany(s => s!.Where(l => l != null && string.Equals(l.PurchaseKey, purchaseKey, StringComparison.Ordinal)))
                    .Select(l => l.SmartContractUID).Where(uid => !string.IsNullOrWhiteSpace(uid))
                    .Distinct(StringComparer.Ordinal).ToList();
                return contracts.Count == 1 ? contracts[0] : null;
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
