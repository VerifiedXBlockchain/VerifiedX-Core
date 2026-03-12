using System.ComponentModel.DataAnnotations;

namespace ReserveBlockCore.Api.Rest.Models.Requests
{
    public class ConnectToShopRequest
    {
        [Required]
        public string Address { get; set; } = "";
        [Required]
        public string Url { get; set; } = "";
    }

    public class CompleteNftPurchaseRequest
    {
        [Required]
        public string ScUID { get; set; } = "";
        [Required]
        public string KeySign { get; set; } = "";
    }
}
