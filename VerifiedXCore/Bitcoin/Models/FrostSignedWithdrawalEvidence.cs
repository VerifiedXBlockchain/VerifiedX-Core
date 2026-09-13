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

        public static bool Delete(string scUID, string withdrawalRequestHash)
        {
            try { return (Coll()?.DeleteMany(x => x.ScUID == scUID && x.WithdrawalRequestHash == withdrawalRequestHash) ?? 0) > 0; }
            catch { return false; }
        }
    }
}
