using VerifiedXCore.Bitcoin.ElectrumX.Results;

namespace VerifiedXCore.Bitcoin.ElectrumX.Response
{
    public class BlockchainTransactionGetConfirmsResponse : ResponseBase<BlockchainTransactionGetConfirmsResult>
    {
        [Newtonsoft.Json.JsonProperty("result")]
        public BlockchainTransactionGetConfirmsResult Result { get; set; }
        public int GetResultModel()
        {
            // Result is null when the server returned a JSON-RPC error (e.g. tx not found,
            // verbose unsupported) — report the established "no answer" sentinel instead of NRE'ing.
            return Result == null ? -1 : Result.Confirmations;
        }
    }
}
