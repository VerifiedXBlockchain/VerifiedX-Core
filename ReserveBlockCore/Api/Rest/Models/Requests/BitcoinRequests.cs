using System.ComponentModel.DataAnnotations;

namespace ReserveBlockCore.Api.Rest.Models.Requests
{
    public class ImportBtcKeyRequest
    {
        [Required]
        [MinLength(50, ErrorMessage = "Incorrect key format. Please try again.")]
        public string PrivateKey { get; set; } = "";
        /// <summary>Segwit (default), SegwitP2SH, or Taproot.</summary>
        public string? AddressFormat { get; set; }
    }

    public class BtcSendTransactionRequest
    {
        [Required]
        public string FromAddress { get; set; } = "";
        [Required]
        public string ToAddress { get; set; } = "";
        [Range(0.00000001, (double)decimal.MaxValue, ErrorMessage = "Amount must be greater than zero.")]
        public decimal Amount { get; set; }
        [Range(1, int.MaxValue, ErrorMessage = "Fee rate must be greater than zero.")]
        public int FeeRate { get; set; }
        public bool OverrideInternalSend { get; set; }
    }

    public class BtcCreateAdnrRequest
    {
        [Required]
        public string Address { get; set; } = "";
        [Required]
        public string BtcAddress { get; set; } = "";
        [Required]
        public string Name { get; set; } = "";
    }

    public class BtcTransferAdnrRequest
    {
        [Required]
        public string ToAddress { get; set; } = "";
        [Required]
        public string BtcFromAddress { get; set; } = "";
        [Required]
        public string BtcToAddress { get; set; } = "";
    }

    public class BtcDeleteAdnrRequest
    {
        [Required]
        public string BtcFromAddress { get; set; } = "";
    }

    public class BtcBroadcastRequest
    {
        /// <summary>Raw signed transaction hex.</summary>
        [Required]
        public string Hex { get; set; } = "";
    }

    public class BtcReplaceByFeeRequest
    {
        [Range(1, int.MaxValue, ErrorMessage = "Fee rate must be greater than zero.")]
        public int FeeRate { get; set; }
    }

    public class BtcTransferOwnershipRequest
    {
        [Required]
        public string ToAddress { get; set; } = "";
        public string? BackupURL { get; set; }
    }
}
