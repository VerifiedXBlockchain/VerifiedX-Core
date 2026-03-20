namespace ReserveBlockCore.Privacy
{
    /// <summary>
    /// Shielded address format: <c>zfx_</c> + Base58Check(<see cref="VersionBytes"/> || 33-byte encryption key).
    /// Version bytes are distinct from the legacy <c>zbx_</c> sketch (<c>0x1C, 0xB6</c>) and identify VFX shielded v1 payloads.
    /// </summary>
    public static class ShieldedAddressConstants
    {
        public const string Prefix = "zfx_";

        /// <summary>ASCII-oriented id: <c>0x7A</c> = 'z', <c>0x66</c> = 'f' (mnemonic for <c>zfx_</c>).</summary>
        public static ReadOnlySpan<byte> VersionBytes => new byte[] { 0x7A, 0x66 };

        public const int VersionByteLength = 2;

        /// <summary>Compressed BLS12-381 G1 encoding key material in addresses (per privacy plan).</summary>
        public const int EncryptionKeyLength = 33;

        public const int PayloadLength = VersionByteLength + EncryptionKeyLength;

        /// <summary>BIP32-style path segment name (documentation only; on-wire path uses numeric <c>1'</c> chain — see <see cref="ShieldedHdDerivation"/>).</summary>
        public const string HdPathShieldedSegment = "shielded";

        /// <summary>
        /// BIP-44 coin type (hardened) for the shielded branch. Must stay consensus-stable across wallets; register on SLIP-44 when finalized.
        /// </summary>
        public const uint DefaultBip44CoinType = 889;

        /// <summary>
        /// Path pattern compatible with <see cref="global::ReserveBlockCore.BIP32.BIP32"/>: only hardened numeric segments.
        /// Replace <c>{coinType}</c> and <c>{index}</c> with decimal integers, e.g. <c>m/44'/889'/0'/1'/0'</c>.
        /// </summary>
        public const string HdPathPatternDescription = "m/44'/{coinType}'/0'/1'/{index}'";
    }
}
