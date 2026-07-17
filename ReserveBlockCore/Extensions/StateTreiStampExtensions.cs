using LiteDB;
using ReserveBlockCore.Data;
using ReserveBlockCore.Models;

namespace ReserveBlockCore.Extensions
{
    /// <summary>
    /// Typed overloads of the DbExtensions *Safe write methods for the two diff-tracked state treis.
    /// C# overload resolution prefers a non-generic method over a generic one, so EVERY existing
    /// and future call site that writes an AccountStateTrei or SmartContractStateTrei through the
    /// *Safe extensions automatically lands here and gets its <c>LastModifiedHeight</c> stamped
    /// with <see cref="StateWriteContext.StampHeight"/> — no per-call-site conversion needed, and
    /// no way for a new write site to silently skip stamping (any file that can call UpdateSafe
    /// already imports this namespace).
    ///
    /// StateSnapshotService relies on these stamps to diff-copy only changed records into
    /// snapshot slots. Deletions are handled separately via <see cref="StateTombstone"/>
    /// (see SmartContractStateTrei.DeleteSmartContract).
    /// </summary>
    public static class StateTreiStampExtensions
    {
        private static AccountStateTrei Stamp(AccountStateTrei entity)
        {
            entity.LastModifiedHeight = StateWriteContext.StampHeight;
            return entity;
        }

        private static SmartContractStateTrei Stamp(SmartContractStateTrei entity)
        {
            entity.LastModifiedHeight = StateWriteContext.StampHeight;
            return entity;
        }

        private static IEnumerable<AccountStateTrei> Stamp(IEnumerable<AccountStateTrei> entities)
        {
            var h = StateWriteContext.StampHeight;
            foreach (var e in entities)
            {
                e.LastModifiedHeight = h;
                yield return e;
            }
        }

        private static IEnumerable<SmartContractStateTrei> Stamp(IEnumerable<SmartContractStateTrei> entities)
        {
            var h = StateWriteContext.StampHeight;
            foreach (var e in entities)
            {
                e.LastModifiedHeight = h;
                yield return e;
            }
        }

        // ── AccountStateTrei ────────────────────────────────────────────────────────────────

        public static BsonValue InsertSafe(this ILiteCollection<AccountStateTrei> col, AccountStateTrei entity)
            => DbExtensions.InsertSafe<AccountStateTrei>(col, Stamp(entity));

        public static int InsertSafe(this ILiteCollection<AccountStateTrei> col, IEnumerable<AccountStateTrei> entities)
            => DbExtensions.InsertSafe<AccountStateTrei>(col, Stamp(entities));

        public static int InsertBulkSafe(this ILiteCollection<AccountStateTrei> col, IEnumerable<AccountStateTrei> entities, int batchSize = 5000)
            => DbExtensions.InsertBulkSafe<AccountStateTrei>(col, Stamp(entities), batchSize);

        public static bool UpdateSafe(this ILiteCollection<AccountStateTrei> col, AccountStateTrei entity)
            => DbExtensions.UpdateSafe<AccountStateTrei>(col, Stamp(entity));

        public static int UpdateSafe(this ILiteCollection<AccountStateTrei> col, IEnumerable<AccountStateTrei> entities)
            => DbExtensions.UpdateSafe<AccountStateTrei>(col, Stamp(entities));

        public static bool UpsertSafe(this ILiteCollection<AccountStateTrei> col, AccountStateTrei entity)
            => DbExtensions.UpsertSafe<AccountStateTrei>(col, Stamp(entity));

        public static Task<BsonValue> InsertSafeAsync(this ILiteCollection<AccountStateTrei> col, AccountStateTrei entity)
            => DbExtensions.InsertSafeAsync<AccountStateTrei>(col, Stamp(entity));

        public static Task<int> InsertSafeAsync(this ILiteCollection<AccountStateTrei> col, IEnumerable<AccountStateTrei> entities)
            => DbExtensions.InsertSafeAsync<AccountStateTrei>(col, Stamp(entities));

