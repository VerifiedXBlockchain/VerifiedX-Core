namespace VerifiedXCore.Bitcoin.ElectrumX.Response
{
    /// <summary>What every Electrum JSON-RPC answer carries, whatever its result type.</summary>
    public interface IElectrumResponse
    {
        Error Error { get; }
    }

    public class ResponseBase<TResultModel> : IElectrumResponse where TResultModel:new()
    {

        [Newtonsoft.Json.JsonProperty("jsonrpc")]
        protected string JsonRpcVersion { get; set; }

        [Newtonsoft.Json.JsonProperty("id")]
        protected int MessageId { get; set; }

        [Newtonsoft.Json.JsonProperty("error")]
        public Error Error { get; set; }
        

        private TResultModel GetResultModel()
        {
            return new TResultModel();
        }
    }
}
