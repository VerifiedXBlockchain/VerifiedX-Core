namespace VerifiedXCore.Privacy
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

        /// <summary>
        /// Stage 3: the nullifier key the v2 circuits spend with - the viewing key reduced to a canonical field element.
        /// (The viewing key is spend authority in this scheme, as before; a view-only share of it is a spend share.)
        /// </summary>
        public byte[] NullifierKey32 => ViewingKey32 == null || ViewingKey32.Length != 32 ? Array.Empty<byte>() : PrivacyField.ReduceLe(ViewingKey32);

        /// <summary>Stage 3: <c>owner_pk = Poseidon(OwnerPkDomain, nullifier_key)</c>, the owner key in every note sent to this wallet and in its v2 address.</summary>
        public byte[] OwnerPk32 => OwnerPkFromViewingKey(ViewingKey32);

        /// <summary>The owner key for a 32-byte viewing key.</summary>
        public static byte[] OwnerPkFromViewingKey(byte[] viewingKey32)
        {
            if (viewingKey32 == null || viewingKey32.Length != 32) throw new ArgumentException("Viewing key must be 32 bytes.", nameof(viewingKey32));
            return PoseidonV1.OwnerPk(PrivacyField.ReduceLe(viewingKey32));
        }
    }
}
