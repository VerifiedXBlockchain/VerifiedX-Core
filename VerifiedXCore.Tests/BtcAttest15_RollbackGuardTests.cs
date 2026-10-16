using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NBitcoin;
using Newtonsoft.Json;
using VerifiedXCore.Bitcoin.Attestation;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;
using Xunit;
using BtcTx = NBitcoin.Transaction;
using VfxBlock = VerifiedXCore.Models.Block;
using VfxTx = VerifiedXCore.Models.Transaction;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Bitcoin facts in consensus, part 15: undoing blocks. The fast rollback undoes a block one transaction at a time
    /// and is allowed only when every transaction is a plain balance move. Bitcoin attestation also writes state at the
    /// start of a block that carries nothing of its own (it closes and refunds an unpinned withdrawal past its window,
    /// and stores the signer set at the start of an epoch). Such a block must not be undone that way: its writes would
    /// stay behind, and another block at the same height would be judged against a state it never produced.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BtcAttest15_RollbackGuardTests : BtcAttestTestBase
    {
        private const long Checkpoint = 4032;
        private const string BtcDest = "bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4";
        private readonly Btc _btc = new(Checkpoint - 11);

        public BtcAttest15_RollbackGuardTests() : base(realMainnetRules: false)
        {
            _btc.MineEmpty(12);
            Globals.BitcoinCheckpointHeightMainnet = Checkpoint;
            Globals.BitcoinCheckpointHeadersMainnet = string.Concat(_btc.HeaderHexes(Checkpoint - 11, Checkpoint));
        }

        private static long Tip => Globals.LastBlock.Height;

        /// <summary>A block as the rollback sees it: plain transfers only, or nothing at all.</summary>
        private static VfxBlock PlainBlock(long height, int transfers = 1) => new()
        {
            Height = height,
            Transactions = Enumerable.Range(0, transfers).Select(i => new VfxTx { TransactionType = TransactionType.TX, FromAddress = "RSender", ToAddress = "RReceiver", Hash = $"t{height}-{i}", Amount = 1M }).ToList(),
        };

        private static string? Undoing(params long[] heights) =>
            BlockRollbackUtility.WhyNotIncremental(heights.OrderByDescending(h => h).Select(h => PlainBlock(h)), heights.Min() - 1);

        private async Task<VfxTx> Cancel(string vault, string requestHash)
        {
            var tx = new VfxTx
            {
                Timestamp = TimeUtil.GetTime(), FromAddress = Minter.Address, ToAddress = Minter.Address, Amount = 0.0M, Fee = 0, Nonce = 0,
                TransactionType = TransactionType.VBTC_V2_WITHDRAWAL_CANCEL,
                Data = JsonConvert.SerializeObject(new { Function = "VBTCWithdrawalCancel()", ContractUID = vault, WithdrawalRequestHash = requestHash, FailureProof = "" }),
            };
            tx.Fee = VerifiedXCore.Services.FeeCalcService.CalculateTXFee(tx);
            tx.Build();
            tx.Signature = VerifiedXCore.Services.SignatureService.CreateSignature(tx.Hash, Minter.Key, Minter.Pub);
            var (ok, why) = await Verify(tx, Tip + 1);
            Assert.True(ok, why);
            await Apply(Tip + 1, tx);
            return tx;
        }

        private async Task Report(params BtcTx[] txs)
        {
            var at = _btc.Mine(txs);
            _btc.MineEmpty(BtcAttestRules.Depth);
            await Apply(Tip + 1, Signed(Assert.Single(BtcBlockScanner.BuildForward(_btc.Block(at), at, _btc.HeaderHexes(BtcAttestStore.GetMeta()!.TipHeight + 1, _btc.Tip), 0))));
        }

        private async Task<VfxTx> Request(string vault, decimal amount)
        {
            var tx = new VfxTx
            {
                Timestamp = TimeUtil.GetTime(), FromAddress = Minter.Address, ToAddress = Minter.Address, Amount = 0.0M, Fee = 0, Nonce = 0,
                TransactionType = TransactionType.VBTC_V2_WITHDRAWAL_REQUEST,
                Data = JsonConvert.SerializeObject(new { Function = "VBTCWithdrawalRequest()", ContractUID = vault, BTCAddress = BtcDest, Amount = amount, FeeRate = 10, UniqueId = Guid.NewGuid().ToString("N") }),
            };
            tx.Fee = VerifiedXCore.Services.FeeCalcService.CalculateTXFee(tx);
            tx.Build();
            tx.Signature = VerifiedXCore.Services.SignatureService.CreateSignature(tx.Hash, Minter.Key, Minter.Pub);
            var (ok, why) = await Verify(tx, Tip + 1);
            Assert.True(ok, why);
            await Apply(Tip + 1, tx);
            return tx;
        }

        /// <summary>
        /// Delayed reserve payments settle after each block timed past their unlock time; undoing a block's transactions
        /// does not reverse a settlement it caused (round 29). Such blocks are not undone the fast way.
        /// </summary>
        [Fact]
        public async Task ABlockThatSettledAReservePayment_IsNotUndoneOneTransactionAtATime()
        {
            await Apply(H1 - 1);
            var due = (BlockchainData.GetBlockByHeight(H1 - 2)?.Timestamp ?? 1_700_000_000L) + 500;
            ReserveTransactions.GetReserveTransactionsDb()!.Insert(new ReserveTransactions { Id = Guid.NewGuid(), Hash = new string('e', 64), FromAddress = "xRBXa", ToAddress = "R",
                TransactionType = TransactionType.TX, ReserveTransactionStatus = ReserveTransactionStatus.Confirmed, ConfirmTimestamp = due, Signature = "s" });
            VfxBlock Timed(long height, long time) { var b = PlainBlock(height); b.Timestamp = time; return b; }
            Assert.Contains("reserve payment", BlockRollbackUtility.WhyNotIncremental(new[] { Timed(H1 - 1, due + 10) }, H1 - 2) ?? "");
            Assert.DoesNotContain("reserve payment", BlockRollbackUtility.WhyNotIncremental(new[] { Timed(H1 - 1, due - 10) }, H1 - 2) ?? "");
        }

        [Fact]
        public async Task ABlockThatChangedAttestationState_IsNeverUndoneOneTransactionAtATime()
        {
            // Before the recording height the layer has written nothing: plain blocks are undone the fast way, as ever.
            await Apply(H1 - 1);
            Assert.Null(Undoing(H1 - 1));
            Assert.Contains("non-reversible tx type", BlockRollbackUtility.WhyNotIncremental(
                new[] { new VfxBlock { Height = H1 - 1, Transactions = new List<VfxTx> { new() { TransactionType = TransactionType.NFT_TX, Hash = "n" } } } }, H1 - 2));

            // The recording height: the Bitcoin view starts and the first signer set is stored, in a block that may carry nothing.
            await Start();
            Assert.Contains("Bitcoin attestation state was written above block " + (H1 - 1), Undoing(H1));

            // Blocks after it that the layer did not touch are undone the fast way again.
            await Apply(H1 + 1);
            await Apply(H1 + 2);
            Assert.Null(Undoing(H1 + 2));
            Assert.Null(Undoing(H1 + 1, H1 + 2));
            Assert.NotNull(Undoing(H1, H1 + 1, H1 + 2));

            // A vault, a deposit, and two requests of which the second has to wait for coins.
            var vault = await CreateVault(Tip + 1);
            Assert.NotNull(Undoing(Tip));
            _btc.Mine(Pay(vault.Script, 150_000));
            _btc.MineEmpty(BtcAttestRules.Depth);
            var deposited = _btc.Tip - BtcAttestRules.Depth;
            await Apply(Tip + 1, Signed(Assert.Single(BtcBlockScanner.BuildForward(_btc.Block(deposited), deposited, _btc.HeaderHexes(BtcAttestStore.GetMeta()!.TipHeight + 1, _btc.Tip), 0))));
            await Request(vault.Uid, 0.001M);
            var waiting = await Request(vault.Uid, 0.0002M);
            Assert.Equal(VaultWithdrawalState.Waiting, BtcAttestStore.Withdrawal(BtcWithdrawals.RefOf(waiting.Hash, vault.Uid))!.State);
            await Apply(Tip + 1);
            Assert.Null(Undoing(Tip));

            // The block in which the waiting request's window ends carries no transaction at all. Applying it closes
            // the request and refunds it. It is not undone the fast way...
            var expiry = waiting.Height + VBTCWithdrawalRequest.EXPIRY_BLOCKS + 1;
            await Apply(expiry - 1);
            Assert.Null(Undoing(expiry - 1));
            await Apply(expiry);
            Assert.Equal(VaultWithdrawalState.Expired, BtcAttestStore.Withdrawal(BtcWithdrawals.RefOf(waiting.Hash, vault.Uid))!.State);
            Assert.True(BtcAttestStore.ChangedAbove(expiry - 1));
            Assert.False(BtcAttestStore.ChangedAbove(expiry));
            Assert.Contains("Bitcoin attestation state was written above block " + (expiry - 1), Undoing(expiry));
            // ...nor is any range that reaches back to it; the blocks after it are.
            await Apply(expiry + 1);
            await Apply(expiry + 2);
            Assert.Null(Undoing(expiry + 1, expiry + 2));
            Assert.NotNull(Undoing(expiry, expiry + 1, expiry + 2));

            // The first block of the next signer epoch stores a signer set.
            var epochStart = H1 + BtcAttestRules.EpochBlocks;
            await Apply(epochStart - 1);
            Assert.Null(Undoing(epochStart - 1));
            await Apply(epochStart);
            Assert.NotNull(Undoing(epochStart));
            await Apply(epochStart + 1);
            Assert.Null(Undoing(epochStart + 1));
        }

        /// <summary>
        /// The withdrawal table is written from blocks, and a node that lost it puts it back by scanning the blocks
        /// for withdrawal transactions. On a vault the chain watches no such transaction ends a withdrawal: a report
        /// completes it, or the chain closes it by rule. The rebuilt table must still say what the chain says, or that
        /// node would count the owner's balance differently from every other.
        /// </summary>
        [Fact]
        public async Task AWithdrawalTableRebuiltFromBlocks_SaysWhatTheChainSays_ForAWatchedVault()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            await Report(Pay(vault.Script, 150_000));
            var paid = await Request(vault.Uid, 0.001M);
            await Report(BtcTx.Parse(BtcAttestStore.Withdrawal(BtcWithdrawals.RefOf(paid.Hash, vault.Uid))!.Pins[0].TxHex, Network.Main));
            var open = await Request(vault.Uid, 0.0003M);        // pinned on the change, not paid yet
            var cancelled = await Request(vault.Uid, 0.0001M);   // waits for coins
            await Cancel(vault.Uid, cancelled.Hash);
            var expires = await Request(vault.Uid, 0.00005M);    // waits, and runs out of its window
            await Apply(expires.Height + VBTCWithdrawalRequest.EXPIRY_BLOCKS + 1);

            (VBTCWithdrawalStatus Status, bool IsCompleted, string? BtcTx, decimal Amount, long Height) Of(VfxTx request)
            {
                var row = VBTCWithdrawalRequest.GetByTransactionHash(request.Hash, vault.Uid)!;
                return (row.Status, row.IsCompleted, row.BTCTxHash, row.Amount, row.RequestBlockHeight);
            }
            var requests = new[] { paid, open, cancelled, expires };
            var before = requests.Select(Of).ToList();
            Assert.Equal(new[] { VBTCWithdrawalStatus.Completed, VBTCWithdrawalStatus.Requested, VBTCWithdrawalStatus.Cancelled, VBTCWithdrawalStatus.Cancelled }, before.Select(b => b.Status));
            var ownerBefore = (await VBTCService.TryGetAvailableTransparentVbtcBalance(vault.Uid, Minter.Address)).availableBalance;
            Assert.Equal(0.0002M, ownerBefore);   // 0.0015 - 0.001 paid - 0.0003 open

            // The node loses the table and rebuilds it from its blocks.
            foreach (var block in AppliedBlocks)
                BlockchainData.GetBlocks().InsertSafe(block);
            VBTCWithdrawalRequest.GetVBTCWithdrawalRequestDb()!.DeleteAll();
            Assert.Null(VBTCWithdrawalRequest.GetByTransactionHash(paid.Hash, vault.Uid));
            var result = await VBTCWithdrawalStoreRebuildService.RebuildFromChainAsync("test");
            Assert.True(result.Success, result.Message);
            Assert.Equal(4, result.RequestsInserted);
            Assert.Equal(3, result.AttestedOutcomesApplied);
            Assert.Equal(0, result.Errors);

            Assert.Equal(before, requests.Select(Of).ToList());
            Assert.Equal(ownerBefore, (await VBTCService.TryGetAvailableTransparentVbtcBalance(vault.Uid, Minter.Address)).availableBalance);
            // The cancel of a watched vault's request opened no vote.
            Assert.Empty(VBTCWithdrawalCancellation.GetCancellationsFor(cancelled.Hash, vault.Uid));
            // Running it again changes nothing.
            var again = await VBTCWithdrawalStoreRebuildService.RebuildFromChainAsync("test, again");
            Assert.Equal(0, again.RequestsInserted);
            Assert.Equal(0, again.AttestedOutcomesApplied);
            Assert.Equal(before, requests.Select(Of).ToList());

            // A request on a watched vault that live processing gave no row (its escrow debit was not written, so it
            // was never recorded in the attestation table either) gets none from the rebuild.
            VBTCWithdrawalRequest.GetVBTCWithdrawalRequestDb()!.DeleteAll();
            DbContext.DB_BtcAttest.GetCollection(BtcAttestStore.Withdrawals).Delete(BtcWithdrawals.RefOf(open.Hash, vault.Uid));
            var withoutRecord = await VBTCWithdrawalStoreRebuildService.RebuildFromChainAsync("test, a request live never wrote");
            Assert.True(withoutRecord.Success, withoutRecord.Message);
            Assert.Null(VBTCWithdrawalRequest.GetByTransactionHash(open.Hash, vault.Uid));
            Assert.NotNull(VBTCWithdrawalRequest.GetByTransactionHash(paid.Hash, vault.Uid));
        }
    }
}
