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

        /// <summary>
        /// The request row is lost while its cancellation record survives, already decided: an approved one refunded the
        /// withdrawal, so the row put back reads cancelled (not open, to be paid or refunded again); a rejected one leaves
        /// it open.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task Rebuild_RestoresARowWhoseCancellationWasDecided_AsItWasDecided(bool approved)
        {
            const string sc = "rebuild:4";
            InsertBlock(400, RequestTx("rb4-req", "xRequestorD", sc, 0.3M, 400));
            VBTCWithdrawalCancellation.SaveCancellation(new VBTCWithdrawalCancellation
            {
                CancellationUID = "CANCEL_rb4-cancel", SmartContractUID = sc, OwnerAddress = "xRequestorD", WithdrawalRequestHash = "rb4-req",
                BTCTxHash = "", FailureProof = "", RequestTime = 1_755_000_410L, ValidatorVotes = new Dictionary<string, bool>(),
                ApproveCount = approved ? 3 : 0, RejectCount = approved ? 0 : 3, IsApproved = approved, IsProcessed = true,
            });

            var result = await VBTCWithdrawalStoreRebuildService.RebuildFromChainAsync("test");
            Assert.True(result.Success, result.Message);
            Assert.Equal(1, result.RequestsInserted);

            var row = VBTCWithdrawalRequest.GetByTransactionHash("rb4-req", sc)!;
            Assert.Equal(approved ? VBTCWithdrawalStatus.Cancelled : VBTCWithdrawalStatus.Requested, row.Status);
            Assert.Equal(approved, row.IsCompleted);

            // A second rebuild changes nothing.
            var again = await VBTCWithdrawalStoreRebuildService.RebuildFromChainAsync("test-again");
            Assert.Equal(0, again.DecidedCancellationsApplied);
            Assert.Equal(approved ? VBTCWithdrawalStatus.Cancelled : VBTCWithdrawalStatus.Requested, VBTCWithdrawalRequest.GetByTransactionHash("rb4-req", sc)!.Status);
        }

        /// <summary>
        /// While the store is being rebuilt, from the path-audit rules a completion, cancel or vote is not judged against it
        /// (a row put back reads open until the rebuild brings it to how its withdrawal ended); the refusal is not read as
        /// state corruption. Before the rules, and once the rebuild has finished, it is judged as before.
        /// </summary>
        [Theory]
        [InlineData(TransactionType.VBTC_V2_WITHDRAWAL_COMPLETE)]
        [InlineData(TransactionType.VBTC_V2_WITHDRAWAL_CANCEL)]
        [InlineData(TransactionType.VBTC_V2_WITHDRAWAL_VOTE)]
        public async Task WhileTheStoreIsRebuilt_AWithdrawalTransactionInABlockIsNotJudged(TransactionType type)
        {
            var (priorAudit, priorTip) = (Globals.PathAuditRulesHeight, Globals.LastBlock);
            var key = new VerifiedXCore.EllipticCurve.PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            var address = AccountData.GetHumanAddress(pub);
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = address, Balance = 10M, Nonce = 0 });
            var tx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(), FromAddress = address, ToAddress = "TW_Base", Amount = 0M, Fee = 0M, Nonce = 0, TransactionType = type,
                Data = "{\"Function\":\"x\",\"ContractUID\":\"rebuild:6\",\"WithdrawalRequestHash\":\"rb6-req\",\"CancellationUID\":\"CANCEL_rb6\",\"BTCTransactionHash\":\"" + new string('a', 64) + "\"}",
            };
            tx.Build();
            tx.Signature = VerifiedXCore.Services.SignatureService.CreateSignature(tx.Hash, key, pub);
            try
            {
                Globals.PathAuditRulesHeight = 50;
                Globals.LastBlock = new Block { Height = 100 };
                VBTCWithdrawalStoreRebuildService.SetRunningForTests(true);
                var (_, why) = await VerifiedXCore.Services.TransactionValidatorService.VerifyTX(tx, blockVerify: true, blockHeight: 60);
                Assert.Equal(VerifiedXCore.Services.TransactionValidatorService.WithdrawalStoreRebuildingReason, why);
                Assert.False(VerifiedXCore.Services.BlockValidatorService.IsStateCorruptionSignal(why));
                var (_, whyBefore) = await VerifiedXCore.Services.TransactionValidatorService.VerifyTX(tx, blockVerify: true, blockHeight: 40);
                Assert.NotEqual(VerifiedXCore.Services.TransactionValidatorService.WithdrawalStoreRebuildingReason, whyBefore);
                VBTCWithdrawalStoreRebuildService.SetRunningForTests(false);
                var (_, whyAfter) = await VerifiedXCore.Services.TransactionValidatorService.VerifyTX(tx, blockVerify: true, blockHeight: 60);
                Assert.NotEqual(VerifiedXCore.Services.TransactionValidatorService.WithdrawalStoreRebuildingReason, whyAfter);
            }
            finally
            {
                VBTCWithdrawalStoreRebuildService.SetRunningForTests(false);
                (Globals.PathAuditRulesHeight, Globals.LastBlock) = (priorAudit, priorTip);
            }
        }

        /// <summary>
        /// A wallet path's own fields are written alone: a row rewritten meanwhile by a writer that does not take the row
        /// lock (a state rebuild or snapshot restore copying the store) keeps its status.
        /// </summary>
        [Fact]
        public void AWalletFieldUpdate_WritesOnlyItsOwnFields()
        {
            const string sc = "rebuild:13";
            Assert.True(VBTCWithdrawalRequest.Save(new VBTCWithdrawalRequest
            {
                RequestorAddress = "xRequestorM", OriginalUniqueId = Guid.NewGuid().ToString("N"), SmartContractUID = sc, TransactionHash = "rb13-req", Amount = 0.1M,
                BTCDestination = "tb1q066af78la3rqmnchc396keujllva6turs52749", FeeRate = 10, Timestamp = 1, RequestBlockHeight = 1300, Status = VBTCWithdrawalStatus.Requested,
                PinnedUnsignedTxHex = "0200", PinnedCoinsJson = "[]", CompleteAfterBtcTxId = "aa",
            }));
            var store = VBTCWithdrawalRequest.GetVBTCWithdrawalRequestDb()!;
            Assert.True(VBTCWithdrawalRequest.UpdateLocalFields("rb13-req", sc, r =>
            {
                var rewritten = store.FindById(r.Id);
                rewritten.Status = VBTCWithdrawalStatus.Cancelled;
                rewritten.IsCompleted = true;
                Assert.True(store.Update(rewritten));
                r.PinnedUnsignedTxHex = null;
                r.CompleteAfterBtcTxId = null;
            }));
            var after = VBTCWithdrawalRequest.GetByTransactionHash("rb13-req", sc)!;
            Assert.Equal(VBTCWithdrawalStatus.Cancelled, after.Status);
            Assert.True(after.IsCompleted);
            Assert.Null(after.PinnedUnsignedTxHex);
            Assert.Null(after.CompleteAfterBtcTxId);
            Assert.Equal("[]", after.PinnedCoinsJson);
        }

        /// <summary>
        /// A wallet path changes its own fields of a request row while the chain's processing cancels the withdrawal:
        /// the two writes do not interleave, and the cancellation is not written over with the status the wallet read.
        /// </summary>
        [Fact]
        public async Task AWalletFieldUpdate_DoesNotWriteBackAStatusChangedMeanwhile()
        {
            const string sc = "rebuild:9";
            Assert.True(VBTCWithdrawalRequest.Save(new VBTCWithdrawalRequest
            {
                RequestorAddress = "xRequestorI", OriginalUniqueId = Guid.NewGuid().ToString("N"), SmartContractUID = sc, TransactionHash = "rb9-req", Amount = 0.1M,
                BTCDestination = "tb1q066af78la3rqmnchc396keujllva6turs52749", FeeRate = 10, Timestamp = 1, RequestBlockHeight = 900, Status = VBTCWithdrawalStatus.Requested,
                PinnedUnsignedTxHex = "0200", PinnedCoinsJson = "[]",
            }));
            using var inside = new System.Threading.ManualResetEventSlim();
            using var gate = new System.Threading.ManualResetEventSlim();
            var wallet = Task.Run(() => VBTCWithdrawalRequest.UpdateLocalFields("rb9-req", sc, r => { inside.Set(); gate.Wait(5000); r.PinnedUnsignedTxHex = null; r.PinnedCoinsJson = null; }));
            Assert.True(inside.Wait(5000));

            var cancelled = VBTCWithdrawalRequest.GetByTransactionHash("rb9-req", sc)!;
            cancelled.Status = VBTCWithdrawalStatus.Cancelled;
            cancelled.IsCompleted = true;
            var chain = Task.Run(() => VBTCWithdrawalRequest.Save(cancelled, true));
            await Task.Delay(300);
            Assert.False(chain.IsCompleted);   // waits for the wallet's write

            gate.Set();
            Assert.True(await wallet);
            Assert.True(await chain);
            var after = VBTCWithdrawalRequest.GetByTransactionHash("rb9-req", sc)!;
            Assert.Equal(VBTCWithdrawalStatus.Cancelled, after.Status);
            Assert.True(after.IsCompleted);
        }

        [Fact]
        public async Task Rebuild_ADecidedCancellation_DoesNotTurnACompletedRowCancelled()
        {
            const string sc = "rebuild:5";
            InsertBlock(500, RequestTx("rb5-req", "xRequestorE", sc, 0.2M, 500));
            InsertBlock(510, CompleteTx("rb5-done", "xRequestorE", sc, "rb5-req", 510));
            VBTCWithdrawalCancellation.SaveCancellation(new VBTCWithdrawalCancellation
            {
                CancellationUID = "CANCEL_rb5-cancel", SmartContractUID = sc, OwnerAddress = "xRequestorE", WithdrawalRequestHash = "rb5-req",
                BTCTxHash = "", FailureProof = "", RequestTime = 1_755_000_505L, ValidatorVotes = new Dictionary<string, bool>(),
                ApproveCount = 3, IsApproved = true, IsProcessed = true,
            });

            var result = await VBTCWithdrawalStoreRebuildService.RebuildFromChainAsync("test");
            Assert.True(result.Success, result.Message);
            var row = VBTCWithdrawalRequest.GetByTransactionHash("rb5-req", sc)!;
            Assert.Equal(VBTCWithdrawalStatus.Completed, row.Status);
            Assert.Equal(0, result.DecidedCancellationsApplied);
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
