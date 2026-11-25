using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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
        /// <summary>
        /// A raw primitive: calls named <paramref name="Calls"/> ("Receiver.Method", or "*.Method" for any receiver), and
        /// for body reads and async void the syntax they have. Matched on the parsed code, so comments, strings and spacing
        /// do not count or hide a use.
        /// </summary>
        public sealed record Primitive(string Name, string[] Calls, string UseInstead, params string[] Homes);

        /// <summary>Each raw primitive, the bounded version to use, and the files where the bounded version lives.</summary>
        public static readonly Primitive[] Primitives =
        {
            new("json-parse", new[] { "JObject.Parse", "JArray.Parse", "JToken.Parse", "JsonConvert.DeserializeObject" },
                "BoundedJson.Parse for a peer's or a caller's text, ConsensusData for a transaction's Data",
                "Utilities/BoundedJson.cs", "Utilities/ConsensusData.cs"),
            new("base58", new[] { "*.Base58Decode" }, "BoundedDecode.Base58", "Utilities/BoundedDecode.cs", "Utilities/Base58Utility.cs"),
            new("base64", new[] { "Convert.FromBase64String" }, "BoundedDecode.Base64", "Utilities/BoundedDecode.cs"),
            new("hex", new[] { "Convert.FromHexString", "*.HexToByte" }, "BoundedDecode.Hex", "Utilities/BoundedDecode.cs", "Utilities/HexByteUtility.cs"),
            new("bitcoin-address", new[] { "BitcoinAddress.Create" }, "BoundedDecode.BitcoinAddress", "Utilities/BoundedDecode.cs"),
            new("body-read", new[] { "Request.Body" }, "BoundedJsonBodies.ReadBody", "Utilities/BoundedJson.cs"),
            new("log-write", new[] { "File.AppendAllText", "File.AppendAllTextAsync" }, "LogFile.Append", "Utilities/LogFile.cs"),
            new("async-void", new[] { "async void" }, "async Task (an exception in async void ends the process)"),
        };

        private static string Root([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "") =>
            Path.GetDirectoryName(Path.GetDirectoryName(thisFile))!;

        /// <summary>The last identifier of an expression: "JsonConvert" for Newtonsoft.Json.JsonConvert.</summary>
        private static string LastName(ExpressionSyntax e) => e switch
        {
            MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
            SimpleNameSyntax n => n.Identifier.Text,
            _ => "",
        };

        /// <summary>What a node is, as the primitives name it: "Receiver.Method" or "*.Method" for a call, "Request.Body", "async void".</summary>
        private static IEnumerable<string> Names(SyntaxNode node)
        {
            switch (node)
            {
                case InvocationExpressionSyntax call:
                    var (receiver, method) = call.Expression switch
                    {
                        MemberAccessExpressionSyntax m => (LastName(m.Expression), m.Name.Identifier.Text),
                        SimpleNameSyntax n => ("", n.Identifier.Text),
                        _ => ("", ""),
                    };
                    if (method.Length > 0) { yield return $"{receiver}.{method}"; yield return $"*.{method}"; }
                    break;
                case MemberAccessExpressionSyntax access when access.Name.Identifier.Text == "Body" && LastName(access.Expression) == "Request":
                    yield return "Request.Body";
                    break;
                case MethodDeclarationSyntax declared when declared.Modifiers.Any(SyntaxKind.AsyncKeyword) && declared.ReturnType is PredefinedTypeSyntax { Keyword.Text: "void" }:
                    yield return "async void";
                    break;
                case LocalFunctionStatementSyntax local when local.Modifiers.Any(SyntaxKind.AsyncKeyword) && local.ReturnType is PredefinedTypeSyntax { Keyword.Text: "void" }:
                    yield return "async void";
                    break;
            }
        }

        private static readonly string[] Net6Symbols =
            { "NET", "NET6_0", "NET6_0_OR_GREATER", "NET5_0_OR_GREATER", "NETCOREAPP", "NETCOREAPP3_1_OR_GREATER", "NETCOREAPP3_0_OR_GREATER",
              "NETCOREAPP2_1_OR_GREATER", "NETCOREAPP2_0_OR_GREATER", "NETCOREAPP1_0_OR_GREATER", "TRACE" };

        /// <summary>The preprocessor symbols of each build of the node.</summary>
        private static readonly string[][] BuildSymbols = { Net6Symbols.Append("DEBUG").ToArray(), Net6Symbols.Append("RELEASE").ToArray() };

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
                // As the node is built (net6.0, Debug and Release): code under #if counts as it is compiled.
                var builds = BuildSymbols.Select(symbols => CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(preprocessorSymbols: symbols))
                    .GetRoot().DescendantNodes().SelectMany(Names).ToList()).ToList();
                foreach (var p in Primitives)
                {
                    if (p.Homes.Contains(relative)) continue;
                    var n = builds.Max(names => names.Count(name => p.Calls.Contains(name)));
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
        public void A_use_is_found_however_it_is_spaced_and_never_in_a_comment_or_a_string()
        {
            var code = "class C { async void A() { var b = Convert.FromBase64String (t); var j = Newtonsoft.Json.JsonConvert.DeserializeObject<int>(t); /* JObject.Parse(x) */ var s = \"JToken.Parse(y)\"; var r = HttpContext.Request.Body; } }";
            var names = CSharpSyntaxTree.ParseText(code).GetRoot().DescendantNodes().SelectMany(Names).ToList();
            Assert.Contains("Convert.FromBase64String", names);
            Assert.Contains("JsonConvert.DeserializeObject", names);
            Assert.Contains("Request.Body", names);
            Assert.Contains("async void", names);
            Assert.DoesNotContain("JObject.Parse", names);
            Assert.DoesNotContain("JToken.Parse", names);
        }

        [Fact]
        public void A_use_under_the_builds_symbols_counts()
        {
            var code = "class C { void A() {
#if NET6_0_OR_GREATER
 var b = Convert.FromHexString(t);
#endif
#if RELEASE
 var j = JObject.Parse(t);
#endif
 } }";
            var seen = BuildSymbols.SelectMany(symbols => CSharpSyntaxTree.ParseText(code, new CSharpParseOptions(preprocessorSymbols: symbols))
                .GetRoot().DescendantNodes().SelectMany(Names)).ToList();
            Assert.Contains("Convert.FromHexString", seen); // compiled into every build of the node
            Assert.Contains("JObject.Parse", seen);         // compiled into the Release build
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
