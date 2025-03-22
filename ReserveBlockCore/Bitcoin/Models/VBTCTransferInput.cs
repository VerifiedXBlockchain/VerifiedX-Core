namespace ReserveBlockCore.Bitcoin.Models
{
    public class VBTCTransferInput
    {
        /// <summary>
        /// Smart Contract ID
        /// </summary>
        /// <example>somescguid:1234</example>
        public string SCUID { get; set; }

        /// <summary>
        /// From VFX Address
        /// </summary>
        /// <example>fromVFX</example>
        public string? FromAddress { get; set; }

        /// <summary>
        /// Amount vBTC 
        /// </summary>
        /// <example>1.23</example>
        public decimal Amount { get; set; }
        /// <summary>
        /// vBTC signature
        /// </summary>
        /// <example>1.23</example>
        public string? Signature { get; set; }

    }
}
