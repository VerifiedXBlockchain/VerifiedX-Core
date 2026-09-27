using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Historical reserve reversals (owner decision, 26 Sep 2026). Before 6b914353 a CallBack()/Recover() refunded the
    /// reserve sender everywhere but marked the transfer CalledBack/Recovered only on a node whose local wallet held it; on
    /// every other node the finalizer paid the recipient too. The mainnet replay showed 11 such transfers (299,615.99 VFX)
    /// and a sync stopping at 4,124,088. For exactly those control transactions the transfer now stays Pending, so a sync
    /// from genesis reproduces the live balances; every other callback/recover keeps the fixed behaviour. The tests run the
    /// real apply path (StateData.UpdateTreis, ReserveService.Run) with the real mainnet hashes, heights and parties.
    /// </summary>
    [Collection("DbContextSequential")]
    public class HistoricalReserveReversalTests : IDisposable
    {
        // The 4,000 VFX transfer to RF3X6EB... (block 1,659,320) and its callback (block 1,659,325).
        private const string Sender = "xRBX68MKPk1rqXvB7jzsRW4BSTZCkkvHh7";
        private const string Recipient = "RF3X6EBSNht3kGwD7yy4MFq1jau7Le3yLR";
        private const string TransferHash = "bc76703f48f0e330c01001cc9abf5e2b1a76120b04aec2fce3e287225ed7542a";
        private const string CallBackHash = "e86c39058693c6a564a5afe21a5e2b6128f623a7a1cfd652e862133be2b968bb";
        private const long TransferHeight = 1_659_320, CallBackHeight = 1_659_325, UnlockTime = 1701266798;

        // The recovery at 1,243,110 of two transfers to REjHm... (995.99 + 8,998.99).
        private const string RecSender = "xRBX9Zgi3Dsyufu8xG4Yf1W95gdhXa1ApZ";
        private const string RecRecipient = "REjHmNpnJ9SZK43VwgLuno4nq2Yzx6Ja6n";
        private const string RecoveryAddress = "RG1ndQxwQ4yhAFQBtwgP2eTKfLCpmGXWCA";
        private const string RecoverHash = "2f20dff8b6264688179edb8bf911fef22647643ae3dda4b7983b5f780568c7ed";

        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly bool _priorIsTestNet;
        private readonly Block _priorLastBlock;

        public HistoricalReserveReversalTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"rsvrev_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            _priorIsTestNet = Globals.IsTestNet;
            _priorLastBlock = Globals.LastBlock;
            Globals.CustomPath = _tempRoot;
            Globals.IsTestNet = false;
            DbContext.Initialize();
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.LastBlock = _priorLastBlock;
            Globals.IsTestNet = _priorIsTestNet;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static AccountStateTrei Account(string key) => StateData.GetSpecificAccountStateTrei(key)!;
        private static ReserveTransactionStatus StatusOf(string hash) => ReserveTransactions.GetTransactions(hash)!.ReserveTransactionStatus;

        private static Transaction Tx(string hash, string from, string to, decimal amount, TransactionType type, long height, string? data = null, long? unlock = null) => new Transaction
        {
            Hash = hash, FromAddress = from, ToAddress = to, Amount = amount, Fee = 0M, Nonce = 0, TransactionType = type,
            Timestamp = UnlockTime - 86400, UnlockTime = unlock, Data = data, Height = height, Signature = "sig",
        };

        private static async Task Apply(long height, params Transaction[] txs) =>
            Assert.True(await StateData.UpdateTreis(new Block { Height = height, StateRoot = "root", Timestamp = UnlockTime - 86000, Transactions = new List<Transaction>(txs) }));

        private static async Task SettleAfter(long timestamp)
        {
            Globals.LastBlock = new Block { Height = 1_700_000, Timestamp = timestamp };
            await ReserveService.Run();
        }

        private async Task SendThenCallBack(long callBackHeight)
        {
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = Sender, Balance = 5000M, Nonce = 0 });
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = Recipient, Balance = 0M, Nonce = 0 });
            await Apply(TransferHeight, Tx(TransferHash, Sender, Recipient, 4000M, TransactionType.TX, TransferHeight, unlock: UnlockTime));
            Assert.Equal(1000M, Account(Sender).Balance);
            Assert.Equal(4000M, Account(Recipient).LockedBalance);
            await Apply(callBackHeight, Tx(CallBackHash, Sender, "Reserve_Base", 0M, TransactionType.RESERVE, callBackHeight,
                data: "{\"Function\":\"CallBack()\",\"Hash\":\"" + TransferHash + "\"}"));
            Assert.Equal(5000M, Account(Sender).Balance);          // refunded on every node, before and after the fix
            Assert.Equal(0M, Account(Recipient).LockedBalance);
        }

        [Fact]
        public async Task TheListedCallBack_LeavesTheTransferPending_AndItSettles_AsTheNetworkAppliedIt()
        {
            await SendThenCallBack(CallBackHeight);
            Assert.Equal(ReserveTransactionStatus.Pending, StatusOf(TransferHash));
            await SettleAfter(UnlockTime + 1);
            Assert.Equal(4000M, Account(Recipient).Balance);       // the live network's balance (paid a second time)
            Assert.Equal(5000M, Account(Sender).Balance);
            Assert.Equal(ReserveTransactionStatus.Confirmed, StatusOf(TransferHash));
        }

        [Fact]
        public async Task AnyOtherCallBack_KeepsTheFix()
        {
            await SendThenCallBack(CallBackHeight + 1);              // same transaction, not its historical block
            Assert.Equal(ReserveTransactionStatus.CalledBack, StatusOf(TransferHash));
            await SettleAfter(UnlockTime + 1);
            Assert.Equal(0M, Account(Recipient).Balance);
            Assert.Equal(5000M, Account(Sender).Balance);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task TheListedRecovery_LeavesBothTransfersPending_OnlyAtItsBlock(bool listed)
        {
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = RecSender, Balance = 10000M, Nonce = 0, RecoveryAccount = RecoveryAddress });
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = RecRecipient, Balance = 0M, Nonce = 0 });
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = RecoveryAddress, Balance = 0M, Nonce = 0 });
            await Apply(1_243_096, Tx("402549cbd223e897292d1ee94851a4c37bed7e453dbdd1061985481c1ed4dd4b", RecSender, RecRecipient, 995.99M, TransactionType.TX, 1_243_096, unlock: UnlockTime));
            await Apply(1_243_105, Tx("73f4d19b2a74d2ebc72250215fd53d76c7f2bb68faae2ba9fea7380b618c6b78", RecSender, RecRecipient, 8998.99M, TransactionType.TX, 1_243_105, unlock: UnlockTime));
            var height = listed ? 1_243_110 : 1_243_111;
            await Apply(height, Tx(RecoverHash, RecSender, "Reserve_Base", 0M, TransactionType.RESERVE, height,
                data: "{\"Function\":\"Recover()\",\"RecoveryAddress\":\"" + RecoveryAddress + "\",\"RecoverySigScript\":\"x\"}"));
            Assert.True(Account(RecoveryAddress).Balance >= 9994.98M);   // the recovery redirect happens either way
            await SettleAfter(UnlockTime + 1);
            Assert.Equal(listed ? 9994.98M : 0M, Account(RecRecipient).Balance);
        }

        [Fact]
        public void TheMatch_IsExact()
        {
            Transaction CallBack(string hash, long h, string from, string target, TransactionType type = TransactionType.RESERVE) =>
                Tx(hash, from, "Reserve_Base", 0M, type, h, data: "{\"Function\":\"CallBack()\",\"Hash\":\"" + target + "\"}");
            Assert.True(HistoricalTransactionExceptions.KeepsReversedTransferPending(CallBack(CallBackHash, CallBackHeight, Sender, TransferHash), CallBackHeight));
            Assert.False(HistoricalTransactionExceptions.KeepsReversedTransferPending(CallBack(CallBackHash, CallBackHeight, Sender, TransferHash), CallBackHeight + 1));
            Assert.False(HistoricalTransactionExceptions.KeepsReversedTransferPending(CallBack(CallBackHash, CallBackHeight, Sender, TransferHash), null));
            Assert.False(HistoricalTransactionExceptions.KeepsReversedTransferPending(CallBack(CallBackHash, CallBackHeight, RecSender, TransferHash), CallBackHeight));
            Assert.False(HistoricalTransactionExceptions.KeepsReversedTransferPending(CallBack(CallBackHash, CallBackHeight, Sender, "0" + TransferHash.Substring(1)), CallBackHeight));
            Assert.False(HistoricalTransactionExceptions.KeepsReversedTransferPending(CallBack(CallBackHash, CallBackHeight, Sender, TransferHash, TransactionType.TX), CallBackHeight));
            Globals.IsTestNet = true;
            Assert.False(HistoricalTransactionExceptions.KeepsReversedTransferPending(CallBack(CallBackHash, CallBackHeight, Sender, TransferHash), CallBackHeight));
            Globals.IsTestNet = false;
            // 9 callbacks + 1 recovery = the 11 transfers the replay found.
            Assert.Equal(10, HistoricalTransactionExceptions.MainnetReserveReversalCount);
        }
    }
}
