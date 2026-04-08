using System.Collections.Concurrent;
using ReserveBlockCore.Models;
using ReserveBlockCore.Utilities;

namespace ReserveBlockCore.Services
{
    /// <summary>§12.2 — same tuple within 60s returns cached block.</summary>
    public static class RequestBlockCache
    {
        private static readonly ConcurrentDictionary<string, (Block Block, long ExpiryUnix)> Entries = new();

        private static string Key(long height, string caster, string winner) =>
            $"{caster}|{height}|{winner}";

        public static bool TryGet(long height, string casterAddress, string winnerAddress, out Block? block)
        {
            block = null;
            var k = Key(height, casterAddress, winnerAddress);
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

        public static void Add(long height, string casterAddress, string winnerAddress, Block block, int ttlSeconds = 60)
        {
            var k = Key(height, casterAddress, winnerAddress);
            Entries[k] = (block, TimeUtil.GetTime() + ttlSeconds);
        }
    }
}
