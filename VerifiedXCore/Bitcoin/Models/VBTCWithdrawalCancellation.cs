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
        #endregion

        #region Database Methods
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
        public static bool HasPendingCancellation(string withdrawalRequestHash, long nowSeconds, string? scUID)
        {
            if (string.IsNullOrWhiteSpace(withdrawalRequestHash)) return false;
            try
            {
                var db = GetDb();
                if (db == null) return false;
                var cutoff = nowSeconds - PENDING_CANCELLATION_MAX_AGE_SECONDS;
                return db.Find(x => x.WithdrawalRequestHash == withdrawalRequestHash)
                         .Where(x => string.IsNullOrEmpty(scUID) || x.SmartContractUID == scUID)
                         .Any(x => !x.IsProcessed && x.RequestTime >= cutoff);
            }
            catch { return false; }
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

            return cancellations.Find(x => x.WithdrawalRequestHash == withdrawalRequestHash)
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
            }
        }

        public static void AddVote(string cancellationUID, string validatorAddress, bool approve)
        {
            try
            {
                var cancellations = GetDb();
                var cancellation = cancellations.FindOne(x => x.CancellationUID == cancellationUID);

                if (cancellation != null && !cancellation.IsProcessed)
                {
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

                    cancellations.UpdateSafe(cancellation);
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError(ex.ToString(), "VBTCWithdrawalCancellation.AddVote()");
            }
        }

        public static void MarkAsProcessed(string cancellationUID, bool approved)
        {
            try
            {
                var cancellations = GetDb();
                var cancellation = cancellations.FindOne(x => x.CancellationUID == cancellationUID);

                if (cancellation != null)
                {
                    cancellation.IsProcessed = true;
                    cancellation.IsApproved = approved;
                    cancellations.UpdateSafe(cancellation);
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError(ex.ToString(), "VBTCWithdrawalCancellation.MarkAsProcessed()");
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
