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

        /// <summary>BIP32-style path segment for shielded derivation (wallet integration).</summary>
        public const string HdPathShieldedSegment = "shielded";

        /// <summary>Coin type and account structure per implementation plan (string form for wallet docs).</summary>
        public const string HdPathTemplate = "m/44'/VFX'/0'/1/{index}/shielded";
    }
}
