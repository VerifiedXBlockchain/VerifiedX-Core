using System.ComponentModel.DataAnnotations;

namespace ReserveBlockCore.Api.Rest.Models
{
    public class PaginationParams
    {
        [Range(1, int.MaxValue, ErrorMessage = "Page must be at least 1")]
        public int Page { get; set; } = 1;

        [Range(1, 100, ErrorMessage = "PageSize must be between 1 and 100")]
        public int PageSize { get; set; } = 25;
    }
}
