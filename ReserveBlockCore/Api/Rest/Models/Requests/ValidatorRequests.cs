using System.ComponentModel.DataAnnotations;

namespace ReserveBlockCore.Api.Rest.Models.Requests
{
    public class StartValidatorRequest
    {
        [Required]
        public string Address { get; set; } = "";
    }

    public class StopValidatorRequest
    {
        [Required]
        public string Address { get; set; } = "";
    }

    public class RegisterValidatorRequest
    {
        [Required]
        public string Address { get; set; } = "";
        [Required]
        public string UniqueName { get; set; } = "";
    }

    public class ChangeValidatorNameRequest
    {
        [Required]
        public string UniqueName { get; set; } = "";
    }
}
