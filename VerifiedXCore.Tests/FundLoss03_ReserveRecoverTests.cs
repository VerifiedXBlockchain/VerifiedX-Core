using System;
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
    /// Fund-loss audit item 3 (Globals.ReserveRecoverRulesHeight): a reserve Recover() must carry RecoveryAddress,
    /// RecoverySigScript and SignatureTime, the carried address must be the one recorded in state, and the signature
    /// must verify. Below the height a Recover() with an empty signature, or with no SignatureTime, passes with every
    /// recovery check skipped (the hole, kept for replay); at the height a real recovery still passes.
    /// </summary>
    [Collection("DbContextSequential")]
    public class FundLoss03_ReserveRecoverTests : IDisposable
    {
        private const long Gate = 1000;
        private const string Required = "Recover() requires RecoveryAddress, RecoverySigScript and SignatureTime.";
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly long _priorGate;
        private readonly (PrivateKey Key, string Pub) _vault;
        private readonly string _reserve;
        private readonly (PrivateKey Key, string Pub) _recovery;
        private readonly string _recoveryAddress;

        public FundLoss03_ReserveRecoverTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"fl03_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorLastBlock = Globals.LastBlock;
            _priorGate = Globals.ReserveRecoverRulesHeight;
            Globals.ReserveRecoverRulesHeight = Gate;
            DbContext.Initialize();

            // Not every key yields the xRBX prefix (the audit's own tests hit this); keep drawing until one does.
            do
            {
                _vault = NewKey();
                _reserve = ReserveAccount.GetHumanAddress(_vault.Pub);
            } while (!_reserve.StartsWith("xRBX", StringComparison.Ordinal));
            _recovery = NewKey();
            _recoveryAddress = AccountData.GetHumanAddress(_recovery.Pub);
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _reserve, Balance = 1000M, Nonce = 0, RecoveryAccount = _recoveryAddress });
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.LastBlock = _priorLastBlock;
            Globals.ReserveRecoverRulesHeight = _priorGate;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static (PrivateKey Key, string Pub) NewKey()
        {
            var key = new PrivateKey("secp256k1");
            return (key, "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant());
        }

        /// <summary>A Recover() signed by the vault key; the recovery leg is whatever the caller supplies.</summary>
        private Transaction Recover(object data)
        {
            var tx = new Transaction
            {
                FromAddress = _reserve, ToAddress = "Reserve_Base", Amount = 0M, Fee = 0.00000100M, Nonce = 0,
                Timestamp = TimeUtil.GetTime(), TransactionType = TransactionType.RESERVE,
                Data = JsonConvert.SerializeObject(data),
            };
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, _vault.Key, _vault.Pub);
            return tx;
        }

        private string RecoverySig(long sigTime, string recoveryAddress) =>
            SignatureService.CreateSignature($"{sigTime}{recoveryAddress}", _recovery.Key, _recovery.Pub);

        private static void TipBelowGate() => Globals.LastBlock = new Block { Height = Gate - 2 };
        private static void TipAtGate() => Globals.LastBlock = new Block { Height = Gate - 1 };

        /// <summary>
        /// A reserve's pending payment to its own recovery address: Recover() credited the recovery account, then wrote a
        /// copy of it read before that credit, and the payment left the supply (fifth review, round 28).
        /// </summary>
        [Fact]
        public async Task Recover_OfAPaymentToTheRecoveryAddressItself_KeepsTheAmount()
        {
            var accounts = StateData.GetAccountStateTrei();
            var reserve = accounts.FindOne(x => x.Key == _reserve);
            reserve.Balance = 900M; reserve.LockedBalance = 100M;
            accounts.UpdateSafe(reserve);
            accounts.InsertSafe(new AccountStateTrei { Key = _recoveryAddress, Balance = 0M, LockedBalance = 100M, Nonce = 0 });
            ReserveTransactions.GetReserveTransactionsDb()!.Insert(new ReserveTransactions
            {
                Id = Guid.NewGuid(), Hash = new string('a', 64), FromAddress = _reserve, ToAddress = _recoveryAddress, Amount = 100M,
                TransactionType = TransactionType.TX, ReserveTransactionStatus = ReserveTransactionStatus.Pending,
                ConfirmTimestamp = TimeUtil.GetTime() + 86_400, UnlockTime = TimeUtil.GetTime() + 86_400, Signature = "s", Height = 1,
            });
            var sigTime = TimeUtil.GetTime();
            var recover = Recover(new { Function = "Recover()", RecoveryAddress = _recoveryAddress, RecoverySigScript = RecoverySig(sigTime, _recoveryAddress), SignatureTime = sigTime });
            var height = Math.Max(Globals.PathAuditRulesHeight, Gate);
            recover.Height = height;
            var applied = await StateData.UpdateTreis(new Block { Height = height, StateRoot = "root", Transactions = new List<Transaction> { recover } });

            var after = accounts.FindOne(x => x.Key == _recoveryAddress);
            var left = accounts.FindOne(x => x.Key == _reserve);
            Assert.True(1000M - recover.Fee == after.Balance + left.Balance + left.LockedBalance, $"applied {applied}; recovery {after.Balance}/{after.LockedBalance}; reserve {left.Balance}/{left.LockedBalance}"); // nothing left the supply but the fee
            Assert.Equal(0M, after.LockedBalance);
        }

        /// <summary>
        /// From the path-audit rules a reserve moves no token, and Recover() did not move them either: a reserve's token
        /// balances, and its token contracts' owner powers, were frozen for good (round 28). They go to the recovery address.
        /// </summary>
        [Fact]
        public async Task Recover_MovesTheReservesTokensAndTokenContracts()
        {
            var accounts = StateData.GetAccountStateTrei();
            var reserve = accounts.FindOne(x => x.Key == _reserve);
            reserve.TokenAccounts = new List<TokenAccount> { TokenAccount.CreateTokenAccount("tok:1", "T", "T", 50M, 2) };
            accounts.UpdateSafe(reserve);
            accounts.InsertSafe(new AccountStateTrei { Key = _recoveryAddress, Balance = 0M, Nonce = 0 });
            SmartContractStateTrei.GetSCST()!.InsertSafe(new SmartContractStateTrei
            {
                SmartContractUID = "tok:1", OwnerAddress = "someoneelse", ContractData = "x", Nonce = 0,
                TokenDetails = new VerifiedXCore.Models.SmartContracts.TokenDetails { TokenName = "T", TokenTicker = "T", ContractOwner = _reserve, IsPaused = true },
            });
            var sigTime = TimeUtil.GetTime();
            var recover = Recover(new { Function = "Recover()", RecoveryAddress = _recoveryAddress, RecoverySigScript = RecoverySig(sigTime, _recoveryAddress), SignatureTime = sigTime });
            var height = Math.Max(Globals.PathAuditRulesHeight, Gate);
            recover.Height = height;
            await StateData.UpdateTreis(new Block { Height = height, StateRoot = "root", Transactions = new List<Transaction> { recover } });

            Assert.Equal(50M, accounts.FindOne(x => x.Key == _recoveryAddress).TokenAccounts!.Single(t => t.SmartContractUID == "tok:1").Balance);
            Assert.Equal(0M, accounts.FindOne(x => x.Key == _reserve).TokenAccounts!.Single().Balance);
            Assert.Equal(_recoveryAddress, SmartContractStateTrei.GetSmartContractState("tok:1")!.TokenDetails!.ContractOwner);
        }

        /// <summary>
        /// Recover() redirected every pending payment, also one whose unlock time had passed by the block's time (a
        /// block late, or across a halt), which a call-back may no longer take back (round 29). It is left to settle.
        /// </summary>
        [Fact]
        public async Task Recover_LeavesAPaymentWhoseTimeHasPassed_ToSettle()
        {
            var payee = AccountData.GetHumanAddress(NewKey().Pub);
            var now = TimeUtil.GetTime();
            var row = new ReserveTransactions
            {
                Id = Guid.NewGuid(), Hash = new string('c', 64), FromAddress = _reserve, ToAddress = payee, Amount = 100M,
                TransactionType = TransactionType.TX, ReserveTransactionStatus = ReserveTransactionStatus.Pending,
                ConfirmTimestamp = now - 3_600, UnlockTime = now - 3_600, Signature = "s", Height = 1,
            };
            ReserveTransactions.GetReserveTransactionsDb()!.Insert(row);
            var sigTime = now;
            var recover = Recover(new { Function = "Recover()", RecoveryAddress = _recoveryAddress, RecoverySigScript = RecoverySig(sigTime, _recoveryAddress), SignatureTime = sigTime });
            var height = Math.Max(Globals.PathAuditRulesHeight, Gate);
            recover.Height = height;
            await StateData.UpdateTreis(new Block { Height = height, StateRoot = "root", Timestamp = now, Transactions = new List<Transaction> { recover } });
            Assert.Equal(ReserveTransactionStatus.Pending, ReserveTransactions.GetTransactions(row.Hash)!.ReserveTransactionStatus);
        }

        [Fact]
        public async Task EmptyRecoverySigScript_PassesBelowGate_RefusedAtGate()
        {
            var thief = AccountData.GetHumanAddress(NewKey().Pub);
            var data = new { Function = "Recover()", RecoveryAddress = thief, RecoverySigScript = "", SignatureTime = TimeUtil.GetTime() };

            TipBelowGate();
            var (okBelow, msgBelow) = await TransactionValidatorService.VerifyTX(Recover(data));
            Assert.True(okBelow, msgBelow); // the hole: the vault key alone, to any address, at once

            TipAtGate();
            var (okAt, msgAt) = await TransactionValidatorService.VerifyTX(Recover(data));
            Assert.False(okAt);
            Assert.Equal(Required, msgAt);
        }

        [Fact]
        public async Task MissingSignatureTime_PassesBelowGate_RefusedAtGate()
        {
            var thief = AccountData.GetHumanAddress(NewKey().Pub);
            var data = new { Function = "Recover()", RecoveryAddress = thief, RecoverySigScript = "not-a-signature" };

            TipBelowGate();
            var (okBelow, msgBelow) = await TransactionValidatorService.VerifyTX(Recover(data));
            Assert.True(okBelow, msgBelow); // the throw was swallowed by the empty catch

            TipAtGate();
            var (okAt, msgAt) = await TransactionValidatorService.VerifyTX(Recover(data));
            Assert.False(okAt);
            Assert.Equal(Required, msgAt);
        }

        [Fact]
        public async Task CarriedAddressDiffersFromState_RefusedAtGate()
        {
            TipAtGate();
            var thief = AccountData.GetHumanAddress(NewKey().Pub);
            var sigTime = TimeUtil.GetTime();
            var (ok, msg) = await TransactionValidatorService.VerifyTX(Recover(new { Function = "Recover()", RecoveryAddress = thief, RecoverySigScript = RecoverySig(sigTime, thief), SignatureTime = sigTime }));
            Assert.False(ok);
            Assert.Contains("does not match", msg);
        }

        [Fact]
        public async Task WrongRecoveryKey_RefusedAtGate()
        {
            TipAtGate();
            var sigTime = TimeUtil.GetTime();
            var other = NewKey();
            var badSig = SignatureService.CreateSignature($"{sigTime}{_recoveryAddress}", other.Key, other.Pub);
            var (ok, msg) = await TransactionValidatorService.VerifyTX(Recover(new { Function = "Recover()", RecoveryAddress = _recoveryAddress, RecoverySigScript = badSig, SignatureTime = sigTime }));
            Assert.False(ok);
            Assert.Contains("signature did not verify", msg);
        }

        [Fact]
        public async Task RealRecovery_PassesAtGate()
        {
            TipAtGate();
            var sigTime = TimeUtil.GetTime();
            var (ok, msg) = await TransactionValidatorService.VerifyTX(Recover(new { Function = "Recover()", RecoveryAddress = _recoveryAddress, RecoverySigScript = RecoverySig(sigTime, _recoveryAddress), SignatureTime = sigTime }));
            Assert.True(ok, msg);
        }

        [Fact]
        public async Task BlockPath_JudgesAtTheBlocksHeight()
        {
            var thief = AccountData.GetHumanAddress(NewKey().Pub);
            var data = new { Function = "Recover()", RecoveryAddress = thief, RecoverySigScript = "", SignatureTime = TimeUtil.GetTime() };
            Globals.LastBlock = new Block { Height = Gate + 5000 };
            var (okHistorical, msg) = await TransactionValidatorService.VerifyTX(Recover(data), blockDownloads: true, blockVerify: true, blockHeight: Gate - 1);
            Assert.True(okHistorical, msg);

            Globals.LastBlock = new Block { Height = 10 };
            var (okAtGate, msgAtGate) = await TransactionValidatorService.VerifyTX(Recover(data), blockVerify: true, blockHeight: Gate);
            Assert.False(okAtGate);
            Assert.Equal(Required, msgAtGate);
        }
    }
}
