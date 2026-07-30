using System.Collections.Generic;
using VerifiedXCore.Bitcoin.ElectrumX.Results;

namespace VerifiedXCore.Bitcoin.ElectrumX.Response
{
    public class BlockchainScripthashListunspentResponse : ResponseBase<BlockchainScripthashListunspentResult>
    {
        [Newtonsoft.Json.JsonProperty("result")]
        public List<BlockchainScripthashListunspentResult> Result { get; set; }
        public List<BlockchainScripthashListunspentResult> GetResultModel()
        {
            return Result;
        }
    }
}
