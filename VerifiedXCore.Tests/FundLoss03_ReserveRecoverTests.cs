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
        /// C-199: a call-back whose pending row cannot be read on this node (its storage fails) was read as "no such row":
        /// the call-back was skipped, the block counted as applied, and the row later settled to its payee here while every
        /// other node had refunded the sender. The apply's read (GetTransactionsForApply, which the call-back uses) takes
        /// nothing but the hash, so any failure of it - LiteDB's own exceptions (a corrupt page) included - is passed on as a
        /// storage fault, and the call-back's apply reports it (StateData.CallBackReserveAccountTx: NoteApplyStepFailed).
        /// Validation's read is unchanged.
        /// </summary>
        [Fact]
        public async Task APendingRowThatCannotBeReadOnThisNode_IsAFault_NotANoSuchRow()
        {
            var hash = new string('c', 64);
            ReserveTransactions.GetReserveTransactionsDb()!.Insert(new ReserveTransactions
            {
                Id = Guid.NewGuid(), Hash = hash, FromAddress = _reserve, ToAddress = _recoveryAddress, Amount = 100M,
                TransactionType = TransactionType.TX, ReserveTransactionStatus = ReserveTransactionStatus.Pending,
                ConfirmTimestamp = TimeUtil.GetTime() + 86_400, UnlockTime = TimeUtil.GetTime() + 86_400, Signature = "s", Height = 1,
            });
            try
            {
                ReserveTransactions.TestReadFault = () => throw new IOException("reserve store unreadable");
                Assert.Throws<IOException>(() => ReserveTransactions.GetTransactionsForApply(hash));
                ReserveTransactions.TestReadFault = () => throw new LiteDB.LiteException(0, "ENSURE: page corrupt");
                Assert.Throws<IOException>(() => ReserveTransactions.GetTransactionsForApply(hash));

                // The call-back's apply itself (the production call site): the fault is reported for the block.
                StateData.BlockStartFailedAtHeight = -1;
                var callBack = typeof(StateData).GetMethod("CallBackReserveAccountTx", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
                await (Task)callBack.Invoke(null, new object[] { hash, false, 777L })!;
                Assert.Equal(777L, StateData.BlockStartFailedAtHeight);
            }
            finally { ReserveTransactions.TestReadFault = null; }
            Assert.NotNull(ReserveTransactions.GetTransactionsForApply(hash));   // read as before when nothing fails
            Assert.NotNull(ReserveTransactions.GetTransactions(hash));
        }

        /// <summary>
        /// C-183: a reserve registers its recovery account once, but the check reads committed state, so two Register() of
        /// one reserve in a block both passed and the last applied replaced the first's recovery account. From the
        /// path-audit rules the block's guard (both block validators, the producer, admission) takes the first; the
        /// producer keeps the first for the block. Below the rules, history replays unchanged.
        /// </summary>
        [Fact]
        public void TwoRegistersOfOneReserveInABlock_OnlyTheFirstIsTaken()
        {
            var height = Math.Max(Globals.PathAuditRulesHeight, Gate);
            var first = Recover(new { Function = "Register()", RecoveryAddress = _recoveryAddress });
            var second = Recover(new { Function = "Register()", RecoveryAddress = AccountData.GetHumanAddress(NewKey().Pub) });
            Assert.NotEqual(first.Hash, second.Hash);

            var state = new SameBlockDebitGuard.State { BlockHeight = height };
            Assert.True(SameBlockDebitGuard.TryRegister(first, state).Ok);
            var (ok, why) = SameBlockDebitGuard.TryRegister(second, state);
            Assert.False(ok);
            Assert.Contains("registers its recovery account more than once", why);
            Assert.False(BlockValidatorService.IsStateCorruptionSignal(why));   // a producer's fault: no restore or rebuild

            var below = new SameBlockDebitGuard.State { BlockHeight = height - 1 };
            Assert.True(SameBlockDebitGuard.TryRegister(first, below).Ok);
            Assert.True(SameBlockDebitGuard.TryRegister(second, below).Ok);
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
            // The activation block, before the row fell due: its settlement is judged by the same rules.
            BlockchainData.GetBlocks().InsertSafe(new Block { Height = Globals.PathAuditRulesHeight, Hash = "gate", StateRoot = "g", Timestamp = now - 7_200, Transactions = new List<Transaction>() });
            // A row that fell due before the activation block settles under the older rules: it is taken back (round 30).
            var older = new ReserveTransactions
            {
                Id = Guid.NewGuid(), Hash = new string('f', 64), FromAddress = _reserve, ToAddress = payee, Amount = 1M,
                TransactionType = TransactionType.TX, ReserveTransactionStatus = ReserveTransactionStatus.Pending,
                ConfirmTimestamp = now - 9_000, UnlockTime = now - 9_000, Signature = "s", Height = 1,
            };
            ReserveTransactions.GetReserveTransactionsDb()!.Insert(older);
            var sigTime = now;
            var recover = Recover(new { Function = "Recover()", RecoveryAddress = _recoveryAddress, RecoverySigScript = RecoverySig(sigTime, _recoveryAddress), SignatureTime = sigTime });
            var height = Math.Max(Globals.PathAuditRulesHeight, Gate);
            recover.Height = height;
            await StateData.UpdateTreis(new Block { Height = height, StateRoot = "root", Timestamp = now, Transactions = new List<Transaction> { recover } });
            Assert.Equal(ReserveTransactionStatus.Pending, ReserveTransactions.GetTransactions(row.Hash)!.ReserveTransactionStatus);
            Assert.Equal(ReserveTransactionStatus.Recovered, ReserveTransactions.GetTransactions(older.Hash)!.ReserveTransactionStatus);
        }

        /// <summary>A reserve registered as its own recovery address lost its balance at Recover() (round 30): refused.</summary>
        [Fact]
        public async Task ARegisterNamingTheReserveItselfForRecovery_IsRefused()
        {
            var tx = new Transaction
            {
                FromAddress = _reserve, ToAddress = "Reserve_Base", Amount = Globals.RSRVAccountRegisterRBX, Fee = 0.00000100M, Nonce = 0,
                Timestamp = TimeUtil.GetTime(), TransactionType = TransactionType.RESERVE,
                Data = JsonConvert.SerializeObject(new { Function = "Register()", RecoveryAddress = _reserve }),
            };
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, _vault.Key, _vault.Pub);
            var (ok, why) = await TransactionValidatorService.VerifyTX(tx, blockVerify: true, blockHeight: Math.Max(Globals.PathAuditRulesHeight, Gate));
            Assert.False(ok);
            Assert.Contains("recovery address is another address", why);
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
