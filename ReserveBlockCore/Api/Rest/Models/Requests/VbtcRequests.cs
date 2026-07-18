using System.ComponentModel.DataAnnotations;

namespace ReserveBlockCore.Api.Rest.Models.Requests
{
    public class InitiateCeremonyRequest
    {
        [Required]
        public string OwnerAddress { get; set; } = "";
        /// <summary>S3C §5: set true when minting a public companion for an S3C contract.</summary>
        public bool ForcePublic { get; set; }
    }

    public class CancelCeremonyRequest
    {
        [Required]
        public string OwnerAddress { get; set; } = "";
    }

    public class PrepareCeremonyRawRequest
    {
        [Required]
        public string OwnerAddress { get; set; } = "";
    }

    public class ExecuteCeremonyRawRequest
    {
        [Required]
        public string OwnerAddress { get; set; } = "";
        [Required]
        public string CeremonyId { get; set; } = "";
        [Required]
        public string SessionId { get; set; } = "";
        public long StartTimestamp { get; set; }
        [Required]
        public string StartSignature { get; set; } = "";
        public long ShareDistributionTimestamp { get; set; }
        [Required]
        public string ShareDistributionSignature { get; set; } = "";
    }

    public class CreateVbtcContractRequest
    {
        [Required]
        public string OwnerAddress { get; set; } = "";
        [Required]
        public string Name { get; set; } = "";
        public string? Description { get; set; }
        public string? Ticker { get; set; }
        public string? ImageBase { get; set; }
        /// <summary>Ceremony ID from a completed MPC ceremony (required — run the ceremony first).</summary>
        [Required]
        public string CeremonyId { get; set; } = "";
        /// <summary>S3C §5: when minting a public companion, the linked S3C contract's scUID.</summary>
        public string? LinkedContractUID { get; set; }
    }

    public class CreateVbtcContractRawRequest
    {
        [Required]
        public string OwnerAddress { get; set; } = "";
        [Required]
        public string Name { get; set; } = "";
        public string? Description { get; set; }
        public string? Ticker { get; set; }
        public string? ImageBase { get; set; }
        [Required]
        public string CeremonyId { get; set; } = "";
        public long Timestamp { get; set; }
        public string? UniqueId { get; set; }
        public string? OwnerSignature { get; set; }
        public string? LinkedContractUID { get; set; }
    }

    public class VbtcTransferRequest
    {
        [Required]
        public string SmartContractUID { get; set; } = "";
        [Required]
        public string FromAddress { get; set; } = "";
        [Required]
        public string ToAddress { get; set; } = "";
        [Range(0.00000001, (double)decimal.MaxValue, ErrorMessage = "Amount must be greater than zero.")]
        public decimal Amount { get; set; }
    }

    public class VbtcTransferOwnershipRequest
    {
        [Required]
        public string ToAddress { get; set; } = "";
    }

    public class VbtcWithdrawalRequestModel
    {
        [Required]
        public string SmartContractUID { get; set; } = "";
        /// <summary>The VFX address requesting withdrawal. Any address with a vBTC balance — not just the contract owner.</summary>
        [Required]
        public string RequestorAddress { get; set; } = "";
        [Required]
        public string BTCAddress { get; set; } = "";
        [Range(0.00000001, (double)decimal.MaxValue, ErrorMessage = "Amount must be greater than zero.")]
        public decimal Amount { get; set; }
        [Range(1, int.MaxValue, ErrorMessage = "Fee rate must be greater than zero.")]
        public int FeeRate { get; set; }
    }

    public class VbtcCompleteWithdrawalRequest
    {
        [Required]
        public string SmartContractUID { get; set; } = "";
        [Required]
        public string WithdrawalRequestHash { get; set; } = "";
    }

    public class VbtcCancelWithdrawalRequest
    {
        [Required]
        public string SmartContractUID { get; set; } = "";
        [Required]
        public string OwnerAddress { get; set; } = "";
        [Required]
        public string WithdrawalRequestHash { get; set; } = "";
        public string? BTCTxHash { get; set; }
        public string? FailureProof { get; set; }
    }