        public static Task<int> InsertBulkSafeAsync(this ILiteCollection<AccountStateTrei> col, IEnumerable<AccountStateTrei> entities, int batchSize = 5000)
            => DbExtensions.InsertBulkSafeAsync<AccountStateTrei>(col, Stamp(entities), batchSize);

        public static Task<bool> UpdateSafeAsync(this ILiteCollection<AccountStateTrei> col, AccountStateTrei entity)
            => DbExtensions.UpdateSafeAsync<AccountStateTrei>(col, Stamp(entity));

        public static Task<int> UpdateSafeAsync(this ILiteCollection<AccountStateTrei> col, IEnumerable<AccountStateTrei> entities)
            => DbExtensions.UpdateSafeAsync<AccountStateTrei>(col, Stamp(entities));

        public static Task<bool> UpsertSafeAsync(this ILiteCollection<AccountStateTrei> col, AccountStateTrei entity)
            => DbExtensions.UpsertSafeAsync<AccountStateTrei>(col, Stamp(entity));

        // ── SmartContractStateTrei ──────────────────────────────────────────────────────────

        public static BsonValue InsertSafe(this ILiteCollection<SmartContractStateTrei> col, SmartContractStateTrei entity)
            => DbExtensions.InsertSafe<SmartContractStateTrei>(col, Stamp(entity));

        public static int InsertSafe(this ILiteCollection<SmartContractStateTrei> col, IEnumerable<SmartContractStateTrei> entities)
            => DbExtensions.InsertSafe<SmartContractStateTrei>(col, Stamp(entities));

        public static int InsertBulkSafe(this ILiteCollection<SmartContractStateTrei> col, IEnumerable<SmartContractStateTrei> entities, int batchSize = 5000)
            => DbExtensions.InsertBulkSafe<SmartContractStateTrei>(col, Stamp(entities), batchSize);

        public static bool UpdateSafe(this ILiteCollection<SmartContractStateTrei> col, SmartContractStateTrei entity)
            => DbExtensions.UpdateSafe<SmartContractStateTrei>(col, Stamp(entity));

        public static int UpdateSafe(this ILiteCollection<SmartContractStateTrei> col, IEnumerable<SmartContractStateTrei> entities)
            => DbExtensions.UpdateSafe<SmartContractStateTrei>(col, Stamp(entities));

        public static bool UpsertSafe(this ILiteCollection<SmartContractStateTrei> col, SmartContractStateTrei entity)
            => DbExtensions.UpsertSafe<SmartContractStateTrei>(col, Stamp(entity));

        public static Task<BsonValue> InsertSafeAsync(this ILiteCollection<SmartContractStateTrei> col, SmartContractStateTrei entity)
            => DbExtensions.InsertSafeAsync<SmartContractStateTrei>(col, Stamp(entity));

        public static Task<int> InsertSafeAsync(this ILiteCollection<SmartContractStateTrei> col, IEnumerable<SmartContractStateTrei> entities)
            => DbExtensions.InsertSafeAsync<SmartContractStateTrei>(col, Stamp(entities));

        public static Task<int> InsertBulkSafeAsync(this ILiteCollection<SmartContractStateTrei> col, IEnumerable<SmartContractStateTrei> entities, int batchSize = 5000)
            => DbExtensions.InsertBulkSafeAsync<SmartContractStateTrei>(col, Stamp(entities), batchSize);

        public static Task<bool> UpdateSafeAsync(this ILiteCollection<SmartContractStateTrei> col, SmartContractStateTrei entity)
            => DbExtensions.UpdateSafeAsync<SmartContractStateTrei>(col, Stamp(entity));

        public static Task<int> UpdateSafeAsync(this ILiteCollection<SmartContractStateTrei> col, IEnumerable<SmartContractStateTrei> entities)
            => DbExtensions.UpdateSafeAsync<SmartContractStateTrei>(col, Stamp(entities));

        public static Task<bool> UpsertSafeAsync(this ILiteCollection<SmartContractStateTrei> col, SmartContractStateTrei entity)
            => DbExtensions.UpsertSafeAsync<SmartContractStateTrei>(col, Stamp(entity));
    }
}
