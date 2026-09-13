using VerifiedXCore;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Services;

namespace VerifiedXCore.Bitcoin.Services
{
    /// <summary>
    /// Validates adaptive-majority caster votes for Base bridge exit / sync flows (VFX signatures).
    ///
    /// Two vote formats exist:
    ///  - Legacy: "VFX_BRIDGE_BURN|{burn}|{type}|{ts}" — signer identity unchecked (pre-gate only).
    ///  - Bound (at/after <see cref="Globals.BridgeBurnBindingHeight"/>):
    ///    "VFX_BRIDGE_BURN_V2|{burn}|{type}|{amountSats}|{destination}|{ts}" — the vote commits to
    ///    the burned amount and payout destination, and every counted signer must be a member of
    ///    the caster committee governing the block height being validated.
    /// </summary>
    public static class BridgeCasterConsensus
    {
        public static int RequiredCasterVotes => Math.Max(2, Globals.ActiveCasterCount / 2 + 1);

        public static string BuildVoteMessage(string baseBurnTxHash, string burnType, long timestamp) =>
            $"VFX_BRIDGE_BURN|{baseBurnTxHash}|{burnType}|{timestamp}";

        public static string BuildBoundVoteMessage(string baseBurnTxHash, string burnType, long amountSats, string destination, long timestamp) =>
            $"VFX_BRIDGE_BURN_V2|{baseBurnTxHash}|{burnType}|{amountSats}|{(destination ?? string.Empty).Trim()}|{timestamp}";

        /// <summary>
        /// The caster set whose votes count for <paramref name="height"/>: the signed membership
        /// record governing that height when the record era is active, otherwise the union of the
        /// seed allowlist and the currently known caster set.
        /// </summary>
        public static HashSet<string> GetCommitteeForHeight(long height)
        {
            try
            {
                var rec = CasterMembershipStore.GetCommitteeForHeight(height);
                if (rec != null && rec.Count > 0) return rec;
            }
            catch { }

            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var a in Globals.BootstrapCasterAddresses) set.Add(a);
            foreach (var p in Globals.BlockCasters)
                if (!string.IsNullOrEmpty(p.ValidatorAddress)) set.Add(p.ValidatorAddress);
            lock (Globals.KnownCasters)
            {
                foreach (var k in Globals.KnownCasters)
                    if (!string.IsNullOrEmpty(k.Address)) set.Add(k.Address);
            }
            return set;
        }

        public static int RequiredVotesFor(HashSet<string> committee) => Math.Max(2, committee.Count / 2 + 1);

        public const long ALERT_MAX_SKEW_SECONDS = 600;

        /// <summary>Message a caster signs when announcing a detected burn to its peers.</summary>
        public static string BuildAlertMessage(string baseBurnTxHash, string burnType, long amountSats, string destination, string senderCasterAddress, long timestamp) =>
            $"VFX_BURN_ALERT|{baseBurnTxHash}|{burnType}|{amountSats}|{(destination ?? string.Empty).Trim()}|{senderCasterAddress}|{timestamp}";

        /// <summary>
        /// A burn alert is accepted only from a committee member, with a fresh timestamp and a valid
        /// signature over every field that later flows into the vote. Unauthenticated alerts let
        /// anyone push a fabricated burn into every caster's consensus queue.
        /// </summary>
        public static (bool Ok, string Reason) VerifyBurnAlert(string baseBurnTxHash, string burnType, long amountSats, string destination,
            string senderCasterAddress, long timestamp, string signature, HashSet<string> committee, long nowSeconds)
        {
            if (string.IsNullOrWhiteSpace(baseBurnTxHash)) return (false, "burn hash required");
            if (string.IsNullOrWhiteSpace(senderCasterAddress)) return (false, "sender required");
            if (string.IsNullOrWhiteSpace(signature)) return (false, "signature required");
            if (amountSats <= 0) return (false, "amount must be positive");
            if (committee == null || !committee.Contains(senderCasterAddress)) return (false, "sender is not a committee caster");
            if (Math.Abs(nowSeconds - timestamp) > ALERT_MAX_SKEW_SECONDS) return (false, "alert timestamp out of range");
            var msg = BuildAlertMessage(baseBurnTxHash, burnType, amountSats, destination, senderCasterAddress, timestamp);
            if (!VerifiedXCore.Services.SignatureService.VerifySignature(senderCasterAddress, msg, signature)) return (false, "invalid alert signature");
            return (true, "");
        }

        /// <summary>
        /// A peer's confirmation is accepted only from a committee member whose signature is the
        /// bound vote (burn, type, amount, destination) for the burn as THIS caster recorded it.
        /// </summary>
        public static (bool Ok, string Reason) VerifyConfirmation(string confirmingCaster, string baseBurnTxHash, string burnType,
            long amountSats, string destination, long timestamp, string signature, HashSet<string> committee)
        {
            if (string.IsNullOrWhiteSpace(confirmingCaster) || string.IsNullOrWhiteSpace(signature)) return (false, "caster and signature required");
            if (committee == null || !committee.Contains(confirmingCaster)) return (false, "confirming caster is not a committee member");
            var msg = BuildBoundVoteMessage(baseBurnTxHash, burnType, amountSats, destination, timestamp);
            if (!VerifiedXCore.Services.SignatureService.VerifySignature(confirmingCaster, msg, signature)) return (false, "invalid confirmation signature");
            return (true, "");
        }

