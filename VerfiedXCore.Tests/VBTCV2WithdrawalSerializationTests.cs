using System;
using System.IO;
using LiteDB;
using ReserveBlockCore.Data;
using ReserveBlockCore.Bitcoin.Models;
using Xunit;

namespace VerfiedXCore.Tests
{
    /// <summary>
    /// S3C §0 prerequisite — per-contract withdrawal serialization.
    /// Exercises the real VBTCWithdrawalRequest.HasActiveContractRequest gate (DB query +
    /// anti-grief expiry) and the RequestBlockHeight persistence in Save. The three consensus
    /// gate sites (TransactionValidatorService, BlockTransactionValidatorService, VBTCController)
    /// all delegate the decision to this helper, so testing it covers the rule itself.
    /// </summary>
    public class VBTCV2WithdrawalSerializationTests : IDisposable
    {
        private readonly string _dbPath;
        private const string ScA = "vbtc-contract-A";
        private const string ScB = "vbtc-contract-B";

        public VBTCV2WithdrawalSerializationTests()
        {
            // Point the withdrawal-request collection at an isolated temp DB for this instance.
            _dbPath = Path.Combine(Path.GetTempPath(), $"vbtc_wr_test_{Guid.NewGuid():N}.db");
            DbContext.DB_VBTCWithdrawalRequests = new LiteDatabase(
                new ConnectionString { Filename = _dbPath, Connection = ConnectionType.Direct });
        }

        public void Dispose()
        {
            try { DbContext.DB_VBTCWithdrawalRequests?.Dispose(); } catch { }
            DbContext.DB_VBTCWithdrawalRequests = null;
            try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        }

        private static ILiteCollection<VBTCWithdrawalRequest> Col()
            => DbContext.DB_VBTCWithdrawalRequests.GetCollection<VBTCWithdrawalRequest>(
                DbContext.RSRV_VBTC_WITHDRAWAL_REQUESTS);

        private static void InsertIncomplete(string scUID, string requester, long requestBlockHeight, string txHash)
        {
            Col().Insert(new VBTCWithdrawalRequest
            {
                RequestorAddress = requester,
                SmartContractUID = scUID,
                OriginalUniqueId = txHash,
                TransactionHash = txHash,
                Status = VBTCWithdrawalStatus.Requested,
                IsCompleted = false,
                RequestBlockHeight = requestBlockHeight
            });
        }

        // A not-yet-mined request (RequestBlockHeight == 0) must block — fail toward locked so a
        // second request can't slip the mempool race before the first is mined.
        [Fact]
        public void UnsetHeight_Blocks()
        {
            InsertIncomplete(ScA, "addr1", requestBlockHeight: 0, txHash: "tx-unset");
            Assert.True(VBTCWithdrawalRequest.HasActiveContractRequest(ScA, currentHeight: 5000));
        }

        // Inside the expiry window (boundary is inclusive: currentHeight - height <= EXPIRY_BLOCKS).
        [Fact]
        public void WithinExpiryWindow_Blocks()
        {
            InsertIncomplete(ScA, "addr1", requestBlockHeight: 1000, txHash: "tx-fresh");
            Assert.True(VBTCWithdrawalRequest.HasActiveContractRequest(ScA, currentHeight: 1000 + 1));
            Assert.True(VBTCWithdrawalRequest.HasActiveContractRequest(
                ScA, currentHeight: 1000 + VBTCWithdrawalRequest.EXPIRY_BLOCKS)); // exact boundary
        }

        // One block past the window → non-blocking; the next valid request may overwrite it.
        [Fact]
        public void PastExpiry_DoesNotBlock()
        {
            InsertIncomplete(ScA, "addr1", requestBlockHeight: 1000, txHash: "tx-stale");
            Assert.False(VBTCWithdrawalRequest.HasActiveContractRequest(
                ScA, currentHeight: 1000 + VBTCWithdrawalRequest.EXPIRY_BLOCKS + 1));
        }

        // A completed/cancelled request frees the slot immediately (cancellation-vote recovery).
        [Fact]
        public void CompletedRequest_DoesNotBlock()
        {
            Col().Insert(new VBTCWithdrawalRequest
            {
                RequestorAddress = "addr1",
                SmartContractUID = ScA,
                OriginalUniqueId = "tx-done",
                TransactionHash = "tx-done",
                Status = VBTCWithdrawalStatus.Completed,
                IsCompleted = true,
                RequestBlockHeight = 1000
            });
            Assert.False(VBTCWithdrawalRequest.HasActiveContractRequest(ScA, currentHeight: 1000));
        }

        // Per-CONTRACT isolation: an active request on one contract must not lock another
        // (existing single-owner / unrelated-contract behavior unaffected).
        [Fact]
        public void OtherContract_DoesNotBlock()
        {
            InsertIncomplete(ScA, "addr1", requestBlockHeight: 1000, txHash: "tx-A");
            Assert.False(VBTCWithdrawalRequest.HasActiveContractRequest(ScB, currentHeight: 1000));
        }

        // Concurrent multi-holder: once ANY holder has an active request, the contract is locked
        // for every other holder (this is the per-contract gate the validators reject on).
        [Fact]
        public void ConcurrentMultiHolder_OneLocksContract()
        {
            InsertIncomplete(ScA, "holderB", requestBlockHeight: 1000, txHash: "tx-B");
            // holderC / holderD would be rejected because the gate is contract-scoped, not per-user.
            Assert.True(VBTCWithdrawalRequest.HasActiveContractRequest(ScA, currentHeight: 1010));
        }

        // Save must persist RequestBlockHeight when StateData stamps it at mine time, and must
        // never zero it back out on a later completion/cancellation save (RequestBlockHeight == 0).
        [Fact]
        public void Save_PersistsMinedHeight_AndNeverZeroesIt()
        {
            // Submit-time insert: height unset (0).
            Assert.True(VBTCWithdrawalRequest.Save(new VBTCWithdrawalRequest
            {
                RequestorAddress = "addr1",
                SmartContractUID = ScA,
                OriginalUniqueId = "uid-1",
                TransactionHash = "tx-1",
                Status = VBTCWithdrawalStatus.Requested,
                IsCompleted = false,
                RequestBlockHeight = 0
            }));

            // Mine-time update: StateData stamps the true height.
            Assert.True(VBTCWithdrawalRequest.Save(new VBTCWithdrawalRequest
            {
                RequestorAddress = "addr1",
                SmartContractUID = ScA,
                OriginalUniqueId = "uid-1",
                TransactionHash = "tx-1",
                Status = VBTCWithdrawalStatus.Requested,
                IsCompleted = false,
                RequestBlockHeight = 500
            }, update: true));
            Assert.Equal(500, VBTCWithdrawalRequest.GetByTransactionHash("tx-1")!.RequestBlockHeight);

            // Completion update carries no height (0) — must NOT overwrite the stamped 500.
            Assert.True(VBTCWithdrawalRequest.Save(new VBTCWithdrawalRequest
            {
                RequestorAddress = "addr1",
                SmartContractUID = ScA,
                OriginalUniqueId = "uid-1",
                TransactionHash = "tx-1",
                Status = VBTCWithdrawalStatus.Completed,
                IsCompleted = true,
                RequestBlockHeight = 0
            }, update: true));

            var final = VBTCWithdrawalRequest.GetByTransactionHash("tx-1")!;
            Assert.Equal(500, final.RequestBlockHeight);
            Assert.True(final.IsCompleted);
        }
    }
}
