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
        /// (<see cref="HasLocalSentBid"/>). Filled by <see cref="Build"/> from the listing of the shop the bid is sent to
        /// when the client did not send it; null when that shop's data does not name it, and on rows from before this field.
        /// </summary>
        public string? SmartContractUID { get; set; }

        /// <summary>
        /// For a vBTC vault the chain watches: the owner's spendable vBTC when the bid was made. A sale is completed from
        /// this wallet's balance automatically when the seller starts it, and the seller could empty the vault's owner
        /// balance before (or with) its Sale_Start: the buyer paid in full for an empty vault (fifth review, round 30).
        /// </summary>
        public decimal? VaultOwnerBalanceAtBid { get; set; }

        /// <summary>
        /// Until when (unix seconds) a Sale_Start may complete this bid from the wallet automatically: the listing's end plus
        /// <see cref="AutoCompleteGraceSeconds"/>, or the bid time plus that grace when the end is unknown. A bid signature
        /// never expires and a cancelled sale does not use up its purchase key, so without a deadline the seller could start
        /// the sale under the old signature at any later time and the wallet paid. Null on rows from before this field:
        /// those use the bid time.
        /// </summary>
        public long? AutoCompleteUntil { get; set; }

        /// <summary>Whether a Sale_Start already completed this bid automatically. A bid pays automatically at most once.</summary>
        public bool? AutoCompleted { get; set; }

        /// <summary>How long after a listing ends (or, when its end is unknown, after the bid) a sale is completed automatically.</summary>
        public const long AutoCompleteGraceSeconds = 3 * 24 * 60 * 60;

        internal long AutoCompleteDeadline => AutoCompleteUntil ?? BidSendTime + AutoCompleteGraceSeconds;

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

        /// <param name="targetShop">The data of the shop this bid is sent to; the contract is read from it alone.</param>
        public bool Build(bool thirdParty = false, DecShopData? targetShop = null)
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
                SmartContractUID = ListingContractFor(targetShop, ListingId, PurchaseKey);
            VaultOwnerBalanceAtBid = VaultOwnerFigure(SmartContractUID);
            AutoCompleteUntil = Math.Max(ListingEndFor(PurchaseKey) ?? BidSendTime, BidSendTime) + AutoCompleteGraceSeconds;

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
            lock (BidWriteLock)
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
                        // A bid a sale already completed automatically stays used: a caller holding a copy read before
                        // that would otherwise save it back unused.
                        if (singleBid.AutoCompleted == true)
                            bid.AutoCompleted = true;
                        bidDb.UpdateSafe(bid);
                        return (true, "Bid updated.");
                    }
                }
                return (false, "Bid DB was null.");
            }
        }

        /// <summary>
        /// The owner resending a bid authorises it again: a rejected bid is Sent once more and its auto-complete window runs
        /// from now. Only this owner action reopens a rejected bid; a shop response cannot (<see cref="ApplyShopResponse"/>).
        /// A bid a sale already completed automatically stays used.
        /// </summary>
        public static Bid? ReopenForResend(Guid bidId)
        {
            lock (BidWriteLock)
            {
                var bid = GetSingleBid(bidId);
                if (bid == null || bid.BidSendReceive != BidSendReceive.Sent)
                    return bid;
                bid.BidStatus = BidStatus.Sent;
                bid.BidSendTime = TimeUtil.GetTime();
                bid.AutoCompleteUntil = Math.Max(ListingEndFor(bid.PurchaseKey) ?? bid.BidSendTime, bid.BidSendTime) + AutoCompleteGraceSeconds;
                GetBidDb()?.UpdateSafe(bid);
                return bid;
            }
        }

        /// <summary>Serialises bid writes, so a status update and <see cref="MarkAutoCompleted"/> cannot interleave.</summary>
        private static readonly object BidWriteLock = new();

        /// <summary>
        /// Applies a shop's response to a bid this wallet sent. The response is the shop's word and is not authenticated,
        /// so it can change only the status of a sent bid, and a rejected bid stays rejected: otherwise a response naming
        /// it made it payable again by <see cref="HasLocalSentBid"/>.
        /// </summary>
        public static void ApplyShopResponse(Guid bidId, BidStatus status)
        {
            lock (BidWriteLock)
            {
                var bid = GetSingleBid(bidId);
                if (bid == null || bid.BidSendReceive != BidSendReceive.Sent || bid.BidStatus == BidStatus.Rejected)
                    return;
                bid.BidStatus = status;
                GetBidDb()?.UpdateSafe(bid);
            }
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
                var now = TimeUtil.GetTime();
                return MatchingSentBids(bidderAddress, purchaseKey, smartContractUID).Any(b =>
                    Math.Max(b.BidAmount, b.MaxBidAmount) >= amount
                    && b.BidStatus != BidStatus.Rejected
                    && b.AutoCompleted != true
                    && now <= b.AutoCompleteDeadline);
            }
            catch { return false; }
        }

        /// <summary>
        /// Records that a sale completed this wallet's bids on <paramref name="purchaseKey"/> automatically, so a later
        /// Sale_Start under the same bid signature (after a cancel) is not paid automatically again.
        /// </summary>
        public static void MarkAutoCompleted(string? bidderAddress, string? purchaseKey, string? smartContractUID)
        {
            lock (BidWriteLock)
            {
                var db = GetBidDb();
                if (db == null) return;
                foreach (var b in MatchingSentBids(bidderAddress, purchaseKey, smartContractUID))
                {
                    b.AutoCompleted = true;
                    db.UpdateSafe(b);
                }
            }
        }

        private static List<Bid> MatchingSentBids(string? bidderAddress, string? purchaseKey, string? smartContractUID) =>
            (GetAllBids(BidSendReceive.Sent) ?? new List<Bid>()).Where(b => b != null
                && string.Equals(b.BidAddress, bidderAddress, StringComparison.Ordinal)
                && string.Equals(b.PurchaseKey, purchaseKey, StringComparison.Ordinal)
                && string.Equals(b.SmartContractUID, smartContractUID, StringComparison.Ordinal)) // no fallback: shop data is the seller's (re-audit)
                .ToList();

        /// <summary>
        /// The end (unix seconds) of the auction listed under <paramref name="purchaseKey"/>, from the shop data this node
        /// holds, when every shop holding that key names one end; null for a buy-now-only listing or when unknown.
        /// </summary>
        public static long? ListingEndFor(string? purchaseKey)
        {
            try
            {
                if (string.IsNullOrEmpty(purchaseKey)) return null;
                var sources = new List<IEnumerable<Listing>?> { Globals.DecShopData?.Listings };
                foreach (var shop in Globals.MultiDecShopData.Values)
                    sources.Add(shop?.Listings);
                var listings = sources.Where(s => s != null)
                    .SelectMany(s => s!.Where(l => l != null && string.Equals(l.PurchaseKey, purchaseKey, StringComparison.Ordinal)))
                    .ToList();
                if (listings.Count == 0 || listings.Any(l => l.IsBuyNowOnly)) return null;
                var ends = listings.Select(l => new DateTimeOffset(DateTime.SpecifyKind(l.EndDate, DateTimeKind.Utc)).ToUnixTimeSeconds()).Distinct().ToList();
                return ends.Count == 1 ? ends[0] : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// The contract a listing sells, from the data of the shop the bid is sent to only: the listing with this id and
        /// this purchase key there, when it names one contract. Any other shop's data is the other shop's to serve: read
        /// from every shop this node had cached, a shop that served a listing with the key the buyer bid with bound the bid
        /// to its own contract, whose owner could then start a sale of it under the bid's signature. Null when unknown,
        /// and a bid without a contract is never completed automatically.
        /// </summary>
        public static string? ListingContractFor(DecShopData? targetShop, int listingId, string? purchaseKey)
        {
            try
            {
                if (targetShop?.Listings == null || string.IsNullOrEmpty(purchaseKey)) return null;
                var contracts = targetShop.Listings
                    .Where(l => l != null && l.Id == listingId && string.Equals(l.PurchaseKey, purchaseKey, StringComparison.Ordinal))
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
