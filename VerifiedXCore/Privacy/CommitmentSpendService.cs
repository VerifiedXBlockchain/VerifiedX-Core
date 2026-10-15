using LiteDB;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models.Privacy;

namespace VerifiedXCore.Privacy
{
    /// <summary>
    /// Marks <see cref="CommitmentRecord.IsSpent"/> when a nullifier consumes a known tree position (see <see cref="PrivateTxPayload.SpentCommitmentTreePositions"/>).
    /// </summary>
    public static class CommitmentSpendService
    {
        public static void TryMarkSpent(string assetType, long treePosition, LiteDatabase db)
        {
            if (string.IsNullOrWhiteSpace(assetType) || treePosition < 0)
                return;
            var col = db.GetCollection<CommitmentRecord>(PrivacyDbContext.PRIV_COMMITMENTS);
            // Through the position index: the query on asset and position took the asset index and read the whole pool for
            // each note spent, and a spend names any position, past the end too (fifth review, round 34). The asset is
            // compared as the database compares it; the first of several is the earliest row, as before.
            var rec = col.Find(x => x.TreePosition == treePosition)
                .Where(x => x.AssetType != null && db.Collation.Compare(x.AssetType, assetType) == 0)
                .OrderBy(x => x.Id)
                .FirstOrDefault();
            if (rec == null)
                return;
            rec.IsSpent = true;
            col.UpdateSafe(rec);
        }
    }
}
