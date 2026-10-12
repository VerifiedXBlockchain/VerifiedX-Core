using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NBitcoin;
using Newtonsoft.Json;
using VerifiedXCore.Bitcoin.Attestation;
using VerifiedXCore.Bitcoin.Models;
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
    }
}
