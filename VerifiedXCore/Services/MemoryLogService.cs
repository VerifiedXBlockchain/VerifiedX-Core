using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Text;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Services
{
    /// <summary>
    /// Once a minute, one line in its own log file saying where this node's memory is: the process, the garbage collector's
    /// heaps, the thread pool, how many peers are connected (and how many peer records are held), and how many entries the
    /// larger in-memory stores hold. It reads counts and collector figures only and serializes nothing, so it costs
    /// nothing to leave on. The file goes through the shared log writer, which caps a line and rotates the file.
    /// </summary>
    public static class MemoryLogService
    {
        public const string FileName = "memorylog.txt";
        public static readonly TimeSpan Period = TimeSpan.FromMinutes(1);

        public static async Task Run()
        {
            while (true)
            {
                try { LogUtility.LogQueue(Line(), "MemoryLog", FileName, true); }
                catch { /* a figure that cannot be read costs one line, not the loop */ }
                await Task.Delay(Period);
            }
        }

        private static string Mb(long bytes) => (bytes / 1048576.0).ToString("F1", CultureInfo.InvariantCulture);

        /// <summary>A count that cannot be read (a store not opened on this node) is written as -1.</summary>
        private static long Count(Func<long> read)
        {
            try { return read(); } catch { return -1; }
        }

        /// <summary>The line: name=value pairs, sizes in MB.</summary>
        internal static string Line()
        {
            var line = new StringBuilder(640);
            using (var process = Process.GetCurrentProcess())
                line.Append("workingSetMb=").Append(Mb(process.WorkingSet64)).Append(" privateMb=").Append(Mb(process.PrivateMemorySize64));

            var gc = GC.GetGCMemoryInfo();
            line.Append(" gcCommittedMb=").Append(Mb(gc.TotalCommittedBytes))
                .Append(" gcHeapMb=").Append(Mb(gc.HeapSizeBytes))
                .Append(" gcFragmentedMb=").Append(Mb(gc.FragmentedBytes));
            var generations = new[] { "gen0Mb", "gen1Mb", "gen2Mb", "lohMb", "pohMb" };
            for (var i = 0; i < generations.Length; i++)
                line.Append(" ").Append(generations[i]).Append("=").Append(Mb(i < gc.GenerationInfo.Length ? gc.GenerationInfo[i].SizeAfterBytes : 0));
            line.Append(" gcCounts=").Append(GC.CollectionCount(0)).Append("/").Append(GC.CollectionCount(1)).Append("/").Append(GC.CollectionCount(2))
                .Append(" gcPausePct=").Append(gc.PauseTimePercentage.ToString("F2", CultureInfo.InvariantCulture))
                .Append(" serverGc=").Append(GCSettings.IsServerGC ? 1 : 0)
                .Append(" cores=").Append(Environment.ProcessorCount);

            line.Append(" threads=").Append(ThreadPool.ThreadCount).Append(" queuedWork=").Append(ThreadPool.PendingWorkItemCount);

            // Connected now, and records held: a record can outlive its connection, and both matter (one is traffic, the
            // other is what is kept).
            line.Append(" validatorPeers=").Append(Count(() => Globals.ValidatorNodes.Values.Count(n => n.IsConnected)))
                .Append(" validatorPeerRecords=").Append(Count(() => Globals.ValidatorNodes.Count))
                .Append(" casterPeers=").Append(Count(() => Globals.BlockCasterNodes.Values.Count(n => n.IsConnected)))
                .Append(" casterPeerRecords=").Append(Count(() => Globals.BlockCasterNodes.Count))
                .Append(" peers=").Append(Count(() => Globals.Nodes.Values.Count(n => n.IsConnected)))
                .Append(" peerRecords=").Append(Count(() => Globals.Nodes.Count));

            line.Append(" craftedBlocks=").Append(Count(() => RequestBlockCache.EntryCount))
                .Append(" casterRounds=").Append(Count(() => Globals.CasterRoundDict.Count))
                .Append(" proofs=").Append(Count(() => Globals.Proofs.Count))
                .Append(" downloadBlocks=").Append(Count(() => BlockDownloadService.BlockDict.Count))
                .Append(" memBlocks=").Append(Count(() => Globals.MemBlocks.Count))
                .Append(" parentHashes=").Append(Count(() => VerifiedXCore.Privacy.ParentHashCache.Count))
                .Append(" outflows=").Append(Count(() => VerifiedXCore.Bitcoin.Services.VBTCService.OutflowsRemembered))
                .Append(" logQueue=").Append(Count(() => LogUtility.QueuedLines));
            return line.ToString();
        }
    }
}
