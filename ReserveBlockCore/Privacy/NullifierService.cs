using LiteDB;
using ReserveBlockCore.Extensions;
using ReserveBlockCore.Models.Privacy;

namespace ReserveBlockCore.Privacy
{
    public static class NullifierService
    {
        private static ILiteCollection<NullifierRecord> Nullifiers(LiteDatabase? privacyDb = null) =>
            (privacyDb ?? PrivacyDbContext.GetPrivacyDb()).GetCollection<NullifierRecord>(PrivacyDbContext.PRIV_NULLIFIERS);

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
