using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// From Globals.ReserveRecoverReadsEveryRowHeight a pending NFT_TX row whose Data is not an array of objects does not
    /// stop a reserve's Recover(): the rows after it are redirected and the balance goes to the recovery address. Below
    /// the height the recovery stops at that row, as it always did (kept for replay).
    /// </summary>
    [Collection("DbContextSequential")]
    public class ReserveRecoverUnreadableRowTests : IDisposable
    {
        private const long Gate = 1000;
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly long _priorRecoverGate;
        private readonly long _priorRowGate;
        private readonly (PrivateKey Key, string Pub) _vault;
        private readonly string _reserve;
        private readonly (PrivateKey Key, string Pub) _recovery;
        private readonly string _recoveryAddress;
        private readonly long _height = Math.Max(Globals.PathAuditRulesHeight, Gate);

        public ReserveRecoverUnreadableRowTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"rsrv_unreadable_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorLastBlock = Globals.LastBlock;
            _priorRecoverGate = Globals.ReserveRecoverRulesHeight;
            _priorRowGate = Globals.ReserveRecoverReadsEveryRowHeight;
            Globals.ReserveRecoverRulesHeight = Gate;
            DbContext.Initialize();

            do
            {
                _vault = NewKey();
                _reserve = ReserveAccount.GetHumanAddress(_vault.Pub);
            } while (!_reserve.StartsWith("xRBX", StringComparison.Ordinal));
            _recovery = NewKey();
            _recoveryAddress = AccountData.GetHumanAddress(_recovery.Pub);
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _reserve, Balance = 900M, LockedBalance = 100M, Nonce = 0, RecoveryAccount = _recoveryAddress });
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = "xPayee", Balance = 0M, LockedBalance = 100M, Nonce = 0 });
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.LastBlock = _priorLastBlock;
            Globals.ReserveRecoverRulesHeight = _priorRecoverGate;
            Globals.ReserveRecoverReadsEveryRowHeight = _priorRowGate;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static (PrivateKey Key, string Pub) NewKey()
        {
            var key = new PrivateKey("secp256k1");
            return (key, "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant());
        }

        private void Pending(char hash, TransactionType type, decimal amount, long confirm, string? data) =>
            ReserveTransactions.GetReserveTransactionsDb()!.Insert(new ReserveTransactions
            {
                Id = Guid.NewGuid(), Hash = new string(hash, 64), FromAddress = _reserve, ToAddress = "xPayee", Amount = amount,
                TransactionType = type, ReserveTransactionStatus = ReserveTransactionStatus.Pending, Data = data,
                ConfirmTimestamp = confirm, UnlockTime = confirm, Signature = "s", Height = 1,
            });

        private async Task<Transaction> ApplyRecover()
        {
            var sigTime = TimeUtil.GetTime();
            var tx = new Transaction
            {
                FromAddress = _reserve, ToAddress = "Reserve_Base", Amount = 0M, Fee = 0.00000100M, Nonce = 0,
                Timestamp = TimeUtil.GetTime(), TransactionType = TransactionType.RESERVE, Height = _height,
                Data = JsonConvert.SerializeObject(new
                {
                    Function = "Recover()", RecoveryAddress = _recoveryAddress, SignatureTime = sigTime,
                    RecoverySigScript = SignatureService.CreateSignature($"{sigTime}{_recoveryAddress}", _recovery.Key, _recovery.Pub),
                }),
            };
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, _vault.Key, _vault.Pub);
            await StateData.UpdateTreis(new Block { Height = _height, StateRoot = "root", Transactions = new List<Transaction> { tx } });
            return tx;
        }

        private decimal RecoveryBalance() => StateData.GetSpecificAccountStateTrei(_recoveryAddress)?.Balance ?? 0M;

        [Theory]
        [InlineData("not json")]
        [InlineData("[]")]
        [InlineData("[1]")]
        [InlineData("{\"Function\":\"Transfer()\"}")]
        public async Task FromTheHeight_AnUnreadableRow_DoesNotStopTheRecovery(string data)
        {
            Globals.ReserveRecoverReadsEveryRowHeight = _height;
            var later = TimeUtil.GetTime() + 86_400;
            Pending('a', TransactionType.NFT_TX, 0M, later, data);      // read first
            Pending('b', TransactionType.TX, 100M, later + 1, null);    // the payment after it

            var recover = await ApplyRecover();

            Assert.Equal(1000M - recover.Fee, RecoveryBalance()); // the balance and the redirected payment
            Assert.Equal(0M, StateData.GetSpecificAccountStateTrei("xPayee")!.LockedBalance);
            Assert.Equal(ReserveTransactionStatus.Recovered, ReserveTransactions.GetTransactions(new string('b', 64))!.ReserveTransactionStatus);
        }

        [Fact]
        public async Task BelowTheHeight_AnUnreadableRow_StillStopsTheRecoveryThere()
        {
            Globals.ReserveRecoverReadsEveryRowHeight = _height + 1;
            var later = TimeUtil.GetTime() + 86_400;
            Pending('a', TransactionType.NFT_TX, 0M, later, "not json");
            Pending('b', TransactionType.TX, 100M, later + 1, null);

            await ApplyRecover();

            Assert.Equal(0M, RecoveryBalance());
            Assert.Equal(ReserveTransactionStatus.Pending, ReserveTransactions.GetTransactions(new string('b', 64))!.ReserveTransactionStatus);
        }

        [Fact]
        public async Task FromTheHeight_AReadableRow_StillTakesItsContractBack()
        {
            Globals.ReserveRecoverReadsEveryRowHeight = _height;
            SmartContractStateTrei.GetSCST()!.Insert(new SmartContractStateTrei { SmartContractUID = "uid:1", OwnerAddress = _reserve, NextOwner = "xPayee", IsLocked = true });
            Pending('a', TransactionType.NFT_TX, 0M, TimeUtil.GetTime() + 86_400,
                JsonConvert.SerializeObject(new[] { new { Function = "Transfer()", ContractUID = "uid:1", ToAddress = "xPayee" } }));

            await ApplyRecover();

            var contract = SmartContractStateTrei.GetSmartContractState("uid:1")!;
            Assert.Equal(_recoveryAddress, contract.OwnerAddress);
            Assert.False(contract.IsLocked);
        }

        [Theory]
        [InlineData("not json")]
        [InlineData("[]")]
        [InlineData("[1]")]
        [InlineData("{\"Function\":\"Transfer()\"}")]
        [InlineData("[{\"Function\":{}}]")]
        public async Task FromTheHeight_AnUnreadableRowFallingDue_IsSettledWithNoEffect_NotLeftPending(string data)
        {
            Globals.ReserveRecoverReadsEveryRowHeight = _height;
            Pending('a', TransactionType.NFT_TX, 0M, 1, data);
            var row = ReserveTransactions.GetTransactions(new string('a', 64))!;

            await StateData.UpdateTreiFromReserve(new List<ReserveTransactions> { row }, new Block { Height = _height, Timestamp = TimeUtil.GetTime() });

            Assert.Equal(ReserveTransactionStatus.CalledBack, ReserveTransactions.GetTransactions(new string('a', 64))!.ReserveTransactionStatus);
        }

        [Fact]
        public async Task BelowTheHeight_AnUnreadableRowFallingDue_StaysPending()
        {
            Globals.ReserveRecoverReadsEveryRowHeight = _height + 1;
            Pending('a', TransactionType.NFT_TX, 0M, 1, "not json");
            var row = ReserveTransactions.GetTransactions(new string('a', 64))!;

            await StateData.UpdateTreiFromReserve(new List<ReserveTransactions> { row }, new Block { Height = _height, Timestamp = TimeUtil.GetTime() });

            Assert.Equal(ReserveTransactionStatus.Pending, ReserveTransactions.GetTransactions(new string('a', 64))!.ReserveTransactionStatus);
        }

        [Fact]
        public void ARowCall_ReadsAsTheOldReadDid_WhereThatReadWorked()
        {
            Assert.Equal((true, "Transfer()", "uid:1"), StateData.ReserveRowCall("[{\"Function\":\"Transfer()\",\"ContractUID\":\"uid:1\"}]"));
            Assert.Equal((true, (string?)null, (string?)null), StateData.ReserveRowCall("[{}]")); // readable, no function: settles as before
            Assert.False(StateData.ReserveRowCall(null).Readable);
            Assert.False(StateData.ReserveRowCall("null").Readable);
        }
    }
}
