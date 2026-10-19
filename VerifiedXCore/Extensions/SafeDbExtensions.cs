using LiteDB;
using VerifiedXCore.Utilities;
using System.Collections.Concurrent;
using System.Linq.Expressions;

namespace VerifiedXCore.Extensions
{
    public static class DbExtensions
    {
        /// <summary>
        /// Counts the failures these wrappers swallow (logged, a default returned) in the named collections, within the
        /// work that opened it (it flows with that work's awaits and tasks, not to other threads). A caller whose writes
        /// must all land (a block's apply, the withdrawal-store rebuild) opens one and checks Count when done.
        /// </summary>
        public sealed class FailureScope : IDisposable
        {
            private static readonly AsyncLocal<FailureScope?> CurrentScope = new();
            private readonly FailureScope? _outer;
            private readonly HashSet<string> _collections;
            private readonly Func<Exception, bool>? _counts;
            private int _count;

            public FailureScope(params string[] collections) : this(null, collections) { }

            /// <summary>
            /// A scope that counts a swallowed exception only when <paramref name="counts"/> accepts it (a fault of this
            /// node's storage, say); a failure noted without its exception (<see cref="NoteFailure(string?)"/>) always counts.
            /// </summary>
            public FailureScope(Func<Exception, bool>? counts, params string[] collections)
            {
                _collections = new HashSet<string>(collections, StringComparer.Ordinal);
                _counts = counts;
                _outer = CurrentScope.Value;
                CurrentScope.Value = this;
            }

            public int Count => Volatile.Read(ref _count);

            public void Dispose() => CurrentScope.Value = _outer;

            internal static void Note(string? collection, Exception? ex = null)
            {
                if (collection == null) return;
                for (var scope = CurrentScope.Value; scope != null; scope = scope._outer)
                    if (scope._collections.Contains(collection) && (ex == null || scope._counts == null || scope._counts(ex)))
                        Interlocked.Increment(ref scope._count);
            }
        }

        /// <summary>Reports a failure swallowed elsewhere (a catch around this collection's writes) to the open scopes.</summary>
        public static void NoteFailure(string? collection) => FailureScope.Note(collection);

        private static void NoteFailure<T>(ILiteCollection<T>? col, Exception ex)
        {
            try { FailureScope.Note(col?.Name, ex); } catch { }
        }
        private static ConcurrentDictionary<string, SemaphoreSlim> DbSemaphores = new ConcurrentDictionary<string, SemaphoreSlim>();        

        private static SemaphoreSlim GetSlim<T>(this ILiteCollection<T> col)
        {
            if (!DbSemaphores.TryGetValue(col.Name, out SemaphoreSlim DbSemaphore))
            {
                DbSemaphore = new SemaphoreSlim(1, 1);
                if (!DbSemaphores.TryAdd(col.Name, DbSemaphore))
                    DbSemaphores.TryGetValue(col.Name, out DbSemaphore);
            }

            return DbSemaphore;
        }

        // HAL-16 Deadlock Fix: Original synchronous methods kept for backward compatibility
        public static S Command<S, T>(this ILiteCollection<T> col, Func<S> cmd)
        {
            SemaphoreSlim slim = null;
            S Result = default;
            try
            {
                slim = col.GetSlim();
                slim.Wait();
                Result =  cmd();
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Unknown Error: {ex.ToString()}", "SafeDBExtensions.Command()-1");
                NoteFailure(col, ex);                
            }

            try { slim.Release(); } catch { }            
            return Result;
        }

        public static void Command<T>(this ILiteCollection<T> col, Action cmd)
        {
            SemaphoreSlim slim = null;
            try
            {
                slim = col.GetSlim();
                slim.Wait();
                cmd();
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Unknown Error: {ex.ToString()}", "SafeDBExtensions.Command()-2");
                NoteFailure(col, ex);
            }

            try { slim.Release(); } catch { }            
        }

        // HAL-16 Deadlock Fix: New async methods that use WaitAsync() instead of Wait()
        // This prevents thread pool starvation and allows blocks to process during TX load
        public static async Task<S> CommandAsync<S, T>(this ILiteCollection<T> col, Func<S> cmd)
        {
            SemaphoreSlim slim = null;
            S Result = default;
            try
            {
                slim = col.GetSlim();
                await slim.WaitAsync();  // ✅ Returns thread to pool while waiting
                Result = cmd();
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Unknown Error: {ex.ToString()}", "SafeDBExtensions.CommandAsync()-1");
                NoteFailure(col, ex);                
            }

            try { slim?.Release(); } catch { }            
            return Result;
        }

