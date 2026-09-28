using VerifiedXCore.Bitcoin.ElectrumX.Results;

namespace VerifiedXCore.Bitcoin.ElectrumX.Response
{
    /// <summary>
    /// The direct answer to blockchain.headers.subscribe: the server's current tip. (Later pushes on a held
    /// connection use the notification shape, <see cref="BlockchainHeadersSubscribeResponse"/>.)
    /// </summary>
    public class BlockchainHeadersTipResponse : ResponseBase<BlockchainHeadersSubscribeResult>
    {
        [Newtonsoft.Json.JsonProperty("result")]
        public BlockchainHeadersSubscribeResult Result { get; set; }
    }
}
