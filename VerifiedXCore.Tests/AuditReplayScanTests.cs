using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// AUDIT-PREP: the replay scan must flag exactly the historical transactions the ungated VX-01 /
    /// VX-02 rules reject, and nothing legitimate. It reuses the consensus predicates, so this also pins
    /// that the scan and consensus agree.
    /// </summary>
    [Collection("DbContextSequential")]
    public class AuditReplayScanTests : IDisposable
    {
        private const string Minter = "xScanMinter00000000000000000000001";
        private const string BtcDest = "tb1qw508d6qejxtdg4y5r3zarvary0c5xw7kxpjzsx";

        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;

        public AuditReplayScanTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"scan_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorLastBlock = Globals.LastBlock;
            DbContext.Initialize();

            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei { SmartContractUID = "scan:v2", ContractData = VbtcTestContracts.VbtcV2ContractData });
            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei { SmartContractUID = "scan:nft", ContractData = VbtcTestContracts.PlainNftContractData });
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.LastBlock = _priorLastBlock;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static Transaction Tx(TransactionType type, object data, string from = Minter, string hash = "")
            => new Transaction { TransactionType = type, FromAddress = from, ToAddress = from, Data = JsonConvert.SerializeObject(data), Hash = hash == "" ? Guid.NewGuid().ToString("N") : hash };

        private static Transaction Withdrawal(string scUid, decimal amount, int feeRate = 10)
            => Tx(TransactionType.VBTC_V2_WITHDRAWAL_REQUEST, new { ContractUID = scUid, BTCAddress = BtcDest, Amount = amount, FeeRate = feeRate });

        private static Transaction Deploy(string txUid, string body, string function = "TokenDeploy()", string from = Minter)
            => Tx(TransactionType.TKNZ_MINT, new[] { new { Function = function, ContractUID = txUid, Data = body, MD5List = "NA" } }, from);

        [Fact]
        public void Scan_FlagsExactlyTheViolations()
        {
            Assert.Empty(AuditReplayScanService.CheckTransaction(Withdrawal("scan:v2", 0.5M), 1));
            Assert.Contains(AuditReplayScanService.CheckTransaction(Withdrawal("scan:v2", -10M), 1), h => h.Rule.StartsWith("VX-01 amount"));
            Assert.Contains(AuditReplayScanService.CheckTransaction(Withdrawal("scan:v2", 0.5M, feeRate: 0), 1), h => h.Rule.StartsWith("VX-01 fee"));
            Assert.Contains(AuditReplayScanService.CheckTransaction(Withdrawal("scan:nft", 0.5M), 1), h => h.Rule.StartsWith("VX-01 contract-type"));

            var goodBody = VbtcTestContracts.TokenContractData("aaaa:1", Minter, 100);
            Assert.Empty(AuditReplayScanService.CheckTransaction(Deploy("aaaa:1", goodBody), 2));
            var mismatchedUid = VbtcTestContracts.TokenContractData("victim:1", Minter, 100);
            Assert.Contains(AuditReplayScanService.CheckTransaction(Deploy("fresh:1", mismatchedUid), 2), h => h.Rule == "VX-02 binding (TokenDeploy)");
            var nftBody = VbtcTestContracts.BuildContractData("nft:1", "xSomeoneElse000000000000000000001", null);
            Assert.Contains(AuditReplayScanService.CheckTransaction(Deploy("nft:1", nftBody, "Mint()"), 3), h => h.Rule == "VX-02 binding (Mint)");

            // Unrelated types are ignored.
            Assert.Empty(AuditReplayScanService.CheckTransaction(new Transaction { TransactionType = TransactionType.TX, Data = null }, 4));
        }

        [Fact]
        public void ScanChain_WalksStoredBlocks_AndReportsHeights()
        {
            var blocks = BlockchainData.GetBlocks();
            // Fixture blocks start at 1: block 0 is genesis, whose merkle root and transaction hashes the scan now checks (NEW-25).
            var w0 = Withdrawal("scan:v2", 0.5M);
            var w1 = Withdrawal("scan:v2", -1M);
            blocks.InsertSafe(new Block { Height = 1, Hash = "h1", Transactions = new List<Transaction> { w0 } });
            blocks.InsertSafe(new Block { Height = 2, Hash = "h2", Transactions = new List<Transaction> { w1 } });
            blocks.InsertSafe(new Block { Height = 3, Hash = "h3", Transactions = new List<Transaction>() });
            Globals.LastBlock = new Block { Height = 3 };

            var (blockCount, txCount, hits) = AuditReplayScanService.ScanChain();

            Assert.Equal(3, blockCount);
            Assert.Equal(2, txCount);
            var hit = Assert.Single(hits);
            Assert.Equal(2, hit.Height);
        }

        [Fact]
        public void ScanChain_OnePass_InHeightOrder_StopsAtTheTip()
        {
            // Blocks stored out of height order (a rollback re-inserts them) are still walked in height order, and blocks
            // above the tip are not scanned. The scan used a range query per batch, which re-read the collection each time.
            var blocks = BlockchainData.GetBlocks();
            var seen = new List<long>();
            foreach (var h in new long[] { 3, 1, 5, 2, 4 })
                blocks.InsertSafe(new Block { Height = h, Hash = "h" + h, Transactions = new List<Transaction> { Withdrawal("scan:v2", h == 5 ? -1M : 0.5M) } });
            Globals.LastBlock = new Block { Height = 4 };

            var (blockCount, txCount, hits) = AuditReplayScanService.ScanChain(p => seen.Add(0));

            Assert.Equal(4, blockCount);           // block 5 is above the tip
            Assert.Equal(4, txCount);
            Assert.Empty(hits);                    // the negative withdrawal is only in block 5
            Assert.Single(seen);                   // one final progress line for a short chain
            var src = File.ReadAllText(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(ThisFile()))!, "VerifiedXCore", "Services", "AuditReplayScanService.cs"));
            Assert.DoesNotContain("b.Height >= start && b.Height <= end", src);
        }

        private static string ThisFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

        // ── Independent-review follow-ups ─────────────────────────────────────────────────

        [Fact]
        public void Scan_FlagsTheFollowUpRules()
        {
            Transaction TokenTx(string fn, string from, object data) =>
                new Transaction { TransactionType = TransactionType.FTKN_TX, FromAddress = from, ToAddress = "xTO", Hash = Guid.NewGuid().ToString("N"), Data = JsonConvert.SerializeObject(data) };

            // NEW-04
            Assert.Empty(AuditReplayScanService.CheckTransaction(TokenTx("t", "xA", new { Function = "TokenTransfer()", ContractUID = "c", FromAddress = "xA", ToAddress = "xTO", Amount = 1M }), 5));
            Assert.Contains(AuditReplayScanService.CheckTransaction(TokenTx("t", "xA", new { Function = "TokenTransfer()", ContractUID = "c", FromAddress = "xVICTIM", ToAddress = "xTO", Amount = 1M }), 5), h => h.Rule == "NEW-04 token transfer binding");
            Assert.Contains(AuditReplayScanService.CheckTransaction(TokenTx("t", "xA", new { Function = "TokenBurn()", ContractUID = "c", FromAddress = "xA", Amount = -1M }), 5), h => h.Rule == "NEW-04 token burn binding");
            Assert.Contains(AuditReplayScanService.CheckTransaction(TokenTx("t", "xA", new { Function = "TokenVoteTopicCast()", ContractUID = "c", FromAddress = "xVICTIM", TopicUID = "t" }), 5), h => h.Rule == "NEW-04 token vote binding");

            // NEW-05 (V1)
            var v1 = new Transaction { TransactionType = TransactionType.TKNZ_TX, FromAddress = "xA", ToAddress = "xB", Hash = "v1",
                Data = JsonConvert.SerializeObject(new[] { new { Function = "TransferCoin()", ContractUID = "scan:nft", Amount = -3M } }) };
            Assert.Contains(AuditReplayScanService.CheckTransaction(v1, 6), h => h.Rule == "NEW-05 V1 amount");
            var multi = new Transaction { TransactionType = TransactionType.TKNZ_TX, FromAddress = "xA", ToAddress = "xB", Hash = "m1",
                Data = JsonConvert.SerializeObject(new { Function = "TransferCoinMulti()", SignatureInput = "s", Amount = 1M,
                    Inputs = new[] { new { SCUID = "scan:nft", FromAddress = "xVICTIM", Amount = 1M, Signature = "forged" } } }) };
            Assert.Contains(AuditReplayScanService.CheckTransaction(multi, 6), h => h.Rule == "NEW-05 V1 multi input");

            // VX-01 follow-up (function path)
            var fn = new Transaction { TransactionType = TransactionType.TKNZ_TX, FromAddress = "xA", ToAddress = "xB", Hash = "f1",
                Data = JsonConvert.SerializeObject(new { Function = "TransferVBTCV2()", ContractUID = "scan:nft", FromAddress = "xA", ToAddress = "xB", Amount = 1M }) };
            Assert.Contains(AuditReplayScanService.CheckTransaction(fn, 7), h => h.Rule == "VX-01 contract-type (TransferVBTCV2 function)");
        }

        [Fact]
        public void ScanChain_FlagsDuplicateCreationInABlock()
        {
            var body = VbtcTestContracts.TokenContractData("dup:1", Minter, 10);
            BlockchainData.GetBlocks().InsertSafe(new Block { Height = 0, Hash = "h0", Transactions = new List<Transaction> { Deploy("dup:1", body), Deploy("dup:1", body) } });
            Globals.LastBlock = new Block { Height = 0 };
            var (_, _, hits) = AuditReplayScanService.ScanChain();
            Assert.Contains(hits, h => h.Rule == "NEW-06 duplicate creation in block");
        }
    }
}
