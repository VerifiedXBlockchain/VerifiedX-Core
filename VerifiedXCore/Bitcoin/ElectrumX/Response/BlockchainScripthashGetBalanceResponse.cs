using VerifiedXCore.Bitcoin.ElectrumX.Results;

namespace VerifiedXCore.Bitcoin.ElectrumX.Response
{
    public class BlockchainScripthashGetBalanceResponse : ResponseBase<BlockchainScripthashGetBalanceResult>
    {
        [Newtonsoft.Json.JsonProperty("result")]
        public BlockchainScripthashGetBalanceResult Result { get; set; }
        public BlockchainScripthashGetBalanceResult GetResultModel()
        {
            return Result;
        }
    }
}
