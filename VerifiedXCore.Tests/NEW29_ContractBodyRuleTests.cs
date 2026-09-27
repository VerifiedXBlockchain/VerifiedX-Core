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
