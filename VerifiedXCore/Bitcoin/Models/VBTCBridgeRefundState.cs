using LiteDB;
using VerifiedXCore.Data;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Bitcoin.Models
{
    /// <summary>
    /// A Base burn, or a part of one, that VFX records it cannot pay: the validators sign it for the vBTCb contract's
    /// refundBurn(to, amount, burnId), which mints it back once. Written only by block application (from
    /// Globals.BridgeBurnRefundHeight), wiped and replayed with the rest of the bridge state, and kept in state snapshots.
    /// </summary>
    public class VBTCBridgeRefundState
    {
        /// <summary>
        /// The id the contract refunds under, exactly as signed: the burn's id as BaseBridgeService.BurnId names it, in its
        /// one spelling (BaseBridgeService.CanonicalBurnId).
        /// </summary>
        [BsonId]
        public string RefundId { get; set; } = string.Empty;
        /// <summary>The burn the refund is for, as RefundId names it.</summary>
        public string BaseBurnTxHash { get; set; } = string.Empty;
        /// <summary>The Base address that burned, in lower case: the refund's recipient.</summary>
        public string BaseBurner { get; set; } = string.Empty;
        public long AmountSats { get; set; }
        public string Reason { get; set; } = string.Empty;
        public string VfxTxHash { get; set; } = string.Empty;
        public long VfxHeight { get; set; }

        public const string CollectionName = "rsrv_vbtc_bridge_refunds";

        /// <summary>The refund of a burn VFX cannot pay at all: it is below the smallest part a vault pays in an exit.</summary>
        public const string BelowSmallestExitReason = "BELOW_SMALLEST_EXIT";

        /// <summary>
        /// The refund of a burn whose exit carried the holder's least payout and was failed by rule (Globals.ExitMinPayoutHeight):
        /// no transaction could pay it at that minimum. The failed exit record is the evidence; the whole burn is refunded.
        /// </summary>
        public const string MinPayoutExitFailedReason = "MIN_PAYOUT_EXIT_FAILED";

        /// <summary>
        /// A part of an exit that its vault could not pay after its transaction was voided while another part was paid
        /// (Globals.ExitPartRefundHeight): written by the chain, not by a transaction, under "{burn}#{vault}".
        /// </summary>
        public const string ExitPartUnpaidReason = "EXIT_PART_UNPAID";

        /// <summary>The id of the refund of one vault's part of the exit for a burn.</summary>
        public static string PartRefundId(string burnId, string scUID) =>
            $"{VerifiedXCore.Bitcoin.Services.BaseBridgeService.CanonicalBurnId(burnId) ?? (burnId ?? "").Trim()}#{scUID}";

        public static ILiteCollection<VBTCBridgeRefundState> GetCollection() =>
            DbContext.DB_VBTCWithdrawalRequests.GetCollection<VBTCBridgeRefundState>(CollectionName);

        /// <summary>The refund recorded under exactly this id, or null.</summary>
        public static VBTCBridgeRefundState? Get(string? refundId)
        {
            if (string.IsNullOrEmpty(refundId)) return null;
            try { return GetCollection().FindById(refundId); }
            catch { return null; }
        }

        /// <summary>Whether a refund of the whole burn is recorded under any spelling of it (or of the transaction's hash, VBTCBridgeConsumedBurn.Overlaps).</summary>
        public static bool IsBurnRefunded(string? burnId)
        {
            var n = VBTCBridgeConsumedBurn.Normalize(burnId);
            if (n.Length == 0) return false;
            try { return GetCollection().FindAll().Any(r => r.RefundId == r.BaseBurnTxHash && VBTCBridgeConsumedBurn.Overlaps(VBTCBridgeConsumedBurn.Normalize(r.BaseBurnTxHash), n)); }
            catch { return false; }
        }

        /// <summary>Records the refund unless one is recorded under its id. A storage fault stops the node as a failed apply.</summary>
        public static bool TryInsert(VBTCBridgeRefundState refund)
        {
            try
            {
                var c = GetCollection();
                if (c.FindById(refund.RefundId) != null) return false;
                c.Insert(refund);
                return true;
            }
            catch (Exception ex)
            {
                StateData.NoteApplyStepFailed(StateWriteContext.StampHeight, ex);
                ErrorLogUtility.LogError($"TryInsert: refund {refund.RefundId} not written: {ex.Message}", "VBTCBridgeRefundState.TryInsert");
                return false;
            }
        }
    }
}