    public class VbtcWithdrawalRawRequest
    {
        [Required]
        public string VFXAddress { get; set; } = "";
        [Required]
        public string BTCAddress { get; set; } = "";
        [Required]
        public string SmartContractUID { get; set; } = "";
        public decimal Amount { get; set; }
        public int FeeRate { get; set; }
        public long Timestamp { get; set; }
        [Required]
        public string UniqueId { get; set; } = "";
        [Required]
        public string VFXSignature { get; set; } = "";
        public bool IsTest { get; set; }
    }

    public class VbtcCancelWithdrawalRawRequest
    {
        [Required]
        public string SmartContractUID { get; set; } = "";
        [Required]
        public string OwnerAddress { get; set; } = "";
        [Required]
        public string WithdrawalRequestHash { get; set; } = "";
        public string? BTCTxHash { get; set; }
        public string? FailureProof { get; set; }
        public long Timestamp { get; set; }
        [Required]
        public string UniqueId { get; set; } = "";
        [Required]
        public string OwnerSignature { get; set; } = "";
    }

    public class PrepareCompleteWithdrawalRequest
    {
        [Required]
        public string OwnerAddress { get; set; } = "";
        [Required]
        public string SmartContractUID { get; set; } = "";
        [Required]
        public string WithdrawalRequestHash { get; set; } = "";
    }

    public class ExecuteCompleteWithdrawalRequest
    {
        [Required]
        public string OwnerAddress { get; set; } = "";
        [Required]
        public string SmartContractUID { get; set; } = "";
        [Required]
        public string WithdrawalRequestHash { get; set; } = "";
        [Required]
        public string SessionId { get; set; } = "";
        public long StartTimestamp { get; set; }
        [Required]
        public string StartSignature { get; set; } = "";
        public long ShareDistributionTimestamp { get; set; }
        [Required]
        public string ShareDistributionSignature { get; set; } = "";
        /// <summary>Delegated withdrawal amount (BTC). Passed through if local DB doesn't have the withdrawal request.</summary>
        public decimal Amount { get; set; }
        /// <summary>Delegated BTC destination address.</summary>
        public string? BTCDestination { get; set; }
        /// <summary>Delegated fee rate (sats/vB).</summary>
        public int FeeRate { get; set; }
    }

    /// <summary>Submission body for every raw-tx build/send pair (client signed the Hash offline).</summary>
    public class RawVbtcTxSubmissionRequest
    {
        /// <summary>TX hash returned by the matching build endpoint (the value the client signed).</summary>
        [Required]
        public string Hash { get; set; } = "";
        /// <summary>ECDSA base64 signature over UTF8(Hash).</summary>
        [Required]
        public string Signature { get; set; } = "";
        /// <summary>Hex-encoded secp256k1 public key of the signing account.</summary>
        [Required]
        public string PublicKey { get; set; } = "";
    }

    public class RawCompleteWithdrawalTxRequest
    {
        [Required]
        public string SmartContractUID { get; set; } = "";
        /// <summary>The withdrawal requestor's VFX address (FromAddress for the completion TX).</summary>
        [Required]
        public string FromAddress { get; set; } = "";
        [Required]
        public string WithdrawalRequestHash { get; set; } = "";
        /// <summary>The BTC transaction hash from the FROST signing result (after wallet broadcasts to Bitcoin).</summary>
        [Required]
        public string BTCTransactionHash { get; set; } = "";
        /// <summary>Withdrawal amount in BTC.</summary>
        public decimal Amount { get; set; }
        /// <summary>BTC destination address.</summary>
        [Required]
        public string BTCDestination { get; set; } = "";
    }

    public class VbtcCancelWithdrawalTxRequest
    {
        [Required]
        public string SmartContractUID { get; set; } = "";
        [Required]
        public string RequestorAddress { get; set; } = "";
        [Required]
        public string WithdrawalRequestHash { get; set; } = "";
    }

    public class BridgeToBaseRequest
    {
        [Required]
        public string SmartContractUID { get; set; } = "";
        [Required]
        public string OwnerAddress { get; set; } = "";
        [Range(0.00000001, (double)decimal.MaxValue, ErrorMessage = "Amount must be greater than zero.")]
        public decimal Amount { get; set; }
        /// <summary>EVM destination address on Base (0x + 40 hex chars) where vBTC.b mints.</summary>
        [Required]
        public string EvmDestination { get; set; } = "";
    }
}
