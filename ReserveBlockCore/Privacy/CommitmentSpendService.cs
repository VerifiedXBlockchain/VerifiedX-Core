using LiteDB;
using ReserveBlockCore.Extensions;
using ReserveBlockCore.Models.Privacy;

namespace ReserveBlockCore.Privacy
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
            var rec = col.FindOne(x => x.AssetType == assetType && x.TreePosition == treePosition);
            if (rec == null)
                return;
            rec.IsSpent = true;
            col.UpdateSafe(rec);
        }
    }
}
