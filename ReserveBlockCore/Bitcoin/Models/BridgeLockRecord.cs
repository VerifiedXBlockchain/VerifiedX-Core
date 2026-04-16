using System;
using LiteDB;
using ReserveBlockCore.Data;
using ReserveBlockCore.Models;
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
        /// <summary>VerifiedX VBTC_V2_BRIDGE_LOCK transaction hash (after broadcast).</summary>
        public string? VfxLockTxHash { get; set; }
        /// <summary>True after the lock is applied in the state trei (included in a block).</summary>
        public bool VfxLockConfirmedOnChain { get; set; }
        /// <summary>VFX block height when the lock TX was included (used as mint attestation nonce for VBTCbV2).</summary>
        public long VfxLockBlockHeight { get; set; }
        public string? BaseTxHash { get; set; }
        /// <summary>Set when burnForExit on Base is observed and the lock is released on VFX (demo bridge-back).</summary>
        public string? ExitBurnTxHash { get; set; }
        public BridgeLockStatus Status { get; set; } = BridgeLockStatus.Locked;
        public long CreatedAtUtc { get; set; }
        public long? RelayedAtUtc { get; set; }
        public long? FinalizedAtUtc { get; set; }
        public string? ErrorMessage { get; set; }

        public Dictionary<string, string>? ValidatorSignatures { get; set; }
        public int RequiredSignatures { get; set; }
        public long MintNonce { get; set; }
        public string? BtcExitDestination { get; set; }
        public string? BtcExitTxHash { get; set; }
        public decimal? BtcExitAmountSent { get; set; }
        public decimal? BtcExitFeePaid { get; set; }

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

        /// <summary>Locks confirmed on VFX that still need validator mint attestations (V2).</summary>
        public static List<BridgeLockRecord> GetPendingV2Attestations()
        {
            if (!ReserveBlockCore.Bitcoin.Services.BaseBridgeService.IsV2MintBridge)
                return new List<BridgeLockRecord>();
            var col = GetCollection();
            return col.Find(x =>
                x.VfxLockConfirmedOnChain &&
                !string.IsNullOrEmpty(x.VfxLockTxHash) &&
                (x.Status == BridgeLockStatus.Locked || x.Status == BridgeLockStatus.AttestationPending)).ToList();
        }

        /// <summary>
        /// Local DB amounts still reserved in the UI before the VFX state trei reflects the lock
        /// (<see cref="VfxLockConfirmedOnChain"/> false). Once confirmed, transparent balance already
        /// includes the on-chain lock debit — do not double-subtract.
        /// </summary>
        public static decimal GetLockedAmount(string ownerAddress, string scUID)
        {
            var col = GetCollection();
            var active = col.Find(x =>
                x.OwnerAddress == ownerAddress &&
                x.SmartContractUID == scUID &&
                !x.VfxLockConfirmedOnChain &&
                (x.Status == BridgeLockStatus.Locked || x.Status == BridgeLockStatus.ProofSubmitted));
            return active.Sum(x => x.Amount);
        }

        /// <summary>Called when a VBTC_V2_BRIDGE_LOCK is applied chain-wide (state trei update).</summary>
        public static bool TryMarkVfxLockConfirmed(string lockId, string vfxTxHash, long vfxBlockHeight = 0)
        {
            try
            {
                var col = GetCollection();
                var r = col.FindOne(x => x.LockId == lockId.Trim());
                if (r == null) return false;
                if (!string.IsNullOrEmpty(r.VfxLockTxHash) && !string.Equals(r.VfxLockTxHash, vfxTxHash, StringComparison.OrdinalIgnoreCase))
                    return false;
                r.VfxLockConfirmedOnChain = true;
                if (string.IsNullOrEmpty(r.VfxLockTxHash))
                    r.VfxLockTxHash = vfxTxHash;
                if (vfxBlockHeight > 0)
                    r.VfxLockBlockHeight = vfxBlockHeight;
                col.Update(r);
                return true;
            }
            catch
            {
                return false;
            }
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
                else if (newStatus == BridgeLockStatus.Minted || newStatus == BridgeLockStatus.MintedOnBase || newStatus == BridgeLockStatus.Redeemed || newStatus == BridgeLockStatus.Unlocked || newStatus == BridgeLockStatus.UnlockedOnVFX || newStatus == BridgeLockStatus.BTCExitComplete)
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
        /// Validates a local <see cref="BridgeLockStatus.Minted"/> row against an <c>ExitBurned</c> event (before broadcasting VFX unlock).
        /// </summary>
        public static bool ValidateExitBurnMatchesMinted(string lockId, string burnerAddress, long amountSats)
        {
            try
            {
                var record = GetByLockId(lockId);
                if (record == null || (record.Status != BridgeLockStatus.Minted && record.Status != BridgeLockStatus.MintedOnBase)) return false;
                if (record.AmountSats != amountSats) return false;
                if (string.IsNullOrEmpty(burnerAddress) ||
                    !string.Equals(record.EvmDestination.Trim(), burnerAddress.Trim(), StringComparison.OrdinalIgnoreCase))
                    return false;
                if (!string.IsNullOrEmpty(record.ExitBurnTxHash)) return false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// After a matching burn is seen and before/after <see cref="VBTCService.CreateBridgeUnlockTx"/>: mark local DB as redeeming.
        /// </summary>
        public static bool TryMarkRedeemingForExit(string lockId, string burnTxHash)
        {
            try
            {
                var col = GetCollection();
                var record = col.FindOne(x => x.LockId == lockId.Trim());
                if (record == null || (record.Status != BridgeLockStatus.Minted && record.Status != BridgeLockStatus.MintedOnBase)) return false;
                if (!string.IsNullOrEmpty(record.ExitBurnTxHash)) return false;

                record.Status = BridgeLockStatus.Redeeming;
                record.ExitBurnTxHash = burnTxHash;
                col.Update(record);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Called from <see cref="StateData"/> when <see cref="TransactionType.VBTC_V2_BRIDGE_UNLOCK"/> is applied on-chain.
        /// </summary>
        public static void FinalizeFromChainUnlockIfPending(string lockId)
        {
            try
            {
                var col = GetCollection();
                var record = col.FindOne(x => x.LockId == lockId.Trim());
                if (record == null || record.Status != BridgeLockStatus.Redeeming) return;

                record.Status = BridgeLockStatus.Unlocked;
                record.FinalizedAtUtc = TimeUtil.GetTime();
                col.Update(record);
            }
            catch
            {
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
        Failed = 6,
        AttestationPending = 7,
        AttestationReady = 8,
        MintedOnBase = 9,
        ExitBurned = 10,
        UnlockedOnVFX = 11,
        BTCExitBurned = 12,
        BTCExitSigning = 13,
        BTCExitBroadcast = 14,
        BTCExitComplete = 15,
        Expired = 16
    }
}