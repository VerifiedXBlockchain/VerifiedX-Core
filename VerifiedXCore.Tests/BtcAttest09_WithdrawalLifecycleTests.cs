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
    /// Bitcoin facts in consensus, part 9: a withdrawal from a vault the chain watches
    /// (docs/PLAN-vBTC-Bitcoin-Attestation.md, section 5). The requester sends one transaction. The Bitcoin transaction
    /// that will pay it is fixed in the same block from the vault's coins, Bitcoin showing that transaction completes
    /// the request, and a request that can never be paid is refunded, all by rule, at the same block on every node.
    /// Transactions are judged as block validation judges them and applied with UpdateTreis.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BtcAttest09_WithdrawalLifecycleTests : BtcAttestTestBase
    {
        private const long Checkpoint = 4032;
        private const string BtcDest = "bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4";
        private readonly Btc _btc = new(Checkpoint - 11);
        private readonly V _holder = NewKey();

        public BtcAttest09_WithdrawalLifecycleTests() : base(realMainnetRules: false)
        {
            _btc.MineEmpty(12);
            Globals.BitcoinCheckpointHeightMainnet = Checkpoint;
            Globals.BitcoinCheckpointHeadersMainnet = string.Concat(_btc.HeaderHexes(Checkpoint - 11, Checkpoint));
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _holder.Address, Balance = 100M, Nonce = 0 });
        }

        private static long Tip => Globals.LastBlock.Height;

        // ── Bitcoin side ────────────────────────────────────────────────────────────────────────────────────

        private async Task<BtcTx> Deposit(NBitcoin.Script vault, long sats)
        {
            var tx = Pay(vault, sats);
            await Report(_btc.Mine(tx));
            return tx;
        }

        /// <summary>Buries a Bitcoin block and has the validators report it.</summary>
        private async Task Report(long bitcoinHeight)
        {
            _btc.MineEmpty(BtcAttestRules.Depth);
            var meta = BtcAttestStore.GetMeta()!;
            var headers = _btc.HeaderHexes(meta.TipHeight + 1, bitcoinHeight + BtcAttestRules.Depth);
            var report = Assert.Single(BtcBlockScanner.BuildForward(_btc.Block(bitcoinHeight), bitcoinHeight, headers, 0));
            var tx = Signed(report);
            var (ok, why) = await Verify(tx, Tip + 1);
            Assert.True(ok, why);
            await Apply(Tip + 1, tx);
        }

        /// <summary>What the vault's signers do with a pin: sign exactly that transaction and broadcast it. Here it is mined.</summary>
        private async Task<BtcTx> SignAndConfirm(VaultPin pin)
        {
            var tx = BtcTx.Parse(pin.TxHex, Network.Main);
            Assert.Equal(pin.Txid, tx.GetHash().ToString());
            await Report(_btc.Mine(tx));
            return tx;
        }

        // ── VFX side ────────────────────────────────────────────────────────────────────────────────────────

        private static VfxTx SignedBy(V signer, string to, TransactionType type, object data)
        {
            var tx = new VfxTx
            {
                Timestamp = TimeUtil.GetTime(), FromAddress = signer.Address, ToAddress = to, Amount = 0.0M, Fee = 0, Nonce = 0,
                TransactionType = type, Data = JsonConvert.SerializeObject(data),
            };
            tx.Fee = VerifiedXCore.Services.FeeCalcService.CalculateTXFee(tx);
            tx.Build();
            tx.Signature = VerifiedXCore.Services.SignatureService.CreateSignature(tx.Hash, signer.Key, signer.Pub);
            return tx;
        }

        private VfxTx Transfer(string vault, decimal amount, V? from = null, V? to = null) =>
            SignedBy(from ?? Minter, (to ?? _holder).Address, TransactionType.VBTC_V2_TRANSFER,
                new { Function = "TransferVBTCV2()", ContractUID = vault, FromAddress = (from ?? Minter).Address, ToAddress = (to ?? _holder).Address, Amount = amount });

        private VfxTx Request(string vault, decimal amount, V? from = null, int feeRate = 10, string destination = BtcDest) =>
            SignedBy(from ?? Minter, (from ?? Minter).Address, TransactionType.VBTC_V2_WITHDRAWAL_REQUEST,
                new { Function = "VBTCWithdrawalRequest()", ContractUID = vault, BTCAddress = destination, Amount = amount, FeeRate = feeRate, UniqueId = Guid.NewGuid().ToString("N") });

        private VfxTx Cancel(string vault, string requestHash, V? from = null) =>
            SignedBy(from ?? Minter, (from ?? Minter).Address, TransactionType.VBTC_V2_WITHDRAWAL_CANCEL,
                new { Function = "VBTCWithdrawalCancel()", ContractUID = vault, WithdrawalRequestHash = requestHash, FailureProof = "" });

        /// <summary>Judged as a block's transaction, then mined in the next block.</summary>
        private async Task<VfxTx> Mine(VfxTx tx)
        {
            var (ok, why) = await Verify(tx, Tip + 1);
            Assert.True(ok, why);
            await Apply(Tip + 1, tx);
            return tx;
        }

        private async Task RefusedInBlock(VfxTx tx, string because)
        {
            var (ok, why) = await Verify(tx, Tip + 1);
            Assert.False(ok, "accepted in a block");
            Assert.Contains(because, why);
        }

        private static VaultWithdrawalRow Row(VfxTx request, string vault) => BtcAttestStore.Withdrawal(BtcWithdrawals.RefOf(request.Hash, vault))!;
        private static VBTCWithdrawalRequest RequestRow(VfxTx request, string vault) => VBTCWithdrawalRequest.GetByTransactionHash(request.Hash, vault)!;

        private async Task<decimal> Spendable(string vault, V who) =>
            (await VBTCService.TryGetAvailableTransparentVbtcBalance(vault, who.Address)).availableBalance;

        // ── The ordinary path ───────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task ARequest_IsPinnedInItsOwnBlock_AndBitcoinCompletesIt()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            var deposit = await Deposit(vault.Script, 150_000);

            var request = await Mine(Request(vault.Uid, 0.001M));
            var row = Row(request, vault.Uid);
            Assert.Equal(VaultWithdrawalState.Pinned, row.State);
            Assert.Equal(Tip, row.LastModifiedHeight);
            var pin = Assert.Single(row.Pins);
            Assert.Equal(new[] { Outpoint(deposit, 0) }, pin.Inputs);
            Assert.Equal(154 * 10, pin.FeeSats);                       // one input, two outputs, 10 sat/vB
            Assert.Equal(100_000 - 1_540, pin.DestinationSats);        // the fee comes out of the amount
            Assert.Equal(50_000, pin.ChangeSats);
            Assert.Equal(Tip, pin.PinnedAtVfxHeight);

            // The transaction itself, as the signers will build it from state.
            var btc = BtcTx.Parse(pin.TxHex, Network.Main);
            Assert.Equal(pin.Txid, btc.GetHash().ToString());
            Assert.Equal(2u, btc.Version);
            Assert.Equal(0u, btc.LockTime.Value);
            Assert.Equal(new OutPoint(deposit, 0), Assert.Single(btc.Inputs).PrevOut);
            Assert.Equal(BtcCanonicalWithdrawal.InputSequence, btc.Inputs[0].Sequence.Value); // replaceable: a fee bump can follow
            Assert.Equal(BitcoinAddress.Create(BtcDest, Network.Main).ScriptPubKey, btc.Outputs[0].ScriptPubKey);
            Assert.Equal(98_460, btc.Outputs[0].Value.Satoshi);
            Assert.Equal(vault.Script, btc.Outputs[1].ScriptPubKey);
            Assert.Equal(50_000, btc.Outputs[1].Value.Satoshi);

            // The coin is held for this withdrawal and still counts as the vault's; the amount is already debited.
            var coin = BtcAttestStore.Coin(Outpoint(deposit, 0))!;
            Assert.Equal(VaultCoinStatus.Pinned, coin.Status);
            Assert.Equal(row.Ref, coin.PinnedToRef);
            Assert.Equal(150_000, BtcAttestStore.Total(vault.Uid).PinnedSats);
            Assert.Equal(0.0015M, BtcAttestBalance.DepositBtc(vault.Uid));
            Assert.Equal(0.0005M, await Spendable(vault.Uid, Minter));
            Assert.Equal(VBTCWithdrawalStatus.Requested, RequestRow(request, vault.Uid).Status);

            // Nobody completes it by hand.
            await RefusedInBlock(SignedBy(Minter, Minter.Address, TransactionType.VBTC_V2_WITHDRAWAL_COMPLETE,
                new { Function = "VBTCWithdrawalComplete()", ContractUID = vault.Uid, WithdrawalRequestHash = request.Hash, BTCTransactionHash = pin.Txid }),
                "completed by the chain when Bitcoin shows its transaction");

            // The signers sign the pinned transaction; Bitcoin confirms it; the validators report it.
            await SignAndConfirm(pin);
            row = Row(request, vault.Uid);
            Assert.Equal(VaultWithdrawalState.Settled, row.State);
            Assert.Equal(pin.Txid, row.SettledTxid);
            var done = RequestRow(request, vault.Uid);
            Assert.Equal(VBTCWithdrawalStatus.Completed, done.Status);
            Assert.True(done.IsCompleted);
            Assert.Equal(pin.Txid, done.BTCTxHash);

            Assert.Equal(VaultCoinStatus.Spent, BtcAttestStore.Coin(Outpoint(deposit, 0))!.Status);
            Assert.Equal(VaultCoinStatus.Unspent, BtcAttestStore.Coin(BtcAttestStore.OutpointKey(pin.Txid, 1))!.Status);
            Assert.Equal(50_000, BtcAttestStore.Total(vault.Uid).UnspentSats);
            Assert.Equal(0, BtcAttestStore.Total(vault.Uid).PinnedSats);
            // The owner's figure is what it was the moment the request was mined: the Bitcoin left, the vBTC was burned once.
            Assert.Equal(0.0005M, await Spendable(vault.Uid, Minter));
            await Mine(Transfer(vault.Uid, 0.0005M));
            Assert.Equal(0M, await Spendable(vault.Uid, Minter));
        }

        [Fact]
        public async Task AHoldersWithdrawal_NeverMovesTheOwnersFigure()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            await Deposit(vault.Script, 150_000);
            await Mine(Transfer(vault.Uid, 0.001M)); // the owner gives 0.001 to a holder
            Assert.Equal(0.0005M, await Spendable(vault.Uid, Minter));
            Assert.Equal(0.001M, await Spendable(vault.Uid, _holder));

            var request = await Mine(Request(vault.Uid, 0.001M, from: _holder));
            Assert.Equal(VaultWithdrawalState.Pinned, Row(request, vault.Uid).State);
            Assert.Equal(0.0005M, await Spendable(vault.Uid, Minter));
            Assert.Equal(0M, await Spendable(vault.Uid, _holder));

            await SignAndConfirm(Row(request, vault.Uid).Pins[0]);
            Assert.Equal(VaultWithdrawalState.Settled, Row(request, vault.Uid).State);
            Assert.Equal(0.0005M, await Spendable(vault.Uid, Minter)); // the vault holds 0.0005 and all of it is the owner's
            Assert.Equal(0M, await Spendable(vault.Uid, _holder));
            Assert.Equal(0.0005M, BtcAttestBalance.DepositBtc(vault.Uid));
        }

        // ── Coins are never shared ──────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task TwoWithdrawals_NeverShareACoin_AndOneThatCannotBePaidYet_Waits()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            var large = await Deposit(vault.Script, 150_000);
            var small = await Deposit(vault.Script, 60_000);

            // The smallest coin that covers the amount, so the small withdrawal does not tie up the large coin.
            var a = await Mine(Request(vault.Uid, 0.0005M));
            Assert.Equal(new[] { Outpoint(small, 0) }, Row(a, vault.Uid).Pins[0].Inputs);
            var b = await Mine(Request(vault.Uid, 0.001M));
            Assert.Equal(new[] { Outpoint(large, 0) }, Row(b, vault.Uid).Pins[0].Inputs);

            // The owner still has 0.0006 to spend, but every coin is held: this one waits. Nothing is fixed for it.
            var c = await Mine(Request(vault.Uid, 0.0002M));
            var waiting = Row(c, vault.Uid);
            Assert.Equal(VaultWithdrawalState.Waiting, waiting.State);
            Assert.Empty(waiting.Pins);
            Assert.Contains("free coins do not cover", waiting.WaitingReason);

            // The first settles: 0.0001 comes back as change, not enough. The second settles: 0.0005 comes back, and
            // the waiting request is pinned in that same block.
            await SignAndConfirm(Row(a, vault.Uid).Pins[0]);
            Assert.Equal(VaultWithdrawalState.Waiting, Row(c, vault.Uid).State);
            var bPin = Row(b, vault.Uid).Pins[0];
            await SignAndConfirm(bPin);
            var pinned = Row(c, vault.Uid);
            Assert.Equal(VaultWithdrawalState.Pinned, pinned.State);
            Assert.Equal(new[] { BtcAttestStore.OutpointKey(bPin.Txid, 1) }, pinned.Pins[0].Inputs);
            Assert.Equal(Tip, pinned.Pins[0].PinnedAtVfxHeight);

            await SignAndConfirm(pinned.Pins[0]);
            Assert.Equal(VaultWithdrawalState.Settled, Row(c, vault.Uid).State);
            // 210,000 in; 50,000 + 100,000 + 20,000 withdrawn.
            Assert.Equal(40_000, BtcAttestStore.Total(vault.Uid).HeldSats);
            Assert.Equal(0.0004M, await Spendable(vault.Uid, Minter));
        }

        [Fact]
        public async Task ARequestTooLargeForOneCoin_TakesTheLargestCoinsFirst()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            var c1 = await Deposit(vault.Script, 40_000);
            var c2 = await Deposit(vault.Script, 70_000);
            var c3 = await Deposit(vault.Script, 30_000);

            var request = await Mine(Request(vault.Uid, 0.001M));
            var pin = Row(request, vault.Uid).Pins[0];
            Assert.Equal(new[] { Outpoint(c2, 0), Outpoint(c1, 0) }, pin.Inputs);  // 70,000 then 40,000: covered
            Assert.Equal(211 * 10, pin.FeeSats);                                    // two inputs: 57.5 x 2 + 96.5, rounded down
            Assert.Equal(100_000 - 2_110, pin.DestinationSats);
            Assert.Equal(10_000, pin.ChangeSats);
            Assert.Equal(VaultCoinStatus.Unspent, BtcAttestStore.Coin(Outpoint(c3, 0))!.Status);

            await SignAndConfirm(pin);
            Assert.Equal(40_000, BtcAttestStore.Total(vault.Uid).HeldSats);
        }

        [Fact]
        public async Task ChangeTooSmallToExist_GoesToTheFee()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            await Deposit(vault.Script, 100_300);

            var request = await Mine(Request(vault.Uid, 0.001M));
            var pin = Row(request, vault.Uid).Pins[0];
            Assert.Equal(0, pin.ChangeSats);
            Assert.Equal(1_540 + 300, pin.FeeSats);
            Assert.Single(BtcTx.Parse(pin.TxHex, Network.Main).Outputs);

            await SignAndConfirm(pin);
            Assert.Equal(0, BtcAttestStore.Total(vault.Uid).HeldSats);
            // 0.001003 came in, 0.001 was withdrawn: the 300 sats went to Bitcoin miners, and the owner's figure says so.
            Assert.Equal(0M, await Spendable(vault.Uid, Minter));
        }

        // ── Requests that are never paid ────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task APinnedCoinSpentByAnotherTransaction_VoidsTheRequest_AndRefundsIt()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            var deposit = await Deposit(vault.Script, 150_000);
            await Mine(Transfer(vault.Uid, 0.001M));
            var request = await Mine(Request(vault.Uid, 0.001M, from: _holder));
            Assert.Equal(VaultWithdrawalState.Pinned, Row(request, vault.Uid).State);
            Assert.Equal(0M, await Spendable(vault.Uid, _holder));

            // The coin leaves in a transaction that is not the pinned one (signers that signed something else).
            var other = Pay(new[] { new OutPoint(deposit, 0) }, (Elsewhere(), 20_000), (vault.Script, 129_000));
            await Report(_btc.Mine(other));

            var row = Row(request, vault.Uid);
            Assert.Equal(VaultWithdrawalState.Void, row.State);
            Assert.True(row.Pins[0].Dead);
            Assert.Contains("spent by another Bitcoin transaction", row.WaitingReason);
            var closed = RequestRow(request, vault.Uid);
            Assert.Equal(VBTCWithdrawalStatus.Cancelled, closed.Status);
            Assert.True(closed.IsCompleted);
            Assert.Equal(0.001M, await Spendable(vault.Uid, _holder)); // refunded
            // The vault holds 0.00129 against the holder's 0.001: the owner has 0.00029.
            Assert.Equal(0.00029M, await Spendable(vault.Uid, Minter));

            // The refunded holder asks again and is paid from the change.
            var again = await Mine(Request(vault.Uid, 0.001M, from: _holder));
            await SignAndConfirm(Row(again, vault.Uid).Pins[0]);
            Assert.Equal(VaultWithdrawalState.Settled, Row(again, vault.Uid).State);
            Assert.Equal(0.00029M, await Spendable(vault.Uid, Minter));
        }

        [Fact]
        public async Task ARequestNeverPinned_ExpiresAndIsRefunded_AfterItsWindow()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            await Deposit(vault.Script, 150_000);
            var first = await Mine(Request(vault.Uid, 0.001M));       // holds the only coin
            var second = await Mine(Request(vault.Uid, 0.0003M));     // waits
            var mined = Tip;
            Assert.Equal(VaultWithdrawalState.Waiting, Row(second, vault.Uid).State);
            Assert.Equal(0.0002M, await Spendable(vault.Uid, Minter));

            await Apply(mined + VBTCWithdrawalRequest.EXPIRY_BLOCKS);
            Assert.Equal(VaultWithdrawalState.Waiting, Row(second, vault.Uid).State); // the last block of its window
            await Apply(mined + VBTCWithdrawalRequest.EXPIRY_BLOCKS + 1);
            var expired = Row(second, vault.Uid);
            Assert.Equal(VaultWithdrawalState.Expired, expired.State);
            Assert.Equal(mined + VBTCWithdrawalRequest.EXPIRY_BLOCKS + 1, expired.ClosedAtVfxHeight);
            Assert.Equal(VBTCWithdrawalStatus.Cancelled, RequestRow(second, vault.Uid).Status);
            Assert.Equal(0.0005M, await Spendable(vault.Uid, Minter));

            // A pinned request does not expire: a signature for it may exist.
            Assert.Equal(VaultWithdrawalState.Pinned, Row(first, vault.Uid).State);
            await Apply(H1 + BtcAttestRules.EpochBlocks - 10); // well past its window, still in the first signer epoch
            Assert.True(Tip > mined + VBTCWithdrawalRequest.EXPIRY_BLOCKS + 100);
            Assert.Equal(VaultWithdrawalState.Pinned, Row(first, vault.Uid).State);
            await SignAndConfirm(Row(first, vault.Uid).Pins[0]);
            Assert.Equal(VaultWithdrawalState.Settled, Row(first, vault.Uid).State);
        }

        [Fact]
        public async Task Cancellation_IsAtOnce_ButOnlyBeforeAnythingIsPinned()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            await Deposit(vault.Script, 150_000);
            var pinned = await Mine(Request(vault.Uid, 0.001M));
            var waiting = await Mine(Request(vault.Uid, 0.0003M));

            await RefusedInBlock(Cancel(vault.Uid, pinned.Hash), "may already be signed");
            await RefusedInBlock(Cancel(vault.Uid, waiting.Hash, from: _holder), "Only the requester");
            await RefusedInBlock(Cancel(vault.Uid, new string('0', 64)), "No withdrawal request");
            await RefusedInBlock(SignedBy(Signers[0], Signers[0].Address, TransactionType.VBTC_V2_WITHDRAWAL_VOTE,
                new { Function = "VBTCWithdrawalCancelVote()", CancellationUID = "CANCEL_x", Approve = true, ContractUID = vault.Uid, WithdrawalRequestHash = pinned.Hash }),
                "not cancelled by vote");

            Assert.Equal(0.0002M, await Spendable(vault.Uid, Minter));
            await Mine(Cancel(vault.Uid, waiting.Hash));
            Assert.Equal(VaultWithdrawalState.Cancelled, Row(waiting, vault.Uid).State);
            Assert.Equal(VBTCWithdrawalStatus.Cancelled, RequestRow(waiting, vault.Uid).Status);
            Assert.Equal(0.0005M, await Spendable(vault.Uid, Minter));
            await RefusedInBlock(Cancel(vault.Uid, waiting.Hash), "already closed");

            // No vote record was made, and the pinned request is untouched.
            Assert.Null(VBTCWithdrawalCancellation.GetCancellationByWithdrawalHash(waiting.Hash, vault.Uid));
            Assert.Equal(VaultWithdrawalState.Pinned, Row(pinned, vault.Uid).State);
        }

        [Fact]
        public async Task ARequest_MustNameABitcoinAddress_AndLeaveSomethingToPay()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            await Deposit(vault.Script, 150_000);

            await RefusedInBlock(Request(vault.Uid, 0.001M, destination: "not-an-address"), "not an address on this network");
            await RefusedInBlock(Request(vault.Uid, 0.001M, destination: "tb1qw508d6qejxtdg4y5r3zarvary0c5xw7kxpjzsx"), "not an address on this network");

            // An amount that leaves 400 sats after the fee: the request's own rule allows it (330 sats is enough for a
            // Taproot payout), but the pinned transaction pays any kind of address and needs 546. It is accepted, never
            // pinned, and refunded at expiry. It holds no coin while it waits.
            var hopeless = await Mine(Request(vault.Uid, 0.0000194M, feeRate: 10));
            var row = Row(hopeless, vault.Uid);
            Assert.Equal(VaultWithdrawalState.Waiting, row.State);
            Assert.Contains("leaves less than 546 sats", row.WaitingReason);
            Assert.Equal(150_000, BtcAttestStore.Total(vault.Uid).UnspentSats);
        }

        // ── Several vaults in one request ───────────────────────────────────────────────────────────────────

        [Fact]
        public async Task ARequestOnTwoVaults_IsOnePinPerVault_EachCompletedByItsOwnTransaction()
        {
            await Start();
            var first = await CreateVault(H1 + 1);
            var second = await CreateVault(H1 + 2);
            await Deposit(first.Script, 150_000);
            await Deposit(second.Script, 80_000);

            var request = await Mine(SignedBy(Minter, Minter.Address, TransactionType.VBTC_V2_WITHDRAWAL_REQUEST, new
            {
                Function = VBTCService.MultiWithdrawalFunction, RequestorAddress = Minter.Address, BTCAddress = BtcDest, TotalAmount = 0.0015M, FeeRate = 10,
                UniqueId = Guid.NewGuid().ToString("N"),
                Inputs = new[] { new { SCUID = first.Uid, Amount = 0.001M }, new { SCUID = second.Uid, Amount = 0.0005M } },
            }));
            var a = Row(request, first.Uid);
            var b = Row(request, second.Uid);
            Assert.Equal(VaultWithdrawalState.Pinned, a.State);
            Assert.Equal(VaultWithdrawalState.Pinned, b.State);
            Assert.NotEqual(a.Pins[0].Txid, b.Pins[0].Txid);

            await SignAndConfirm(b.Pins[0]);
            Assert.Equal(VaultWithdrawalState.Settled, Row(request, second.Uid).State);
            Assert.Equal(VaultWithdrawalState.Pinned, Row(request, first.Uid).State);
            Assert.Equal(VBTCWithdrawalStatus.Completed, RequestRow(request, second.Uid).Status);
            Assert.Equal(VBTCWithdrawalStatus.Requested, RequestRow(request, first.Uid).Status);

            await SignAndConfirm(a.Pins[0]);
            Assert.Equal(VaultWithdrawalState.Settled, Row(request, first.Uid).State);
            Assert.Equal(0.0005M, await Spendable(first.Uid, Minter));
            Assert.Equal(0.0003M, await Spendable(second.Uid, Minter));
        }

        // ── An old vault ────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task AVaultCreatedBeforeTheRecordingHeight_IsNotPinned_AndKeepsItsCompletionTransaction()
        {
            var legacy = await CreateVault(H1 - 50);
            await Start();
            using var electrum = new FakeElectrum(("a", true, 1M));

            var request = Request(legacy.Uid, 0.001M);
            Assert.True((await TransactionValidatorService.VerifyTX(request)).Item1);
            await Apply(Tip + 1, request);
            Assert.Null(BtcAttestStore.Withdrawal(BtcWithdrawals.RefOf(request.Hash, legacy.Uid)));
            Assert.Equal(VBTCWithdrawalStatus.Requested, RequestRow(request, legacy.Uid).Status);
            Assert.Null(BtcWithdrawals.Refusal(SignedBy(Minter, Minter.Address, TransactionType.VBTC_V2_WITHDRAWAL_COMPLETE,
                new { Function = "VBTCWithdrawalComplete()", ContractUID = legacy.Uid, WithdrawalRequestHash = request.Hash, BTCTransactionHash = new string('a', 64) })));
        }

        /// <summary>
        /// A payment to the vault's own address cannot be told from the transaction's change. The vault's signers
        /// count both as the payment, find it larger than the withdrawal, and refuse to sign: the withdrawal would hold
        /// its coin and never be paid. It is refused when it is made instead.
        /// </summary>
        [Fact]
        public async Task AWithdrawalToTheVaultsOwnAddress_IsRefusedWhenItIsMade()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            var second = await CreateVault(H1 + 2);
            await Deposit(vault.Script, 150_000);
            await Mine(Transfer(vault.Uid, 0.001M));

            await RefusedInBlock(Request(vault.Uid, 0.0005M, from: _holder, destination: vault.Address), "is the deposit address of vault");
            var admitted = await VerifiedXCore.Services.TransactionValidatorService.VerifyTX(Request(vault.Uid, 0.0005M, from: _holder, destination: vault.Address));
            Assert.False(admitted.Item1);
            Assert.Contains("is the deposit address of vault", admitted.Item2);

            // Another vault's address is a destination like any other: the Bitcoin moves from one vault to the next.
            var moved = await Mine(Request(vault.Uid, 0.0005M, from: _holder, destination: second.Address));
            Assert.Equal(VaultWithdrawalState.Pinned, Row(moved, vault.Uid).State);
        }

        /// <summary>
        /// Burning a contract deletes its state record, and a vault keeps its whole ledger there. Its coins, withdrawals
        /// and deposit script are kept under its UID and are never deleted, so a vault burned and created again would
        /// have every coin and no debit. A watched vault is not burned, at any height, and nothing is created under
        /// its UID.
        /// </summary>
        [Fact]
        public async Task AWatchedVault_IsNeverBurned_AndNothingIsCreatedUnderItsUid()
        {
            var priorRule = Globals.VaultBurnRuleHeight;
            Globals.VaultBurnRuleHeight = long.MaxValue; // the rule for vaults the chain does not watch is not in force here
            try
            {
                await Start();
                var vault = await CreateVault(H1 + 1);
                await Deposit(vault.Script, 150_000);
                await Mine(Transfer(vault.Uid, 0.001M));

                await RefusedInBlock(SignedBy(Minter, Minter.Address, TransactionType.NFT_BURN, new[] { new { Function = "Burn()", ContractUID = vault.Uid } }), "vBTC V2 vault cannot be burned");
                await RefusedInBlock(SignedBy(Minter, Minter.Address, TransactionType.TKNZ_BURN, new[] { new { Function = "Burn()", ContractUID = vault.Uid } }), "vBTC V2 vault cannot be burned");

                var again = SignedBy(Minter, Minter.Address, TransactionType.VBTC_V2_CONTRACT_CREATE,
                    new[] { new { Function = "Mint()", ContractUID = vault.Uid, Data = VbtcTestContracts.VaultContractData(vault.Uid, Minter.Address, vault.Address), MD5List = "NA" } });
                await RefusedInBlock(again, "a vault's UID is never created twice");
                // Even if its record were gone, what the chain keeps under the UID is still there.
                SmartContractStateTrei.DeleteSmartContract(SmartContractStateTrei.GetSmartContractState(vault.Uid)!);
                await RefusedInBlock(again, "a vault's UID is never created twice");
            }
            finally { Globals.VaultBurnRuleHeight = priorRule; }
        }
    }
}
