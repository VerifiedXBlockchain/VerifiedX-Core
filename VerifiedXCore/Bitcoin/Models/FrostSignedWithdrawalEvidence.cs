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
        /// <summary>When this record was first written (kept when it is written again); 0 on a record from before the field.</summary>
        public long FirstSignedAt { get; set; }
        /// <summary>An operator confirmed that nothing signed for this withdrawal before exact spellings can still pay (see <see cref="NeedsOperatorRelease"/>).</summary>
        public bool OperatorReleased { get; set; }

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

        /// <summary>The record for a payment: under its reference as given, else under another spelling of it (<see cref="ForPayment"/>).</summary>
        public static FrostSignedWithdrawalEvidence? Get(string scUID, string withdrawalRequestHash)
        {
            if (string.IsNullOrEmpty(scUID) || string.IsNullOrEmpty(withdrawalRequestHash)) return null;
            try { return Coll()?.FindOne(x => x.ScUID == scUID && x.WithdrawalRequestHash == withdrawalRequestHash) ?? ForPayment(scUID, withdrawalRequestHash).FirstOrDefault(); }
            catch { return null; }
        }

        /// <summary>
        /// Every record this validator holds for one payment of a vault: under the reference the chain names it by, and
        /// under any other spelling an earlier version signed it under (padded or in another case; for a bridge exit, a
        /// longer, shorter or 0x-prefixed burn hash). A record whose spelling could name this payment counts as one for it.
        /// </summary>
        public static List<FrostSignedWithdrawalEvidence> ForPayment(string scUID, string reference)
        {
            if (string.IsNullOrEmpty(scUID) || string.IsNullOrEmpty(reference)) return new();
            try { return Coll()?.Find(x => x.ScUID == scUID).Where(x => SamePayment(x.WithdrawalRequestHash, reference)).ToList() ?? new(); }
            catch { return new(); }
        }

        /// <summary>Whether a recorded reference may name the same payment of the same vault as <paramref name="reference"/>.</summary>
        internal static bool SamePayment(string? recorded, string reference)
        {
            var r = (recorded ?? string.Empty).Trim();
            if (string.Equals(r, reference.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
            var exit = FROST.FrostSigningAuthorization.BtcExitPrefix;
            if (!r.StartsWith(exit, StringComparison.OrdinalIgnoreCase) || !reference.Trim().StartsWith(exit, StringComparison.OrdinalIgnoreCase)) return false;
            // A bridge exit is named by its burn hash's prefix (the vault is the record's own): prefixes of one hash overlap.
            var a = ExitBurnPart(r);
            var b = ExitBurnPart(reference.Trim());
            return a.Length > 0 && b.Length > 0 && (a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal));
        }

        private static string ExitBurnPart(string reference)
        {
            var parts = reference.Substring(FROST.FrostSigningAuthorization.BtcExitPrefix.Length).Split('_');
            if (parts.Length != 2) return string.Empty;
            var p = parts[0].Trim().ToLowerInvariant();
            return p.StartsWith("0x") ? p.Substring(2) : p;
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
                if (rec.Id == 0) rec.FirstSignedAt = rec.Timestamp;
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
        /// When this validator first ran signing only under the exact reference the chain records. Before, a signing under
        /// another letter case of the reference was saved over the record of the first transaction signed for the same
        /// withdrawal, so a record from then may be the only trace of two transactions.
        /// </summary>
        public class ExactSpelling
        {
            public int Id { get; set; } = 1;
            public long Since { get; set; }
        }

        public const string ExactSpellingCollectionName = "rsrv_frost_signed_evidence_exact";

        /// <summary>Stamps, once, the time this validator first ran with exact spellings (at the FROST server's start); returns it.</summary>
        public static long? EnsureExactSpellingSince()
        {
            try
            {
                var c = DbContext.DB_vBTC?.GetCollection<ExactSpelling>(ExactSpellingCollectionName);
                if (c == null) return null;
                var row = c.FindById(1);
                if (row != null) return row.Since;
                row = new ExactSpelling { Since = TimeUtil.GetTime() };
                c.Upsert(row);
                return row.Since;
            }
            catch { return null; }
        }

        public static long? ExactSpellingSince()
        {
            try { return DbContext.DB_vBTC?.GetCollection<ExactSpelling>(ExactSpellingCollectionName).FindById(1)?.Since; }
            catch { return null; }
        }

        /// <summary>
        /// A record written before exact spellings, not yet released by an operator: proving its transaction dead does not
        /// by itself let another be signed or the withdrawal be refunded, since a transaction signed under another
        /// spelling may have been recorded over.
        /// </summary>
        public bool NeedsOperatorRelease(long? exactSince) =>
            !OperatorReleased && (FirstSignedAt == 0 || (exactSince.HasValue && FirstSignedAt < exactSince.Value));

        /// <summary>Marks every record this validator holds for one payment as released by its operator; returns how many.</summary>
        public static int ReleaseByOperator(string scUID, string reference)
        {
            try
            {
                var c = Coll();
                if (c == null) return 0;
                var n = 0;
                foreach (var r in ForPayment(scUID, reference))
                {
                    r.OperatorReleased = true;
                    if (c.Update(r)) n++;
                }
                return n;
            }
            catch { return 0; }
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

        /// <summary>One record, by its row: records of other transactions for the same withdrawal can share its stored reference.</summary>
        public static bool DeleteRecord(long id)
        {
            try { return Coll()?.Delete(id) ?? false; }
            catch { return false; }
        }

        public static bool Delete(string scUID, string withdrawalRequestHash)
        {
            try { return (Coll()?.DeleteMany(x => x.ScUID == scUID && x.WithdrawalRequestHash == withdrawalRequestHash) ?? 0) > 0; }
            catch { return false; }
        }
    }
}
