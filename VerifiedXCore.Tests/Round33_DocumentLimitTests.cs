using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
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
    /// Documents read whole by later transactions (a token's state, an account's token list) do not grow without bound
    /// from what one cheap transaction adds (fifth review, round 33): from the path-audit height.
    /// </summary>
    [Collection("DbContextSequential")]
    public class Round33_DocumentLimitTests : IDisposable
    {
        private const string TokenX = "5a1d0c2a9b8e4c7d8e1f2a3b4c5d6e7f:1790200000";
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly long _priorAudit;
        private readonly (PrivateKey Key, string Pub, string Address) _owner = NewKey();

        public Round33_DocumentLimitTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"r33doc_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorLastBlock = Globals.LastBlock;
            _priorAudit = Globals.PathAuditRulesHeight;
            DbContext.Initialize();
            Globals.LastBlock = new Block { Height = 100 };
            Globals.PathAuditRulesHeight = 50;

            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
            {
                SmartContractUID = TokenX,
                ContractData = VbtcTestContracts.TokenContractData(TokenX, _owner.Address, 1000),
                MinterAddress = _owner.Address,
                OwnerAddress = _owner.Address,
                IsToken = true,
                TokenDetails = new TokenDetails { TokenName = "X", TokenTicker = "X", StartingSupply = 1000, CurrentSupply = 1000, ContractOwner = _owner.Address, DecimalPlaces = 2, TokenVoting = true },
            });
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei
            {
                Key = _owner.Address, Balance = 100M, Nonce = 0,
                TokenAccounts = new List<TokenAccount> { TokenAccount.CreateTokenAccount(TokenX, "X", "X", 1000M, 2) },
            });
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.LastBlock = _priorLastBlock;
            Globals.PathAuditRulesHeight = _priorAudit;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static (PrivateKey, string, string) NewKey()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            return (key, pub, AccountData.GetHumanAddress(pub));
        }

        private static Transaction TokenTx((PrivateKey Key, string Pub, string Address) signer, string to, object data)
        {
            var tx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(), FromAddress = signer.Address, ToAddress = to, Amount = 0.0M, Fee = 0, Nonce = 0,
                TransactionType = TransactionType.FTKN_TX, Data = JsonConvert.SerializeObject(data),
            };
            tx.Fee = FeeCalcService.CalculateTXFee(tx);
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, signer.Key, signer.Pub);
            return tx;
        }

        private Transaction TopicCreate(string name, string description) =>
            TokenTx(_owner, "Token_Base", new
            {
                Function = "TokenVoteTopicCreate()", ContractUID = TokenX, FromAddress = _owner.Address,
                TokenVoteTopic = new TokenVoteTopic { SmartContractUID = TokenX, TopicUID = "T" + Guid.NewGuid().ToString("N")[..12], TopicName = name, TopicDescription = description, VotingEndDate = TimeUtil.GetTime() + 86_400 },
            });

        private Transaction Ban(string address) =>
            TokenTx(_owner, "Token_Base", new { Function = "TokenBanAddress()", ContractUID = TokenX, FromAddress = _owner.Address, BanAddress = address });

        [Fact]
        public async Task ABanSentAsAnArray_IsStillRefused()
        {
            var tx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(), FromAddress = _owner.Address, ToAddress = "Token_Base", Amount = 0.0M, Fee = 0, Nonce = 0,
                TransactionType = TransactionType.FTKN_TX,
                Data = JsonConvert.SerializeObject(new[] { new { Function = "TokenBanAddress()", ContractUID = TokenX, FromAddress = _owner.Address, BanAddress = new string('R', 5000) } }),
            };
            tx.Fee = FeeCalcService.CalculateTXFee(tx);
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, _owner.Key, _owner.Pub);
            Assert.False((await TransactionValidatorService.VerifyTX(tx)).Item1);
        }

        private Transaction Transfer(string to, decimal amount) =>
            TokenTx(_owner, to, new { Function = "TokenTransfer()", ContractUID = TokenX, FromAddress = _owner.Address, ToAddress = to, Amount = amount, TokenTicker = "X", TokenName = "X" });

        /// <summary>An address holds at most so many tokens: anyone could add one to any address with each token sent to it.</summary>
        [Fact]
        public async Task AnAddressFullOfTokens_IsNotCreditedANewOne_ButStillItsOwn()
        {
            var victim = NewKey().Item3;
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei
            {
                Key = victim, Balance = 1M, Nonce = 0,
                TokenAccounts = Enumerable.Range(0, LedgerIntegrityRules.MaxTokenAccounts).Select(i => TokenAccount.CreateTokenAccount("junk" + i, "J", "J", 1M, 0)).ToList(),
            });
            var (ok, why) = await TransactionValidatorService.VerifyTX(Transfer(victim, 1M));
            Assert.False(ok);
            Assert.Contains($"at most {LedgerIntegrityRules.MaxTokenAccounts} tokens", why);

            var holder = NewKey().Item3;
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei
            {
                Key = holder, Balance = 1M, Nonce = 0,
                TokenAccounts = Enumerable.Range(0, LedgerIntegrityRules.MaxTokenAccounts - 1).Select(i => TokenAccount.CreateTokenAccount("junk" + i, "J", "J", 1M, 0))
                    .Append(TokenAccount.CreateTokenAccount(TokenX, "X", "X", 1M, 2)).ToList(),
            });
            var (heldOk, heldWhy) = await TransactionValidatorService.VerifyTX(Transfer(holder, 1M));
            Assert.True(heldOk, heldWhy);
        }

        /// <summary>A token's name and ticker are copied into every holder's account: bounded at deploy.</summary>
        [Fact]
        public async Task ATokensNameAndTicker_AreBoundedAtDeploy()
        {
            Transaction Deploy(string uid, string name)
            {
                var body = VbtcTestContracts.TokenContractData(uid, _owner.Address, 1000, tokenName: name);
                var tx = new Transaction
                {
                    Timestamp = TimeUtil.GetTime(), FromAddress = _owner.Address, ToAddress = _owner.Address, Amount = 0.0M, Fee = 0, Nonce = 0,
                    TransactionType = TransactionType.FTKN_MINT,
                    Data = JsonConvert.SerializeObject(new[] { new { Function = "TokenDeploy()", ContractUID = uid, Data = body, MD5List = "NA" } }),
                };
                tx.Fee = FeeCalcService.CalculateTXFee(tx);
                tx.Build();
                tx.Signature = SignatureService.CreateSignature(tx.Hash, _owner.Key, _owner.Pub);
                return tx;
            }
            var (ok, why) = await TransactionValidatorService.VerifyTX(Deploy("6b1d0c2a9b8e4c7d8e1f2a3b4c5d6e7f:1790200001", "Short Name"));
            Assert.True(ok, why);
            var (longOk, longWhy) = await TransactionValidatorService.VerifyTX(Deploy("7c1d0c2a9b8e4c7d8e1f2a3b4c5d6e7f:1790200002", new string('N', LedgerIntegrityRules.MaxTokenNameLength + 1)));
            Assert.False(longOk);
            Assert.Contains("name is at most", longWhy);
        }

        [Fact]
        public async Task ATokensBans_AreBoundedInSizeAndNumber()
        {
            var (ok, why) = await TransactionValidatorService.VerifyTX(Ban(NewKey().Item3));
            Assert.True(ok, why);
            var (longOk, longWhy) = await TransactionValidatorService.VerifyTX(Ban(new string('R', LedgerIntegrityRules.MaxBanAddressLength + 1)));
            Assert.False(longOk);
            Assert.Contains("at most", longWhy);

            var token = SmartContractStateTrei.GetSmartContractState(TokenX)!;
            token.TokenDetails!.AddressBlackList = Enumerable.Range(0, LedgerIntegrityRules.MaxTokenBans).Select(i => "Rbanned" + i).ToList();
            SmartContractStateTrei.UpdateSmartContract(token);
            var (fullOk, fullWhy) = await TransactionValidatorService.VerifyTX(Ban(NewKey().Item3));
            Assert.False(fullOk);
            Assert.Contains($"at most {LedgerIntegrityRules.MaxTokenBans} addresses", fullWhy);
        }

        [Fact]
        public async Task ATokensVoteTopics_AreBoundedInSizeAndNumber()
        {
            var (ok, why) = await TransactionValidatorService.VerifyTX(TopicCreate("Budget", "Fund the audit."));
            Assert.True(ok, why);

            var (longOk, longWhy) = await TransactionValidatorService.VerifyTX(TopicCreate("Budget", new string('d', LedgerIntegrityRules.MaxTopicDescriptionLength + 1)));
            Assert.False(longOk);
            Assert.Contains("description", longWhy);

            var token = SmartContractStateTrei.GetSmartContractState(TokenX)!;
            token.TokenDetails!.TokenTopicList = Enumerable.Range(0, LedgerIntegrityRules.MaxTokenTopics)
                .Select(i => new TokenVoteTopic { SmartContractUID = TokenX, TopicUID = "old" + i, TopicName = "n", TopicDescription = "d" }).ToList();
            SmartContractStateTrei.UpdateSmartContract(token);
            var (fullOk, fullWhy) = await TransactionValidatorService.VerifyTX(TopicCreate("Budget", "Fund the audit."));
            Assert.False(fullOk);
            Assert.Contains($"at most {LedgerIntegrityRules.MaxTokenTopics} vote topics", fullWhy);

            // Below the path-audit height the topic is judged as before.
            Globals.PathAuditRulesHeight = 1_000;
            var (oldOk, oldWhy) = await TransactionValidatorService.VerifyTX(TopicCreate("Budget", new string('d', LedgerIntegrityRules.MaxTopicDescriptionLength + 1)));
            Assert.True(oldOk, oldWhy);
        }
    }
}
