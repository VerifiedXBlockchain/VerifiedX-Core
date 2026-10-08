using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Fund-loss audit item 8 (validator-local): a Completed withdrawal whose Bitcoin transaction has not confirmed is
    /// not added back to the owner's spendable balance at admission and proposal - the deposit still holds the coin,
    /// so the add-back overstated the owner by the withdrawn amount for as long as the transaction was withheld.
    /// </summary>
    [Collection("DbContextSequential")]
    public class FundLoss08_CompletedUnconfirmedWithdrawalTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Func<string, Task<int?>> _priorLookup;
        private readonly string _vault = "vault:" + Guid.NewGuid().ToString("N");

        public FundLoss08_CompletedUnconfirmedWithdrawalTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"fl08_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorLookup = CompletedWithdrawalConfirmation.LookupConfirmations;
            CompletedWithdrawalConfirmation.ResetForTests();
            DbContext.Initialize();
        }

        public void Dispose()
        {
            CompletedWithdrawalConfirmation.LookupConfirmations = _priorLookup;
            CompletedWithdrawalConfirmation.ResetForTests();
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private void Row(string txid, decimal amount, VBTCWithdrawalStatus status, string? btcTxId)
        {
            VBTCWithdrawalRequest.Save(new VBTCWithdrawalRequest
            {
                RequestorAddress = "xOwner", OriginalUniqueId = Guid.NewGuid().ToString("N"), SmartContractUID = _vault,
                TransactionHash = txid, Amount = amount, BTCDestination = "tb1qdest", FeeRate = 10, Timestamp = TimeUtil.GetTime(),
                RequestBlockHeight = 100, Status = status, IsCompleted = status != VBTCWithdrawalStatus.Requested, BTCTxHash = btcTxId,
            });
        }

        private static Dictionary<string, int?> Answers(params (string Txid, int? Confirmations)[] a)
        {
            var d = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (t, c) in a) d[t] = c;
            return d;
        }

        [Fact]
        public async Task UnconfirmedCompletedWithdrawals_AreNotAddedBack_ConfirmedOnesAre()
        {
            var answers = Answers(("btc-confirmed", 3), ("btc-mempool", 0), ("btc-unknown", null));
            var asked = new List<string>();
            CompletedWithdrawalConfirmation.LookupConfirmations = txid => { asked.Add(txid); return Task.FromResult(answers[txid]); };

            var candidates = new List<(string, decimal)> { ("btc-confirmed", 0.5M), ("btc-mempool", 0.3M) };
            Assert.Equal(0.3M, await CompletedWithdrawalConfirmation.UnconfirmedAmountAsync(candidates));

            // A withheld transaction no server knows: no answer, and the caller fails closed.
            candidates.Add(("btc-unknown", 0.2M));
            Assert.Null(await CompletedWithdrawalConfirmation.UnconfirmedAmountAsync(candidates));

            // Confirmed once, never asked again.
            asked.Clear();
            await CompletedWithdrawalConfirmation.UnconfirmedAmountAsync(new List<(string, decimal)> { ("btc-confirmed", 0.5M) });
            Assert.Empty(asked);

            Assert.Equal(0M, await CompletedWithdrawalConfirmation.UnconfirmedAmountAsync(new List<(string, decimal)>()));
        }

        [Fact]
        public void Candidates_AreTheCompletedRowsTheAddBackCounts()
        {
            Row("H1", 0.5M, VBTCWithdrawalStatus.Completed, "btc-1");
            Row("H2", 0.3M, VBTCWithdrawalStatus.Completed, "btc-2");
            Row("H3", 0.9M, VBTCWithdrawalStatus.Cancelled, null);   // no burn: never added back, never a candidate
            Row("H4", 0.7M, VBTCWithdrawalStatus.Requested, null);
            var height = Globals.V2WithdrawalOwnerAddBackFixHeight + 1;
            var candidates = CompletedWithdrawalConfirmation.Candidates(_vault, height);
            Assert.Equal(2, candidates.Count);
            Assert.Contains(("btc-1", 0.5M), candidates);
            Assert.Contains(("btc-2", 0.3M), candidates);
            Assert.Equal(0.8M, VBTCWithdrawalRequest.GetCompletedWithdrawalAmount("xOwner", _vault, height));
        }

        [Fact]
        public async Task OwnerDepositCheck_RequiresTheDepositToCoverTheUnconfirmedWithdrawal()
        {
            Row("H5", 0.4M, VBTCWithdrawalStatus.Completed, "btc-withheld");
            CompletedWithdrawalConfirmation.LookupConfirmations = _ => Task.FromResult<int?>(0);
            var height = Globals.V2WithdrawalOwnerAddBackFixHeight + 1;

            using var electrum = new FakeElectrum(("one.test", true, 1.0M));
            // The owner ledger (with the 0.4 add-back) leaves 0.8 for the deposit to cover; the deposit shows 1.0 but
            // 0.4 of it is the withheld withdrawal, so only 0.6 is really spendable.
            var withRule = await VbtcOwnerDeposit.CheckAsync(() => "tb1qdeposit", 0.8M, false, false, _vault, height);
            Assert.Equal(OwnerDepositStatus.Checked, withRule.Status);
            Assert.Equal(0.6M, withRule.DepositBalance);

            var before = await VbtcOwnerDeposit.CheckAsync(() => "tb1qdeposit", 0.8M, false, false);
            Assert.Equal(1.0M, before.DepositBalance); // the hole: the full deposit counted

            CompletedWithdrawalConfirmation.LookupConfirmations = _ => Task.FromResult<int?>(null);
            var noAnswer = await VbtcOwnerDeposit.CheckAsync(() => "tb1qdeposit", 0.8M, false, false, _vault, height);
            Assert.Equal(OwnerDepositStatus.Unverifiable, noAnswer.Status); // fail closed, like an unreadable deposit

            var blockValidation = await VbtcOwnerDeposit.CheckAsync(() => "tb1qdeposit", 0.8M, false, true, _vault, height);
            Assert.Equal(OwnerDepositStatus.NotQueried, blockValidation.Status); // block validation untouched
        }
    }
}
