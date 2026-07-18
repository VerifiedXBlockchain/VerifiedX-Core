using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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

        /// <summary>
        /// Wrap a legacy v1-style {"Success":bool,"Message":...} JSON string from a shared
        /// service into the v2 envelope. On success the parsed object becomes the data
        /// payload (original field names preserved); on failure its Message becomes the error.
        /// </summary>
        protected IActionResult FromLegacyJson(string json, string failCode, int successStatus = 200)
        {
            JObject parsed;
            try
            {
                parsed = JObject.Parse(json);
            }
            catch
            {
                return Fail(failCode, json);
            }

            var success = parsed.Value<bool?>("Success") ?? false;
            if (!success)
                return Fail(failCode, parsed.Value<string>("Message") ?? json);

            if (successStatus == 201)
                return Created((object)parsed);
            return Ok((object)parsed);
        }
    }
}
