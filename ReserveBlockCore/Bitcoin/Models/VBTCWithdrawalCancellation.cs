using LiteDB;
using ReserveBlockCore.Data;
using ReserveBlockCore.Extensions;
using ReserveBlockCore.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ReserveBlockCore.Bitcoin.Models
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
            var db = DbContext.DB_Assets.GetCollection<VBTCWithdrawalCancellation>(DbContext.RSRV_VBTC_V2_CANCELLATIONS);
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

        public static VBTCWithdrawalCancellation? GetCancellationByWithdrawalHash(string withdrawalRequestHash)
        {
            var cancellations = GetDb();
            if (cancellations != null)
            {
                var cancellation = cancellations.FindOne(x => x.WithdrawalRequestHash == withdrawalRequestHash);
                if (cancellation != null)
                {
                    return cancellation;
                }
            }

            return null;
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
