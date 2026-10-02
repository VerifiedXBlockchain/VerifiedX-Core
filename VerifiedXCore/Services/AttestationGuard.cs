using System.Collections.Concurrent;

namespace VerifiedXCore.Services
{
    /// <summary>
    /// A caster signs at most one block per height. Its attestation is what makes a block's certificate valid, so signing
    /// two different blocks at one height (equivocation) lets two competing blocks both reach a quorum — the precondition
    /// for a committed fork. Both signing sites (the pre-commit certificate attach and the post-acceptance publisher)
    /// claim the height here first; a second, different hash at a claimed height is refused.
    /// </summary>
    public static class AttestationGuard
    {
        private const long RetainHeights = 200;
        private static readonly ConcurrentDictionary<long, string> _signed = new();

        /// <summary>True when this node may sign <paramref name="hash"/> at <paramref name="height"/>: nothing signed there yet, or the same hash.</summary>
        public static bool TryClaim(long height, string? hash)
        {
            if (string.IsNullOrEmpty(hash)) return false;
            var claimed = _signed.GetOrAdd(height, hash);
            if (_signed.Count > RetainHeights * 2)
                Prune(height);
            return string.Equals(claimed, hash, StringComparison.Ordinal);
        }

        /// <summary>The hash this node signed at the height, if any.</summary>
        public static string? SignedAt(long height) => _signed.TryGetValue(height, out var h) ? h : null;

        private static void Prune(long current)
        {
            foreach (var h in _signed.Keys)
                if (h < current - RetainHeights) _signed.TryRemove(h, out _);
        }

        /// <summary>Test hook.</summary>
        internal static void ResetForTests() => _signed.Clear();
    }
}
