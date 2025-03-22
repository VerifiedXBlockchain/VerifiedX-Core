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
        /// <example>toVFX</example>
        public List<VBTCTransferInput> vBTCInputs { get; set; }

        /// <summary>
        /// Sum of vBTC inputs
        /// </summary>
        /// <example>toVFX</example>
        public decimal vBTCInputAmount { get { return vBTCInputs.Any() ? vBTCInputs.Sum(x => x.Amount) : 0M; } }


    }
}
