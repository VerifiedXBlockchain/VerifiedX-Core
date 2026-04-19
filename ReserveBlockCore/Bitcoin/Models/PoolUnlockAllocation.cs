namespace ReserveBlockCore.Bitcoin.Models
{
    /// <summary>
    /// Deterministic allocation entry for a pool-based bridge unlock or BTC exit.
    /// Serialized inside the VBTC_V2_BRIDGE_POOL_UNLOCK / VBTC_V2_BRIDGE_EXIT_TO_BTC
    /// transaction Data payload so all nodes process the exact same state changes.
    /// </summary>
    public class PoolUnlockAllocation
    {
        public string LockId { get; set; } = string.Empty;
        public decimal UnlockAmount { get; set; }
        /// <summary>
        /// The smart contract UID that owns this lock.
        /// Used by BTC exits to group allocations by FROST key group (each contract has its own deposit address).
        /// </summary>
        public string SmartContractUID { get; set; } = string.Empty;
    }

    /// <summary>
    /// Records a per-contract BTC withdrawal within a multi-contract BTC exit.
    /// Serialized inside the VBTC_V2_BRIDGE_EXIT_TO_BTC transaction Data payload.
    /// </summary>
    public class BtcExitWithdrawalRecord
    {
        public string SmartContractUID { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public long AmountSats { get; set; }
        public string BtcTxHash { get; set; } = string.Empty;
    }
}
