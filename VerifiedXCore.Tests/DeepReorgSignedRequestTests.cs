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
        private readonly long _priorVreeHeight = Globals.VreeActivationHeight;
        private readonly string _priorValidator = Globals.ValidatorAddress;
        private readonly List<string> _sent = new();

        private static long Tip => Globals.LastBlock.Height;

        public DeepReorgSignedRequestTests() : base(realMainnetRules: false)
        {
            SignedRequestCarry.AtNetworkHeight = () => true;
            SignedRequestCarry.Broadcast = tx => { _sent.Add(tx.Hash); return Task.CompletedTask; };
            Globals.VreeActivationHeight = long.MaxValue; // as released: the aged-request rule is not active unless a test sets the height
        }

        public override void Dispose()
        {
            SignedRequestCarry.AtNetworkHeight = _priorAtHeight;
            SignedRequestCarry.Broadcast = _priorBroadcast;
            SignedRequestCarry.SignedFor = _priorSignedFor;
            Globals.VreeActivationHeight = _priorVreeHeight;
            Globals.ValidatorAddress = _priorValidator;
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

        // ── From the activation height: a request past the age limit, in a key holder's block ─────────────────────────

        private static long TooOld => Globals.MaxTxAgeSeconds + LedgerIntegrityRules.BlockTimeMarginSeconds + 60;

        /// <summary>Block validation's verdict on a transaction of the next block, produced by <paramref name="producer"/>.</summary>
        private static async Task<(bool Ok, string Why)> InBlockOf(string? producer, VfxTx tx)
        {
            var (ok, why) = await TransactionValidatorService.VerifyTX(tx, false, true, false, null, false, Tip + 1, TimeUtil.GetTime(), producer);
            return (ok, why);
        }

        /// <summary>
        /// The consensus rule. From the height, a withdrawal request older than its block allows is valid in a block whose
        /// producer holds a share of its vault's key, and nowhere else: not below the height, not in anybody else's block,
        /// not ahead of its block, not a second time, and no other kind of transaction.
        /// </summary>
        [Fact]
        public async Task ARequestPastTheAgeLimit_IsValidOnlyInAKeyHoldersBlock_FromTheHeight()
        {
            var vault = await CreateVault(H1 - 50, keyHolders: Signers);
            await Start();
            var aged = RequestOf(Minter, vault.Uid, 0.0003M, ageSeconds: TooOld);
            var fresh = RequestOf(Minter, vault.Uid, 0.0001M);
            var holder = Signers[0].Address;

            // Not active: refused whoever produced the block, as before.
            var dark = await InBlockOf(holder, aged);
            Assert.False(dark.Ok);
            Assert.Equal(LedgerIntegrityRules.TimestampOutsideItsBlockMessage, dark.Why);

            Globals.VreeActivationHeight = Tip + 1;
            Assert.Equal(new[] { holder }.Concat(Signers.Skip(1).Select(s => s.Address)).OrderBy(a => a), LedgerIntegrityRules.WithdrawalKeyHolders(aged).OrderBy(a => a));
            var held = await InBlockOf(holder, aged);
            Assert.True(held.Ok, held.Why);
            foreach (var other in new[] { null, "", Unfunded.Address, Minter.Address })
            {
                var refused = await InBlockOf(other, aged);
                Assert.False(refused.Ok);
                Assert.Equal(LedgerIntegrityRules.AgedRequestProducerMessage, refused.Why);
            }
            // One block below the height: refused. And the rule never applies below the height from which a block's
            // transactions are judged by the block's own time, whatever the activation height says.
            Globals.VreeActivationHeight = Tip + 2;
            Assert.False((await InBlockOf(holder, aged)).Ok);
            Globals.VreeActivationHeight = Tip + 1;
            Assert.True(LedgerIntegrityRules.AgedRequestRuleActive(Tip + 1));
            var priorPathAudit = Globals.PathAuditRulesHeight;
            Globals.PathAuditRulesHeight = Tip + 2;
            Assert.False(LedgerIntegrityRules.AgedRequestRuleActive(Tip + 1));
            Globals.PathAuditRulesHeight = priorPathAudit;

            // Old has a limit too: past it a request is as expired as any other transaction, in anybody's block.
            var ancient = RequestOf(Minter, vault.Uid, 0.0001M, ageSeconds: LedgerIntegrityRules.AgedRequestMaxAgeSeconds + 60);
            Assert.Equal(LedgerIntegrityRules.TimestampOutsideItsBlockMessage, (await InBlockOf(holder, ancient)).Why);

            // A request of ordinary age needs no such producer; one ahead of its block is refused even from a holder; and no
            // other kind of transaction is let past the age limit.
            Assert.True((await InBlockOf(null, fresh)).Ok);
            var ahead = RequestOf(Minter, vault.Uid, 0.0001M, ageSeconds: -(Globals.MaxFutureSkewSeconds + LedgerIntegrityRules.BlockTimeMarginSeconds + 60));
            Assert.Equal(LedgerIntegrityRules.TimestampOutsideItsBlockMessage, (await InBlockOf(holder, ahead)).Why);
            var agedTransfer = new VfxTx
            {
                Timestamp = TimeUtil.GetTime() - TooOld, FromAddress = Minter.Address, ToAddress = holder, Amount = 0.0M, Fee = 0, Nonce = 0,
                TransactionType = TransactionType.VBTC_V2_TRANSFER,
                Data = JsonConvert.SerializeObject(new { Function = "TransferVBTCV2()", ContractUID = vault.Uid, FromAddress = Minter.Address, ToAddress = holder, Amount = 0.0001M }),
            };
            agedTransfer.Fee = FeeCalcService.CalculateTXFee(agedTransfer);
            agedTransfer.Build();
            agedTransfer.Signature = SignatureService.CreateSignature(agedTransfer.Hash, Minter.Key, Minter.Pub);
            Assert.Equal(LedgerIntegrityRules.TimestampOutsideItsBlockMessage, (await InBlockOf(holder, agedTransfer)).Why);

            // Mined once, it is never valid again, whoever produces the block: the hash guard no longer covers it.
            await Apply(Tip + 1, aged);
            Globals.VreeActivationHeight = 1;
            Assert.NotNull(VBTCWithdrawalRequest.GetByTransactionHash(aged.Hash, vault.Uid));
            var again = await InBlockOf(holder, aged);
            Assert.False(again.Ok);
            Assert.Contains("already mined", again.Why);
        }

        /// <summary>
        /// The validator's side, from the height: a signed-for request past the age limit is no longer let go. It is kept
        /// in this node's mempool, proposed only when this node holds a share of its vault's key, mined in such a block,
        /// and then no longer held. One that is never mined is let go after the give-up time, with the loss named.
        /// </summary>
        [Fact]
        public async Task ASignedForRequestPastTheAgeLimit_IsKeptAndMinedInAKeyHoldersBlock_FromTheHeight()
        {
            var vault = await CreateVault(H1 - 50, keyHolders: Signers);
            await Start();
            using var electrum = new FakeElectrum(("a", true, 1M));
            Globals.VreeActivationHeight = 1;
            var aged = RequestOf(Minter, vault.Uid, 0.0003M, ageSeconds: TooOld);
            var notHeld = RequestOf(Minter, vault.Uid, 0.0001M, ageSeconds: TooOld);
            Assert.True(FrostSignedWithdrawalEvidence.Record(vault.Uid, aged.Hash, "ee".PadLeft(64, 'e'), new[] { "ee:0" }));
            Assert.Equal(1, SignedRequestCarry.Hold(new[] { new Block { Height = Tip + 20, Transactions = new List<VfxTx> { aged, notHeld } } }));

            // Kept past the age limit here, and only what is held; put back and sent on.
            Assert.False(await TransactionData.IsTxTimestampStale(aged));
            Assert.True(await TransactionData.IsTxTimestampStale(notHeld));
            Assert.Equal(new[] { aged.Hash }, await SignedRequestCarry.ResubmitDueAsync());
            Assert.Equal(new[] { aged.Hash }, _sent);
            var pooled = TransactionData.GetPool().FindOne(x => x.Hash == aged.Hash)!;
            Assert.NotNull(pooled);

            // This node proposes it only as a holder of a share of the vault's key. Below the height the pass changes nothing.
            Globals.ValidatorAddress = Unfunded.Address;
            Assert.Empty(TransactionData.DropAgedRequestsOfOtherKeys(new List<VfxTx> { pooled }));
            Globals.VreeActivationHeight = long.MaxValue;
            Assert.Single(TransactionData.DropAgedRequestsOfOtherKeys(new List<VfxTx> { pooled }));
            Globals.VreeActivationHeight = 1;
            Globals.ValidatorAddress = Signers[1].Address;
            Assert.Single(TransactionData.DropAgedRequestsOfOtherKeys(new List<VfxTx> { pooled }));
            Assert.Single(TransactionData.ClosingPasses(new List<VfxTx> { pooled }));

            // Mined in a holder's block: the row and its escrow debit stand, and the request is let go.
            var inBlock = await InBlockOf(Signers[1].Address, pooled);
            Assert.True(inBlock.Ok, inBlock.Why);
            await Apply(Tip + 1, pooled);
            Assert.Equal(1, EscrowDebits(vault.Uid, Minter.Address, 0.0003M));
            Assert.Empty(await SignedRequestCarry.ResubmitDueAsync());
            Assert.Empty(SignedRequestCarry.Held());

            // One that is never mined is not held for ever.
            var stuck = RequestOf(Minter, vault.Uid, 0.0001M, ageSeconds: TooOld);
            Assert.True(FrostSignedWithdrawalEvidence.Record(vault.Uid, stuck.Hash, "ff".PadLeft(64, 'f'), new[] { "ff:0" }));
            Assert.Equal(1, SignedRequestCarry.Hold(new[] { new Block { Height = Tip + 20, Transactions = new List<VfxTx> { stuck } } }));
            var rows = DbContext.DB_vBTC.GetCollection<SignedRequestCarry.Carried>(SignedRequestCarry.CollectionName);
            var row = rows.FindOne(x => x.TxHash == stuck.Hash);
            row.HeldAt = TimeUtil.GetTime() - SignedRequestCarry.GiveUpSeconds - 1;
            rows.Update(row);
            _sent.Clear();
            Assert.Empty(await SignedRequestCarry.ResubmitDueAsync());
            Assert.Empty(_sent);
            Assert.Empty(SignedRequestCarry.Held());
            Assert.True(await TransactionData.IsTxTimestampStale(stuck));
        }

        /// <summary>
        /// The rule reads the block's producer, so block validation must hand it over for every transaction it judges:
        /// without it an aged request is refused in every block (fail closed), and the rule would never apply.
        /// </summary>
        [Fact]
        public void BlockValidation_HandsTheBlocksProducer_ToEveryTransactionItJudges()
        {
            string? source = null;
            for (var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory); dir != null && source == null; dir = dir.Parent)
            {
                var candidate = System.IO.Path.Combine(dir.FullName, "VerifiedXCore", "Services", "BlockValidatorService.cs");
                if (System.IO.File.Exists(candidate)) source = candidate;
            }
            Assert.NotNull(source);
            var calls = System.IO.File.ReadAllLines(source!).Where(l => l.Contains("TransactionValidatorService.VerifyTX(")).ToList();
            Assert.Equal(2, calls.Count);
            Assert.All(calls, l => Assert.Contains("blockValidator: block.Validator", l));
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
