using System.ComponentModel.DataAnnotations;

namespace ReserveBlockCore.Api.Rest.Models.Requests
{
    public class CreateSignatureRequest
    {
        [Required(ErrorMessage = "Address is required")]
        public string Address { get; set; } = string.Empty;

        [Required(ErrorMessage = "Message is required")]
        public string Message { get; set; } = string.Empty;
    }

    public class VerifySignatureRequest
    {
        [Required(ErrorMessage = "Address is required")]
        public string Address { get; set; } = string.Empty;

        [Required(ErrorMessage = "Message is required")]
        public string Message { get; set; } = string.Empty;

        [Required(ErrorMessage = "Signature is required")]
        public string Signature { get; set; } = string.Empty;
    }
}
