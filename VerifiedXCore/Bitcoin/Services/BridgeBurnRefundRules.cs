using Newtonsoft.Json.Linq;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Bitcoin.Services
{
    /// <summary>
    /// What a <see cref="TransactionType.VBTC_V2_BRIDGE_BURN_REFUND"/> is, and when it is accepted (from
    /// Globals.BridgeBurnRefundHeight): a committee caster records, with the committee's votes bound to the burn, the
    /// refunded satoshis and the burner, a Base burn VFX cannot pay and that nothing has redeemed or refunded. Its apply
    /// (StateData) records a <see cref="VBTCBridgeRefundState"/> and consumes the burn, so no exit can follow it.
    /// </summary>
    public static class BridgeBurnRefundRules
    {
        public const string Function = "VBTCBridgeBurnRefund()";

        public const string NotYetReason = "A bridge burn refund is accepted only from Globals.BridgeBurnRefundHeight.";

        /// <summary>A refund as its transaction carries it.</summary>
        public sealed record Refund(string BurnId, string Burner, long AmountSats, string Reason, List<CasterConsensusVote> Votes);

        /// <summary>Whether a Base address is written as the refund carries one: "0x" and 40 hex digits in lower case.</summary>
        public static bool IsBurnerAddress(string? address) =>
            address != null && address.Length == 42 && address.StartsWith("0x", StringComparison.Ordinal)
            && address.Skip(2).All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));

        /// <summary>The refund a transaction carries, or why it carries none that can be judged.</summary>
        public static (Refund? Refund, string? Refusal) Read(Transaction tx, long height)
        {
            if (string.IsNullOrWhiteSpace(tx.Data)) return (null, "A bridge burn refund carries data.");
            JObject jobj;
            try { jobj = ConsensusData.ParseObject(tx.Data, height); }
            catch { return (null, "A bridge burn refund's data does not parse."); }
            if (jobj["Function"]?.Type != JTokenType.String || jobj["Function"]!.ToObject<string>() != Function)
                return (null, "Invalid Function for a bridge burn refund.");
            if (jobj["BaseBurnTxHash"]?.Type != JTokenType.String || jobj["BaseBurner"]?.Type != JTokenType.String
                || jobj["AmountSats"]?.Type != JTokenType.Integer || jobj["Reason"]?.Type != JTokenType.String)
                return (null, "A bridge burn refund carries BaseBurnTxHash, BaseBurner, AmountSats and Reason.");
            if (jobj["CasterConsensusVotes"] is not JArray votesToken)
                return (null, "A bridge burn refund requires a CasterConsensusVotes array.");
            long amountSats;
            List<CasterConsensusVote>? votes;
            try
            {
                amountSats = jobj["AmountSats"]!.ToObject<long>();
                votes = ConsensusData.ToObject<List<CasterConsensusVote>>(votesToken, height);
            }
            catch { return (null, "A bridge burn refund's amount or votes do not read."); }
            return (new Refund(jobj["BaseBurnTxHash"]!.ToObject<string>()!, jobj["BaseBurner"]!.ToObject<string>()!, amountSats,
                jobj["Reason"]!.ToObject<string>()!, votes ?? new List<CasterConsensusVote>()), null);
        }

        /// <summary>
        /// Why the refund's reason does not hold for its burn, or null. <see cref="VBTCBridgeRefundState.BelowSmallestExitReason"/>:
        /// the burn is below the smallest part a vault pays in an exit, so no exit of it can be paid.
        /// </summary>
        public static string? ReasonRefusal(Refund refund, long height)
        {
            if (refund.Reason == VBTCBridgeRefundState.BelowSmallestExitReason)
                return refund.AmountSats < Attestation.BtcWithdrawals.MinExitPartSats
                    ? null
                    : $"A burn of {refund.AmountSats} sats can be paid by an exit (the smallest is {Attestation.BtcWithdrawals.MinExitPartSats}); it is not refunded.";
            if (refund.Reason == VBTCBridgeRefundState.MinPayoutExitFailedReason)
            {
                if (height < Globals.ExitMinPayoutHeight) return "A failed exit with a minimum payout is refunded only from Globals.ExitMinPayoutHeight.";
                var failed = FailedMinPayoutExit(refund.BurnId);
                if (failed == null) return "This burn has no exit that carried a minimum payout and was failed by rule.";
                if (failed.AmountSats != refund.AmountSats) return $"The failed exit was for {failed.AmountSats} sats; the whole burn is refunded.";
                if (!string.IsNullOrEmpty(failed.BaseBurner) && !string.Equals(failed.BaseBurner, refund.Burner, StringComparison.OrdinalIgnoreCase))
                    return $"The failed exit's burner was {failed.BaseBurner}; its burn is refunded to that address.";
                return null;
            }
            return $"A bridge burn is not refunded for the reason \"{refund.Reason}\".";
        }

        /// <summary>The exit recorded for the burn when it carried the holder's least payout and was failed by rule (expired), else null.</summary>
        public static VBTCBridgeBtcExitState? FailedMinPayoutExit(string burnId) =>
            VBTCBridgeBtcExitState.GetByBurnHash(burnId) is { IsFailedRecord: true, MinPayoutSats: > 0 } failed
            && (failed.BtcTxHash ?? "").StartsWith(VBTCBridgeBtcExitState.FailedMarkerPrefix + "expired", StringComparison.Ordinal)
                ? failed : null;

        /// <summary>
        /// Why the transaction is not a refund this chain takes at <paramref name="height"/>, or null. In a block
        /// (<paramref name="blockHeight"/> set) everything judged is the chain's own state and the votes; at admission the
        /// burn is also read on Base, and must be a Bitcoin exit burn of the refunded amount by the refunded address.
        /// </summary>
        public static async Task<string?> RefusalAsync(Transaction tx, long height, long? blockHeight, long? blockTime)
        {
            if (height < Globals.BridgeBurnRefundHeight) return NotYetReason;
            if (tx.Fee != 0M) return "A bridge burn refund is fee-free (Fee must equal 0).";
            if (tx.Amount != 0M) return "A bridge burn refund carries no VFX amount.";
            if (tx.FromAddress != tx.ToAddress) return "A bridge burn refund is a self-transaction (from == to).";

            var (refund, readRefusal) = Read(tx, height);
            if (refund == null) return readRefusal;

            // One spelling: the contract refunds a burn once by the exact bytes of its id, so the id must be the one Core
            // names the burn by.
            if (!VerifiedXCore.Services.TransactionValidatorService.IsValidBurnId(refund.BurnId, height)
                || BaseBridgeService.CanonicalBurnId(refund.BurnId) != refund.BurnId)
                return "A bridge burn refund names its burn by its id in its one spelling (\"0x\", 64 lower-case hex digits, \":n\" for one of several).";
            if (refund.AmountSats <= 0) return "A bridge burn refund refunds a positive amount.";
            if (!IsBurnerAddress(refund.Burner)) return "A bridge burn refund's BaseBurner is a Base address (\"0x\" and 40 lower-case hex digits).";
            if (ReasonRefusal(refund, height) is { } reasonRefusal) return reasonRefusal;

            var (subOk, subReason) = BridgeTxRules.CheckSubmitter(tx.FromAddress, BridgeCasterConsensus.GetCommitteeForHeight(height));
            if (!subOk) return subReason;

            if (VBTCBridgeConsumedBurn.IsBurnUsedAnywhere(refund.BurnId) || VBTCBridgeRefundState.IsBurnRefunded(refund.BurnId))
                return "This Base burn is already redeemed, held by an exit or refunded.";

            var (attempt, attemptRefusal) = BridgeCasterConsensus.AttemptFor(tx.FromAddress, refund.BurnId, height, blockHeight, blockTime);
            if (attemptRefusal != null) return attemptRefusal;
            var (votesOk, votesReason) = BridgeCasterConsensus.VerifyBoundVotes(refund.Votes, refund.BurnId, BridgeCasterConsensus.RefundBurnType,
                refund.AmountSats, refund.Burner, height, eachCasterOnce: true, attempt: attempt,
                voteMessage: v => BridgeCasterConsensus.BuildRefundVoteMessage(v.BaseBurnTxHash, refund.AmountSats, refund.Burner, v.Timestamp));
            if (!votesOk) return $"Caster consensus votes rejected for the bridge burn refund: {votesReason}";

            // Admission only: a block is judged on the committee's votes, never on a live answer from Base.
            if (BridgeBurnEvidencePolicy.ShouldQueryBase(blockHeight, BaseBridgeService.IsBridgeConfigured))
            {
                var (evOk, ev, evReason) = await BaseBridgeService.TryGetBurnEventAsync(refund.BurnId, vfxExit: false);
                if (!evOk || ev == null) return $"Base burn transaction could not be verified: {evReason}";
                if (ev.AmountSats != refund.AmountSats) return "Base burn amount does not match the refunded amount.";
                if (!string.Equals((ev.Burner ?? "").Trim(), refund.Burner, StringComparison.OrdinalIgnoreCase))
                    return "Base burn's burner does not match the refunded address.";
            }
            return null;
        }
    }
}
