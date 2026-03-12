using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using ReserveBlockCore.Api.Rest.Models;

namespace ReserveBlockCore.Api.Rest.Infrastructure
{
    [RestApiAuthFilter]
    [ServiceFilter(typeof(RestExceptionFilter))]
    [ApiController]
    [ApiExplorerSettings(GroupName = "rest")]
    [Route("api/rest/[controller]")]
    [Produces("application/json")]
    public abstract class RestBaseController : ControllerBase
    {
        protected IActionResult Ok<T>(T data)
        {
            var envelope = ApiResponse<T>.Succeed(data);
            return Content(
                JsonConvert.SerializeObject(envelope, RestJsonSettings.CamelCase),
                "application/json");
        }

        protected IActionResult OkPaged<T>(IEnumerable<T> items, int page, int pageSize, int totalCount)
        {
            var envelope = ApiResponse<IEnumerable<T>>.Paged(items, page, pageSize, totalCount);
            return Content(
                JsonConvert.SerializeObject(envelope, RestJsonSettings.CamelCase),
                "application/json");
        }

        protected IActionResult Created<T>(T data)
        {
            var envelope = ApiResponse<T>.Succeed(data);
            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(envelope, RestJsonSettings.CamelCase),
                ContentType = "application/json",
                StatusCode = 201
            };
        }

        protected IActionResult Fail(string code, string message, int status = 400)
        {
            var envelope = ApiResponse<object>.Fail(code, message);
            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(envelope, RestJsonSettings.CamelCase),
                ContentType = "application/json",
                StatusCode = status
            };
        }
    }
}
