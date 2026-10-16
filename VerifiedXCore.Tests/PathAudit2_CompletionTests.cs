using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using SignatureService = VerifiedXCore.Services.SignatureService;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A withdrawal's completion names a request that exists on its own contract (second path audit, 14 October 2026).
    ///
    /// A completion whose (request hash, contract) had no row passed validation "lightweight", and its apply then
    /// "recovered the missing row" by rebuilding every row of that request from its mined transaction and saving them
    /// over what the store held. Naming a request's hash under a different contract therefore reset the request's real
    /// row to freshly requested: a cancelled and refunded withdrawal could be cancelled and refunded again, a paid one
    /// signed and paid again, and a vault's owner lost the credit of a withdrawal that had settled.
    ///
    /// Transactions are judged by VerifyTX and applied with UpdateTreis.
    /// </summary>
    [Collection("DbContextSequential")]
    public class PathAudit2_CompletionTests : IDisposable
    {
        private const long Rule = 300;
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly long _priorRule = Globals.PathAuditRulesHeight;
        private readonly long _priorFreeze = Globals.BitcoinAttestationHeight;
        private readonly long _priorRecord = Globals.BitcoinAttestationRecordHeight;
        private readonly (PrivateKey Key, string Pub, string Address) _requester = NewKey();
        private readonly (PrivateKey Key, string Pub, string Address) _anyone = NewKey();
        private readonly string _vault = Guid.NewGuid().ToString("N") + ":1755000001";
        private readonly string _other = Guid.NewGuid().ToString("N") + ":1755000002";
        private readonly string _requestHash = new string('a', 64);

        public PathAudit2_CompletionTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"pa2complete_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            Globals.PathAuditRulesHeight = Rule;
            Globals.BitcoinAttestationHeight = long.MaxValue;
            Globals.BitcoinAttestationRecordHeight = long.MaxValue;
            Globals.LastBlock = new Block { Height = 199 };
            foreach (var who in new[] { _requester, _anyone })
                StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = who.Address, Balance = 10M, Nonce = 0 });
            foreach (var uid in new[] { _vault, _other })
                SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
                {
                    SmartContractUID = uid, ContractData = VbtcTestContracts.VbtcV2ContractData, MinterAddress = _requester.Address, OwnerAddress = _requester.Address, Nonce = 0,
                });
            // The request, as the chain holds it (block 150) and as every node's table holds it.
            BlockchainData.GetBlocks().InsertSafe(new Block
            {
                Height = 150, Hash = "block-150", PrevHash = "block-149", Timestamp = 1_755_000_150, NumOfTx = 1,
                Transactions = new List<Transaction>
                {
                    new Transaction
                    {
                        Hash = _requestHash, FromAddress = _requester.Address, ToAddress = "TW_Base", TransactionType = TransactionType.VBTC_V2_WITHDRAWAL_REQUEST,
                        Timestamp = 1_755_000_150, Height = 150,
                        Data = $"{{\"Function\":\"VBTCWithdrawalRequest()\",\"ContractUID\":\"{_vault}\",\"BTCAddress\":\"tb1q066af78la3rqmnchc396keujllva6turs52749\",\"Amount\":0.25,\"FeeRate\":10}}",
                    },
                },
            });
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.LastBlock = _priorLastBlock;
            Globals.PathAuditRulesHeight = _priorRule;
            Globals.BitcoinAttestationHeight = _priorFreeze;
            Globals.BitcoinAttestationRecordHeight = _priorRecord;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static (PrivateKey Key, string Pub, string Address) NewKey()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            return (key, pub, AccountData.GetHumanAddress(pub));
        }

        /// <summary>The request's row once its withdrawal has ended (paid, or cancelled and refunded).</summary>
        private void SaveEndedRow(VBTCWithdrawalStatus status)
        {
            Assert.True(VBTCWithdrawalRequest.Save(new VBTCWithdrawalRequest
            {
                RequestorAddress = _requester.Address, SmartContractUID = _vault, Amount = 0.25M, BTCDestination = "tb1q066af78la3rqmnchc396keujllva6turs52749",
                FeeRate = 10, Timestamp = 1_755_000_150, TransactionHash = _requestHash, Status = status, IsCompleted = true,
                BTCTxHash = status == VBTCWithdrawalStatus.Completed ? new string('b', 64) : null, RequestBlockHeight = 150,
            }));
        }

        private VBTCWithdrawalRequest Row() => VBTCWithdrawalRequest.GetByTransactionHash(_requestHash, _vault)!;

        private Transaction Complete((PrivateKey Key, string Pub, string Address) from, string contract, string btcTxid)
        {
            var tx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(), FromAddress = from.Address, ToAddress = from.Address, Amount = 0.0M, Fee = 0.0M, Nonce = 0,
                TransactionType = TransactionType.VBTC_V2_WITHDRAWAL_COMPLETE,
                Data = JsonConvert.SerializeObject(new { Function = "VBTCWithdrawalComplete()", ContractUID = contract, WithdrawalRequestHash = _requestHash, BTCTransactionHash = btcTxid }),
            };
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, from.Key, from.Pub);
            return tx;
        }

        private static async Task<(bool Ok, string Why)> VerifyInBlock(Transaction tx, long height)
        {
            var (ok, why) = await TransactionValidatorService.VerifyTX(tx, false, true, false, null, false, height);
            return (ok, why);
        }

        private static async Task Apply(long height, params Transaction[] txs)
        {
            foreach (var tx in txs) tx.Height = height;
            var block = new Block { Height = height, Hash = $"b{height}", StateRoot = "root" + height, Timestamp = TimeUtil.GetTime(), Transactions = txs.ToList() };
            await StateData.UpdateTreis(block);
            Globals.LastBlock = block;
        }

        [Theory]
        [InlineData(VBTCWithdrawalStatus.Completed)]
        [InlineData(VBTCWithdrawalStatus.Cancelled)]
        public async Task BeforeTheRule_ACompletionNamingAnotherContract_ReopenedTheRequest(VBTCWithdrawalStatus ended)
        {
            SaveEndedRow(ended);
            var foreign = Complete(_anyone, _other, new string('c', 64));   // anyone; the request was never made on that contract

            var (ok, why) = await VerifyInBlock(foreign, Rule - 50);
            Assert.True(ok, why);                                           // the hole: accepted "lightweight"
            await Apply(Rule - 50, foreign);
            Assert.Equal(VBTCWithdrawalStatus.Requested, Row().Status);     // and the ended request is open again
            Assert.False(Row().IsCompleted);
        }

        [Theory]
        [InlineData(VBTCWithdrawalStatus.Completed)]
        [InlineData(VBTCWithdrawalStatus.Cancelled)]
        public async Task FromTheRule_ItIsRefused_AndEvenInABlockItChangesNothing(VBTCWithdrawalStatus ended)
        {
            SaveEndedRow(ended);
            var foreign = Complete(_anyone, _other, new string('c', 64));

            // In a block and at admission alike.
            var (ok, why) = await VerifyInBlock(foreign, Rule);
            Assert.False(ok);
            Assert.Contains($"No withdrawal request {_requestHash} exists on contract {_other}", why);
            Globals.LastBlock = new Block { Height = Rule };
            var admitted = await TransactionValidatorService.VerifyTX(foreign);
            Assert.False(admitted.Item1);
            Assert.Contains("No withdrawal request", admitted.Item2);

            // A block that carried it all the same leaves the request's row as it was.
            await Apply(Rule + 1, foreign);
            Assert.Equal(ended, Row().Status);
            Assert.True(Row().IsCompleted);
            Assert.Null(VBTCWithdrawalRequest.GetByTransactionHash(_requestHash, _other));
        }

        [Fact]
        public async Task ANodeWhoseTableLacksTheRow_StillAcceptsTheRealCompletionInABlock_AndRebuildsOnlyWhatIsMissing()
        {
            // This node's store never got the row (a database copied from elsewhere): the request is on chain.
            Assert.Null(VBTCWithdrawalRequest.GetByTransactionHash(_requestHash, _vault));
            var real = Complete(_requester, _vault, new string('b', 64));

            var (ok, why) = await VerifyInBlock(real, Rule);
            Assert.True(ok, why);
            // Validation wrote nothing (a block that is then refused must leave no trace); the row is rebuilt at apply.
            Assert.Null(VBTCWithdrawalRequest.GetByTransactionHash(_requestHash, _vault));

            await Apply(Rule, real);
            Assert.Equal(VBTCWithdrawalStatus.Completed, Row().Status);
            Assert.Equal(0.25M, Row().Amount);
            Assert.Equal(_requester.Address, Row().RequestorAddress);
            // Completed, it is not opened again by anything that asks for a recovery.
            Assert.NotNull(VBTCWithdrawalStoreRebuildService.TryRecoverRequestRowFromChain(_requestHash, Rule + 5, _vault));
            Assert.Equal(VBTCWithdrawalStatus.Completed, Row().Status);
            Assert.Null(VBTCWithdrawalStoreRebuildService.TryRecoverRequestRowFromChain(_requestHash, Rule + 5, _other));
            Assert.Equal(VBTCWithdrawalStatus.Completed, Row().Status);
        }

        /// <summary>
        /// A node whose table lacks the row judges a completion in a block against the row the chain's request makes, as a
        /// node holding it does: a sender that is neither the requester nor a validator is refused on both, and nothing is
        /// written (round 37).
        /// </summary>
        [Fact]
        public async Task ANodeWhoseTableLacksTheRow_RefusesACompletionFromAnyoneElse_AsANodeHoldingItDoes()
        {
            Assert.Null(VBTCWithdrawalRequest.GetByTransactionHash(_requestHash, _vault));
            var foreign = Complete(_anyone, _vault, new string('b', 64));

            var (okLacking, whyLacking) = await VerifyInBlock(foreign, Rule);
            Assert.False(okLacking);
            Assert.StartsWith("Only the original requester", whyLacking);
            Assert.Null(VBTCWithdrawalRequest.GetByTransactionHash(_requestHash, _vault)); // validation wrote nothing

            // A node holding the row gives the same answer.
            Assert.NotNull(VBTCWithdrawalStoreRebuildService.TryRecoverRequestRowFromChain(_requestHash, Rule, _vault));
            var (okHolding, whyHolding) = await VerifyInBlock(Complete(_anyone, _vault, new string('b', 64)), Rule);
            Assert.False(okHolding);
            Assert.Equal(whyLacking, whyHolding);
        }

        /// <summary>
        /// Two requests mined before the rule that both ended, the first row lost on this node. The recovery of the
        /// first must not land on the second's row: saving falls back to (requester, UniqueId, contract), and the two
        /// carry the same UniqueId.
        /// </summary>
        [Fact]
        public void TheRecovery_NeverWritesOverTheRowOfAnotherRequestWithTheSameUniqueId()
        {
            const string laterHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            // The request of block 150 carries no UniqueId of its own, so its hash is its UniqueId; a later request
            // of the same requester reused that value, was paid, and is the only row this node holds.
            Assert.True(VBTCWithdrawalRequest.Save(new VBTCWithdrawalRequest
            {
                RequestorAddress = _requester.Address, SmartContractUID = _vault, Amount = 0.1M, BTCDestination = "tb1q066af78la3rqmnchc396keujllva6turs52749",
                FeeRate = 10, Timestamp = 1_755_000_160, TransactionHash = laterHash, OriginalUniqueId = _requestHash,
                Status = VBTCWithdrawalStatus.Completed, IsCompleted = true, BTCTxHash = new string('d', 64), RequestBlockHeight = 160,
            }));

            var recovered = VBTCWithdrawalStoreRebuildService.TryRecoverRequestRowFromChain(_requestHash, Rule + 5, _vault);
            Assert.NotNull(recovered);
            Assert.Equal(_requestHash, recovered!.TransactionHash);
            var later = VBTCWithdrawalRequest.GetByTransactionHash(laterHash, _vault)!;
            Assert.Equal(VBTCWithdrawalStatus.Completed, later.Status);     // untouched
            Assert.Equal(0.1M, later.Amount);
            Assert.Equal(new string('d', 64), later.BTCTxHash);
        }
    }
}
