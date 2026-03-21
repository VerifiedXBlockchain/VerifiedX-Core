namespace ReserveBlockCore.Privacy
{
    /// <summary>
    /// Token-scoped shielded pool key for vBTC V2: one Merkle tree + pool row per smart-contract UID (<c>DB_Privacy</c> <see cref="Models.Privacy.ShieldedPoolState.AssetType"/>).
    /// </summary>
    public static class VbtcPrivacyAsset
    {
        public const string Prefix = "VBTC:";

        public static string FormatAssetKey(string scUid)
        {
            if (string.IsNullOrWhiteSpace(scUid))
                throw new ArgumentException("vBTC contract UID is required.", nameof(scUid));
            return Prefix + scUid.Trim();
        }

        public static bool TryParseContractUid(string? assetType, out string scUid)
        {
            scUid = "";
            if (string.IsNullOrEmpty(assetType) || !assetType.StartsWith(Prefix, StringComparison.Ordinal))
                return false;
            scUid = assetType.Substring(Prefix.Length).Trim();
            return scUid.Length > 0;
        }

        public static bool IsVbtcShieldedAsset(string? assetType) =>
            !string.IsNullOrEmpty(assetType) && assetType.StartsWith(Prefix, StringComparison.Ordinal);

        public static bool MatchesContract(string? assetType, string? vbtcContractUid)
        {
            if (string.IsNullOrWhiteSpace(vbtcContractUid))
                return false;
            return string.Equals(FormatAssetKey(vbtcContractUid), assetType?.Trim(), StringComparison.Ordinal);
        }
    }
}
