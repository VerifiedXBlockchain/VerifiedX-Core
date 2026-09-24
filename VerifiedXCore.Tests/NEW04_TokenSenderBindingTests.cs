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
using VerifiedXCore.Models.SmartContracts;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// NEW-04 (found by the independent review; not in the audit; same class as VX-02): TokenTransfer(), TokenBurn() and
    /// TokenVoteTopicCast() took the holder from the transaction DATA ("FromAddress") and never compared it with the
    /// signer. The apply debited that holder, so anyone could sign a transaction moving or burning any holder's
    /// fungible tokens, or cast their vote. A negative Amount passed the balance check and minted.
    /// </summary>
    [Collection("DbContextSequential")]
    public class NEW04_TokenSenderBindingTests : IDisposable
    {
        private const string TokenX = "4f1d0c2a9b8e4c7d8e1f2a3b4c5d6e7f:1790200000";
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly (PrivateKey Key, string Pub, string Address) _victim = NewKey();
        private readonly (PrivateKey Key, string Pub, string Address) _attacker = NewKey();

        public NEW04_TokenSenderBindingTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"new04_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorLastBlock = Globals.LastBlock;
            DbContext.Initialize();
            Globals.LastBlock = new Block { Height = 100 };

            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
            {
                SmartContractUID = TokenX,
                ContractData = VbtcTestContracts.TokenContractData(TokenX, _victim.Address, 1000),
                MinterAddress = _victim.Address,
                OwnerAddress = _victim.Address,
                IsToken = true,
                TokenDetails = new TokenDetails { TokenName = "X", TokenTicker = "X", StartingSupply = 1000, CurrentSupply = 1000, ContractOwner = _victim.Address, DecimalPlaces = 2, TokenBurnable = true },
            });
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei
            {
                Key = _victim.Address, Balance = 100M, Nonce = 0,
                TokenAccounts = new List<TokenAccount> { TokenAccount.CreateTokenAccount(TokenX, "X", "X", 1000M, 2) },
            });
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei
            {
                Key = _attacker.Address, Balance = 100M, Nonce = 0,
                TokenAccounts = new List<TokenAccount> { TokenAccount.CreateTokenAccount(TokenX, "X", "X", 5M, 2) },
            });
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.LastBlock = _priorLastBlock;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static (PrivateKey, string, string) NewKey()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            return (key, pub, AccountData.GetHumanAddress(pub));
        }

        /// <summary>A token TX shaped exactly as TokenContractService builds it, signed by <paramref name="signer"/>.</summary>
        private static Transaction TokenTx((PrivateKey Key, string Pub, string Address) signer, string to, TransactionType type, object data)
        {
            var tx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(),
                FromAddress = signer.Address,
                ToAddress = to,
                Amount = 0.0M,
                Fee = 0,
                Nonce = 0,
                TransactionType = type,
                Data = JsonConvert.SerializeObject(data),
            };
            tx.Fee = FeeCalcService.CalculateTXFee(tx);
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, signer.Key, signer.Pub);
            return tx;
        }

        private Transaction Transfer((PrivateKey, string, string Address) signer, string dataFrom, string to, decimal amount) =>
            TokenTx(signer, to, TransactionType.FTKN_TX,
                new { Function = "TokenTransfer()", ContractUID = TokenX, FromAddress = dataFrom, ToAddress = to, Amount = amount, TokenTicker = "X", TokenName = "X" });

        [Fact]
        public async Task NEW04_PoC_AttackerSignsTransferOfTheVictimsTokens_Refused()
        {
            var (ok, message) = await TransactionValidatorService.VerifyTX(Transfer(_attacker, _victim.Address, _attacker.Address, 1000M));
            Assert.False(ok);
            Assert.Contains("FromAddress must be the transaction signer", message);
        }

        [Fact]
        public async Task NEW04_Control_HolderTransfersOwnTokens_Accepted()
        {
            var (ok, message) = await TransactionValidatorService.VerifyTX(Transfer(_victim, _victim.Address, _attacker.Address, 10M));
            Assert.True(ok, message);
        }

        [Fact]
        public async Task NEW04_NegativeAmount_Refused()
        {
            var (ok, message) = await TransactionValidatorService.VerifyTX(Transfer(_attacker, _attacker.Address, _victim.Address, -500M));
            Assert.False(ok);
            Assert.Contains("greater than zero", message);
        }

        [Fact]
        public async Task NEW04_CreditToADifferentAddressThanTheTransaction_Refused()
        {
            var tx = TokenTx(_victim, _attacker.Address, TransactionType.FTKN_TX,
                new { Function = "TokenTransfer()", ContractUID = TokenX, FromAddress = _victim.Address, ToAddress = "xSOMEONE_ELSE", Amount = 1M, TokenTicker = "X", TokenName = "X" });
            var (ok, message) = await TransactionValidatorService.VerifyTX(tx);
            Assert.False(ok);
            Assert.Contains("ToAddress must be", message);
        }

        [Fact]
        public async Task NEW04_PoC_AttackerBurnsTheVictimsTokens_Refused()
        {
            var tx = TokenTx(_attacker, "Token_Base", TransactionType.FTKN_BURN,
                new { Function = "TokenBurn()", ContractUID = TokenX, FromAddress = _victim.Address, Amount = 1000M, TokenTicker = "X", TokenName = "X" });
            var (ok, message) = await TransactionValidatorService.VerifyTX(tx);
            Assert.False(ok);
            Assert.Contains("FromAddress must be the transaction signer", message);
        }

        [Fact]
        public async Task NEW04_NegativeBurn_Refused()
        {
            var tx = TokenTx(_attacker, "Token_Base", TransactionType.FTKN_BURN,
                new { Function = "TokenBurn()", ContractUID = TokenX, FromAddress = _attacker.Address, Amount = -1000M, TokenTicker = "X", TokenName = "X" });
            var (ok, message) = await TransactionValidatorService.VerifyTX(tx);
            Assert.False(ok);
            Assert.Contains("greater than zero", message);
        }

        [Fact]
        public async Task NEW04_PoC_AttackerCastsTheVictimsVote_Refused()
        {
            var tx = TokenTx(_attacker, _victim.Address, TransactionType.FTKN_TX,
                new { Function = "TokenVoteTopicCast()", ContractUID = TokenX, FromAddress = _victim.Address, TopicUID = "topic-1", VoteType = 1 });
            var (ok, message) = await TransactionValidatorService.VerifyTX(tx);
            Assert.False(ok);
            Assert.Contains("vote FromAddress must be the transaction signer", message);
        }
    }
}
