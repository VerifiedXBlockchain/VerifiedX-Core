using System.Linq;
using VerifiedXCore.Models;
using VerifiedXCore.Nodes;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A caster's per-height caches drop what is behind the committed tip. The crafted-block cache looked at an entry's
    /// time only when the same height and winner were asked for again, and a gossip pin was taken back only for its own
    /// height, so both kept one entry (the first a whole block) for every height the caster ever served.
    /// </summary>
    [Collection("DbContextSequential")]
    public class CasterCachePruneTests
    {
        [Fact]
        public void The_crafted_block_cache_keeps_only_heights_near_the_tip()
        {
            var prior = Globals.LastBlock;
            try
            {
                const long start = 9_100_000;
                for (var h = start; h < start + 500; h++)
                {
                    Globals.LastBlock = new Block { Height = h - 1 };                 // the tip while height h is being crafted
                    RequestBlockCache.Add(h, "xWinner", new Block { Height = h, Hash = $"hash-{h}" });
                }
                Assert.True(RequestBlockCache.EntryCount <= RequestBlockCache.KeptBehindTip + 2, $"{RequestBlockCache.EntryCount} entries kept after 500 heights");
                // The height being crafted and the one just committed are still served.
                Assert.True(RequestBlockCache.TryGet(start + 499, "xWinner", out var newest));
                Assert.Equal($"hash-{start + 499}", newest!.Hash);
                Assert.True(RequestBlockCache.TryGet(start + 498, "xWinner", out _));
                Assert.False(RequestBlockCache.TryGet(start, "xWinner", out _));

                // An entry past its time at or behind the tip goes at the next add, whoever asks for it or not.
                Globals.LastBlock = new Block { Height = start + 499 };
                RequestBlockCache.Add(start + 499, "xLate", new Block { Height = start + 499, Hash = "late" }, ttlSeconds: -1);
                RequestBlockCache.Add(start + 500, "xWinner", new Block { Height = start + 500, Hash = "next" });
                Assert.False(RequestBlockCache.TryGet(start + 499, "xLate", out _));
            }
            finally
            {
                Globals.LastBlock = new Block { Height = long.MaxValue - 10 };
                RequestBlockCache.PruneEntriesBehind(long.MaxValue - 10);
                Globals.LastBlock = prior;
            }
        }

        [Fact]
        public void Gossip_pins_behind_the_newest_are_dropped()
        {
            const long start = 9_200_000;
            try
            {
                for (var h = start; h < start + 300; h++)
                    BlockcasterNode.RegisterGossipPin(h, $"hash-{h}");
                Assert.True(BlockcasterNode.GossipPinCount <= RequestBlockCache.KeptBehindTip + 1, $"{BlockcasterNode.GossipPinCount} pins kept after 300 heights");
            }
            finally
            {
                foreach (var h in Enumerable.Range(0, 300).Select(i => start + i))
                    Globals.CasterApprovedBlockHashDict.TryRemove(h, out _);
            }
        }
    }
}
