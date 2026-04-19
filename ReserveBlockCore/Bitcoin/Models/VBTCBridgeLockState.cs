using System;
using System.Collections.Generic;
using LiteDB;
using ReserveBlockCore.Data;
using ReserveBlockCore.Models;
using ReserveBlockCore.Utilities;

namespace ReserveBlockCore.Bitcoin.Models
{
    /// <summary>
    /// Consensus bridge lock state for <see cref="TransactionType.VBTC_V2_BRIDGE_LOCK"/> /
    /// <see cref="TransactionType.VBTC_V2_BRIDGE_POOL_UNLOCK"/>. Replicated on every node when
    /// processing blocks (same pattern as <see cref="VBTCWithdrawalRequest"/>).
    /// 
    /// V3: Supports partial unlocks — a single lock can be consumed across multiple pool unlocks.
    /// </summary>
    public class VBTCBridgeLockState
    {
        [BsonId]
        public string LockId { get; set; } = string.Empty;

        public string SmartContractUID { get; set; } = string.Empty;
        public string OwnerAddress { get; set; } = string.Empty;
        
        /// <summary>Original locked amount in BTC.</summary>
        public decimal Amount { get; set; }
        /// <summary>Original locked amount in satoshis.</summary>
        public long AmountSats { get; set; }
        
        /// <summary>Total amount already unlocked (via pool unlocks). Initially 0.</summary>
        public decimal UnlockedAmount { get; set; }
        /// <summary>Total sats already unlocked. Initially 0.</summary>
        public long UnlockedAmountSats { get; set; }
        
        public string EvmDestination { get; set; } = string.Empty;
        public string LockTxHash { get; set; } = string.Empty;
        public long LockTimestamp { get; set; }

        /// <summary>True when fully consumed (UnlockedAmount >= Amount).</summary>
        public bool IsUnlocked { get; set; }
        public string? UnlockTxHash { get; set; }
        public string? ExitBurnTxHash { get; set; }

        /// <summary>
        /// When true, this lock is permanently excluded from FIFO allocation.
        /// Set on-chain via VBTC_V2_BRIDGE_EXIT_TO_BTC_FAIL when FROST signing
        /// fails because the contract's deposit UTXO is unspendable.
        /// </summary>
        public bool IsBlacklisted { get; set; }

        /// <summary>Reason the lock was blacklisted (for diagnostics).</summary>
        public string? BlacklistReason { get; set; }

        /// <summary>Remaining BTC available to unlock.</summary>
        [BsonIgnore]
        public decimal RemainingAmount => Amount - UnlockedAmount;

        /// <summary>Remaining sats available to unlock.</summary>
        [BsonIgnore]
        public long RemainingAmountSats => AmountSats - UnlockedAmountSats;

        private const string CollectionName = "rsrv_vbtc_bridge_locks";

        public static ILiteCollection<VBTCBridgeLockState> GetCollection()
        {
            var c = DbContext.DB_VBTCWithdrawalRequests.GetCollection<VBTCBridgeLockState>(CollectionName);
            c.EnsureIndex(x => x.OwnerAddress, false);
            return c;
        }

        public static VBTCBridgeLockState? GetByLockId(string lockId)
        {
            if (string.IsNullOrWhiteSpace(lockId)) return null;
            return GetCollection().FindOne(x => x.LockId == lockId.Trim());
        }

        /// <summary>
        /// Returns all locks that still have remaining balance, ordered by LockTimestamp ASC (FIFO).
        /// Used by pool-based unlock to select inputs.
        /// </summary>
        public static List<VBTCBridgeLockState> GetAvailableLocksFIFO()
        {
            return GetCollection()
                .Find(x => !x.IsUnlocked && !x.IsBlacklisted)
                .Where(x => x.RemainingAmount > 0)
                .OrderBy(x => x.LockTimestamp)
                .ToList();
        }

