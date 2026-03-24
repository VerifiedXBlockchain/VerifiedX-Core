using LiteDB;
using ReserveBlockCore.Data;
using ReserveBlockCore.Utilities;

namespace ReserveBlockCore.Bitcoin.Models
{
    /// <summary>
    /// Tracks vBTC locked on VerifiedX for bridging to Base as vBTC.b ERC-20.
    /// Stored in DB_vBTC LiteDB collection.
    /// </summary>
    public class BridgeLockRecord
    {
        [BsonId]
        public long Id { get; set; }
        public string LockId { get; set; } = string.Empty;
        public string SmartContractUID { get; set; } = string.Empty;
        public string OwnerAddress { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public long AmountSats { get; set; }
        public string EvmDestination { get; set; } = string.Empty;
        public string? BaseTxHash { get; set; }
        /// <summary>Set when burnForExit on Base is observed and the lock is released on VFX (demo bridge-back).</summary>
        public string? ExitBurnTxHash { get; set; }
        public BridgeLockStatus Status { get; set; } = BridgeLockStatus.Locked;
        public long CreatedAtUtc { get; set; }
        public long? RelayedAtUtc { get; set; }
        public long? FinalizedAtUtc { get; set; }
        public string? ErrorMessage { get; set; }

        #region DB Constants
        private const string COLLECTION_NAME = "rsrv_bridge_locks";
        #endregion

        #region Static DB Methods

        public static ILiteCollection<BridgeLockRecord> GetCollection()
        {
            return DbContext.DB_vBTC.GetCollection<BridgeLockRecord>(COLLECTION_NAME);
        }

        public static BridgeLockRecord? GetByLockId(string lockId)
        {
            var col = GetCollection();
            return col.FindOne(x => x.LockId == lockId);
        }

        public static List<BridgeLockRecord> GetBySmartContract(string scUID)
        {
            var col = GetCollection();
            return col.Find(x => x.SmartContractUID == scUID).ToList();
        }

        public static List<BridgeLockRecord> GetByOwner(string ownerAddress)
        {
            var col = GetCollection();
            return col.Find(x => x.OwnerAddress == ownerAddress).ToList();
        }

        public static List<BridgeLockRecord> GetByStatus(BridgeLockStatus status)
        {
            var col = GetCollection();
            return col.Find(x => x.Status == status).ToList();
        }

        public static List<BridgeLockRecord> GetPendingRelays()
        {
            var col = GetCollection();
            return col.Find(x => x.Status == BridgeLockStatus.Locked).ToList();
        }

        /// <summary>
        /// Get total bridge-locked amount for a given address and contract (in BTC decimal).
        /// Used to deduct from available balance.
        /// </summary>
        public static decimal GetLockedAmount(string ownerAddress, string scUID)
        {
            var col = GetCollection();
            var active = col.Find(x =>
                x.OwnerAddress == ownerAddress &&
                x.SmartContractUID == scUID &&
                (x.Status == BridgeLockStatus.Locked ||
                 x.Status == BridgeLockStatus.ProofSubmitted ||
                 x.Status == BridgeLockStatus.Minted));
            return active.Sum(x => x.Amount);
        }

        public static bool Save(BridgeLockRecord record)
        {
            try
            {
                var col = GetCollection();
                col.Upsert(record);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool UpdateStatus(string lockId, BridgeLockStatus newStatus, string? baseTxHash = null, string? errorMessage = null)
        {
            try
            {
                var col = GetCollection();
                var record = col.FindOne(x => x.LockId == lockId);
                if (record == null) return false;

                record.Status = newStatus;
                if (baseTxHash != null) record.BaseTxHash = baseTxHash;
                if (errorMessage != null) record.ErrorMessage = errorMessage;

                if (newStatus == BridgeLockStatus.ProofSubmitted)
                    record.RelayedAtUtc = TimeUtil.GetTime();
                else if (newStatus == BridgeLockStatus.Minted || newStatus == BridgeLockStatus.Redeemed || newStatus == BridgeLockStatus.Unlocked)
                    record.FinalizedAtUtc = TimeUtil.GetTime();

                col.Update(record);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// After Base <c>burnForExit</c> is indexed: verify lock, full amount, burner == Evm destination, then unlock on VFX.
        /// </summary>
        public static bool TryUnlockFromBaseExit(string lockId, string burnerAddress, long amountSats, string burnTxHash)
        {
            try
            {
                var col = GetCollection();
                var record = col.FindOne(x => x.LockId == lockId.Trim());
                if (record == null) return false;
                if (record.Status != BridgeLockStatus.Minted) return false;
                if (record.AmountSats != amountSats) return false;
                if (string.IsNullOrEmpty(burnerAddress) ||
                    !string.Equals(record.EvmDestination.Trim(), burnerAddress.Trim(), StringComparison.OrdinalIgnoreCase))
                    return false;
                if (!string.IsNullOrEmpty(record.ExitBurnTxHash)) return false;

                record.Status = BridgeLockStatus.Unlocked;
                record.ExitBurnTxHash = burnTxHash;
                record.FinalizedAtUtc = TimeUtil.GetTime();
                col.Update(record);
                return true;
            }
            catch
            {
                return false;
            }
        }

        #endregion
    }

    public enum BridgeLockStatus
    {
        Locked = 0,
        ProofSubmitted = 1,
        Minted = 2,
        Redeeming = 3,
        Redeemed = 4,
        Unlocked = 5,
        Failed = 6
    }
}