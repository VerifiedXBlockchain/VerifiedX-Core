using LiteDB;
using VerifiedXCore.Data;
using VerifiedXCore.Extensions;
using VerifiedXCore.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;

namespace VerifiedXCore.Bitcoin.Models
{
    public class VBTCWithdrawalCancellation
    {
        #region Variables
        public long Id { get; set; }
        public string CancellationUID { get; set; }
        public string SmartContractUID { get; set; }
        public string OwnerAddress { get; set; }
        public string WithdrawalRequestHash { get; set; }
        public string BTCTxHash { get; set; }
        public string FailureProof { get; set; }
        public long RequestTime { get; set; }
        
        // Validator Voting
        public Dictionary<string, bool> ValidatorVotes { get; set; }
        public int ApproveCount { get; set; }
        public int RejectCount { get; set; }
        public bool IsApproved { get; set; }
        public bool IsProcessed { get; set; }

        // VbtcCancellationVoteRulesHeight. Height the cancel was mined at (0 on records from before the rules): the
        // vote window is measured from it, in blocks, so every node agrees when it closes.
        public long RequestBlockHeight { get; set; }
        // The vote that decided the cancellation (approved or rejected): its height and hash; unset while undecided.
        // The escrow refund belongs to that transaction, so a node that replays state from blocks, or whose store
        // rebuild counted the vote first, still writes the credit exactly once.
        public long DecidedAtHeight { get; set; }
        public string? DecidedByTxHash { get; set; }
        // Set when a validator's reject named the Bitcoin transaction it signed for the withdrawal: that
        // transaction can still pay out, so the cancellation was rejected and the withdrawal should be completed.
        public string? SignedBtcTxId { get; set; }
        public string? SignedTxReportedBy { get; set; }
        // Validator address → hash of the vote transaction that was counted for it (display and wallet bookkeeping).
        public Dictionary<string, string>? VoteTxHashes { get; set; }
        #endregion

        /// <summary>True when <paramref name="voteTxHash"/> is the vote that was counted for <paramref name="validatorAddress"/>.</summary>
        public static bool WasVoteCounted(VBTCWithdrawalCancellation? c, string validatorAddress, string voteTxHash) =>
            c?.VoteTxHashes != null && c.VoteTxHashes.TryGetValue(validatorAddress, out var counted)
            && string.Equals(counted, voteTxHash, StringComparison.Ordinal);

        /// <summary>
        /// Blocks after a cancel is mined during which validators may vote on it (~1 day). Validators refuse to sign
        /// for the withdrawal for exactly as long, so an approval can never race a new signature. After it, an
        /// undecided cancellation has lapsed: votes are refused, signing resumes, and the requester may cancel again.
        /// </summary>
        public const long VOTE_WINDOW_BLOCKS = 7_200;

        /// <summary>True while the cancellation can still be voted on at <paramref name="height"/>.</summary>
        public static bool IsVoteWindowOpen(VBTCWithdrawalCancellation c, long height) =>
            c != null && !c.IsProcessed && c.RequestBlockHeight > 0 && height >= c.RequestBlockHeight
            && height - c.RequestBlockHeight <= VOTE_WINDOW_BLOCKS;

        /// <summary>
        /// UID prefix of every record created by a mined VBTC_V2_WITHDRAWAL_CANCEL (StateData and the store rebuild:
        /// "CANCEL_{tx hash}"). Older builds' /CancelWithdrawal and /CancelWithdrawalRaw endpoints saved records with
        /// random GUIDs on the serving node only; those exist nowhere else on the network.
        /// </summary>
        public const string OnChainUidPrefix = "CANCEL_";

        /// <summary>True for a record that exists on every node (created from a mined cancel transaction).</summary>
        public static bool IsOnChainRecord(VBTCWithdrawalCancellation? c) =>
            c?.CancellationUID?.StartsWith(OnChainUidPrefix, StringComparison.Ordinal) == true;

        #region Database Methods
        /// <summary>
        /// Index on the cancelled request's hash, kept only up to 200 characters (others indexed as null): it is taken from
        /// the cancel's data and found through the collation, so its length is not bounded before the path-audit rules,
        /// and LiteDB loses a write whose key is over 1,023 bytes (round 34). The store had no index: every cancel and
        /// every vote read every cancellation ever recorded, about two fees a row (fifth review, round 35).
        /// </summary>
        internal const string ShortRequestHashIndexExpression = "IIF(LENGTH($.WithdrawalRequestHash) <= 200, $.WithdrawalRequestHash, null)";
        internal const string ShortRequestHashIndexName = "WithdrawalRequestHashShort";

