using System.ComponentModel.DataAnnotations;

namespace ReserveBlockCore.Api.Rest.Models.Requests
{
    public class WalletPasswordRequest
    {
        [Required(ErrorMessage = "Password is required")]
        public string Password { get; set; } = string.Empty;
    }

    public class CreateHdWalletRequest
    {
        [Required]
        [Range(12, 24, ErrorMessage = "Strength must be 12 or 24")]
        public int Strength { get; set; } = 12;
    }
}
