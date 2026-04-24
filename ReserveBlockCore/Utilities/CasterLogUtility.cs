using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ReserveBlockCore.Utilities
{
    /// <summary>
    /// Writes timestamped diagnostic logs to casterlog.txt for debugging consensus rounds.
    /// Thread-safe via a concurrent queue flushed periodically.
    /// </summary>
    public static class CasterLogUtility
    {
        private static readonly ConcurrentQueue<string> _queue = new();
        private static readonly object _flushLock = new();
        private static string? _cachedPath;
        private static readonly Stopwatch _uptime = Stopwatch.StartNew();

        /// <summary>Log a message with automatic timestamp (ms since startup) and caller tag.</summary>
        public static void Log(string message, string phase = "")
        {
            if (!Globals.CasterLogEnabled)
                return;

            var ts = _uptime.ElapsedMilliseconds;
            var tag = string.IsNullOrEmpty(phase) ? "" : $"[{phase}] ";
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] +{ts}ms {tag}{message}";
            _queue.Enqueue(line);

            // Auto-flush every 10 lines
            if (_queue.Count >= 10)
                Flush();
        }

        /// <summary>Flush all queued lines to disk.</summary>
        public static void Flush()
        {
            if (_queue.IsEmpty) return;

            lock (_flushLock)
            {
                try
                {
                    if (!Globals.CasterLogEnabled)
                    {
                        while (_queue.TryDequeue(out _)) { }
                        return;
                    }

                    var path = GetLogPath();
                    var lines = new List<string>();
                    while (_queue.TryDequeue(out var line))
                        lines.Add(line);

                    if (lines.Count > 0)
                        File.AppendAllLines(path, lines);
                }
                catch { /* best-effort */ }
            }
        }

        /// <summary>Clear the log file for a fresh run.</summary>
        public static void Clear()
        {
            if (!Globals.CasterLogEnabled)
                return;

            try
            {
                var path = GetLogPath();
                if (File.Exists(path))
                    File.WriteAllText(path, $"=== Caster Log Started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}");
            }
            catch { }
        }

        private static string GetLogPath()
        {
            if (_cachedPath != null) return _cachedPath;

            var databaseLocation = Globals.IsTestNet != true ? "Databases" : "DatabasesTestNet";
            var mainFolderPath = Globals.IsTestNet != true ? "RBX" : "RBXTest";

            string path;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                string homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                path = homeDirectory + Path.DirectorySeparatorChar + mainFolderPath.ToLower() + Path.DirectorySeparatorChar + databaseLocation + Path.DirectorySeparatorChar;
            }
            else
            {
                if (Debugger.IsAttached)
                {
                    path = Directory.GetCurrentDirectory() + Path.DirectorySeparatorChar + "DBs" + Path.DirectorySeparatorChar + databaseLocation + Path.DirectorySeparatorChar;
                }
                else
                {
                    path = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + Path.DirectorySeparatorChar + mainFolderPath + Path.DirectorySeparatorChar + databaseLocation + Path.DirectorySeparatorChar;
                }
            }

            if (!string.IsNullOrEmpty(Globals.CustomPath))
            {
                path = Globals.CustomPath + mainFolderPath + Path.DirectorySeparatorChar + databaseLocation + Path.DirectorySeparatorChar;
            }

            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);

            // Cap file size at 50MB — rotate if too large
            var fullPath = path + "casterlog.txt";
            try
            {
                if (File.Exists(fullPath) && new FileInfo(fullPath).Length > 50 * 1024 * 1024)
                {
                    var backup = path + "casterlog_prev.txt";
                    if (File.Exists(backup)) File.Delete(backup);
                    File.Move(fullPath, backup);
                }
            }
            catch { }

            _cachedPath = fullPath;
            return _cachedPath;
        }
    }
}