        public static async Task CommandAsync<T>(this ILiteCollection<T> col, Action cmd)
        {
            SemaphoreSlim slim = null;
            try
            {
                slim = col.GetSlim();
                await slim.WaitAsync();  // ✅ Returns thread to pool while waiting
                cmd();
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Unknown Error: {ex.ToString()}", "SafeDBExtensions.CommandAsync()-2");
                NoteFailure(col, ex);
            }

            try { slim?.Release(); } catch { }            
        }

        //
        // Summary:
        //     Insert or Update a document in this collection.
        public static bool UpsertSafe<T>(this ILiteCollection<T> col, T entity)
        {
            return Command(col, () => col.Upsert(entity));
        }

        //
        // Summary:
        //     Insert or Update all documents
        public static int UpsertSafe<T>(this ILiteCollection<T> col, IEnumerable<T> entities)
        {
            return Command(col, () => col.Upsert(entities));
        }

        //
        // Summary:
        //     Insert or Update a document in this collection.
        public static bool UpsertSafe<T>(this ILiteCollection<T> col, BsonValue id, T entity)
        {
            return Command(col, () => col.Upsert(id, entity));
        }

        //
        // Summary:
        //     Update a document in this collection. Returns false if not found document in
        //     collection
        public static bool UpdateSafe<T>(this ILiteCollection<T> col, T entity)
        {
            return Command(col, () => col.Update(entity));
        }

        /// <summary>
        /// As <see cref="UpdateSafe{T}(ILiteCollection{T}, T)"/> (the collection's lock held), but a failure is thrown, not
        /// logged and dropped: for consensus writes whose loss would leave state other than the chain says.
        /// </summary>
        public static bool UpdateOrThrow<T>(this ILiteCollection<T> col, T entity)
        {
            var slim = col.GetSlim();
            slim.Wait();
            try { return col.Update(entity); }
            finally { try { slim.Release(); } catch { } }
        }

        //
        // Summary:
        //     Update a document in this collection. Returns false if not found document in
        //     collection
        public static bool UpdateSafe<T>(this ILiteCollection<T> col, BsonValue id, T entity)
        {
            return Command(col, () => col.Update(id, entity));
        }

        //
        // Summary:
        //     Update all documents
        public static int UpdateSafe<T>(this ILiteCollection<T> col, IEnumerable<T> entities)
        {
            return Command(col, () => col.Update(entities));
        }

        //
        // Summary:
        //     Update many documents based on transform expression. This expression must return
        //     a new document that will be replaced over current document (according with predicate).
        //     Eg: col.UpdateManySafe("{ Name: UPPER($.Name), Age }", "_id > 0")
        public static int UpdateManySafe<T>(this ILiteCollection<T> col, BsonExpression transform, BsonExpression predicate)
        {
            return Command(col, () => col.UpdateMany(transform, predicate));
        }

        //
        // Summary:
        //     Update many document based on merge current document with extend expression.
        //     Use your class with initializers. Eg: col.UpdateManySafe(x => new Customer { Name
        //     = x.Name.ToUpper(), Salary: 100 }, x => x.Name == "John")
        public static int UpdateManySafe<T>(this ILiteCollection<T> col, Expression<Func<T, T>> extend, Expression<Func<T, bool>> predicate)
        {
            return Command(col, () => col.UpdateMany(extend, predicate));
        }

        //
        // Summary:
        //     Insert a new entity to this collection. Document Id must be a new value in collection
        //     - Returns document Id
        public static BsonValue InsertSafe<T>(this ILiteCollection<T> col, T entity)
        {
            return Command(col, () => col.Insert(entity));
        }

        //
        // Summary:
        //     Insert a new document to this collection using passed id value.
        public static void InsertSafe<T>(this ILiteCollection<T> col, BsonValue id, T entity)
        {
            Command(col, () => col.Insert(id, entity));
        }

        //
        // Summary:
        //     Insert an array of new documents to this collection. Document Id must be a new
        //     value in collection. Can be set buffer size to commit at each N documents
        public static int InsertSafe<T>(this ILiteCollection<T> col, IEnumerable<T> entities)
        {
            return Command(col, () => col.Insert(entities));
        }

