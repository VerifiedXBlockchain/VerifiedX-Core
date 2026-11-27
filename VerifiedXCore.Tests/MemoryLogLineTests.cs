using System.Globalization;
using System.Linq;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// The once-a-minute memory line names the process, the collector's heaps, the thread pool, the peers and the larger
    /// in-memory stores, in name=value pairs with numbers only, and fits in one log line.
    /// </summary>
    public class MemoryLogLineTests
    {
        [Fact]
        public void The_line_carries_every_figure_as_a_number_and_fits_one_log_line()
        {
            var line = MemoryLogService.Line();
            var pairs = line.Split(" ").Select(p => p.Split("=")).ToList();
            Assert.All(pairs, p => Assert.Equal(2, p.Length));
            var names = pairs.Select(p => p[0]).ToList();
            foreach (var name in new[]
            {
                "workingSetMb", "privateMb", "gcCommittedMb", "gcHeapMb", "gcFragmentedMb", "gen0Mb", "gen1Mb", "gen2Mb", "lohMb", "pohMb",
                "gcCounts", "gcPausePct", "serverGc", "cores", "threads", "queuedWork", "validatorPeers", "casterPeers", "peers",
                "craftedBlocks", "casterRounds", "proofs", "downloadBlocks", "memBlocks", "parentHashes", "outflows", "logQueue",
            })
                Assert.Contains(name, names);
            Assert.Equal(names.Count, names.Distinct().Count());
            Assert.All(pairs.Where(p => p[0] != "gcCounts"),
                p => Assert.True(double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out _), $"{p[0]}={p[1]}"));
            Assert.True(double.Parse(pairs.Single(p => p[0] == "workingSetMb")[1], CultureInfo.InvariantCulture) > 1);
            Assert.True(line.Length < LogUtility.MaxMessageChars, $"{line.Length} characters");
        }
    }
}