        /// <summary>The cancellations of <paramref name="withdrawalRequestHash"/> as the database compares it, in the scan's order.</summary>
        public static List<VBTCWithdrawalCancellation> RowsForRequest(ILiteCollection<VBTCWithdrawalCancellation> col, string? withdrawalRequestHash)
        {
            if (withdrawalRequestHash == null) return new List<VBTCWithdrawalCancellation>();
            var collation = DbContext.DB_vBTC.Collation;
            var candidates = withdrawalRequestHash.Length <= 200
                ? col.Find(Query.EQ(ShortRequestHashIndexExpression, withdrawalRequestHash)).Concat(col.Find(Query.EQ(ShortRequestHashIndexExpression, BsonValue.Null)))
                : col.FindAll();
            return candidates
                .Where(x => x.WithdrawalRequestHash != null && collation.Compare(x.WithdrawalRequestHash, withdrawalRequestHash) == 0)
                .GroupBy(x => x.Id).Select(g => g.First())
                .OrderBy(x => x.Id)
                .ToList();
        }

        /// <summary>Indexes the store (at start; round 35): the cancellation UID ("CANCEL_" and a transaction hash) and the request hash, bounded.</summary>
        internal static void EnsureIndexes(LiteDatabase db)
        {
            try
            {
                var col = db.GetCollection<VBTCWithdrawalCancellation>(DbContext.RSRV_VBTC_V2_CANCELLATIONS);
                col.EnsureIndex(x => x.CancellationUID, false);
                col.EnsureIndex(ShortRequestHashIndexName, ShortRequestHashIndexExpression);
            }
            catch (Exception ex) { ErrorLogUtility.LogError($"Cancellation indexes: {ex.Message}", "VBTCWithdrawalCancellation.EnsureIndexes()"); }
        }

        public static ILiteCollection<VBTCWithdrawalCancellation> GetDb()
        {
            var db = DbContext.DB_vBTC.GetCollection<VBTCWithdrawalCancellation>(DbContext.RSRV_VBTC_V2_CANCELLATIONS);
            return db;
        }

        public static VBTCWithdrawalCancellation? GetCancellation(string cancellationUID)
        {
            var cancellations = GetDb();
            if (cancellations != null)
            {
                var cancellation = cancellations.FindOne(x => x.CancellationUID == cancellationUID);
                if (cancellation != null)
                {
                    return cancellation;
                }
            }

            return null;
        }

        /// <summary>
        /// True when ANY unprocessed cancellation exists for the withdrawal. Cancellation records are
        /// created by consensus (the cancel tx applies on every node), so this is the network-wide
        /// signal that a cancellation vote is in progress — unlike the contract's local status field.
        /// </summary>
        /// <summary>
        /// A cancellation is only ever marked processed on approval; a rejected or stalled vote stays
        /// unprocessed forever. Without a bound, one stalled cancellation would block signing for the
        /// withdrawal permanently. A cancellation older than this no longer blocks signing.
        /// </summary>
        public const long PENDING_CANCELLATION_MAX_AGE_SECONDS = 86_400;

        public static bool HasPendingCancellation(string withdrawalRequestHash) =>
            HasPendingCancellation(withdrawalRequestHash, VerifiedXCore.Utilities.TimeUtil.GetTime());

        public static bool HasPendingCancellation(string withdrawalRequestHash, long nowSeconds) =>
            HasPendingCancellation(withdrawalRequestHash, nowSeconds, null);

        /// <summary>
        /// Contract-scoped pending-cancellation check. A multi-contract withdrawal opens one row
        /// per contract under a single REQUEST tx hash, and each is cancelled independently — so a
        /// cancellation filed against contract A must not block FROST signing for contract B's
        /// share. Callers acting on one contract pass <paramref name="scUID"/>; passing null keeps
        /// the whole-request behavior (unchanged for single-contract withdrawals, whose only
        /// cancellation row is that contract's).
        /// </summary>
        public static bool HasPendingCancellation(string withdrawalRequestHash, long nowSeconds, string? scUID) =>
            PendingCancellation(withdrawalRequestHash, nowSeconds, scUID, whenUnreadable: false);

