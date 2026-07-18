using System.ComponentModel.DataAnnotations;

namespace ReserveBlockCore.Api.Rest.Models.Requests
{
    public class TransferNftRequest
    {
        [Required]
        public string ToAddress { get; set; } = "";
        public string? BackupURL { get; set; }
    }

    public class EvolveRequest
    {
        [Required]
        public string ToAddress { get; set; } = "";
    }

    public class DevolveRequest
    {
        [Required]
        public string ToAddress { get; set; } = "";
    }

    public class TransferSaleRequest
    {
        [Required]
        public string ToAddress { get; set; } = "";
        [Required]
        [Range(0.00000001, double.MaxValue, ErrorMessage = "Sale amount must be greater than zero.")]
        public decimal SaleAmount { get; set; }
        public string? BackupURL { get; set; }
    }

    public class CompleteSaleRequest
    {
        [Required]
        public string KeySign { get; set; } = "";
    }

    public class VerifyOwnershipRequest
    {
        [Required]
        public string OwnershipScript { get; set; } = "";
    }
}
