using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.Models;
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
    /// NEW-07 (found by the second independent review; not in the audit; same end state as VX-01): every per-transaction
    /// balance check reads committed state, and the only cross-transaction accounting was typed VBTC_V2_TRANSFER against
    /// itself (tokens: a JArray-only branch wallets never reach). So within ONE block:
    ///  - vBTC V2: holder B with 1.0 sends two TransferVBTCV2() transfers of 1.0 (or a transfer plus a withdrawal
    ///    request, or a transfer plus a bridge lock) — B ends at −1, the recipient at +2, and 2 BTC can be withdrawn.
    ///  - Tokens: two TokenTransfer() of the full balance — the recipients receive twice the holder's tokens.
    ///  - V1 vBTC: one TransferCoinMulti() repeating the same signed input three times — credited three times.
    /// </summary>
    [Collection("DbContextSequential")]
    public class NEW07_SameBlockOverspendTests : IDisposable
    {
        private const string V2 = "7b7b7b7b7b7b7b7b7b7b7b7b7b7b7b7b:1790500000";
        private const string V1 = "7c7c7c7c7c7c7c7c7c7c7c7c7c7c7c7c:1790500001";
        private const string Tok = "7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d:1790500002";
        private const string BtcDest = "tb1qw508d6qejxtdg4y5r3zarvary0c5xw7kxpjzsx";
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly long _priorEscrowHeight = Globals.WithdrawalEscrowHeight;
        private readonly (PrivateKey Key, string Pub, string Address) _owner = NewKey();
        private readonly (PrivateKey Key, string Pub, string Address) _holder = NewKey();
        private readonly (PrivateKey Key, string Pub, string Address) _other = NewKey();

        public NEW07_SameBlockOverspendTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"new07_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorLastBlock = Globals.LastBlock;
            DbContext.Initialize();
            Globals.LastBlock = new Block { Height = 1001 };
            Globals.WithdrawalEscrowHeight = 1;

            // vBTC V2: the holder received 1.0 (not the owner).
            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
            {
                SmartContractUID = V2, ContractData = VbtcTestContracts.VbtcV2ContractData,
                MinterAddress = _owner.Address, OwnerAddress = _owner.Address,
                SCStateTreiTokenizationTXes = new List<SmartContractStateTreiTokenizationTX>
                {
                    new SmartContractStateTreiTokenizationTX { FromAddress = "+", ToAddress = _holder.Address, Amount = 1.0M },
                },
            });

            // V1 vBTC: the holder received 5.
            var v1Body = VbtcTestContracts.BuildContractData(V1, _owner.Address, new List<SmartContractFeatures>
            {
                new SmartContractFeatures
                {
                    FeatureName = FeatureName.Tokenization,
                    FeatureFeatures = JObject.FromObject(new TokenizationFeature { AssetName = "vBTC", AssetTicker = "vBTC", DepositAddress = "tb1qfixturedeposit", PublicKeyProofs = "p", ImageBase = "default" }),
                },
            }, name: "V1 Fixture");
            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
            {
                SmartContractUID = V1, ContractData = v1Body, MinterAddress = _owner.Address, OwnerAddress = _owner.Address,
                SCStateTreiTokenizationTXes = new List<SmartContractStateTreiTokenizationTX>
                {
                    new SmartContractStateTreiTokenizationTX { FromAddress = "+", ToAddress = _holder.Address, Amount = 5M },
                },
            });

            // Fungible token: the holder has 100.
            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
            {
                SmartContractUID = Tok, ContractData = VbtcTestContracts.TokenContractData(Tok, _owner.Address, 1000),
                MinterAddress = _owner.Address, OwnerAddress = _owner.Address, IsToken = true,
                TokenDetails = new TokenDetails { TokenName = "T", TokenTicker = "T", StartingSupply = 1000, CurrentSupply = 1000, ContractOwner = _owner.Address, DecimalPlaces = 2, TokenBurnable = true },
            });

            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei
            {
                Key = _holder.Address, Balance = 100M, Nonce = 0,
                TokenAccounts = new List<TokenAccount> { TokenAccount.CreateTokenAccount(Tok, "T", "T", 100M, 2) },
            });
            foreach (var k in new[] { _owner, _other })
                StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = k.Address, Balance = 100M, Nonce = 0 });
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.WithdrawalEscrowHeight = _priorEscrowHeight;
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

        private static Transaction Signed((PrivateKey Key, string Pub, string Address) signer, string to, TransactionType type, object data, long nonce = 0)
        {
            var tx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(), FromAddress = signer.Address, ToAddress = to, Amount = 0.0M, Fee = 0, Nonce = nonce,
                TransactionType = type, Data = data is string s ? s : JsonConvert.SerializeObject(data),
            };
            tx.Fee = FeeCalcService.CalculateTXFee(tx);
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, signer.Key, signer.Pub);
            return tx;
        }

        // Shapes exactly as the wallets build them.
        private Transaction V2FunctionTransfer(decimal amount, long nonce = 0) =>
            Signed(_holder, _other.Address, TransactionType.TKNZ_TX,
                new { Function = "TransferVBTCV2()", ContractUID = V2, FromAddress = _holder.Address, ToAddress = _other.Address, Amount = amount }, nonce);

        private Transaction V2TypedTransfer(decimal amount, long nonce = 0) =>
            Signed(_holder, _other.Address, TransactionType.VBTC_V2_TRANSFER,
                new { Function = "TransferVBTCV2()", ContractUID = V2, FromAddress = _holder.Address, ToAddress = _other.Address, Amount = amount }, nonce);

        private Transaction V2Withdrawal(decimal amount, long nonce = 0) =>
            Signed(_holder, _holder.Address, TransactionType.VBTC_V2_WITHDRAWAL_REQUEST,
                new { Function = "WithdrawalRequest()", ContractUID = V2, BTCAddress = BtcDest, Amount = amount, FeeRate = 10, UniqueId = Guid.NewGuid().ToString() }, nonce);

        private Transaction V2BridgeLock(decimal amount, long nonce = 0) =>
            Signed(_holder, "Bridge_Base", TransactionType.VBTC_V2_BRIDGE_LOCK,
                new { Function = "BridgeLock()", ContractUID = V2, LockId = Guid.NewGuid().ToString("N"), Amount = amount, AmountSats = (long)(amount * 100_000_000M), EvmDestination = "0x" + new string('a', 40) }, nonce);

        private Transaction TokenTransfer(decimal amount, long nonce = 0) =>
            Signed(_holder, _other.Address, TransactionType.FTKN_TX,
                new { Function = "TokenTransfer()", ContractUID = Tok, FromAddress = _holder.Address, ToAddress = _other.Address, Amount = amount, TokenTicker = "T", TokenName = "T" }, nonce);

        private Transaction TokenBurn(decimal amount, long nonce = 0) =>
            Signed(_holder, "Token_Base", TransactionType.FTKN_BURN,
                new { Function = "TokenBurn()", ContractUID = Tok, FromAddress = _holder.Address, Amount = amount, TokenTicker = "T", TokenName = "T" }, nonce);

        private Transaction V1Multi(int repeats, decimal each)
        {
            var sigInput = "new07" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var input = new VBTCTransferInput
            {
                SCUID = V1, FromAddress = _holder.Address, Amount = each,
                Signature = SignatureService.CreateSignature(sigInput + _other.Address + _holder.Address, _holder.Key, _holder.Pub),
            };
            return Signed(_holder, _other.Address, TransactionType.TKNZ_TX,
                new { Function = "TransferCoinMulti()", Inputs = Enumerable.Repeat(input, repeats).ToArray(), Amount = each * repeats, SignatureInput = sigInput });
        }

        private Transaction V1Transfer(decimal amount, long nonce = 0) =>
            Signed(_holder, _other.Address, TransactionType.TKNZ_TX,
                JsonConvert.SerializeObject(new[] { new { Function = "TransferCoin()", ContractUID = V1, Amount = amount } }), nonce);

        private static (bool Ok, string Reason) Block(params Transaction[] txs)
        {
            var state = new SameBlockDebitGuard.State();
            foreach (var tx in txs)
            {
                var r = SameBlockDebitGuard.TryRegister(tx, state);
                if (!r.Ok) return r;
            }
            return (true, "");
        }

        // ── Why a block-level rule is needed: each transaction alone is valid ───────────────

        [Fact]
        public async Task NEW07_Precondition_EachDebitAlonePassesVerifyTX()
        {
            foreach (var tx in new[] { V2FunctionTransfer(1.0M), V2Withdrawal(1.0M), TokenTransfer(100M), TokenBurn(100M), V1Transfer(5M) })
            {
                var (ok, message) = await TransactionValidatorService.VerifyTX(tx);
                Assert.True(ok, $"{tx.TransactionType}: {message}");
            }
        }

        [Fact]
        public async Task NEW07_Precondition_RepeatedV1MultiInput_PassesVerifyTX()
        {
            // Each input is checked on its own (5 <= 5), so three copies of one signed input pass per-transaction checks.
            var (ok, message) = await TransactionValidatorService.VerifyTX(V1Multi(3, 5M));
            Assert.True(ok, message);
        }

        // ── Block validation (the guard both ValidateBlock paths call) ──────────────────────

        [Fact]
        public void NEW07_PoC_TwoV2FunctionTransfersOfTheFullBalance_Refused()
        {
            var (ok, reason) = Block(V2FunctionTransfer(1.0M, 0), V2FunctionTransfer(1.0M, 1));
            Assert.False(ok);
            Assert.StartsWith(SameBlockDebitGuard.ReasonPrefix, reason);
        }

        [Theory]
        [InlineData("typed+withdrawal")]
        [InlineData("function+withdrawal")]
        [InlineData("typed+bridge")]
        [InlineData("typed+function")]
        public void NEW07_PoC_V2DebitsOfDifferentTypes_CountTogether(string pair)
        {
            var (a, b) = pair switch
            {
                "typed+withdrawal" => (V2TypedTransfer(1.0M, 0), V2Withdrawal(1.0M, 1)),
                "function+withdrawal" => (V2FunctionTransfer(1.0M, 0), V2Withdrawal(1.0M, 1)),
                "typed+bridge" => (V2TypedTransfer(1.0M, 0), V2BridgeLock(1.0M, 1)),
                _ => (V2TypedTransfer(0.6M, 0), V2FunctionTransfer(0.6M, 1)),
            };
            Assert.False(Block(a, b).Ok);
        }

        [Fact]
        public void NEW07_Control_V2DebitsWithinTheBalance_Accepted()
        {
            Assert.True(Block(V2FunctionTransfer(0.5M, 0), V2Withdrawal(0.5M, 1)).Ok);
        }

        [Fact]
        public void NEW07_PoC_TwoTokenTransfersOfTheFullBalance_Refused()
        {
            Assert.False(Block(TokenTransfer(100M, 0), TokenTransfer(100M, 1)).Ok);
            Assert.False(Block(TokenTransfer(60M, 0), TokenBurn(60M, 1)).Ok);
            Assert.True(Block(TokenTransfer(40M, 0), TokenBurn(60M, 1)).Ok); // control
        }

        [Fact]
        public void NEW07_PoC_RepeatedV1MultiInput_Refused_AndTwoV1TransfersCountTogether()
        {
            Assert.False(Block(V1Multi(3, 5M)).Ok);
            Assert.True(Block(V1Multi(1, 5M)).Ok); // control
            Assert.False(Block(V1Transfer(5M, 0), V1Transfer(5M, 1)).Ok);
            Assert.True(Block(V1Transfer(2M, 0), V1Transfer(3M, 1)).Ok); // control
        }

        [Fact]
        public void NEW07_OwnerBalances_AreLeftToThePerTransactionRules()
        {
            // The owner's balance is deposit-backed, not a ledger sum; the guard does not judge it.
            var t1 = Signed(_owner, _other.Address, TransactionType.TKNZ_TX, JsonConvert.SerializeObject(new[] { new { Function = "TransferCoin()", ContractUID = V1, Amount = 50M } }), 0);
            var t2 = Signed(_owner, _other.Address, TransactionType.TKNZ_TX, JsonConvert.SerializeObject(new[] { new { Function = "TransferCoin()", ContractUID = V1, Amount = 50M } }), 1);
            Assert.True(Block(t1, t2).Ok);
        }

        [Fact]
        public void NEW07_OverspendRejection_IsNotAStateCorruptionSignal()
        {
            var (_, reason) = Block(TokenTransfer(100M, 0), TokenTransfer(100M, 1));
            Assert.False(BlockValidatorService.IsStateCorruptionSignal(reason));
        }

        [Fact]
        public void NEW07_ContractUidCaseVariants_CountAsOneContract()
        {
            // LiteDB's default collation ignores case: a transfer naming the UID in upper case reads and debits the
            // same contract record, so it must share the running total.
            Assert.NotNull(SmartContractStateTrei.GetSmartContractState(V2.ToUpperInvariant()));
            var upper = Signed(_holder, _other.Address, TransactionType.TKNZ_TX,
                new { Function = "TransferVBTCV2()", ContractUID = V2.ToUpperInvariant(), FromAddress = _holder.Address, ToAddress = _other.Address, Amount = 1.0M }, 1);
            Assert.False(Block(V2FunctionTransfer(1.0M, 0), upper).Ok);
        }

        // ── Block proposal ──────────────────────────────────────────────────────────────────

        [Fact]
        public void NEW07_Proposal_KeepsTheFirst_DropsTheOverspendAndThatSendersLaterTxs()
        {
            var first = V2FunctionTransfer(1.0M, 0);
            var second = V2Withdrawal(1.0M, 1);
            var later = TokenTransfer(1M, 2);
            var unrelated = Signed(_other, _holder.Address, TransactionType.TX, "", 0);

            var kept = SameBlockDebitGuard.DropSameBlockOverspends(new List<Transaction> { first, second, later, unrelated });

            Assert.Equal(new[] { first.Hash, unrelated.Hash }, kept.Select(t => t.Hash).ToArray());
        }

        // ── Mempool admission (DoubleSpendReplayCheck, the check every ingress calls) ───────

        [Theory]
        [InlineData("v2")]
        [InlineData("token")]
        [InlineData("v1")]
        public async Task NEW07_PoC_Mempool_SecondOverspendingDebitIsRefused(string kind)
        {
            var (first, second) = kind switch
            {
                "v2" => (V2FunctionTransfer(1.0M, 0), V2Withdrawal(1.0M, 1)),
                "token" => (TokenTransfer(100M, 0), TokenTransfer(100M, 1)),
                _ => (V1Transfer(5M, 0), V1Transfer(5M, 1)),
            };
            TransactionData.GetPool().InsertSafe(first);
            Assert.True(await TransactionData.DoubleSpendReplayCheck(second));
        }

        [Fact]
        public async Task NEW07_Control_Mempool_DebitsWithinTheBalanceAreAdmitted()
        {
            TransactionData.GetPool().InsertSafe(TokenTransfer(40M, 0));
            Assert.False(await TransactionData.DoubleSpendReplayCheck(TokenTransfer(60M, 1)));
        }

        // ── Follow-up (third review) ────────────────────────────────────────────────────────

        private static Transaction Raw(string from, TransactionType type, string data, long nonce, string hash) =>
            new Transaction { FromAddress = from, ToAddress = from, TransactionType = type, Data = data, Nonce = nonce, Hash = hash, Amount = 0M, Fee = 0.00001M };

        [Fact]
        public void NEW07_FollowUp_ReserveRecoverMustBeAloneInItsBlock()
        {
            // Recover() sweeps the reserve's whole balance (not a listed debit); a reserve send in the same block wrote
            // a fresh Pending row after the sweep and paid out again at unlock (reviewer PoC E: reserve at -1.0).
            const string reserve = "xRBXreserve0000000000000000000000";
            var recover = Raw(reserve, TransactionType.RESERVE, JsonConvert.SerializeObject(new { Function = "Recover()", RecoveryAddress = "xRecovery", RecoverySigScript = "sig" }), 0, "recover");
            var send = Raw(reserve, TransactionType.TX, "", 1, "send");
            Assert.False(Block(recover, send).Ok);
            Assert.False(Block(send, recover).Ok);
            Assert.True(Block(recover).Ok);                                                        // control
            Assert.True(Block(recover, Raw("xOther", TransactionType.TX, "", 0, "other")).Ok);      // control: other senders
        }

        [Fact]
        public void NEW07_FollowUp_PreEscrowWithdrawalRequestIsNotADebit()
        {
            // Before WithdrawalEscrowHeight the request wrote no debit (burned at COMPLETE); counting it would refuse
            // historical blocks the apply accepts.
            var prior = Globals.WithdrawalEscrowHeight;
            try
            {
                Globals.WithdrawalEscrowHeight = 5_000;
                var wd = V2Withdrawal(1.0M, 1); wd.Height = 4_000;
                var tr = V2FunctionTransfer(1.0M, 0); tr.Height = 4_000;
                Assert.DoesNotContain(SameBlockDebitGuard.GetDebits(wd), d => d.Key.Kind == SameBlockDebitGuard.LedgerKind.VbtcV2);
                Assert.True(Block(tr, wd).Ok);
                wd.Height = 5_000;                                   // control: escrowed request is a debit
                Assert.False(Block(tr, wd).Ok);
            }
            finally { Globals.WithdrawalEscrowHeight = prior; }
        }

        [Fact]
        public void NEW07_FollowUp_WhitelistedHistoricalTransactionsAreNotJudged()
        {
            var a = V2FunctionTransfer(1.0M, 0);
            var b = V2FunctionTransfer(1.0M, 1);
            Globals.BadTxList.Add(b.Hash);
            try { Assert.True(Block(a, b).Ok); }
            finally { Globals.BadTxList.Remove(b.Hash); }
        }

        [Fact]
        public void NEW07_FollowUp_OwnerDebits_JudgedAtProposalButNotAtBlockValidation()
        {
            // Owner balance = deposit (ElectrumX) + owner ledger: policy for admission/proposal only (reviewer PoC G:
            // an owner's two full-balance transfers were admitted, proposed and verified).
            var o1 = Signed(_owner, _other.Address, TransactionType.TKNZ_TX,
                new { Function = "TransferVBTCV2()", ContractUID = V2, FromAddress = _owner.Address, ToAddress = _other.Address, Amount = 1.0M }, 0);
            var o2 = Signed(_owner, _other.Address, TransactionType.TKNZ_TX,
                new { Function = "TransferVBTCV2()", ContractUID = V2, FromAddress = _owner.Address, ToAddress = _other.Address, Amount = 1.0M }, 1);
            // The owner holds no deposit here (no ElectrumX in the test process: deposit 0) and no ledger rows.
            var ownerKey = new SameBlockDebitGuard.DebitKey(SameBlockDebitGuard.LedgerKind.VbtcV2, V2, _owner.Address);
            Assert.Null(SameBlockDebitGuard.CommittedBalance(ownerKey));
            Assert.NotNull(SameBlockDebitGuard.PolicyBalance(ownerKey));
            var kept = SameBlockDebitGuard.DropSameBlockOverspends(new List<Transaction> { o1, o2 }); // default: policy state
            Assert.Equal(new[] { o1.Hash }, kept.Select(t => t.Hash).ToArray());
            Assert.True(Block(o1, o2).Ok); // consensus keeps trusting the producer for owners
        }

        [Fact]
        public async Task NEW07_FollowUp_ProposalPrecheckDoesNotDeleteGuardedSiblings()
        {
            // The proposal's DoubleSpendReplayCheck deleted a valid transaction whose pending sibling overspent; the
            // guard there only defers (DropSameBlockOverspends).
            TransactionData.GetPool().InsertSafe(TokenTransfer(100M, 0));
            var second = TokenTransfer(100M, 1);
            Assert.True(await TransactionData.DoubleSpendReplayCheck(second));                       // admission refuses
            Assert.False(await TransactionData.DoubleSpendReplayCheck(second, skipDebitGuard: true)); // proposal precheck
        }

        [Fact]
        public void NEW07_FollowUp_NativeSpendsExceedingTheBalanceInOneBlock_Refused()
        {
            // Block validation compared each VFX spend alone with the committed balance: with 100 VFX, two sends of 60
            // both passed (reviewer PoC C; honest admission refuses the pair, a producer running modified code did not).
            Transaction Send(decimal amount, long nonce) =>
                new Transaction { FromAddress = _holder.Address, ToAddress = _other.Address, TransactionType = TransactionType.TX, Amount = amount, Fee = 0.00001M, Nonce = nonce, Hash = $"native-{amount}-{nonce}" };
            Assert.False(Block(Send(60M, 0), Send(60M, 1)).Ok);
            Assert.True(Block(Send(40M, 0), Send(50M, 1)).Ok);                                          // control
        }

        [Fact]
        public void NEW07_FollowUp_NativeSenderWithoutAnAccount_NotJudged()
        {
            // VerifyTX lets an arbiter without an account send TKNZ_WD_ARB; historical blocks carry several per block.
            Transaction Arb(long nonce) =>
                new Transaction { FromAddress = "xArbiterWithoutAccount", ToAddress = "xRequestor", TransactionType = TransactionType.TKNZ_WD_ARB, Amount = 0M, Fee = 0.00001M, Nonce = nonce, Hash = $"arb-{nonce}" };
            Assert.True(Block(Arb(0), Arb(1)).Ok);
        }

        [Fact]
        public void NEW07_FollowUp_NftSaleCompletionPaymentsCountAsNativeDebits()
        {
            // Fourth review: Sale_Complete has Amount 0 but its apply debits the buyer for the inner payments; a sale of
            // 100 plus a VFX send of 100 from a buyer holding 100 both passed and the buyer went negative.
            var inner = new List<Transaction> { new Transaction { FromAddress = _holder.Address, ToAddress = _owner.Address, Amount = 60M, Fee = 0M, Data = "1/2" } };
            var sale = new Transaction
            {
                FromAddress = _holder.Address, ToAddress = _owner.Address, TransactionType = TransactionType.NFT_SALE, Amount = 0M, Fee = 0.00001M, Nonce = 0, Hash = "sale",
                Data = JsonConvert.SerializeObject(new { Function = "Sale_Complete()", ContractUID = "x", Royalty = false, Transactions = inner, KeySign = "k" }),
            };
            Assert.Equal(60M, SameBlockDebitGuard.SaleCompletionPayments(sale));
            var send = new Transaction { FromAddress = _holder.Address, ToAddress = _other.Address, TransactionType = TransactionType.TX, Amount = 60M, Fee = 0.00001M, Nonce = 1, Hash = "send" };
            Assert.False(Block(sale, send).Ok);
            Assert.True(Block(sale).Ok); // a lone sale is left to the per-transaction rules
        }

        [Fact]
        public void NEW07_FollowUp_ReserveKeepsItsMinimumAcrossTheBlock()
        {
            // VerifyTX requires a reserve to keep 0.5 VFX after each send; two sends could leave it between 0 and 0.5.
            const string reserve = "xRBXminimum000000000000000000000";
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = reserve, Balance = 10M, Nonce = 0 });
            Transaction Send(decimal amount, long nonce) =>
                new Transaction { FromAddress = reserve, ToAddress = _other.Address, TransactionType = TransactionType.TX, Amount = amount, Fee = 0M, Nonce = nonce, Hash = $"r-{amount}-{nonce}" };
            Assert.False(Block(Send(5M, 0), Send(4.8M, 1)).Ok);
            Assert.True(Block(Send(5M, 0), Send(4.4M, 1)).Ok);
        }

        // ── NEW-13 (fourth/fifth review): tx.Height is not covered by any hash ──────────────

        [Fact]
        public void NEW13_PoC_RelabelledHeightDoesNotChangeTheHash_AndIsReplacedByTheBlockHeight()
        {
            // A producer/relay relabelled an escrowed withdrawal request as pre-escrow (Height 1): no debit at request.
            var prior = Globals.WithdrawalEscrowHeight;
            try
            {
                Globals.WithdrawalEscrowHeight = 5_000;
                var wd = V2Withdrawal(1.0M, 1);
                var hash = wd.Hash;
                wd.Height = 1;
                Assert.Equal(hash, wd.GetHash());                                                   // hash unchanged
                Assert.DoesNotContain(SameBlockDebitGuard.GetDebits(wd), d => d.Key.Kind == SameBlockDebitGuard.LedgerKind.VbtcV2);

                var block = new Block { Height = 5_000, Transactions = new List<Transaction> { V2FunctionTransfer(1.0M, 0), wd } };
                LedgerIntegrityRules.NormalizeTransactionHeights(block);                             // what validation/apply now do
                Assert.Equal(5_000, wd.Height);
                Assert.False(Block(block.Transactions.ToArray()).Ok);                               // escrow debit counted again

                // Mainnet history has non-coinbase transactions with Height 0: normalising (not refusing) keeps them replayable.
                var legacy = new Block { Height = 2_684_414, Transactions = new List<Transaction> { new Transaction { Height = 0, Hash = "legacy" } } };
                LedgerIntegrityRules.NormalizeTransactionHeights(legacy);
                Assert.Equal(2_684_414, legacy.Transactions[0].Height);
            }
            finally { Globals.WithdrawalEscrowHeight = prior; }
        }

        [Fact]
        public void NEW24_PoC_SameSignedTransactionTwiceInABlock_Refused()
        {
            // Sixth review (PoC R6_SameSignedTxRepeatedInOneBlock_AppliedNTimes: five copies of one payment, +500).
            var pay = new Transaction { FromAddress = _holder.Address, ToAddress = _other.Address, TransactionType = TransactionType.TX, Amount = 1M, Fee = 0.00001M, Nonce = 0, Hash = "same-payment" };
            Assert.False(Block(pay, pay).Ok);
            Assert.True(Block(pay).Ok);
        }
    }
}
