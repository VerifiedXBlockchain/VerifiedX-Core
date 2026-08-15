using System.Collections.Concurrent;

namespace VerifiedXCore.Services
{
    /// <summary>
    /// In-memory diagnostic rings for remote debugging (no SSH needed). Records every
    /// DbContext.Rollback (the tag names the failing validation gate, e.g.
    /// "BlockValidatorService.ValidateBlock()-cert") and every block rejection printed by
    /// ValidateBlocks, exposed read-only via valapi GetRejectionLog. Lock-free; the cap is
    /// approximate under concurrency, which is fine for a diagnostic ring.
    /// </summary>
    public static class BlockDiagnostics
    {
        public const int MaxEntries = 200;

        public class RollbackEntry
        {
            public string Location { get; set; } = "";
            public string Message { get; set; } = "";
            public DateTime TimeUtc { get; set; }
        }

        public class BlockRejectionEntry
        {
            public long Height { get; set; }
            public string Validator { get; set; } = "";
            public string Hash { get; set; } = "";
            public string LastRollbackTag { get; set; } = "";
            public DateTime TimeUtc { get; set; }
        }

        private static readonly ConcurrentQueue<RollbackEntry> _rollbacks = new();
        private static readonly ConcurrentQueue<BlockRejectionEntry> _rejections = new();

        public static void RecordRollback(string location, string message)
        {
            _rollbacks.Enqueue(new RollbackEntry
            {
                Location = location ?? "",
                Message = message ?? "",
                TimeUtc = DateTime.UtcNow
            });
            while (_rollbacks.Count > MaxEntries)
                _rollbacks.TryDequeue(out _);
        }

        public static void RecordBlockRejection(long height, string validator, string hash)
        {
            _rejections.Enqueue(new BlockRejectionEntry
            {
                Height = height,
                Validator = validator ?? "",
                Hash = string.IsNullOrEmpty(hash) ? "" : hash[..Math.Min(16, hash.Length)],
                LastRollbackTag = MostRecentRollbackTag(),
                TimeUtc = DateTime.UtcNow
            });
            while (_rejections.Count > MaxEntries)
                _rejections.TryDequeue(out _);
        }

        /// <summary>Tag of the most recent rollback within the last few seconds — the rollback
        /// that caused a rejection fires immediately before the rejection is printed, so this
        /// reliably names the failing gate. Empty when nothing recent.</summary>
        public static string MostRecentRollbackTag(int withinSeconds = 5)
        {
            var last = _rollbacks.LastOrDefault();
            if (last == null || (DateTime.UtcNow - last.TimeUtc).TotalSeconds > withinSeconds)
                return "";
            return last.Location;
        }

        /// <summary>Newest-first snapshot copies for the diagnostic endpoint.</summary>
        public static List<RollbackEntry> RollbackSnapshot() =>
            _rollbacks.Reverse().ToList();

        public static List<BlockRejectionEntry> RejectionSnapshot() =>
            _rejections.Reverse().ToList();

        /// <summary>Test hook.</summary>
        public static void Clear()
        {
            while (_rollbacks.TryDequeue(out _)) { }
            while (_rejections.TryDequeue(out _)) { }
        }
    }
}
