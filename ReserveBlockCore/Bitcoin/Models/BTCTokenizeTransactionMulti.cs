namespace ReserveBlockCore.Bitcoin.Models
{
    public class BTCTokenizeTransactionMulti
    {
        /// <summary>
        /// From VFX Address
        /// </summary>
        /// <example>fromVFX</example>
        public string? FromAddress { get; set; }

        /// <summary>
        /// To VFX Address
        /// </summary>
        /// <example>toVFX</example>
        public string ToAddress { get; set; }

        /// <summary>
        /// List of vBTC inputs
        /// </summary>
        /// <example>
        /// [
        ///   {
        ///     ""scuid"": ""somescguid:1234"",
        ///     ""fromAddress"": ""fromVFX1"",
        ///     ""amount"": 1.23,
        ///     ""signature"": ""sampleSignature1""
        ///   },
        ///   {
        ///     ""scuid"": ""somescguid:5678"",
        ///     ""fromAddress"": ""fromVFX2"",
        ///     ""amount"": 2.34,
        ///     ""signature"": ""sampleSignature2""
        ///   }
        /// ]
        /// </example>
        public List<VBTCTransferInput> vBTCInputs { get; set; }

        /// <summary>
        /// Sum of vBTC inputs
        /// </summary>
        /// <example>toVFX</example>
        public decimal vBTCInputAmount { get { return vBTCInputs.Any() ? vBTCInputs.Sum(x => x.Amount) : 0M; } }


    }
}
