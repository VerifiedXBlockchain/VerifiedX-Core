using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.P2P;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Bitcoin.Services
{
    /// <summary>
    /// A validator's side of withdrawal-cancellation voting (public and S3C contracts alike): finds the open
    /// cancellations it may vote on, decides each from its OWN signing records, and casts the vote as an on-chain
    /// VBTC_V2_WITHDRAWAL_VOTE signed by the validator's account. Consensus (VBTCCancellationVoting) counts the votes
    /// and refunds the escrow; nothing here writes cancellation records.
    ///
    /// The decision is the safety of the refund: an approve vote says "this validator never released a signature
    /// share for this withdrawal". A validator that did sign rejects and names the transaction, which rejects the
    /// cancellation - the withdrawal is payable, so its requester completes it instead.
    /// </summary>
    public static class VBTCCancellationVoteService
    {
        private const int LOOP_SECONDS = 180;
        private const int MAX_VOTES_PER_PASS = 5;

        public enum VoteDecision
        {
            /// <summary>No signature share of this validator exists for the withdrawal.</summary>
            Approve,
            /// <summary>This validator signed a transaction that can still pay (or has paid): reject and name it.</summary>
            RejectSignedTx,
            /// <summary>The cancellation cannot apply (withdrawal completed), or a build is held without a known txid.</summary>
            Reject,
            /// <summary>Not decidable yet (a ceremony is running here, the row is missing, or Bitcoin could not be read).</summary>
            Wait,
            /// <summary>
            /// This validator cannot vouch for the withdrawal: it was requested before this validator's signing records
            /// begin. No vote is cast; the other validators decide.
            /// </summary>
            Abstain
        }

        /// <summary>The tip block may be this old (seconds) for the node to count as being at the network's height.</summary>
        public const int MAX_TIP_AGE_SECONDS = 300;

        /// <summary>
        /// True when this node is at the network's height: synced, not resyncing or downloading blocks, not stalled, and
        /// its tip block is recent. Everything here that ACTS on chain state (voting, stamping the records epoch,
        /// completing a deferred withdrawal) requires it. A node that is behind measures vote windows against its own
        /// low tip, so a cancellation decided long ago looks open to it; consensus would refuse its vote, but it must not
        /// be built. (Replaying old cancel and vote blocks needs no gate: they are judged at their own heights.)
        /// </summary>
        public static (bool Ready, string Reason) NodeIsAtNetworkHeight()
        {
            if (!Globals.IsChainSynced) return (false, "chain not synced");
            if (Globals.IsResyncing) return (false, "node is resyncing");
            if (Globals.StopAllTimers) return (false, "timers stopped");
            if (Globals.BlocksDownloadSlim.CurrentCount == 0) return (false, "blocks are downloading");
            if (ForkDetectionService.IsStalled) return (false, $"chain stalled ({ForkDetectionService.SyncState})");
            var tip = Globals.LastBlock;
            if (tip == null || tip.Height <= 0) return (false, "no tip block");
            var age = TimeUtil.GetTime() - tip.Timestamp;
            if (age > MAX_TIP_AGE_SECONDS) return (false, $"tip block {tip.Height} is {age}s old");
            return (true, string.Empty);
        }

        /// <summary>The highest block at or before <paramref name="timestamp"/> (block times rise with height).</summary>
        public static long HeightAtOrBefore(long timestamp, long tipHeight)
        {
            long lo = 0, hi = tipHeight, found = 0;
            while (lo <= hi)
            {
                var mid = lo + (hi - lo) / 2;
                var block = BlockchainData.GetBlockByHeight(mid);
                if (block == null) { hi = mid - 1; continue; }
                if (block.Timestamp <= timestamp) { found = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return found;
        }

        /// <summary>
        /// Stamps where this validator's signing records begin, once, and only at the network's height. With records
        /// already held (a validator that has been signing on this machine): the block of its oldest record. With none:
        /// the current tip. Returns the epoch, or null while the node is not at height.
        /// </summary>
        public static FrostSignedWithdrawalEvidence.Epoch? EnsureEvidenceEpoch()
        {
            var epoch = FrostSignedWithdrawalEvidence.GetEpoch();
            if (epoch != null)
                return epoch;
            if (!NodeIsAtNetworkHeight().Ready)
                return null;

            var tip = Globals.LastBlock.Height;
            var oldest = FrostSignedWithdrawalEvidence.OldestRecordTimestamp();
            epoch = oldest.HasValue
                ? FrostSignedWithdrawalEvidence.SetEpoch(HeightAtOrBefore(oldest.Value, tip), "oldest signing record on this machine")
                : FrostSignedWithdrawalEvidence.SetEpoch(tip, "first run at the network's height with no signing records");
            if (epoch != null)
                LogUtility.Log($"[VBTC V2] Signing records of this validator begin at block {epoch.EpochHeight} ({epoch.Source}). It abstains from cancellation votes on withdrawals requested before that.",
                    "VBTCCancellationVoteService.EnsureEvidenceEpoch()");
            return epoch;
        }

        /// <summary>What this validator knows about its own signing for a withdrawal.</summary>
        public sealed class SigningEvidence
        {
            public bool HasEvidence { get; init; }
            public string? BtcTxId { get; init; }
            /// <summary>The signed transaction can never confirm (an input was spent by a confirmed transaction).</summary>
            public bool ProvenDead { get; init; }
            /// <summary>Bitcoin could not be read well enough to tell whether the signed transaction is dead.</summary>
            public bool Inconclusive { get; init; }
        }

        /// <summary>
        /// The vote for one cancellation (pure: every input is passed in). <paramref name="evidenceEpochHeight"/> is
        /// where this validator's signing records begin (null: not established yet). It limits only the approve that
        /// rests on holding NO record: a record for the withdrawal itself speaks for itself.
        /// </summary>
        public static (VoteDecision Decision, string Reason, string? SignedBtcTxId) Decide(
            VBTCWithdrawalRequest? request, bool ceremonyInProgress, SigningEvidence evidence, long? evidenceEpochHeight)
        {
            if (request == null)
                return (VoteDecision.Wait, "withdrawal row not in this node's store yet", null);
            if (request.IsCompleted && request.Status == VBTCWithdrawalStatus.Completed)
                return (VoteDecision.Reject, "withdrawal already completed", null);
            if (request.IsCompleted)
                return (VoteDecision.Wait, "withdrawal already cancelled", null);
            if (ceremonyInProgress)
                return (VoteDecision.Wait, "a signing ceremony for this withdrawal is running on this validator", null);

            if (!evidence.HasEvidence)
            {
                if (!evidenceEpochHeight.HasValue)
                    return (VoteDecision.Wait, "where this validator's signing records begin is not established yet (node not at the network's height)", null);
                if (request.RequestBlockHeight <= 0 || request.RequestBlockHeight < evidenceEpochHeight.Value)
                    return (VoteDecision.Abstain, $"the withdrawal was requested at block {request.RequestBlockHeight}, before this validator's signing records begin (block {evidenceEpochHeight.Value})", null);
                return (VoteDecision.Approve, "no signature share of this validator exists for the withdrawal", null);
            }
            if (evidence.Inconclusive)
                return (VoteDecision.Wait, "this validator signed a transaction and could not establish from Bitcoin whether it can still confirm", null);
            if (evidence.ProvenDead)
                return (VoteDecision.Approve, $"the transaction this validator signed ({evidence.BtcTxId}) can never confirm", null);
            if (VBTCCancellationVoting.IsBtcTxId(evidence.BtcTxId))
                return (VoteDecision.RejectSignedTx, $"this validator signed Bitcoin transaction {evidence.BtcTxId} for the withdrawal; it can still pay out", evidence.BtcTxId);
            return (VoteDecision.Reject, "this validator holds signing records for the withdrawal without a transaction id", null);
        }

        /// <summary>
        /// This validator's signing records for the withdrawal (durable evidence, the in-memory tracker and its pins,
        /// the local request row), and - when a signed transaction is known - whether Bitcoin proves it dead.
        /// </summary>
        public static async Task<SigningEvidence> GetSigningEvidenceAsync(string scUID, string withdrawalRequestHash, VBTCWithdrawalRequest? localRow)
        {
            var durable = FrostSignedWithdrawalEvidence.Get(scUID, withdrawalRequestHash);
            var pin = FrostWithdrawalSigningTracker.GetContractPins(scUID)
                .FirstOrDefault(p => string.Equals(p.WithdrawalRequestHash, withdrawalRequestHash, StringComparison.OrdinalIgnoreCase));
            var trackerSigned = FrostWithdrawalSigningTracker.HasSignedTransaction(scUID, withdrawalRequestHash);
            var rowHolds = !string.IsNullOrWhiteSpace(localRow?.LastSignedBtcTxId) || !string.IsNullOrWhiteSpace(localRow?.PinnedUnsignedTxHex);

            if (durable == null && pin == null && !trackerSigned && !rowHolds)
                return new SigningEvidence { HasEvidence = false };

            var txId = new[] { durable?.BtcTxId, pin?.BtcTxId, localRow?.LastSignedBtcTxId }
                .FirstOrDefault(VBTCCancellationVoting.IsBtcTxId)?.ToLowerInvariant();
            var outpoints = (durable?.Outpoints ?? new List<string>()).Concat(pin?.Outpoints ?? new List<string>()).Distinct().ToList();
            if (txId == null || outpoints.Count == 0)
                return new SigningEvidence { HasEvidence = true, BtcTxId = txId };

            // Confirmed or in a mempool: it pays (or paid). Unknown to every server: dead only when one of its
            // inputs was spent by a CONFIRMED transaction (the same rule the signing guard uses).
            var lookup = await BitcoinTransactionService.GetTransactionConfirmationsResilient(txId);
            if (lookup.Confirmations.HasValue)
                return new SigningEvidence { HasEvidence = true, BtcTxId = txId };

            var depositAddress = VBTCChainView.GetVault(scUID)?.Feature.DepositAddress;
            if (string.IsNullOrEmpty(depositAddress))
                return new SigningEvidence { HasEvidence = true, BtcTxId = txId, Inconclusive = true };

            var (verdict, _) = await BitcoinTransactionService.GetOutpointSpendVerdict(depositAddress, outpoints);
            return verdict switch
            {
                BitcoinTransactionService.OutpointSpendVerdict.SpentByConfirmed => new SigningEvidence { HasEvidence = true, BtcTxId = txId, ProvenDead = true },
                BitcoinTransactionService.OutpointSpendVerdict.NotSpentByConfirmed => new SigningEvidence { HasEvidence = true, BtcTxId = txId },
                _ => new SigningEvidence { HasEvidence = true, BtcTxId = txId, Inconclusive = true },
            };
        }

        /// <summary>This validator's decision for a cancellation, from its own records.</summary>
        public static async Task<(VoteDecision Decision, string Reason, string? SignedBtcTxId)> DecideForAsync(VBTCWithdrawalCancellation cancellation)
        {
            var row = VBTCWithdrawalRequest.GetByTransactionHash(cancellation.WithdrawalRequestHash, cancellation.SmartContractUID);
            var inProgress = FrostWithdrawalSigningTracker.HasCeremonyInProgress(cancellation.SmartContractUID, cancellation.WithdrawalRequestHash);
            var evidence = await GetSigningEvidenceAsync(cancellation.SmartContractUID, cancellation.WithdrawalRequestHash, row);
            return Decide(row, inProgress, evidence, EnsureEvidenceEpoch()?.EpochHeight);
        }

        /// <summary>The signed Data of a vote. Consensus reads CancellationUID, Approve and SignedBtcTxId; the rest documents the vote.</summary>
        public static string BuildVoteData(VBTCWithdrawalCancellation cancellation, bool approve, string? signedBtcTxId, string reason) =>
            JsonConvert.SerializeObject(new
            {
                Function = "VBTCWithdrawalCancelVote()",
                CancellationUID = cancellation.CancellationUID,
                Approve = approve,
                SignedBtcTxId = !approve && VBTCCancellationVoting.IsBtcTxId(signedBtcTxId) ? signedBtcTxId!.ToLowerInvariant() : null,
                ContractUID = cancellation.SmartContractUID,
                WithdrawalRequestHash = cancellation.WithdrawalRequestHash,
                Reason = reason
            }, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });

        /// <summary>
        /// Builds, signs and broadcasts this validator's vote. Verified against consensus first, so a vote the network
        /// would refuse (not a voter of the contract, already voted, window closed) never leaves the node.
        /// </summary>
        public static async Task<(bool Ok, string TxHashOrError)> CastVote(VBTCWithdrawalCancellation cancellation, bool approve, string? signedBtcTxId, string reason)
        {
            try
            {
                var address = Globals.ValidatorAddress;
                if (string.IsNullOrEmpty(address))
                    return (false, "This node is not a validator.");
                var account = AccountData.GetSingleAccount(address);
                if (account == null)
                    return (false, $"Validator account not found in this wallet: {address}");

                var voteTx = new Transaction
                {
                    Timestamp = TimeUtil.GetTime(),
                    FromAddress = address,
                    ToAddress = address,
                    Amount = 0.0M,
                    Fee = 0.0M,
                    Nonce = AccountStateTrei.GetNextNonce(address),
                    TransactionType = TransactionType.VBTC_V2_WITHDRAWAL_VOTE,
                    Data = BuildVoteData(cancellation, approve, signedBtcTxId, reason)
                };
                voteTx.Fee = VerifiedXCore.Services.FeeCalcService.CalculateTXFee(voteTx);
                voteTx.Build();

                var signature = VerifiedXCore.Services.SignatureService.CreateSignature(voteTx.Hash, AccountData.GetPrivateKey(account), account.PublicKey);
                if (signature == "ERROR")
                    return (false, "Vote transaction signature failed.");
                voteTx.Signature = signature;

                var (valid, verifyReason) = await TransactionValidatorService.VerifyTX(voteTx);
                if (!valid)
                    return (false, $"Vote refused: {verifyReason}");

                await TransactionData.AddTxToWallet(voteTx, true);
                await AccountData.UpdateLocalBalance(address, voteTx.Fee + voteTx.Amount);
                await TransactionData.AddToPool(voteTx);
                await P2PClient.SendTXMempool(voteTx);

                LogUtility.Log($"[VBTC V2] Cancellation vote broadcast: {(approve ? "APPROVE" : "REJECT")} {cancellation.CancellationUID} (withdrawal {cancellation.WithdrawalRequestHash} on {cancellation.SmartContractUID}) - {reason}. Tx: {voteTx.Hash}",
                    "VBTCCancellationVoteService.CastVote()");
                return (true, voteTx.Hash);
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Cancellation vote failed for {cancellation.CancellationUID}: {ex}", "VBTCCancellationVoteService.CastVote()");
                return (false, $"Error: {ApiErrorText.For(ex)}");
            }
        }

        /// <summary>True when a vote of this validator for the cancellation is waiting in the local mempool.</summary>
        public static bool HasPendingVote(string validatorAddress, string cancellationUID)
        {
            try
            {
                return TransactionData.GetPool()
                    .Find(x => x.FromAddress == validatorAddress && x.TransactionType == TransactionType.VBTC_V2_WITHDRAWAL_VOTE)
                    .ToList()
                    .Any(x =>
                    {
                        try { return JObject.Parse(x.Data)["CancellationUID"]?.ToObject<string>() == cancellationUID; }
                        catch { return false; }
                    });
            }
            catch { return false; }
        }

        /// <summary>The open cancellations this validator may vote on now and has not voted on.</summary>
        public static List<VBTCWithdrawalCancellation> GetCancellationsAwaitingMyVote()
        {
            var address = Globals.ValidatorAddress;
            var tip = Globals.LastBlock?.Height ?? 0;
            if (string.IsNullOrEmpty(address) || !VBTCCancellationVoting.RulesActive(tip + 1))
                return new List<VBTCWithdrawalCancellation>();

            return (VBTCWithdrawalCancellation.GetAllCancellations() ?? new List<VBTCWithdrawalCancellation>())
                .Where(VBTCWithdrawalCancellation.IsOnChainRecord)
                .Where(c => VBTCWithdrawalCancellation.IsVoteWindowOpen(c, tip + 1))
                .Where(c => c.ValidatorVotes == null || !c.ValidatorVotes.ContainsKey(address))
                .Where(c => !HasPendingVote(address, c.CancellationUID))
                .Where(c => VBTCCancellationVoting.ActiveVoters(c.SmartContractUID, tip).Contains(address))
                .OrderBy(c => c.RequestBlockHeight)
                .ToList();
        }

        // Cancellations this validator abstained from: logged once, not every pass of their vote window.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _abstained = new();

        /// <summary>
        /// One pass: decide and vote on the cancellations awaiting this validator. Returns the votes cast. Does nothing
        /// unless the node is at the network's height (see NodeIsAtNetworkHeight).
        /// </summary>
        public static async Task<int> RunOnce()
        {
            if (!NodeIsAtNetworkHeight().Ready)
                return 0;

            EnsureEvidenceEpoch(); // as early as the node is at height, whether or not anything awaits a vote

            var cast = 0;
            foreach (var cancellation in GetCancellationsAwaitingMyVote())
            {
                if (cast >= MAX_VOTES_PER_PASS)
                    break;

                var (decision, reason, signedTxId) = await DecideForAsync(cancellation);
                if (decision == VoteDecision.Abstain)
                {
                    if (_abstained.TryAdd(cancellation.CancellationUID, 0))
                        LogUtility.Log($"[VBTC V2] Cancellation {cancellation.CancellationUID}: abstaining - {reason}.", "VBTCCancellationVoteService.RunOnce()");
                    continue;
                }
                if (decision == VoteDecision.Wait)
                {
                    LogUtility.Log($"[VBTC V2] Cancellation {cancellation.CancellationUID}: not voting yet - {reason}.", "VBTCCancellationVoteService.RunOnce()");
                    continue;
                }

                var (ok, result) = await CastVote(cancellation, decision == VoteDecision.Approve, signedTxId, reason);
                if (ok)
                    cast++;
                else
                    ErrorLogUtility.LogError($"Cancellation {cancellation.CancellationUID}: vote not cast - {result}", "VBTCCancellationVoteService.RunOnce()");
            }
            return cast;
        }

        /// <summary>Validator nodes only. Votes on open cancellations every few minutes while the node is healthy.</summary>
        public static async Task VoteLoop()
        {
            if (string.IsNullOrEmpty(Globals.ValidatorAddress))
                return;

            await Task.Delay(TimeSpan.FromMinutes(2)); // let startup complete
            LogUtility.Log("Starting vBTC V2 cancellation vote loop", "VBTCCancellationVoteService.VoteLoop()");

            while (true)
            {
                try
                {
                    // RunOnce acts only at the network's height: a vote built against forked, stale or
                    // still-syncing state must not go out.
                    if (!string.IsNullOrEmpty(Globals.ValidatorAddress))
                        await RunOnce();
                }
                catch (Exception ex)
                {
                    ErrorLogUtility.LogError($"Error in cancellation vote loop: {ex}", "VBTCCancellationVoteService.VoteLoop()");
                }
                await Task.Delay(TimeSpan.FromSeconds(LOOP_SECONDS));
            }
        }
    }
}
