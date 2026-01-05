using System.Collections.Generic;

namespace ReserveBlockCore.Models.SmartContracts
{
    public class TokenizationV2Feature
    {
        #region Basic Info
        public string AssetName { get; set; }
        public string AssetTicker { get; set; }
        public string DepositAddress { get; set; }  // MPC-generated BTC address
        public int Version { get; set; } = 2;
        #endregion

        #region MPC Data
        public List<string> ValidatorAddressesSnapshot { get; set; }
        public string MPCPublicKeyData { get; set; }
        public int RequiredThreshold { get; set; }
        #endregion

        #region ZK Proof
        public string AddressCreationProof { get; set; }
        public long ProofBlockHeight { get; set; }
        #endregion

        #region Visual
        public string? ImageBase { get; set; }
        #endregion
    }
}
