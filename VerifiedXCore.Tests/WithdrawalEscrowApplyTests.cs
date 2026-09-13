using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Funds-safety regression (permissionless vault drain): vBTC was burned only at WITHDRAWAL_COMPLETE.
    /// A holder could request, receive the BTC, never complete, keep the vBTC and repeat after the
    /// request expired. At/after WithdrawalEscrowHeight the REQUEST apply debits the requester's ledger
    /// (escrow), COMPLETE only finalizes (no second burn), and approved cancellation refunds.
    /// </summary>
    [Collection("DbContextSequential")]
    public class WithdrawalEscrowApplyTests : IDisposable
    {
        private const string Sc = "sc-escrow";
        private const string Requester = "RRequesterEscrow0000000000000000001";
        private const string BtcDest = "tb1q066af78la3rqmnchc396keujllva6turs52749";

        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly long _priorGate;

        public WithdrawalEscrowApplyTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"escrow_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorGate = Globals.WithdrawalEscrowHeight;
            DbContext.Initialize();

            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
            {
                SmartContractUID = Sc,
                OwnerAddress = "xOwner",
                SCStateTreiTokenizationTXes = new() { new SmartContractStateTreiTokenizationTX { FromAddress = "+", ToAddress = Requester, Amount = 1.0M } },
            });
        }

        public void Dispose()
        {
            Globals.WithdrawalEscrowHeight = _priorGate;
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static Transaction RequestTx(decimal amount, long height)
        {
            var tx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(), FromAddress = Requester, ToAddress = Requester, Amount = 0M, Fee = 0M, Nonce = 0, Height = height,
                TransactionType = TransactionType.VBTC_V2_WITHDRAWAL_REQUEST,
                Data = JsonConvert.SerializeObject(new { Function = "WithdrawalRequest()", ContractUID = Sc, BTCAddress = BtcDest, Amount = amount, FeeRate = 10, UniqueId = Guid.NewGuid().ToString() }),
            };
            tx.Build();
            return tx;
        }

        private static Transaction CompleteTx(string requestHash, long height)
        {
            var tx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(), FromAddress = Requester, ToAddress = Requester, Amount = 0M, Fee = 0M, Nonce = 1, Height = height,
                TransactionType = TransactionType.VBTC_V2_WITHDRAWAL_COMPLETE,
                Data = JsonConvert.SerializeObject(new { Function = "WithdrawalComplete()", ContractUID = Sc, WithdrawalRequestHash = requestHash, BTCTransactionHash = new string('a', 64) }),
            };
            tx.Build();
            return tx;
        }

        private static void Invoke(string method, Transaction tx)
        {
            var m = typeof(StateData).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(m);
            m!.Invoke(null, new object[] { tx });
        }

        private static decimal RequesterLedger() =>
            SmartContractStateTrei.GetSmartContractState(Sc)!.SCStateTreiTokenizationTXes!
                .Where(r => r.FromAddress == Requester || r.ToAddress == Requester).Sum(r => r.Amount);

        private static int BurnRows() =>
            SmartContractStateTrei.GetSmartContractState(Sc)!.SCStateTreiTokenizationTXes!.Count(r => r.ToAddress == "-");

        [Fact]
        public void Escrow_DebitsAtRequest_AndCompleteDoesNotBurnAgain()
        {
            Globals.WithdrawalEscrowHeight = 1;

            var req = RequestTx(0.3M, height: 10);
            Invoke("RequestVBTCV2Withdrawal", req);

            Assert.Equal(0.7M, RequesterLedger());   // debited immediately
            Assert.Equal(1, BurnRows());
            var row = VBTCWithdrawalRequest.GetByTransactionHash(req.Hash);
            Assert.NotNull(row);
            Assert.True(VBTCWithdrawalRequest.EscrowAppliesTo(row!.RequestBlockHeight));
            // Already reflected in the ledger: not counted again as "pending".
            Assert.Equal(0M, VBTCWithdrawalRequest.GetIncompleteWithdrawalAmount(Requester, Sc, 11, TimeUtil.GetTime()));

            Invoke("CompleteVBTCV2Withdrawal", CompleteTx(req.Hash, height: 12));

            Assert.Equal(0.7M, RequesterLedger());   // unchanged: no second burn
            Assert.Equal(1, BurnRows());
            Assert.True(VBTCWithdrawalRequest.GetByTransactionHash(req.Hash)!.IsCompleted);
        }

        [Fact]
        public void Escrow_NeverCompleting_KeepsTheDebit()
        {
            Globals.WithdrawalEscrowHeight = 1;
            var req = RequestTx(0.5M, height: 10);
            Invoke("RequestVBTCV2Withdrawal", req);

            // The attack: BTC received, COMPLETE never sent. The requester's vBTC is already gone.
            Assert.Equal(0.5M, RequesterLedger());
            // A second request for the full original balance would now exceed the ledger.
            Assert.True(RequesterLedger() < 1.0M);
        }

        [Fact]
        public void PreGate_LegacyBurnAtCompleteIsPreserved()
        {
            Globals.WithdrawalEscrowHeight = 999_999_999_999L;

            var req = RequestTx(0.3M, height: 10);
            Invoke("RequestVBTCV2Withdrawal", req);
            Assert.Equal(1.0M, RequesterLedger());   // legacy: nothing debited at request
            Assert.Equal(0, BurnRows());
            Assert.Equal(0.3M, VBTCWithdrawalRequest.GetIncompleteWithdrawalAmount(Requester, Sc, 11, TimeUtil.GetTime()));

            Invoke("CompleteVBTCV2Withdrawal", CompleteTx(req.Hash, height: 12));
            Assert.Equal(0.7M, RequesterLedger());   // legacy: burned at completion
            Assert.Equal(1, BurnRows());
        }

        [Fact]
        public void EscrowPredicate_UsesRequestHeightAgainstGate()
        {
            Globals.WithdrawalEscrowHeight = 100;
            Assert.False(VBTCWithdrawalRequest.EscrowAppliesTo(0));    // unstamped legacy row
            Assert.False(VBTCWithdrawalRequest.EscrowAppliesTo(99));
            Assert.True(VBTCWithdrawalRequest.EscrowAppliesTo(100));
            Assert.True(VBTCWithdrawalRequest.EscrowAppliesTo(5000));
        }
    }
}
