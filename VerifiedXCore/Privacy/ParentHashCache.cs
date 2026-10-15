using System.Buffers.Binary;
using System.Collections.Concurrent;

namespace VerifiedXCore.Privacy
{
    /// <summary>
    /// Remembers Merkle parent hashes (a pure function of the two children). A pool's root was recomputed from every leaf
    /// for each private transaction applied, one native Poseidon call per pair: about 0.5 s a transaction at 10,000
    /// leaves, on every node applying the block, growing with the pool (fifth review, round 32). With the pairs
    /// remembered, a root after an append costs the new leaf's path. Bounded: cleared when it holds
    /// <see cref="MaxEntries"/> pairs (about 150 MB; a tree of n leaves holds about 2n).
    /// </summary>
    internal static class ParentHashCache
    {
        internal const int MaxEntries = 1_000_000;

        private readonly record struct Pair(char Kind, ulong L0, ulong L1, ulong L2, ulong L3, ulong R0, ulong R1, ulong R2, ulong R3);

        private static readonly ConcurrentDictionary<Pair, byte[]> _pairs = new();
        [ThreadStatic] private static long _computedOnThisThread;

        /// <summary>Parent hashes this thread computed rather than remembered (tests).</summary>
        internal static long ComputedOnThisThread => _computedOnThisThread;

        /// <summary>The parent of <paramref name="left"/> and <paramref name="right"/> (32 bytes each) under the hash <paramref name="kind"/> names.</summary>
        internal static byte[] Get(char kind, byte[] left, byte[] right, Func<byte[]> compute)
        {
            if (left.Length != PlonkNative.ScalarSize || right.Length != PlonkNative.ScalarSize)
            {
                _computedOnThisThread++;
                return compute();
            }
            var key = new Pair(kind,
                BinaryPrimitives.ReadUInt64LittleEndian(left.AsSpan(0)), BinaryPrimitives.ReadUInt64LittleEndian(left.AsSpan(8)),
                BinaryPrimitives.ReadUInt64LittleEndian(left.AsSpan(16)), BinaryPrimitives.ReadUInt64LittleEndian(left.AsSpan(24)),
                BinaryPrimitives.ReadUInt64LittleEndian(right.AsSpan(0)), BinaryPrimitives.ReadUInt64LittleEndian(right.AsSpan(8)),
                BinaryPrimitives.ReadUInt64LittleEndian(right.AsSpan(16)), BinaryPrimitives.ReadUInt64LittleEndian(right.AsSpan(24)));
            if (_pairs.TryGetValue(key, out var known))
                return (byte[])known.Clone();
            var parent = compute();
            _computedOnThisThread++;
            if (_pairs.Count >= MaxEntries)
                _pairs.Clear();
            _pairs[key] = (byte[])parent.Clone();
            return parent;
        }
    }
}
