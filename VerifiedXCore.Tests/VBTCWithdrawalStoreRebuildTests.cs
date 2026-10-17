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
        /// The rebuild runs holding the lock block validation, its commit and the state snapshots run under: it waits for
        /// a block being processed, and none is processed (nor snapshot taken) while it rewrites the store, so none reads
        /// a row it has put back before the scan has brought it to how its withdrawal ended.
        /// </summary>
        [Fact]
        public async Task Rebuild_HoldsBlockProcessingOff_ForItsWholeRun()
        {
            InsertBlock(700, RequestTx("rb7-req", "xRequestorG", "rebuild:7", 0.1M, 700));
            InsertBlock(710, RequestTx("rb7b-req", "xRequestorG", "rebuild:7", 0.2M, 710));
            var seen = new List<(bool Running, int Free)>();
            var processing = VerifiedXCore.Services.BlockValidatorService.ValidateBlockSemaphore;
            VBTCWithdrawalStoreRebuildService.BlockScannedForTests = _ => seen.Add((VBTCWithdrawalStoreRebuildService.IsRunning, processing.CurrentCount));
            try
            {
                await processing.WaitAsync();   // a block is being processed
                Task<VBTCWithdrawalStoreRebuildService.RebuildResult> rebuild;
                try
                {
                    rebuild = Task.Run(() => VBTCWithdrawalStoreRebuildService.RebuildFromChainAsync("test"));
                    await Task.Delay(300);
                    Assert.False(rebuild.IsCompleted);
                    Assert.False(VBTCWithdrawalStoreRebuildService.IsRunning);
                    Assert.Null(VBTCWithdrawalRequest.GetByTransactionHash("rb7-req", "rebuild:7"));
                }
                finally { processing.Release(); }

                var result = await rebuild;
                Assert.True(result.Success, result.Message);
                Assert.Equal(2, result.RequestsInserted);
                Assert.NotEmpty(seen);
                Assert.All(seen, s => { Assert.True(s.Running); Assert.Equal(0, s.Free); });
                Assert.Equal(1, processing.CurrentCount);
                Assert.False(VBTCWithdrawalStoreRebuildService.IsRunning);
            }
            finally { VBTCWithdrawalStoreRebuildService.BlockScannedForTests = null; }
        }

        [Fact]
        public async Task Rebuild_DoesNotRunAlongsideASnapshotRestore()
        {
            InsertBlock(800, RequestTx("rb8-req", "xRequestorH", "rebuild:8", 0.1M, 800));
            try
            {
                VerifiedXCore.Utilities.SnapshotRestoreUtility.SetRestoreRunningForTests(true);
                var result = await VBTCWithdrawalStoreRebuildService.RebuildFromChainAsync("test");
                Assert.False(result.Success);
                Assert.Contains("snapshot restore", result.Message);
                Assert.Null(VBTCWithdrawalRequest.GetByTransactionHash("rb8-req", "rebuild:8"));
            }
            finally { VerifiedXCore.Utilities.SnapshotRestoreUtility.SetRestoreRunningForTests(false); }
            Assert.True((await VBTCWithdrawalStoreRebuildService.RebuildFromChainAsync("test-after")).Success);
        }

        /// <summary>
        /// A snapshot restore that starts while the rebuild runs: the rebuild stops at its next block, and the restore
        /// does not rewrite the store until it has.
        /// </summary>
        [Fact]
        public async Task ARestoreStartingMidRebuild_StopsItBeforeRewritingTheStore()
        {
            for (var h = 1000; h < 1010; h++) InsertBlock(h, RequestTx($"rb10-{h}", "xRequestorJ", "rebuild:10", 0.1M, h));
            using var reached = new System.Threading.ManualResetEventSlim();
            using var resume = new System.Threading.ManualResetEventSlim();
            VBTCWithdrawalStoreRebuildService.BlockScannedForTests = height => { if (height == 1003) { reached.Set(); resume.Wait(5000); } };
            try
            {
                var rebuild = Task.Run(() => VBTCWithdrawalStoreRebuildService.RebuildFromChainAsync("test"));
                Assert.True(reached.Wait(5000));
                VerifiedXCore.Utilities.SnapshotRestoreUtility.SetRestoreRunningForTests(true);
                var restoreWaits = VBTCWithdrawalStoreRebuildService.WaitUntilStoppedAsync();
                await Task.Delay(200);
                Assert.False(restoreWaits.IsCompleted);   // the rebuild is still writing
                resume.Set();
                var result = await rebuild;
                await restoreWaits;
                Assert.False(result.Success);
                Assert.Contains("snapshot restore", result.Message);
                Assert.True(result.BlocksScanned < 10);
                Assert.False(VBTCWithdrawalStoreRebuildService.InProgress);
                Assert.True(VBTCWithdrawalStoreRebuildService.IsUnfinished);   // until the restore finishes rewriting the store
            }
            finally
            {
                VBTCWithdrawalStoreRebuildService.BlockScannedForTests = null;
                VerifiedXCore.Utilities.SnapshotRestoreUtility.SetRestoreRunningForTests(false);
            }
        }

        /// <summary>
        /// A rebuild that fails part-way leaves rows put back but not brought to how their withdrawals ended: the store
        /// stays treated as being rebuilt (withdrawal transactions refused, nothing signed, no snapshot) until a rebuild
        /// finishes, which the next start runs by itself.
        /// </summary>
        [Fact]
        public async Task ARebuildThatFailsPartWay_KeepsTheStoreTreatedAsRebuilding_UntilOneFinishes()
        {
            for (var h = 1100; h < 1105; h++) InsertBlock(h, RequestTx($"rb11-{h}", "xRequestorK", "rebuild:11", 0.1M, h));
            VBTCWithdrawalStoreRebuildService.FailAtBlockForTests = height => height == 1102;
            try
            {
                var failed = await VBTCWithdrawalStoreRebuildService.RebuildFromChainAsync("test");
                Assert.False(failed.Success);
                Assert.Contains("unfinished", failed.Message);
            }
            finally { VBTCWithdrawalStoreRebuildService.FailAtBlockForTests = null; }
            Assert.True(VBTCWithdrawalStoreRebuildService.IsRunning);
            Assert.True(VBTCWithdrawalStoreRebuildService.IsUnfinished);
            Assert.False(VBTCWithdrawalStoreRebuildService.InProgress);   // the operator can start the rebuild again
            Assert.Equal(1, VerifiedXCore.Services.BlockValidatorService.ValidateBlockSemaphore.CurrentCount);   // the lock is released; blocks are held by BlockValidatorService.ValidateBlock

            await VBTCWithdrawalStoreRebuildService.MaybeAutoRebuildOnStartupAsync();
            Assert.False(VBTCWithdrawalStoreRebuildService.IsRunning);
            Assert.True(VBTCWithdrawalStoreRebuildService.LastResult!.Success, VBTCWithdrawalStoreRebuildService.LastResult.Message);
            Assert.NotNull(VBTCWithdrawalRequest.GetByTransactionHash("rb11-1104", "rebuild:11"));
        }

        /// <summary>
        /// No block is judged or applied on a store a rebuild left unfinished (every check that reads the withdrawal table
        /// would judge it on rows not yet brought to how their withdrawals ended). Block validation holds the block and
        /// starts the rebuild again; once it finishes, blocks are no longer held.
        /// </summary>
        [Fact]
        public async Task BlockValidation_HoldsBlocksWhileTheStoreIsUnfinished_AndRebuildsIt()
        {
            InsertBlock(1800, RequestTx("rb18-req", "xRequestorR", "rebuild:18", 0.1M, 1800));
            VBTCWithdrawalStoreRebuildService.ResetRetryForTests();
            VBTCWithdrawalStoreRebuildService.FailAtBlockForTests = _ => true;
            try { Assert.False((await VBTCWithdrawalStoreRebuildService.RebuildFromChainAsync("test")).Success); }
            finally { VBTCWithdrawalStoreRebuildService.FailAtBlockForTests = null; }
            Assert.True(VBTCWithdrawalStoreRebuildService.IsUnfinished);

            var next = new Block { Height = 1801, Hash = "block-1801", PrevHash = "block-1800", Timestamp = TimeUtil.GetTime(), Transactions = new List<Transaction>() };
            Assert.False(await VerifiedXCore.Services.BlockValidatorService.ValidateBlock(next, true));
            Assert.True(VBTCWithdrawalStoreRebuildService.BlocksHeld >= 1);

            // The rebuild it started finishes; the store is whole again.
            for (var i = 0; i < 200 && (VBTCWithdrawalStoreRebuildService.InProgress || VBTCWithdrawalStoreRebuildService.IsUnfinished); i++)
                await Task.Delay(50);
            Assert.False(VBTCWithdrawalStoreRebuildService.IsUnfinished);
            Assert.NotNull(VBTCWithdrawalRequest.GetByTransactionHash("rb18-req", "rebuild:18"));

            var held = VBTCWithdrawalStoreRebuildService.BlocksHeld;
            await VerifiedXCore.Services.BlockValidatorService.ValidateBlock(next, true);   // judged on (and refused for its own reasons)
            Assert.Equal(held, VBTCWithdrawalStoreRebuildService.BlocksHeld);
            VBTCWithdrawalStoreRebuildService.ResetRetryForTests();
        }

        [Fact]
        public async Task WhileTheRebuildKeepsFailing_ItIsStartedAgainAtMostOncePerInterval()
        {
            InsertBlock(1900, RequestTx("rb19-req", "xRequestorS", "rebuild:19", 0.1M, 1900));
            VBTCWithdrawalStoreRebuildService.ResetRetryForTests();
            VBTCWithdrawalStoreRebuildService.FailAtBlockForTests = _ => true;
            try
            {
                Assert.False((await VBTCWithdrawalStoreRebuildService.RebuildFromChainAsync("test")).Success);
                var started = VBTCWithdrawalStoreRebuildService.RebuildsStarted;
                var next = new Block { Height = 1901, Hash = "block-1901", PrevHash = "block-1900", Timestamp = TimeUtil.GetTime(), Transactions = new List<Transaction>() };
                Assert.False(await VerifiedXCore.Services.BlockValidatorService.ValidateBlock(next, true));
                for (var i = 0; i < 200 && VBTCWithdrawalStoreRebuildService.RebuildsStarted == started; i++) await Task.Delay(50);
                for (var i = 0; i < 200 && VBTCWithdrawalStoreRebuildService.InProgress; i++) await Task.Delay(50);
                Assert.Equal(started + 1, VBTCWithdrawalStoreRebuildService.RebuildsStarted);   // started again, failed again

                for (var k = 0; k < 3; k++) Assert.False(await VerifiedXCore.Services.BlockValidatorService.ValidateBlock(next, true));
                await Task.Delay(300);
                Assert.Equal(started + 1, VBTCWithdrawalStoreRebuildService.RebuildsStarted);   // not again within the interval
                Assert.True(VBTCWithdrawalStoreRebuildService.IsUnfinished);
            }
            finally
            {
                VBTCWithdrawalStoreRebuildService.FailAtBlockForTests = null;
                VBTCWithdrawalStoreRebuildService.ResetRetryForTests();
            }
        }

        /// <summary>
        /// The SafeDB wrappers swallow a failed write (log it, return a default). A scope opened for the withdrawal store
        /// counts such a failure in its own work, and not one in other work.
        /// </summary>
        [Fact]
        public async Task ASwallowedWriteFailure_IsCountedByAScopeInTheSameWork_Only()
        {
            var requests = VBTCWithdrawalRequest.GetVBTCWithdrawalRequestDb()!;
            using var gate = new System.Threading.ManualResetEventSlim();
            var elsewhere = Task.Run(() => { gate.Wait(5000); requests.InsertSafe<VBTCWithdrawalRequest>((VBTCWithdrawalRequest)null!); });   // other work, begun before the scope
            using (var scope = new DbExtensions.FailureScope(VBTCWithdrawalStoreRebuildService.StoreCollections))
            {
                Assert.True(requests.InsertSafe<VBTCWithdrawalRequest>((VBTCWithdrawalRequest)null!) == null);   // fails inside the wrapper; swallowed
                gate.Set();
                await elsewhere;
                Assert.Equal(1, scope.Count);
            }
        }

        /// <summary>
        /// Helpers that answer "not watched" or "not found" when this node's storage fails count that as a lost write to
        /// the withdrawal store inside a block's apply (so the block is not taken as applied, and the store is marked);
        /// outside one (transaction validation) the answer is as before and nothing is counted.
        /// </summary>
        [Fact]
        public void AnAnswerGivenOnAStorageFault_CountsAsALostStoreWrite_InsideAnApplyOnly()
        {
            InsertBlock(2320, RequestTx("rb24-req", "xRequestorW", "rebuild:24", 0.1M, 2320));
            var cancel = CompleteTx("rb24-cancel", "xRequestorW", "rebuild:24", "rb24-req", 2321);
            cancel.TransactionType = TransactionType.VBTC_V2_WITHDRAWAL_CANCEL;
            var attest = DbContext.DB_BtcAttest;
            var requests = DbContext.DB_VBTCWithdrawalRequests;
            var closed = new LiteDB.LiteDatabase(new MemoryStream());
            closed.Dispose();
            try
            {
                DbContext.DB_BtcAttest = closed;
                Assert.False(VerifiedXCore.Bitcoin.Attestation.BtcWithdrawals.IsWatched(cancel));   // validation: as before
                using (var scope = new DbExtensions.FailureScope(VBTCWithdrawalStoreRebuildService.StoreCollections))
                {
                    Assert.False(VerifiedXCore.Bitcoin.Attestation.BtcWithdrawals.IsWatched(cancel));
                    Assert.Equal(1, scope.Count);
                }
                DbContext.DB_BtcAttest = attest;

                DbContext.DB_VBTCWithdrawalRequests = closed;
                using (var scope = new DbExtensions.FailureScope(VBTCWithdrawalStoreRebuildService.StoreCollections))
                {
                    Assert.Null(VBTCWithdrawalStoreRebuildService.TryRecoverRequestRowFromChain("rb24-req", 2321, "rebuild:24"));
                    Assert.True(scope.Count >= 1);
                }
            }
            finally
            {
                DbContext.DB_BtcAttest = attest;
                DbContext.DB_VBTCWithdrawalRequests = requests;
            }
        }

        /// <summary>A rebuild during which a write to the store was swallowed does not count as finished.</summary>
        [Fact]
        public async Task ARebuildDuringWhichAStoreWriteWasSwallowed_StaysUnfinished()
        {
            InsertBlock(2000, RequestTx("rb20-req", "xRequestorT", "rebuild:20", 0.1M, 2000));
            VBTCWithdrawalStoreRebuildService.BlockScannedForTests = _ => DbExtensions.NoteFailure(DbContext.RSRV_VBTC_WITHDRAWAL_REQUESTS);
            try
            {
                var result = await VBTCWithdrawalStoreRebuildService.RebuildFromChainAsync("test");
                Assert.False(result.Success);
                Assert.True(result.Errors > 0);
                Assert.True(VBTCWithdrawalStoreRebuildService.IsUnfinished);
            }
            finally { VBTCWithdrawalStoreRebuildService.BlockScannedForTests = null; }
            Assert.True((await VBTCWithdrawalStoreRebuildService.RebuildFromChainAsync("test, again")).Success);
            Assert.False(VBTCWithdrawalStoreRebuildService.IsUnfinished);
        }

        /// <summary>
        /// A block whose apply lost a write to the withdrawal store (swallowed by the storage) is not applied: this node's
        /// table would otherwise differ from its peers' with nothing to show for it. A failure elsewhere is not counted.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task ABlockWhoseApplyLostAWithdrawalStoreWrite_IsNotApplied(bool inTheStore)
        {
            var block = new Block { Height = 2100, Hash = "block-2100", PrevHash = "block-2099", Timestamp = TimeUtil.GetTime(), Transactions = new List<Transaction>() };
            StateData.ApplyStepForTests = _ => DbExtensions.NoteFailure(inTheStore ? DbContext.RSRV_VBTC_V2_CANCELLATIONS : "some_other_collection");
            try { Assert.Equal(!inTheStore, await StateData.UpdateTreis(block)); }
            finally { StateData.ApplyStepForTests = null; }
        }

        /// <summary>
        /// A state rebuild whose wipe of the withdrawal store fails (here the cancellations: their database cannot be
        /// written) fails, rather than replaying onto rows left from before and clearing the unfinished mark.
        /// </summary>
        [Fact]
        public async Task AStateRebuildThatCouldNotWipeTheWithdrawalStore_Fails_AndLeavesItUnfinished()
        {
            VBTCWithdrawalStoreRebuildService.SetUnfinishedForTests(true);
            var vbtc = DbContext.DB_vBTC;
            var brokenPath = Path.Combine(_tempRoot, "broken_vbtc.db");
            var broken = new LiteDB.LiteDatabase(new LiteDB.ConnectionString { Filename = brokenPath, Connection = LiteDB.ConnectionType.Direct });
            broken.Dispose();
            try
            {
                DbContext.DB_vBTC = broken;
                Assert.False(await BlockRollbackUtility.ResetTreis());
            }
            finally { DbContext.DB_vBTC = vbtc; }
            Assert.False(BlockRollbackUtility.WithdrawalStoreWiped);
            Assert.True(VBTCWithdrawalStoreRebuildService.IsUnfinished);
            // Blocks are held, and a rebuild that only adds rows does not lift that: only a full state rebuild does.
            VBTCWithdrawalStoreRebuildService.ResetRetryForTests();
            Assert.True(VBTCWithdrawalStoreRebuildService.HoldsBlocks());
            Assert.Equal(0, VBTCWithdrawalStoreRebuildService.BlocksHeld);   // held for the state rebuild, no merge rebuild started
            Assert.True((await VBTCWithdrawalStoreRebuildService.RebuildFromChainAsync("test")).Success);
            Assert.True(VBTCWithdrawalStoreRebuildService.HoldsBlocks());
            Assert.True(await BlockRollbackUtility.ResetTreis());
            Assert.False(VBTCWithdrawalStoreRebuildService.HoldsBlocks());
            Assert.False(VBTCWithdrawalStoreRebuildService.IsUnfinished);
        }

        /// <summary>A signer that cannot read the cancellations answers "pending" (gives no share); other readers keep their answer.</summary>
        [Fact]
        public void ASignerThatCannotReadTheCancellations_TreatsOneAsPending()
        {
            var vbtc = DbContext.DB_vBTC;
            var broken = new LiteDB.LiteDatabase(new LiteDB.ConnectionString { Filename = Path.Combine(_tempRoot, "broken_vbtc2.db"), Connection = LiteDB.ConnectionType.Direct });
            broken.Dispose();
            Assert.False(VBTCWithdrawalCancellation.HasPendingCancellationOrUnreadable("rb22-req", TimeUtil.GetTime(), "rebuild:22"));
            try
            {
                DbContext.DB_vBTC = broken;
                Assert.True(VBTCWithdrawalCancellation.HasPendingCancellationOrUnreadable("rb22-req", TimeUtil.GetTime(), "rebuild:22"));
                Assert.False(VBTCWithdrawalCancellation.HasPendingCancellation("rb22-req", TimeUtil.GetTime(), "rebuild:22"));
            }
            finally { DbContext.DB_vBTC = vbtc; }
        }

        /// <summary>
        /// End to end: a block's apply loses a write to the withdrawal store (a real write the storage wrapper swallowed).
        /// The block's apply is incomplete, the store is marked unfinished, block validation holds blocks and rebuilds
        /// the store, after which blocks are judged again.
        /// </summary>
        [Fact]
        public async Task ALiveApplyThatLostAStoreWrite_HoldsBlocksUntilTheStoreIsRebuilt()
        {
            InsertBlock(2200, RequestTx("rb22b-req", "xRequestorU", "rebuild:22", 0.1M, 2200));
            VBTCWithdrawalStoreRebuildService.ResetRetryForTests();
            var block = new Block { Height = 2201, Hash = "block-2201", PrevHash = "block-2200", Timestamp = TimeUtil.GetTime(), Transactions = new List<Transaction>() };
            StateData.ApplyStepForTests = _ => VBTCWithdrawalRequest.GetVBTCWithdrawalRequestDb()!.InsertSafe<VBTCWithdrawalRequest>((VBTCWithdrawalRequest)null!);
            try { Assert.False(await StateData.UpdateTreis(block)); }
            finally { StateData.ApplyStepForTests = null; }
            Assert.True(VBTCWithdrawalStoreRebuildService.IsUnfinished);

            var next = new Block { Height = 2202, Hash = "block-2202", PrevHash = "block-2201", Timestamp = TimeUtil.GetTime(), Transactions = new List<Transaction>() };
            Assert.False(await VerifiedXCore.Services.BlockValidatorService.ValidateBlock(next, true));
            Assert.True(VBTCWithdrawalStoreRebuildService.BlocksHeld >= 1);
            for (var i = 0; i < 200 && (VBTCWithdrawalStoreRebuildService.InProgress || VBTCWithdrawalStoreRebuildService.IsUnfinished); i++)
                await Task.Delay(50);
            Assert.False(VBTCWithdrawalStoreRebuildService.IsUnfinished);
            Assert.NotNull(VBTCWithdrawalRequest.GetByTransactionHash("rb22b-req", "rebuild:22"));
            Assert.False(VBTCWithdrawalStoreRebuildService.HoldsBlocks());
            VBTCWithdrawalStoreRebuildService.ResetRetryForTests();
        }

        /// <summary>
        /// A fault of this node's storage in a step that writes the withdrawal store marks it. A fault of what a
        /// transaction says does not, and neither does a local fault in a step that does not write the store (the block's
        /// apply fails, as before, but no rebuild of a store it did not touch holds blocks).
        /// </summary>
        [Theory]
        [InlineData(true, true, true)]
        [InlineData(true, false, false)]
        [InlineData(false, true, false)]
        public async Task OnlyALocalFaultInAWithdrawalStoreStep_MarksTheStore(bool storeStep, bool localFault, bool marked)
        {
            var block = new Block { Height = 2300, Hash = "block-2300", PrevHash = "block-2299", Timestamp = TimeUtil.GetTime(), Transactions = new List<Transaction>() };
            Exception fault = localFault ? new System.IO.IOException("disk") : new FormatException("data");
            StateData.ApplyStepForTests = h => { if (storeStep) StateData.NoteWithdrawalStoreStepFailed(h, fault); else StateData.NoteApplyStepFailed(h, fault); };
            try { Assert.Equal(!localFault, await StateData.UpdateTreis(block)); }
            finally { StateData.ApplyStepForTests = null; }
            Assert.Equal(marked, VBTCWithdrawalStoreRebuildService.IsUnfinished);
            if (marked) VBTCWithdrawalStoreRebuildService.SetUnfinishedForTests(false);
        }

        /// <summary>
        /// A mined COMPLETE or CANCEL whose read of the withdrawal store fails on this node (here: the store is closed) was
        /// caught and logged, and the block counted as applied with the row never written. Now the block's apply is
        /// incomplete and the store is marked unfinished.
        /// </summary>
        [Theory]
        [InlineData(TransactionType.VBTC_V2_WITHDRAWAL_COMPLETE)]
        [InlineData(TransactionType.VBTC_V2_WITHDRAWAL_CANCEL)]
        public async Task AWithdrawalStepThatCannotReadTheStore_FailsTheBlock_AndMarksTheStore(TransactionType type)
        {
            var tx = CompleteTx("rb23c-done", "xRequestorV", "rebuild:23", "rb23c-req", 2310);
            tx.TransactionType = type;
            var block = new Block { Height = 2310, Hash = "block-2310", PrevHash = "block-2309", Timestamp = TimeUtil.GetTime(), Transactions = new List<Transaction> { tx } };
            var store = DbContext.DB_VBTCWithdrawalRequests;
            var closed = new LiteDB.LiteDatabase(new MemoryStream());
            closed.Dispose();
            DbContext.DB_VBTCWithdrawalRequests = closed;
            bool applied;
            try { applied = await StateData.UpdateTreis(block); }
            finally { DbContext.DB_VBTCWithdrawalRequests = store; }
            Assert.False(applied);
            Assert.True(VBTCWithdrawalStoreRebuildService.IsUnfinished);
            VBTCWithdrawalStoreRebuildService.SetUnfinishedForTests(false);
        }

        /// <summary>A state rebuild whose replay lost a write to the withdrawal store does not clear it; it stays unfinished.</summary>
        [Fact]
        public async Task AStateRebuildWhoseReplayLostAStoreWrite_LeavesItUnfinished()
        {
            for (long h = 2400; h < 2403; h++)
                BlockchainData.GetBlocks().InsertSafe(new Block { Height = h, Hash = $"block-{h}", PrevHash = $"block-{h - 1}", Timestamp = 1_755_000_000L + h, Transactions = new List<Transaction>(), NumOfTx = 0 });
            StateData.ApplyStepForTests = h => { if (h == 2401) DbExtensions.NoteFailure(DbContext.RSRV_VBTC_WITHDRAWAL_REQUESTS); };
            try { Assert.False(await BlockRollbackUtility.ResetTreis()); }
            finally { StateData.ApplyStepForTests = null; }
            Assert.True(VBTCWithdrawalStoreRebuildService.IsUnfinished);
            VBTCWithdrawalStoreRebuildService.SetUnfinishedForTests(false);
        }

        [Fact]
        public void WithNoWithdrawalStoreOpen_BlocksAreHeld()
        {
            var store = DbContext.DB_VBTCWithdrawalRequests;
            try
            {
                DbContext.DB_VBTCWithdrawalRequests = null!;
                Assert.True(VBTCWithdrawalStoreRebuildService.HoldsBlocks());
            }
            finally { DbContext.DB_VBTCWithdrawalRequests = store; }
            Assert.False(VBTCWithdrawalStoreRebuildService.HoldsBlocks());
        }

        /// <summary>A state rebuild that finishes rewrites the store from the chain: an unfinished withdrawal-store rebuild no longer holds it.</summary>
        [Fact]
        public async Task AFinishedStateRebuild_ClearsAnUnfinishedWithdrawalStoreRebuild()
        {
            InsertBlock(1400, RequestTx("rb14-req", "xRequestorN", "rebuild:14", 0.1M, 1400));
            VBTCWithdrawalStoreRebuildService.FailAtBlockForTests = _ => true;
            try { Assert.False((await VBTCWithdrawalStoreRebuildService.RebuildFromChainAsync("test")).Success); }
            finally { VBTCWithdrawalStoreRebuildService.FailAtBlockForTests = null; }
            Assert.True(VBTCWithdrawalStoreRebuildService.IsUnfinished);

            BlockchainData.GetBlocks().DeleteAllSafe();   // nothing to replay: the state rebuild finishes cleanly
            Assert.True(await BlockRollbackUtility.ResetTreis());
            Assert.False(VBTCWithdrawalStoreRebuildService.IsUnfinished);
            Assert.False(VBTCWithdrawalStoreRebuildService.IsRunning);
        }

        /// <summary>
        /// Data the chain carries that cannot be read is skipped, as live processing skipped it: it does not leave the
        /// store unfinished (one such transaction in history would otherwise hold every node's store unfinished).
        /// </summary>
        [Fact]
        public async Task UnreadableDataInHistory_IsSkipped_AndTheRebuildFinishes()
        {
            InsertBlock(1500, RequestTx("rb15-req", "xRequestorO", "rebuild:15", 0.1M, 1500));
            var bad = CompleteTx("rb15-bad", "xRequestorO", "rebuild:15", "rb15-req", 1510);
            bad.Data = "{not json";
            InsertBlock(1510, bad);
            var result = await VBTCWithdrawalStoreRebuildService.RebuildFromChainAsync("test");
            Assert.True(result.Success, result.Message);
            Assert.Equal(0, result.Errors);
            Assert.True(result.Skipped >= 1);
            Assert.False(VBTCWithdrawalStoreRebuildService.IsUnfinished);
        }

        /// <summary>A request live processing never wrote a row for (a non-positive amount, VX-01) gets none from the rebuild either.</summary>
        [Fact]
        public async Task ARequestLiveProcessingRefused_IsNotPutBack()
        {
            InsertBlock(1700, RequestTx("rb17-zero", "xRequestorQ", "rebuild:17", 0M, 1700));
            InsertBlock(1710, RequestTx("rb17-req", "xRequestorQ", "rebuild:17", 0.1M, 1710));
            var result = await VBTCWithdrawalStoreRebuildService.RebuildFromChainAsync("test");
            Assert.True(result.Success, result.Message);
            Assert.Null(VBTCWithdrawalRequest.GetByTransactionHash("rb17-zero", "rebuild:17"));
            Assert.NotNull(VBTCWithdrawalRequest.GetByTransactionHash("rb17-req", "rebuild:17"));
        }

        /// <summary>
        /// A mined request whose fields validation never reads carries a value of the wrong shape (any holder can send
        /// one): live processing skips it, and so does the rebuild, which still finishes.
        /// </summary>
        [Fact]
        public async Task ARequestWithAFieldOfTheWrongShape_IsSkipped_AndTheRebuildFinishes()
        {
            var odd = RequestTx("rb16-odd", "xRequestorP", "rebuild:16", 0.1M, 1600);
            odd.Data = odd.Data!.TrimEnd('}') + ",\"OriginalRequestTime\":\"x\"}";
            InsertBlock(1600, odd);
            InsertBlock(1610, RequestTx("rb16-req", "xRequestorP", "rebuild:16", 0.2M, 1610));
            var result = await VBTCWithdrawalStoreRebuildService.RebuildFromChainAsync("test");
            Assert.True(result.Success, result.Message);
            Assert.Equal(0, result.Errors);
            Assert.False(VBTCWithdrawalStoreRebuildService.IsUnfinished);
            Assert.NotNull(VBTCWithdrawalRequest.GetByTransactionHash("rb16-req", "rebuild:16"));
        }

        /// <summary>
        /// A state rebuild that starts while the withdrawal store is rebuilt waits for the rebuild to stop (it does at its
        /// next block) before it rewrites the store.
        /// </summary>
        [Fact]
        public async Task AStateRebuildStartingMidRebuild_WaitsForItToStop()
        {
            for (var h = 1200; h < 1210; h++) InsertBlock(h, RequestTx($"rb12-{h}", "xRequestorL", "rebuild:12", 0.1M, h));
            using var reached = new System.Threading.ManualResetEventSlim();
            using var resume = new System.Threading.ManualResetEventSlim();
            VBTCWithdrawalStoreRebuildService.BlockScannedForTests = height => { if (height == 1203) { reached.Set(); resume.Wait(5000); } };
            try
            {
                var rebuild = Task.Run(() => VBTCWithdrawalStoreRebuildService.RebuildFromChainAsync("test"));
                Assert.True(reached.Wait(5000));
                var reset = Task.Run(() => BlockRollbackUtility.ResetTreis());
                await Task.Delay(300);
                Assert.False(reset.IsCompleted);   // waits: the rebuild is still writing
                Assert.NotNull(VBTCWithdrawalRequest.GetByTransactionHash("rb12-1201", "rebuild:12"));   // and has not wiped the store
                Assert.True(BlockRollbackUtility.IsResetTreisRunning);
                resume.Set();
                var result = await rebuild;
                Assert.False(result.Success);
                Assert.Contains("state rebuild", result.Message);
                Assert.True(result.BlocksScanned < 10);
                Assert.Same(reset, await Task.WhenAny(reset, Task.Delay(60_000)));
                Assert.False(VBTCWithdrawalStoreRebuildService.IsRunning);   // the state rebuild rewrites the store itself
            }
            finally { VBTCWithdrawalStoreRebuildService.BlockScannedForTests = null; }
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
