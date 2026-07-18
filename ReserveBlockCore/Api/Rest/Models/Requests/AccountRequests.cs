using System.ComponentModel.DataAnnotations;

namespace ReserveBlockCore.Api.Rest.Models.Requests
{
    public class ImportKeyRequest
    {
        [Required(ErrorMessage = "Private key is required")]
        public string PrivateKey { get; set; } = string.Empty;

        public bool Scan { get; set; } = false;
    }
}
