namespace ReserveBlockCore.Api.Rest.Models
{
    public class ApiResponse<T>
    {
        public bool Success { get; set; }
        public T? Data { get; set; }
        public ApiError? Error { get; set; }
        public PaginationMeta? Meta { get; set; }

        public static ApiResponse<T> Succeed(T data)
        {
            return new ApiResponse<T> { Success = true, Data = data };
        }

        public static ApiResponse<T> Paged(T data, int page, int pageSize, int totalCount)
        {
            return new ApiResponse<T>
            {
                Success = true,
                Data = data,
                Meta = new PaginationMeta
                {
                    Page = page,
                    PageSize = pageSize,
                    TotalCount = totalCount,
                    TotalPages = (int)Math.Ceiling((double)totalCount / pageSize)
                }
            };
        }

        public static ApiResponse<T> Fail(string code, string message)
        {
            return new ApiResponse<T>
            {
                Success = false,
                Error = new ApiError { Code = code, Message = message }
            };
        }
    }

    public class ApiError
    {
        public string Code { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
    }

    public class PaginationMeta
    {
        public int Page { get; set; }
        public int PageSize { get; set; }
        public int TotalCount { get; set; }
        public int TotalPages { get; set; }
    }
}
