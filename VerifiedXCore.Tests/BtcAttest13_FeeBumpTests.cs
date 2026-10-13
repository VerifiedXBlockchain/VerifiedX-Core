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
    /// Bitcoin facts in consensus, part 13: raising the Bitcoin fee of a pinned withdrawal that is not confirming
    /// (docs/PLAN-vBTC-Bitcoin-Attestation.md, 5.2). The requester sends one transaction naming a higher fee rate. Every
    /// node works out the replacement on the same coins and records it; the two transactions exclude each other on
    /// Bitcoin and whichever confirms completes the withdrawal. Transactions are judged as block validation judges them
    /// and applied with UpdateTreis.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BtcAttest13_FeeBumpTests : BtcAttestTestBase
    {
        private const long Checkpoint = 4032;
        private const string BtcDest = "bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4";
        private readonly Btc _btc = new(Checkpoint - 11);
        private readonly V _holder = NewKey();
        private readonly Network? _priorNetwork = Globals.BTCNetwork;
        private readonly Func<VaultWithdrawalRow, VaultPin, Task<(bool, string)>> _realSign = BtcWithdrawalSigner.SignAndBroadcast;
        private readonly Func<string, Task<bool?>> _realKnown = BtcWithdrawalSigner.KnownOnBitcoin;

        public BtcAttest13_FeeBumpTests() : base(realMainnetRules: false)
        {
            _btc.MineEmpty(12);
            Globals.BitcoinCheckpointHeightMainnet = Checkpoint;
            Globals.BitcoinCheckpointHeadersMainnet = string.Concat(_btc.HeaderHexes(Checkpoint - 11, Checkpoint));
            Globals.BTCNetwork = Network.Main;
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _holder.Address, Balance = 100M, Nonce = 0 });
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

        // ── Bitcoin side ────────────────────────────────────────────────────────────────────────────────────

        private async Task<BtcTx> Deposit(NBitcoin.Script vault, long sats)
        {
            var tx = Pay(vault, sats);
            await Report(_btc.Mine(tx));
            return tx;
        }

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

        /// <summary>Bitcoin confirms one of the withdrawal's transactions, and the validators report it.</summary>
        private async Task Confirm(VaultPin pin)
        {
            var tx = BtcTx.Parse(pin.TxHex, Network.Main);
            Assert.Equal(pin.Txid, tx.GetHash().ToString());
            await Report(_btc.Mine(tx));
        }

        // ── VFX side ────────────────────────────────────────────────────────────────────────────────────────

        private static VfxTx SignedBy(V signer, string to, TransactionType type, object data, decimal amount = 0.0M)
        {
            var tx = new VfxTx
            {
                Timestamp = TimeUtil.GetTime(), FromAddress = signer.Address, ToAddress = to, Amount = amount, Fee = 0, Nonce = 0,
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

        private VfxTx Request(string vault, decimal amount, V? from = null, int feeRate = 10) =>
            SignedBy(from ?? Minter, (from ?? Minter).Address, TransactionType.VBTC_V2_WITHDRAWAL_REQUEST,
                new { Function = "VBTCWithdrawalRequest()", ContractUID = vault, BTCAddress = BtcDest, Amount = amount, FeeRate = feeRate, UniqueId = Guid.NewGuid().ToString("N") });

        private VfxTx Bump(string vault, string requestHash, long feeRate, V? from = null) =>
            SignedBy(from ?? Minter, (from ?? Minter).Address, TransactionType.VBTC_V2_COIN_PIN,
                new { Function = BtcWithdrawals.FeeBumpFunction, ContractUID = vault, WithdrawalRequestHash = requestHash, FeeRate = feeRate, Nonce = Guid.NewGuid().ToString("N") });

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

        private async Task<decimal> Spendable(string vault, V who) =>
            (await VBTCService.TryGetAvailableTransparentVbtcBalance(vault, who.Address)).availableBalance;

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

        // ── The ordinary path ───────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task TheRequester_RaisesTheFee_TheReplacementSpendsTheSameCoins_AndCompletesTheWithdrawal()
        {
            await Start();
            var vault = await CreateVault(H1 + 1, keyHolders: Signers);
            var deposit = await Deposit(vault.Script, 150_000);
            var request = await Mine(Request(vault.Uid, 0.001M));
            var first = Assert.Single(Row(request, vault.Uid).Pins);
            Assert.Equal(10, first.FeeRate);
            Assert.Equal(1_540, first.FeeSats);
            // The first transaction says it may be replaced, so Bitcoin nodes will relay the replacement.
            Assert.All(BtcTx.Parse(first.TxHex, Network.Main).Inputs, i => Assert.Equal(BtcCanonicalWithdrawal.InputSequence, i.Sequence.Value));
            Assert.True(BtcTx.Parse(first.TxHex, Network.Main).RBF);

            // Bitcoin fees have risen and it is not confirming: the requester asks for 25 sat/vB.
            var bump = await Mine(Bump(vault.Uid, request.Hash, 25));
            var row = Row(request, vault.Uid);
            Assert.Equal(VaultWithdrawalState.Pinned, row.State);
            Assert.Equal(Tip, row.LastModifiedHeight);
            Assert.Equal(2, row.Pins.Count);
            Assert.True(row.Pins[0].Superseded);
            Assert.False(row.Pins[0].Dead);
            var second = row.Pins[1];
            Assert.False(second.Superseded);
            Assert.Equal(25, second.FeeRate);
            Assert.Equal(154 * 25, second.FeeSats);
            Assert.Equal(100_000 - 154 * 25, second.DestinationSats);  // the requester pays for it, out of its own amount
            Assert.Equal(50_000, second.ChangeSats);                   // the vault gets back exactly what it would have
            Assert.Equal(first.Inputs, second.Inputs);
            Assert.Equal(Tip, second.PinnedAtVfxHeight);
            Assert.NotEqual(first.Txid, second.Txid);
            var replacement = BtcTx.Parse(second.TxHex, Network.Main);
            Assert.Equal(new OutPoint(deposit, 0), Assert.Single(replacement.Inputs).PrevOut);
            Assert.Equal(BitcoinAddress.Create(BtcDest, Network.Main).ScriptPubKey, replacement.Outputs[0].ScriptPubKey);
            Assert.Equal(vault.Script, replacement.Outputs[1].ScriptPubKey);
            Assert.True(bump.Fee > 0);

            // Nothing moved on the vault or the ledger: the coin is held for the same withdrawal.
            var coin = BtcAttestStore.Coin(Outpoint(deposit, 0))!;
            Assert.Equal(VaultCoinStatus.Pinned, coin.Status);
            Assert.Equal(row.Ref, coin.PinnedToRef);
            Assert.Equal(150_000, BtcAttestStore.Total(vault.Uid).PinnedSats);
            Assert.Equal(0.0005M, await Spendable(vault.Uid, Minter));

            // The signers sign either of the withdrawal's transactions and nothing else; left to themselves they sign the newest.
            Assert.True(FrostSigningAuthorization.Authorize(Announce(vault.Uid, vault.Script, request.Hash, replacement, Signers[2].Address)).Item1);
            Assert.True(FrostSigningAuthorization.Authorize(Announce(vault.Uid, vault.Script, request.Hash, BtcTx.Parse(first.TxHex, Network.Main), Signers[2].Address)).Item1);
            var higherStill = BtcTx.Parse(second.TxHex, Network.Main);
            higherStill.Outputs[0].Value -= Money.Satoshis(5_000);
            var (signs, why) = FrostSigningAuthorization.Authorize(Announce(vault.Uid, vault.Script, request.Hash, higherStill, Signers[2].Address));
            Assert.False(signs);
            Assert.Contains("Only the Bitcoin transaction fixed in state", why);
            var led = new List<string>();
            BtcWithdrawalSigner.SignAndBroadcast = (_, p) => { led.Add(p.Txid); return Task.FromResult((true, p.Txid)); };
            BtcWithdrawalSigner.KnownOnBitcoin = _ => Task.FromResult<bool?>(false);
            await BtcWithdrawalSigner.TickAsync(Signers[1].Address, second.PinnedAtVfxHeight + 4 * BtcWithdrawalSigner.TurnBlocks);
            Assert.Equal(new[] { second.Txid }, led);

            // Bitcoin confirms the replacement.
            await Confirm(second);
            row = Row(request, vault.Uid);
            Assert.Equal(VaultWithdrawalState.Settled, row.State);
            Assert.Equal(second.Txid, row.SettledTxid);
            var done = VBTCWithdrawalRequest.GetByTransactionHash(request.Hash, vault.Uid)!;
            Assert.Equal(VBTCWithdrawalStatus.Completed, done.Status);
            Assert.Equal(second.Txid, done.BTCTxHash);
            Assert.Equal(50_000, BtcAttestStore.Total(vault.Uid).UnspentSats);
            Assert.Equal(0, BtcAttestStore.Total(vault.Uid).PinnedSats);
            Assert.Equal(0.0005M, await Spendable(vault.Uid, Minter));
            await RefusedInBlock(Bump(vault.Uid, request.Hash, 40), "already closed (Settled)");
        }

        [Fact]
        public async Task TheEarlierTransaction_ConfirmingAfterAFeeBump_CompletesTheWithdrawalAllTheSame()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            await Deposit(vault.Script, 150_000);
            await Mine(Transfer(vault.Uid, 0.001M));
            var request = await Mine(Request(vault.Uid, 0.001M, from: _holder));
            await Mine(Bump(vault.Uid, request.Hash, 30, from: _holder));
            var row = Row(request, vault.Uid);
            Assert.Equal(2, row.Pins.Count);

            // A miner had the first one all along.
            await Confirm(row.Pins[0]);
            row = Row(request, vault.Uid);
            Assert.Equal(VaultWithdrawalState.Settled, row.State);
            Assert.Equal(row.Pins[0].Txid, row.SettledTxid);
            Assert.Equal(VBTCWithdrawalStatus.Completed, VBTCWithdrawalRequest.GetByTransactionHash(request.Hash, vault.Uid)!.Status);
            Assert.Equal(0M, await Spendable(vault.Uid, _holder));
            Assert.Equal(0.0005M, await Spendable(vault.Uid, Minter));
            Assert.Equal(50_000, BtcAttestStore.Total(vault.Uid).HeldSats);
        }

        [Fact]
        public async Task AValidatorThatSignedTheFirstTransaction_SignsTheReplacementToo()
        {
            var older = await CreateVault(H1 - 50);
            await Start();
            var vault = await CreateVault(H1 + 1, keyHolders: Signers);
            await Deposit(vault.Script, 150_000);
            var request = await Mine(Request(vault.Uid, 0.001M));
            await Mine(Bump(vault.Uid, request.Hash, 25));
            var pins = Row(request, vault.Uid).Pins;
            var (first, second) = (pins[0], pins[1]);

            // Each validator keeps its own record of what it signed for a withdrawal. On a watched vault that record
            // is per pinned transaction; on any other vault it is per withdrawal, as it has always been.
            var scopeFirst = FrostSigningAuthorization.SigningScope(vault.Uid, first.Txid.ToUpperInvariant());
            var scopeSecond = FrostSigningAuthorization.SigningScope(vault.Uid, second.Txid);
            Assert.Equal(first.Txid, scopeFirst);
            Assert.Equal(second.Txid, scopeSecond);
            Assert.Null(FrostSigningAuthorization.SigningScope(older.Uid, first.Txid));
            Assert.Null(FrostSigningAuthorization.SigningScope(vault.Uid, ""));

            // This validator gave its share for the first transaction.
            Assert.False(FrostWithdrawalSigningTracker.TryBeginSigning(vault.Uid, request.Hash, "s-first", 0, "aa11", new List<string> { "aa11" }, first.Inputs, null, scopeFirst).Blocked);
            FrostWithdrawalSigningTracker.RecordSigningCompleted(vault.Uid, request.Hash, "s-first", 0, "aa11", first.Inputs, first.Txid, scopeFirst);

            // The replacement has another sighash for the same input of the same withdrawal. It is signed.
            var (blocked, reason) = FrostWithdrawalSigningTracker.TryBeginSigning(vault.Uid, request.Hash, "s-second", 0, "bb22", new List<string> { "bb22" }, second.Inputs, null, scopeSecond);
            Assert.False(blocked, reason);
            Assert.True(FrostWithdrawalSigningTracker.ConfirmSessionHoldsInput(vault.Uid, request.Hash, 0, "s-second", "bb22", scopeSecond).Ok);
            FrostWithdrawalSigningTracker.RecordSigningCompleted(vault.Uid, request.Hash, "s-second", 0, "bb22", second.Inputs, second.Txid, scopeSecond);
            // The first stays signable too (a retry of its ceremony), and the two records do not disturb each other.
            Assert.False(FrostWithdrawalSigningTracker.TryBeginSigning(vault.Uid, request.Hash, "s-first-again", 0, "aa11", new List<string> { "aa11" }, first.Inputs, null, scopeFirst).Blocked);
            Assert.True(FrostWithdrawalSigningTracker.ConfirmSessionHoldsInput(vault.Uid, request.Hash, 0, "s-second", "bb22", scopeSecond).Ok);
            // The earlier durable check ("this validator already signed another transaction for this withdrawal") gives way to the pin.
            var (priorOk, priorReason) = await FrostStartup.CheckPriorSignedTransactionAsync(vault.Uid, request.Hash, second.Txid);
            Assert.True(priorOk, priorReason);

            // Within one pinned transaction nothing changed: its sighash is its sighash.
            (blocked, reason) = FrostWithdrawalSigningTracker.TryBeginSigning(vault.Uid, request.Hash, "s-forged", 0, "cc33", new List<string> { "cc33" }, second.Inputs, null, scopeSecond);
            Assert.True(blocked);
            Assert.Contains("exactly one Bitcoin transaction", reason);

            // And on a vault the chain does not watch, a second transaction for a withdrawal is refused as before.
            Assert.False(FrostWithdrawalSigningTracker.TryBeginSigning(older.Uid, request.Hash, "o-1", 0, "aa11", new List<string> { "aa11" }, first.Inputs).Blocked);
            FrostWithdrawalSigningTracker.RecordSigningCompleted(older.Uid, request.Hash, "o-1", 0, "aa11", first.Inputs, first.Txid);
            (blocked, reason) = FrostWithdrawalSigningTracker.TryBeginSigning(older.Uid, request.Hash, "o-2", 0, "bb22", new List<string> { "bb22" }, second.Inputs);
            Assert.True(blocked);
            Assert.Contains("exactly one Bitcoin transaction", reason);
        }

        // ── What is refused ─────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task AFeeBump_IsTheRequestersOnly_ForAHigherRate_OnAPinnedWithdrawal()
        {
            var older = await CreateVault(H1 - 50);
            await Start();
            var vault = await CreateVault(H1 + 1);
            await Deposit(vault.Script, 150_000);
            await Mine(Transfer(vault.Uid, 0.0003M));
            var request = await Mine(Request(vault.Uid, 0.001M));                      // pinned, holds the only coin
            var waiting = await Mine(Request(vault.Uid, 0.0002M, from: _holder));      // waits for coins
            Assert.Equal(VaultWithdrawalState.Waiting, Row(waiting, vault.Uid).State);

            await RefusedInBlock(Bump(vault.Uid, request.Hash, 25, from: _holder), "Only the requester");
            await RefusedInBlock(Bump(vault.Uid, request.Hash, 10), "must be above the 10 sat/vB");
            await RefusedInBlock(Bump(vault.Uid, request.Hash, 9), "must be above the 10 sat/vB");
            await RefusedInBlock(Bump(vault.Uid, request.Hash, 0), "a fee rate above zero");
            await RefusedInBlock(Bump(vault.Uid, request.Hash, -5), "a fee rate above zero");
            await RefusedInBlock(Bump(vault.Uid, new string('a', 64), 25), "No such withdrawal request");
            await RefusedInBlock(Bump(older.Uid, request.Hash, 25), "Only a withdrawal from a vault the chain watches");
            await RefusedInBlock(Bump(vault.Uid, waiting.Hash, 25, from: _holder), "has no Bitcoin transaction yet");
            // The fee comes out of the amount: it cannot leave less than a payable output (154 vB x 646 > 100,000 - 546).
            await RefusedInBlock(Bump(vault.Uid, request.Hash, 646), "leaves less than 546 sats");
            await RefusedInBlock(SignedBy(Minter, _holder.Address, TransactionType.VBTC_V2_COIN_PIN,
                new { Function = BtcWithdrawals.FeeBumpFunction, ContractUID = vault.Uid, WithdrawalRequestHash = request.Hash, FeeRate = 25 }), "sent by the requester to itself");
            await RefusedInBlock(SignedBy(Minter, Minter.Address, TransactionType.VBTC_V2_COIN_PIN,
                new { Function = "SomethingElse()", ContractUID = vault.Uid, WithdrawalRequestHash = request.Hash, FeeRate = 25 }), "function is VBTCWithdrawalFeeBump()");
            await RefusedInBlock(SignedBy(Minter, Minter.Address, TransactionType.VBTC_V2_COIN_PIN,
                new { Function = BtcWithdrawals.FeeBumpFunction, ContractUID = vault.Uid, WithdrawalRequestHash = request.Hash, FeeRate = 25 }, amount: 1.0M), "carries no amount");
            Assert.Single(Row(request, vault.Uid).Pins);

            // The highest rate the amount can carry is accepted, and each later bump must beat the one before.
            await Mine(Bump(vault.Uid, request.Hash, 25));
            await RefusedInBlock(Bump(vault.Uid, request.Hash, 25), "must be above the 25 sat/vB");
            await Mine(Bump(vault.Uid, request.Hash, 645));
            Assert.Equal(100_000 - 154 * 645, Row(request, vault.Uid).Pins[2].DestinationSats);
            await RefusedInBlock(Bump(vault.Uid, request.Hash, 646), "leaves less than 546 sats");
        }

        [Fact]
        public async Task AWithdrawal_HasAtMostEightTransactions_AndABlockCarriesOneBumpForIt()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            await Deposit(vault.Script, 150_000);
            var request = await Mine(Request(vault.Uid, 0.001M));
            for (var rate = 11; rate <= 17; rate++)
                await Mine(Bump(vault.Uid, request.Hash, rate));
            var row = Row(request, vault.Uid);
            Assert.Equal(BtcWithdrawals.MaxPins, row.Pins.Count);
            Assert.Equal(BtcWithdrawals.MaxPins, row.Pins.Select(p => p.Txid).Distinct().Count());
            Assert.All(row.Pins.Take(7), p => Assert.True(p.Superseded));
            await RefusedInBlock(Bump(vault.Uid, request.Hash, 18), "already has 8 Bitcoin transactions");

            // Any of the eight completes it.
            await Confirm(row.Pins[3]);
            Assert.Equal(VaultWithdrawalState.Settled, Row(request, vault.Uid).State);
            Assert.Equal(row.Pins[3].Txid, Row(request, vault.Uid).SettledTxid);

            // One bump per withdrawal in a block: each is judged against the state before the block.
            var other = await Mine(Request(vault.Uid, 0.0002M));
            var a = Bump(vault.Uid, other.Hash, 20);
            var b = Bump(vault.Uid, other.Hash, 30);
            var elsewhere = Bump(vault.Uid, request.Hash, 30);
            var state = new BtcAttestBlockRules.State();
            Assert.True(BtcAttestBlockRules.TryRegister(a, state).Ok);
            Assert.True(BtcAttestBlockRules.TryRegister(elsewhere, state).Ok);
            var (ok, reason) = BtcAttestBlockRules.TryRegister(b, state);
            Assert.False(ok);
            Assert.Equal(BtcAttestBlockRules.OneBumpReason, reason);
            Assert.Equal(new[] { a.Hash, elsewhere.Hash }, BtcAttestBlockRules.KeepOnePerBlock(new List<VfxTx> { a, elsewhere, b }).Select(t => t.Hash));

            // And a bump that an earlier one has overtaken changes nothing when applied (defence in depth).
            await Apply(Tip + 1, b, a);
            var pins = Row(other, vault.Uid).Pins;
            Assert.Equal(2, pins.Count);
            Assert.Equal(30, pins[1].FeeRate);
        }

        [Fact]
        public async Task AReplacement_IsBuiltOnlyOnCoinsHeldForThatWithdrawal()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            var first = Pay(vault.Script, 150_000);
            var second = Pay(vault.Script, 120_000);
            await Report(_btc.Mine(first, second));
            var request = await Mine(Request(vault.Uid, 0.001M));
            var row = Row(request, vault.Uid);
            var pin = Assert.Single(row.Pins);
            var free = BtcAttestStore.FreeCoinsOf(vault.Uid).Single().Outpoint;

            Assert.NotNull(BtcCanonicalWithdrawal.BuildReplacement(vault.Uid, row.DestinationScriptHex, row.AmountSats, 20, pin.Inputs, row.Ref, out _));
            Assert.Null(BtcCanonicalWithdrawal.BuildReplacement(vault.Uid, row.DestinationScriptHex, row.AmountSats, 20, new[] { free }, row.Ref, out var why));
            Assert.Contains("is not held for this withdrawal", why);
            Assert.Null(BtcCanonicalWithdrawal.BuildReplacement(vault.Uid, row.DestinationScriptHex, row.AmountSats, 20, pin.Inputs, "another|" + vault.Uid, out why));
            Assert.Contains("is not held for this withdrawal", why);
            Assert.Null(BtcCanonicalWithdrawal.BuildReplacement(vault.Uid, row.DestinationScriptHex, row.AmountSats, 20, Array.Empty<string>(), row.Ref, out _));

            // After the withdrawal confirms its coins are spent: nothing can be built on them.
            await Confirm(pin);
            Assert.Null(BtcCanonicalWithdrawal.BuildReplacement(vault.Uid, row.DestinationScriptHex, row.AmountSats, 20, pin.Inputs, row.Ref, out _));
        }

        /// <summary>
        /// On a watched vault any signer of the vault may lead the ceremony for a pinned transaction. One who starts
        /// ceremonies and never finishes them must not be able to keep the others out: every ceremony for a pinned
        /// transaction signs that same transaction, so they run side by side, one per leader.
        /// </summary>
        [Fact]
        public async Task ASignerThatStartsACeremonyAndNeverFinishes_DoesNotKeepTheOthersOut()
        {
            var older = await CreateVault(H1 - 50);
            await Start();
            var vault = await CreateVault(H1 + 1, keyHolders: Signers);
            await Deposit(vault.Script, 150_000);
            var request = await Mine(Request(vault.Uid, 0.001M));
            var pin = Assert.Single(Row(request, vault.Uid).Pins);
            var scope = FrostSigningAuthorization.SigningScope(vault.Uid, pin.Txid);
            var sighashes = new List<string> { "aa11" };
            (bool Blocked, string Reason) Begin(string session, V leader, string sighash = "aa11") =>
                FrostWithdrawalSigningTracker.TryBeginSigning(vault.Uid, request.Hash, session, 0, sighash, new List<string> { sighash }, pin.Inputs, null, scope, leader.Address);
            bool Holds(string session) => FrostWithdrawalSigningTracker.ConfirmSessionHoldsInput(vault.Uid, request.Hash, 0, session, "aa11", scope).Ok;

            // One signer starts and goes quiet.
            Assert.False(Begin("stalled", Signers[3]).Blocked);
            // Another leads the same transaction at once, and both ceremonies are this validator's.
            var (blocked, reason) = Begin("honest", Signers[1]);
            Assert.False(blocked, reason);
            Assert.True(Holds("honest"));
            Assert.True(Holds("stalled"));
            Assert.False(Holds("never-started"));

            // Starting again does not push anybody else's ceremony out, only the leader's own earlier one.
            Assert.False(Begin("stalled-again", Signers[3]).Blocked);
            Assert.True(Holds("honest"));
            Assert.False(Holds("stalled"));
            Assert.True(Holds("stalled-again"));

            // A ceremony that failed lays no wait on the input: the next one starts straight away.
            FrostWithdrawalSigningTracker.RecordSigningFailed(vault.Uid, request.Hash, "stalled-again", 0, scope);
            Assert.False(Holds("stalled-again"));
            Assert.True(Holds("honest"));
            Assert.False(Begin("third", Signers[2]).Blocked);

            // What is signed is still one transaction only: another sighash for this input is refused.
            (blocked, reason) = Begin("forged", Signers[3], "cc33");
            Assert.True(blocked);
            Assert.Contains("exactly one Bitcoin transaction", reason);

            // The share is given, and a later ceremony for the same transaction still gets one (a retry after a lost broadcast).
            FrostWithdrawalSigningTracker.RecordSigningCompleted(vault.Uid, request.Hash, "honest", 0, "aa11", pin.Inputs, pin.Txid, scope);
            Assert.False(Begin("retry", Signers[0]).Blocked);

            // On a vault the chain does not watch nothing changed: one ceremony holds an input.
            Assert.False(FrostWithdrawalSigningTracker.TryBeginSigning(older.Uid, request.Hash, "o-1", 0, "aa11", sighashes, pin.Inputs).Blocked);
            (blocked, reason) = FrostWithdrawalSigningTracker.TryBeginSigning(older.Uid, request.Hash, "o-2", 0, "aa11", sighashes, pin.Inputs);
            Assert.True(blocked);
            Assert.Contains("already in progress", reason);
        }
    }
}
