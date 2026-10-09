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
using static VerifiedXCore.Bitcoin.Services.CompletedWithdrawalConfirmation;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Fund-loss audit item 8 (validator-local): a Completed withdrawal is added back to the owner's spendable balance at
    /// admission and proposal only once its Bitcoin transaction is confirmed - and (re-audit, 9 Oct 2026) only when that
    /// confirmed transaction is the withdrawal's: it spends the vault's deposit address and pays the destination. COMPLETE
    /// names any 64-hex txid, so a confirmed but unrelated transaction must not unlock the add-back.
    /// </summary>
    [Collection("DbContextSequential")]
    public class FundLoss08_CompletedUnconfirmedWithdrawalTests : IDisposable
    {
        private const string Deposit = "tb1qdeposit";
        private const string Dest = "tb1qdest";
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Func<string, Task<BtcTxFacts?>> _priorLookup;
        private readonly Func<string, string?> _priorResolve;
        private readonly string _vault = "vault:" + Guid.NewGuid().ToString("N");

        public FundLoss08_CompletedUnconfirmedWithdrawalTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"fl08_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorLookup = LookupTransaction;
            _priorResolve = ResolveDepositAddress;
            ResolveDepositAddress = _ => Deposit;
            ResetForTests();
            DbContext.Initialize();
        }

        public void Dispose()
        {
            LookupTransaction = _priorLookup;
            ResolveDepositAddress = _priorResolve;
            ResetForTests();
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private void Row(string txid, decimal amount, VBTCWithdrawalStatus status, string? btcTxId)
        {
            VBTCWithdrawalRequest.Save(new VBTCWithdrawalRequest
            {
                RequestorAddress = "xOwner", OriginalUniqueId = Guid.NewGuid().ToString("N"), SmartContractUID = _vault,
                TransactionHash = txid, Amount = amount, BTCDestination = Dest, FeeRate = 10, Timestamp = TimeUtil.GetTime(),
                RequestBlockHeight = 100, Status = status, IsCompleted = status != VBTCWithdrawalStatus.Requested, BTCTxHash = btcTxId,
            });
        }

        /// <summary>Facts for a transaction: confirmations, the addresses it spends from, and where it pays.</summary>
        private static BtcTxFacts Facts(int confirmations, string[] inputs, params (string Address, decimal Btc)[] outputs)
        {
            var f = new BtcTxFacts { Confirmations = confirmations };
            foreach (var i in inputs) f.InputAddresses.Add(i);
            foreach (var o in outputs) f.Outputs.Add(o);
            return f;
        }

        private static BtcTxFacts Withdrawal(int confirmations, decimal paid) => Facts(confirmations, new[] { Deposit }, (Dest, paid), (Deposit, 0.1M));

        private static Candidate C(string txid, decimal amount) => new(txid, amount, Dest, "vault");

        private static Dictionary<string, BtcTxFacts?> Answers(params (string Txid, BtcTxFacts? Facts)[] a)
        {
            var d = new Dictionary<string, BtcTxFacts?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (t, f) in a) d[t] = f;
            return d;
        }

        [Fact]
        public async Task UnconfirmedCompletedWithdrawals_AreNotAddedBack_ConfirmedOnesAre()
        {
            var answers = Answers(("btc-confirmed", Withdrawal(3, 0.49M)), ("btc-mempool", Facts(0, Array.Empty<string>())), ("btc-unknown", null));
            var asked = new List<string>();
            LookupTransaction = txid => { asked.Add(txid); return Task.FromResult(answers[txid]); };

            var candidates = new List<Candidate> { C("btc-confirmed", 0.5M), C("btc-mempool", 0.3M) };
            Assert.Equal(0.3M, await NotYetCountedAmountAsync(candidates, Deposit));

            // A withheld transaction no server knows: no answer, and the caller fails closed.
            candidates.Add(C("btc-unknown", 0.2M));
            Assert.Null(await NotYetCountedAmountAsync(candidates, Deposit));

            // Verified once, never asked again.
            asked.Clear();
            await NotYetCountedAmountAsync(new List<Candidate> { C("btc-confirmed", 0.5M) }, Deposit);
            Assert.Empty(asked);

            Assert.Equal(0M, await NotYetCountedAmountAsync(new List<Candidate>(), Deposit));
        }

        /// <summary>Re-audit: COMPLETE accepts any txid, so a confirmed transaction that is not this withdrawal's never unlocks the add-back.</summary>
        [Fact]
        public async Task AConfirmedTransactionThatIsNotTheWithdrawals_IsNotCounted()
        {
            var answers = Answers(
                ("btc-strangers", Facts(10, new[] { "tb1qsomeoneelse" }, ("tb1qwherever", 1.0M))),   // any confirmed txid on the chain
                ("btc-deposit-in", Facts(10, new[] { "tb1qsomeoneelse" }, (Deposit, 0.5M))),         // a deposit INTO the vault
                ("btc-wrong-dest", Facts(10, new[] { Deposit }, ("tb1qattacker", 0.5M))),           // from the vault, to the wrong place
                ("btc-zero-pay", Facts(10, new[] { Deposit }, (Dest, 0M))),                         // the destination named but unpaid
                ("btc-real", Withdrawal(10, 0.49M)));
            LookupTransaction = txid => Task.FromResult(answers[txid]);

            Assert.Equal(0.5M, await NotYetCountedAmountAsync(new[] { C("btc-strangers", 0.5M) }, Deposit));
            Assert.Equal(0.5M, await NotYetCountedAmountAsync(new[] { C("btc-deposit-in", 0.5M) }, Deposit));
            Assert.Equal(0.5M, await NotYetCountedAmountAsync(new[] { C("btc-wrong-dest", 0.5M) }, Deposit));
            Assert.Equal(0.5M, await NotYetCountedAmountAsync(new[] { C("btc-zero-pay", 0.5M) }, Deposit));
            Assert.Equal(0M, await NotYetCountedAmountAsync(new[] { C("btc-real", 0.5M) }, Deposit));

            // Without a known deposit address nothing can be verified: the add-back waits (not a no-answer: the chain answered).
            ResetForTests();
            Assert.Equal(0.5M, await NotYetCountedAmountAsync(new[] { C("btc-real", 0.5M) }, null));
            // A candidate with no destination likewise.
            Assert.Equal(0.5M, await NotYetCountedAmountAsync(new[] { new Candidate("btc-real", 0.5M, null, "vault") }, Deposit));
        }

        [Fact]
        public void TheBindingRule_IsPure_AndBech32CaseInsensitive()
        {
            Assert.True(IsTheWithdrawalsTransaction(Withdrawal(1, 0.4M), Dest, Deposit, out _));
            Assert.True(IsTheWithdrawalsTransaction(Withdrawal(1, 0.4M), Dest.ToUpperInvariant(), Deposit.ToUpperInvariant(), out _));
            Assert.False(IsTheWithdrawalsTransaction(Withdrawal(1, 0.4M), "tb1qother", Deposit, out var r1));
            Assert.Contains("destination", r1);
            Assert.False(IsTheWithdrawalsTransaction(Withdrawal(1, 0.4M), Dest, "tb1qothervault", out var r2));
            Assert.Contains("deposit", r2);
            Assert.False(IsTheWithdrawalsTransaction(Withdrawal(1, 0.4M), Dest, "", out _));
            Assert.False(IsTheWithdrawalsTransaction(Withdrawal(1, 0.4M), null, Deposit, out _));
            // Base58 addresses compare exactly.
            var legacy = Facts(1, new[] { "mkLegacyVault" }, ("mkLegacyDest", 0.1M));
            Assert.True(IsTheWithdrawalsTransaction(legacy, "mkLegacyDest", "mkLegacyVault", out _));
            Assert.False(IsTheWithdrawalsTransaction(legacy, "MKLEGACYDEST", "mkLegacyVault", out _));
        }

        [Fact]
        public void Candidates_AreTheCompletedRowsTheAddBackCounts()
        {
            Row("H1", 0.5M, VBTCWithdrawalStatus.Completed, "btc-1");
            Row("H2", 0.3M, VBTCWithdrawalStatus.Completed, "btc-2");
            Row("H3", 0.9M, VBTCWithdrawalStatus.Cancelled, null);   // no burn: never added back, never a candidate
            Row("H4", 0.7M, VBTCWithdrawalStatus.Requested, null);
            var height = Globals.V2WithdrawalOwnerAddBackFixHeight + 1;
            var candidates = Candidates(_vault, height);
            Assert.Equal(2, candidates.Count);
            Assert.Contains(new Candidate("btc-1", 0.5M, Dest, _vault), candidates);
            Assert.Contains(new Candidate("btc-2", 0.3M, Dest, _vault), candidates);
            Assert.Equal(0.8M, VBTCWithdrawalRequest.GetCompletedWithdrawalAmount("xOwner", _vault, height));
        }

        [Fact]
        public async Task OwnerDepositCheck_RequiresTheDepositToCoverTheUncountedWithdrawal()
        {
            Row("H5", 0.4M, VBTCWithdrawalStatus.Completed, "btc-withheld");
            LookupTransaction = _ => Task.FromResult<BtcTxFacts?>(Facts(0, Array.Empty<string>()));
            var height = Globals.V2WithdrawalOwnerAddBackFixHeight + 1;

            using var electrum = new FakeElectrum(("one.test", true, 1.0M));
            // The owner ledger (with the 0.4 add-back) leaves 0.8 for the deposit to cover; the deposit shows 1.0 but
            // 0.4 of it is the withheld withdrawal, so only 0.6 is really spendable.
            var withRule = await VbtcOwnerDeposit.CheckAsync(() => Deposit, 0.8M, false, false, _vault, height);
            Assert.Equal(OwnerDepositStatus.Checked, withRule.Status);
            Assert.Equal(0.6M, withRule.DepositBalance);

            var before = await VbtcOwnerDeposit.CheckAsync(() => Deposit, 0.8M, false, false);
            Assert.Equal(1.0M, before.DepositBalance); // the hole: the full deposit counted

            // Confirmed but someone else's transaction: still not counted.
            LookupTransaction = _ => Task.FromResult<BtcTxFacts?>(Facts(5, new[] { "tb1qsomeoneelse" }, ("tb1qwherever", 0.4M)));
            var unrelated = await VbtcOwnerDeposit.CheckAsync(() => Deposit, 0.8M, false, false, _vault, height);
            Assert.Equal(0.6M, unrelated.DepositBalance);

            // The real withdrawal, confirmed: counted, the full deposit stands.
            LookupTransaction = _ => Task.FromResult<BtcTxFacts?>(Withdrawal(5, 0.39M));
            var real = await VbtcOwnerDeposit.CheckAsync(() => Deposit, 0.8M, false, false, _vault, height);
            Assert.Equal(1.0M, real.DepositBalance);

            ResetForTests();
            LookupTransaction = _ => Task.FromResult<BtcTxFacts?>(null);
            var noAnswer = await VbtcOwnerDeposit.CheckAsync(() => Deposit, 0.8M, false, false, _vault, height);
            Assert.Equal(OwnerDepositStatus.Unverifiable, noAnswer.Status); // fail closed, like an unreadable deposit

            var blockValidation = await VbtcOwnerDeposit.CheckAsync(() => Deposit, 0.8M, false, true, _vault, height);
            Assert.Equal(OwnerDepositStatus.NotQueried, blockValidation.Status); // block validation untouched
        }
    }
}
