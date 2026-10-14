using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VerifiedXCore.Bitcoin.Attestation;
using VerifiedXCore.Bitcoin.Controllers;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;
using BtcTx = NBitcoin.Transaction;
using VfxTx = VerifiedXCore.Models.Transaction;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Bitcoin facts in consensus, part 17: a Bitcoin reorganisation that reaches blocks whose vault events this chain
    /// has already applied (docs/PLAN-vBTC-Bitcoin-Attestation.md, section 7). The validators sign a rewind
    /// (VBTC_V2_BTC_REORG): it names the last height the two branches share and carries the headers of the heavier
    /// branch. Everything the replaced blocks' events did is taken back, and the new branch is reported from there.
    /// Rewinds are judged by VerifyTX and applied by UpdateTreis like every other transaction.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BtcAttest17_DeepReorgTests : BtcAttestTestBase
    {
        private const long Checkpoint = 4032;
        private const string BtcDest = "bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4";
        private Btc _btc = new(Checkpoint - 11);
        private readonly V _holder = NewKey();
        private readonly long _priorGuard = Globals.BridgeIntraBlockGuardHeight;
        private readonly Func<VaultWithdrawalRow, VaultPin, Task<(bool, string)>> _realSign = BtcWithdrawalSigner.SignAndBroadcast;
        private readonly Func<string, Task<bool?>> _realKnown = BtcWithdrawalSigner.KnownOnBitcoin;

        private V Caster => Signers[0];
        private static long Tip => Globals.LastBlock.Height;
        private static AttestationMetaRow Meta => BtcAttestStore.GetMeta()!;
        private static int Depth => BtcAttestRules.Depth;

        public BtcAttest17_DeepReorgTests() : base(realMainnetRules: false)
        {
            _btc.MineEmpty(12);
            Globals.BitcoinCheckpointHeightMainnet = Checkpoint;
            Globals.BitcoinCheckpointHeadersMainnet = string.Concat(_btc.HeaderHexes(Checkpoint - 11, Checkpoint));
            Globals.BridgeIntraBlockGuardHeight = 1;
            Globals.BootstrapCasterAddresses.Add(Caster.Address);
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _holder.Address, Balance = 100M, Nonce = 0 });
            BtcWithdrawalSigner.ForgetAttempts();
        }

        public override void Dispose()
        {
            BtcWithdrawalSigner.SignAndBroadcast = _realSign;
            BtcWithdrawalSigner.KnownOnBitcoin = _realKnown;
            BtcWithdrawalSigner.ForgetAttempts();
            Globals.BootstrapCasterAddresses.Remove(Caster.Address);
            Globals.BridgeIntraBlockGuardHeight = _priorGuard;
            base.Dispose();
        }

        // ── Bitcoin side ────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Mines Bitcoin transactions in one block of the chain the validators follow, buries it and has them report it. Returns the block's height.</summary>
        private async Task<long> MineAndReport(params BtcTx[] txs)
        {
            var height = _btc.Mine(txs);
            _btc.MineEmpty(Depth);
            var headers = _btc.HeaderHexes(Meta.TipHeight + 1, height + Depth);
            var report = Signed(Assert.Single(BtcBlockScanner.BuildForward(_btc.Block(height), height, headers, BtcAttestRules.EpochOf(Tip + 1))));
            var (ok, why) = await Verify(report, Tip + 1);
            Assert.True(ok, why);
            await Apply(Tip + 1, report);
            return height;
        }

        /// <summary>The rewind for a branch that leaves the attested chain after <paramref name="sharedHeight"/>: the fewest headers that outweigh what they replace.</summary>
        private VfxTx RewindTo(Btc branch, long sharedHeight, int? headers = null, IEnumerable<V>? signers = null)
        {
            var count = headers ?? (int)(Meta.TipHeight - sharedHeight) + 1;
            return Signed(BtcBlockScanner.BuildRewind(sharedHeight, branch.HeaderHexes(sharedHeight + 1, sharedHeight + count), BtcAttestRules.EpochOf(Tip + 1)), signers);
        }

        /// <summary>Bitcoin reorganises: the blocks above <paramref name="sharedHeight"/> are replaced by a longer branch, and the validators sign the rewind.</summary>
        private async Task<Btc> Reorganise(long sharedHeight)
        {
            var branch = _btc.ForkAfter(sharedHeight);
            branch.MineEmpty((int)(_btc.Tip - sharedHeight) + 2);
            var rewind = RewindTo(branch, sharedHeight);
            var (ok, why) = await Verify(rewind, Tip + 1);
            Assert.True(ok, why);
            var admitted = await TransactionValidatorService.VerifyTX(rewind);
            Assert.True(admitted.Item1, admitted.Item2);
            await Apply(Tip + 1, rewind);
            _btc = branch;
            return branch;
        }

        // ── VFX side ────────────────────────────────────────────────────────────────────────────────────────

        private static VfxTx SignedBy(V signer, string to, TransactionType type, object data, bool free = false)
        {
            var tx = new VfxTx
            {
                Timestamp = TimeUtil.GetTime(), FromAddress = signer.Address, ToAddress = to, Amount = 0.0M, Fee = 0.0M, Nonce = 0,
                TransactionType = type, Data = JsonConvert.SerializeObject(data),
            };
            if (!free) tx.Fee = VerifiedXCore.Services.FeeCalcService.CalculateTXFee(tx);
            tx.Build();
            tx.Signature = VerifiedXCore.Services.SignatureService.CreateSignature(tx.Hash, signer.Key, signer.Pub);
            return tx;
        }

        private async Task<VfxTx> Mine(VfxTx tx)
        {
            var (ok, why) = await Verify(tx, Tip + 1);
            Assert.True(ok, why);
            await Apply(Tip + 1, tx);
            return tx;
        }

        private VfxTx Transfer(string vault, decimal amount, V? from = null, V? to = null) =>
            SignedBy(from ?? Minter, (to ?? _holder).Address, TransactionType.VBTC_V2_TRANSFER,
                new { Function = "TransferVBTCV2()", ContractUID = vault, FromAddress = (from ?? Minter).Address, ToAddress = (to ?? _holder).Address, Amount = amount });

        private Task<VfxTx> Request(string vault, decimal amount, V? from = null) =>
            Mine(SignedBy(from ?? Minter, (from ?? Minter).Address, TransactionType.VBTC_V2_WITHDRAWAL_REQUEST,
                new { Function = "VBTCWithdrawalRequest()", ContractUID = vault, BTCAddress = BtcDest, Amount = amount, FeeRate = 10, UniqueId = Guid.NewGuid().ToString("N") }));

        private static VaultWithdrawalRow Row(VfxTx request, string vault) => BtcAttestStore.Withdrawal(BtcWithdrawals.RefOf(request.Hash, vault))!;
        private static VaultCoinRow Coin(BtcTx tx, long vout) => BtcAttestStore.Coin(Outpoint(tx, vout))!;

        private async Task<decimal> Spendable(string vault, V who) =>
            (await VBTCService.TryGetAvailableTransparentVbtcBalance(vault, who.Address)).availableBalance;

        // ── Deposits ────────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task ADepositInADroppedBlock_StopsCounting_AndCountsAgainWhenItConfirmsAgain()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            var kept = Pay(vault.Script, 40_000);
            await MineAndReport(kept);
            var dropped = Pay(vault.Script, 150_000);
            var at = await MineAndReport(dropped);
            Assert.Equal(190_000, BtcAttestStore.Total(vault.Uid).HeldSats);
            // The owner pays a holder out of it while it still counts.
            await Mine(Transfer(vault.Uid, 0.001M));
            Assert.Equal(0.0009M, await Spendable(vault.Uid, Minter));

            // Bitcoin drops the block the deposit was in, and the three that buried it.
            var eventsThrough = Meta.EventsThroughHeight;
            Assert.Equal(at, eventsThrough);
            var branch = await Reorganise(at - 1);

            Assert.Equal(at - 1, Meta.EventsThroughHeight);
            Assert.Equal(branch.Header(Meta.TipHeight).Hash, Meta.TipHash);
            Assert.Equal(branch.Header(at).Hash, BtcAttestStore.HeaderAt(at)!.Hash);
            Assert.Equal(VaultCoinStatus.Orphaned, Coin(dropped, 0).Status);
            Assert.Equal(VaultCoinStatus.Unspent, Coin(kept, 0).Status);
            Assert.Equal(40_000, BtcAttestStore.Total(vault.Uid).HeldSats);
            // What was sent on against the dropped deposit cannot be taken back: the holder keeps it, the owner carries it.
            Assert.Equal(0.001M, await Spendable(vault.Uid, _holder));
            Assert.Equal(-0.0006M, await Spendable(vault.Uid, Minter));
            Assert.False((await Verify(Transfer(vault.Uid, 0.00000001M), Tip + 1)).Ok);         // the owner can debit nothing
            Assert.True((await Verify(Transfer(vault.Uid, 0.0002M, _holder, Minter), Tip + 1)).Ok); // a holder's vBTC is a holder's
            var shown = JObject.Parse(await new VBTCController().GetVaultBitcoin(vault.Uid))["Bitcoin"]!;
            Assert.Equal(Outpoint(dropped, 0), (string)Assert.Single((JArray)shown["OrphanedCoins"]!)["Outpoint"]!);
            Assert.Single((JArray)shown["Coins"]!);
            // The orphaned coin is never chosen for a withdrawal: only the 40,000 can be pinned.
            var request = await Request(vault.Uid, 0.0005M, from: _holder);
            Assert.Equal(VaultWithdrawalState.Waiting, Row(request, vault.Uid).State);

            // The same transaction confirms on the new branch, two blocks later: the coin is back, under its outpoint.
            branch.MineEmpty(1);
            var again = await MineAndReport(dropped);
            var coin = Coin(dropped, 0);
            Assert.Equal(again, coin.CreatedInHeight);
            Assert.Equal(190_000, BtcAttestStore.Total(vault.Uid).HeldSats);
            // ...and the withdrawal that was waiting for coins is pinned on it in that same block.
            Assert.Equal(VaultWithdrawalState.Pinned, Row(request, vault.Uid).State);
            Assert.Equal(VaultCoinStatus.Pinned, coin.Status);
            Assert.Equal(0.0009M, await Spendable(vault.Uid, Minter));
        }

        // ── Completions ─────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task ACompletedWithdrawal_IsPinnedAgain_AndCompletedAgainByTheSameTransaction()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            var deposit = Pay(vault.Script, 150_000);
            await MineAndReport(deposit);
            var request = await Request(vault.Uid, 0.001M);
            var pin = Assert.Single(Row(request, vault.Uid).Pins);
            var paying = BtcTx.Parse(pin.TxHex, Network.Main);
            var at = await MineAndReport(paying);
            Assert.Equal(VaultWithdrawalState.Settled, Row(request, vault.Uid).State);
            Assert.Equal(VBTCWithdrawalStatus.Completed, VBTCWithdrawalRequest.GetByTransactionHash(request.Hash, vault.Uid)!.Status);
            Assert.Equal(0.0005M, await Spendable(vault.Uid, Minter));

            await Reorganise(at - 1);

            // The withdrawal is to be paid again, by the same transaction on the same coin; nothing moved for anyone.
            var row = Row(request, vault.Uid);
            Assert.Equal(VaultWithdrawalState.Pinned, row.State);
            Assert.Null(row.SettledTxid);
            Assert.Equal(pin.Txid, Assert.Single(row.Pins).Txid);
            Assert.False(row.Pins[0].Dead);
            Assert.Equal(VaultCoinStatus.Pinned, Coin(deposit, 0).Status);
            Assert.Equal(row.Ref, Coin(deposit, 0).PinnedToRef);
            Assert.Null(Coin(deposit, 0).SpentByTxid);
            Assert.Equal(VaultCoinStatus.Orphaned, Coin(paying, 1).Status);   // its change does not exist yet
            Assert.Equal(150_000, BtcAttestStore.Total(vault.Uid).PinnedSats);
            Assert.Equal(0, BtcAttestStore.Total(vault.Uid).UnspentSats);
            var reopened = VBTCWithdrawalRequest.GetByTransactionHash(request.Hash, vault.Uid)!;
            Assert.Equal(VBTCWithdrawalStatus.Requested, reopened.Status);
            Assert.False(reopened.IsCompleted);
            Assert.Null(reopened.BTCTxHash);
            Assert.Null(reopened.CompletionTxHash);
            Assert.Equal(0.0005M, await Spendable(vault.Uid, Minter));
            var api = new VBTCController();
            Assert.Equal("Awaiting_BTC_Confirmation", (string)JObject.Parse(await api.CompleteWithdrawal(new VBTCWithdrawalCompletePayload { SmartContractUID = vault.Uid, WithdrawalRequestHash = request.Hash }))["Status"]!);
            Assert.Empty((JArray)JObject.Parse(await api.GetWithdrawalHistory(vault.Uid))["WithdrawalHistory"]!);
            // The signers still sign that transaction and no other.
            Assert.True(BtcWithdrawals.IsPinnedTransaction(request.Hash, vault.Uid, pin.Txid).Ok);

            // It confirms on the new branch.
            var again = await MineAndReport(paying);
            row = Row(request, vault.Uid);
            Assert.Equal(VaultWithdrawalState.Settled, row.State);
            Assert.Equal(pin.Txid, row.SettledTxid);
            Assert.Equal(VBTCWithdrawalStatus.Completed, VBTCWithdrawalRequest.GetByTransactionHash(request.Hash, vault.Uid)!.Status);
            Assert.Equal(again, Coin(deposit, 0).SpentInHeight);
            Assert.Equal(VaultCoinStatus.Unspent, Coin(paying, 1).Status);
            Assert.Equal(again, Coin(paying, 1).CreatedInHeight);
            Assert.Equal(50_000, BtcAttestStore.Total(vault.Uid).HeldSats);
            Assert.Equal(0.0005M, await Spendable(vault.Uid, Minter));
            Assert.Single((JArray)JObject.Parse(await api.GetWithdrawalHistory(vault.Uid))["WithdrawalHistory"]!);
        }

        [Fact]
        public async Task AWithdrawalPinnedOnChangeThatNoLongerExists_StaysPinned_AndIsPaidOnceTheChangeIsBack()
        {
            await Start();
            var vault = await CreateVault(H1 + 1, keyHolders: Signers);
            var deposit = Pay(vault.Script, 150_000);
            await MineAndReport(deposit);
            var first = await Request(vault.Uid, 0.001M);
            var firstTx = BtcTx.Parse(Row(first, vault.Uid).Pins[0].TxHex, Network.Main);
            var at = await MineAndReport(firstTx);
            // A second withdrawal is pinned on the change of the first.
            var second = await Request(vault.Uid, 0.0003M);
            var secondPin = Assert.Single(Row(second, vault.Uid).Pins);
            Assert.Equal(new[] { Outpoint(firstTx, 1) }, secondPin.Inputs);

            await Reorganise(at - 1);

            // The first is to be paid again. The second keeps its transaction although its coin is not there:
            // signatures for it may be out, and nothing else is ever signed for it.
            Assert.Equal(VaultWithdrawalState.Pinned, Row(first, vault.Uid).State);
            Assert.Equal(VaultWithdrawalState.Pinned, Row(second, vault.Uid).State);
            Assert.Equal(secondPin.Txid, Assert.Single(Row(second, vault.Uid).Pins).Txid);
            var change = Coin(firstTx, 1);
            Assert.Equal(VaultCoinStatus.Orphaned, change.Status);
            Assert.Equal(Row(second, vault.Uid).Ref, change.PinnedToRef);
            Assert.Equal(150_000, BtcAttestStore.Total(vault.Uid).HeldSats);
            Assert.Equal(0.0002M, await Spendable(vault.Uid, Minter));           // 0.0015 - 0.001 - 0.0003, as before
            // The signers lead the transaction that can be broadcast, not the one whose coin Bitcoin does not have.
            var led = new List<string>();
            BtcWithdrawalSigner.SignAndBroadcast = (_, p) => { led.Add(p.Txid); return Task.FromResult((true, p.Txid)); };
            BtcWithdrawalSigner.KnownOnBitcoin = _ => Task.FromResult<bool?>(false);
            await BtcWithdrawalSigner.TickAsync(Signers[1].Address, Tip + 100);
            Assert.Equal(new[] { firstTx.GetHash().ToString() }, led);

            // The first confirms again: its change is back and is the second's coin at once.
            await MineAndReport(firstTx);
            change = Coin(firstTx, 1);
            Assert.Equal(VaultCoinStatus.Pinned, change.Status);
            Assert.Equal(Row(second, vault.Uid).Ref, change.PinnedToRef);
            Assert.Equal(VaultWithdrawalState.Settled, Row(first, vault.Uid).State);
            await MineAndReport(BtcTx.Parse(secondPin.TxHex, Network.Main));
            Assert.Equal(VaultWithdrawalState.Settled, Row(second, vault.Uid).State);
            Assert.Equal(20_000, BtcAttestStore.Total(vault.Uid).HeldSats);
            Assert.Equal(0.0002M, await Spendable(vault.Uid, Minter));
        }

        [Fact]
        public async Task ACompletedBridgeExit_IsOpenAgain_AndCompletedAgain_WithTheOwnerChargedOnceThroughout()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            await MineAndReport(Pay(vault.Script, 1_000_000));
            var lockId = Guid.NewGuid().ToString("N");
            await Mine(SignedBy(Minter, Minter.Address, TransactionType.VBTC_V2_BRIDGE_LOCK,
                new { Function = "VBTCBridgeLock()", ContractUID = vault.Uid, LockId = lockId, Amount = 0.004M, AmountSats = 400_000L, EvmDestination = "0x" + new string('a', 40) }));
            var burn = "0x" + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            VfxTx Exit(string burnHash) => SignedBy(Caster, Caster.Address, TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC, new
            {
                Function = "VBTCBridgeExitToBTC()", ContractUID = vault.Uid, LockId = lockId, Amount = 0.004M, AmountSats = 400_000L,
                TotalAmount = 0.004M, TotalAmountSats = 400_000L, BtcDestination = BtcDest, BaseBurnTxHash = burnHash,
                Allocations = new List<PoolUnlockAllocation> { new() { LockId = lockId, SmartContractUID = vault.Uid, UnlockAmount = 0.004M } },
            }, free: true);
            await Mine(Exit(burn));
            VaultWithdrawalRow Part() => BtcAttestStore.Withdrawal(BtcWithdrawals.ExitRefOf(VBTCBridgeBtcExitState.GetByBurnHash(burn)!, vault.Uid))!;
            var paying = BtcTx.Parse(Part().Pins[0].TxHex, Network.Main);
            var at = await MineAndReport(paying);
            Assert.True(VBTCBridgeBtcExitState.GetByBurnHash(burn)!.IsComplete);
            Assert.Equal(400_000, BtcAttestStore.Total(vault.Uid).SettledExitSats);
            Assert.Equal(0.006M, await Spendable(vault.Uid, Minter));

            await Reorganise(at - 1);

            Assert.Equal(VaultWithdrawalState.Pinned, Part().State);
            var open = VBTCBridgeBtcExitState.GetByBurnHash(burn)!;
            Assert.False(open.IsComplete);
            Assert.Null(open.BtcTxHash);
            Assert.Equal(0, BtcAttestStore.Total(vault.Uid).SettledExitSats);
            Assert.Equal(1_000_000, BtcAttestStore.Total(vault.Uid).PinnedSats);
            Assert.Equal(0.006M, await Spendable(vault.Uid, Minter));           // 1.0 held - 0.4 locked, as before the exit paid
            // The burn was paid for once and its exit is open: it cannot be used for another exit meanwhile.
            Assert.True(VBTCBridgeConsumedBurn.IsConsumed(burn));
            var (ok, why) = await Verify(Exit(burn), Tip + 1);
            Assert.False(ok);
            Assert.Contains("Duplicate Base burn", why);

            await MineAndReport(paying);
            Assert.Equal(VaultWithdrawalState.Settled, Part().State);
            var done = VBTCBridgeBtcExitState.GetByBurnHash(burn)!;
            Assert.True(done.IsComplete);
            Assert.Equal(paying.GetHash().ToString(), done.BtcTxHash);
            Assert.Equal(400_000, BtcAttestStore.Total(vault.Uid).SettledExitSats);
            Assert.Equal(0.006M, await Spendable(vault.Uid, Minter));
        }

        // ── What is not taken back ──────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task AWithdrawalMadeVoidByADroppedBlock_StaysVoid_AndItsCoinIsFreeAgain()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            var deposit = Pay(vault.Script, 150_000);
            await MineAndReport(deposit);
            await Mine(Transfer(vault.Uid, 0.001M));
            var request = await Request(vault.Uid, 0.001M, from: _holder);
            var pin = Row(request, vault.Uid).Pins[0];
            // The coin leaves in a transaction nobody pinned; the holder is refunded.
            var foreign = Pay(new[] { new OutPoint(deposit, 0) }, (Elsewhere(), 20_000), (vault.Script, 129_000));
            var at = await MineAndReport(foreign);
            Assert.Equal(VaultWithdrawalState.Void, Row(request, vault.Uid).State);
            Assert.Equal(0.001M, await Spendable(vault.Uid, _holder));

            // Bitcoin drops that block. The refund happened in a VFX block and stands: the withdrawal stays closed.
            await Reorganise(at - 1);
            var row = Row(request, vault.Uid);
            Assert.Equal(VaultWithdrawalState.Void, row.State);
            Assert.True(row.Pins[0].Dead);
            Assert.Equal(0.001M, await Spendable(vault.Uid, _holder));
            var coin = Coin(deposit, 0);
            Assert.Equal(VaultCoinStatus.Unspent, coin.Status);
            Assert.Null(coin.PinnedToRef);
            Assert.Null(coin.SpentByTxid);
            Assert.Equal(VaultCoinStatus.Orphaned, Coin(foreign, 1).Status);
            Assert.Equal(150_000, BtcAttestStore.Total(vault.Uid).HeldSats);
            Assert.Equal(0.0005M, await Spendable(vault.Uid, Minter));
            // No signer signs the closed withdrawal's transaction any more.
            Assert.False(BtcWithdrawals.IsPinnedTransaction(request.Hash, vault.Uid, pin.Txid).Ok);

            // If that transaction was already signed and broadcast, Bitcoin can now confirm it. The chain then sees a
            // coin leave that nothing was owed for: the Bitcoin is gone, nothing is completed, and the owner carries it.
            await MineAndReport(BtcTx.Parse(pin.TxHex, Network.Main));
            Assert.Equal(VaultWithdrawalState.Void, Row(request, vault.Uid).State);
            Assert.Equal(VaultCoinStatus.Spent, Coin(deposit, 0).Status);
            Assert.Equal(50_000, BtcAttestStore.Total(vault.Uid).HeldSats);
            Assert.Equal(0.001M, await Spendable(vault.Uid, _holder));
            Assert.Equal(-0.0005M, await Spendable(vault.Uid, Minter));
        }

        // ── The rules ───────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A copy of a rewind the mempool holds is refused before it is verified, as a report's copy is: each copy used to
        /// cost a full check of its signatures and headers (fourth review, round 10). A copy too poorly signed to pass
        /// verification shows it: it is refused as a copy, not for its signatures.
        /// </summary>
        [Fact]
        public async Task ACopyOfAHeldRewind_IsRefusedBeforeItIsVerified()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            var at = await MineAndReport(Pay(vault.Script, 40_000));
            var branch = _btc.ForkAfter(at - 1);
            branch.MineEmpty((int)(_btc.Tip - at) + 3);
            var held = RewindTo(branch, at - 1);
            held.TransactionRating = await TransactionRatingService.GetTransactionRating(held);
            var admitted = await TransactionValidatorService.VerifyTX(held);
            Assert.True(admitted.Item1, admitted.Item2);
            TransactionData.GetPool().InsertSafe(held);

            var copy = RewindTo(branch, at - 1, signers: Signers.Take(1));
            copy.Timestamp = held.Timestamp + 1;
            copy.Build();
            var (ok, why) = await TransactionValidatorService.VerifyTX(copy);
            Assert.False(ok);
            Assert.Contains("already in the mempool", why);
        }

        [Fact]
        public async Task ARewind_NeedsAHeavierBranch_TwoThirdsOfTheSigners_AndSomethingToTakeBack()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            var deposit = Pay(vault.Script, 150_000);
            var at = await MineAndReport(deposit);
            _btc.MineEmpty(2);
            var tip = Meta.TipHeight;                                  // at + Depth
            var branch = _btc.ForkAfter(at - 1);
            branch.MineEmpty(Depth + 6);

            async Task Refused(VfxTx tx, string because)
            {
                var (ok, why) = await Verify(tx, Tip + 1);
                Assert.False(ok, "accepted in a block");
                Assert.Contains(because, why);
                Assert.False((await TransactionValidatorService.VerifyTX(tx)).Item1);
            }

            // As much work as it replaces is not more work.
            await Refused(RewindTo(branch, at - 1, headers: Depth + 1), "do not carry more work");
            // Two of four signers are not two thirds.
            await Refused(RewindTo(branch, at - 1, signers: Signers.Take(2)), "3 of 4 are required");
            // Headers that are not the branch above that height.
            await Refused(Signed(BtcBlockScanner.BuildRewind(at - 1, branch.HeaderHexes(at + 1, at + Depth + 2), 0)), "does not extend");
            // Nothing was applied above the events-through height: that is a report's header replacement, not a rewind.
            var above = _btc.ForkAfter(at);
            above.MineEmpty(Depth + 3);
            await Refused(RewindTo(above, at), "no events were applied above that height");
            await Refused(RewindTo(above, tip), "nothing above that height");
            // Not below the checkpoint.
            await Refused(Signed(BtcBlockScanner.BuildRewind(Checkpoint - 1, branch.HeaderHexes(Checkpoint, Checkpoint + 3), 0)), "below the checkpoint");
            // Headers only.
            var withBlock = BtcBlockScanner.BuildRewind(at - 1, branch.HeaderHexes(at, at + Depth + 1), 0);
            withBlock.BlockHeight = at;
            await Refused(Signed(withBlock), "a height and headers only");

            // A rewind and a report are different transactions and different signed texts.
            var rewind = BtcBlockScanner.BuildRewind(at - 1, branch.HeaderHexes(at, at + Depth + 1), 0);
            var asReport = BtcAttestTxValidator.Create(TransactionType.VBTC_V2_BTC_REPORT, Signed(rewind).Data);
            await Refused(asReport, "carried by its own transaction type");
            var replacement = BtcBlockScanner.BuildHeaderReplacement(at - 1, branch.HeaderHexes(at, at + Depth + 1), 0);
            replacement.Signatures = Signers.Take(3).Select(v => Sign(v, replacement)).ToList();
            await Refused(BtcAttestTxValidator.Create(TransactionType.VBTC_V2_BTC_REORG, replacement.ToCanonicalJson()), "a rewind says that it is one");
            await Refused(BtcAttestTxValidator.Create(TransactionType.VBTC_V2_BTC_REPORT, replacement.ToCanonicalJson()), "that takes a rewind");
            Assert.NotEqual(rewind.Hash(), replacement.Hash());

            // The envelope is the report's: no sender, no value, the sentinel signature.
            var good = RewindTo(branch, at - 1);
            var signedBySomebody = BtcAttestTxValidator.Create(TransactionType.VBTC_V2_BTC_REORG, good.Data);
            signedBySomebody.Signature = VerifiedXCore.Services.SignatureService.CreateSignature(signedBySomebody.Hash, Signers[0].Key, Signers[0].Pub);
            Assert.False((await Verify(signedBySomebody, Tip + 1)).Ok);

            // A block carries one report or one rewind, not both.
            var state = new BtcAttestBlockRules.State();
            Assert.True(BtcAttestBlockRules.TryRegister(good, state).Ok);
            var also = Signed(BtcBlockScanner.BuildQuiet(new List<string>(), 0, _btc.Header(_btc.Tip).Hash));
            Assert.Equal(BtcAttestBlockRules.OneReportReason, BtcAttestBlockRules.TryRegister(also, state).Reason);

            // The good one is accepted, once: afterwards there is nothing left for it to take back.
            Assert.True((await Verify(good, Tip + 1)).Ok);
            await Apply(Tip + 1, good);
            Assert.Equal(at - 1, Meta.EventsThroughHeight);
            Assert.Equal(VaultCoinStatus.Orphaned, Coin(deposit, 0).Status);
            await Refused(good, "no events were applied above that height");
            // Applied a second time by a block that should not carry it, it changes nothing.
            var coinsBefore = JsonConvert.SerializeObject(BtcAttestStore.CoinsOf(vault.Uid));
            await Apply(Tip + 1, good);
            Assert.Equal(coinsBefore.Replace($"\"LastModifiedHeight\":{Tip}", ""), JsonConvert.SerializeObject(BtcAttestStore.CoinsOf(vault.Uid)).Replace($"\"LastModifiedHeight\":{Tip}", ""));
            Assert.Equal(at - 1, Meta.EventsThroughHeight);
        }

        [Fact]
        public async Task ARewindCanReachBackOverSeveralReportedBlocks_AndEachIsReportedAgainInOrder()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            var a = Pay(vault.Script, 100_000);
            var b = Pay(vault.Script, 60_000);
            var c = Pay(vault.Script, 30_000);
            var first = await MineAndReport(a);
            await MineAndReport(b);
            await MineAndReport(c);
            Assert.Equal(190_000, BtcAttestStore.Total(vault.Uid).HeldSats);

            // Everything from the first deposit's block on is replaced.
            var branch = await Reorganise(first - 1);
            Assert.Equal(first - 1, Meta.EventsThroughHeight);
            Assert.Equal(0, BtcAttestStore.Total(vault.Uid).HeldSats);
            Assert.All(new[] { a, b, c }, tx => Assert.Equal(VaultCoinStatus.Orphaned, Coin(tx, 0).Status));

            // On the new branch they confirm in another order, and one of them never does.
            await MineAndReport(c);
            await MineAndReport(a);
            Assert.Equal(130_000, BtcAttestStore.Total(vault.Uid).HeldSats);
            Assert.Equal(VaultCoinStatus.Orphaned, Coin(b, 0).Status);
            Assert.True(Coin(c, 0).CreatedInHeight < Coin(a, 0).CreatedInHeight);
            Assert.Equal(branch.Tip, _btc.Tip);
        }

        /// <summary>
        /// A withdrawal has two transactions (its fee was raised). The first confirms, and another withdrawal is pinned
        /// on its change. Bitcoin then drops that block and confirms the second transaction instead. The first can
        /// never confirm now, so its change will never exist: the withdrawal pinned on it can never be paid. It is
        /// closed and refunded, and its requester asks again; left as it was it would have stayed pinned for good.
        /// </summary>
        [Fact]
        public async Task AWithdrawalPinnedOnChangeThatCanNeverExist_IsClosedAndRefunded()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            var deposit = Pay(vault.Script, 150_000);
            await MineAndReport(deposit);
            await Mine(Transfer(vault.Uid, 0.0003M));
            var first = await Request(vault.Uid, 0.001M);
            await Mine(SignedBy(Minter, Minter.Address, TransactionType.VBTC_V2_COIN_PIN,
                new { Function = BtcWithdrawals.FeeBumpFunction, ContractUID = vault.Uid, WithdrawalRequestHash = first.Hash, FeeRate = 25 }));
            var pins = Row(first, vault.Uid).Pins;
            var (lower, higher) = (BtcTx.Parse(pins[0].TxHex, Network.Main), BtcTx.Parse(pins[1].TxHex, Network.Main));

            // The lower-fee transaction confirms; a holder's withdrawal is pinned on its change.
            var at = await MineAndReport(lower);
            Assert.Equal(VaultWithdrawalState.Settled, Row(first, vault.Uid).State);
            var second = await Request(vault.Uid, 0.0003M, from: _holder);
            Assert.Equal(new[] { Outpoint(lower, 1) }, Row(second, vault.Uid).Pins[0].Inputs);
            Assert.Equal(0M, await Spendable(vault.Uid, _holder));

            // Bitcoin drops that block. The first withdrawal is to be paid again; the second rests on a coin that is not there.
            await Reorganise(at - 1);
            Assert.Equal(VaultWithdrawalState.Pinned, Row(first, vault.Uid).State);
            Assert.Equal(VaultWithdrawalState.Pinned, Row(second, vault.Uid).State);
            Assert.Equal(VaultCoinStatus.Orphaned, Coin(lower, 1).Status);

            // The higher-fee transaction confirms on the new branch. The lower one never can.
            await MineAndReport(higher);
            Assert.Equal(VaultWithdrawalState.Settled, Row(first, vault.Uid).State);
            Assert.Equal(pins[1].Txid, Row(first, vault.Uid).SettledTxid);
            var closed = Row(second, vault.Uid);
            Assert.Equal(VaultWithdrawalState.Void, closed.State);
            Assert.Contains("can never exist", closed.WaitingReason);
            Assert.Equal(0.0003M, await Spendable(vault.Uid, _holder));
            Assert.Equal(50_000, BtcAttestStore.Total(vault.Uid).HeldSats);
            Assert.Equal(0.0002M, await Spendable(vault.Uid, Minter));

            // The holder asks again, and is pinned on the change that does exist.
            var again = await Request(vault.Uid, 0.0003M, from: _holder);
            Assert.Equal(new[] { Outpoint(higher, 1) }, Row(again, vault.Uid).Pins[0].Inputs);
        }
    }
}
