namespace ReserveBlockCore.Privacy
{
    /// <summary>
    /// Deterministic shielded key hierarchy from HD seed (secp256k1). <see cref="EncryptionPublicKey33"/> is what <c>zfx_</c> encodes.
    /// </summary>
    public sealed class ShieldedKeyMaterial
    {
        public byte[] SpendingKey32 { get; init; } = Array.Empty<byte>();
        public byte[] ViewingKey32 { get; init; } = Array.Empty<byte>();
        /// <summary>secp256k1 encryption secret; never publish.</summary>
        public byte[] EncryptionPrivateKey32 { get; init; } = Array.Empty<byte>();
        /// <summary>Compressed secp256k1 pubkey (33 bytes) for ECDH + <c>zfx_</c> wire encoding.</summary>
        public byte[] EncryptionPublicKey33 { get; init; } = Array.Empty<byte>();
        public string ZfxAddress { get; init; } = "";
    }
}
