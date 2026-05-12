using LiteDB;
using ReserveBlockCore.Extensions;
using ReserveBlockCore.Models.Privacy;

namespace ReserveBlockCore.Privacy
{
    /// <summary>
    /// Nullifier derivation and DB persistence.
    /// <para>
    /// <b>v2 (note-hash based):</b> <see cref="DeriveFromNoteHash"/> uses the Poseidon note hash (32 bytes)
    /// as the commitment identifier, matching the in-circuit nullifier gadget:
    /// <c>N = Poseidon(viewingKey, noteHash, treePosition)</c>.
    /// The legacy <see cref="DeriveNullifier"/> accepting G1 commitment bytes is retained for backward compatibility.
    /// </para>
    /// </summary>
    public static class NullifierService
    {
        private static ILiteCollection<NullifierRecord> Nullifiers(LiteDatabase? privacyDb = null) =>
            (privacyDb ?? PrivacyDbContext.GetPrivacyDb()).GetCollection<NullifierRecord>(PrivacyDbContext.PRIV_NULLIFIERS);

        /// <summary>
        /// Derives a 32-byte nullifier from the viewing key, the 32-byte Poseidon note hash, and the leaf index.
        /// <para>This is the preferred derivation matching the PLONK circuit nullifier gadget.</para>
        /// </summary>
        public static byte[] DeriveFromNoteHash(ReadOnlySpan<byte> viewingKey32, ReadOnlySpan<byte> noteHash32, ulong treePosition)
        {
            if (viewingKey32.Length != PlonkNative.ScalarSize)
                throw new ArgumentException("Viewing key must be 32 bytes.");
            if (noteHash32.Length != PlonkNative.ScalarSize)
                throw new ArgumentException("Note hash must be 32 bytes.");

            // Pack 3 field elements: viewingKey || noteHash || position (LE padded to 32 bytes)
            var posBytes = new byte[PlonkNative.ScalarSize];
            var posLe = BitConverter.GetBytes(treePosition);
            Array.Copy(posLe, posBytes, posLe.Length);

            var input = new byte[PlonkNative.ScalarSize * 3];
            viewingKey32.CopyTo(input.AsSpan(0, PlonkNative.ScalarSize));
            noteHash32.CopyTo(input.AsSpan(PlonkNative.ScalarSize, PlonkNative.ScalarSize));
            Array.Copy(posBytes, 0, input, PlonkNative.ScalarSize * 2, PlonkNative.ScalarSize);

            var nullifierOut = new byte[PlonkNative.ScalarSize];
            int code = PlonkNative.poseidon_hash(input, (nuint)input.Length, nullifierOut);
            if (code != PlonkNative.Success)
                throw new InvalidOperationException($"poseidon_hash (nullifier) returned {code}");
            return nullifierOut;
        }

        /// <summary>
        /// Legacy: derives nullifier from viewing key + G1-compressed commitment + position via native FFI.
        /// <para>Retained for backward compatibility. New code should use <see cref="DeriveFromNoteHash"/>.</para>
        /// </summary>
        public static byte[] DeriveNullifier(ReadOnlySpan<byte> viewingKey32, ReadOnlySpan<byte> commitmentG1, ulong treePosition)
        {
            if (viewingKey32.Length != PlonkNative.ScalarSize)
                throw new ArgumentException("Viewing key must be 32 bytes.");
            if (commitmentG1.Length != PlonkNative.G1CompressedSize)
                throw new ArgumentException($"Commitment must be {PlonkNative.G1CompressedSize} bytes.");

            var vk = viewingKey32.ToArray();
            var c = commitmentG1.ToArray();
            var n = new byte[PlonkNative.ScalarSize];
            int code = PlonkNative.nullifier_derive(vk, c, treePosition, n);
            if (code != PlonkNative.Success)
                throw new InvalidOperationException($"nullifier_derive failed: {code}");
            return n;
        }

        public static string ToNullifierKeyBase64(byte[] nullifier32) =>
            Convert.ToBase64String(nullifier32);

        public static bool IsNullifierSpentInDb(string nullifierBase64, string assetType, LiteDatabase? privacyDb = null)
        {
            var col = Nullifiers(privacyDb);
            return col.FindOne(x => x.Nullifier == nullifierBase64 && x.AssetType == assetType) != null;
        }

        /// <summary>Returns false if nullifier already exists for this asset.</summary>
        public static bool TryRecordNullifier(string nullifierBase64, string assetType, long blockHeight, long timestamp, LiteDatabase? privacyDb = null)
        {
            var col = Nullifiers(privacyDb);
            if (col.FindOne(x => x.Nullifier == nullifierBase64 && x.AssetType == assetType) != null)
                return false;
            col.InsertSafe(new NullifierRecord
            {
                Nullifier = nullifierBase64,
                AssetType = assetType,
                BlockHeight = blockHeight,
                Timestamp = timestamp
            });
            return true;
        }
    }
}