        /// <summary>
        /// Blacklist a lock on-chain, preventing it from being selected for future FIFO allocations.
        /// Also reverses the partial unlock so the deducted amount is restored (but remains blacklisted).
        /// </summary>
        public static bool BlacklistLock(string lockId, decimal restoreAmount, long restoreSats, string reason)
        {
            var col = GetCollection();
            var rec = col.FindOne(x => x.LockId == lockId);
            if (rec == null) return false;

            rec.IsBlacklisted = true;
            rec.BlacklistReason = reason;

            // Restore the previously deducted amount
            if (restoreAmount > 0)
            {
                rec.UnlockedAmount = Math.Max(0, rec.UnlockedAmount - restoreAmount);
                rec.UnlockedAmountSats = Math.Max(0, rec.UnlockedAmountSats - restoreSats);
                rec.IsUnlocked = false; // un-mark fully consumed since we restored
            }

            return col.Update(rec);
        }

        /// <summary>
        /// Returns the total amount of vBTC currently available in the bridge lock pool.
        /// </summary>
        public static decimal GetTotalAvailableAmount()
        {
            var locks = GetCollection().Find(x => !x.IsUnlocked).ToList();
            return locks.Sum(x => x.RemainingAmount);
        }

        /// <summary>Insert when a bridge lock TX is applied in <see cref="StateData"/>.</summary>
        public static bool TryInsertFromLockTx(Transaction tx, string scUID, string lockId, decimal amount, long amountSats, string evmDestination)
        {
            try
            {
                lockId = lockId.Trim();
                if (GetByLockId(lockId) != null)
                    return false;

                var rec = new VBTCBridgeLockState
                {
                    LockId = lockId,
                    SmartContractUID = scUID,
                    OwnerAddress = tx.FromAddress,
                    Amount = amount,
                    AmountSats = amountSats,
                    UnlockedAmount = 0,
                    UnlockedAmountSats = 0,
                    EvmDestination = evmDestination.Trim(),
                    LockTxHash = tx.Hash,
                    LockTimestamp = tx.Timestamp,
                    IsUnlocked = false
                };
                GetCollection().Insert(rec);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Apply a partial or full unlock to this lock record (pool-based unlock).
        /// Returns true if successfully applied.
        /// </summary>
        public static bool ApplyPartialUnlock(string lockId, decimal unlockAmount, long unlockAmountSats)
        {
            try
            {
                var rec = GetByLockId(lockId);
                if (rec == null || rec.IsUnlocked) return false;

                if (unlockAmount > rec.RemainingAmount || unlockAmountSats > rec.RemainingAmountSats)
                {
                    ErrorLogUtility.LogError(
                        $"ApplyPartialUnlock: unlock amount {unlockAmount} exceeds remaining {rec.RemainingAmount} for lock {lockId}",
                        "VBTCBridgeLockState.ApplyPartialUnlock");
                    return false;
                }

                rec.UnlockedAmount += unlockAmount;
                rec.UnlockedAmountSats += unlockAmountSats;

                // Mark fully consumed
                if (rec.UnlockedAmount >= rec.Amount || rec.UnlockedAmountSats >= rec.AmountSats)
                    rec.IsUnlocked = true;

                GetCollection().Update(rec);
                return true;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"ApplyPartialUnlock error: {ex.Message}", "VBTCBridgeLockState.ApplyPartialUnlock");
                return false;
            }
        }

        /// <summary>Called after tokenization credit is applied for an unlock TX (legacy single-lock path).</summary>
        public static bool TryFinalizeUnlock(Transaction tx, VBTCBridgeLockState rec, string exitBurnTxHash)
        {
            try
            {
                if (rec.IsUnlocked)
                    return false;
                rec.IsUnlocked = true;
                rec.UnlockedAmount = rec.Amount;
                rec.UnlockedAmountSats = rec.AmountSats;
                rec.UnlockTxHash = tx.Hash;
                rec.ExitBurnTxHash = exitBurnTxHash.Trim();
                GetCollection().Update(rec);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
