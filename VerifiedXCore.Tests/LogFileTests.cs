using System.Text.RegularExpressions;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Every log file is written through one writer: a line keeps its first few thousand characters, and a file that
    /// reaches its size is moved aside and started again, so a log holds at most twice that on disk however much a
    /// sender makes the node log.
    /// </summary>
    public class LogFileTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), $"logfile_{Guid.NewGuid():N}");

        public LogFileTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

        [Fact]
        public async Task A_line_keeps_its_first_characters()
        {
            var path = Path.Combine(_dir, "rbxlog.txt");
            await LogFile.AppendAsync(path, Environment.NewLine + new string('A', 2 * 1024 * 1024));
            Assert.InRange(new FileInfo(path).Length, 1, LogUtility.MaxMessageChars + 1024);
        }

        [Fact]
        public async Task A_log_holds_at_most_twice_its_size_however_much_is_written()
        {
            var path = Path.Combine(_dir, "errorlog.txt");
            var line = Environment.NewLine + new string('B', LogUtility.MaxMessageChars);
            var lines = (int)(2.5 * LogFile.MaxFileBytes / line.Length);
            for (var i = 0; i < lines; i++) await LogFile.AppendAsync(path, line);

            Assert.True(File.Exists(path + ".1"));
            Assert.False(File.Exists(path + ".2"));
            Assert.True(new FileInfo(path).Length <= LogFile.MaxFileBytes + line.Length);
            Assert.True(new FileInfo(path + ".1").Length <= LogFile.MaxFileBytes + line.Length);
        }

        [Fact]
        public void Lines_written_together_are_each_cut()
        {
            var path = Path.Combine(_dir, "casterlog.txt");
            LogFile.AppendLines(path, new[] { "short", new string('C', 1_000_000), "end" });
            var written = File.ReadAllLines(path);
            Assert.Equal("short", written[0]);
            Assert.True(written[1].Length <= LogUtility.MaxMessageChars + 64);
            Assert.Equal("end", written[2]);
        }

        [Fact]
        public void No_log_utility_writes_or_measures_its_file_any_other_way()
        {
            var raw = new Regex(@"(?<![\w.])File\.(AppendAllText|AppendAllTextAsync|AppendAllLines|AppendAllLinesAsync|ReadAllBytes)\(");
            var offenders = new List<string>();
            foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "VerifiedXCore", "Utilities"), "*Log*Utility.cs"))
            {
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                    if (raw.IsMatch(lines[i]) && !lines[i].TrimStart().StartsWith("//"))
                        offenders.Add($"{Path.GetFileName(file)}:{i + 1}");
            }
            Assert.True(offenders.Count == 0, "Write logs through LogFile instead:\n  " + string.Join("\n  ", offenders));
        }

        private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "") =>
            Path.GetDirectoryName(Path.GetDirectoryName(thisFile))!;
    }
}
