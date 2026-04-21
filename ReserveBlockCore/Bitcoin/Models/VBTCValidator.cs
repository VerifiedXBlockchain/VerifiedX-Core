using System;

namespace ReserveBlockCore.Bitcoin.Models
{
    /// <summary>
    /// Model representing a vBTC V2 validator.
    /// Active validators are derived from block scanning (see VBTCValidatorRegistry).
    /// No persistent DB storage — the blockchain is the source of truth.
    /// </summary>
    public class VBTCValidator
    {
        public long Id { get; set; }
        public string ValidatorAddress { get; set; }      // VFX address
        public string IPAddress { get; set; }
        public string FrostPublicKey { get; set; }        // Validator's FROST public key (public, shareable)
        public long RegistrationBlockHeight { get; set; }
        public long LastHeartbeatBlock { get; set; }
        public bool IsActive { get; set; }
        public string RegistrationSignature { get; set; } // Proof of address ownership
        public string RegisterTransactionHash { get; set; }  // TX hash of join transaction
        public string ExitTransactionHash { get; set; }      // TX hash of exit transaction (if exited)
        public long? ExitBlockHeight { get; set; }           // Block height when exited (if exited)

        public string BaseAddress { get; set; } = string.Empty;
        public string BaseAddressSignature { get; set; } = string.Empty;
        public long BaseSignatureTimestamp { get; set; }
        public long BaseSignatureBlockHeight { get; set; }
        public bool IsRegisteredOnBase { get; set; }
    }
}