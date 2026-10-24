using System.Text;

namespace VerifiedXCore.Services
{
    /// <summary>
    /// The time and height contract code is read at when a block is judged or applied (third pass, 14 October 2026).
    ///
    /// Every node reads a contract by running its code, and that code has two inputs from outside it: the built-in
    /// dateProc, which says whether a date has passed by the node's clock (date-evolving NFTs use it), and the chain
    /// height the decompiler hands an evolving contract. Read in block validation, both came from the node doing the
    /// reading: a contract whose royalty payee, minter or features change at a date gave one answer to the validators
    /// of the block and another to a node reading the same block a day later, which then refused a block the network
    /// had accepted and could not follow the chain past it.
    ///
    /// From Globals.PathAuditRulesHeight, while a block is judged, built or applied, both are the block's: dateProc
    /// answers from the block's timestamp and the height is the block before it. The interpreter is a package and has
    /// no clock to set, but a function a contract declares itself takes the place of a built-in of the same name; so
    /// the code is run with a dateProc declared in front of it that answers each date the code asks about as the
    /// block's timestamp decides. Outside a block (the wallet, the API) nothing is changed.
    /// </summary>
    public static class ContractClock
    {
        private static readonly AsyncLocal<(long Time, long Tip)?> Current = new();

        /// <summary>The height of the block being judged or applied, at any height (the clock above is set only from the rules' height).</summary>
        private static readonly AsyncLocal<long?> Judged = new();

        private sealed class Restore : IDisposable
        {
            private readonly (long, long)? _prior;
            private readonly long? _priorJudged;
            public Restore((long, long)? prior, long? priorJudged) { _prior = prior; _priorJudged = priorJudged; }
            public void Dispose() { Current.Value = _prior; Judged.Value = _priorJudged; }
        }

        /// <summary>
        /// Contract code read until the result is disposed is read as of this block: its timestamp, and the block
        /// before it as the chain's height (from the rules' height, and only when a timestamp is given). At every
        /// height the block's height is recorded (<see cref="BlockHeight"/>).
        /// </summary>
        public static IDisposable ForBlock(long? blockTimestamp, long blockHeight)
        {
            var prior = Current.Value;
            var priorJudged = Judged.Value;
            Judged.Value = blockHeight;
            if (blockTimestamp != null && blockHeight >= Globals.PathAuditRulesHeight)
                Current.Value = (blockTimestamp.Value, blockHeight - 1);
            return new Restore(prior, priorJudged);
        }

        /// <summary>
        /// The height of the block being validated or applied on this flow; null outside one (admission, API). Rules that
        /// change at a height read it, never the node's tip: a state rebuild at startup runs before the tip is loaded,
        /// and a block is applied after the tip has moved to it.
        /// </summary>
        public static long? BlockHeight => Judged.Value;

        /// <summary>Whether contract code is being read as of a block.</summary>
        public static bool IsSet => Current.Value != null;

        /// <summary>The chain height an evolving contract is handed: the block before the one being judged, else this node's tip.</summary>
        public static long TipHeight() => Current.Value?.Tip ?? Globals.LastBlock.Height;

        /// <summary>
        /// The code as it is run: unchanged outside a block; in a block, with a dateProc in front that answers from
        /// the block's timestamp. Unchanged too when the code declares its own dateProc (then that one is what runs,
        /// and it reads no clock), calls none, or does not parse (it then never runs).
        /// </summary>
        public static string Prepare(string source)
        {
            var at = Current.Value;
            if (at == null || string.IsNullOrEmpty(source) || !source.Contains("dateProc", StringComparison.Ordinal)) return source;
            try
            {
                var tree = global::Trillium.Syntax.SyntaxTree.Parse(source, preventLoopsAndRecursion: true);
                if (tree.Diagnostics.Length > 0) return source;

                var asked = new SortedSet<string>(StringComparer.Ordinal);
                var calls = false;
                var pending = new Stack<global::Trillium.Syntax.SyntaxNode>();
                pending.Push(tree.Root);
                while (pending.Count > 0)
                {
                    var node = pending.Pop();
                    if (node is global::Trillium.Syntax.FunctionDeclarationSyntax function && function.Identifier.Text == "dateProc")
                        return source;
                    if (node is global::Trillium.Syntax.CallExpressionSyntax call && call.Identifier.Text == "dateProc")
                    {
                        calls = true;
                        if (call.Arguments.Count == 1 && call.Arguments[0] is global::Trillium.Syntax.LiteralExpressionSyntax literal && literal.Value is string text)
                            asked.Add(text);
                    }
                    foreach (var child in node.GetChildren())
                        pending.Push(child);
                }
                if (!calls) return source;

                // The built-in says whether the date (a count of ticks) has passed. A date the code computes, or one
                // that is not a plain number, has not: the same answer on every node.
                var nowTicks = DateTimeOffset.FromUnixTimeSeconds(at.Value.Time).UtcDateTime.Ticks;
                var shadow = new StringBuilder();
                shadow.AppendLine("function dateProc(d : string) : bool");
                shadow.AppendLine("{");
                foreach (var date in asked)
                {
                    if (date.Length == 0 || date.Length > 19 || !date.All(c => c >= '0' && c <= '9')) continue;
                    if (!long.TryParse(date, out var ticks) || ticks > nowTicks) continue;
                    shadow.AppendLine("   if(d == \"" + date + "\")");
                    shadow.AppendLine("   {");
                    shadow.AppendLine("      return true");
                    shadow.AppendLine("   }");
                }
                shadow.AppendLine("   return false");
                shadow.AppendLine("}");
                return shadow.ToString() + source;
            }
            catch
            {
                return source;
            }
        }
    }
}
