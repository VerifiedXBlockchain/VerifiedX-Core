using VerifiedXCore.Data;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Services
{
    /// <summary>
    /// A caster signs at most one block per height. Its attestation is what makes a block's certificate valid, so signing
    /// two different blocks at one height (equivocation) lets two competing blocks both reach a quorum — the precondition
    /// for a committed fork. Both signing sites (the pre-commit certificate attach and the post-acceptance publisher)
    /// claim the height here first; a second, different hash at a claimed height is refused.
    ///
    /// The claim is written to disk before the signature exists, so a restart cannot make the caster forget what it
    /// signed (it used to live in memory only). When the store cannot be read or written the claim is refused: a caster
    /// that cannot tell what it signed does not sign.
    /// </summary>
    public static class AttestationGuard
    {
        private const long RetainHeights = 200;
        private const string COLLECTION_NAME = "rsrv_attestation_signed";

        /// <summary>The block this node signed at a height (Id = height).</summary>
        public class SignedHeight
        {
            public long Id { get; set; }
            public string Hash { get; set; } = "";
        }

        internal interface IStore
        {
            /// <summary>Every stored claim, or null when the store is unavailable.</summary>
            List<SignedHeight>? Load();
            bool Save(long height, string hash);
            void DeleteBelow(long height);
            void Clear();
        }

        private sealed class LiteDbStore : IStore
        {
            private static LiteDB.ILiteCollection<SignedHeight>? Col() => DbContext.DB_Config?.GetCollection<SignedHeight>(COLLECTION_NAME);

            public List<SignedHeight>? Load()
            {
                try { return Col()?.FindAll().ToList(); }
                catch { return null; }
            }

            public bool Save(long height, string hash)
            {
                try
                {
                    var col = Col();
                    if (col == null) return false;
                    col.Insert(height, new SignedHeight { Id = height, Hash = hash });
                    return true;
                }
                catch { return false; }
            }

            public void DeleteBelow(long height)
            {
                try { Col()?.DeleteMany(x => x.Id < height); } catch { }
            }

            public void Clear()
            {
                try { Col()?.DeleteAll(); } catch { }
            }
        }

        /// <summary>Test store: survives <see cref="ForgetMemoryForTests"/> the way the database survives a restart.</summary>
        internal sealed class MemoryStore : IStore
        {
            private readonly Dictionary<long, string> _rows = new();
            public bool Available = true;
            public List<SignedHeight>? Load() => Available ? _rows.Select(kv => new SignedHeight { Id = kv.Key, Hash = kv.Value }).ToList() : null;
            public bool Save(long height, string hash) { if (!Available) return false; _rows[height] = hash; return true; }
            public void DeleteBelow(long height) { foreach (var h in _rows.Keys.Where(k => k < height).ToList()) _rows.Remove(h); }
            public void Clear() => _rows.Clear();
        }

        private static readonly object Mut = new();
        private static IStore _store = new LiteDbStore();
        private static Dictionary<long, string>? _signed;

        /// <summary>The stored claims, read once per process; null while the store is unavailable (retried on the next call).</summary>
        private static Dictionary<long, string>? Loaded()
        {
            if (_signed != null) return _signed;
            var rows = _store.Load();
            if (rows == null) return null;
            _signed = rows.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First().Hash);
            return _signed;
        }

        /// <summary>True when this node may sign <paramref name="hash"/> at <paramref name="height"/>: nothing signed there yet, or the same hash.</summary>
        public static bool TryClaim(long height, string? hash)
        {
            if (string.IsNullOrEmpty(hash)) return false;
            lock (Mut)
            {
                var signed = Loaded();
                if (signed == null) return false;
                if (signed.TryGetValue(height, out var claimed))
                    return string.Equals(claimed, hash, StringComparison.Ordinal);
                if (!_store.Save(height, hash)) return false;
                signed[height] = hash;
                if (signed.Count > RetainHeights * 2)
                    Prune(signed, height);
                return true;
            }
        }

        /// <summary>The hash this node signed at the height, if any.</summary>
        public static string? SignedAt(long height)
        {
            lock (Mut)
            {
                var signed = Loaded();
                return signed != null && signed.TryGetValue(height, out var h) ? h : null;
            }
        }

        private static void Prune(Dictionary<long, string> signed, long current)
        {
            var cutoff = current - RetainHeights;
            foreach (var h in signed.Keys.Where(k => k < cutoff).ToList())
                signed.Remove(h);
            _store.DeleteBelow(cutoff);
        }

        /// <summary>
        /// Operator reset (localhost endpoint): forgets every stored claim. Before the claims were stored, restarting a
        /// caster did this by accident; now it takes a deliberate action. Only for a height that can never be certified
        /// with the block this node signed.
        /// </summary>
        public static int ClearForOperatorReset()
        {
            lock (Mut)
            {
                var count = Loaded()?.Count ?? 0;
                _store.Clear();
                _signed = null;
                CasterLogUtility.Log($"ATTEST-GUARD: OPERATOR RESET — cleared {count} signed-height claim(s).", "CERT");
                return count;
            }
        }

        /// <summary>Test hook: a fresh in-memory store and no cached claims.</summary>
        internal static MemoryStore ResetForTests()
        {
            lock (Mut)
            {
                var store = new MemoryStore();
                _store = store;
                _signed = null;
                return store;
            }
        }

        /// <summary>Test hook: what a restart does — the cache is gone, the store is not.</summary>
        internal static void ForgetMemoryForTests()
        {
            lock (Mut) { _signed = null; }
        }
    }
}
