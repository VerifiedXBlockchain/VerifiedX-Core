using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VerifiedXCore.Bitcoin.Attestation;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Every transaction this node builds writes its Amount and Fee in the one spelling accepted from
    /// Globals.CanonicalInputHeight, so the node's own transactions keep passing on a network where the height is active.
    /// The spelling is accepted at every height, so the builders write it whatever the height.
    /// </summary>
    public class CanonicalAmountFeeBuildersTests
    {
        private static decimal Dec(string text) => decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);

        [Theory]
        [InlineData("1", "1.0")]
        [InlineData("1.00", "1.0")]
        [InlineData("1.50", "1.5")]
        [InlineData("0", "0.0")]
        [InlineData("0.00000600", "0.000006")]
        [InlineData("2.5", "2.5")]
        public void The_normalizing_helper_the_builders_use_writes_the_canonical_spelling(string text, string expected)
        {
            var normalized = Dec(text).ToNormalizeDecimal();

            Assert.Equal(expected, normalized.ToString(CultureInfo.InvariantCulture));
            Assert.True(LedgerIntegrityRules.IsCanonicalDecimal(normalized));
        }

        /// <summary>
        /// The raw transaction routes (TXV1Controller GetTxHash, VerifyRawTransaction, SendRawTransaction) read the body
        /// through JToken.Parse, whose numbers are doubles, and then normalize the Amount. A double is written back in its
        /// shortest form, so what reaches validation is the canonical spelling of the number the client sent.
        /// </summary>
        [Theory]
        [InlineData("1.5", "0.00002", "1.5", "0.00002")]
        [InlineData("1", "0.000006", "1.0", "0.000006")]
        [InlineData("1.0", "0.0000060", "1.0", "0.000006")]
        [InlineData("1.50", "0.00000600", "1.5", "0.000006")]
        [InlineData("0", "0.0", "0.0", "0.0")]
        [InlineData("123.456", "0.00001234", "123.456", "0.00001234")]
        [InlineData("1E0", "6E-6", "1.0", "0.000006")]
        public void The_raw_routes_hand_validation_the_canonical_spelling(string amount, string fee, string amountRead, string feeRead)
        {
            var body = $"{{\"Amount\":{amount},\"Fee\":{fee},\"FromAddress\":\"a\",\"ToAddress\":\"b\",\"TransactionType\":0}}";

            // As TXV1Controller reads a raw transaction.
            var tx = JsonConvert.DeserializeObject<Transaction>(JToken.Parse(body).ToString())!;
            tx.Amount = tx.Amount.ToNormalizeDecimal();

            Assert.Equal(amountRead, tx.Amount.ToString(CultureInfo.InvariantCulture));
            Assert.Equal(feeRead, tx.Fee.ToString(CultureInfo.InvariantCulture));
            Assert.True(LedgerIntegrityRules.IsCanonicalDecimal(tx.Amount));
            Assert.True(LedgerIntegrityRules.IsCanonicalDecimal(tx.Fee));
        }

        /// <summary>
        /// The raw routes normalize only the Amount: a Fee sent as a JSON integer (a fee-free transaction's "Fee": 0) reads
        /// with no decimal places and is refused from the height. Clients send 0.0.
        /// </summary>
        [Fact]
        public void The_raw_routes_keep_a_fee_sent_as_an_integer_as_it_was_sent()
        {
            var tx = JsonConvert.DeserializeObject<Transaction>(JToken.Parse("{\"Amount\":0,\"Fee\":0}").ToString())!;
            tx.Amount = tx.Amount.ToNormalizeDecimal();

            Assert.Equal("0.0", tx.Amount.ToString(CultureInfo.InvariantCulture));
            Assert.Equal("0", tx.Fee.ToString(CultureInfo.InvariantCulture));
            Assert.False(LedgerIntegrityRules.IsCanonicalDecimal(tx.Fee));
        }

        [Fact]
        public void An_attestation_transaction_is_built_canonical()
        {
            var tx = BtcAttestTxValidator.Create(TransactionType.VBTC_V2_BTC_REPORT, "{}", 1_790_000_000);

            Assert.True(LedgerIntegrityRules.IsCanonicalDecimal(tx.Amount));
            Assert.True(LedgerIntegrityRules.IsCanonicalDecimal(tx.Fee));
        }

        // ── Every place the node builds a transaction ────────────────────────────────────────────────────────────

        /// <summary>
        /// Places that construct a Transaction from another one (verification re-hashes, copies, display rows) or that
        /// write history (the genesis balances). They carry the values they are given and build nothing new.
        /// </summary>
        private static readonly HashSet<string> Copies = new(StringComparer.Ordinal)
        {
            "TransactionValidatorService.cs: txRequest.Amount", "TransactionValidatorService.cs: txRequest.Fee",
            "TransactionValidatorService.cs: amountFormat", "TransactionValidatorService.cs: 0",
            "TransactionValidatorService.cs: tx.Amount", "TransactionValidatorService.cs: tx.Fee",
            "PrivateTransactionValidatorService.cs: txRequest.Amount", "PrivateTransactionValidatorService.cs: txRequest.Fee",
            "ForkCorrectionService.cs: amount", "ForkCorrectionService.cs: fee",
            "TransactionData.cs: balance", "TransactionData.cs: 0",
            "TransactionData.cs: transaction.Amount", "TransactionData.cs: transaction.Fee",
            "TransactionData.cs: (tx.Amount * -1M)", "TransactionData.cs: (tx.Fee * -1M)",
            "BlockTransactionValidatorService.cs: tx.Amount * -1M", "BlockTransactionValidatorService.cs: tx.Fee * -1M",
            "WalletService.cs: txRequest.Amount", "WalletService.cs: txRequest.Fee",
            "PrivacyApiHelper.cs: onChainTx.Fee",
        };

        private static readonly Regex Construction = new(@"(?:(?<var>\w+)\s*=\s*)?new\s+(?:Models\.)?Transaction\s*(?:\(\s*\))?\s*\{", RegexOptions.Compiled);

        /// <summary>
        /// Reads every <c>new Transaction { ... }</c> in Core and the Amount and Fee it is hashed with: its initializer's,
        /// or the last assignment to that variable's field before the transaction is built (Build or BuildPrivate). Each
        /// is canonical by construction: a decimal literal written canonically, a Globals constant whose value is
        /// canonical, the fee calculator, or the normalizing helpers.
        /// </summary>
        [Fact]
        public void Every_transaction_core_builds_writes_amount_and_fee_canonically()
        {
            var root = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(ThisFile()))!, "VerifiedXCore");
            var sep = Path.DirectorySeparatorChar;
            var offenders = new List<string>();
            var builders = 0;
            foreach (var file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains(sep + "bin" + sep) && !f.Contains(sep + "obj" + sep)))
            {
                offenders.AddRange(Offenders(Path.GetFileName(file), File.ReadAllText(file), out var found));
                builders += found;
            }

            Assert.True(builders > 50, $"the scan found only {builders} transaction builders");
            Assert.True(offenders.Count == 0, "Transactions built with an Amount or Fee that may not be canonical: " + string.Join("; ", offenders));
        }

        [Fact]
        public void The_scan_judges_the_spelling_a_transaction_is_hashed_with()
        {
            // Normalized only after it is built (and signed): hashed with the amount as given.
            const string normalizedAfterBuild = @"
                var nTx = new Transaction { Amount = Amount, Fee = 0 };
                nTx.Fee = FeeCalcService.CalculateTXFee(nTx);
                nTx.Build();
                nTx.Amount = nTx.Amount.ToNormalizeDecimal();";
            // Written canonically before it is built.
            const string canonicalBeforeBuild = @"
                var nTx = new Transaction { Amount = LedgerIntegrityRules.CanonicalDecimal(Amount), Fee = 0 };
                nTx.Fee = FeeCalcService.CalculateTXFee(nTx);
                nTx.Build();
                nTx.Amount = nTx.Amount.ToNormalizeDecimal();";

            Assert.Equal(new[] { "Example.cs:2 Amount = Amount" }, Offenders("Example.cs", normalizedAfterBuild, out _));
            Assert.Empty(Offenders("Example.cs", canonicalBeforeBuild, out _));
        }

        private static bool IsCoinbase(string file, string initializer) =>
            file == "BlockchainData.cs" && Regex.IsMatch(initializer, @"FromAddress\s*=\s*""Coinbase_(BlkRwd|TrxFees)""");

        /// <summary>
        /// The block producer's coinbases (CraftNewBlock_New, CraftBlock_V4, CraftBlock_V5, CraftSpecialBlock) keep the
        /// spelling they have always had: a Fee of 0.00M, and an Amount of the block reward, 0.00M, or the special
        /// block's balance. Coinbases are not judged by the canonical rule, which leaves them as they were.
        /// </summary>
        [Fact]
        public void CoinbasesKeepTheirHistoricalSpelling()
        {
            var file = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(ThisFile()))!, "VerifiedXCore", "Data", "BlockchainData.cs");
            var text = File.ReadAllText(file);
            var coinbases = new List<Dictionary<string, string>>();
            foreach (Match m in Construction.Matches(text))
            {
                var bodyStart = m.Index + m.Length;
                var body = text[bodyStart..ClosingBrace(text, bodyStart)];
                if (!IsCoinbase("BlockchainData.cs", body)) continue;
                var fields = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var member in TopLevelMembers(body))
                {
                    var eq = Regex.Match(member, @"^\s*(Amount|Fee)\s*=\s*(.+?)\s*$", RegexOptions.Singleline);
                    if (eq.Success) fields[eq.Groups[1].Value] = Collapse(eq.Groups[2].Value);
                }
                coinbases.Add(fields);
            }

            Assert.Equal(5, coinbases.Count);
            Assert.All(coinbases, c => Assert.Equal("0.00M", c["Fee"]));
            Assert.All(coinbases, c => Assert.Contains(c["Amount"], new[] { "GetBlockReward()", "0.00M", "postMineBalance" }));
            Assert.Equal(2, ((decimal.GetBits(0.00M)[3] >> 16) & 0xFF)); // 0.00M carries two places
        }

        private static List<string> Offenders(string name, string text, out int builders)
        {
            var offenders = new List<string>();
            builders = 0;
            foreach (Match m in Construction.Matches(text))
            {
                var bodyStart = m.Index + m.Length;
                var bodyEnd = ClosingBrace(text, bodyStart);
                if (bodyEnd < 0) continue;
                var body = text[bodyStart..bodyEnd];
                // Coinbases are exempt from the canonical rule and keep their historical spelling (0.00M), as the
                // block producer in BlockchainData has always written them; CoinbasesKeepTheirHistoricalSpelling holds it.
                if (IsCoinbase(name, body)) continue;
                var fields = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var member in TopLevelMembers(body))
                {
                    var eq = Regex.Match(member, @"^\s*(Amount|Fee)\s*=\s*(.+?)\s*$", RegexOptions.Singleline);
                    if (eq.Success) fields[eq.Groups[1].Value] = Collapse(eq.Groups[2].Value);
                }
                if (fields.Count == 0) continue; // a Transaction used only as a key holder (a hash and a time)
                builders++;

                // A later "x.Fee = ..." before the transaction is built replaces the initializer's value; one after it
                // does not change what was hashed.
                var variable = m.Groups["var"].Success ? m.Groups["var"].Value : null;
                if (variable != null)
                {
                    var after = text.Substring(bodyEnd, Math.Min(4_000, text.Length - bodyEnd));
                    var built = Regex.Match(after, $@"\b{Regex.Escape(variable)}\.Build(Private)?\(");
                    if (built.Success) after = after[..built.Index];
                    foreach (Match later in Regex.Matches(after, $@"\b{Regex.Escape(variable)}\.(Amount|Fee)\s*=\s*([^;]+);"))
                        fields[later.Groups[1].Value] = Collapse(later.Groups[2].Value);
                }

                var line = text[..m.Index].Count(c => c == '\n') + 1;
                foreach (var (field, expr) in fields)
                    if (!Canonical(expr) && !Copies.Contains($"{name}: {expr}"))
                        offenders.Add($"{name}:{line} {field} = {expr}");
            }
            return offenders;
        }

        private static bool Canonical(string expr)
        {
            if (Regex.IsMatch(expr, @"^-?\d+\.\d+[Mm]$"))
                return LedgerIntegrityRules.IsCanonicalDecimal(Dec(expr[..^1]));
            if (expr.Contains("CanonicalDecimal(") || expr.EndsWith(".ToNormalizeDecimal()", StringComparison.Ordinal) || expr.Contains("FeeCalcService.CalculateTXFee("))
                return true;
            var global = Regex.Match(expr, @"^Globals\.(\w+)$");
            if (global.Success)
            {
                var f = typeof(Globals).GetField(global.Groups[1].Value, BindingFlags.Public | BindingFlags.Static);
                return f?.GetValue(null) is decimal d && LedgerIntegrityRules.IsCanonicalDecimal(d);
            }
            return false;
        }

        private static string Collapse(string s) => Regex.Replace(s.Trim(), @"\s+", " ").TrimEnd(',', ' ');

        private static int ClosingBrace(string text, int start)
        {
            var depth = 1;
            for (var i = start; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}' && --depth == 0) return i;
            }
            return -1;
        }

        /// <summary>The initializer's members, split at commas outside any bracket, string or comment.</summary>
        private static List<string> TopLevelMembers(string body)
        {
            var members = new List<string>();
            var depth = 0;
            var current = new System.Text.StringBuilder();
            for (var i = 0; i < body.Length; i++)
            {
                var c = body[i];
                if (c == '/' && i + 1 < body.Length && body[i + 1] == '/')
                {
                    while (i < body.Length && body[i] != '\n') i++;
                    continue;
                }
                if (c == '"')
                {
                    current.Append(c);
                    for (i++; i < body.Length && body[i] != '"'; i++) { if (body[i] == '\\') current.Append(body[i++]); current.Append(body[i]); }
                    if (i < body.Length) current.Append('"');
                    continue;
                }
                if (c is '(' or '{' or '[') depth++;
                if (c is ')' or '}' or ']') depth--;
                if (c == ',' && depth == 0) { members.Add(current.ToString()); current.Clear(); continue; }
                current.Append(c);
            }
            members.Add(current.ToString());
            return members;
        }

        private static string ThisFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
    }
}