        /// <summary>
        /// For a signer: as <see cref="HasPendingCancellation(string, long, string?)"/>, but a store that cannot be read
        /// answers "pending", so no share is given for a withdrawal whose cancellation could not be ruled out.
        /// </summary>
        public static bool HasPendingCancellationOrUnreadable(string withdrawalRequestHash, long nowSeconds, string? scUID) =>
            PendingCancellation(withdrawalRequestHash, nowSeconds, scUID, whenUnreadable: true);

        private static bool PendingCancellation(string withdrawalRequestHash, long nowSeconds, string? scUID, bool whenUnreadable)
        {
            if (string.IsNullOrWhiteSpace(withdrawalRequestHash)) return false;
            try
            {
                var db = GetDb();
                if (db == null) return whenUnreadable;
                var cutoff = nowSeconds - PENDING_CANCELLATION_MAX_AGE_SECONDS;
                var height = Globals.LastBlock?.Height ?? 0;
                // On-chain records only: a node-local leftover must not block signing on one validator.
                // A record stamped with its mined height is pending for exactly its vote window (block height, the
                // same clock the votes are judged by - a stalled chain must not reopen signing while an approval
                // can still land). Older records keep the wall-clock bound.
                return RowsForRequest(db, withdrawalRequestHash)
                         .Where(x => string.IsNullOrEmpty(scUID) || x.SmartContractUID == scUID)
                         .Where(IsOnChainRecord)
                         .Any(x => x.RequestBlockHeight > 0
                             ? IsVoteWindowOpen(x, Math.Max(height, x.RequestBlockHeight))
                             : !x.IsProcessed && x.RequestTime >= cutoff);
            }
            catch { return whenUnreadable; }
        }

        /// <summary>
        /// VbtcCancellationVoteRulesHeight: the cancellation of this withdrawal share that is open at
        /// <paramref name="height"/> (undecided, inside its vote window), or null. At most one exists: a new cancel
        /// is accepted only when none is open. Records from before the rules carry no height and are never open.
        /// </summary>
        public static VBTCWithdrawalCancellation? GetOpenCancellation(string withdrawalRequestHash, string? scUID, long height)
        {
            if (string.IsNullOrWhiteSpace(withdrawalRequestHash)) return null;
            var db = GetDb();
            if (db == null) return null;
            return RowsForRequest(db, withdrawalRequestHash)
                .Where(IsOnChainRecord)
                .Where(x => string.IsNullOrEmpty(scUID) || x.SmartContractUID == scUID)
                .Where(x => IsVoteWindowOpen(x, height))
                .OrderByDescending(x => x.RequestBlockHeight)
                .FirstOrDefault();
        }

        /// <summary>Every on-chain cancellation of a withdrawal share, newest first (status display).</summary>
        public static List<VBTCWithdrawalCancellation> GetCancellationsFor(string withdrawalRequestHash, string? scUID)
        {
            var db = GetDb();
            if (db == null || string.IsNullOrWhiteSpace(withdrawalRequestHash)) return new List<VBTCWithdrawalCancellation>();
            return RowsForRequest(db, withdrawalRequestHash)
                .Where(IsOnChainRecord)
                .Where(x => string.IsNullOrEmpty(scUID) || x.SmartContractUID == scUID)
                .OrderByDescending(x => x.RequestBlockHeight).ThenByDescending(x => x.RequestTime)
                .ToList();
        }

        public static VBTCWithdrawalCancellation? GetCancellationByWithdrawalHash(string withdrawalRequestHash) =>
            GetCancellationByWithdrawalHash(withdrawalRequestHash, null);

        /// <summary>
        /// Contract-scoped duplicate-cancellation lookup. Same reason as
        /// <see cref="HasPendingCancellation(string, long, string?)"/>: cancelling one contract's
        /// share of a multi-contract withdrawal must not read as "already cancelled" for its
        /// siblings.
        /// </summary>
        public static VBTCWithdrawalCancellation? GetCancellationByWithdrawalHash(string withdrawalRequestHash, string? scUID)
        {
            var cancellations = GetDb();
            if (cancellations == null)
                return null;

            // On-chain records only. This backs the consensus duplicate-cancel rule (VerifyTX, StateData): a
            // node-local leftover here made that node refuse the real cancel — at block validation too — while
            // every other node accepted it.
            return RowsForRequest(cancellations, withdrawalRequestHash)
                .Where(IsOnChainRecord)
                .FirstOrDefault(x => string.IsNullOrEmpty(scUID) || x.SmartContractUID == scUID);
        }