        //
        // Summary:
        //     Implements bulk insert documents in a collection. Usefull when need lots of documents.
        public static int InsertBulkSafe<T>(this ILiteCollection<T> col, IEnumerable<T> entities, int batchSize = 5000)
        {
            return Command(col, () => col.InsertBulk(entities, batchSize));
        }


        //
        // Summary:
        //     Create a new permanent index in all documents inside this collections if index
        //     not exists already. Returns true if index was created or false if already exits
        //
        // Parameters:
        //   name:
        //     Index name - unique name for this collection
        //
        //   expression:
        //     Create a custom expression function to be indexed
        //
        //   unique:
        //     If is a unique index
        public static bool EnsureIndexSafe<T>(this ILiteCollection<T> col, string name, BsonExpression expression, bool unique = false)
        {
            return Command(col, () => col.EnsureIndex(name, expression, unique));
        }

        //
        // Summary:
        //     Create a new permanent index in all documents inside this collections if index
        //     not exists already.
        //
        // Parameters:
        //   keySelector:
        //     LinqExpression to be converted into BsonExpression to be indexed
        //
        //   unique:
        //     Create a unique keys index?
        public static bool EnsureIndexSafe<K, T>(this ILiteCollection<T> col, Expression<Func<T, K>> keySelector, bool unique = false)
        {
            return Command(col, () => col.EnsureIndex(keySelector, unique));
        }

        //
        // Summary:
        //     Create a new permanent index in all documents inside this collections if index
        //     not exists already. Returns true if index was created or false if already exits
        //
        // Parameters:
        //   expression:
        //     Document field/expression
        //
        //   unique:
        //     If is a unique index
        public static bool EnsureIndexSafe<T>(this ILiteCollection<T> col, BsonExpression expression, bool unique = false)
        {
            return Command(col, () => col.EnsureIndex(expression, unique));
        }

        //
        // Summary:
        //     Create a new permanent index in all documents inside this collections if index
        //     not exists already.
        //
        // Parameters:
        //   keySelector:
        //     LinqExpression to be converted into BsonExpression to be indexed
        //
        //   unique:
        //     Create a unique keys index?
        public static bool EnsureIndex<T, K>(this ILiteCollection<T> col, Expression<Func<T, K>> keySelector, bool unique = false)
        {
            return Command(col, () => col.EnsureIndex(keySelector, unique));
        }

        //
        // Summary:
        //     Create a new permanent index in all documents inside this collections if index
        //     not exists already.
        //
        // Parameters:
        //   name:
        //     Index name - unique name for this collection
        //
        //   keySelector:
        //     LinqExpression to be converted into BsonExpression to be indexed
        //
        //   unique:
        //     Create a unique keys index?
        public static bool EnsureIndex<T, K>(this ILiteCollection<T> col, string name, Expression<Func<T, K>> keySelector, bool unique = false)
        {
            return Command(col, () => col.EnsureIndex(name, keySelector, unique));
        }

        //
        // Summary:
        //     Drop index and release slot for another index
        public static bool DropIndexSafe<T>(this ILiteCollection<T> col, string name)
        {
            return Command(col, () => col.DropIndex(name));
        }

        //
        // Summary:
        //     Delete a single document on collection based on _id index. Returns true if document
        //     was deleted
        public static bool DeleteSafe<T>(this ILiteCollection<T> col, BsonValue id)
        {
            return Command(col, () => col.Delete(id));
        }

        //
        // Summary:
        //     Delete all documents inside collection. Returns how many documents was deleted.
        //     Run inside current transaction
        public static int DeleteAllSafe<T>(this ILiteCollection<T> col)
        {
            return Command(col, () => col.DeleteAll());
        }

        //
        // Summary:
        //     Delete all documents based on predicate expression. Returns how many documents
        //     was deleted
        public static int DeleteManySafe<T>(this ILiteCollection<T> col, BsonExpression predicate)
        {
            return Command(col, () => col.DeleteMany(predicate));
        }

        //
        // Summary:
        //     Delete all documents based on predicate expression. Returns how many documents
        //     was deleted
        public static int DeleteManySafe<T>(this ILiteCollection<T> col, string predicate, BsonDocument parameters)
        {
            return Command(col, () => col.DeleteMany(predicate, parameters));
        }

