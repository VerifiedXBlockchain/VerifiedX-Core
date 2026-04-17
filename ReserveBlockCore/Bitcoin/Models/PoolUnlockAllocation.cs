namespace ReserveBlockCore.Bitcoin.Models
{
    /// <summary>
    /// Deterministic allocation entry for a pool-based bridge unlock.
    /// Serialized inside the VBTC_V2_BRIDGE_POOL_UNLOCK transaction Data payload
    /// so all nodes process the exact same state changes.
    /// </summary>
    public class PoolUnlockAllocation
    {
        public string LockId { get; set; } = string.Empty;
        public decimal UnlockAmount { get; set; }
    }
}
