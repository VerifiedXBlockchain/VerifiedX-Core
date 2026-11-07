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
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;
using BtcTx = NBitcoin.Transaction;
using VfxTx = VerifiedXCore.Models.Transaction;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Bitcoin facts in consensus, part 23: payment attribution on a vault the chain watches, from the VREE height. A
    /// withdrawal or exit part used to be paid only as the exact transaction state fixed for it. After a VFX rewind that
    /// removed the request, the transaction its signers had signed was unknown to the chain the node ended up on: when it
    /// confirmed it was a foreign spend, and the request mined again was voided and refunded, or stuck. Now a transaction
    /// of the vault's coins that pays an open withdrawal's or exit part's destination the amount owed, less the fee it
    /// paid, settles it; one nothing was open for is kept and settles the request or exit applied later for it.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BtcAttest23_PaymentAttributionTests : BtcAttestTestBase
    {
        private const long Checkpoint = 4032;
        private const string BtcDest = "bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4";
        private Btc _btc = new(Checkpoint - 11);
        private readonly V _holder = NewKey();
        private readonly Network? _priorNetwork = Globals.BTCNetwork;
        private readonly long _priorGuard = Globals.BridgeIntraBlockGuardHeight;
        private readonly long _priorVree = Globals.VreeActivationHeight;

        private V Caster => Signers[0];
        private static long Tip => Globals.LastBlock.Height;
        private static AttestationMetaRow Meta => BtcAttestStore.GetMeta()!;
        private static int Depth => BtcAttestRules.Depth;
        private static NBitcoin.Script Destination => BitcoinAddress.Create(BtcDest, Network.Main).ScriptPubKey;

        public BtcAttest23_PaymentAttributionTests() : base(realMainnetRules: false)
        {
            _btc.MineEmpty(12);
            Globals.BitcoinCheckpointHeightMainnet = Checkpoint;
            Globals.BitcoinCheckpointHeadersMainnet = string.Concat(_btc.HeaderHexes(Checkpoint - 11, Checkpoint));
            Globals.BTCNetwork = Network.Main;
            Globals.BridgeIntraBlockGuardHeight = 1;
            Globals.VreeActivationHeight = 1; // the rule is on unless a test turns it off
            Globals.BootstrapCasterAddresses.Add(Caster.Address);
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _holder.Address, Balance = 100M, Nonce = 0 });
        }

        public override void Dispose()
        {
            Globals.BootstrapCasterAddresses.Remove(Caster.Address);
            Globals.BridgeIntraBlockGuardHeight = _priorGuard;
            Globals.VreeActivationHeight = _priorVree;
            Globals.BTCNetwork = _priorNetwork;
            SnapshotRestoreUtility.ClearStateDestroyed();
            base.Dispose();
        }

        // ── Bitcoin side ────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Mines Bitcoin transactions in one block, buries it and has the validators report it. Returns the block's height.</summary>
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

        /// <summary>Bitcoin reorganises: the blocks above <paramref name="sharedHeight"/> are replaced by a longer branch, and the validators sign the rewind.</summary>
        private async Task Reorganise(long sharedHeight)
        {
            var branch = _btc.ForkAfter(sharedHeight);
            branch.MineEmpty((int)(_btc.Tip - sharedHeight) + 2);
            var count = (int)(Meta.TipHeight - sharedHeight) + 1;
            var rewind = Signed(BtcBlockScanner.BuildRewind(sharedHeight, branch.HeaderHexes(sharedHeight + 1, sharedHeight + count), BtcAttestRules.EpochOf(Tip + 1)));
            var (ok, why) = await Verify(rewind, Tip + 1);
            Assert.True(ok, why);
            await Apply(Tip + 1, rewind);
            _btc = branch;
        }

        /// <summary>
        /// A transaction the vault's key signed for a payment on a chain this node has left: it spends the given coins of the
        /// vault, pays the destination the amount less <paramref name="feeSats"/>, and returns the rest to the vault.
        /// </summary>
        private static BtcTx PaymentOf(IEnumerable<string> outpoints, long inputSats, NBitcoin.Script vaultScript, long amountSats, long feeSats, NBitcoin.Script? to = null)
        {
            var spends = outpoints.Select(o => new OutPoint(uint256.Parse(o[..64]), uint.Parse(o[65..])));
            var change = inputSats - amountSats;
            return change > 0
                ? Pay(spends, (to ?? Destination, amountSats - feeSats), (vaultScript, change))
                : Pay(spends, (to ?? Destination, amountSats - feeSats));
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

        /// <summary>Mines and stores the block, as the node stores every block it applies (what a snapshot restore replays).</summary>
        private async Task<VfxTx> MineStored(VfxTx tx)
        {
            await Mine(tx);
            BlockchainData.GetBlocks().InsertSafe(AppliedBlocks.Last());
            return tx;
        }

        private VfxTx Transfer(string vault, decimal amount) =>
            SignedBy(Minter, _holder.Address, TransactionType.VBTC_V2_TRANSFER,
                new { Function = "TransferVBTCV2()", ContractUID = vault, FromAddress = Minter.Address, ToAddress = _holder.Address, Amount = amount });

        private VfxTx RequestTx(string vault, decimal amount) =>
            SignedBy(_holder, _holder.Address, TransactionType.VBTC_V2_WITHDRAWAL_REQUEST,
                new { Function = "VBTCWithdrawalRequest()", ContractUID = vault, BTCAddress = BtcDest, Amount = amount, FeeRate = 10, UniqueId = Guid.NewGuid().ToString("N") });

        private static VaultWithdrawalRow Row(VfxTx request, string vault) => BtcAttestStore.Withdrawal(BtcWithdrawals.RefOf(request.Hash, vault))!;
        private static VaultCoinRow Coin(string outpoint) => BtcAttestStore.Coin(outpoint)!;
        private static VBTCWithdrawalRequest Request(VfxTx tx, string vault) => VBTCWithdrawalRequest.GetByTransactionHash(tx.Hash, vault)!;

        private async Task<decimal> Spendable(string vault, V who) =>
            (await VBTCService.TryGetAvailableTransparentVbtcBalance(vault, who.Address)).availableBalance;

        /// <summary>A holder locks vBTC of a vault for the bridge; returns the lock's id.</summary>
        private async Task<string> Lock(string vault, decimal amount)
        {
            var lockId = Guid.NewGuid().ToString("N");
            await MineStored(SignedBy(Minter, Minter.Address, TransactionType.VBTC_V2_BRIDGE_LOCK,
                new { Function = "VBTCBridgeLock()", ContractUID = vault, LockId = lockId, Amount = amount, AmountSats = (long)(amount * 100_000_000M), EvmDestination = "0x" + new string('a', 40) }));
            return lockId;
        }

        private static string NewBurn() => "0x" + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

        private VfxTx Exit(string burn, string lockId, string vault, decimal amount) =>
            SignedBy(Caster, Caster.Address, TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC, new
            {
                Function = "VBTCBridgeExitToBTC()", ContractUID = vault, LockId = lockId,
                Amount = amount, AmountSats = (long)(amount * 100_000_000M), TotalAmount = amount, TotalAmountSats = (long)(amount * 100_000_000M),
                BtcDestination = BtcDest, BaseBurnTxHash = burn,
                Allocations = new List<PoolUnlockAllocation> { new() { LockId = lockId, SmartContractUID = vault, UnlockAmount = amount } },
            }, free: true);

        private static VaultWithdrawalRow Part(string burn, string vault) => BtcAttestStore.Withdrawal(BtcWithdrawals.ExitRefOf(VBTCBridgeBtcExitState.GetByBurnHash(burn)!, vault))!;

        /// <summary>A watched vault funded with one deposit, the holder holding 0.0005 on it, every block stored and the state marked good.</summary>
        private async Task<(string Uid, string Address, NBitcoin.Script Script)> StoredChainWithAFundedVault(long depositSats = 100_000)
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            await MineAndReport(Pay(vault.Script, depositSats));
            await Mine(Transfer(vault.Uid, 0.0005M));
            foreach (var block in AppliedBlocks) BlockchainData.GetBlocks().InsertSafe(block);
            StateTreiStatusService.SetSynced(Tip);
            return vault;
        }

        /// <summary>A snapshot of the state as it is now; the block it was taken at.</summary>
        private static async Task<long> Snapshot()
        {
            await StateSnapshotService.UpdateCycleAsync(Tip, Globals.LastBlock.Hash);
            Assert.Equal(Tip, StateSnapshotService.PickVerifiedSlotForRestore(Tip)?.Height);
            return Tip;
        }

        /// <summary>The chain forks below the blocks after the slot, deeper than the bounded reorg: the node restores the snapshot and is back at the slot.</summary>
        private static async Task Restore(long slotHeight)
        {
            Assert.True(await SnapshotRestoreUtility.TryRestoreAsync(slotHeight));
            Globals.LastBlock = BlockchainData.GetBlockByHeight(slotHeight)!;
        }

        // ── A payment the chain did not fix ────────────────────────────────────────────────────────────────

        /// <summary>
        /// The request is pinned here on one transaction; the one the vault's signers signed on a chain the node has left spends
        /// one of the same coins and pays the destination the amount less its fee. From the height it is the payment: the
        /// request completes, nothing is refunded, and the coins it spent are held by the request so a Bitcoin rewind of its
        /// block reopens it. Below the height it is a foreign spend: the request is void and refunded, as before.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task ATransactionThatPaysAPinnedWithdrawal_SettlesIt_FromTheHeight_AndVoidsItBelow(bool fromTheHeight)
        {
            Globals.VreeActivationHeight = fromTheHeight ? 1 : long.MaxValue;
            var vault = await StoredChainWithAFundedVault();
            var ownerBefore = await Spendable(vault.Uid, Minter);
            var request = await Mine(RequestTx(vault.Uid, 0.0003M));
            var fixedPin = Assert.Single(Row(request, vault.Uid).Pins);
            Assert.Equal(0.0002M, await Spendable(vault.Uid, _holder));
            Assert.Equal(ownerBefore, await Spendable(vault.Uid, Minter));

            var signedElsewhere = PaymentOf(fixedPin.Inputs, 100_000, vault.Script, 30_000, feeSats: 1_000);
            Assert.NotEqual(fixedPin.Txid, signedElsewhere.GetHash().ToString());
            var at = await MineAndReport(signedElsewhere);

            var row = Row(request, vault.Uid);
            if (!fromTheHeight)
            {
                Assert.Equal(VaultWithdrawalState.Void, row.State);
                Assert.Equal(0.0005M, await Spendable(vault.Uid, _holder));
                Assert.True(Request(request, vault.Uid).IsCompleted);
                Assert.Equal(VBTCWithdrawalStatus.Cancelled, Request(request, vault.Uid).Status);
                Assert.Null(BtcAttestStore.Payment(signedElsewhere.GetHash().ToString(), vault.Uid)); // nothing is kept below the height either
                return;
            }
            Assert.Equal(VaultWithdrawalState.Settled, row.State);
            Assert.Equal(signedElsewhere.GetHash().ToString(), row.SettledTxid);
            Assert.Equal(0.0002M, await Spendable(vault.Uid, _holder));
            Assert.Equal(VBTCWithdrawalStatus.Completed, Request(request, vault.Uid).Status);
            Assert.Equal(row.SettledTxid, Request(request, vault.Uid).BTCTxHash);
            var paid = Assert.Single(row.Pins, p => p.Txid == row.SettledTxid);
            Assert.Equal(29_000, paid.DestinationSats);
            Assert.Equal(70_000, paid.ChangeSats);
            Assert.Equal(1_000, paid.FeeSats);
            Assert.Equal(signedElsewhere.ToHex(), paid.TxHex);
            var spent = Coin(fixedPin.Inputs[0]);
            Assert.Equal(VaultCoinStatus.Spent, spent.Status);
            Assert.Equal(row.Ref, spent.PinnedToRef);
            Assert.Null(BtcAttestStore.Payment(row.SettledTxid!, vault.Uid)); // it paid something open: nothing to keep
            Assert.Equal(ownerBefore, await Spendable(vault.Uid, Minter)); // the Bitcoin that left is the holder's completed withdrawal: the owner carries nothing

            // Bitcoin drops the block: the request is to be paid again, by the same transaction, exactly as a fixed one would be.
            await Reorganise(at - 1);
            row = Row(request, vault.Uid);
            Assert.Equal(VaultWithdrawalState.Pinned, row.State);
            Assert.Null(row.SettledTxid);
            Assert.Equal(VBTCWithdrawalStatus.Requested, Request(request, vault.Uid).Status);
            Assert.Equal(0.0002M, await Spendable(vault.Uid, _holder));
            var back = Coin(fixedPin.Inputs[0]);
            Assert.Equal(VaultCoinStatus.Pinned, back.Status);
            Assert.Equal(row.Ref, back.PinnedToRef);
            Assert.True(BtcWithdrawals.IsPinnedTransaction(request.Hash, vault.Uid, signedElsewhere.GetHash().ToString()).Ok);

            // It confirms again on the new branch: completed again.
            await MineAndReport(signedElsewhere);
            Assert.Equal(VaultWithdrawalState.Settled, Row(request, vault.Uid).State);
            Assert.Equal(VBTCWithdrawalStatus.Completed, Request(request, vault.Uid).Status);
        }

        /// <summary>
        /// A rewind of this chain removed the request after its transaction was signed. The transaction confirms before the
        /// request is mined again: nothing is open for it, so it is kept. The request mined again is settled by it at once,
        /// with no second transaction fixed: the requester is debited once and paid once, and the owner carries nothing.
        /// </summary>
        [Fact]
        public async Task APaymentNothingWasOpenFor_IsKept_AndSettlesTheRequestMinedLater_WithoutFixingAnother()
        {
            var vault = await StoredChainWithAFundedVault();
            var ownerBefore = await Spendable(vault.Uid, Minter);
            var slot = await Snapshot();
            var request = await MineStored(RequestTx(vault.Uid, 0.0003M));
            var fixedPin = Assert.Single(Row(request, vault.Uid).Pins);
            var signed = BtcTx.Parse(fixedPin.TxHex, Network.Main);
            Assert.Equal(0.0002M, await Spendable(vault.Uid, _holder));

            // The node's chain forks below the request: the request, its row and its debit are gone; the signed transaction is not.
            await Restore(slot);
            Assert.Null(VBTCWithdrawalRequest.GetByTransactionHash(request.Hash, vault.Uid));
            Assert.Null(BtcAttestStore.Withdrawal(BtcWithdrawals.RefOf(request.Hash, vault.Uid)));
            Assert.Equal(0.0005M, await Spendable(vault.Uid, _holder));
            Assert.Equal(VaultCoinStatus.Unspent, Coin(fixedPin.Inputs[0]).Status);
            Assert.Null(Coin(fixedPin.Inputs[0]).PinnedToRef);

            // Bitcoin confirms it: the vault paid 0.0003 to the destination for nothing the chain has open. It is kept, not lost.
            await MineAndReport(signed);
            var txid = signed.GetHash().ToString();
            var kept = BtcAttestStore.Payment(txid, vault.Uid);
            Assert.NotNull(kept);
            Assert.Null(kept!.ClaimedByRef);
            Assert.Equal(100_000, kept.InputSats);
            Assert.Equal(2, kept.Outputs.Count);
            Assert.Equal(ownerBefore - 0.0003M, await Spendable(vault.Uid, Minter)); // the owner carries the 0.0003 that left until the request is back

            // The request is mined again (the validator that signed puts it back): paid already, by that transaction. Nothing new is fixed.
            var again = await MineStored(RequestTx(vault.Uid, 0.0003M));
            var row = Row(again, vault.Uid);
            Assert.Equal(VaultWithdrawalState.Settled, row.State);
            Assert.Equal(txid, row.SettledTxid);
            Assert.Equal(txid, Assert.Single(row.Pins).Txid);
            Assert.Equal(VBTCWithdrawalStatus.Completed, Request(again, vault.Uid).Status);
            Assert.Equal(txid, Request(again, vault.Uid).BTCTxHash);
            Assert.Equal(0.0002M, await Spendable(vault.Uid, _holder));
            Assert.Equal(ownerBefore, await Spendable(vault.Uid, Minter));
            Assert.Equal(row.Ref, BtcAttestStore.Payment(txid, vault.Uid)!.ClaimedByRef);
            Assert.Equal(row.Ref, Coin(Assert.Single(row.Pins).Inputs[0]).PinnedToRef);

            // A third request for the same amount is not paid by it a second time: it is fixed on the vault's coins as usual.
            var third = await MineStored(RequestTx(vault.Uid, 0.0002M));
            Assert.Equal(VaultWithdrawalState.Pinned, Row(third, vault.Uid).State);
            Assert.NotEqual(txid, Assert.Single(Row(third, vault.Uid).Pins).Txid);
        }

        /// <summary>The same for a bridge exit's part: kept while nothing is open, and settling the exit filed again, which completes and consumes the burn.</summary>
        [Fact]
        public async Task APaymentOfAnExitPart_IsKept_AndSettlesTheExitFiledAgain()
        {
            var vault = await StoredChainWithAFundedVault(1_000_000);
            var lockId = await Lock(vault.Uid, 0.004M);
            var slot = await Snapshot();
            var burn = NewBurn();
            await MineStored(Exit(burn, lockId, vault.Uid, 0.004M));
            var part = Part(burn, vault.Uid);
            Assert.Equal(VaultWithdrawalState.Pinned, part.State);
            var signed = BtcTx.Parse(Assert.Single(part.Pins).TxHex, Network.Main);

            await Restore(slot);
            Assert.Null(VBTCBridgeBtcExitState.GetByBurnHash(burn));
            await MineAndReport(signed);
            var txid = signed.GetHash().ToString();
            Assert.Null(BtcAttestStore.Payment(txid, vault.Uid)!.ClaimedByRef);
            Assert.False(VBTCBridgeConsumedBurn.IsConsumed(burn));

            // The caster files the exit again on the chain the node is on now: its part is paid already.
            await MineStored(Exit(burn, lockId, vault.Uid, 0.004M));
            part = Part(burn, vault.Uid);
            Assert.Equal(VaultWithdrawalState.Settled, part.State);
            Assert.Equal(txid, part.SettledTxid);
            Assert.Equal(txid, Assert.Single(part.Pins).Txid);
            Assert.True(VBTCBridgeBtcExitState.GetByBurnHash(burn)!.IsComplete);
            Assert.Equal(txid, VBTCBridgeBtcExitState.GetByBurnHash(burn)!.BtcTxHash);
            Assert.True(VBTCBridgeConsumedBurn.IsConsumed(burn));
            Assert.Equal(part.Ref, BtcAttestStore.Payment(txid, vault.Uid)!.ClaimedByRef);
        }

        /// <summary>
        /// Only a transaction that pays the destination what is owed is the payment. One to another script, or for another
        /// amount, is a foreign spend as before: the pinned withdrawal it robs is void and refunded, and it is kept as a
        /// payment that a later request for exactly what it paid may claim, while one for anything else is fixed as usual.
        /// </summary>
        [Fact]
        public async Task AnUnrelatedSpend_StillVoidsTheWithdrawalItRobs_AndSettlesNoRequestForAnotherAmount()
        {
            var vault = await StoredChainWithAFundedVault();
            var request = await Mine(RequestTx(vault.Uid, 0.0003M));
            var pin = Assert.Single(Row(request, vault.Uid).Pins);

            // To somebody else, for 20,000: not this request's payment.
            var elsewhere = Elsewhere();
            var foreign = PaymentOf(pin.Inputs, 100_000, vault.Script, 20_000, feeSats: 500, to: elsewhere);
            await MineAndReport(foreign);
            Assert.Equal(VaultWithdrawalState.Void, Row(request, vault.Uid).State);
            Assert.Equal(0.0005M, await Spendable(vault.Uid, _holder));
            var kept = BtcAttestStore.Payment(foreign.GetHash().ToString(), vault.Uid);
            Assert.NotNull(kept);

            // A request to the usual destination for 0.0002 is fixed on the vault's change, not paid by the kept spend to another script.
            var other = await Mine(RequestTx(vault.Uid, 0.0002M));
            Assert.Equal(VaultWithdrawalState.Pinned, Row(other, vault.Uid).State);
            Assert.Null(BtcAttestStore.Payment(foreign.GetHash().ToString(), vault.Uid)!.ClaimedByRef);

            // A payment to the destination for the wrong amount settles nothing either: the pinned request keeps its coins and the spend is kept.
            var coins = Assert.Single(Row(other, vault.Uid).Pins).Inputs;
            var wrongAmount = PaymentOf(coins, 80_000, vault.Script, 15_000, feeSats: 400);
            await MineAndReport(wrongAmount);
            Assert.Equal(VaultWithdrawalState.Void, Row(other, vault.Uid).State);
            Assert.Equal(0.0005M, await Spendable(vault.Uid, _holder));
            Assert.NotNull(BtcAttestStore.Payment(wrongAmount.GetHash().ToString(), vault.Uid));
        }
    }
}
