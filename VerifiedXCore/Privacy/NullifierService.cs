using LiteDB;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models.Privacy;

namespace VerifiedXCore.Privacy
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

        /// <summary>
        /// Whether <paramref name="nullifierB64"/> is the one Base64 spelling of a canonical 32-byte field element. The
        /// proof binds the decoded bytes, and the decoder accepts whitespace and other spellings of the same bytes, so
        /// from the proof-rules height a nullifier must be the spelling <see cref="Convert.ToBase64String(byte[])"/> gives.
        /// </summary>
        public static bool IsCanonicalNullifier(string? nullifierB64)
        {
            if (string.IsNullOrEmpty(nullifierB64)) return false;
            byte[] bytes;
            try { bytes = Convert.FromBase64String(nullifierB64); }
            catch (FormatException) { return false; }
            return PrivacyField.IsCanonicalLe(bytes) && string.Equals(Convert.ToBase64String(bytes), nullifierB64, StringComparison.Ordinal);
        }

        /// <summary>
        /// The spent-set key of a nullifier judged at <paramref name="height"/>: from the proof-rules height its 32 bytes,
        /// spelled canonically, so every spelling of one note is one key; before it the text as given, as earlier releases kept it.
        /// </summary>
        public static string SpentSetKey(string nullifierBase64, long height)
        {
            if (!PrivacyEpoch.ProofRulesActive(height) || string.IsNullOrEmpty(nullifierBase64))
                return nullifierBase64;
            try
            {
                var bytes = Convert.FromBase64String(nullifierBase64);
                return bytes.Length == PlonkNative.ScalarSize ? Convert.ToBase64String(bytes) : nullifierBase64;
            }
            catch (FormatException)
            {
                return nullifierBase64;
            }
        }

        private static long NextHeight() => (Globals.LastBlock?.Height ?? 0) + 1;

        /// <summary>Whether the nullifier is recorded as spent, judged at <paramref name="height"/> (the next block when null).</summary>
        public static bool IsNullifierSpentInDb(string nullifierBase64, string assetType, LiteDatabase? privacyDb = null, long? height = null)
        {
            var key = SpentSetKey(nullifierBase64, height ?? NextHeight());
            var col = Nullifiers(privacyDb);
            return col.FindOne(x => x.Nullifier == key && x.AssetType == assetType) != null;
        }

        /// <summary>Returns false if nullifier already exists for this asset.</summary>
        public static bool TryRecordNullifier(string nullifierBase64, string assetType, long blockHeight, long timestamp, LiteDatabase? privacyDb = null)
        {
            var key = SpentSetKey(nullifierBase64, blockHeight);
            var col = Nullifiers(privacyDb);
            if (col.FindOne(x => x.Nullifier == key && x.AssetType == assetType) != null)
                return false;
            col.InsertSafe(new NullifierRecord
            {
                Nullifier = key,
                AssetType = assetType,
                BlockHeight = blockHeight,
                Timestamp = timestamp
            });
            return true;
        }
    }
}