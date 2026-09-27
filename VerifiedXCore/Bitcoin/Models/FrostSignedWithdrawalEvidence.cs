using LiteDB;
using Newtonsoft.Json;
using VerifiedXCore.Data;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Bitcoin.Models
{
    /// <summary>
    /// Durable, validator-local record that THIS validator contributed a signature share to a
    /// Bitcoin transaction for a withdrawal (or bridge exit). The in-memory signing tracker expires
    /// after 24h and is lost on restart; a signed transaction never stops being broadcastable. This
    /// record backs two decisions: refusing to approve a cancellation for a withdrawal we already
    /// signed for, and refusing to sign a DIFFERENT transaction for the same withdrawal once the
    /// first one has confirmed on Bitcoin.
    /// </summary>
    public class FrostSignedWithdrawalEvidence
    {
        public long Id { get; set; }
        public string ScUID { get; set; } = string.Empty;
        public string WithdrawalRequestHash { get; set; } = string.Empty;
        public string BtcTxId { get; set; } = string.Empty;
        public string OutpointsJson { get; set; } = "[]";
        public long Timestamp { get; set; }

        public const string CollectionName = "rsrv_frost_signed_evidence";

        private static ILiteCollection<FrostSignedWithdrawalEvidence>? Coll()
        {
            var db = DbContext.DB_vBTC;
            if (db == null) return null;
            var c = db.GetCollection<FrostSignedWithdrawalEvidence>(CollectionName);
            c.EnsureIndex(x => x.ScUID, false);
            c.EnsureIndex(x => x.WithdrawalRequestHash, false);
            return c;
        }

        public List<string> Outpoints
        {
            get { try { return JsonConvert.DeserializeObject<List<string>>(OutpointsJson) ?? new(); } catch { return new(); } }
        }

        public static FrostSignedWithdrawalEvidence? Get(string scUID, string withdrawalRequestHash)
        {
            if (string.IsNullOrEmpty(scUID) || string.IsNullOrEmpty(withdrawalRequestHash)) return null;
            try { return Coll()?.FindOne(x => x.ScUID == scUID && x.WithdrawalRequestHash == withdrawalRequestHash); }
            catch { return null; }
        }

        /// <summary>Upsert: the most recently signed transaction for the withdrawal wins.</summary>
        public static bool Record(string scUID, string withdrawalRequestHash, string? btcTxId, IEnumerable<string>? outpoints)
        {
            if (string.IsNullOrEmpty(scUID) || string.IsNullOrEmpty(withdrawalRequestHash)) return false;
            try
            {
                var c = Coll();
                if (c == null) return false;
                var rec = c.FindOne(x => x.ScUID == scUID && x.WithdrawalRequestHash == withdrawalRequestHash)
                          ?? new FrostSignedWithdrawalEvidence { ScUID = scUID, WithdrawalRequestHash = withdrawalRequestHash };
                rec.BtcTxId = (btcTxId ?? string.Empty).Trim().ToLowerInvariant();
                rec.OutpointsJson = JsonConvert.SerializeObject((outpoints ?? Enumerable.Empty<string>()).Select(o => o.Trim().ToLowerInvariant()).ToList());
                rec.Timestamp = TimeUtil.GetTime();
                if (rec.Id == 0) c.Insert(rec); else c.Update(rec);
                return true;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"FrostSignedWithdrawalEvidence.Record failed for {scUID}/{withdrawalRequestHash}: {ex.Message}", "FrostSignedWithdrawalEvidence");
                return false;
            }
        }

        /// <summary>
        /// Where this validator's signing records begin. "I hold no signing record for this withdrawal" means "I never
        /// signed for it" only for withdrawals requested after this height: a validator restored onto a new machine has
        /// its key shares (peer backups) but not its records. Stamped once, when the node is at the network's height -
        /// a node still syncing has a low local tip, and stamping there would claim records for history it never saw.
        /// </summary>
        public class Epoch
        {
            public int Id { get; set; } = 1;
            public long EpochHeight { get; set; }
            public long StampedAtUtc { get; set; }
            public string Source { get; set; } = string.Empty;
        }

        public const string EpochCollectionName = "rsrv_frost_signed_evidence_epoch";

        private static ILiteCollection<Epoch>? EpochColl() => DbContext.DB_vBTC?.GetCollection<Epoch>(EpochCollectionName);

        public static Epoch? GetEpoch()
        {
            try { return EpochColl()?.FindById(1); }
            catch { return null; }
        }

        public static Epoch? SetEpoch(long epochHeight, string source)
        {
            try
            {
                var epoch = new Epoch { EpochHeight = Math.Max(0, epochHeight), StampedAtUtc = TimeUtil.GetTime(), Source = source };
                EpochColl()?.Upsert(epoch);
                return epoch;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"FrostSignedWithdrawalEvidence.SetEpoch failed: {ex.Message}", "FrostSignedWithdrawalEvidence");
                return null;
            }
        }

        /// <summary>Time of the oldest signing record held, or null when there is none.</summary>
        public static long? OldestRecordTimestamp()
        {
            try
            {
                var all = Coll()?.FindAll().Where(x => x.Timestamp > 0).ToList();
                return all == null || all.Count == 0 ? null : all.Min(x => x.Timestamp);
            }
            catch { return null; }
        }

        /// <summary>Every recorded signing (startup pin restore). Empty when the store is unavailable.</summary>
        public static List<FrostSignedWithdrawalEvidence> GetAll()
        {
            try { return Coll()?.FindAll().ToList() ?? new(); }
            catch { return new(); }
        }

        public static bool Delete(string scUID, string withdrawalRequestHash)
        {
            try { return (Coll()?.DeleteMany(x => x.ScUID == scUID && x.WithdrawalRequestHash == withdrawalRequestHash) ?? 0) > 0; }
            catch { return false; }
        }
    }
}
