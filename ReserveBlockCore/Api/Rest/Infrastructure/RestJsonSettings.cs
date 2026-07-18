using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace ReserveBlockCore.Api.Rest.Infrastructure
{
    public static class RestJsonSettings
    {
        public static readonly JsonSerializerSettings CamelCase = new()
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Ignore,
            Formatting = Formatting.None
        };
    }
}
