using ReserveBlockCore.Data;
using ReserveBlockCore.Utilities;

namespace ReserveBlockCore.Bitcoin.Models
{
    /// <summary>
    /// Tracks vBTC V2 withdrawal requests for replay attack prevention and audit trail
    /// </summary>
    public class VBTCWithdrawalRequest
    {
        public long Id { get; set; }
        public string RequestorAddress { get; set; }
        public long OriginalRequestTime { get; set; }
        public string OriginalSignature { get; set; }
        public string OriginalUniqueId { get; set; }
        public long Timestamp { get; set; }
        public string SmartContractUID { get; set; }
        public decimal Amount { get; set; }
        public string BTCDestination { get; set; }
        public int FeeRate { get; set; }
        public string TransactionHash { get; set; }
        public bool IsCompleted { get; set; }
        public VBTCWithdrawalStatus Status { get; set; }
        public string? BTCTxHash { get; set; }

        #region Get DB
        public static LiteDB.ILiteCollection<VBTCWithdrawalRequest>? GetVBTCWithdrawalRequestDb()
        {
            try
            {
                var vwr = DbContext.DB_VBTCWithdrawalRequests.GetCollection<VBTCWithdrawalRequest>(DbContext.RSRV_VBTC_WITHDRAWAL_REQUESTS);
                return vwr;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError(ex.ToString(), "VBTCWithdrawalRequest.GetVBTCWithdrawalRequestDb()");
                return null;
            }
        }
        #endregion

        #region Get Withdrawal Request by UniqueId
        /// <summary>
        /// Retrieve a withdrawal request by unique ID
        /// Used for replay attack prevention
        /// </summary>
        public static VBTCWithdrawalRequest? GetByUniqueId(string address, string uniqueId, string scUID)
        {
            var vwrDb = GetVBTCWithdrawalRequestDb();
            if (vwrDb == null)
            {
                ErrorLogUtility.LogError("GetVBTCWithdrawalRequestDb() returned a null value.", "VBTCWithdrawalRequest.GetByUniqueId()");
                return null;
            }

            var request = vwrDb.Query()
                .Where(x => x.RequestorAddress == address && 
                            x.OriginalUniqueId == uniqueId && 
                            x.SmartContractUID == scUID)
                .FirstOrDefault();

            return request;
        }
        #endregion

        #region Check for Incomplete Requests
        /// <summary>
        /// Check if there are any incomplete withdrawal requests for this address and contract
        /// SECURITY: Prevents multiple simultaneous withdrawals (Hard Requirement #4)
        /// </summary>
        public static bool HasIncompleteRequest(string address, string scUID)
        {
            var vwrDb = GetVBTCWithdrawalRequestDb();
            if (vwrDb == null)
            {
                ErrorLogUtility.LogError("GetVBTCWithdrawalRequestDb() returned a null value.", "VBTCWithdrawalRequest.HasIncompleteRequest()");
                return false;
            }

            var incompleteRequests = vwrDb.Query()
                .Where(x => x.RequestorAddress == address && 
                            x.SmartContractUID == scUID && 
                            !x.IsCompleted)
                .ToList();

            return incompleteRequests.Any();
        }
        #endregion

        #region Get Incomplete Withdrawal Amount
        /// <summary>
        /// Gets the total amount of incomplete (pending) withdrawals for an address and smart contract.
        /// This is used to calculate available balance during withdrawal validation.
        /// SECURITY: Prevents spam attack where user could request multiple withdrawals exceeding their balance.
        /// </summary>
        public static decimal GetIncompleteWithdrawalAmount(string address, string scUID)
        {
            var vwrDb = GetVBTCWithdrawalRequestDb();
            if (vwrDb == null)
            {
                ErrorLogUtility.LogError("GetVBTCWithdrawalRequestDb() returned a null value.", "VBTCWithdrawalRequest.GetIncompleteWithdrawalAmount()");
                return 0M;
            }

            var incompleteWithdrawals = vwrDb.Query()
                .Where(x => x.RequestorAddress == address && 
                            x.SmartContractUID == scUID && 
                            !x.IsCompleted)
                .ToList();

            if (incompleteWithdrawals.Any())
            {
                return incompleteWithdrawals.Sum(x => x.Amount);
            }

            return 0M;
        }
        #endregion

        #region Save Withdrawal Request
        /// <summary>
        /// Save or update a withdrawal request
        /// </summary>
        public static bool Save(VBTCWithdrawalRequest request, bool update = false)
        {
            var vwrDb = GetVBTCWithdrawalRequestDb();
            if (vwrDb == null)
            {
                ErrorLogUtility.LogError("GetVBTCWithdrawalRequestDb() returned a null value.", "VBTCWithdrawalRequest.Save()");
                return false;
            }

            var existingRequest = vwrDb.FindOne(x => x.OriginalUniqueId == request.OriginalUniqueId);
            if (existingRequest != null)
            {
                if (!update)
                    return false;

                existingRequest.Status = request.Status;
                existingRequest.TransactionHash = request.TransactionHash;
                existingRequest.IsCompleted = request.IsCompleted;
                existingRequest.BTCTxHash = request.BTCTxHash;

                vwrDb.UpdateSafe(existingRequest);
                return true;
            }
            else
            {
                vwrDb.InsertSafe(request);
                return true;
            }
        }
        #endregion

        #region Complete Withdrawal Request
        /// <summary>
        /// Mark a withdrawal request as completed
        /// </summary>
        public static bool Complete(string address, string uniqueId, string scUID, string txHash, string? btcTxHash = null)
        {
            var request = GetByUniqueId(address, uniqueId, scUID);

            if (request == null)
                return false;

            request.Status = VBTCWithdrawalStatus.Completed;
            request.TransactionHash = txHash;
            request.BTCTxHash = btcTxHash;
            request.IsCompleted = true;

            var result = Save(request, true);
            return result;
        }
        #endregion

        #region Cancel Withdrawal Request
        /// <summary>
        /// Mark a withdrawal request as cancelled
        /// </summary>
        public static bool Cancel(string address, string uniqueId, string scUID)
        {
            var request = GetByUniqueId(address, uniqueId, scUID);

            if (request == null)
                return false;

            request.Status = VBTCWithdrawalStatus.Cancelled;
            request.IsCompleted = true;

            var result = Save(request, true);
            return result;
        }
        #endregion
    }
}