        public static List<VBTCWithdrawalCancellation>? GetAllCancellations()
        {
            var cancellations = GetDb();
            if (cancellations != null)
            {
                var cancellationList = cancellations.FindAll().ToList();
                if (cancellationList.Any())
                {
                    return cancellationList;
                }
            }

            return null;
        }

        public static List<VBTCWithdrawalCancellation>? GetPendingCancellations()
        {
            var cancellations = GetDb();
            if (cancellations != null)
            {
                var cancellationList = cancellations.Find(x => !x.IsProcessed).ToList();
                if (cancellationList.Any())
                {
                    return cancellationList;
                }
            }

            return null;
        }

        public static List<VBTCWithdrawalCancellation>? GetCancellationsByContract(string smartContractUID)
        {
            var cancellations = GetDb();
            if (cancellations != null)
            {
                var cancellationList = cancellations.Find(x => x.SmartContractUID == smartContractUID).ToList();
                if (cancellationList.Any())
                {
                    return cancellationList;
                }
            }

            return null;
        }

        public static void SaveCancellation(VBTCWithdrawalCancellation cancellation)
        {
            try
            {
                var cancellations = GetDb();
                var existing = cancellations.FindOne(x => x.CancellationUID == cancellation.CancellationUID);

                if (existing == null)
                {
                    cancellations.InsertSafe(cancellation);
                }
                else
                {
                    cancellations.UpdateSafe(cancellation);
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError(ex.ToString(), "VBTCWithdrawalCancellation.SaveCancellation()");
                Extensions.DbExtensions.NoteFailure(DbContext.RSRV_VBTC_V2_CANCELLATIONS);
            }
        }

        public static void AddVote(string cancellationUID, string validatorAddress, bool approve) =>
            AddVote(cancellationUID, validatorAddress, approve, null);

        public static void AddVote(string cancellationUID, string validatorAddress, bool approve, string? voteTxHash)
        {
            try
            {
                var cancellations = GetDb();
                var cancellation = cancellations.FindOne(x => x.CancellationUID == cancellationUID);

                if (cancellation != null && !cancellation.IsProcessed)
                {
                    CountVote(cancellation, validatorAddress, approve, voteTxHash);
                    Write(cancellations, cancellation);
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError(ex.ToString(), "VBTCWithdrawalCancellation.AddVote()");
                Extensions.DbExtensions.NoteFailure(DbContext.RSRV_VBTC_V2_CANCELLATIONS);
            }
        }

        /// <summary>The decision a counted vote makes: approved or rejected, with what is recorded of it.</summary>
        public sealed record VoteDecision(bool Approved, long DecidedAtHeight, string? DecidedByTxHash, string? SignedBtcTxId, string? ReportedBy);

        /// <summary>
        /// Counts a vote and records the decision it makes in one write of the cancellation. Written apart, a vote whose
        /// decision was then lost stayed counted: a replay refused it as a second vote by that validator and the
        /// cancellation stayed undecided where the network decided it. <paramref name="decide"/> is given the record
        /// with the vote counted and returns the decision, or null when there is none yet. Returns the record as
        /// written, or null when the cancellation is missing or decided, or the write failed (counted as a lost write).
        /// </summary>
        public static VBTCWithdrawalCancellation? AddVoteAndDecide(string cancellationUID, string validatorAddress, bool approve, string? voteTxHash,
            Func<VBTCWithdrawalCancellation, VoteDecision?> decide)
        {
            try
            {
                var cancellations = GetDb();
                var cancellation = cancellations.FindOne(x => x.CancellationUID == cancellationUID);
                if (cancellation == null || cancellation.IsProcessed) return null;
                CountVote(cancellation, validatorAddress, approve, voteTxHash);
                if (decide(cancellation) is { } decision)
                    RecordDecision(cancellation, decision.Approved, decision.DecidedAtHeight, decision.DecidedByTxHash, decision.SignedBtcTxId, decision.ReportedBy);
                return Write(cancellations, cancellation) ? cancellation : null;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError(ex.ToString(), "VBTCWithdrawalCancellation.AddVoteAndDecide()");
                Extensions.DbExtensions.NoteFailure(DbContext.RSRV_VBTC_V2_CANCELLATIONS);
                return null;
            }
        }

        /// <summary>A vote or decision written to the store; a failure is counted as a lost write by the storage wrapper.</summary>
        private static bool Write(ILiteCollection<VBTCWithdrawalCancellation> cancellations, VBTCWithdrawalCancellation cancellation)
        {
            if (FailWriteForTests?.Invoke() == true)
            {
                Extensions.DbExtensions.NoteFailure(DbContext.RSRV_VBTC_V2_CANCELLATIONS);
                return false;
            }
            return cancellations.UpdateSafe(cancellation);
        }

        internal static Func<bool>? FailWriteForTests;

        private static void CountVote(VBTCWithdrawalCancellation cancellation, string validatorAddress, bool approve, string? voteTxHash)
        {
            if (!string.IsNullOrEmpty(voteTxHash))
            {
                cancellation.VoteTxHashes ??= new Dictionary<string, string>();
                cancellation.VoteTxHashes[validatorAddress] = voteTxHash;
            }

            if (cancellation.ValidatorVotes == null)
            {
                cancellation.ValidatorVotes = new Dictionary<string, bool>();
            }

            // Add or update vote
            if (cancellation.ValidatorVotes.ContainsKey(validatorAddress))
            {
                // If vote changed, update counts
                bool previousVote = cancellation.ValidatorVotes[validatorAddress];
                if (previousVote != approve)
                {
                    if (previousVote)
                    {
                        cancellation.ApproveCount--;
                        cancellation.RejectCount++;
                    }
                    else
                    {
                        cancellation.RejectCount--;
                        cancellation.ApproveCount++;
                    }
                }
                cancellation.ValidatorVotes[validatorAddress] = approve;
            }
            else
            {
                cancellation.ValidatorVotes.Add(validatorAddress, approve);
                if (approve)
                {
                    cancellation.ApproveCount++;
                }
                else
                {
                    cancellation.RejectCount++;
                }
            }
        }

        private static void RecordDecision(VBTCWithdrawalCancellation cancellation, bool approved, long decidedAtHeight, string? decidedByTxHash, string? signedBtcTxId, string? reportedBy)
        {
            cancellation.IsProcessed = true;
            cancellation.IsApproved = approved;
            if (decidedAtHeight > 0)
                cancellation.DecidedAtHeight = decidedAtHeight;
            if (!string.IsNullOrEmpty(decidedByTxHash))
                cancellation.DecidedByTxHash = decidedByTxHash;
            if (!string.IsNullOrEmpty(signedBtcTxId))
            {
                cancellation.SignedBtcTxId = signedBtcTxId;
                cancellation.SignedTxReportedBy = reportedBy;
            }
        }

        public static void MarkAsProcessed(string cancellationUID, bool approved) =>
            MarkAsProcessed(cancellationUID, approved, 0, null, null, null);

        public static void MarkAsProcessed(string cancellationUID, bool approved, long decidedAtHeight, string? decidedByTxHash, string? signedBtcTxId, string? reportedBy)
        {
            try
            {
                var cancellations = GetDb();
                var cancellation = cancellations.FindOne(x => x.CancellationUID == cancellationUID);

                if (cancellation != null)
                {
                    RecordDecision(cancellation, approved, decidedAtHeight, decidedByTxHash, signedBtcTxId, reportedBy);
                    Write(cancellations, cancellation);
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError(ex.ToString(), "VBTCWithdrawalCancellation.MarkAsProcessed()");
                Extensions.DbExtensions.NoteFailure(DbContext.RSRV_VBTC_V2_CANCELLATIONS);
            }
        }

        public static void DeleteCancellation(string cancellationUID)
        {
            try
            {
                var cancellations = GetDb();
                cancellations.DeleteManySafe(x => x.CancellationUID == cancellationUID);
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError(ex.ToString(), "VBTCWithdrawalCancellation.DeleteCancellation()");
                Extensions.DbExtensions.NoteFailure(DbContext.RSRV_VBTC_V2_CANCELLATIONS);
            }
        }

        public static bool HasValidatorVoted(string cancellationUID, string validatorAddress)
        {
            var cancellation = GetCancellation(cancellationUID);
            if (cancellation != null && cancellation.ValidatorVotes != null)
            {
                return cancellation.ValidatorVotes.ContainsKey(validatorAddress);
            }
            return false;
        }

        public static int GetVotePercentage(string cancellationUID, int totalValidators)
        {
            var cancellation = GetCancellation(cancellationUID);
            if (cancellation != null && totalValidators > 0)
            {
                return (int)((double)cancellation.ApproveCount / totalValidators * 100);
            }
            return 0;
        }
        #endregion
    }
}
