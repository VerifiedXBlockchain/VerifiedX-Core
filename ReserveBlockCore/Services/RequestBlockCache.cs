using System.Collections.Concurrent;
using System.Threading;
using ReserveBlockCore.Models;
using ReserveBlockCore.Utilities;

namespace ReserveBlockCore.Services
{
    /// <summary>Canonical crafted block for (height, winner) within TTL; shared across casters so M-of-N attest one hash.</summary>
    public static class RequestBlockCache
    {
        private static readonly ConcurrentDictionary<string, (Block Block, long ExpiryUnix)> Entries = new();
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> CraftLocks = new();

        private static string Key(long height, string winner) =>
            $"{height}|{winner}";

        /// <summary>Single-flight craft per (height, winner) so concurrent RequestBlock callers cannot produce two different hashes.</summary>
        internal static SemaphoreSlim GetCraftLock(long height, string winnerAddress) =>
            CraftLocks.GetOrAdd(Key(height, winnerAddress), _ => new SemaphoreSlim(1, 1));

        public static bool TryGet(long height, string winnerAddress, out Block? block)
        {
            block = null;
            var k = Key(height, winnerAddress);
            if (!Entries.TryGetValue(k, out var e))
                return false;
            if (TimeUtil.GetTime() > e.ExpiryUnix)
            {
                Entries.TryRemove(k, out _);
                return false;
            }

            block = e.Block;
            return true;
        }

        public static void Add(long height, string winnerAddress, Block block, int ttlSeconds = 60)
        {
            var k = Key(height, winnerAddress);
            Entries[k] = (block, TimeUtil.GetTime() + ttlSeconds);
        }
    }
}
