namespace ReserveBlockCore.Privacy
{
    /// <summary>
    /// Privacy sentinel strings and limits. Numeric limits mirror <see cref="Globals"/> where noted.
    /// </summary>
    public static class PrivacyConstants
    {
        public const string ShieldedPoolAddress = "Shielded_Pool";
        public const string PlonkSignatureSentinel = "PLONK";

        /// <summary>Must match <see cref="Globals.MaxPrivateTxDataSize"/> (validator uses <see cref="Globals"/> at runtime).</summary>
        public const int MaxPrivateTransactionDataCharacters = 8192;

        /// <summary>Must match <see cref="Globals.MaxMerkleRootAge"/>.</summary>
        public const int MaxMerkleRootAgeBlocks = 100;

        /// <summary>Max decoded bytes for optional large Base64 fields (proofs).</summary>
        public const int MaxProofFieldDecodedBytes = 512 * 1024;

        /// <summary>Max UTF-8 length for optional string fields (addresses, roots as text).</summary>
        public const int MaxPayloadStringFieldLength = 512;
    }
}
