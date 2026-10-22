using System;
using System.IO;
using System.Threading.Tasks;
using VerifiedXCore.Data;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Privacy;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// The wallet pass a block runs (ProcessOutgoingTransaction / ProcessIncomingTransactions) keeps a local account's
    /// balance equal to state for shields and unshields: a shield debits its sender once, an unshield credits its recipient.
    /// </summary>
    [Collection("DbContextSequential")]
    public class ShieldLocalBalanceTests : IDisposable
    {
        private const string Sender = "xShieldLocalBalanceSenderAddress0001";
        private const string Recipient = "xShieldLocalBalanceRecipientAddr0002";
        private const string Zfx = "zfxShieldLocalBalanceTestAddress";
        private const long Height = 500;
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;

        public ShieldLocalBalanceTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"shield_local_balance_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static Account LocalAccount(string address, decimal balance)
        {
            var account = new Account { Address = address, PublicKey = "04" + address, PrivateKey = "", Balance = balance };
            AccountData.GetAccounts().InsertSafe(account);
            return AccountData.GetSingleAccount(address)!;
        }

        private static void StateBalance(string address, decimal balance) =>
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = address, Balance = balance, Nonce = 1 });

        private static Transaction Shield(decimal amount, decimal fee, string hash) => new Transaction
        {
            Hash = hash, FromAddress = Sender, ToAddress = PrivacyConstants.ShieldedPoolAddress, Amount = amount, Fee = fee,
            Nonce = 0, Timestamp = 1, TransactionType = TransactionType.VFX_SHIELD, Data = "{}", Signature = "sig",
        };

        private static decimal LocalBalance(string address) => AccountData.GetSingleAccount(address)!.Balance;

        [Fact]
        public async Task AShieldBroadcastFromThisWallet_DebitsTheSenderOnce_WhenItsBlockApplies()
        {
            var account = LocalAccount(Sender, 100M);
            StateBalance(Sender, 98.4999M);
            var shield = Shield(1.5M, 0.0001M, "shield-hash-1");
            await PrivacyApiHelper.SavePrivacyTxLocally(shield, Sender, Zfx, shield.Amount, TransactionStatus.Pending);

            await BlockTransactionValidatorService.ProcessOutgoingTransaction(shield, account, Height);
            Assert.Equal(98.4999M, LocalBalance(Sender));

            // The same block seen again (a resync's replay) does not debit twice.
            await BlockTransactionValidatorService.ProcessOutgoingTransaction(shield, AccountData.GetSingleAccount(Sender)!, Height);
            Assert.Equal(98.4999M, LocalBalance(Sender));
        }

        [Fact]
        public async Task AShieldThisWalletNeverRecorded_TakesTheBalanceFromState()
        {
            var account = LocalAccount(Sender, 100M);
            StateBalance(Sender, 98.4999M);

            await BlockTransactionValidatorService.ProcessOutgoingTransaction(Shield(1.5M, 0.0001M, "shield-hash-2"), account, Height);

            Assert.Equal(98.4999M, LocalBalance(Sender));
        }

        [Fact]
        public async Task AnUnshieldToThisWallet_CreditsTheRecipient()
        {
            var account = LocalAccount(Recipient, 20.1234441M);
            var unshield = new Transaction
            {
                Hash = "unshield-hash-1", FromAddress = PrivacyConstants.ShieldedPoolAddress, ToAddress = Recipient, Amount = 0.2M,
                Fee = 0M, Nonce = 0, Timestamp = 1, TransactionType = TransactionType.VFX_UNSHIELD, Data = "{}",
                Signature = PrivacyConstants.PlonkSignatureSentinel,
            };

            await BlockTransactionValidatorService.ProcessIncomingTransactions(unshield, account, Height);

            Assert.Equal(20.3234441M, LocalBalance(Recipient));
        }

        [Fact]
        public async Task ATransparentSend_IsStillDebitedOnlyAtBroadcast()
        {
            var account = LocalAccount(Sender, 95M); // already debited by the send's broadcast
            StateBalance(Sender, 95M);
            var send = new Transaction
            {
                Hash = "send-hash-1", FromAddress = Sender, ToAddress = Recipient, Amount = 5M, Fee = 0M, Nonce = 0,
                Timestamp = 1, TransactionType = TransactionType.TX, Data = "", Signature = "sig",
                TransactionStatus = TransactionStatus.Pending,
            };
            await TransactionData.AddTxToWallet(send, true);

            await BlockTransactionValidatorService.ProcessOutgoingTransaction(send, account, Height);

            Assert.Equal(95M, LocalBalance(Sender));
        }
    }
}
