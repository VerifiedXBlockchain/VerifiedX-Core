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
        /// The caster set whose votes count for <paramref name="height"/>. This feeds CONSENSUS
        /// validation, so it must be identical on every node: the signed, hash-chained membership
        /// record governing that height, or — for heights the record era does not cover — the
        /// hard-coded seed allowlist. The live BlockCasters / KnownCasters views are node-local and
        /// mutable and must never decide block validity.
        /// </summary>
        public static HashSet<string> GetCommitteeForHeight(long height)
        {
            try
            {
                var rec = CasterMembershipStore.GetCommitteeForHeight(height);
                if (rec != null && rec.Count > 0) return rec;
            }
            catch { }

            return SeedCommitteeForNetwork(Globals.IsTestNet);
        }

        /// <summary>
        /// The hard-coded seed allowlist holds both networks' casters (mainnet addresses begin with
        /// 'R', testnet with 'x'). The fallback committee must be THIS network's seeds only, or the
        /// majority threshold would count casters that cannot exist on this chain.
        /// </summary>
        public static HashSet<string> SeedCommitteeForNetwork(bool isTestNet)
        {
            var prefix = isTestNet ? 'x' : 'R';
            return new HashSet<string>(Globals.BootstrapCasterAddresses.Where(a => !string.IsNullOrEmpty(a) && a[0] == prefix), StringComparer.Ordinal);
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

        /// <summary>The span of time one handler election for a burn holds: a retry in a later span draws again.</summary>
        public const long ELECTION_WINDOW_SECONDS = 600;

        /// <summary>
        /// How far a proposal's timestamp may be from the receiver's clock. Tighter than an alert's: the window of the
        /// timestamp picks the draw, and a proposer may choose among the windows this skew reaches (at most two).
        /// </summary>
        public const long PROPOSAL_MAX_SKEW_SECONDS = 120;

        /// <summary>
        /// Deterministic handler-election hash for a burn: keccak256("{proposer}:{burn}:{window}"), the window that of
        /// the proposal's signed timestamp. Every caster can recompute it, so a proposer cannot pick an artificially low
        /// hash to win. Without the window one caster had the lowest hash for a burn in every retry: elected, it could
        /// leave the burn unpaid for as long as it sat on the committee (fifth review, round 23).
        /// </summary>
        public static string ComputeProposalHash(string proposerCasterAddress, string baseBurnTxHash, long timestamp)
        {
            var input = System.Text.Encoding.UTF8.GetBytes($"{proposerCasterAddress}:{baseBurnTxHash}:{Math.Max(0, timestamp) / ELECTION_WINDOW_SECONDS}");
            return Nethereum.Util.Sha3Keccack.Current.CalculateHashFromHex(Convert.ToHexString(input));
        }

        public static string BuildProposalMessage(string baseBurnTxHash, string proposerCasterAddress, string proposerHash, long timestamp) =>
            $"VFX_BURN_PROPOSAL|{baseBurnTxHash}|{proposerCasterAddress}|{proposerHash}|{timestamp}";

        /// <summary>
        /// A handler proposal is accepted only from a committee caster, with the deterministic hash
        /// for (proposer, burn), a fresh timestamp and a valid signature. Unauthenticated proposals
        /// let anyone inject the lowest hash under an address that will never execute, stalling exits.
        /// </summary>
        public static (bool Ok, string Reason) VerifyProposal(string baseBurnTxHash, string proposerCasterAddress, string proposerHash,
            long timestamp, string signature, HashSet<string> committee, long nowSeconds)
        {
            if (string.IsNullOrWhiteSpace(baseBurnTxHash) || string.IsNullOrWhiteSpace(proposerCasterAddress)) return (false, "burn and proposer required");
            if (string.IsNullOrWhiteSpace(signature)) return (false, "signature required");
            if (committee == null || !committee.Contains(proposerCasterAddress)) return (false, "proposer is not a committee caster");
            if (!string.Equals(ComputeProposalHash(proposerCasterAddress, baseBurnTxHash, timestamp), proposerHash, StringComparison.OrdinalIgnoreCase))
                return (false, "proposal hash is not the deterministic hash for this proposer and burn");
            if (Math.Abs(nowSeconds - timestamp) > PROPOSAL_MAX_SKEW_SECONDS) return (false, "proposal timestamp out of range");
            var msg = BuildProposalMessage(baseBurnTxHash, proposerCasterAddress, proposerHash, timestamp);
            if (!VerifiedXCore.Services.SignatureService.VerifySignature(proposerCasterAddress, msg, signature)) return (false, "invalid proposal signature");
            return (true, "");
        }

        /// <summary>
        /// Lowest deterministic hash among COMMITTEE proposers wins. Non-committee entries never win.
        /// Returns null when no committee proposer is present.
        /// </summary>
        public static string? SelectHandler(IEnumerable<(string Proposer, string Hash)> proposals, HashSet<string> committee)
        {
            if (proposals == null || committee == null) return null;
            return proposals
                .Where(p => !string.IsNullOrEmpty(p.Proposer) && committee.Contains(p.Proposer))
                .OrderBy(p => p.Hash, StringComparer.OrdinalIgnoreCase)
                .Select(p => p.Proposer)
                .FirstOrDefault();
        }

        /// <summary>Confirmation-specific message: binds WHICH handler the caster agreed to.</summary>
        public static string BuildConfirmationHandlerMessage(string baseBurnTxHash, string confirmingCaster, string agreedHandler, long timestamp) =>
            $"VFX_BURN_CONFIRM|{baseBurnTxHash}|{confirmingCaster}|{agreedHandler}|{timestamp}";

        /// <summary>
        /// The bound vote signature covers burn/type/amount/destination but not the handler the
        /// caster agreed to; without this second signature an observer could replay a caster's real
        /// vote under a junk handler and drop it from the count.
        /// </summary>
        public static (bool Ok, string Reason) VerifyConfirmationHandler(string confirmingCaster, string baseBurnTxHash, string agreedHandler,
            long timestamp, string handlerSignature, long nowSeconds)
        {
            if (string.IsNullOrWhiteSpace(agreedHandler)) return (false, "agreed handler required");
            if (string.IsNullOrWhiteSpace(handlerSignature)) return (false, "handler signature required");
            if (Math.Abs(nowSeconds - timestamp) > ALERT_MAX_SKEW_SECONDS) return (false, "confirmation timestamp out of range");
            var msg = BuildConfirmationHandlerMessage(baseBurnTxHash, confirmingCaster, agreedHandler, timestamp);
            if (!VerifiedXCore.Services.SignatureService.VerifySignature(confirmingCaster, msg, handlerSignature)) return (false, "invalid handler signature");
            return (true, "");
        }

        public const string VoteListedTwiceReason = "a caster's vote is listed more than once";

        /// <summary>Tests only: how many vote signatures the two committee vote checks have verified.</summary>
        internal static long VoteSignatureChecksForTests;

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
        /// A signature is checked only while it can still change the count: not for a caster already counted, and not
        /// once enough are. <paramref name="eachCasterOnce"/> (admission, and blocks from
        /// Globals.BridgeVoteOncePerCasterHeight) also refuses a list in which a caster has more than one vote to check,
        /// so a list costs at most one signature check a committee member.
        /// </summary>
        public static (bool Ok, string Reason) TryVerifyVotesFromCommittee(IEnumerable<CasterConsensusVote>? votes, string baseBurnTxHash, string burnType, long height, bool eachCasterOnce = false)
        {
            if (votes == null) return (false, "no votes");
            var committee = GetCommitteeForHeight(height);
            if (committee.Count == 0) return (false, "caster committee unavailable");
            var required = RequiredVotesFor(committee);

            var ok = new HashSet<string>(StringComparer.Ordinal);
            var listed = eachCasterOnce ? new HashSet<string>(StringComparer.Ordinal) : null;
            foreach (var v in votes)
            {
                if (ok.Count >= required) break;
                if (v == null || string.IsNullOrEmpty(v.CasterAddress) || string.IsNullOrEmpty(v.Signature)) continue;
                if (!committee.Contains(v.CasterAddress)) continue;
                if (!string.Equals(v.BaseBurnTxHash, baseBurnTxHash, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(v.BurnType, burnType, StringComparison.Ordinal)) continue;
                if (listed != null && !listed.Add(v.CasterAddress)) return (false, VoteListedTwiceReason);
                if (ok.Contains(v.CasterAddress)) continue;
                var msg = BuildVoteMessage(v.BaseBurnTxHash, v.BurnType, v.Timestamp);
                Interlocked.Increment(ref VoteSignatureChecksForTests);
                if (!VerifiedXCore.Services.SignatureService.VerifySignature(v.CasterAddress, msg, v.Signature)) continue;
                ok.Add(v.CasterAddress);
            }
            return ok.Count >= required ? (true, "") : (false, $"{ok.Count}/{required} valid committee votes");
        }

        /// <summary>
        /// What a vote must also meet to count for one attempt at a burn's redemption (from
        /// Globals.BridgeVotesBoundToAttemptHeight): it names <paramref name="Submitter"/> as the handler its caster agreed
        /// to, under that caster's handler signature, and its timestamp is within [<paramref name="NotBefore"/>,
        /// <paramref name="NotAfter"/>].
        /// </summary>
        public sealed record VoteAttempt(string Submitter, long NotBefore, long NotAfter);

        /// <summary>
        /// The oldest a counted vote may be against its block's timestamp: the age a transaction may have, and the margin a
        /// block's timestamp is allowed (LedgerIntegrityRules.TimestampOutsideItsBlock). A caster builds its transaction
        /// right after its round, so an honest vote is about as old as the transaction that carries it.
        /// </summary>
        public const long VoteMaxAgeSeconds = Globals.MaxTxAgeSeconds + LedgerIntegrityRules.BlockTimeMarginSeconds;

        /// <summary>
        /// How much stricter admission is than a block on a vote's age: a transaction admitted with its votes just inside
        /// the limit is not refused in the block that carries it, and block building drops one that has aged out.
        /// </summary>
        public const long VoteAdmissionMarginSeconds = LedgerIntegrityRules.BlockTimeMarginSeconds;

        /// <summary>
        /// The attempt the votes of a transaction from <paramref name="submitter"/> must be for, judged at
        /// <paramref name="height"/>; null below Globals.BridgeVotesBoundToAttemptHeight (nothing more is asked). In a block
        /// the ages are taken from the block's timestamp (<paramref name="blockTime"/>); a block judged without one (a local
        /// rescan) is held to the handler and to the earlier attempt only. At admission (<paramref name="blockHeight"/>
        /// null) they are taken from this node's clock, the age limit <see cref="VoteAdmissionMarginSeconds"/> shorter and
        /// the skew allowance without the block margin. When <paramref name="failedExitBurn"/> has an exit that failed and
        /// was recorded with its block (ExitVfxHeight), a counted vote is newer than that block: the earlier attempt's votes
        /// were given before it. Refusal is set when that block cannot be read.
        /// </summary>
        public static (VoteAttempt? Attempt, string? Refusal) AttemptFor(string submitter, string? failedExitBurn, long height, long? blockHeight, long? blockTime)
        {
            if (height < Globals.BridgeVotesBoundToAttemptHeight) return (null, null);

            var notBefore = long.MinValue;
            if (!string.IsNullOrWhiteSpace(failedExitBurn)
                && VBTCBridgeBtcExitState.GetByBurnHash(failedExitBurn) is { IsFailedRecord: true, ExitVfxHeight: > 0 } failed)
            {
                var earlier = VerifiedXCore.Data.BlockchainData.GetBlockByHeight(failed.ExitVfxHeight);
                if (earlier == null)
                    return (null, $"The block of this burn's earlier exit ({failed.ExitVfxHeight}) could not be read.");
                notBefore = earlier.Timestamp + 1;
            }

            var notAfter = long.MaxValue;
            if (blockHeight == null)
            {
                var now = VerifiedXCore.Utilities.TimeUtil.GetTime();
                notBefore = Math.Max(notBefore, now - (VoteMaxAgeSeconds - VoteAdmissionMarginSeconds));
                notAfter = now + Globals.MaxFutureSkewSeconds;
            }
            else if (blockTime != null)
            {
                notBefore = Math.Max(notBefore, blockTime.Value - VoteMaxAgeSeconds);
                notAfter = blockTime.Value + Globals.MaxFutureSkewSeconds + LedgerIntegrityRules.BlockTimeMarginSeconds;
            }
            return (new VoteAttempt(submitter ?? string.Empty, notBefore, notAfter), null);
        }

        /// <summary>
        /// Bound verification: signer must be in the committee for <paramref name="height"/>, and the
        /// signature must cover the burn hash, burn type, burned amount (sats) and payout destination
        /// exactly as carried by the transaction being validated.
        /// A signature is checked only while it can still change the count, and <paramref name="eachCasterOnce"/>
        /// (admission, and blocks from the height) refuses a list in which a caster has more than one vote to check
        /// (see <see cref="TryVerifyVotesFromCommittee"/>).
        /// With <paramref name="attempt"/> (see <see cref="AttemptFor"/>) a vote counts only when it is timestamped within
        /// the attempt's bounds and names the attempt's submitter as its handler under its caster's handler signature.
        /// </summary>
        public static (bool Ok, string Reason) VerifyBoundVotes(IEnumerable<CasterConsensusVote>? votes, string baseBurnTxHash, string burnType,
            long amountSats, string destination, long height, bool eachCasterOnce = false, VoteAttempt? attempt = null)
        {
            if (votes == null) return (false, "no votes");
            if (amountSats <= 0) return (false, "amount must be positive");
            if (string.IsNullOrWhiteSpace(destination)) return (false, "destination required");

            var committee = GetCommitteeForHeight(height);
            if (committee.Count == 0) return (false, "caster committee unavailable");
            var required = RequiredVotesFor(committee);

            var ok = new HashSet<string>(StringComparer.Ordinal);
            var listed = eachCasterOnce ? new HashSet<string>(StringComparer.Ordinal) : null;
            int outsideTime = 0, otherHandler = 0;
            foreach (var v in votes)
            {
                if (ok.Count >= required) break;
                if (v == null || string.IsNullOrEmpty(v.CasterAddress) || string.IsNullOrEmpty(v.Signature)) continue;
                if (!committee.Contains(v.CasterAddress)) continue;
                if (!string.Equals(v.BaseBurnTxHash, baseBurnTxHash, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(v.BurnType, burnType, StringComparison.Ordinal)) continue;
                if (listed != null && !listed.Add(v.CasterAddress)) return (false, VoteListedTwiceReason);
                if (ok.Contains(v.CasterAddress)) continue;
                if (attempt != null)
                {
                    if (v.Timestamp < attempt.NotBefore || v.Timestamp > attempt.NotAfter) { outsideTime++; continue; }
                    if (string.IsNullOrEmpty(v.HandlerSignature) || !string.Equals(v.AgreedHandler, attempt.Submitter, StringComparison.Ordinal)) { otherHandler++; continue; }
                }
                var msg = BuildBoundVoteMessage(v.BaseBurnTxHash, v.BurnType, amountSats, destination, v.Timestamp);
                Interlocked.Increment(ref VoteSignatureChecksForTests);
                if (!VerifiedXCore.Services.SignatureService.VerifySignature(v.CasterAddress, msg, v.Signature)) continue;
                if (attempt != null
                    && !VerifiedXCore.Services.SignatureService.VerifySignature(v.CasterAddress,
                        BuildConfirmationHandlerMessage(v.BaseBurnTxHash, v.CasterAddress, v.AgreedHandler!, v.Timestamp), v.HandlerSignature!))
                {
                    otherHandler++;
                    continue;
                }
                ok.Add(v.CasterAddress);
            }
            if (ok.Count >= required) return (true, "");
            var why = $"{ok.Count}/{required} valid bound committee votes";
            if (outsideTime > 0) why += $"; {outsideTime} {VoteOutsideAttemptTimeReason}";
            if (otherHandler > 0) why += $"; {otherHandler} {VoteForOtherHandlerReason}";
            return (false, why);
        }

        /// <summary>Why a vote did not count: its timestamp is outside what the attempt allows.</summary>
        public const string VoteOutsideAttemptTimeReason = "timestamped outside the time this attempt allows";

        /// <summary>Why a vote did not count: it does not name the transaction's sender as its handler under its caster's signature.</summary>
        public const string VoteForOtherHandlerReason = "not naming the sender as the handler its caster agreed to";
    }
}
