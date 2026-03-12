using System.ComponentModel.DataAnnotations;

namespace ReserveBlockCore.Api.Rest.Models.Requests
{
    public class CreateBeaconRequest
    {
        [Required]
        public string Name { get; set; } = "";
        public bool IsPrivate { get; set; }
        public bool AutoDelete { get; set; }
        public int FileCachePeriod { get; set; }
        public int Port { get; set; }
    }

    public class AddBeaconRequest
    {
        [Required]
        public string Name { get; set; } = "";
        [Required]
        public string IpAddress { get; set; } = "";
        public int Port { get; set; }
    }
}
