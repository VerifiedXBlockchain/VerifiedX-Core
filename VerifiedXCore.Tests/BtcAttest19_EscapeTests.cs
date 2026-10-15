using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NBitcoin;
using Newtonsoft.Json;
using VerifiedXCore.Bitcoin.Attestation;
using VerifiedXCore.Bitcoin.FROST;
using VerifiedXCore.Bitcoin.FROST.Models;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;
using Xunit;
using BtcTx = NBitcoin.Transaction;
using VfxTx = VerifiedXCore.Models.Transaction;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Bitcoin facts in consensus, part 19: the ways out of a pinned withdrawal that does not confirm
    /// (docs/PLAN-vBTC-Bitcoin-Attestation.md, 5.5). Its requester can give it up (a release: the coins go back to the
    /// vault, the vBTC comes back less the Bitcoin fee); and after 2,160 blocks another withdrawal that pays more may be
    /// fixed on its coins (a takeover), so that one withdrawal nobody can or will pay does not hold a vault for good.
    /// Both end on Bitcoin, with transactions that exclude each other, and the chain follows whichever confirms.
    /// Transactions are judged as block validation judges them and applied with UpdateTreis.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BtcAttest19_EscapeTests : BtcAttestTestBase
    {
        private const long Checkpoint = 4032;
        private const string BtcDest = "bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4";
        private Btc _btc = new(Checkpoint - 11);
        private readonly V _holder = NewKey();
        private readonly V _other = NewKey();
        private readonly Network? _priorNetwork = Globals.BTCNetwork;
        private readonly Func<VaultWithdrawalRow, VaultPin, Task<(bool, string)>> _realSign = BtcWithdrawalSigner.SignAndBroadcast;
        private readonly Func<string, Task<bool?>> _realKnown = BtcWithdrawalSigner.KnownOnBitcoin;

        public BtcAttest19_EscapeTests() : base(realMainnetRules: false)
        {
            _btc.MineEmpty(12);
            Globals.BitcoinCheckpointHeightMainnet = Checkpoint;
            Globals.BitcoinCheckpointHeadersMainnet = string.Concat(_btc.HeaderHexes(Checkpoint - 11, Checkpoint));
            Globals.BTCNetwork = Network.Main;
            foreach (var who in new[] { _holder, _other })
                StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = who.Address, Balance = 100M, Nonce = 0 });
            BtcWithdrawalSigner.ForgetAttempts();
            FrostWithdrawalSigningTracker.ResetForTests();
        }

        public override void Dispose()
        {
            FrostWithdrawalSigningTracker.ResetForTests();
            BtcWithdrawalSigner.SignAndBroadcast = _realSign;
            BtcWithdrawalSigner.KnownOnBitcoin = _realKnown;
            BtcWithdrawalSigner.ForgetAttempts();
            Globals.BTCNetwork = _priorNetwork;
            base.Dispose();
        }

        private static long Tip => Globals.LastBlock.Height;
        private static AttestationMetaRow Meta => BtcAttestStore.GetMeta()!;
        private static int Stuck => BtcAttestRules.StuckPinBlocks;

        // ── Bitcoin side ────────────────────────────────────────────────────────────────────────────────────

        private async Task<long> MineAndReport(params BtcTx[] txs)
        {
            var height = _btc.Mine(txs);
            _btc.MineEmpty(BtcAttestRules.Depth);
            var headers = _btc.HeaderHexes(Meta.TipHeight + 1, height + BtcAttestRules.Depth);
            var report = Signed(Assert.Single(BtcBlockScanner.BuildForward(_btc.Block(height), height, headers, BtcAttestRules.EpochOf(Tip + 1))));
            var (ok, why) = await Verify(report, Tip + 1);
            Assert.True(ok, why);
            await Apply(Tip + 1, report);
            return height;
        }

        private async Task<BtcTx> Deposit(NBitcoin.Script vault, long sats)
        {
            var tx = Pay(vault, sats);
            await MineAndReport(tx);
            return tx;
        }

        /// <summary>Bitcoin confirms one of a withdrawal's transactions, and the validators report it. Returns the block's height.</summary>
        private Task<long> Confirm(VaultPin pin)
        {
            var tx = BtcTx.Parse(pin.TxHex, Network.Main);
            Assert.Equal(pin.Txid, tx.GetHash().ToString());
            return MineAndReport(tx);
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

        private VfxTx Transfer(string vault, decimal amount, V? to = null) =>
            SignedBy(Minter, (to ?? _holder).Address, TransactionType.VBTC_V2_TRANSFER,
                new { Function = "TransferVBTCV2()", ContractUID = vault, FromAddress = Minter.Address, ToAddress = (to ?? _holder).Address, Amount = amount });

        private VfxTx Request(string vault, decimal amount, V? from = null, int feeRate = 10, string address = BtcDest) =>
            SignedBy(from ?? Minter, (from ?? Minter).Address, TransactionType.VBTC_V2_WITHDRAWAL_REQUEST,
                new { Function = "VBTCWithdrawalRequest()", ContractUID = vault, BTCAddress = address, Amount = amount, FeeRate = feeRate, UniqueId = Guid.NewGuid().ToString("N") });

        private VfxTx Bump(string vault, string requestHash, long feeRate, V? from = null) =>
            SignedBy(from ?? Minter, (from ?? Minter).Address, TransactionType.VBTC_V2_COIN_PIN,
                new { Function = BtcWithdrawals.FeeBumpFunction, ContractUID = vault, WithdrawalRequestHash = requestHash, FeeRate = feeRate, Nonce = Guid.NewGuid().ToString("N") });

        private VfxTx Release(string vault, string requestHash, long feeRate, V? from = null) =>
            SignedBy(from ?? Minter, (from ?? Minter).Address, TransactionType.VBTC_V2_COIN_PIN,
                new { Function = BtcWithdrawals.ReleaseFunction, ContractUID = vault, WithdrawalRequestHash = requestHash, FeeRate = feeRate, Nonce = Guid.NewGuid().ToString("N") });

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
        private static VaultCoinRow Coin(BtcTx tx, long vout) => BtcAttestStore.Coin(Outpoint(tx, vout))!;

        private async Task<decimal> Spendable(string vault, V who) =>
            (await VBTCService.TryGetAvailableTransparentVbtcBalance(vault, who.Address)).availableBalance;

        /// <summary>What the vault holds on Bitcoin equals what everyone can spend plus what is in escrow for withdrawals still open.</summary>
        private async Task AssertBacked(string vault, params V[] holders)
        {
            decimal claims = await Spendable(vault, Minter);
            foreach (var holder in holders) claims += await Spendable(vault, holder);
            var open = BtcAttestStore.WithdrawalsOf(vault, VaultWithdrawalState.Waiting).Concat(BtcAttestStore.WithdrawalsOf(vault, VaultWithdrawalState.Pinned)).Sum(w => w.AmountSats);
            Assert.Equal(BtcAttestStore.Total(vault).HeldSats, (long)(claims * BtcAttestBalance.SatsPerBtc) + open);
        }

        private static FrostSigningStartRequest Announce(string vault, NBitcoin.Script vaultScript, string reference, BtcTx tx, string leader)
        {
            var prevouts = tx.Inputs.Select(i =>
            {
                var coin = BtcAttestStore.Coin(BtcAttestStore.OutpointKey(i.PrevOut.Hash.ToString(), i.PrevOut.N));
                return new PinnedWithdrawalCoin { TxId = i.PrevOut.Hash.ToString(), Vout = i.PrevOut.N, ValueSats = (ulong)(coin?.ValueSats ?? 77_777), ScriptPubKeyHex = vaultScript.ToHex() };
            }).ToList();
            var spent = prevouts.Select(p => new TxOut(Money.Satoshis((long)p.ValueSats), NBitcoin.Script.FromHex(p.ScriptPubKeyHex))).ToArray();
            var precomputed = tx.PrecomputeTransactionData(spent);
            var sighashes = Enumerable.Range(0, tx.Inputs.Count)
                .Select(i => Convert.ToHexString(tx.GetSignatureHashTaproot(precomputed, new TaprootExecutionData(i) { SigHash = TaprootSigHash.Default }).ToBytes()).ToLowerInvariant())
                .ToList();
            return new FrostSigningStartRequest
            {
                SessionId = Guid.NewGuid().ToString(), SmartContractUID = vault, WithdrawalRequestHash = reference, LeaderAddress = leader,
                MessageHash = sighashes[0], InputIndex = 0, InputCount = tx.Inputs.Count, AllInputSighashes = sighashes,
                UnsignedTxHex = tx.ToHex(), Prevouts = prevouts, Timestamp = TimeUtil.GetTime(),
            };
        }

        // ── Release ─────────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task ARequesterGivesAWithdrawalUp_ItsCoinsReturnToTheVault_AndItHasTheVbtcBackLessTheBitcoinFee()
        {
            await Start();
            var vault = await CreateVault(H1 + 1, keyHolders: Signers);
            var deposit = await Deposit(vault.Script, 150_000);
            await Mine(Transfer(vault.Uid, 0.001M));
            var request = await Mine(Request(vault.Uid, 0.001M, from: _holder));
            var payment = Assert.Single(Row(request, vault.Uid).Pins);
            Assert.False(payment.IsRelease);

            // Only its requester, and only at a rate that can replace the payment on Bitcoin.
            await RefusedInBlock(Release(vault.Uid, request.Hash, 25), "Only the requester");
            await RefusedInBlock(Release(vault.Uid, request.Hash, 10, from: _holder), "must be above the 10 sat/vB");

            await Mine(Release(vault.Uid, request.Hash, 25, from: _holder));
            var row = Row(request, vault.Uid);
            Assert.Equal(VaultWithdrawalState.Pinned, row.State);   // nothing is refunded until Bitcoin has decided
            Assert.Equal(2, row.Pins.Count);
            Assert.True(row.Pins[0].Superseded);
            var release = row.Pins[1];
            Assert.True(release.IsRelease);
            Assert.Equal(25, release.FeeRate);
            Assert.Equal(154 * 25, release.FeeSats);
            Assert.Equal(0, release.DestinationSats);
            Assert.Equal(150_000 - 154 * 25, release.ChangeSats);
            Assert.Equal(payment.Inputs, release.Inputs);
            var releaseTx = BtcTx.Parse(release.TxHex, Network.Main);
            Assert.Equal(release.Txid, releaseTx.GetHash().ToString());
            Assert.Equal(new OutPoint(deposit, 0), Assert.Single(releaseTx.Inputs).PrevOut);
            var back = Assert.Single(releaseTx.Outputs);
            Assert.Equal(vault.Script, back.ScriptPubKey);                 // every sat but the fee goes back to the vault
            Assert.Equal(150_000 - 154 * 25, back.Value.Satoshi);
            Assert.True(releaseTx.RBF);
            Assert.Equal(0M, await Spendable(vault.Uid, _holder));
            Assert.Equal(0.0005M, await Spendable(vault.Uid, Minter));
            await AssertBacked(vault.Uid, _holder);

            // The vault's signers sign the release (it is fixed in state), still sign the payment, and sign nothing else.
            Assert.True(BtcWithdrawals.IsPinnedRelease(request.Hash, vault.Uid, release.Txid));
            Assert.False(BtcWithdrawals.IsPinnedRelease(request.Hash, vault.Uid, payment.Txid));
            var (signs, why) = FrostSigningAuthorization.Authorize(Announce(vault.Uid, vault.Script, request.Hash, releaseTx, Signers[2].Address));
            Assert.True(signs, why);
            Assert.True(FrostSigningAuthorization.Authorize(Announce(vault.Uid, vault.Script, request.Hash, BtcTx.Parse(payment.TxHex, Network.Main), Signers[2].Address)).Item1);
            var skimmed = BtcTx.Parse(release.TxHex, Network.Main);
            skimmed.Outputs[0].Value -= Money.Satoshis(40_000);
            (signs, why) = FrostSigningAuthorization.Authorize(Announce(vault.Uid, vault.Script, request.Hash, skimmed, Signers[2].Address));
            Assert.False(signs);
            Assert.Contains("Only the Bitcoin transaction fixed in state", why);
            var led = new List<string>();
            BtcWithdrawalSigner.SignAndBroadcast = (_, p) => { led.Add(p.Txid); return Task.FromResult((true, p.Txid)); };
            BtcWithdrawalSigner.KnownOnBitcoin = _ => Task.FromResult<bool?>(false);
            await BtcWithdrawalSigner.TickAsync(Signers[1].Address, release.PinnedAtVfxHeight + 4 * BtcWithdrawalSigner.TurnBlocks + 100);
            Assert.Equal(new[] { release.Txid }, led);

            // Once it is being given up its payment is not raised any more; the release itself can be.
            await RefusedInBlock(Bump(vault.Uid, request.Hash, 40, from: _holder), "being released");

            // Bitcoin confirms the release.
            await Confirm(release);
            row = Row(request, vault.Uid);
            Assert.Equal(VaultWithdrawalState.Released, row.State);
            Assert.Equal(release.Txid, row.SettledTxid);
            var closed = VBTCWithdrawalRequest.GetByTransactionHash(request.Hash, vault.Uid)!;
            Assert.Equal(VBTCWithdrawalStatus.Cancelled, closed.Status);
            Assert.True(closed.IsCompleted);
            Assert.Equal(VaultCoinStatus.Spent, Coin(deposit, 0).Status);
            var returned = Coin(releaseTx, 0);
            Assert.Equal(VaultCoinStatus.Unspent, returned.Status);
            Assert.Equal(146_150, returned.ValueSats);
            var total = BtcAttestStore.Total(vault.Uid);
            Assert.Equal(146_150, total.UnspentSats);
            Assert.Equal(0, total.PinnedSats);
            Assert.Equal(3_850, total.ReleaseFeeSats);
            // The holder paid the one Bitcoin fee the vault spent; the owner is where it was.
            Assert.Equal(0.001M - 0.0000385M, await Spendable(vault.Uid, _holder));
            Assert.Equal(0.0005M, await Spendable(vault.Uid, Minter));
            await AssertBacked(vault.Uid, _holder);
            // No signer signs the payment any more, and it could not confirm if one had.
            Assert.False(BtcWithdrawals.IsPinnedTransaction(request.Hash, vault.Uid, payment.Txid).Ok);
            await RefusedInBlock(Release(vault.Uid, request.Hash, 40, from: _holder), "already closed (Released)");

            // And it can withdraw again, at once.
            var again = await Mine(Request(vault.Uid, 0.0009M, from: _holder, feeRate: 30));
            var second = Row(again, vault.Uid);
            Assert.Equal(VaultWithdrawalState.Pinned, second.State);
            Assert.Equal(new[] { Outpoint(releaseTx, 0) }, second.Pins[0].Inputs);
            await Confirm(second.Pins[0]);
            Assert.Equal(VaultWithdrawalState.Settled, Row(again, vault.Uid).State);
            Assert.Equal(0.001M - 0.0000385M - 0.0009M, await Spendable(vault.Uid, _holder));
            Assert.Equal(0.0005M, await Spendable(vault.Uid, Minter));
            await AssertBacked(vault.Uid, _holder);
        }

        [Fact]
        public async Task TheOwnerGivesItsOwnWithdrawalUp_AndPaysTheFeeOnce()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            await Deposit(vault.Script, 150_000);
            var request = await Mine(Request(vault.Uid, 0.001M));
            await Mine(Release(vault.Uid, request.Hash, 20));
            await Confirm(Row(request, vault.Uid).Pins[1]);
            Assert.Equal(VaultWithdrawalState.Released, Row(request, vault.Uid).State);
            Assert.Equal(150_000 - 154 * 20, BtcAttestStore.Total(vault.Uid).HeldSats);
            // The closed-unpaid list shows what was refunded: the amount less the release's Bitcoin fee (round 18).
            var listed = Newtonsoft.Json.Linq.JObject.FromObject(Assert.Single(VerifiedXCore.Bitcoin.Services.VBTCChainView.WithdrawalsClosedWithoutPayment(vault.Uid)));
            Assert.Equal(0.001M - 154 * 20 / 100_000_000M, (decimal)listed["Refunded"]!);
            Assert.Equal((150_000 - 154 * 20) / 100_000_000M, await Spendable(vault.Uid, Minter));
            await AssertBacked(vault.Uid);
        }

        [Fact]
        public async Task APaymentThatConfirmsAfterTheRelease_PaysTheWithdrawal_AndNothingIsRefunded()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            await Deposit(vault.Script, 150_000);
            await Mine(Transfer(vault.Uid, 0.001M));
            var request = await Mine(Request(vault.Uid, 0.001M, from: _holder));
            await Mine(Release(vault.Uid, request.Hash, 25, from: _holder));
            // A higher release is accepted on top of the first.
            await Mine(Release(vault.Uid, request.Hash, 40, from: _holder));
            var row = Row(request, vault.Uid);
            Assert.Equal(3, row.Pins.Count);
            Assert.True(row.Pins[2].IsRelease);
            Assert.Equal(154 * 40, row.Pins[2].FeeSats);

            // A miner had the payment all along.
            await Confirm(row.Pins[0]);
            row = Row(request, vault.Uid);
            Assert.Equal(VaultWithdrawalState.Settled, row.State);
            Assert.Equal(row.Pins[0].Txid, row.SettledTxid);
            Assert.Equal(VBTCWithdrawalStatus.Completed, VBTCWithdrawalRequest.GetByTransactionHash(request.Hash, vault.Uid)!.Status);
            Assert.Equal(0, BtcAttestStore.Total(vault.Uid).ReleaseFeeSats);
            Assert.Equal(0M, await Spendable(vault.Uid, _holder));
            Assert.Equal(0.0005M, await Spendable(vault.Uid, Minter));
            Assert.Equal(50_000, BtcAttestStore.Total(vault.Uid).HeldSats);
            await AssertBacked(vault.Uid, _holder);
        }

        [Fact]
        public async Task AReleaseIsRefused_WhenItCannotEndTheWithdrawalSafely()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            await Deposit(vault.Script, 150_000);
            var small = await Mine(Request(vault.Uid, 0.00003M));         // 3,000 sats at 10 sat/vB: pays 1,460
            // Its fee comes out of the refund, so it cannot be more than the withdrawal is for.
            await RefusedInBlock(Release(vault.Uid, small.Hash, 20), "more than the 3000 sats the withdrawal is for");
            await Mine(Release(vault.Uid, small.Hash, 15));               // 2,310 sats: allowed
            Assert.True(Row(small, vault.Uid).Pins[1].IsRelease);

            // A withdrawal that has no Bitcoin transaction is cancelled, not released.
            var waiting = await Mine(Request(vault.Uid, 0.001M));
            Assert.Equal(VaultWithdrawalState.Waiting, Row(waiting, vault.Uid).State);
            await RefusedInBlock(Release(vault.Uid, waiting.Hash, 20), "no Bitcoin transaction yet");
            await RefusedInBlock(Release(vault.Uid, "nosuchrequest", 20), "No such withdrawal request");
            await RefusedInBlock(SignedBy(Minter, Minter.Address, TransactionType.VBTC_V2_COIN_PIN,
                new { Function = "VBTCWithdrawalSeize()", ContractUID = vault.Uid, WithdrawalRequestHash = small.Hash, FeeRate = 50 }), "function is VBTCWithdrawalFeeBump() or VBTCWithdrawalRelease()");

            // One per withdrawal per block, bump or release.
            var state = new BtcAttestBlockRules.State();
            Assert.True(BtcAttestBlockRules.TryRegister(Release(vault.Uid, small.Hash, 30), state).Ok);
            Assert.False(BtcAttestBlockRules.TryRegister(Release(vault.Uid, small.Hash, 31), state).Ok);
        }

        // ── What the signers check a pinned transaction against ─────────────────────────────────────────────

        [Fact]
        public async Task TheSigners_CheckOutputsAgainstTheScriptInState_AndSignAChangeMadeUpToTheSmallestOutput()
        {
            await Start();
            var vault = await CreateVault(H1 + 1, keyHolders: Signers);
            await Deposit(vault.Script, 100_000);
            await Mine(Transfer(vault.Uid, 0.000997M));
            // 300 sats more than the amount in the coin: the change is made up to 546 out of the payout.
            var request = await Mine(Request(vault.Uid, 0.000997M, from: _holder));
            var pin = Assert.Single(Row(request, vault.Uid).Pins);
            Assert.Equal(546, pin.ChangeSats);
            var (signs, why) = FrostSigningAuthorization.Authorize(Announce(vault.Uid, vault.Script, request.Hash, BtcTx.Parse(pin.TxHex, Network.Main), Signers[1].Address));
            Assert.True(signs, why);
            Assert.Equal(BitcoinAddress.Create(BtcDest, Network.Main).ScriptPubKey.ToHex(), BtcWithdrawals.DestinationScriptOf(request.Hash, vault.Uid));
            Assert.Null(BtcWithdrawals.DestinationScriptOf("nosuchrequest", vault.Uid));
        }

        // ── Takeover ────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>A vault with one coin of 150,000 sats, held by the holder's withdrawal of 10,000 sats at 1 sat/vB.</summary>
        private async Task<((string Uid, string Address, NBitcoin.Script Script) Vault, BtcTx Deposit, VfxTx Slow)> VaultHeldByASlowWithdrawal()
        {
            await Start();
            var vault = await CreateVault(H1 + 1, keyHolders: Signers);
            var deposit = await Deposit(vault.Script, 150_000);
            await Mine(Transfer(vault.Uid, 0.0001M));
            var slow = await Mine(Request(vault.Uid, 0.0001M, from: _holder, feeRate: 1));
            Assert.Equal(VaultWithdrawalState.Pinned, Row(slow, vault.Uid).State);
            return (vault, deposit, slow);
        }

        [Fact]
        public async Task AWithdrawalThatStoppedMoving_NoLongerKeepsItsCoins_TheOneThatConfirmsIsPaidAndTheOtherRefunded()
        {
            var (vault, deposit, slow) = await VaultHeldByASlowWithdrawal();
            var slowPin = Row(slow, vault.Uid).Pins[0];

            // While it is young it keeps the vault's only coin to itself: the owner's withdrawal waits.
            var early = await Mine(Request(vault.Uid, 0.001M));
            Assert.Equal(VaultWithdrawalState.Waiting, Row(early, vault.Uid).State);
            await Mine(SignedBy(Minter, Minter.Address, TransactionType.VBTC_V2_WITHDRAWAL_CANCEL,
                new { Function = "VBTCWithdrawalCancel()", ContractUID = vault.Uid, WithdrawalRequestHash = early.Hash, FailureProof = "" }));

            // 2,160 blocks after it was fixed, a withdrawal that pays more is fixed on the same coin.
            await AdvanceTo(slowPin.PinnedAtVfxHeight + Stuck - 1);
            var cheap = await Mine(Request(vault.Uid, 0.0002M, feeRate: 1));       // no better than the one it would replace: waits
            Assert.Equal(Tip, slowPin.PinnedAtVfxHeight + Stuck);
            Assert.Equal(VaultWithdrawalState.Waiting, Row(cheap, vault.Uid).State);
            var request = await Mine(Request(vault.Uid, 0.001M, feeRate: 10));
            var row = Row(request, vault.Uid);
            Assert.Equal(VaultWithdrawalState.Pinned, row.State);
            var taking = Assert.Single(row.Pins);
            Assert.Equal(new[] { Outpoint(deposit, 0) }, taking.Inputs);
            Assert.Equal(100_000 - 1_540, taking.DestinationSats);
            Assert.Equal(50_000, taking.ChangeSats);

            // Both hold the coin; it counts once.
            var coin = Coin(deposit, 0);
            Assert.Equal(VaultCoinStatus.Pinned, coin.Status);
            Assert.Equal(Row(slow, vault.Uid).Ref, coin.PinnedToRef);
            Assert.Equal(new[] { row.Ref }, coin.AlsoPinnedTo);
            Assert.Equal(150_000, BtcAttestStore.Total(vault.Uid).PinnedSats);
            Assert.Equal(VaultWithdrawalState.Pinned, Row(slow, vault.Uid).State);
            await AssertBacked(vault.Uid, _holder);

            // The signers sign both transactions: consensus fixed both, and Bitcoin can confirm only one.
            var takingTx = BtcTx.Parse(taking.TxHex, Network.Main);
            var slowTx = BtcTx.Parse(slowPin.TxHex, Network.Main);
            Assert.True(FrostSigningAuthorization.Authorize(Announce(vault.Uid, vault.Script, slow.Hash, slowTx, Signers[1].Address)).Item1);
            var (signs, why) = FrostSigningAuthorization.Authorize(Announce(vault.Uid, vault.Script, request.Hash, takingTx, Signers[1].Address));
            Assert.True(signs, why);
            var slowScope = FrostSigningAuthorization.SigningScope(vault.Uid, slowPin.Txid);
            var takingScope = FrostSigningAuthorization.SigningScope(vault.Uid, taking.Txid);
            Assert.False(FrostWithdrawalSigningTracker.TryBeginSigning(vault.Uid, slow.Hash, "s-slow", 0, "aa11", new List<string> { "aa11" }, slowPin.Inputs, null, slowScope).Blocked);
            FrostWithdrawalSigningTracker.RecordSigningCompleted(vault.Uid, slow.Hash, "s-slow", 0, "aa11", slowPin.Inputs, slowPin.Txid, slowScope);
            var (blocked, reason) = FrostWithdrawalSigningTracker.TryBeginSigning(vault.Uid, request.Hash, "s-taking", 0, "bb22", new List<string> { "bb22" }, taking.Inputs, null, takingScope);
            Assert.False(blocked, reason);   // this validator's own coin rule gives way to what consensus fixed

            // Bitcoin confirms the one that pays more.
            await Confirm(taking);
            Assert.Equal(VaultWithdrawalState.Settled, Row(request, vault.Uid).State);
            var voided = Row(slow, vault.Uid);
            Assert.Equal(VaultWithdrawalState.Void, voided.State);
            Assert.True(voided.Pins[0].Dead);
            Assert.Equal(VBTCWithdrawalStatus.Cancelled, VBTCWithdrawalRequest.GetByTransactionHash(slow.Hash, vault.Uid)!.Status);
            Assert.Equal(0.0001M, await Spendable(vault.Uid, _holder));           // refunded in full: it paid no Bitcoin fee
            // The change is back, and the withdrawal that was waiting behind them is paid from it.
            var waited = Row(cheap, vault.Uid);
            Assert.Equal(VaultWithdrawalState.Pinned, waited.State);
            Assert.Equal(new[] { Outpoint(takingTx, 1) }, waited.Pins[0].Inputs);
            Assert.Equal(50_000, BtcAttestStore.Total(vault.Uid).HeldSats);
            await AssertBacked(vault.Uid, _holder);
            Assert.False(BtcWithdrawals.IsPinnedTransaction(slow.Hash, vault.Uid, slowPin.Txid).Ok);
        }

        [Fact]
        public async Task TheSlowWithdrawalConfirmingAfterAll_IsPaid_AndTheOneThatTookItsCoinIsRefunded()
        {
            var (vault, _, slow) = await VaultHeldByASlowWithdrawal();
            var slowPin = Row(slow, vault.Uid).Pins[0];
            await AdvanceTo(slowPin.PinnedAtVfxHeight + Stuck);
            var request = await Mine(Request(vault.Uid, 0.001M, feeRate: 10));
            Assert.Equal(VaultWithdrawalState.Pinned, Row(request, vault.Uid).State);
            Assert.Equal(0.0004M, await Spendable(vault.Uid, Minter));

            await Confirm(slowPin);
            Assert.Equal(VaultWithdrawalState.Settled, Row(slow, vault.Uid).State);
            var taker = Row(request, vault.Uid);
            Assert.Equal(VaultWithdrawalState.Void, taker.State);
            Assert.Equal(0M, await Spendable(vault.Uid, _holder));
            Assert.Equal(0.0014M, await Spendable(vault.Uid, Minter));            // 140,000 sats of change, all the owner's again
            Assert.Equal(140_000, BtcAttestStore.Total(vault.Uid).UnspentSats);
            Assert.Equal(0, BtcAttestStore.Total(vault.Uid).PinnedSats);
            await AssertBacked(vault.Uid, _holder);
        }

        [Fact]
        public async Task AWithdrawalStillMoving_KeepsItsCoins_AndAWaitingOneTakesThemWhenItStops()
        {
            var (vault, deposit, slow) = await VaultHeldByASlowWithdrawal();
            var first = Row(slow, vault.Uid).Pins[0];

            // Its requester raises the fee just before the 2,160 blocks are up: it is moving again, and counted from there.
            await AdvanceTo(first.PinnedAtVfxHeight + Stuck - 10);
            await Mine(Bump(vault.Uid, slow.Hash, 2, from: _holder));
            var bumped = Row(slow, vault.Uid).Pins[1];
            await AdvanceTo(first.PinnedAtVfxHeight + Stuck + 5);
            var tooSoon = await Mine(Request(vault.Uid, 0.001M, feeRate: 10));
            Assert.Equal(VaultWithdrawalState.Waiting, Row(tooSoon, vault.Uid).State);
            Assert.Null(Coin(deposit, 0).AlsoPinnedTo);

            // It waits, past its own 360 blocks, and is fixed by itself when the bumped one stops moving: nobody has
            // to send anything at the right moment.
            var waiting = tooSoon;
            var due = bumped.PinnedAtVfxHeight + Stuck;
            var retried = due % BtcAttestRules.TakeRetryBlocks == 0 ? due : due + BtcAttestRules.TakeRetryBlocks - due % BtcAttestRules.TakeRetryBlocks;
            await AdvanceTo(retried - 1);
            Assert.Equal(VaultWithdrawalState.Waiting, Row(waiting, vault.Uid).State);
            await AdvanceTo(retried);
            var row = Row(waiting, vault.Uid);
            Assert.Equal(VaultWithdrawalState.Pinned, row.State);
            Assert.Equal(retried, row.Pins[0].PinnedAtVfxHeight);
            Assert.Equal(new[] { row.Ref }, Coin(deposit, 0).AlsoPinnedTo);
            // It must be able to replace what it conflicts with on Bitcoin: more fee in all than the newest transaction it takes from.
            Assert.True(row.Pins[0].FeeSats >= bumped.FeeSats + 154);
            await AssertBacked(vault.Uid, _holder);
        }

        /// <summary>
        /// Small raises do not buy more time. Every fee bump used to start the 2,160 blocks again, so a requester adding
        /// 1 sat/vB just before they ran out kept a vault's coin eight times as long (second path audit, 14 October
        /// 2026). Only a raise that at least doubles the rate counts as moving again.
        /// </summary>
        [Fact]
        public async Task ASmallFeeRaise_DoesNotStartTheCountAgain_ADoublingDoes()
        {
            var (vault, deposit, slow) = await VaultHeldByASlowWithdrawal();
            var first = Row(slow, vault.Uid).Pins[0];
            // The slow one pays 1 sat/vB. Raised to 10, then by one at a time: 10 counts (it more than doubled), 11 and 12 do not.
            await AdvanceTo(first.PinnedAtVfxHeight + 100);
            await Mine(Bump(vault.Uid, slow.Hash, 10, from: _holder));
            var doubled = Row(slow, vault.Uid).Pins[1];
            Assert.Equal(doubled.PinnedAtVfxHeight, BtcWithdrawals.MovingSince(Row(slow, vault.Uid)));
            await AdvanceTo(doubled.PinnedAtVfxHeight + Stuck - 20);
            await Mine(Bump(vault.Uid, slow.Hash, 11, from: _holder));
            await Mine(Bump(vault.Uid, slow.Hash, 12, from: _holder));
            Assert.Equal(doubled.PinnedAtVfxHeight, BtcWithdrawals.MovingSince(Row(slow, vault.Uid)));

            // So a withdrawal that pays more takes the coin 2,160 blocks after the raise that counted.
            await AdvanceTo(doubled.PinnedAtVfxHeight + Stuck);
            var taker = await Mine(Request(vault.Uid, 0.001M, feeRate: 30));
            Assert.Equal(VaultWithdrawalState.Pinned, Row(taker, vault.Uid).State);
            Assert.Equal(new[] { Row(taker, vault.Uid).Ref }, Coin(deposit, 0).AlsoPinnedTo);

            // A release does not start it again either.
            Assert.Equal(doubled.PinnedAtVfxHeight, BtcWithdrawals.MovingSince(Row(slow, vault.Uid)));
            await AssertBacked(vault.Uid, _holder);
        }

        /// <summary>
        /// The give-up path stays open. Releases used to share the limit of eight transactions with fee raises: after
        /// seven raises a withdrawal could be neither raised nor released, and a release at too low a fee could not be
        /// followed by a better one (second path audit, 14 October 2026).
        /// </summary>
        [Fact]
        public async Task AfterEveryFeeRaiseIsUsed_TheWithdrawalCanStillBeReleased_AndTheReleaseRaised()
        {
            await Start();
            var vault = await CreateVault(H1 + 1, keyHolders: Signers);
            await Deposit(vault.Script, 150_000);
            await Mine(Transfer(vault.Uid, 0.001M));
            var request = await Mine(Request(vault.Uid, 0.001M, from: _holder));
            for (var rate = 11; rate <= 17; rate++)
                await Mine(Bump(vault.Uid, request.Hash, rate, from: _holder));
            Assert.Equal(BtcWithdrawals.MaxPins, Row(request, vault.Uid).Pins.Count);
            await RefusedInBlock(Bump(vault.Uid, request.Hash, 18, from: _holder), "It can still be released");

            await Mine(Release(vault.Uid, request.Hash, 20, from: _holder));
            await Mine(Release(vault.Uid, request.Hash, 30, from: _holder));
            var row = Row(request, vault.Uid);
            Assert.Equal(BtcWithdrawals.MaxPins + 2, row.Pins.Count);
            Assert.True(row.Pins[^1].IsRelease);
            // Releases have a limit of their own.
            for (var rate = 40; row.Pins.Count(p => p.IsRelease) < BtcWithdrawals.MaxReleasePins; rate += 10)
            {
                await Mine(Release(vault.Uid, request.Hash, rate, from: _holder));
                row = Row(request, vault.Uid);
            }
            await RefusedInBlock(Release(vault.Uid, request.Hash, 500, from: _holder), $"already has {BtcWithdrawals.MaxReleasePins} release transactions");
        }

        // ── A vault that changes hands (second path audit, 14 October 2026) ─────────────────────────────────

        private VfxTx TransferVault(string vault, V to) =>
            SignedBy(Minter, to.Address, TransactionType.NFT_TX, new[]
            {
                new { Function = "Transfer()", ContractUID = vault, ToAddress = to.Address, Data = SmartContractStateTrei.GetSmartContractState(vault)!.ContractData, MD5List = "NA" },
            });

        /// <summary>
        /// The owner's own withdrawal is open when the vault goes to a new owner, and then does not go through. Its
        /// refund used to shrink a debt left on the old owner's address, so it went to the new owner's balance and the
        /// old owner had nothing: a failed withdrawal, and the vBTC not back.
        /// </summary>
        [Fact]
        public async Task AnOwnerWhoHandsTheVaultOn_StillGetsItsOwnWithdrawalBack()
        {
            await Start();
            var vault = await CreateVault(H1 + 1, keyHolders: Signers);
            await Deposit(vault.Script, 150_000);
            var request = await Mine(Request(vault.Uid, 0.001M));
            Assert.Equal(VaultWithdrawalState.Pinned, Row(request, vault.Uid).State);

            await Mine(TransferVault(vault.Uid, _holder));
            Assert.Equal(_holder.Address, SmartContractStateTrei.GetSmartContractState(vault.Uid)!.OwnerAddress);
            // What the old owner had sent out as owner went with the vault: it holds nothing and owes nothing.
            Assert.Equal(0M, await Spendable(vault.Uid, Minter));
            Assert.Equal(0.0005M, await Spendable(vault.Uid, _holder));
            await AssertBacked(vault.Uid, _holder);

            // It gives its withdrawal up, and Bitcoin confirms the release.
            await Mine(Release(vault.Uid, request.Hash, 20));
            await Confirm(Row(request, vault.Uid).Pins[1]);
            Assert.Equal(VaultWithdrawalState.Released, Row(request, vault.Uid).State);
            // The refund (less the Bitcoin fee) is the requester's, to spend as any holder; the new owner has what it had.
            Assert.Equal(0.001M - 154 * 20 / 100_000_000M, await Spendable(vault.Uid, Minter));
            Assert.Equal(0.0005M, await Spendable(vault.Uid, _holder));
            await AssertBacked(vault.Uid, _holder);
            var (ok, why) = await Verify(Transfer(vault.Uid, 0.0009M, to: _other), Tip + 1);
            Assert.True(ok, why);
        }

        /// <summary>While a vault is marked for a new owner, its owner moves no vBTC out of it; holders are not touched.</summary>
        [Fact]
        public async Task AVaultBeingSoldOrTransferred_KeepsItsOwnersBalance()
        {
            await Start();
            var vault = await CreateVault(H1 + 1, keyHolders: Signers);
            await Deposit(vault.Script, 150_000);
            await Mine(Transfer(vault.Uid, 0.0002M));
            // A sale has started (or a reserve account's delayed transfer): state marks the vault for its next owner.
            var contract = SmartContractStateTrei.GetSmartContractState(vault.Uid)!;
            contract.NextOwner = _other.Address;
            contract.IsLocked = true;
            SmartContractStateTrei.UpdateSmartContract(contract);

            await RefusedInBlock(Transfer(vault.Uid, 0.0005M, to: _other), "is being transferred or sold");
            await RefusedInBlock(Request(vault.Uid, 0.0005M), "is being transferred or sold");
            // A holder's vBTC is its own.
            await Mine(Request(vault.Uid, 0.0002M, from: _holder));

            contract = SmartContractStateTrei.GetSmartContractState(vault.Uid)!;
            contract.NextOwner = null;
            contract.IsLocked = false;
            SmartContractStateTrei.UpdateSmartContract(contract);
            await Mine(Transfer(vault.Uid, 0.0005M, to: _other));
        }

        /// <summary>vBTC goes only to an address somebody can sign for: sent anywhere else it is lost.</summary>
        [Fact]
        public async Task Vbtc_IsTransferredOnlyToAnAddressThatCanHoldIt()
        {
            await Start();
            var vault = await CreateVault(H1 + 1, keyHolders: Signers);
            await Deposit(vault.Script, 150_000);
            foreach (var nowhere in new[] { "Reserve_Base", "Token_Base", "TW_Base", "zfx_" + new string('q', 60), "some.adnr.name.that.is.long.enough.vfx" })
            {
                var tx = SignedBy(Minter, nowhere, TransactionType.VBTC_V2_TRANSFER,
                    new { Function = "TransferVBTCV2()", ContractUID = vault.Uid, FromAddress = Minter.Address, ToAddress = nowhere, Amount = 0.0001M });
                var (ok, why) = await Verify(tx, Tip + 1);
                Assert.False(ok, $"a transfer to {nowhere} was accepted");
            }
            await Mine(Transfer(vault.Uid, 0.0001M, to: _other));
        }

        [Fact]
        public async Task TwoWithdrawalsThatAreTheSameBitcoinTransaction_ArePaidOnce()
        {
            var (vault, _, slow) = await VaultHeldByASlowWithdrawal();
            var slowPin = Row(slow, vault.Uid).Pins[0];
            await Mine(Transfer(vault.Uid, 0.0001M, to: _other));
            await AdvanceTo(slowPin.PinnedAtVfxHeight + Stuck);
            // Same amount, same address, on the same coin; then the first raises its fee to the same rate.
            var twin = await Mine(Request(vault.Uid, 0.0001M, from: _other, feeRate: 5));
            Assert.Equal(VaultWithdrawalState.Pinned, Row(twin, vault.Uid).State);
            await Mine(Bump(vault.Uid, slow.Hash, 5, from: _holder));
            var bumped = Row(slow, vault.Uid).Pins[1];
            Assert.Equal(Row(twin, vault.Uid).Pins[0].Txid, bumped.Txid);

            // One Bitcoin transaction paid once: one withdrawal is completed by it, the other is refunded.
            await Confirm(bumped);
            Assert.Equal(VaultWithdrawalState.Settled, Row(slow, vault.Uid).State);
            Assert.Equal(VaultWithdrawalState.Void, Row(twin, vault.Uid).State);
            Assert.Equal(0M, await Spendable(vault.Uid, _holder));
            Assert.Equal(0.0001M, await Spendable(vault.Uid, _other));
            Assert.Equal(140_000, BtcAttestStore.Total(vault.Uid).HeldSats);
            await AssertBacked(vault.Uid, _holder, _other);
        }

        [Fact]
        public async Task ABitcoinReorganisationThatDropsTheTakersPayment_LeavesItPinnedOnTheCoin()
        {
            var (vault, deposit, slow) = await VaultHeldByASlowWithdrawal();
            var slowPin = Row(slow, vault.Uid).Pins[0];
            await AdvanceTo(slowPin.PinnedAtVfxHeight + Stuck);
            var request = await Mine(Request(vault.Uid, 0.001M, feeRate: 10));
            var taking = Row(request, vault.Uid).Pins[0];
            var at = await Confirm(taking);
            Assert.Equal(VaultWithdrawalState.Settled, Row(request, vault.Uid).State);
            Assert.Equal(VaultWithdrawalState.Void, Row(slow, vault.Uid).State);

            await Reorganise(at - 1);
            // The payment is to be made again, by the same transaction on the same coin. The withdrawal it replaced was
            // refunded in a VFX block and stays closed.
            var row = Row(request, vault.Uid);
            Assert.Equal(VaultWithdrawalState.Pinned, row.State);
            Assert.Equal(VaultWithdrawalState.Void, Row(slow, vault.Uid).State);
            var coin = Coin(deposit, 0);
            Assert.Equal(VaultCoinStatus.Pinned, coin.Status);
            Assert.Equal(row.Ref, coin.PinnedToRef);
            Assert.Null(coin.AlsoPinnedTo);
            Assert.Equal(150_000, BtcAttestStore.Total(vault.Uid).PinnedSats);
            await AssertBacked(vault.Uid, _holder);

            await Confirm(taking);
            Assert.Equal(VaultWithdrawalState.Settled, Row(request, vault.Uid).State);
            Assert.Equal(50_000, BtcAttestStore.Total(vault.Uid).HeldSats);
            await AssertBacked(vault.Uid, _holder);
        }

        /// <summary>
        /// A waiting request lives 360 blocks; a withdrawal stops moving after 2,160. A request made while the vault's
        /// coin is held by a withdrawal that pays less is kept until it can take that coin, so its requester does not
        /// have to come back and ask again at the right moment. One that could never take it runs out as before.
        /// </summary>
        [Fact]
        public async Task ARequestMadeEarly_IsKeptUntilItCanTakeTheStoppedOnesCoins()
        {
            var (vault, deposit, slow) = await VaultHeldByASlowWithdrawal();
            var slowPin = Row(slow, vault.Uid).Pins[0];
            await Mine(Transfer(vault.Uid, 0.0002M, to: _other));

            var early = await Mine(Request(vault.Uid, 0.001M, feeRate: 10));               // pays more: can take, in time
            var never = await Mine(Request(vault.Uid, 0.0002M, from: _other, feeRate: 1));  // pays no more: never can
            Assert.Equal(VaultWithdrawalState.Waiting, Row(early, vault.Uid).State);
            var takesAt = slowPin.PinnedAtVfxHeight + Stuck;
            var retried = takesAt % BtcAttestRules.TakeRetryBlocks == 0 ? takesAt : takesAt + BtcAttestRules.TakeRetryBlocks - takesAt % BtcAttestRules.TakeRetryBlocks;
            Assert.Equal(slowPin.PinnedAtVfxHeight + Stuck + BtcAttestRules.TakeRetryBlocks + 1, BtcWithdrawals.ExpiresAtVfxHeight(Row(early, vault.Uid)));
            Assert.Equal(never.Height + VBTCWithdrawalRequest.EXPIRY_BLOCKS + 1, BtcWithdrawals.ExpiresAtVfxHeight(Row(never, vault.Uid)));

            // Past the ordinary window: the one that can never take the coin is closed and refunded, the other waits on.
            await AdvanceTo(never.Height + VBTCWithdrawalRequest.EXPIRY_BLOCKS + 1);
            Assert.Equal(VaultWithdrawalState.Expired, Row(never, vault.Uid).State);
            Assert.Equal(0.0002M, await Spendable(vault.Uid, _other));
            Assert.Equal(VaultWithdrawalState.Waiting, Row(early, vault.Uid).State);
            Assert.Equal(0.0002M, await Spendable(vault.Uid, Minter));                      // still in escrow

            // Its requester can cancel it at any time while it waits; here it does not.
            await AdvanceTo(retried - 1);
            Assert.Equal(VaultWithdrawalState.Waiting, Row(early, vault.Uid).State);
            await AdvanceTo(retried);
            var row = Row(early, vault.Uid);
            Assert.Equal(VaultWithdrawalState.Pinned, row.State);
            Assert.Equal(new[] { Outpoint(deposit, 0) }, row.Pins[0].Inputs);
            await AssertBacked(vault.Uid, _holder, _other);
        }

        [Fact]
        public async Task ARequestKeptForATakeover_ThatStillCannotBePaid_IsRefundedOnceItHasHadItsTurn()
        {
            var (vault, _, slow) = await VaultHeldByASlowWithdrawal();
            var slowPin = Row(slow, vault.Uid).Pins[0];
            // The slow one raises its fee above the waiting one's before it stops: it can no longer be taken from.
            var waiting = await Mine(Request(vault.Uid, 0.001M, feeRate: 5));
            Assert.Equal(VaultWithdrawalState.Waiting, Row(waiting, vault.Uid).State);
            await AdvanceTo(waiting.Height + VBTCWithdrawalRequest.EXPIRY_BLOCKS + 1);
            Assert.Equal(VaultWithdrawalState.Waiting, Row(waiting, vault.Uid).State);      // kept: 5 sat/vB against 1
            await Mine(Bump(vault.Uid, slow.Hash, 8, from: _holder));
            Assert.Equal(VaultWithdrawalState.Waiting, Row(waiting, vault.Uid).State);
            await Apply(Tip + 1);
            Assert.Equal(VaultWithdrawalState.Expired, Row(waiting, vault.Uid).State);      // nothing left to wait for
            Assert.Equal(0.0014M, await Spendable(vault.Uid, Minter));
            _ = slowPin;
        }
    }
}