        //
        // Summary:
        //     Delete all documents based on predicate expression. Returns how many documents
        //     was deleted
        public static int DeleteManySafe<T>(this ILiteCollection<T> col, string predicate, params BsonValue[] args)
        {
            return Command(col, () => col.DeleteMany(predicate, args));
        }

        //
        // Summary:
        //     Delete all documents based on predicate expression. Returns how many documents
        //     was deleted
        public static int DeleteManySafe<T>(this ILiteCollection<T> col, Expression<Func<T, bool>> predicate)
        {
            return Command(col, () => col.DeleteMany(predicate));
        }

        // HAL-16 Deadlock Fix: Async variants of all Safe methods
        // These use CommandAsync internally to prevent thread pool starvation

        public static async Task<bool> UpsertSafeAsync<T>(this ILiteCollection<T> col, T entity)
        {
            return await CommandAsync(col, () => col.Upsert(entity));
        }

        public static async Task<int> UpsertSafeAsync<T>(this ILiteCollection<T> col, IEnumerable<T> entities)
        {
            return await CommandAsync(col, () => col.Upsert(entities));
        }

        public static async Task<bool> UpsertSafeAsync<T>(this ILiteCollection<T> col, BsonValue id, T entity)
        {
            return await CommandAsync(col, () => col.Upsert(id, entity));
        }

        public static async Task<bool> UpdateSafeAsync<T>(this ILiteCollection<T> col, T entity)
        {
            return await CommandAsync(col, () => col.Update(entity));
        }

        public static async Task<bool> UpdateSafeAsync<T>(this ILiteCollection<T> col, BsonValue id, T entity)
        {
            return await CommandAsync(col, () => col.Update(id, entity));
        }

        public static async Task<int> UpdateSafeAsync<T>(this ILiteCollection<T> col, IEnumerable<T> entities)
        {
            return await CommandAsync(col, () => col.Update(entities));
        }

        public static async Task<int> UpdateManySafeAsync<T>(this ILiteCollection<T> col, BsonExpression transform, BsonExpression predicate)
        {
            return await CommandAsync(col, () => col.UpdateMany(transform, predicate));
        }

        public static async Task<int> UpdateManySafeAsync<T>(this ILiteCollection<T> col, Expression<Func<T, T>> extend, Expression<Func<T, bool>> predicate)
        {
            return await CommandAsync(col, () => col.UpdateMany(extend, predicate));
        }

        public static async Task<BsonValue> InsertSafeAsync<T>(this ILiteCollection<T> col, T entity)
        {
            return await CommandAsync(col, () => col.Insert(entity));
        }

        public static async Task InsertSafeAsync<T>(this ILiteCollection<T> col, BsonValue id, T entity)
        {
            await CommandAsync(col, () => col.Insert(id, entity));
        }

        public static async Task<int> InsertSafeAsync<T>(this ILiteCollection<T> col, IEnumerable<T> entities)
        {
            return await CommandAsync(col, () => col.Insert(entities));
        }

        public static async Task<int> InsertBulkSafeAsync<T>(this ILiteCollection<T> col, IEnumerable<T> entities, int batchSize = 5000)
        {
            return await CommandAsync(col, () => col.InsertBulk(entities, batchSize));
        }

        public static async Task<bool> DeleteSafeAsync<T>(this ILiteCollection<T> col, BsonValue id)
        {
            return await CommandAsync(col, () => col.Delete(id));
        }

        public static async Task<int> DeleteAllSafeAsync<T>(this ILiteCollection<T> col)
        {
            return await CommandAsync(col, () => col.DeleteAll());
        }

        public static async Task<int> DeleteManySafeAsync<T>(this ILiteCollection<T> col, BsonExpression predicate)
        {
            return await CommandAsync(col, () => col.DeleteMany(predicate));
        }

        public static async Task<int> DeleteManySafeAsync<T>(this ILiteCollection<T> col, string predicate, BsonDocument parameters)
        {
            return await CommandAsync(col, () => col.DeleteMany(predicate, parameters));
        }

        public static async Task<int> DeleteManySafeAsync<T>(this ILiteCollection<T> col, string predicate, params BsonValue[] args)
        {
            return await CommandAsync(col, () => col.DeleteMany(predicate, args));
        }

        public static async Task<int> DeleteManySafeAsync<T>(this ILiteCollection<T> col, Expression<Func<T, bool>> predicate)
        {
            return await CommandAsync(col, () => col.DeleteMany(predicate));
        }
    }
}
