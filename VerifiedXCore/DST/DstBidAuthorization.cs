using VerifiedXCore.Models.DST;

namespace VerifiedXCore.DST
{
    /// <summary>
    /// VX-10: a bid or buy-now arriving over UDP may change auction state only if its bidder signed the
    /// exact price the shop will commit — the same check on-chain settlement (Sale_Start) applies, so a bid
    /// the shop accepts is one that can actually settle.
    ///
    /// Before: datagrams were enqueued with no signature check and no check that the sender controls
    /// BidAddress; the balance check used BidAmount while the committed price was MaxBidAmount (unsigned);
    /// a forged buy-now ended any live auction. VerifyBidSignature existed but only settlement called it.
    /// </summary>
    public static class DstBidAuthorization
    {
        /// <summary>
        /// Returns null when the bid may be processed, else the rejection reason.
        /// </summary>
        /// <param name="committedPrice">The price the shop will record and later settle for: the bid's
        /// MaxBidAmount for a bid, the listing's BuyNowPrice for a buy-now.</param>
        public static string? Validate(BidQueue bid, Listing? listing, bool isBuyNow, out decimal committedPrice)
        {
            committedPrice = 0M;
            if (bid == null) return "no bid";
            if (listing == null) return "unknown listing";
            if (listing.IsCancelled || listing.IsAuctionEnded) return "listing closed";
            if (string.IsNullOrEmpty(bid.BidAddress) || string.IsNullOrEmpty(bid.BidSignature)) return "unsigned";
            if (string.IsNullOrEmpty(listing.PurchaseKey)) return "listing has no purchase key";

            if (isBuyNow)
            {
                if (listing.BuyNowPrice == null || listing.BuyNowPrice.Value <= 0) return "listing has no buy-now price";
                committedPrice = listing.BuyNowPrice.Value;
            }
            else
            {
                // The signature covers BidAmount only. MaxBidAmount is what the auction records and what
                // settlement signs over (FinalPrice = MaxBidPrice), so the two must be the same value.
                if (bid.BidAmount <= 0 || bid.MaxBidAmount != bid.BidAmount) return "max bid must equal the signed bid";
                committedPrice = bid.MaxBidAmount;
            }

            // The listing's own purchase key — never the one in the datagram.
            bool signed;
            try { signed = Bid.VerifyBidSignature(listing.PurchaseKey, committedPrice, bid.BidAddress, bid.BidSignature); }
            catch { signed = false; }
            return signed ? null : "signature does not cover this bidder, listing and price";
        }
    }
}
