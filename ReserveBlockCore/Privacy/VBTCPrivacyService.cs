using LiteDB;
using ReserveBlockCore.Models.Privacy;

namespace ReserveBlockCore.Privacy
{
    /// <summary>
    /// vBTC V2 shielded pool helpers (Phase 5): per-contract asset key <c>VBTC:{SmartContractUID}</c> in <c>DB_Privacy</c>.
    /// </summary>
    public static class VBTCPrivacyService
    {
        public static string AssetKeyForContract(string scUid) => VbtcPrivacyAsset.FormatAssetKey(scUid);

        public static ShieldedPoolState? GetPoolState(string scUid, LiteDatabase? db = null) =>
            ShieldedPoolService.GetState(AssetKeyForContract(scUid), db);

        public static ShieldedPoolState GetOrCreatePoolState(string scUid, LiteDatabase? db = null) =>
            ShieldedPoolService.GetOrCreateState(AssetKeyForContract(scUid), db);

        public static string? GetCurrentMerkleRootB64(string scUid, LiteDatabase? db = null) =>
            ShieldedPoolService.GetCurrentMerkleRootB64(AssetKeyForContract(scUid), db);
    }
}
