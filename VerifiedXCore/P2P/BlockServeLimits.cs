using System.Collections.Concurrent;
using System.Text;
using Newtonsoft.Json;
using VerifiedXCore.Data;
using VerifiedXCore.Models;

namespace VerifiedXCore.P2P
{
    /// <summary>
    /// VX-09: bounds for serving blocks to unauthenticated peers on the general hub.
    ///
    /// SendBlockList materialised whatever height range the caller named (0..long.MaxValue = the whole
    /// chain) and then built a JSON copy, a compressed copy and a base64 copy of it; nothing bounded the
    /// range, the bytes, or how many such requests one connection could run at once (the hub allows 200
    /// parallel invocations per client).
    ///
    /// The generic SignalRQueue is deliberately NOT used here: it adds an exponential delay to requests that
    /// arrive less than a second apart, which is exactly how block download works, so it would throttle
    /// legitimate sync (the likely reason SendBlock's queue wrapper was left commented out).
    /// </summary>
    public static class BlockServeLimits
    {
        /// <summary>Most blocks one SendBlockList reply may contain.</summary>
        public const int MaxBlocksPerList = 1000;

        /// <summary>Most serialized block bytes one SendBlockList reply may contain (≥ the V2 client's 1 MB
        /// spans; matches the 8 MB batch planned for block streaming V3).</summary>
        public const long MaxListBytes = 8L * 1024 * 1024;

        /// <summary>Concurrent block-serving requests allowed per peer IP, and in total.</summary>
        public const int MaxConcurrentPerIp = 2;
        public const int MaxConcurrentGlobal = 16;

        /// <summary>
        /// One entry per peer address that is being served or waiting; removed when its last user leaves. Kept forever,
        /// the table grew by one semaphore for every distinct address that ever asked (an IPv6 host has billions).
        /// </summary>
        private sealed class PeerSlot
        {
            public readonly SemaphoreSlim Semaphore = new(MaxConcurrentPerIp, MaxConcurrentPerIp);
            public int Users;
        }
        private static readonly Dictionary<string, PeerSlot> _perIp = new(StringComparer.Ordinal);
        private static readonly object _perIpLock = new();

        /// <summary>Peer addresses currently tracked (for tests).</summary>
        internal static int TrackedPeerCount { get { lock (_perIpLock) return _perIp.Count; } }

        private static PeerSlot EnterSlot(string key)
        {
            lock (_perIpLock)
            {
                if (!_perIp.TryGetValue(key, out var slot))
                    _perIp[key] = slot = new PeerSlot();
                slot.Users++;
                return slot;
            }
        }

        private static void LeaveSlot(string key, PeerSlot slot)
        {
            lock (_perIpLock)
            {
                if (--slot.Users == 0 && _perIp.TryGetValue(key, out var current) && ReferenceEquals(current, slot))
                    _perIp.Remove(key);
            }
        }
        private static readonly SemaphoreSlim _global = new(MaxConcurrentGlobal, MaxConcurrentGlobal);

        /// <summary>
        /// Validates and clamps a requested height range. Returns null when the request is malformed
        /// (negative start, end before start, start past the tip).
        /// </summary>
        public static (long Start, long End)? ClampRange(long start, long end, long tip)
        {
            if (start < 0 || end < start || start > tip) return null;
            var clampedEnd = Math.Min(end, tip);
            clampedEnd = Math.Min(clampedEnd, start + MaxBlocksPerList - 1);
            return (start, clampedEnd);
        }

        /// <summary>Clamps a caller-supplied cumulative byte budget (SendBlockSpan) to [0, MaxListBytes].</summary>
        public static long ClampByteBudget(long requested) => Math.Clamp(requested, 0, MaxListBytes);

        /// <summary>
        /// Serializes blocks start..end (already clamped) as the same JSON array SendBlockList has always
        /// returned, stopping once <see cref="MaxListBytes"/> is reached (at least one block is always
        /// included so progress is guaranteed). Bytes are measured locally, never taken from Block.Size.
        /// </summary>
        public static (string Json, int Count) BuildListJson(long start, long end, Func<long, Block?> getBlock)
        {
            var sb = new StringBuilder("[");
            int count = 0;
            for (long h = start; h <= end; h++)
            {
                var block = getBlock(h);
                if (block == null) break;
                var json = JsonConvert.SerializeObject(block);
                if (count > 0 && sb.Length + json.Length + 1 > MaxListBytes) break;
                if (count > 0) sb.Append(',');
                sb.Append(json);
                count++;
                if (sb.Length >= MaxListBytes) break;
            }
            sb.Append(']');
            return (sb.ToString(), count);
        }

        /// <summary>
        /// Takes a per-IP and a global slot without waiting long. Returns a releaser, or null when the peer
        /// (or the node) is already serving its limit — the caller answers "nothing" and the peer retries.
        /// </summary>
        public static async Task<IDisposable?> TryEnterAsync(string ip, TimeSpan wait)
        {
            var key = ip ?? "";
            var slot = EnterSlot(key);
            if (!await slot.Semaphore.WaitAsync(wait)) { LeaveSlot(key, slot); return null; }
            if (!await _global.WaitAsync(wait)) { slot.Semaphore.Release(); LeaveSlot(key, slot); return null; }
            return new Releaser(key, slot);
        }

        private sealed class Releaser : IDisposable
        {
            private readonly string _key;
            private PeerSlot? _slot;
            public Releaser(string key, PeerSlot slot) { _key = key; _slot = slot; }
            public void Dispose()
            {
                var slot = Interlocked.Exchange(ref _slot, null);
                if (slot == null) return;
                _global.Release();
                slot.Semaphore.Release();
                LeaveSlot(_key, slot);
            }
        }
    }
}
