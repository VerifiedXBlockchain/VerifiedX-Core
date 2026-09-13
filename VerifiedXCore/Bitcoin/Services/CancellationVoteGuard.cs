using VerifiedXCore.Bitcoin.Models;

namespace VerifiedXCore.Bitcoin.Services
{
    /// <summary>
    /// A validator must never approve cancelling a withdrawal for which a Bitcoin transaction has
    /// already been signed. vBTC is only burned when the completion lands, so "sign, cancel, then
    /// broadcast the held BTC" would leave the user with both the vBTC and the BTC. The signing
    /// tracker (this validator's own share history) and the local withdrawal row (pinned build /
    /// last signed txid) are the evidence; either one blocks an approve vote.
    /// </summary>
    public static class CancellationVoteGuard
    {
        public static (bool Ok, string Reason) CanApprove(bool trackerHasSignedTransaction, VBTCWithdrawalRequest? localRow)
        {
            if (trackerHasSignedTransaction)
                return (false, "This validator has already contributed a signature share to a Bitcoin transaction for this withdrawal; cancelling now would double-pay.");

            if (localRow != null)
            {
                if (!string.IsNullOrWhiteSpace(localRow.LastSignedBtcTxId))
                    return (false, $"A signed Bitcoin transaction ({localRow.LastSignedBtcTxId}) already exists for this withdrawal; cancelling now would double-pay.");
                if (!string.IsNullOrWhiteSpace(localRow.PinnedUnsignedTxHex))
                    return (false, "A pinned (announced) Bitcoin transaction already exists for this withdrawal; cancelling now would double-pay.");
                if (localRow.IsCompleted || localRow.Status == VBTCWithdrawalStatus.Completed)
                    return (false, "Withdrawal already completed.");
            }

            return (true, "");
        }
    }
}
