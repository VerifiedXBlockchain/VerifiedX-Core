namespace ReserveBlockCore.Bitcoin.ElectrumX.Results
{
    public class BlockchainTransactionBroadcastResult
    {
        /// <summary>
        /// The transaction hash as a hexadecimal string.
        /// </summary>
        public string TxHash { get; set; }

        /// <summary>
        /// Error message from Electrum server if broadcast was rejected.
        /// </summary>
        public string ErrorMessage { get; set; }
    }
}
