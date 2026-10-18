using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VerifiedXCore.Data;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Receivers, the producer's own check of its block, and the proposal's closing passes read a block's transactions in
    /// one order (by sender, then nonce): the per-block guards keep running state, so their verdicts depend on it.
    /// </summary>
    [Collection("DbContextSequential")]
    public class ValidationOrderTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;

        public ValidationOrderTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"validation_order_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            foreach (var a in new[] { "xA", "xM", "xZ" })
                StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = a, Balance = 100M, Nonce = 0 });
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static Transaction Tx(string from, long nonce) => new() { Hash = $"{from}-{nonce}", FromAddress = from, ToAddress = "xTo", Nonce = nonce, TransactionType = TransactionType.TX, Amount = 1M };

        [Fact]
        public void TheValidationOrder_IsBySenderThenNonce()
        {
            var order = BlockValidatorService.InValidationOrder(new[] { Tx("xZ", 0), Tx("xA", 1), Tx("xA", 0), Tx("xM", 4) });
            Assert.Equal(new[] { "xA-0", "xA-1", "xM-4", "xZ-0" }, order.Select(t => t.Hash));
        }

        [Fact]
        public void TheProposalsClosingPasses_JudgeAndReturnTheBlockInTheValidationOrder()
        {
            var proposal = new List<Transaction> { Tx("xZ", 0), Tx("xA", 1), Tx("xA", 0) };

            var kept = TransactionData.ClosingPasses(proposal);

            Assert.Equal(new[] { "xA-0", "xA-1", "xZ-0" }, kept.Select(t => t.Hash));
        }
    }
}
