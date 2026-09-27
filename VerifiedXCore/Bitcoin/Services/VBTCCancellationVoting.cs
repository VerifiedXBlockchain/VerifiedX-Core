using Newtonsoft.Json.Linq;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Bitcoin.Services
{
    /// <summary>
    /// Consensus rules for cancelling a vBTC V2 withdrawal and refunding its escrow, from
    /// Globals.VbtcCancellationVoteRulesHeight. One set of rules for every vBTC V2 contract: a public contract's voters
    /// are its DKG snapshot (or the public validators when it has none); an S3C contract's voters are its own S3C
    /// snapshot. Validation (VerifyTX), block apply (StateData) and the store rebuild all call these methods, so they
    /// cannot drift apart.
    ///
    /// What a refund must never do is pay twice: the requester coordinates the Bitcoin signing, so it can hold a
    /// signed transaction back, cancel, collect the refund, and then broadcast. Counting approvals cannot rule that
    /// out - when most of a contract's validators are offline the signing quorum shrinks to as few as two - so the
    /// rule is evidence, not arithmetic:
    ///  - an approver states that it never released a signature share for the withdrawal (its node checks its own
    ///    signing records before it votes; see VBTCCancellationVoteService);
    ///  - a validator that did sign rejects and names the transaction, and that one vote rejects the cancellation:
    ///    the withdrawal is payable, so it is completed instead;
    ///  - while a cancellation is open, validators refuse to sign for the withdrawal (same window, in blocks).
    /// Everything here reads committed chain data only (blocks, state trei, the withdrawal and cancellation tables
    /// written while blocks are applied), so every node - and a node replaying history - reaches the same result.
    /// </summary>
    public static class VBTCCancellationVoting
    {
        /// <summary>Share of the active voters that must approve.</summary>
        public const int ApprovalPercent = 75;

        /// <summary>Approvals are never fewer than this, or than every active voter when fewer are active.</summary>
        public const int MinApprovals = 3;

        public enum Outcome { Pending, Approved, Rejected }

        public static bool RulesActive(long height) => height >= Globals.VbtcCancellationVoteRulesHeight;

        /// <summary>Approvals needed from <paramref name="activeVoters"/> voters; never reachable with none.</summary>
        public static int RequiredApprovals(int activeVoters)
        {
            if (activeVoters <= 0)
                return int.MaxValue;
            var byPercent = (activeVoters * ApprovalPercent + 99) / 100; // ceiling, integer math
            return Math.Max(byPercent, Math.Min(MinApprovals, activeVoters));
        }

        /// <summary>A Bitcoin transaction id: 64 hex characters.</summary>
        public static bool IsBtcTxId(string? value) =>
            !string.IsNullOrEmpty(value) && value.Length == 64 && value.All(Uri.IsHexDigit);

        /// <summary>
        /// The state of a cancellation after a vote. A reject naming a signed transaction decides it at once; otherwise
        /// it is approved at the required approvals, and rejected once the voters still able to approve cannot reach them.
        /// </summary>
        public static Outcome Decide(int approveCount, int rejectCount, int activeVoters, bool rejectNamesSignedTx)
        {
            if (rejectNamesSignedTx)
                return Outcome.Rejected;
            var required = RequiredApprovals(activeVoters);
            if (approveCount >= required)
                return Outcome.Approved;
            if (activeVoters - rejectCount < required)
                return Outcome.Rejected;
            return Outcome.Pending;
        }

        /// <summary>
        /// The validators that may vote on a contract's cancellations in the block after <paramref name="atHeight"/>,
        /// and the denominator of the approval: the contract's voter set, limited to validators active at that height.
        /// </summary>
        public static HashSet<string> ActiveVoters(string scUID, long atHeight)
        {
            var active = VBTCValidatorRegistry.GetActiveValidatorsAt(atHeight);
            var snapshot = VBTCChainView.GetVault(scUID)?.Feature.ValidatorAddressesSnapshot;
            if (snapshot != null && snapshot.Count > 0)
            {
                var members = new HashSet<string>(snapshot, StringComparer.Ordinal);
                return active.Where(v => members.Contains(v.ValidatorAddress)).Select(v => v.ValidatorAddress).ToHashSet(StringComparer.Ordinal);
            }
            // No snapshot (legacy contract): the public validators. S3C validators never vote on a public contract.
            return active.Where(v => !v.IsS3C).Select(v => v.ValidatorAddress).ToHashSet(StringComparer.Ordinal);
        }

        private static (string? Uid, bool Approve, string? SignedBtcTxId) ParseVote(Transaction tx)
        {
            var jobj = JObject.Parse(tx.Data);
            return (jobj["CancellationUID"]?.ToObject<string?>(),
                    jobj["Approve"]?.ToObject<bool?>() ?? false,
                    jobj["SignedBtcTxId"]?.ToObject<string?>());
        }

        // ── Validation ──────────────────────────────────────────────────────────────────────────

        /// <summary>Null when a cancel at <paramref name="height"/> is valid, else the reason.</summary>
        public static string? ValidateCancel(Transaction tx, long height)
        {
            if (string.IsNullOrWhiteSpace(tx.Data))
                return "Transaction data cannot be null for vBTC V2 withdrawal cancel.";
            var jobj = JObject.Parse(tx.Data);
            var scUID = jobj["ContractUID"]?.ToObject<string?>();
            var requestHash = jobj["WithdrawalRequestHash"]?.ToObject<string?>();
            if (string.IsNullOrEmpty(scUID))
                return "ContractUID is required for vBTC V2 withdrawal cancel.";
            if (string.IsNullOrEmpty(requestHash))
                return "WithdrawalRequestHash is required for vBTC V2 withdrawal cancel.";

            // Contract-scoped: each contract's share of a multi-contract withdrawal is cancelled on its own.
            var request = VBTCWithdrawalRequest.GetByTransactionHash(requestHash, scUID);
            if (request == null)
                return $"Withdrawal request not found: {requestHash} on contract {scUID}";
            if (request.RequestorAddress != tx.FromAddress)
                return "Only the original withdrawal requestor can submit a cancellation.";
            if (request.IsCompleted)
                return "Cannot cancel an already completed/cancelled withdrawal.";

            var open = VBTCWithdrawalCancellation.GetOpenCancellation(requestHash, scUID, height);
            if (open != null)
                return $"A cancellation of this withdrawal is already being voted on ({open.CancellationUID}); it is decided or lapses by block {open.RequestBlockHeight + VBTCWithdrawalCancellation.VOTE_WINDOW_BLOCKS}.";
            return null;
        }

        /// <summary>Null when a vote at <paramref name="height"/> is valid, else the reason.</summary>
        public static string? ValidateVote(Transaction tx, long height)
        {
            if (string.IsNullOrWhiteSpace(tx.Data))
                return "Transaction data cannot be null for vBTC V2 withdrawal vote.";
            var (uid, approve, signedTxId) = ParseVote(tx);
            if (string.IsNullOrEmpty(uid))
                return "CancellationUID is required for vBTC V2 withdrawal vote.";

            var cancellation = VBTCWithdrawalCancellation.GetCancellation(uid);
            if (cancellation == null || !VBTCWithdrawalCancellation.IsOnChainRecord(cancellation))
                return $"Cancellation request not found: {uid}";
            if (cancellation.IsProcessed)
                return "Cannot vote on an already decided cancellation.";
            if (!VBTCWithdrawalCancellation.IsVoteWindowOpen(cancellation, height))
                return cancellation.RequestBlockHeight > 0
                    ? $"The vote window of cancellation {uid} closed at block {cancellation.RequestBlockHeight + VBTCWithdrawalCancellation.VOTE_WINDOW_BLOCKS}. The withdrawal can be cancelled again."
                    : $"Cancellation {uid} was filed before the voting rules were active. The withdrawal can be cancelled again.";

            if (!string.IsNullOrEmpty(signedTxId))
            {
                if (approve)
                    return "An approve vote cannot name a signed Bitcoin transaction.";
                if (!IsBtcTxId(signedTxId))
                    return "SignedBtcTxId must be a Bitcoin transaction id (64 hex characters).";
            }

            if (!ActiveVoters(cancellation.SmartContractUID, height - 1).Contains(tx.FromAddress))
                return "Only active validators in this contract's voter set can vote on its cancellation.";
            if (VBTCWithdrawalCancellation.HasValidatorVoted(uid, tx.FromAddress))
                return "Validator has already voted on this cancellation.";
            return null;
        }

        // ── Apply ───────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Creates the cancellation record of a mined cancel (tx.Height). False when the cancel does not apply (the same
        /// checks as ValidateCancel) or its record already exists (store rebuild running beside live processing).
        /// </summary>
        public static bool ApplyCancel(Transaction tx, out VBTCWithdrawalCancellation? created)
        {
            created = null;
            var uid = $"{VBTCWithdrawalCancellation.OnChainUidPrefix}{tx.Hash}";
            if (VBTCWithdrawalCancellation.GetCancellation(uid) != null)
                return false;

            var reason = ValidateCancel(tx, tx.Height);
            if (reason != null)
            {
                ErrorLogUtility.LogError($"Cancel {tx.Hash} not applied: {reason}", "VBTCCancellationVoting.ApplyCancel()");
                return false;
            }

            var jobj = JObject.Parse(tx.Data);
            created = new VBTCWithdrawalCancellation
            {
                CancellationUID = uid,
                SmartContractUID = jobj["ContractUID"]!.ToObject<string>()!,
                OwnerAddress = tx.FromAddress,
                WithdrawalRequestHash = jobj["WithdrawalRequestHash"]!.ToObject<string>()!,
                BTCTxHash = "",
                FailureProof = jobj["FailureProof"]?.ToObject<string?>() ?? "",
                RequestTime = tx.Timestamp,
                RequestBlockHeight = tx.Height,
                ValidatorVotes = new Dictionary<string, bool>(),
                ApproveCount = 0,
                RejectCount = 0,
                IsApproved = false,
                IsProcessed = false
            };
            VBTCWithdrawalCancellation.SaveCancellation(created);
            return true;
        }

        public sealed class VoteResult
        {
            /// <summary>False when the vote was not counted (invalid, duplicate, or the cancellation is decided).</summary>
            public bool Applied { get; init; }
            public Outcome Outcome { get; init; }
            public VBTCWithdrawalCancellation? Cancellation { get; init; }
            public int ApproveCount { get; init; }
            public int RejectCount { get; init; }
            public int ActiveVoters { get; init; }
        }

        /// <summary>
        /// The withdrawal whose escrow the vote <paramref name="tx"/> refunds, or null. A refund belongs to the vote
        /// that approved the cancellation (DecidedByTxHash), not to whoever counted that vote: the store rebuild reads
        /// blocks, which are stored before their state is applied, and a node replaying state finds every cancellation
        /// already decided. The ledger's owner (StateData) calls this for every vote it applies and writes the credit.
        /// </summary>
        public static VBTCWithdrawalRequest? RefundDueFor(Transaction tx)
        {
            try
            {
                var (uid, _, _) = ParseVote(tx);
                if (string.IsNullOrEmpty(uid))
                    return null;
                var cancellation = VBTCWithdrawalCancellation.GetCancellation(uid);
                if (cancellation == null || !cancellation.IsProcessed || !cancellation.IsApproved
                    || !string.Equals(cancellation.DecidedByTxHash, tx.Hash, StringComparison.Ordinal))
                    return null;

                var request = VBTCWithdrawalRequest.GetByTransactionHash(cancellation.WithdrawalRequestHash, cancellation.SmartContractUID);
                if (request == null || request.Status != VBTCWithdrawalStatus.Cancelled)
                    return null; // completed with a burn: nothing to give back
                return VBTCWithdrawalRequest.EscrowAppliesTo(request.RequestBlockHeight) ? request : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Counts a mined vote (tx.Height) and decides the cancellation when it can be. On approval the withdrawal row
        /// becomes Cancelled - unless it already completed with a burn, which stands.
        /// </summary>
        public static VoteResult ApplyVote(Transaction tx)
        {
            var reason = ValidateVote(tx, tx.Height);
            if (reason != null)
            {
                ErrorLogUtility.LogError($"Vote {tx.Hash} from {tx.FromAddress} not counted: {reason}", "VBTCCancellationVoting.ApplyVote()");
                return new VoteResult { Applied = false };
            }

            var (uid, approve, signedTxId) = ParseVote(tx);
            VBTCWithdrawalCancellation.AddVote(uid!, tx.FromAddress, approve, tx.Hash);

            var cancellation = VBTCWithdrawalCancellation.GetCancellation(uid!)!;
            var activeVoters = ActiveVoters(cancellation.SmartContractUID, tx.Height - 1).Count;
            var namesSignedTx = !approve && IsBtcTxId(signedTxId);
            var outcome = Decide(cancellation.ApproveCount, cancellation.RejectCount, activeVoters, namesSignedTx);

            if (outcome == Outcome.Rejected)
            {
                VBTCWithdrawalCancellation.MarkAsProcessed(uid!, false, tx.Height, tx.Hash,
                    namesSignedTx ? signedTxId!.ToLowerInvariant() : null, namesSignedTx ? tx.FromAddress : null);
            }
            else if (outcome == Outcome.Approved)
            {
                VBTCWithdrawalCancellation.MarkAsProcessed(uid!, true, tx.Height, tx.Hash, null, null);

                var request = VBTCWithdrawalRequest.GetByTransactionHash(cancellation.WithdrawalRequestHash, cancellation.SmartContractUID);
                if (request != null && !(request.IsCompleted && request.Status == VBTCWithdrawalStatus.Completed))
                {
                    request.Status = VBTCWithdrawalStatus.Cancelled;
                    request.IsCompleted = true;
                    VBTCWithdrawalRequest.Save(request, true);
                }
            }

            return new VoteResult
            {
                Applied = true,
                Outcome = outcome,
                Cancellation = VBTCWithdrawalCancellation.GetCancellation(uid!),
                ApproveCount = cancellation.ApproveCount,
                RejectCount = cancellation.RejectCount,
                ActiveVoters = activeVoters,
            };
        }
    }
}
