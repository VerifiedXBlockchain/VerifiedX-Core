using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// The vBTC withdrawal store is consensus-READ but node-local WRITTEN: a Databases folder that
    /// arrived by out-of-band file copy can boot with it empty, silently skewing owner balances and
    /// (worse) making CompleteVBTCV2Withdrawal skip the consensus burn row. These tests cover the
    /// chain-derived heal paths: the merge rebuild and the bounded request-row recovery.
    /// </summary>
    [Collection("DbContextSequential")]
    public class VBTCWithdrawalStoreRebuildTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;

        public VBTCWithdrawalStoreRebuildTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"vbtcrebuild_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static Transaction RequestTx(string hash, string requestor, string scUid, decimal amount, long height) =>
            new Transaction
            {
                Hash = hash,
                FromAddress = requestor,
                ToAddress = "TW_Base",
                TransactionType = TransactionType.VBTC_V2_WITHDRAWAL_REQUEST,
                Timestamp = 1_755_000_000L + height,
                Height = height,
                Data = $"{{\"Function\":\"VBTCWithdrawalRequest()\",\"ContractUID\":\"{scUid}\",\"BTCAddress\":\"tb1q066af78la3rqmnchc396keujllva6turs52749\",\"Amount\":{amount},\"FeeRate\":10}}"
            };

        private static Transaction CompleteTx(string hash, string requestor, string scUid, string requestHash, long height) =>
            new Transaction
            {
                Hash = hash,
                FromAddress = requestor,
                ToAddress = "TW_Base",
                TransactionType = TransactionType.VBTC_V2_WITHDRAWAL_COMPLETE,
                Timestamp = 1_755_000_000L + height,
                Height = height,
                Data = $"{{\"Function\":\"VBTCWithdrawalComplete()\",\"ContractUID\":\"{scUid}\",\"WithdrawalRequestHash\":\"{requestHash}\",\"BTCTransactionHash\":\"btc-{requestHash}\"}}"
            };

        private static void InsertBlock(long height, params Transaction[] txs)
        {
            var block = new Block
            {
                Height = height,
                Hash = $"block-{height}",
                PrevHash = $"block-{height - 1}",
                Timestamp = 1_755_000_000L + height,
                Transactions = txs.ToList(),
                NumOfTx = txs.Length
            };
            BlockchainData.GetBlocks().InsertSafe(block);
        }

        [Fact]
        public async Task Rebuild_ReconstructsRequestsAndCompletions_FromEmptyStore()
        {
            const string sc = "rebuild:1";
            InsertBlock(100, RequestTx("rb1-req", "xRequestorA", sc, 0.25M, 100));
            InsertBlock(110, CompleteTx("rb1-done", "xRequestorA", sc, "rb1-req", 110));

            var result = await VBTCWithdrawalStoreRebuildService.RebuildFromChainAsync("test");
            Assert.True(result.Success, result.Message);
            Assert.Equal(1, result.RequestsInserted);
            Assert.Equal(1, result.CompletionsApplied);

            var row = VBTCWithdrawalRequest.GetByTransactionHash("rb1-req");
            Assert.NotNull(row);
            Assert.Equal(VBTCWithdrawalStatus.Completed, row!.Status);
            Assert.True(row.IsCompleted);
            Assert.Equal(0.25M, row.Amount);
            Assert.Equal("xRequestorA", row.RequestorAddress);
            Assert.Equal(100, row.RequestBlockHeight);
            Assert.Equal("btc-rb1-req", row.BTCTxHash);
        }

        [Fact]
        public async Task Rebuild_IsIdempotent_AndPreservesPinsOnExistingRows()
        {
            const string sc = "rebuild:2";
            InsertBlock(200, RequestTx("rb2-req", "xRequestorB", sc, 0.1M, 200));

            var first = await VBTCWithdrawalStoreRebuildService.RebuildFromChainAsync("test-first");
            Assert.Equal(1, first.RequestsInserted);

            // Simulate an in-flight FROST ceremony: a FIND-028 pin lands on the mined row.
            var pinned = VBTCWithdrawalRequest.GetByTransactionHash("rb2-req")!;
            pinned.PinnedUnsignedTxHex = "0200000001feed";
            pinned.PinnedCoinsJson = "[{\"Outpoint\":\"feed:0\"}]";
            Assert.True(VBTCWithdrawalRequest.Save(pinned, true));

            var second = await VBTCWithdrawalStoreRebuildService.RebuildFromChainAsync("test-second");
            Assert.True(second.Success, second.Message);
            Assert.Equal(0, second.RequestsInserted);
            Assert.Equal(1, second.RequestsAlreadyPresent);

            var after = VBTCWithdrawalRequest.GetByTransactionHash("rb2-req")!;
            Assert.Equal("0200000001feed", after.PinnedUnsignedTxHex);
            Assert.Equal("[{\"Outpoint\":\"feed:0\"}]", after.PinnedCoinsJson);
            Assert.Equal(VBTCWithdrawalStatus.Requested, after.Status);
        }

        [Fact]
        public async Task Rebuild_DoesNotDowngradeAlreadyCompletedRow()
        {
            const string sc = "rebuild:3";
            InsertBlock(300, RequestTx("rb3-req", "xRequestorC", sc, 0.4M, 300));

            // Live processing already completed this withdrawal before the rebuild runs.
            Assert.True(VBTCWithdrawalRequest.Save(new VBTCWithdrawalRequest
            {
                RequestorAddress = "xRequestorC",
                SmartContractUID = sc,
                Amount = 0.4M,
                BTCDestination = "tb1q066af78la3rqmnchc396keujllva6turs52749",
                FeeRate = 10,
                Timestamp = TimeUtil.GetTime(),
                TransactionHash = "rb3-req",
                Status = VBTCWithdrawalStatus.Completed,
                IsCompleted = true,
                BTCTxHash = "btc-live",
                RequestBlockHeight = 300
            }));

            var result = await VBTCWithdrawalStoreRebuildService.RebuildFromChainAsync("test");
            Assert.True(result.Success, result.Message);
            Assert.Equal(0, result.RequestsInserted);

            var row = VBTCWithdrawalRequest.GetByTransactionHash("rb3-req")!;
            Assert.Equal(VBTCWithdrawalStatus.Completed, row.Status);
            Assert.True(row.IsCompleted);
            Assert.Equal("btc-live", row.BTCTxHash);
        }

        [Fact]
        public void RecoverRequestRow_FindsMinedRequestWithinWindow()
        {
            const string sc = "rebuild:4";
            InsertBlock(400, RequestTx("rb4-req", "xRequestorD", sc, 0.33M, 400));

            Assert.Null(VBTCWithdrawalRequest.GetByTransactionHash("rb4-req"));

            var recovered = VBTCWithdrawalStoreRebuildService.TryRecoverRequestRowFromChain("rb4-req", 450);
            Assert.NotNull(recovered);
            Assert.Equal("xRequestorD", recovered!.RequestorAddress);
            Assert.Equal(0.33M, recovered.Amount);
            Assert.Equal(400, recovered.RequestBlockHeight);
            Assert.Equal(VBTCWithdrawalStatus.Requested, recovered.Status);

            // And it is persisted, not just returned.
            Assert.NotNull(VBTCWithdrawalRequest.GetByTransactionHash("rb4-req"));
        }

        [Fact]
        public void RecoverRequestRow_ReturnsNullWhenTxNotOnChain()
        {
            Assert.Null(VBTCWithdrawalStoreRebuildService.TryRecoverRequestRowFromChain("no-such-tx", 500));
        }

        [Fact]
        public async Task AutoProbe_RunsOnceThenMarkerSuppressesRescan()
        {
            // Empty store, empty chain, no marker: probe runs and records the marker.
            await VBTCWithdrawalStoreRebuildService.MaybeAutoRebuildOnStartupAsync();
            Assert.NotNull(VBTCWithdrawalStoreRebuildService.GetMeta());

            // A withdrawal mined later must NOT be picked up by the probe (marker present) —
            // live processing owns it from here; only an explicit rebuild rescans.
            InsertBlock(600, RequestTx("rb6-req", "xRequestorE", "rebuild:6", 0.2M, 600));
            await VBTCWithdrawalStoreRebuildService.MaybeAutoRebuildOnStartupAsync();
            Assert.Null(VBTCWithdrawalRequest.GetByTransactionHash("rb6-req"));

            // The explicit operator rebuild does rescan.
            var result = await VBTCWithdrawalStoreRebuildService.RebuildFromChainAsync("test-explicit");
            Assert.True(result.Success, result.Message);
            Assert.NotNull(VBTCWithdrawalRequest.GetByTransactionHash("rb6-req"));
        }
    }
}
