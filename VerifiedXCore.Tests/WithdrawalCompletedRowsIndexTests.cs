using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Data;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// The owner's add-back reads a vault's Completed withdrawal rows. A vault's requested and cancelled rows are never
    /// deleted, so they are not read for it: the Completed rows come from their own index, exactly the rows and order a
    /// read of every row of the vault gives.
    /// </summary>
    [Collection("DbContextSequential")]
    public class WithdrawalCompletedRowsIndexTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly string _vault = Guid.NewGuid().ToString("N") + ":1790000000";

        public WithdrawalCompletedRowsIndexTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"wrindex_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static VBTCWithdrawalRequest Row(string uid, VBTCWithdrawalStatus status, bool completed, decimal amount, string requester = "xRequester") => new()
        {
            RequestorAddress = requester, SmartContractUID = uid, Amount = amount, Status = status, IsCompleted = completed,
            TransactionHash = Guid.NewGuid().ToString("N"), OriginalUniqueId = Guid.NewGuid().ToString("N"), BTCDestination = "bc1q", OriginalSignature = "s",
        };

        /// <summary>What the owner's add-back read before: every row of the vault, then the Completed ones.</summary>
        private static List<VBTCWithdrawalRequest> EveryRowThenCompleted(string uid) =>
            VBTCWithdrawalRequest.RowsOfContract(VBTCWithdrawalRequest.GetVBTCWithdrawalRequestDb()!, uid)
                .Where(x => x.IsCompleted && x.Status == VBTCWithdrawalStatus.Completed).ToList();

        [Fact]
        public void TheCompletedRows_AreTheSameRowsInTheSameOrder_AndTheVaultsOtherRowsAreNotRead()
        {
            var col = VBTCWithdrawalRequest.GetVBTCWithdrawalRequestDb()!;
            var longUid = new string('L', 250) + ":1790000000";
            var rows = new List<VBTCWithdrawalRequest>();
            // Requested and cancelled, as request-and-cancel leaves them.
            rows.AddRange(Enumerable.Range(0, 60_000).Select(i => Row(_vault, i % 2 == 0 ? VBTCWithdrawalStatus.Cancelled : VBTCWithdrawalStatus.Requested, i % 2 == 0, 0.000007M)));
            // Completed rows of the vault, between them.
            rows.InsertRange(10, new[] { Row(_vault, VBTCWithdrawalStatus.Completed, true, 0.01M, "xOther") });
            rows.InsertRange(30_000, new[] { Row(_vault, VBTCWithdrawalStatus.Completed, true, 0.02M) });
            rows.Add(Row(_vault.ToUpperInvariant(), VBTCWithdrawalStatus.Completed, true, 0.04M));        // the same UID as the database compares it
            rows.Add(Row(_vault, VBTCWithdrawalStatus.Completed, false, 0.08M));                          // not completed: not counted
            rows.Add(Row(_vault, VBTCWithdrawalStatus.Cancelled, true, 0.16M));                           // completed by a cancellation: not counted
            rows.Add(Row("other:1790000000", VBTCWithdrawalStatus.Completed, true, 0.32M));               // another vault
            rows.Add(Row(longUid, VBTCWithdrawalStatus.Completed, true, 0.64M));                          // a long UID, outside the index
            col.InsertBulk(rows);
            // A row completed after it was stored: the index follows the update.
            var later = Row(_vault, VBTCWithdrawalStatus.Requested, false, 1.28M);
            col.Insert(later);
            later.Status = VBTCWithdrawalStatus.Completed; later.IsCompleted = true;
            col.Update(later);

            Assert.Equal(EveryRowThenCompleted(_vault).Select(x => x.Id), VBTCWithdrawalRequest.CompletedRowsOfContract(col, _vault).Select(x => x.Id));
            Assert.Equal(EveryRowThenCompleted(longUid).Select(x => x.Id), VBTCWithdrawalRequest.CompletedRowsOfContract(col, longUid).Select(x => x.Id));
            Assert.Equal(0.01M + 0.02M + 0.04M + 1.28M, VBTCWithdrawalRequest.GetCompletedWithdrawalAmount("xRequester", _vault, Globals.V2WithdrawalOwnerAddBackFixHeight));
            Assert.Equal(0.64M, VBTCWithdrawalRequest.GetCompletedWithdrawalAmount("xRequester", longUid, Globals.V2WithdrawalOwnerAddBackFixHeight));

            // The vault's 60,000 other rows are not read: as fast as for a vault that has none.
            VBTCWithdrawalRequest.GetCompletedWithdrawalAmount("xRequester", _vault, Globals.V2WithdrawalOwnerAddBackFixHeight); // warm
            var timer = Stopwatch.StartNew();
            for (var i = 0; i < 10; i++)
                VBTCWithdrawalRequest.GetCompletedWithdrawalAmount("xRequester", _vault, Globals.V2WithdrawalOwnerAddBackFixHeight);
            Assert.True(timer.ElapsedMilliseconds < 500, $"10 reads took {timer.ElapsedMilliseconds} ms");
        }
    }
}
