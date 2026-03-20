using ReserveBlockCore.Models;

namespace ReserveBlockCore.Privacy
{
    public static class PlonkCircuitHelper
    {
        public static PlonkCircuitType GetPrimaryCircuit(TransactionType t) =>
            t switch
            {
                TransactionType.VFX_SHIELD or TransactionType.VBTC_V2_SHIELD => PlonkCircuitType.Shield,
                TransactionType.VFX_UNSHIELD or TransactionType.VBTC_V2_UNSHIELD => PlonkCircuitType.Unshield,
                TransactionType.VFX_PRIVATE_TRANSFER or TransactionType.VBTC_V2_PRIVATE_TRANSFER => PlonkCircuitType.Transfer,
                _ => PlonkCircuitType.Transfer
            };

        public static bool UsesFeeProof(TransactionType t) =>
            t is TransactionType.VBTC_V2_UNSHIELD or TransactionType.VBTC_V2_PRIVATE_TRANSFER;
    }
}
