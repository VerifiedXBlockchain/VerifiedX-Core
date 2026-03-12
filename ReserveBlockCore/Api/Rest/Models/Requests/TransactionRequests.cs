using System.ComponentModel.DataAnnotations;

namespace ReserveBlockCore.Api.Rest.Models.Requests
{
    public class SendTransactionRequest
    {
        [Required(ErrorMessage = "Sender address is required")]
        public string FromAddress { get; set; } = string.Empty;

        [Required(ErrorMessage = "Recipient address is required")]
        public string ToAddress { get; set; } = string.Empty;

        [Required]
        [Range(0.00000001, (double)decimal.MaxValue, ErrorMessage = "Amount must be greater than 0")]
        public decimal Amount { get; set; }
    }

    public class RawTransactionRequest
    {
        [Required(ErrorMessage = "Transaction JSON is required")]
        public object Transaction { get; set; } = null!;
    }

    public class ReplaceByFeeRequest
    {
        [Required]
        [Range(0.00000001, (double)decimal.MaxValue, ErrorMessage = "Fee must be greater than 0")]
        public decimal NewFee { get; set; }
    }
}
