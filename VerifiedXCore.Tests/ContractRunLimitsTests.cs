using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VerifiedXCore.Data;
using VerifiedXCore.Extensions;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Models;
using VerifiedXCore.Models.SmartContracts;
using VerifiedXCore.Services;
using VerifiedXCore.Trillium;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Every run of contract code on a node is held to ContractRunLimits.Contract: steps, the length of any one string, and
    /// the text it works through. A small transaction whose body builds a huge string with no loop and no call (a short
    /// string, then a line that doubles it, again and again) stops early on every node and is refused, at admission and,
    /// from ContractRunLimitsHeight, in a block, whatever the contract UID it names.
    /// </summary>
    [Collection("DbContextSequential")]
    public class ContractRunLimitsTests : IDisposable
    {
        private const string Uid = "7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d:1790900002";
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly long _priorHeight = Globals.ContractRunLimitsHeight;
        private readonly (PrivateKey Key, string Pub, string Address) _owner = NewKey();

        public ContractRunLimitsTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"runlimits_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
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
            Globals.ContractRunLimitsHeight = _priorHeight;
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

        /// <summary>A 125-character string, then <paramref name="doublings"/> lines that double it.</summary>
        private static string Doubling(int doublings) =>
            "var payload = \"" + new string('x', 125) + "\"\n" + string.Concat(Enumerable.Repeat("payload = payload + payload\n", doublings));

        private string Body(string uid, string code) =>
            NEW29_ContractCodeTests.WithCode(VbtcTestContracts.BuildContractData(uid, _owner.Address, null, name: "Plain"), code);

        private Transaction Mint(string body)
        {
            var tx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(), FromAddress = _owner.Address, ToAddress = _owner.Address, Amount = 0.0M, Fee = 0, Nonce = 0,
                TransactionType = TransactionType.NFT_MINT,
                Data = JsonConvert.SerializeObject(new[] { new { Function = "Mint()", ContractUID = Uid, Data = body } }),
            };
            tx.Fee = FeeCalcService.CalculateTXFee(tx);
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, _owner.Key, _owner.Pub);
            return tx;
        }

        [Theory]
        [InlineData(false)] // a UID that is not the transaction's: refused, and never mined, so it costs nothing
        [InlineData(true)]  // the transaction's own UID: refused too, so no node that falls back on a body it cannot run admits it
        public async Task A_body_that_doubles_a_string_is_refused_at_admission_early_and_in_little_memory(bool ownUid)
        {
            var mint = Mint(Body(ownUid ? Uid : "0000000000000000000000000000000f:1790900003", Doubling(23))); // 2 GB of text unlimited
            Assert.True(mint.Data.Length < 4_000);

            GC.Collect();
            var before = GC.GetTotalAllocatedBytes(precise: true);
            var admission = Task.Run(() => TransactionValidatorService.VerifyTX(mint));
            Assert.True(admission.Wait(TimeSpan.FromSeconds(20)), "admission must stop the run early");
            var (ok, why) = await admission;
            var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

            Assert.False(ok);
            Assert.StartsWith("Contract code costs more to run than a contract may:", why);
            Assert.True(allocated < 512L * 1024 * 1024, $"allocated {allocated / (1024 * 1024)} MB");
        }

        [Fact]
        public void Every_reader_of_the_body_meets_the_limits_outside_a_block()
        {
            var body = Body(Uid, Doubling(23));
            Assert.ThrowsAny<global::Trillium.CodeAnalysis.EvaluationLimitException>(() => SmartContractMain.GenerateSmartContractInMemory(body));
            var refused = SmartContractDeployBinding.Validate(body, Uid, _owner.Address, isTokenDeploy: false, out _);
            Assert.Null(refused); // the binding alone falls back on the declared identity: the rule above refuses the body first
        }

        [Fact]
        public void In_a_block_the_limits_hold_from_their_height_and_not_below()
        {
            Globals.ContractRunLimitsHeight = 2_000;
            Assert.Same(global::Trillium.CodeAnalysis.EvaluationLimits.None, ContractRunLimits.For(1_999));
            Assert.Same(ContractRunLimits.Contract, ContractRunLimits.For(2_000));
            Assert.Same(ContractRunLimits.Contract, ContractRunLimits.For(null)); // admission, the API, the wallet

            // 8 million characters: past the string limit, small enough to run as before.
            var mint = Mint(Body(Uid, Doubling(16)));
            using (ContractClock.ForBlock(TimeUtil.GetTime(), 1_999))
            {
                Assert.Null(LedgerIntegrityRules.ContractBodyAllowed(mint, 1_999, inBlock: true));
                Assert.NotNull(SmartContractMain.GenerateSmartContractInMemory(Body(Uid, Doubling(16))));
            }
            using (ContractClock.ForBlock(TimeUtil.GetTime(), 2_000))
                Assert.StartsWith("Contract code costs more to run than a contract may:", LedgerIntegrityRules.ContractBodyAllowed(mint, 2_000, inBlock: true));
            Assert.StartsWith("Contract code costs more to run than a contract may:", LedgerIntegrityRules.ContractBodyAllowed(mint));
        }

        [Fact]
        public void An_ordinary_contract_runs_as_before()
        {
            var body = Body(Uid, "");
            Assert.Null(LedgerIntegrityRules.ContractBodyAllowed(Mint(body)));
            var main = SmartContractMain.GenerateSmartContractInMemory(body);
            Assert.Equal(Uid, main.SmartContractUID);
            Assert.Null(SmartContractDeployBinding.Validate(body, Uid, _owner.Address, isTokenDeploy: false, out _));
        }

        [Fact]
        public void Every_run_of_contract_code_in_the_node_is_given_the_contract_limits()
        {
            // A new place that runs code without them fails here.
            var root = Path.Combine(RepoRoot(), "VerifiedXCore");
            var runs = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
                .SelectMany(f => File.ReadLines(f).Select(l => (File: Path.GetFileName(f), Line: l)))
                .Where(x => Regex.IsMatch(x.Line, @"\bcompilation\.Evaluate\(", RegexOptions.IgnoreCase))
                .ToList();
            Assert.NotEmpty(runs);
            Assert.All(runs, x => Assert.Contains("ContractRunLimits", x.Line));
        }

        private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "") =>
            Path.GetDirectoryName(Path.GetDirectoryName(thisFile))!;
    }
}
