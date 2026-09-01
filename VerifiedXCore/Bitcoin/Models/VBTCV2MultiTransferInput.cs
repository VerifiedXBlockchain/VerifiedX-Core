namespace VerifiedXCore.Bitcoin.Models
{
    /// <summary>
    /// One contract input of a vBTC V2 multi-contract transfer (Data.Function == "TransferVBTCMultiV2()").
    /// Deliberately has no FromAddress/Signature: every input debits the outer tx.FromAddress, whose
    /// single transaction signature covers the whole Inputs array via the tx hash. (Contrast with the
    /// V1 VBTCTransferInput, whose per-input FromAddress enabled the spoofing shape the legacy
    /// TransferVBTCMulti handler trusted.)
    /// </summary>
    public class VBTCV2MultiTransferInput
    {
        /// <summary>
        /// Smart Contract UID of the vBTC V2 contract being debited
        /// </summary>
        /// <example>somescguid:1234</example>
        public string SCUID { get; set; }

        /// <summary>
        /// Amount of vBTC drawn from this contract
        /// </summary>
        /// <example>0.023</example>
        public decimal Amount { get; set; }
    }
}
