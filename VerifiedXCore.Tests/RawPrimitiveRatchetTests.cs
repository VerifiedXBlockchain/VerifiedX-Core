using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// The raw primitives that have let a peer's or a caller's input cost a node more than it should, or bypass a rule, have
    /// one bounded version each, in one file. Their raw use in the node is held to <see cref="RawPrimitiveBaseline"/>, a
    /// list that can only shrink: a file that uses one more time than it does today, or a file that starts using one, fails
    /// here with the bounded version to use instead. A file that uses one less must lower its entry, so a removed use
    /// cannot quietly come back.
    /// Regenerate the list after removing uses: set VFX_WRITE_RAW_BASELINE=1 and run this test.
    /// </summary>
    public class RawPrimitiveRatchetTests
    {
        public sealed record Primitive(string Name, string Pattern, string UseInstead, params string[] Homes);

        /// <summary>Each raw primitive, the bounded version to use, and the files where the bounded version lives.</summary>
        public static readonly Primitive[] Primitives =
        {
            new("json-parse", @"\b(JObject|JArray|JToken)\.Parse\(|\bJsonConvert\.DeserializeObject\b",
                "BoundedJson.Parse for a peer's or a caller's text, ConsensusData for a transaction's Data",
                "Utilities/BoundedJson.cs", "Utilities/ConsensusData.cs"),
            new("base58", @"\bBase58Decode\(", "BoundedDecode.Base58", "Utilities/BoundedDecode.cs", "Utilities/Base58Utility.cs"),
            new("base64", @"\bConvert\.FromBase64String\(", "BoundedDecode.Base64", "Utilities/BoundedDecode.cs"),
            new("hex", @"\bConvert\.FromHexString\(|\bHexToByte\(", "BoundedDecode.Hex", "Utilities/BoundedDecode.cs", "Utilities/HexByteUtility.cs"),
            new("bitcoin-address", @"\bBitcoinAddress\.Create\(", "BoundedDecode.BitcoinAddress", "Utilities/BoundedDecode.cs"),
            new("body-read", @"\bRequest\.Body\b", "BoundedJsonBodies.ReadBody", "Utilities/BoundedJson.cs"),
            new("log-write", @"\bFile\.AppendAllText(Async)?\(", "LogFile.Append", "Utilities/LogFile.cs"),
            new("async-void", @"\basync\s+void\b", "async Task (an exception in async void ends the process)"),
        };

        private static string Root([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "") =>
            Path.GetDirectoryName(Path.GetDirectoryName(thisFile))!;

        /// <summary>Uses of each primitive in each production file, keyed "path|primitive", homes left out.</summary>
        internal static SortedDictionary<string, int> Count()
        {
            var root = Path.Combine(Root(), "VerifiedXCore");
            var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var sep = Path.DirectorySeparatorChar;
            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(sep + "bin" + sep) || file.Contains(sep + "obj" + sep)) continue;
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                var text = File.ReadAllText(file);
                foreach (var p in Primitives)
                {
                    if (p.Homes.Contains(relative)) continue;
                    var n = Regex.Matches(text, p.Pattern).Count;
                    if (n > 0) counts[$"{relative}|{p.Name}"] = n;
                }
            }
            return counts;
        }

        [Fact]
        public void No_file_uses_a_raw_primitive_more_than_the_list_allows()
        {
            var now = Count();
            if (Environment.GetEnvironmentVariable("VFX_WRITE_RAW_BASELINE") == "1")
            {
                var sb = new StringBuilder();
                sb.AppendLine("using System.Collections.Generic;");
                sb.AppendLine();
                sb.AppendLine("namespace VerifiedXCore.Tests");
                sb.AppendLine("{");
                sb.AppendLine("    /// <summary>Raw primitive uses allowed per file (RawPrimitiveRatchetTests). Only ever lowered.</summary>");
                sb.AppendLine("    public static class RawPrimitiveBaseline");
                sb.AppendLine("    {");
                sb.AppendLine("        public static readonly Dictionary<string, int> Allowed = new()");
                sb.AppendLine("        {");
                foreach (var (k, v) in now) sb.AppendLine($"            [\"{k}\"] = {v},");
                sb.AppendLine("        };");
                sb.AppendLine("    }");
                sb.AppendLine("}");
                File.WriteAllText(Path.Combine(Root(), "VerifiedXCore.Tests", "RawPrimitiveBaseline.cs"), sb.ToString());
                return;
            }

            var problems = new List<string>();
            foreach (var (key, n) in now)
            {
                var allowed = RawPrimitiveBaseline.Allowed.TryGetValue(key, out var a) ? a : 0;
                if (n > allowed)
                {
                    var p = Primitives.First(x => key.EndsWith("|" + x.Name, StringComparison.Ordinal));
                    problems.Add($"{key}: {n} uses, {allowed} allowed. Use {p.UseInstead}.");
                }
            }
            foreach (var (key, allowed) in RawPrimitiveBaseline.Allowed)
            {
                var n = now.TryGetValue(key, out var c) ? c : 0;
                if (n < allowed) problems.Add($"{key}: {n} uses, the list still allows {allowed}. Lower the entry (VFX_WRITE_RAW_BASELINE=1).");
            }
            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        }

        [Fact]
        public void Every_primitive_has_its_bounded_home()
        {
            var root = Path.Combine(Root(), "VerifiedXCore");
            foreach (var p in Primitives)
                foreach (var home in p.Homes)
                    Assert.True(File.Exists(Path.Combine(root, home)), $"{p.Name}: {home} is missing");
        }
    }
}
