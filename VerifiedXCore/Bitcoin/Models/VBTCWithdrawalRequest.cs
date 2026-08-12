using VerifiedXCore.Data;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Bitcoin.Models
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
        // S3C §0: block height the request was mined at (0 = submitted but not yet mined).
        // Drives the per-contract anti-grief expiry in HasActiveContractRequest.
        public long RequestBlockHeight { get; set; }

        // Local-only observability (never consensus-read): what/when/why the last FROST signing
        // attempt for this request failed, and the txid of the last successfully signed BTC tx
        // (traceable even if the caller died before broadcasting/completing).
        public string? LastSigningFailureCode { get; set; }
        public long? LastSigningFailureAt { get; set; }
        public string? LastSigningSessionId { get; set; }
        public string? LastSignedBtcTxId { get; set; }

        // S3C §0: ~1 hour at ~10s/block; matches the existing 1-hour FROST ceremony TTL.
        public const long EXPIRY_BLOCKS = 360;

        // Wall-clock equivalent of EXPIRY_BLOCKS (~1 hour), used only by the LOCAL per-user gates
        // as a fallback for rows that never got a mined height (RequestBlockHeight == 0) — e.g.
        // rows written by RequestWithdrawalRaw before heights were stamped, or legacy pre-upgrade
        // rows. Wall clock is acceptable here because these gates are local-only, never consensus.
        public const long EXPIRY_SECONDS = 3600;

        /// <summary>
        /// Shared read-time expiry predicate for the LOCAL per-user gates (GetActiveRequest,
        /// GetIncompleteWithdrawalAmount, HasIncompleteRequest). A request stops blocking its
        /// requester once it completes OR ages out of the anti-grief window — the same window after
        /// which the per-contract consensus gate (HasActiveContractRequest) frees the contract for
        /// everyone. Without this, a stalled request (failed FROST ceremony, dead web-wallet
        /// session, node restart) locked its requester out of the contract permanently.
        /// NOT used by consensus paths — HasActiveContractRequest has its own height-only rule.
        /// </summary>
        public static bool IsStillBlocking(VBTCWithdrawalRequest request, long currentHeight, long currentTime)
        {
            if (request.IsCompleted)
                return false;

            if (request.RequestBlockHeight > 0)
                return currentHeight - request.RequestBlockHeight <= EXPIRY_BLOCKS;

            // No mined height recorded — fall back to the request's own timestamp so legacy and
            // local-only rows self-heal instead of blocking forever.
            return currentTime - request.Timestamp <= EXPIRY_SECONDS;
        }

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
            return HasIncompleteRequest(address, scUID, Globals.LastBlock?.Height ?? 0, TimeUtil.GetTime());
        }

        public static bool HasIncompleteRequest(string address, string scUID, long currentHeight, long currentTime)
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

            return incompleteRequests.Any(x => IsStillBlocking(x, currentHeight, currentTime));
        }
        #endregion

        #region Per-Contract Active Request Gate (S3C §0)
        /// <summary>
        /// S3C §0: per-CONTRACT active-withdrawal gate (not per-user). Returns true if the
        /// contract has ANY incomplete withdrawal request that still blocks new requests.
        /// A request blocks iff its RequestBlockHeight is unset (0 = submitted, not yet mined —
        /// fail toward locked so it cannot slip the mempool race) OR it is still inside the
        /// EXPIRY_BLOCKS anti-grief window. Older incomplete requests are non-blocking; the
        /// next valid request overwrites them. Stuck requests are recovered via the existing
        /// cancellation vote.
        /// </summary>
        /// <param name="currentHeight">Height to measure expiry against — Globals.LastBlock.Height
        /// at mempool/API time, the block-being-validated's height during block validation
        /// (must be the block height, not the chain tip, so replay stays deterministic).</param>
        /// <param name="includeLocalOnlyRows">True (default) for LOCAL gates (API/bridge). CONSENSUS
        /// callers (mempool/block validation) must pass false: local-only rows (TransactionHash=="",
        /// created by RequestWithdrawalRaw) exist only on the API node that served the call, so a
        /// consensus rule reading them is a fork vector. The exclusion activates at
        /// Globals.V2WithdrawalExpiryFixHeight so historical replay stays byte-identical.</param>
        public static bool HasActiveContractRequest(string scUID, long currentHeight, bool includeLocalOnlyRows = true)
        {
            var vwrDb = GetVBTCWithdrawalRequestDb();
            if (vwrDb == null)
            {
                ErrorLogUtility.LogError("GetVBTCWithdrawalRequestDb() returned a null value.", "VBTCWithdrawalRequest.HasActiveContractRequest()");
                return false;
            }

            var incompleteRequests = vwrDb.Query()
                .Where(x => x.SmartContractUID == scUID && !x.IsCompleted)
                .ToList();

            var fixActive = currentHeight >= Globals.V2WithdrawalExpiryFixHeight;

            foreach (var r in incompleteRequests)
            {
                var isLocalOnlyRow = string.IsNullOrEmpty(r.TransactionHash);

                // Consensus callers ignore rows that exist on this node only (fork vector).
                if (fixActive && !includeLocalOnlyRows && isLocalOnlyRow)
                    continue;

                if (r.RequestBlockHeight == 0)
                {
                    // Legacy MINED rows (created before RequestBlockHeight existed) deserialize with
                    // height 0 and would block their contract forever. At/after activation they get a
                    // fixed grace window measured from the activation constant — identical on every
                    // node, no wall clock — then stop blocking permanently. (New mined rows always
                    // stamp a real height via StateData, so they can never be 0.)
                    if (fixActive && !isLocalOnlyRow)
                    {
                        if (currentHeight <= Globals.V2WithdrawalExpiryFixHeight + EXPIRY_BLOCKS)
                            return true;
                        continue;
                    }

                    // Pre-activation (and local-only rows on local gates): fail toward locked so an
                    // unmined request cannot slip the mempool race — byte-identical legacy behavior.
                    return true;
                }

                if (currentHeight - r.RequestBlockHeight <= EXPIRY_BLOCKS)
                    return true;
            }

            return false;
        }
        #endregion

        #region Per-Requestor Repeat-Offense Cooldown
        /// <summary>
        /// Anti-griefing: blocks after an expired-incomplete request during which the SAME requestor
        /// may not open another request on the same contract. Without this, any vBTC holder on a
        /// shared contract could lock all withdrawals indefinitely by requesting, letting the
        /// ceremony fail, and re-requesting the moment the 360-block window expires (~1h lock per
        /// tx fee, repeatable forever). 1080 blocks ≈ 3 windows.
        /// </summary>
        public const long REPEAT_REQUEST_COOLDOWN_BLOCKS = 1080;

        /// <summary>
        /// CONSENSUS RULE (active at/after Globals.V2WithdrawalExpiryFixHeight): true when the
        /// requestor has a prior MINED incomplete request on this contract whose anti-grief window
        /// expired within the last REPEAT_REQUEST_COOLDOWN_BLOCKS. Deterministic — derived solely
        /// from mined data (RequestBlockHeight stamped by StateData on every node) and the supplied
        /// height (block height during block validation, never the chain tip).
        /// </summary>
        public static bool IsRequestorInRepeatCooldown(string requestorAddress, string scUID, long currentHeight)
        {
            if (currentHeight < Globals.V2WithdrawalExpiryFixHeight)
                return false;

            var vwrDb = GetVBTCWithdrawalRequestDb();
            if (vwrDb == null)
            {
                ErrorLogUtility.LogError("GetVBTCWithdrawalRequestDb() returned a null value.", "VBTCWithdrawalRequest.IsRequestorInRepeatCooldown()");
                return false;
            }

            var expiredIncomplete = vwrDb.Query()
                .Where(x => x.RequestorAddress == requestorAddress &&
                            x.SmartContractUID == scUID &&
                            !x.IsCompleted)
                .ToList()
                .Where(x => x.RequestBlockHeight > 0 && !string.IsNullOrEmpty(x.TransactionHash));

            foreach (var r in expiredIncomplete)
            {
                var expiredAtHeight = r.RequestBlockHeight + EXPIRY_BLOCKS;
                if (currentHeight > expiredAtHeight && currentHeight - expiredAtHeight <= REPEAT_REQUEST_COOLDOWN_BLOCKS)
                    return true;
            }

            return false;
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
            return GetIncompleteWithdrawalAmount(address, scUID, Globals.LastBlock?.Height ?? 0, TimeUtil.GetTime());
        }

        public static decimal GetIncompleteWithdrawalAmount(string address, string scUID, long currentHeight, long currentTime)
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
                .ToList()
                .Where(x => IsStillBlocking(x, currentHeight, currentTime))
                .ToList();

            if (incompleteWithdrawals.Any())
            {
                return incompleteWithdrawals.Sum(x => x.Amount);
            }

            return 0M;
        }
        #endregion

        #region Get Completed Withdrawal Amount
        /// <summary>
        /// Gets the total amount of COMPLETED withdrawals for an address and smart contract.
        /// Completed withdrawals already reduced the BTC deposit address balance (ElectrumX reflects
        /// them) AND wrote a burn row (ToAddress "-") to the tokenization ledger. Owner balance math
        /// adds this amount back so the burn rows aren't double-counted against the deposit balance,
        /// while transfer debits and bridge locks (same "-" row shape, but BTC never left the deposit)
        /// correctly remain debited. Withdrawal records are consensus-critical and exist on ALL nodes.
        /// </summary>
        public static decimal GetCompletedWithdrawalAmount(string address, string scUID)
        {
            var vwrDb = GetVBTCWithdrawalRequestDb();
            if (vwrDb == null)
            {
                ErrorLogUtility.LogError("GetVBTCWithdrawalRequestDb() returned a null value.", "VBTCWithdrawalRequest.GetCompletedWithdrawalAmount()");
                return 0M;
            }

            var completedWithdrawals = vwrDb.Query()
                .Where(x => x.RequestorAddress == address &&
                            x.SmartContractUID == scUID &&
                            x.IsCompleted)
                .ToList();

            if (completedWithdrawals.Any())
            {
                return completedWithdrawals.Sum(x => x.Amount);
            }

            return 0M;
        }
        #endregion

        #region Save Withdrawal Request
        /// <summary>
        /// Save or update a withdrawal request
        /// FIND-005 FIX: Use composite key (RequestorAddress, OriginalUniqueId, SmartContractUID) for uniqueness
        /// Also supports lookup by TransactionHash for FIND-002 multi-user tracking
        /// </summary>
        public static bool Save(VBTCWithdrawalRequest request, bool update = false)
        {
            var vwrDb = GetVBTCWithdrawalRequestDb();
            if (vwrDb == null)
            {
                ErrorLogUtility.LogError("GetVBTCWithdrawalRequestDb() returned a null value.", "VBTCWithdrawalRequest.Save()");
                return false;
            }

            VBTCWithdrawalRequest? existingRequest = null;
            
            // FIND-002: First try to find by TransactionHash (for multi-user tracking updates)
            if (!string.IsNullOrEmpty(request.TransactionHash))
            {
                existingRequest = vwrDb.FindOne(x => x.TransactionHash == request.TransactionHash);
            }
            
            // FIND-005: If not found by TxHash, try composite key (for raw withdrawal flow)
            if (existingRequest == null && !string.IsNullOrEmpty(request.OriginalUniqueId))
            {
                existingRequest = vwrDb.FindOne(x => 
                    x.RequestorAddress == request.RequestorAddress && 
                    x.OriginalUniqueId == request.OriginalUniqueId && 
                    x.SmartContractUID == request.SmartContractUID);
            }

            if (existingRequest != null)
            {
                if (!update)
                    return false;

                existingRequest.Status = request.Status;
                existingRequest.TransactionHash = request.TransactionHash;
                existingRequest.IsCompleted = request.IsCompleted;
                existingRequest.BTCTxHash = request.BTCTxHash;
                // S3C §0: persist the mined block height when StateData updates the record at
                // mine time; never zero it back out on later completion/cancellation saves.
                if (request.RequestBlockHeight > 0)
                    existingRequest.RequestBlockHeight = request.RequestBlockHeight;

                // Local-only observability fields — carry forward when set, never blank out.
                if (!string.IsNullOrEmpty(request.LastSigningFailureCode))
                    existingRequest.LastSigningFailureCode = request.LastSigningFailureCode;
                if (request.LastSigningFailureAt.HasValue)
                    existingRequest.LastSigningFailureAt = request.LastSigningFailureAt;
                if (!string.IsNullOrEmpty(request.LastSigningSessionId))
                    existingRequest.LastSigningSessionId = request.LastSigningSessionId;
                if (!string.IsNullOrEmpty(request.LastSignedBtcTxId))
                    existingRequest.LastSignedBtcTxId = request.LastSignedBtcTxId;

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

        #region Get Active Request by Transaction Hash
        /// <summary>
        /// FIND-002 FIX: Get active withdrawal request by transaction hash
        /// Used during withdrawal completion to validate requester
        /// </summary>
        public static VBTCWithdrawalRequest? GetByTransactionHash(string txHash)
        {
            var vwrDb = GetVBTCWithdrawalRequestDb();
            if (vwrDb == null)
            {
                ErrorLogUtility.LogError("GetVBTCWithdrawalRequestDb() returned a null value.", "VBTCWithdrawalRequest.GetByTransactionHash()");
                return null;
            }

            var request = vwrDb.Query()
                .Where(x => x.TransactionHash == txHash)
                .FirstOrDefault();

            return request;
        }
        #endregion

        #region Get Active Request for User and Contract
        /// <summary>
        /// FIND-002 FIX: Get active (incomplete) withdrawal request for a specific user and contract
        /// Used during validation to check if user already has an active request
        /// </summary>
        public static VBTCWithdrawalRequest? GetActiveRequest(string address, string scUID)
        {
            return GetActiveRequest(address, scUID, Globals.LastBlock?.Height ?? 0, TimeUtil.GetTime());
        }

        public static VBTCWithdrawalRequest? GetActiveRequest(string address, string scUID, long currentHeight, long currentTime)
        {
            var vwrDb = GetVBTCWithdrawalRequestDb();
            if (vwrDb == null)
            {
                ErrorLogUtility.LogError("GetVBTCWithdrawalRequestDb() returned a null value.", "VBTCWithdrawalRequest.GetActiveRequest()");
                return null;
            }

            // Read-time expiry: a stalled request stops blocking its requester after the same
            // anti-grief window the per-contract gate uses, instead of blocking forever.
            var request = vwrDb.Query()
                .Where(x => x.RequestorAddress == address &&
                            x.SmartContractUID == scUID &&
                            !x.IsCompleted)
                .ToList()
                .FirstOrDefault(x => IsStillBlocking(x, currentHeight, currentTime));

            return request;
        }
        #endregion
    }
}
