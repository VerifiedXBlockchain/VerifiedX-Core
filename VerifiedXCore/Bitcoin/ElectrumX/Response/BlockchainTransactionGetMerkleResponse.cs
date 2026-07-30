using Newtonsoft.Json;
using VerifiedXCore.Bitcoin.ElectrumX.Results;

namespace VerifiedXCore.Bitcoin.ElectrumX.Response
{
    public class BlockchainTransactionGetMerkleResponse : ResponseBase<BlockchainTransactionGetMerkleResult>
    {
        [JsonProperty("result")]
        public BlockchainTransactionGetMerkleResult Result { get; set; }
        public BlockchainTransactionGetMerkleResult GetResultModel()
        {
            return Result;
        }
    }
}
