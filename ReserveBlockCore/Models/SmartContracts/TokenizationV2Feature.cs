using System.Collections.Generic;

namespace ReserveBlockCore.Models.SmartContracts
{
    public class TokenizationV2Feature
    {
        #region Basic Info
        public string AssetName { get; set; }
        public string AssetTicker { get; set; }
        public string DepositAddress { get; set; }  // FROST-generated BTC Taproot address (bc1p...)
        public int Version { get; set; } = 2;
        #endregion

        #region FROST Data
        public List<string> ValidatorAddressesSnapshot { get; set; }
        public string FrostGroupPublicKey { get; set; }  // Aggregated FROST group public key
        public int RequiredThreshold { get; set; }
        #endregion

        #region DKG Proof
        public string DKGProof { get; set; }  // Distributed Key Generation completion proof
        public long ProofBlockHeight { get; set; }
        #endregion

        #region Visual
        public string? ImageBase { get; set; }
        #endregion
    }
}
