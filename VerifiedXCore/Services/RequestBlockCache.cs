using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Services
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

        /// <summary>
        /// Drops craft locks for heights strictly behind the committed chain tip so <see cref="CraftLocks"/> cannot grow without bound.
        /// Does not <see cref="SemaphoreSlim.Dispose"/> removed instances: a slow in-flight <c>RequestBlock</c> may still hold a reference and call <c>Release</c>.
        /// </summary>
        internal static void PruneCraftLocksBehind(long committedHeight)
        {
            foreach (var key in CraftLocks.Keys.ToList())
            {
                var pipe = key.IndexOf('|');
                if (pipe <= 0)
                    continue;
                if (!long.TryParse(key.AsSpan(0, pipe), out var h))
                    continue;
                if (h < committedHeight)
                    CraftLocks.TryRemove(key, out _);
            }
        }

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
            PruneCraftLocksBehind(Globals.LastBlock.Height);
            PruneEntriesBehind(Globals.LastBlock.Height);
        }

        /// <summary>Heights behind the committed tip an entry is kept for: a caster still asking for the block just committed is answered.</summary>
        internal const long KeptBehindTip = 2;

        /// <summary>
        /// Drops the blocks of heights more than <see cref="KeptBehindTip"/> behind the committed tip, and any past their time.
        /// An entry's time was looked at only when the same height and winner were asked for again, which never happens once
        /// the chain has moved on, so a caster kept one whole block for every height it ever served.
        /// </summary>
        internal static void PruneEntriesBehind(long committedHeight)
        {
            var now = TimeUtil.GetTime();
            foreach (var entry in Entries.ToList())
            {
                var pipe = entry.Key.IndexOf('|');
                if (pipe <= 0 || !long.TryParse(entry.Key.AsSpan(0, pipe), out var h))
                    continue;
                if (h < committedHeight - KeptBehindTip || (h <= committedHeight && now > entry.Value.ExpiryUnix))
                    Entries.TryRemove(entry.Key, out _);
            }
        }

        internal static int EntryCount => Entries.Count;
    }
}
