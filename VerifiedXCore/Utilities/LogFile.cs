using System.Collections.Concurrent;

namespace VerifiedXCore.Utilities
{
    /// <summary>
    /// The one way the node's log utilities write a log file. A line keeps its first
    /// <see cref="LogUtility.MaxMessageChars"/> characters, and a file that reaches <see cref="MaxFileBytes"/> is moved
    /// aside to <c>name.1</c> (replacing the one before it) and started again, so each log holds at most twice that on
    /// disk however much is logged. Several routes log text a sender chose; with no bound a sender filled a validator's
    /// disk, and once the disk was full the node could no longer write its blocks, state or key shares.
    /// </summary>
    public static class LogFile
    {
        /// <summary>The size at which a log file is moved aside and started again.</summary>
        public const long MaxFileBytes = 20L * 1024 * 1024;

        private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Appends <paramref name="text"/> to the log at <paramref name="path"/>.</summary>
        public static async Task AppendAsync(string path, string text)
        {
            var gate = Gates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync();
            try
            {
                RotateIfFull(path);
                await File.AppendAllTextAsync(path, Bounded(text));
            }
            finally { gate.Release(); }
        }

        /// <summary>Appends <paramref name="text"/> to the log at <paramref name="path"/>, waiting for it.</summary>
        public static void Append(string path, string text)
        {
            var gate = Gates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
            gate.Wait();
            try
            {
                RotateIfFull(path);
                File.AppendAllText(path, Bounded(text));
            }
            finally { gate.Release(); }
        }

        /// <summary>Appends each of <paramref name="lines"/>, each followed by a line break.</summary>
        public static void AppendLines(string path, IEnumerable<string> lines) =>
            Append(path, string.Concat(lines.Select(l => Bounded(l) + Environment.NewLine)));

        /// <summary>The size of the log at <paramref name="path"/>, read from the file system and not from its contents.</summary>
        public static long Length(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

        /// <summary>
        /// A text of at most a line's length, or of a batch of such lines: what is written at once never exceeds the
        /// file's own bound.
        /// </summary>
        private static string Bounded(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var limit = (int)Math.Min(MaxFileBytes / 4, int.MaxValue);
            if (text.Length <= LogUtility.MaxMessageChars + 512) return text;
            if (!text.Contains('\n')) return LogUtility.Shorten(text);
            // Several lines written at once: each line is cut, and the batch as a whole is held to a quarter of a file.
            var cut = string.Join("\n", text.Split('\n').Select(l => l.Length > LogUtility.MaxMessageChars + 512 ? LogUtility.Shorten(l) : l));
            return cut.Length <= limit ? cut : cut.Substring(0, limit);
        }

        private static void RotateIfFull(string path)
        {
            try
            {
                if (Length(path) < MaxFileBytes) return;
                File.Move(path, path + ".1", overwrite: true);
            }
            catch (IOException) { /* moved by another process, or held open: written to as it is */ }
            catch (UnauthorizedAccessException) { }
        }
    }
}
