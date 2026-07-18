using System.ComponentModel.DataAnnotations;

namespace ReserveBlockCore.Api.Rest.Models.Requests
{
    public class CreateAdnrRequest
    {
        [Required(ErrorMessage = "Address is required")]
        public string Address { get; set; } = string.Empty;

        [Required(ErrorMessage = "Name is required")]
        public string Name { get; set; } = string.Empty;
    }

    public class TransferAdnrRequest
    {
        [Required(ErrorMessage = "From address is required")]
        public string FromAddress { get; set; } = string.Empty;

        [Required(ErrorMessage = "To address is required")]
        public string ToAddress { get; set; } = string.Empty;
    }
}
