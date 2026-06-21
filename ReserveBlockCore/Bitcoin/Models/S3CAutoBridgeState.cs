namespace ReserveBlockCore.Bitcoin.Models
{
    /// <summary>
    /// S3C §12: state of an in-memory auto-bridge orchestration (S3C vBTC → public companion →
    /// Base). Persists NOTHING — if the wallet restarts mid-flight the orchestration is lost but
    /// all on-chain/companion state survives and the user finishes via the manual path.
    /// </summary>
    public enum S3CAutoBridgeStatus
    {
        ResolvingCompanion,      // scanning owned contracts for an existing linked companion
        CreatingCompanion,       // public DKG (only if none discovered)
        AwaitingCompanionReady,  // DKG complete → deposit address available
        WaitingForContractFree,  // S3C contract's withdrawal slot is busy (§0) — wait
        WithdrawingFromS3C,       // submit + complete the S3C withdrawal → companion deposit address
        AwaitingBTCArrival,       // poll the companion deposit address for the confirmed delta
        AwaitingGas,              // BTC arrived; poll ETH gas on the derived Base address (<= 1h)
        Bridging,                 // CreateBridgeLockTx; hand off to the existing bridge flow
        Completed,
        Failed,
        Abandoned
    }

    public class S3CAutoBridgeState
    {
        public string OrchestrationId { get; set; } = string.Empty;
        public string S3CContractUID { get; set; } = string.Empty;
        public string RequesterAddress { get; set; } = string.Empty;   // holds S3C vBTC, signs, owns companion
        public decimal RequestedAmount { get; set; }
        public string EvmDestination { get; set; } = string.Empty;     // where vBTC.b mints (user-supplied)

        public S3CAutoBridgeStatus Status { get; set; } = S3CAutoBridgeStatus.ResolvingCompanion;

        public string? PublicScUID { get; set; }
        public string? PublicDepositAddress { get; set; }
        public string? BaseGasAddress { get; set; }
        public decimal BaseGasEthBalance { get; set; }

        public decimal CompanionBalanceBefore { get; set; }            // snapshot for delta (§12.3)
        public decimal ArrivedAmount { get; set; }                     // actually-arrived delta, bridged
        public string? LockId { get; set; }                            // once Bridging
        public string? Error { get; set; }                             // failure / abandon reason

        public long StartedTimestamp { get; set; }
        public long UpdatedTimestamp { get; set; }
    }
}
