using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// The audit replay scan and the fund-loss history scan judge a contract call the payload reader cannot read (a
    /// non-text MD5List) as a violation: the reader sees no rewrite there while the dispatcher still runs it, so a vault
    /// rewrite carried that way passed both scans. A readable rewrite that resends the code is still clean.
    /// </summary>
    [Collection("DbContextSequential")]
    public class ScanUnreadableRewriteTests : IDisposable
    {
        private const string Owner = "xScanOwner000000000000000000000001";
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;

        public ScanUnreadableRewriteTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"scan_unreadable_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei { SmartContractUID = "scan:v2", ContractData = VbtcTestContracts.VbtcV2ContractData });
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static Transaction Call(TransactionType type, string function, string uid, string body, object md5List) => new()
        {
            TransactionType = type, FromAddress = Owner, ToAddress = Owner, Hash = Guid.NewGuid().ToString("N"),
            Data = JsonConvert.SerializeObject(new[] { new { Function = function, ContractUID = uid, Data = body, MD5List = md5List } }),
        };

        [Fact]
        public void ReplayScan_FlagsAnUnreadableVaultRewrite()
        {
            var hits = AuditReplayScanService.CheckTransaction(Call(TransactionType.TKNZ_TX, "Transfer()", "scan:v2", "replaced code", new[] { "x" }), 5);
            Assert.Contains(hits, h => h.Rule == "contract call unreadable");
        }

        [Fact]
        public void ReplayScan_PassesAReadableTransferThatResendsTheCode()
        {
            var hits = AuditReplayScanService.CheckTransaction(Call(TransactionType.TKNZ_TX, "Transfer()", "scan:v2", VbtcTestContracts.VbtcV2ContractData, "NA"), 5);
            Assert.DoesNotContain(hits, h => h.Rule == "contract call unreadable" || h.Rule.StartsWith("NEW-26"));
        }

        [Fact]
        public void HistoryScan_FlagsAnUnreadableRewriteOfAPlainNft()
        {
            var mint = Call(TransactionType.NFT_MINT, "Mint()", "plain:1", VbtcTestContracts.PlainNftContractData, "NA");
            var rewrite = Call(TransactionType.NFT_TX, "Transfer()", "plain:1", VbtcTestContracts.VbtcV2ContractData, new[] { "x" });
            var blocks = new List<Block>
            {
                new() { Height = 1, Transactions = new List<Transaction> { mint } },
                new() { Height = 2, Transactions = new List<Transaction> { rewrite } },
            };
            var result = FundLossHistoryScanService.Scan(blocks);
            Assert.Equal(1, result.Counters.GetValueOrDefault("rewrites.unreadable"));
            Assert.Contains(result.Hits, h => h.Item == "4/9 unreadable rewrite" && h.Height == 2);
        }

        [Fact]
        public void HistoryScan_TracksTheBodyAnUnreadableRewriteCarried()
        {
            var mint = Call(TransactionType.NFT_MINT, "Mint()", "plain:3", VbtcTestContracts.PlainNftContractData, "NA");
            var unreadable = Call(TransactionType.NFT_TX, "Transfer()", "plain:3", VbtcTestContracts.VbtcV2ContractData, new[] { "x" });
            var resend = Call(TransactionType.NFT_TX, "Transfer()", "plain:3", VbtcTestContracts.VbtcV2ContractData, "NA");
            var result = FundLossHistoryScanService.Scan(new List<Block>
            {
                new() { Height = 1, Transactions = new List<Transaction> { mint } },
                new() { Height = 2, Transactions = new List<Transaction> { unreadable } },
                new() { Height = 3, Transactions = new List<Transaction> { resend } },
            });
            // The vault code the unreadable rewrite carried is judged there, once; the readable resend matches the tracked body.
            Assert.Single(result.Hits, h => h.Item.StartsWith("4 rewrite adds TokenizationV2"));
            Assert.Contains(result.Hits, h => h.Item.StartsWith("4 rewrite adds TokenizationV2") && h.Height == 2);
            Assert.DoesNotContain(result.Hits, h => h.Height == 3);
        }

        [Fact]
        public void HistoryScan_ReportsARewriteAfterAnUnreadableBody()
        {
            var mint = Call(TransactionType.NFT_MINT, "Mint()", "plain:4", VbtcTestContracts.PlainNftContractData, "NA");
            var lost = new Transaction
            {
                TransactionType = TransactionType.NFT_TX, FromAddress = Owner, ToAddress = Owner, Hash = Guid.NewGuid().ToString("N"),
                Data = JsonConvert.SerializeObject(new[] { new { Function = "Transfer()", ContractUID = "plain:4", Data = new { Body = "not text" }, MD5List = "NA" } }),
            };
            var later = Call(TransactionType.NFT_TX, "Transfer()", "plain:4", VbtcTestContracts.PlainNftContractData, "NA");
            var result = FundLossHistoryScanService.Scan(new List<Block>
            {
                new() { Height = 1, Transactions = new List<Transaction> { mint } },
                new() { Height = 2, Transactions = new List<Transaction> { lost } },
                new() { Height = 3, Transactions = new List<Transaction> { later } },
            });
            Assert.Contains(result.Hits, h => h.Item == "4/9 unreadable rewrite" && h.Height == 2);
            Assert.Contains(result.Hits, h => h.Item.StartsWith("4/9 rewrite of a contract whose body") && h.Height == 3);
        }

        [Fact]
        public void HistoryScan_PassesAReadableTransferThatResendsTheBody()
        {
            var mint = Call(TransactionType.NFT_MINT, "Mint()", "plain:2", VbtcTestContracts.PlainNftContractData, "NA");
            var transfer = Call(TransactionType.NFT_TX, "Transfer()", "plain:2", VbtcTestContracts.PlainNftContractData, "NA");
            var result = FundLossHistoryScanService.Scan(new List<Block>
            {
                new() { Height = 1, Transactions = new List<Transaction> { mint } },
                new() { Height = 2, Transactions = new List<Transaction> { transfer } },
            });
            Assert.False(result.Counters.ContainsKey("rewrites.unreadable"));
            Assert.Empty(result.Hits);
        }
    }
}
