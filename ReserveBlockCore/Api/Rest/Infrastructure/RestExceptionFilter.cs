using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using ReserveBlockCore.Api.Rest.Models;

namespace ReserveBlockCore.Api.Rest.Infrastructure
{
    public class RestExceptionFilter : IExceptionFilter
    {
        public void OnException(ExceptionContext context)
        {
            var error = context.Exception switch
            {
                ArgumentException ex => (400, "BAD_REQUEST", ex.Message),
                KeyNotFoundException ex => (404, "NOT_FOUND", ex.Message),
                UnauthorizedAccessException ex => (401, "UNAUTHORIZED", ex.Message),
                InvalidOperationException ex => (409, "CONFLICT", ex.Message),
                _ => (500, "INTERNAL_ERROR", "An unexpected error occurred.")
            };

            context.Result = new ObjectResult(
                ApiResponse<object>.Fail(error.Item2, error.Item3))
            { StatusCode = error.Item1 };

            context.ExceptionHandled = true;
        }
    }
}
