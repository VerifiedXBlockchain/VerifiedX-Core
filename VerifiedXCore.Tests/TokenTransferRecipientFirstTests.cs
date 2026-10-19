using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Models.SmartContracts;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// From TokenTransferRecipientFirstHeight a token transfer writes the recipient's record first and debits the sender
    /// only once that write succeeded, so a failed recipient write loses no tokens. Below the height the sender is debited
    /// first, as before.
    /// </summary>
    [Collection("DbContextSequential")]
    public class TokenTransferRecipientFirstTests : IDisposable
    {
        private const string TokenX = "5a1d0c2a9b8e4c7d8e1f2a3b4c5d6e7f:1790200000";
        private const long Gate = 200;
        private const string Sender = "RTokenSenderAddress000000000000000";
        private const string Recipient = "RTokenRecipientAddress00000000000";
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly long _priorGate = Globals.TokenTransferRecipientFirstHeight;
        private readonly Func<LiteDB.ILiteCollection<AccountStateTrei>, AccountStateTrei, Task<bool>> _priorWrite = StateData.TokenRecipientWrite;

        public TokenTransferRecipientFirstTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"token_recipient_first_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            Globals.LastBlock = new Block { Height = 100 };
            Globals.TokenTransferRecipientFirstHeight = Gate;

            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
            {
                SmartContractUID = TokenX, MinterAddress = Sender, OwnerAddress = Sender, IsToken = true,
                TokenDetails = new TokenDetails { TokenName = "X", TokenTicker = "X", StartingSupply = 1000, CurrentSupply = 1000, ContractOwner = Sender, DecimalPlaces = 2 },
            });
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei
            {
                Key = Sender, Balance = 100M, Nonce = 0,
                TokenAccounts = new List<TokenAccount> { TokenAccount.CreateTokenAccount(TokenX, "X", "X", 1000M, 2) },
            });
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei
            {
                Key = Recipient, Balance = 100M, Nonce = 0,
                TokenAccounts = new List<TokenAccount> { TokenAccount.CreateTokenAccount(TokenX, "X", "X", 5M, 2) },
            });
        }

        public void Dispose()
        {
            StateData.TokenRecipientWrite = _priorWrite;
            Globals.TokenTransferRecipientFirstHeight = _priorGate;
            Globals.LastBlock = _priorLastBlock;
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static decimal TokenBalanceOf(string address) =>
            StateData.GetSpecificAccountStateTrei(address)?.TokenAccounts?.FirstOrDefault(t => t.SmartContractUID == TokenX)?.Balance ?? 0M;

        private static Block TransferBlock(long height, decimal amount)
        {
            var tx = new Transaction
            {
                FromAddress = Sender, ToAddress = Recipient, Amount = 0M, Fee = 0M, Nonce = 0, Timestamp = 1_700_000_000, Height = height,
                TransactionType = TransactionType.FTKN_TX, Hash = $"token-transfer-{height}",
                Data = JsonConvert.SerializeObject(new { Function = "TokenTransfer()", ContractUID = TokenX, FromAddress = Sender, ToAddress = Recipient, Amount = amount, TokenTicker = "X", TokenName = "X" }),
            };
            return new Block { Height = height, StateRoot = "root", Transactions = new List<Transaction> { tx } };
        }

        [Fact]
        public async Task FromTheHeight_AFailedRecipientWrite_LeavesTheSenderWhole()
        {
            StateData.TokenRecipientWrite = (_, _) => Task.FromResult(false);
            await StateData.UpdateTreis(TransferBlock(Gate, 10M));
            Assert.Equal(1000M, TokenBalanceOf(Sender));
            Assert.Equal(5M, TokenBalanceOf(Recipient));
        }

        [Fact]
        public async Task FromTheHeight_ATransferMovesTheTokens()
        {
            Assert.True(await StateData.UpdateTreis(TransferBlock(Gate, 10M)));
            Assert.Equal(990M, TokenBalanceOf(Sender));
            Assert.Equal(15M, TokenBalanceOf(Recipient));
        }

        [Fact]
        public async Task BelowTheHeight_TheSenderIsDebitedFirst_AsBefore()
        {
            StateData.TokenRecipientWrite = (_, _) => Task.FromResult(false); // not used below the height
            Assert.True(await StateData.UpdateTreis(TransferBlock(Gate - 1, 10M)));
            Assert.Equal(990M, TokenBalanceOf(Sender));
            Assert.Equal(15M, TokenBalanceOf(Recipient));
        }
    }
}
