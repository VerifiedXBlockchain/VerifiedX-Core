using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VerifiedXCore.Bitcoin.FROST;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Data;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;
using VfxTx = VerifiedXCore.Models.Transaction;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A withdrawal request a validator gave a share for is signed for good: the Bitcoin transaction stays payable whatever
    /// the chain does. A rewind that removes the request's block (a fork deeper than the bounded reorg, mended by a snapshot
    /// restore) would give its escrow back on the chain the node ends up on. The validator holds the request over the rewind
    /// and puts it back in the mempool, so it is mined again and its escrow stands where the payment is.
    /// </summary>
    [Collection("DbContextSequential")]
    public class DeepReorgSignedRequestTests : BtcAttestTestBase
    {
        private const string BtcDest = "bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4";
        private readonly Func<bool> _priorAtHeight = SignedRequestCarry.AtNetworkHeight;
        private readonly Func<VfxTx, Task> _priorBroadcast = SignedRequestCarry.Broadcast;
        private readonly Func<string, bool> _priorSignedFor = SignedRequestCarry.SignedFor;
        private readonly List<string> _sent = new();

        private static long Tip => Globals.LastBlock.Height;

        public DeepReorgSignedRequestTests() : base(realMainnetRules: false)
        {
            SignedRequestCarry.AtNetworkHeight = () => true;
            SignedRequestCarry.Broadcast = tx => { _sent.Add(tx.Hash); return Task.CompletedTask; };
        }

        public override void Dispose()
        {
            SignedRequestCarry.AtNetworkHeight = _priorAtHeight;
            SignedRequestCarry.Broadcast = _priorBroadcast;
            SignedRequestCarry.SignedFor = _priorSignedFor;
            SnapshotRestoreUtility.ClearStateDestroyed();
            base.Dispose();
        }

        private static VfxTx RequestOf(V who, string vault, decimal amount, long ageSeconds = 0)
        {
            var tx = new VfxTx
            {
                Timestamp = TimeUtil.GetTime() - ageSeconds, FromAddress = who.Address, ToAddress = who.Address, Amount = 0.0M, Fee = 0, Nonce = 0,
                TransactionType = TransactionType.VBTC_V2_WITHDRAWAL_REQUEST,
                Data = JsonConvert.SerializeObject(new { Function = "VBTCWithdrawalRequest()", ContractUID = vault, BTCAddress = BtcDest, Amount = amount, FeeRate = 10, UniqueId = Guid.NewGuid().ToString("N") }),
            };
            tx.Fee = FeeCalcService.CalculateTXFee(tx);
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, who.Key, who.Pub);
            return tx;
        }

        private static int EscrowDebits(string vault, string address, decimal amount) =>
            SmartContractStateTrei.GetSmartContractState(vault)!.SCStateTreiTokenizationTXes?.Count(x => x.FromAddress == address && x.ToAddress == "-" && x.Amount == -amount) ?? 0;

        /// <summary>Applies the next block with <paramref name="txs"/> and stores it, as the node stores every block it applies.</summary>
        private static async Task MineStored(params VfxTx[] txs)
        {
            foreach (var tx in txs)
            {
                var (ok, why) = await Verify(tx, Tip + 1);
                Assert.True(ok, why);
            }
            await Apply(Tip + 1, txs);
            BlockchainData.GetBlocks().InsertSafe(AppliedBlocks.Last());
        }

        [Fact]
        public async Task ARequestThisValidatorSignedFor_IsPutBackOnTheChainARewindLeftItOff()
        {
            // A vault the chain does not watch, its owner holding vBTC on it, and a snapshot of the state before the request.
            var vault = await CreateVault(H1 - 50, keyHolders: Signers);
            await Start();
            foreach (var block in AppliedBlocks) BlockchainData.GetBlocks().InsertSafe(block);
            StateTreiStatusService.SetSynced(Tip);
            var slotHeight = Tip;
            await StateSnapshotService.UpdateCycleAsync(slotHeight, Globals.LastBlock.Hash);
            Assert.Equal(slotHeight, StateSnapshotService.PickVerifiedSlotForRestore(slotHeight)?.Height);

            // Two requests in the next block: this validator gave a share for the first (its evidence is on record), not the second.
            var signedFor = RequestOf(Minter, vault.Uid, 0.0003M);
            var unsigned = RequestOf(Minter, vault.Uid, 0.0001M);
            await MineStored(signedFor, unsigned);
            var requestHeight = Tip;
            Assert.NotNull(VBTCWithdrawalRequest.GetByTransactionHash(signedFor.Hash, vault.Uid));
            Assert.Equal(1, EscrowDebits(vault.Uid, Minter.Address, 0.0003M));
            Assert.True(FrostSignedWithdrawalEvidence.Record(vault.Uid, signedFor.Hash, "aa".PadLeft(64, 'a'), new[] { "aa:0" }));
            for (var i = 0; i < FrostSigningAuthorization.WithdrawalSigningDepth; i++) await MineStored();

            // The chain forks below the request, deeper than the bounded reorg: the node restores the snapshot. The request and its
            // escrow debit are gone with the block; the signature this validator gave is not.
            Assert.True(await SnapshotRestoreUtility.TryRestoreAsync(slotHeight));
            Assert.Null(VBTCWithdrawalRequest.GetByTransactionHash(signedFor.Hash, vault.Uid));
            Assert.Equal(0, EscrowDebits(vault.Uid, Minter.Address, 0.0003M));
            Assert.NotNull(FrostSignedWithdrawalEvidence.Get(vault.Uid, signedFor.Hash));
            var held = Assert.Single(SignedRequestCarry.Held());
            Assert.Equal(signedFor.Hash, held.TxHash);
            Assert.Equal(requestHeight, held.RemovedAtHeight);

            // Back at the network's height, the validator puts the request back in the mempool and sends it on; the one it gave no
            // share for is the requester's to send again, as before. Admission asks Bitcoin for the vault's deposit, as for any request.
            Globals.LastBlock = new Block { Height = slotHeight, Hash = BlockchainData.GetBlockByHeight(slotHeight)!.Hash, Timestamp = TimeUtil.GetTime() };
            // While no Bitcoin server answers, admission refuses it: it stays held for the next pass, and is not let go.
            Assert.Empty(await SignedRequestCarry.ResubmitDueAsync());
            Assert.Equal(1, Assert.Single(SignedRequestCarry.Held()).Attempts);
            Assert.Empty(_sent);
            using var electrum = new FakeElectrum(("a", true, 1M));
            var put = await SignedRequestCarry.ResubmitDueAsync();
            Assert.True(put.Count == 1 && put[0] == signedFor.Hash, $"not put back: {SignedRequestCarry.Held().FirstOrDefault()?.LastRefusal}");
            Assert.Equal(new[] { signedFor.Hash }, _sent);
            Assert.NotNull(TransactionData.GetPool().FindOne(x => x.Hash == signedFor.Hash));
            Assert.Null(TransactionData.GetPool().FindOne(x => x.Hash == unsigned.Hash));
            Assert.Empty(await SignedRequestCarry.ResubmitDueAsync()); // pooled: nothing to do until it is mined

            // Mined again on the chain the node is on now: the row and its one escrow debit stand, and the request is no longer held.
            var pooled = TransactionData.GetPool().FindOne(x => x.Hash == signedFor.Hash)!;
            await MineStored(pooled);
            var row = VBTCWithdrawalRequest.GetByTransactionHash(signedFor.Hash, vault.Uid);
            Assert.NotNull(row);
            Assert.Equal(Tip, row!.RequestBlockHeight);
            Assert.Equal(1, EscrowDebits(vault.Uid, Minter.Address, 0.0003M));
            Assert.Empty(await SignedRequestCarry.ResubmitDueAsync());
            Assert.Empty(SignedRequestCarry.Held());
        }

        /// <summary>
        /// The hold comes before anything is wiped or deleted. A restore that cannot read whether this validator signed for a
        /// request in the blocks it would remove does not start: the chain and the state stay as they are, and the next attempt
        /// holds the request and goes through.
        /// </summary>
        [Fact]
        public async Task ARestoreThatCannotHoldASignedForRequest_DoesNotStart()
        {
            var vault = await CreateVault(H1 - 50, keyHolders: Signers);
            await Start();
            foreach (var block in AppliedBlocks) BlockchainData.GetBlocks().InsertSafe(block);
            StateTreiStatusService.SetSynced(Tip);
            var slotHeight = Tip;
            await StateSnapshotService.UpdateCycleAsync(slotHeight, Globals.LastBlock.Hash);
            var request = RequestOf(Minter, vault.Uid, 0.0003M);
            await MineStored(request);
            var requestHeight = Tip;
            Assert.True(FrostSignedWithdrawalEvidence.Record(vault.Uid, request.Hash, "cc".PadLeft(64, 'c'), new[] { "cc:0" }));

            SignedRequestCarry.SignedFor = _ => throw new System.IO.IOException("the evidence store cannot be read");
            Assert.False(await SnapshotRestoreUtility.TryRestoreAsync(slotHeight));
            Assert.False(SnapshotRestoreUtility.StateDestroyedByFailedRestore);
            Assert.True(StateTreiStatusService.IsSynced());
            Assert.NotNull(BlockchainData.GetBlockByHeight(requestHeight));
            Assert.NotNull(VBTCWithdrawalRequest.GetByTransactionHash(request.Hash, vault.Uid));
            Assert.Equal(1, EscrowDebits(vault.Uid, Minter.Address, 0.0003M));
            Assert.Empty(SignedRequestCarry.Held());

            SignedRequestCarry.SignedFor = _priorSignedFor;
            Assert.True(await SnapshotRestoreUtility.TryRestoreAsync(slotHeight));
            Assert.Null(BlockchainData.GetBlockByHeight(requestHeight));
            Assert.Equal(request.Hash, Assert.Single(SignedRequestCarry.Held()).TxHash);
        }

        /// <summary>
        /// What this cannot mend, pinned so it stays visible: a block takes a transaction only within the chain's age limit
        /// (Globals.MaxTxAgeSeconds), so a signed-for request older than that when the node is back at the network's height
        /// cannot be mined again. It is let go, not retried for ever, and the loss is named for the operator.
        /// </summary>
        [Fact]
        public async Task ASignedForRequestTooOldForTheChain_IsLetGo_NotSent()
        {
            var vault = await CreateVault(H1 - 50, keyHolders: Signers);
            await Start();
            using var electrum = new FakeElectrum(("a", true, 1M));
            var old = RequestOf(Minter, vault.Uid, 0.0003M, ageSeconds: Globals.MaxTxAgeSeconds + 60);
            Assert.True(FrostSignedWithdrawalEvidence.Record(vault.Uid, old.Hash, "dd".PadLeft(64, 'd'), new[] { "dd:0" }));
            Assert.Equal(1, SignedRequestCarry.Hold(new[] { new Block { Height = Tip + 20, Transactions = new List<VfxTx> { old } } }));

            Assert.Empty(await SignedRequestCarry.ResubmitDueAsync());
            Assert.Empty(_sent);
            Assert.Empty(SignedRequestCarry.Held());
            Assert.Null(TransactionData.GetPool().FindOne(x => x.Hash == old.Hash));
        }

        [Fact]
        public async Task ARestoreToTheTip_HoldsNothing_AndNothingIsPutBackOffTheNetworksHeight()
        {
            var vault = await CreateVault(H1 - 50, keyHolders: Signers);
            await Start();
            var request = RequestOf(Minter, vault.Uid, 0.0003M);
            await MineStored(request);
            foreach (var block in AppliedBlocks) BlockchainData.GetBlocks().InsertSafe(block);
            StateTreiStatusService.SetSynced(Tip);
            await StateSnapshotService.UpdateCycleAsync(Tip, Globals.LastBlock.Hash);
            Assert.True(FrostSignedWithdrawalEvidence.Record(vault.Uid, request.Hash, "bb".PadLeft(64, 'b'), new[] { "bb:0" }));

            // A crash restore keeps every block: nothing is removed, nothing is held.
            Assert.True(await SnapshotRestoreUtility.TryRestoreAsync(Tip));
            Assert.Empty(SignedRequestCarry.Held());
            Assert.NotNull(VBTCWithdrawalRequest.GetByTransactionHash(request.Hash, vault.Uid));

            // A held request is put back only at the network's height.
            SignedRequestCarry.Hold(new[] { new Block { Height = Tip + 5, Transactions = new List<VfxTx> { request } } });
            Assert.Single(SignedRequestCarry.Held());
            SignedRequestCarry.AtNetworkHeight = () => false;
            Assert.Empty(await SignedRequestCarry.ResubmitDueAsync());
            Assert.Empty(_sent);
            // At it, one that stands on this chain is let go without being sent anywhere.
            SignedRequestCarry.AtNetworkHeight = () => true;
            Assert.Empty(await SignedRequestCarry.ResubmitDueAsync());
            Assert.Empty(_sent);
            Assert.Empty(SignedRequestCarry.Held());
        }
    }
}