        /// <summary>
        /// The burn event read from Base must match what the caster is about to vote for exactly.
        /// </summary>
        public static (bool Ok, string Reason) BurnEvidenceMatches(BaseBridgeService.BurnEventInfo? evidence, long expectedSats, string expectedDestination)
        {
            if (evidence == null) return (false, "no burn evidence");
            if (evidence.AmountSats != expectedSats) return (false, $"burned amount {evidence.AmountSats} != expected {expectedSats}");
            if (!string.Equals((evidence.Destination ?? "").Trim(), (expectedDestination ?? "").Trim(), StringComparison.Ordinal))
                return (false, "burn destination does not match");
            return (true, "");
        }

        /// <summary>Legacy (pre-gate) verification: any VFX address with a valid signature counts.</summary>
        public static bool TryVerifyVotes(IEnumerable<CasterConsensusVote>? votes, string baseBurnTxHash, string burnType)
        {
            if (votes == null)
                return false;
            var ok = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in votes)
            {
                if (v == null || string.IsNullOrEmpty(v.CasterAddress)) continue;
                if (!string.Equals(v.BaseBurnTxHash, baseBurnTxHash, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!string.Equals(v.BurnType, burnType, StringComparison.Ordinal))
                    continue;
                var msg = BuildVoteMessage(v.BaseBurnTxHash, v.BurnType, v.Timestamp);
                if (string.IsNullOrEmpty(v.Signature)) continue;
                if (!VerifiedXCore.Services.SignatureService.VerifySignature(v.CasterAddress, msg, v.Signature))
                    continue;
                ok.Add(v.CasterAddress);
            }
            return ok.Count >= RequiredCasterVotes;
        }

        /// <summary>
        /// Legacy message format, but every counted signer must be a committee member for the height
        /// and the threshold is derived from the committee size. Used for the legacy single-lock
        /// VBTC_V2_BRIDGE_UNLOCK path (no producer emits bound votes for it).
        /// </summary>
        public static (bool Ok, string Reason) TryVerifyVotesFromCommittee(IEnumerable<CasterConsensusVote>? votes, string baseBurnTxHash, string burnType, long height)
        {
            if (votes == null) return (false, "no votes");
            var committee = GetCommitteeForHeight(height);
            if (committee.Count == 0) return (false, "caster committee unavailable");
            var required = RequiredVotesFor(committee);

            var ok = new HashSet<string>(StringComparer.Ordinal);
            foreach (var v in votes)
            {
                if (v == null || string.IsNullOrEmpty(v.CasterAddress) || string.IsNullOrEmpty(v.Signature)) continue;
                if (!committee.Contains(v.CasterAddress)) continue;
                if (!string.Equals(v.BaseBurnTxHash, baseBurnTxHash, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(v.BurnType, burnType, StringComparison.Ordinal)) continue;
                var msg = BuildVoteMessage(v.BaseBurnTxHash, v.BurnType, v.Timestamp);
                if (!VerifiedXCore.Services.SignatureService.VerifySignature(v.CasterAddress, msg, v.Signature)) continue;
                ok.Add(v.CasterAddress);
            }
            return ok.Count >= required ? (true, "") : (false, $"{ok.Count}/{required} valid committee votes");
        }

        /// <summary>
        /// Bound verification: signer must be in the committee for <paramref name="height"/>, and the
        /// signature must cover the burn hash, burn type, burned amount (sats) and payout destination
        /// exactly as carried by the transaction being validated.
        /// </summary>
        public static (bool Ok, string Reason) VerifyBoundVotes(IEnumerable<CasterConsensusVote>? votes, string baseBurnTxHash, string burnType,
            long amountSats, string destination, long height)
        {
            if (votes == null) return (false, "no votes");
            if (amountSats <= 0) return (false, "amount must be positive");
            if (string.IsNullOrWhiteSpace(destination)) return (false, "destination required");

            var committee = GetCommitteeForHeight(height);
            if (committee.Count == 0) return (false, "caster committee unavailable");
            var required = RequiredVotesFor(committee);

            var ok = new HashSet<string>(StringComparer.Ordinal);
            foreach (var v in votes)
            {
                if (v == null || string.IsNullOrEmpty(v.CasterAddress) || string.IsNullOrEmpty(v.Signature)) continue;
                if (!committee.Contains(v.CasterAddress)) continue;
                if (!string.Equals(v.BaseBurnTxHash, baseBurnTxHash, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(v.BurnType, burnType, StringComparison.Ordinal)) continue;
                var msg = BuildBoundVoteMessage(v.BaseBurnTxHash, v.BurnType, amountSats, destination, v.Timestamp);
                if (!VerifiedXCore.Services.SignatureService.VerifySignature(v.CasterAddress, msg, v.Signature)) continue;
                ok.Add(v.CasterAddress);
            }
            return ok.Count >= required ? (true, "") : (false, $"{ok.Count}/{required} valid bound committee votes");
        }
    }
}
