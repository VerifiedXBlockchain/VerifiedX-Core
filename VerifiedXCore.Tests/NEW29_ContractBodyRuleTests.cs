using System;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// NEW-29 (follow-up): a transaction carrying a contract body that calls rand() or input() (answers differ between
    /// nodes: unseeded random, the node's console) or uses a do-while loop (the one endless construct the parse-time ban
    /// misses) is refused before anything runs the code. Only bodies that parse are examined, on their syntax tree, so the
    /// same words inside strings - or inside a broken multi-line description, which never runs - do not refuse a body.
    /// </summary>
    [Collection("DbContextSequential")]
    public class NEW29_ContractBodyRuleTests : IDisposable
    {
        private const string Uid = "8e8e8e8e8e8e8e8e8e8e8e8e8e8e8e8e:1790900001";
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly (PrivateKey Key, string Pub, string Address) _owner = NewKey();

        public NEW29_ContractBodyRuleTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"new29r_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorLastBlock = Globals.LastBlock;
            DbContext.Initialize();
            Globals.LastBlock = new Block { Height = 1000 };
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _owner.Address, Balance = 100M, Nonce = 0 });
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.LastBlock = _priorLastBlock;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static (PrivateKey Key, string Pub, string Address) NewKey()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            return (key, pub, AccountData.GetHumanAddress(pub));
        }

        private string Plain() => VbtcTestContracts.BuildContractData(Uid, _owner.Address, null, name: "Plain");
        private string With(string code) => NEW29_ContractCodeTests.WithCode(Plain(), code);

        private Transaction Tx(string function, string body, TransactionType type = TransactionType.NFT_MINT)
        {
            var tx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(), FromAddress = _owner.Address, ToAddress = _owner.Address, Amount = 0.0M, Fee = 0, Nonce = 0,
                TransactionType = type,
                Data = JsonConvert.SerializeObject(new[] { new { Function = function, ContractUID = Uid, Data = body } }),
            };
            tx.Fee = VerifiedXCore.Services.FeeCalcService.CalculateTXFee(tx);
            tx.Build();
            tx.Signature = VerifiedXCore.Services.SignatureService.CreateSignature(tx.Hash, _owner.Key, _owner.Pub);
            return tx;
        }

        public const string CallsRand = "function Lucky() : int { return rand(10) }";
        public const string CallsInput = "function Ask() : string { return input() }";
        public const string DoWhile = "function Spin() : int { var i = 0 do { i = i + 1 } while true return i }";

        [Theory]
        [InlineData(CallsRand, "Contract code may not call rand().")]
        [InlineData(CallsInput, "Contract code may not call input().")]
        [InlineData(DoWhile, "Contract code may not use a do-while loop.")]
        public async Task ABodyUsingThem_IsRefused_AtAdmissionAndInABlock(string code, string reason)
        {
            var mint = Tx("Mint()", With(code));
            Assert.Equal(reason, LedgerIntegrityRules.ContractBodyAllowed(mint));
            var admission = TransactionValidatorService.VerifyTX(mint);
            Assert.True(admission.Wait(TimeSpan.FromSeconds(20)), "the rule must answer before anything runs the body");
            Assert.Equal((false, reason), await admission);
            Assert.Equal((false, reason), await TransactionValidatorService.VerifyTX(mint, false, true, false, null, false, 1001));
        }

        /// <summary>
        /// A transaction nobody signed is refused before any rule parses or runs the code it carries (round 36): the
        /// signature is checked first, so only its owner's own transactions reach the parser.
        /// </summary>
        [Theory]
        [InlineData("Mint()", TransactionType.NFT_MINT)]
        [InlineData("Transfer()", TransactionType.NFT_TX)]
        [InlineData("Update()", TransactionType.NFT_MINT)]
        [InlineData("Evolve()", TransactionType.NFT_TX)]
        [InlineData("Devolve()", TransactionType.NFT_TX)]
        [InlineData("ChangeEvolveStateSpecific()", TransactionType.NFT_TX)]
        public async Task AnUnsignedTransaction_IsRefusedBeforeItsBodyIsParsed(string function, TransactionType type)
        {
            var unsigned = Tx(function, With(DoWhile), type);
            unsigned.Signature = null;
            Assert.Equal((false, "Signature cannot be null."), await TransactionValidatorService.VerifyTX(unsigned));
            var forged = Tx(function, With(DoWhile), type);
            forged.Signature = Tx(function, With(CallsRand), type).Signature;
            Assert.Equal((false, "Signature Failed to verify."), await TransactionValidatorService.VerifyTX(forged));
        }

        [Theory]
        [InlineData("Transfer()", TransactionType.NFT_TX)]
        [InlineData("Update()", TransactionType.NFT_MINT)]
        [InlineData("Evolve()", TransactionType.NFT_TX)]
        public void EveryFunctionThatCarriesABody_IsCovered(string function, TransactionType type)
        {
            Assert.Equal("Contract code may not call rand().", LedgerIntegrityRules.ContractBodyAllowed(Tx(function, With(CallsRand), type)));
        }

        [Fact]
        public void TheSameWordsInStrings_OrInABodyThatNeverRuns_AreAllowed()
        {
            // Inside string literals of a well-formed body.
            Assert.Null(LedgerIntegrityRules.ContractBodyAllowed(Tx("Mint()", With("let Note = \"do rand(5) and input() while you can\""))));
            // A multi-line description (as some historical mints have): the body no longer parses, so it never runs,
            // and "do" / "for" in the prose are not code.
            var src = NEW29_ContractCodeTests.Source(Plain()).Replace("let Name = \"Plain\"", "let Name = \"Plain\nwhat we do for rand(1) input()\"");
            Assert.Contains("what we do", src);
            Assert.Null(LedgerIntegrityRules.ContractBodyAllowed(Tx("Mint()", NEW29_ContractCodeTests.Body(src))));
        }

        [Fact]
        public void ABodyTheParserCannotRead_IsRefused_NotThrown()
        {
            var prior = LedgerIntegrityRules.ParseContractBody;
            try
            {
                LedgerIntegrityRules.ParseContractBody = _ => throw new InvalidOperationException("parser fault");
                Assert.Equal("Contract code cannot be read.", LedgerIntegrityRules.ContractBodyAllowed(Tx("Mint()", Plain())));
            }
            finally { LedgerIntegrityRules.ParseContractBody = prior; }

            Assert.Null(LedgerIntegrityRules.ContractBodyAllowed(Tx("Mint()", Plain()))); // the real parser reads it
        }

        /// <summary>
        /// Every node parses a carried body during VerifyTX, so the body must be one every node can finish parsing: a body
        /// nested past Trillium's depth limit, or larger than the token cap, is refused at admission and in a block instead
        /// of running the node out of stack or time.
        /// </summary>
        [Theory]
        [InlineData("parentheses", "Contract code is nested too deeply.")]
        [InlineData("blocks", "Contract code is nested too deeply.")]
        [InlineData("tokens", "Contract code is too large: ")]
        [InlineData("operator chain", "Contract code is nested too deeply.")]
        [InlineData("operator chain with a syntax error after it", "Contract code is nested too deeply.")]
        public async Task ABodyNoNodeCanFinishParsing_IsRefused_AtAdmissionAndInABlock(string shape, string expected)
        {
            var chain = "function GetNFTId() : string { var v = 1" + string.Concat(Enumerable.Repeat("+1", 32_000)) + " return \"x\" }";
            var source = shape switch
            {
                "parentheses" => "var a = " + new string('(', 20_000),
                "blocks" => new string('{', 20_000),
                "operator chain" => chain,
                "operator chain with a syntax error after it" => chain + "@",
                _ => string.Concat(Enumerable.Repeat("var a = 1\n", 20_000)),
            };
            var mint = Tx("Mint()", NEW29_ContractCodeTests.Body(source));
            var reason = LedgerIntegrityRules.ContractBodyAllowed(mint);
            Assert.StartsWith(expected, reason);
            Assert.Equal((false, reason), await TransactionValidatorService.VerifyTX(mint));
            await AssertRefusedInBlocksFromTheLimitsHeight(mint, reason!);
        }

        /// <summary>
        /// Mempool admission applies the limits at every height; a block is judged by them from ContractBodyLimitsHeight,
        /// so a block older builds mined before it (testnet carries some) stays valid.
        /// </summary>
        private static async Task AssertRefusedInBlocksFromTheLimitsHeight(Transaction mint, string reason)
        {
            Assert.Equal((false, reason), await TransactionValidatorService.VerifyTX(mint, false, true, false, null, false, Globals.ContractBodyLimitsHeight));
            Assert.NotEqual(reason, (await TransactionValidatorService.VerifyTX(mint, false, true, false, null, false, Globals.ContractBodyLimitsHeight - 1)).Item2);
        }

        /// <summary>A body that unpacks to more characters than any node should read is refused before it is read.</summary>
        [Fact]
        public async Task ABodyThatUnpacksTooLarge_IsRefused_AtAdmissionAndInABlock()
        {
            var limit = LedgerIntegrityRules.MaxContractBodyChars;
            var reason = $"Contract code is too large: {limit + 1} characters, at most {limit}.";
            var mint = Tx("Mint()", NEW29_ContractCodeTests.Body(new string(' ', limit - 9) + "var a = 1\n"));
            Assert.Equal((false, reason), await TransactionValidatorService.VerifyTX(mint));
            await AssertRefusedInBlocksFromTheLimitsHeight(mint, reason);

            // A long body of few tokens (a large description, a vault's validator list) stays allowed.
            Assert.Null(LedgerIntegrityRules.ContractBodyAllowed(Tx("Mint()", With($"let Note = \"{new string('a', 900_000)}\""))));
        }

        private static string Name(int i) { var s = ""; do { s = (char)('a' + i % 26) + s; i /= 26; } while (i > 0); return s; }

        /// <summary>
        /// Fa calls Fb calls ... n deep, each call under <paramref name="unary"/> unary operators (more stack per level);
        /// GetNFTId(), which the node runs at admission, starts the chain.
        /// </summary>
        private static string CallChain(int n, int unary = 0) =>
            "function GetNFTId() : string { var v = Fa() return \"x\" }\n"
            + string.Concat(Enumerable.Range(0, n).Select(i => $"function F{Name(i)}() : int {{ return {string.Concat(Enumerable.Repeat("- ", unary))}F{Name(i + 1)}() }}\n"))
            + $"function F{Name(n)}() : int {{ return 1 }}\n";

        /// <summary>Each level calls the next twice: 2^d calls from a body of a few hundred tokens.</summary>
        private static string CallFan(int d) =>
            "function GetNFTId() : string { var n = Fa() return \"x\" }\n"
            + string.Concat(Enumerable.Range(0, d).Select(i => $"function F{Name(i)}() : int {{ return F{Name(i + 1)}() + F{Name(i + 1)}() }}\n"))
            + $"function F{Name(d)}() : int {{ return 1 }}\n";

        /// <summary>
        /// Without loops or recursion, a long chain of calls still overflows the evaluator's stack (ending the node) and a
        /// fan of them runs for days. Both are refused at admission and in a block, before anything runs the body.
        /// </summary>
        [Theory]
        [InlineData("chain", "Contract code nests calls too deeply.")]
        [InlineData("fan", "Contract code makes too many calls.")]
        public async Task ABodyWhoseCallsNoNodeCanFinish_IsRefused_AtAdmissionAndInABlock(string shape, string reason)
        {
            var mint = Tx("Mint()", NEW29_ContractCodeTests.Body(shape == "chain" ? CallChain(150, unary: 200) : CallFan(40)));
            // From VbtcV1RetirementHeight (below the limits height) the validator runs a minted body's GetNFTId() to ask
            // whether it is a V1 contract; before the limits height Trillium's own evaluation limits stop such a run.
            Assert.True(Globals.VbtcV1RetirementHeight < Globals.ContractBodyLimitsHeight);
            await AssertRefusedInBlocksFromTheLimitsHeight(mint, reason);
            Assert.Equal((false, reason), await TransactionValidatorService.VerifyTX(mint));
            Assert.Equal(reason, LedgerIntegrityRules.ContractBodyAllowed(mint));
        }

        /// <summary>The deepest and the widest call shapes the rule lets through run to the end on a 1 MB stack.</summary>
        [Fact]
        public void TheLargestCallShapesAllowed_RunOnAnOrdinaryStack()
        {
            static global::Trillium.Syntax.SyntaxTree Parse(string s) => global::Trillium.Syntax.SyntaxTree.Parse(s, preventLoopsAndRecursion: true);
            string Largest(Func<int, string> shape, Func<(int Depth, long Calls), bool> allowed) =>
                Enumerable.Range(1, 1_000).Select(shape).TakeWhile(s => allowed(LedgerIntegrityRules.ContractCallCost(Parse(s).Root))).Last();
            var deepest = Largest(n => CallChain(n), c => c.Depth <= LedgerIntegrityRules.MaxContractCallDepth);
            var deepestUnary = Largest(n => CallChain(n, unary: 100), c => c.Depth <= LedgerIntegrityRules.MaxContractCallDepth);
            var widest = Largest(CallFan, c => c.Calls <= LedgerIntegrityRules.MaxContractCalls);
            foreach (var source in new[] { deepest, deepestUnary, widest })
            {
                Assert.Null(LedgerIntegrityRules.ContractBodyAllowed(Tx("Mint()", NEW29_ContractCodeTests.Body(source))));
                object? value = null;
                var thread = new System.Threading.Thread(() =>
                {
                    var vars = new Dictionary<global::Trillium.Symbols.VariableSymbol, object>();
                    var body = global::Trillium.CodeAnalysis.Compilation.CreateScript(null, Parse(source));
                    body.Evaluate(vars);
                    value = global::Trillium.CodeAnalysis.Compilation.CreateScript(body, Parse("GetNFTId()")).Evaluate(vars).Value;
                }, 1024 * 1024);
                thread.Start();
                Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
                Assert.Equal("x", value);
            }
        }

        [Fact]
        public void OrdinaryBodies_AndDateProc_AreAllowed()
        {
            Assert.Null(LedgerIntegrityRules.ContractBodyAllowed(Tx("Mint()", Plain())));
            Assert.Null(LedgerIntegrityRules.ContractBodyAllowed(Tx("Mint()", VbtcTestContracts.VbtcV2ContractData)));
            Assert.Null(LedgerIntegrityRules.ContractBodyAllowed(Tx("TokenDeploy()", VbtcTestContracts.TokenContractData(Uid, _owner.Address, 1000), TransactionType.FTKN_MINT)));
            // dateProc is used by 50 historical bodies (evolving NFTs); it stays allowed.
            Assert.Null(LedgerIntegrityRules.ContractBodyAllowed(Tx("Mint()", With("function Ready() : bool { return dateProc(\"638000000000000000\") }"))));
            // Transactions without a body.
            Assert.Null(LedgerIntegrityRules.ContractBodyAllowed(new Transaction { Data = "{\"Function\":\"TokenTransfer()\",\"Amount\":1}" }));
            Assert.Null(LedgerIntegrityRules.ContractBodyAllowed(new Transaction { Data = "[{\"Function\":\"Transfer()\",\"ContractUID\":\"x:1\",\"Data\":\"not base64\"}]" }));
        }

        [Fact]
        public void TheReplayScanReportsIt()
        {
            var hits = AuditReplayScanService.CheckTransaction(Tx("Mint()", With(CallsRand)), 1001);
            Assert.Contains(hits, h => h.Rule == "NEW-29 contract body");
        }
    }
}
