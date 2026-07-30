namespace VerifiedXCore.Bitcoin.ElectrumX.Request
{
    internal class BlockchainTransactionBroadcastRequest : RequestBase<string>
    {
        internal BlockchainTransactionBroadcastRequest(string rawTx)
        {
            Method = "blockchain.transaction.broadcast";
            Parameters = new [] { rawTx };
        }
    }
}
