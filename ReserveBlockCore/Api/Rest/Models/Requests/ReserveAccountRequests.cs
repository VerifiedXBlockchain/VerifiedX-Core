using System.ComponentModel.DataAnnotations;

namespace ReserveBlockCore.Api.Rest.Models.Requests
{
    public class CreateReserveAccountRequest
    {
        [Required]
        public string Password { get; set; } = "";
        public bool StoreRecoveryAccount { get; set; }
    }

    public class PublishReserveAccountRequest
    {
        [Required]
        public string Password { get; set; } = "";
    }

    public class UnlockReserveAccountRequest
    {
        [Required]
        public string Password { get; set; } = "";
        [Range(0, int.MaxValue, ErrorMessage = "Unlock time cannot be negative.")]
        public int UnlockTime { get; set; }
    }

    public class SendReserveTransactionRequest
    {
        [Required]
        public string FromAddress { get; set; } = "";
        [Required]
        public string ToAddress { get; set; } = "";
        [Required]
        [Range(0.00000001, (double)decimal.MaxValue, ErrorMessage = "Amount must be greater than zero.")]
        public decimal Amount { get; set; }
        [Required]
        public string DecryptPassword { get; set; } = "";
        public int UnlockDelayHours { get; set; }
    }

    public class ReserveTransferNftRequest
    {
        [Required]
        public string FromAddress { get; set; } = "";
        [Required]
        public string ToAddress { get; set; } = "";
        [Required]
        public string SmartContractUID { get; set; } = "";
        [Required]
        public string DecryptPassword { get; set; } = "";
        public int UnlockDelayHours { get; set; }
        public string? BackupURL { get; set; }
    }

    public class RecoverReserveAccountRequest
    {
        [Required]
        public string RecoveryPhrase { get; set; } = "";
        [Required]
        public string Password { get; set; } = "";
    }

    public class RestoreReserveAccountRequest
    {
        [Required]
        public string RestoreCode { get; set; } = "";
        [Required]
        public string Password { get; set; } = "";
        public bool StoreRecoveryAccount { get; set; }
        public bool RescanForTx { get; set; }
        public bool OnlyRestoreRecovery { get; set; }
    }

    public class CallbackReserveAccountRequest
    {
        [Required]
        public string Password { get; set; } = "";
    }
}
