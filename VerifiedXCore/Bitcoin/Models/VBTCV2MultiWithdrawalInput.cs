namespace VerifiedXCore.Bitcoin.Models
{
    /// <summary>
    /// One contract input of a vBTC V2 multi-contract withdrawal request
    /// (Data.Function == "VBTCWithdrawalRequestMultiV2()").
    ///
    /// Unlike a multi-contract TRANSFER — which is pure ledger math inside one VFX transaction —
    /// each input here names a SEPARATE Bitcoin vault: its own taproot deposit address, its own
    /// DKG ceremony and its own FROST group key. One REQUEST mints one
    /// <see cref="VBTCWithdrawalRequest"/> row per input (all sharing the REQUEST tx hash), and
    /// each row is then signed and paid out by its OWN Bitcoin transaction, exactly as the bridge
    /// BTC-exit path already does per allocation. Keeping one vault per Bitcoin transaction is
    /// load-bearing: FrostSigningAuthorization requires every input of a signed tx to spend the
    /// declared contract's deposit address, and every output to be the authorized destination or
    /// change back to that same vault.
    ///
    /// Deliberately has no RequestorAddress/Signature: every input is charged to the outer
    /// tx.FromAddress, whose single transaction signature covers the whole Inputs array via the
    /// tx hash.
    /// </summary>
    public class VBTCV2MultiWithdrawalInput
    {
        /// <summary>
        /// Smart Contract UID of the vBTC V2 contract (vault) this share is withdrawn from
        /// </summary>
        /// <example>somescguid:1234</example>
        public string SCUID { get; set; }

        /// <summary>
        /// Amount of vBTC drawn from this contract. Paid out by its own Bitcoin transaction, so
        /// each input independently carries a miner fee.
        /// </summary>
        /// <example>0.023</example>
        public decimal Amount { get; set; }
    }
}
