using System;
using LiteDB;
using ReserveBlockCore.Data;
using ReserveBlockCore.Models;

namespace ReserveBlockCore.Bitcoin.Models
{
    /// <summary>
    /// Consensus bridge lock state for <see cref="TransactionType.VBTC_V2_BRIDGE_LOCK"/> /
    /// <see cref="TransactionType.VBTC_V2_BRIDGE_UNLOCK"/>. Replicated on every node when
    /// processing blocks (same pattern as <see cref="VBTCWithdrawalRequest"/>).
    /// </summary>
    public class VBTCBridgeLockState
    {
        [BsonId]
        public string LockId { get; set; } = string.Empty;

        public string SmartContractUID { get; set; } = string.Empty;
        public string OwnerAddress { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public long AmountSats { get; set; }
        public string EvmDestination { get; set; } = string.Empty;
        public string LockTxHash { get; set; } = string.Empty;
        public long LockTimestamp { get; set; }

        public bool IsUnlocked { get; set; }
        public string? UnlockTxHash { get; set; }
        public string? ExitBurnTxHash { get; set; }

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

        /// <summary>Called after tokenization credit is applied for an unlock TX.</summary>
        public static bool TryFinalizeUnlock(Transaction tx, VBTCBridgeLockState rec, string exitBurnTxHash)
        {
            try
            {
                if (rec.IsUnlocked)
                    return false;
                rec.IsUnlocked = true;
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
