using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NBitcoin;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Newtonsoft.Json;
using VerifiedXCore.Bitcoin.Attestation;
using VerifiedXCore.Bitcoin.FROST;
using VerifiedXCore.Bitcoin.FROST.Models;
using VerifiedXCore.Bitcoin.Models;
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
    /// Bitcoin facts in consensus, part 10: signing a pinned withdrawal (docs/PLAN-vBTC-Bitcoin-Attestation.md, 5.1
    /// and 5.2). A signer of a watched vault gives a signature share for the transaction consensus has pinned for a
    /// withdrawal and for no other; and the vault's signers start the ceremony themselves, in an order nobody appoints.
    /// The authorization is the one every validator runs before contributing a share (FrostSigningAuthorization).
    /// </summary>
    [Collection("DbContextSequential")]
    public class BtcAttest10_PinnedSigningTests : BtcAttestTestBase
    {
        private const long Checkpoint = 4032;
        private const string BtcDest = "bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4";
        private readonly Btc _btc = new(Checkpoint - 11);
        private readonly V _holder = NewKey();
        private readonly Network? _priorNetwork = Globals.BTCNetwork;

        public BtcAttest10_PinnedSigningTests() : base(realMainnetRules: false)
        {
            _btc.MineEmpty(12);
            Globals.BitcoinCheckpointHeightMainnet = Checkpoint;
            Globals.BitcoinCheckpointHeadersMainnet = string.Concat(_btc.HeaderHexes(Checkpoint - 11, Checkpoint));
            Globals.BTCNetwork = Network.Main;
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _holder.Address, Balance = 100M, Nonce = 0 });
            BtcWithdrawalSigner.ForgetAttempts();
        }

        public override void Dispose()
        {
            BtcWithdrawalSigner.SignAndBroadcast = _realSign;
            BtcWithdrawalSigner.KnownOnBitcoin = _realKnown;
            BtcWithdrawalSigner.ForgetAttempts();
            Globals.BTCNetwork = _priorNetwork;
            base.Dispose();
        }

        private readonly Func<VaultWithdrawalRow, VaultPin, Task<(bool, string)>> _realSign = BtcWithdrawalSigner.SignAndBroadcast;
        private readonly Func<string, Task<bool?>> _realKnown = BtcWithdrawalSigner.KnownOnBitcoin;
        private static long Tip => Globals.LastBlock.Height;

        private async Task Report(long bitcoinHeight)
        {
            _btc.MineEmpty(BtcAttestRules.Depth);
            var meta = BtcAttestStore.GetMeta()!;
            var headers = _btc.HeaderHexes(meta.TipHeight + 1, bitcoinHeight + BtcAttestRules.Depth);
            await Apply(Tip + 1, Signed(Assert.Single(BtcBlockScanner.BuildForward(_btc.Block(bitcoinHeight), bitcoinHeight, headers, 0))));
        }

        private async Task<VfxTx> Request(string vault, decimal amount, V? from = null)
        {
            var signer = from ?? Minter;
            var tx = new VfxTx
            {
                Timestamp = TimeUtil.GetTime(), FromAddress = signer.Address, ToAddress = signer.Address, Amount = 0.0M, Fee = 0, Nonce = 0,
                TransactionType = TransactionType.VBTC_V2_WITHDRAWAL_REQUEST,
                Data = JsonConvert.SerializeObject(new { Function = "VBTCWithdrawalRequest()", ContractUID = vault, BTCAddress = BtcDest, Amount = amount, FeeRate = 10, UniqueId = Guid.NewGuid().ToString("N") }),
            };
            tx.Fee = VerifiedXCore.Services.FeeCalcService.CalculateTXFee(tx);
            tx.Build();
            tx.Signature = VerifiedXCore.Services.SignatureService.CreateSignature(tx.Hash, signer.Key, signer.Pub);
            var (ok, why) = await Verify(tx, Tip + 1);
            Assert.True(ok, why);
            await Apply(Tip + 1, tx);
            return tx;
        }

        /// <summary>What a ceremony leader announces to the signers for a transaction: the bytes, the coins it spends, and every input's sighash.</summary>
        private static FrostSigningStartRequest Announce(string vault, NBitcoin.Script vaultScript, string requestHash, BtcTx tx, string leader)
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
                SessionId = Guid.NewGuid().ToString(), SmartContractUID = vault, WithdrawalRequestHash = requestHash, LeaderAddress = leader,
                MessageHash = sighashes[0], InputIndex = 0, InputCount = tx.Inputs.Count, AllInputSighashes = sighashes,
                UnsignedTxHex = tx.ToHex(), Prevouts = prevouts, Timestamp = TimeUtil.GetTime(),
            };
        }

        /// <summary>
        /// A signing start was authorized by "{session}.{leader}.{timestamp}" alone, which names no content: a key holder
        /// holding one of the leader's could open a session under it with content of its own (round 17 (2)). And a start
        /// whose session was already open recorded a failure under that id, dropping the leader's own open session
        /// (round 17 (1)). A watched vault's start is signed in full; an id is opened once and a copy changes nothing.
        /// </summary>
        [Fact]
        public async Task AWatchedVaultsSigningStart_IsSignedInFull_AndAnIdIsOpenedOnce()
        {
            await Start();
            var vault = await CreateVault(H1 + 1, keyHolders: Signers);
            await Report(_btc.Mine(Pay(vault.Script, 150_000)));
            var request = await Request(vault.Uid, 0.001M);
            var pinned = BtcTx.Parse(Assert.Single(BtcAttestStore.Withdrawal(BtcWithdrawals.RefOf(request.Hash, vault.Uid))!.Pins).TxHex, Network.Main);
            var leader = Signers[2];
            using var server = new TestServer(new WebHostBuilder().UseStartup<FrostStartup>());
            var client = server.CreateClient();
            async Task<(System.Net.HttpStatusCode Code, string Body)> Post(FrostSigningStartRequest start)
            {
                var r = await client.PostAsync("/frost/sign/start", new System.Net.Http.StringContent(JsonConvert.SerializeObject(start), System.Text.Encoding.UTF8, "application/json"));
                return (r.StatusCode, await r.Content.ReadAsStringAsync());
            }
            FrostSigningStartRequest Start2(bool full)
            {
                var start = Announce(vault.Uid, vault.Script, request.Hash, pinned, leader.Address);
                start.SignerAddresses = Signers.Select(s => s.Address).ToList();
                start.RequiredThreshold = 51;
                start.LeaderSignature = VerifiedXCore.Services.SignatureService.CreateSignature($"{start.SessionId}.{leader.Address}.{start.Timestamp}", leader.Key, leader.Pub);
                if (full) start.ContentSignature = VerifiedXCore.Services.SignatureService.CreateSignature(FrostDkgLeaderAuth.SigningStartMessage(start), leader.Key, leader.Pub);
                return start;
            }

            var bare = await Post(Start2(full: false));
            Assert.Equal(System.Net.HttpStatusCode.Forbidden, bare.Code);
            Assert.Contains("signed in full", bare.Body);
            var changed = Start2(full: true);
            changed.RequiredThreshold = 52;                                                   // content not what was signed
            Assert.Contains("signed in full", (await Post(changed)).Body);
            Assert.DoesNotContain("signed in full", (await Post(Start2(full: true))).Body);   // passes this check

            // An id already open, or opened here before: refused up front, nothing touched.
            var open = Start2(full: true);
            FrostSessionStorage.SigningSessions.TryAdd(open.SessionId, new SigningSession { SessionId = open.SessionId, LeaderAddress = leader.Address, StartTimestamp = TimeUtil.GetTime(), LeaderStartTimestamp = open.Timestamp });
            try
            {
                var copy = await Post(open);
                Assert.True(copy.Code == System.Net.HttpStatusCode.Conflict, copy.Body);
                Assert.True(FrostSessionStorage.SigningSessions.ContainsKey(open.SessionId));
            }
            finally { FrostSessionStorage.SigningSessions.TryRemove(open.SessionId, out _); }
            var used = Start2(full: true);
            FrostSessionStorage.NoteSigningStart(used.SessionId);
            Assert.Equal(System.Net.HttpStatusCode.Conflict, (await Post(used)).Code);
        }

        [Fact]
        public async Task ASigner_SignsThePinnedTransaction_AndNoOther()
        {
            await Start();
            var vault = await CreateVault(H1 + 1, keyHolders: Signers);
            var first = Pay(vault.Script, 150_000);
            var second = Pay(vault.Script, 120_000);
            await Report(_btc.Mine(first, second));
            var request = await Request(vault.Uid, 0.001M);
            var row = BtcAttestStore.Withdrawal(BtcWithdrawals.RefOf(request.Hash, vault.Uid))!;
            var pin = Assert.Single(row.Pins);
            var pinned = BtcTx.Parse(pin.TxHex, Network.Main);

            // Led by any signer of the vault, or by the requester: the transaction is the same whoever leads.
            var (ok, why) = FrostSigningAuthorization.Authorize(Announce(vault.Uid, vault.Script, request.Hash, pinned, Signers[2].Address));
            Assert.True(ok, why);
            (ok, why) = FrostSigningAuthorization.Authorize(Announce(vault.Uid, vault.Script, request.Hash, pinned, Minter.Address));
            Assert.True(ok, why);
            // Not by somebody who is neither.
            (ok, why) = FrostSigningAuthorization.Authorize(Announce(vault.Uid, vault.Script, request.Hash, pinned, Unfunded.Address));
            Assert.False(ok);
            Assert.Contains("led by the withdrawal owner", why);

            void Refused(BtcTx other)
            {
                var (accepted, reason) = FrostSigningAuthorization.Authorize(Announce(vault.Uid, vault.Script, request.Hash, other, Signers[2].Address));
                Assert.False(accepted, "a signer would have signed a transaction that is not the pinned one");
                Assert.Contains("Only the Bitcoin transaction fixed in state", reason);
            }

            // Every one of these passed the earlier rules (it pays the right destination within the amount, change
            // goes back to the vault, it spends the vault's own coins). None is the pinned transaction.
            var lessToTheUser = BtcTx.Parse(pin.TxHex, Network.Main);
            lessToTheUser.Outputs[0].Value -= Money.Satoshis(10_000); // a larger fee
            Refused(lessToTheUser);

            var otherCoin = BtcTx.Parse(pin.TxHex, Network.Main);
            otherCoin.Inputs[0].PrevOut = new OutPoint(first, 0); // the vault's other, unpinned coin
            Assert.NotEqual(pinned.Inputs[0].PrevOut, otherCoin.Inputs[0].PrevOut);
            otherCoin.Outputs[1].Value = Money.Satoshis(50_000);
            Refused(otherCoin);

            var final = BtcTx.Parse(pin.TxHex, Network.Main);
            final.Inputs[0].Sequence = Sequence.Final; // not replaceable any more: a different transaction
            Refused(final);

            var locked = BtcTx.Parse(pin.TxHex, Network.Main);
            locked.LockTime = new LockTime(900_000);
            Refused(locked);
        }

        [Fact]
        public async Task NothingIsSigned_ForAWithdrawalWithNoPin_OrOneAlreadyPaid()
        {
            await Start();
            var vault = await CreateVault(H1 + 1, keyHolders: Signers);
            var deposit = Pay(vault.Script, 150_000);
            await Report(_btc.Mine(deposit));
            var paid = await Request(vault.Uid, 0.001M);
            var waiting = await Request(vault.Uid, 0.0003M); // the only coin is held: nothing is fixed for this one
            Assert.Equal(VaultWithdrawalState.Waiting, BtcAttestStore.Withdrawal(BtcWithdrawals.RefOf(waiting.Hash, vault.Uid))!.State);

            // A transaction that would pay the waiting request, spending the coin held for the other one.
            var grab = Network.Main.CreateTransaction();
            grab.Version = 2;
            grab.Inputs.Add(new TxIn(new OutPoint(deposit, 0)) { Sequence = Sequence.Final });
            grab.Outputs.Add(Money.Satoshis(28_460), BitcoinAddress.Create(BtcDest, Network.Main).ScriptPubKey);
            grab.Outputs.Add(Money.Satoshis(120_000), vault.Script);
            var (ok, why) = FrostSigningAuthorization.Authorize(Announce(vault.Uid, vault.Script, waiting.Hash, grab, Minter.Address));
            Assert.False(ok);
            Assert.Contains("No Bitcoin transaction is fixed in state", why);

            // The pinned one confirms and is reported: nothing more is signed for it.
            var pin = BtcAttestStore.Withdrawal(BtcWithdrawals.RefOf(paid.Hash, vault.Uid))!.Pins[0];
            var tx = BtcTx.Parse(pin.TxHex, Network.Main);
            Assert.True(FrostSigningAuthorization.Authorize(Announce(vault.Uid, vault.Script, paid.Hash, tx, Signers[0].Address)).Ok);
            await Report(_btc.Mine(tx));
            (ok, why) = FrostSigningAuthorization.Authorize(Announce(vault.Uid, vault.Script, paid.Hash, tx, Signers[0].Address));
            Assert.False(ok);
            Assert.Contains("already completed", why);
        }

        [Fact]
        public async Task AnOldVault_KeepsItsOwnerLedSigning()
        {
            var legacy = await CreateVault(H1 - 50, keyHolders: Signers);
            await Start();
            using var electrum = new FakeElectrum(("a", true, 1M));
            var signer = Minter;
            var tx = new VfxTx
            {
                Timestamp = TimeUtil.GetTime(), FromAddress = signer.Address, ToAddress = signer.Address, Amount = 0.0M, Fee = 0, Nonce = 0,
                TransactionType = TransactionType.VBTC_V2_WITHDRAWAL_REQUEST,
                Data = JsonConvert.SerializeObject(new { Function = "VBTCWithdrawalRequest()", ContractUID = legacy.Uid, BTCAddress = BtcDest, Amount = 0.001M, FeeRate = 10, UniqueId = Guid.NewGuid().ToString("N") }),
            };
            tx.Fee = VerifiedXCore.Services.FeeCalcService.CalculateTXFee(tx);
            tx.Build();
            await Apply(Tip + 1, tx);

            // Any transaction within the bounds, built by the owner from coins only Bitcoin knows about.
            var built = Network.Main.CreateTransaction();
            built.Inputs.Add(new TxIn(new OutPoint(new uint256(RandomUtils.GetBytes(32)), 0)));
            built.Outputs.Add(Money.Satoshis(70_000), BitcoinAddress.Create(BtcDest, Network.Main).ScriptPubKey);
            var (ok, why) = FrostSigningAuthorization.Authorize(Announce(legacy.Uid, legacy.Script, tx.Hash, built, Minter.Address));
            Assert.True(ok, why);
            (ok, why) = FrostSigningAuthorization.Authorize(Announce(legacy.Uid, legacy.Script, tx.Hash, built, Signers[0].Address));
            Assert.False(ok);
            Assert.Contains("led by the withdrawal owner", why);
        }

        /// <summary>
        /// The pinned withdrawals are taken oldest first. One that throws when it is led (its vault names a proof height
        /// in the future, a signer library fault) used to end the pass there, on every pass, so no later withdrawal of
        /// any vault was ever led by this validator (second path audit, 14 October 2026).
        /// </summary>
        [Fact]
        public async Task AWithdrawalThatCannotBeLed_DoesNotStopTheOnesAfterIt()
        {
            await Start();
            var vault = await CreateVault(H1 + 1, keyHolders: Signers);
            await Report(_btc.Mine(Pay(vault.Script, 150_000)));
            await Report(_btc.Mine(Pay(vault.Script, 160_000)));
            var first = await Request(vault.Uid, 0.001M);
            var second = await Request(vault.Uid, 0.0012M);
            var firstPin = BtcAttestStore.Withdrawal(BtcWithdrawals.RefOf(first.Hash, vault.Uid))!.Pins[0];
            var secondPin = BtcAttestStore.Withdrawal(BtcWithdrawals.RefOf(second.Hash, vault.Uid))!.Pins[0];

            BtcWithdrawalSigner.KnownOnBitcoin = _ => Task.FromResult<bool?>(null);
            BtcWithdrawalSigner.SignAndBroadcast = (row, p) =>
                row.RequestHash == first.Hash ? throw new ArgumentException("Current block cannot be before last activity block") : Task.FromResult((true, p.Txid));

            var late = Math.Max(firstPin.PinnedAtVfxHeight, secondPin.PinnedAtVfxHeight) + 10 * BtcWithdrawalSigner.TurnBlocks; // everybody's turn has come
            var led = await BtcWithdrawalSigner.TickAsync(Signers[0].Address, late);
            Assert.Equal(new[] { secondPin.Txid }, led);
        }

        [Fact]
        public async Task TheVaultsSigners_StartTheCeremonyThemselves_OneAtATime_UntilBitcoinHasTheTransaction()
        {
            await Start();
            var vault = await CreateVault(H1 + 1, keyHolders: Signers);
            await Report(_btc.Mine(Pay(vault.Script, 150_000)));
            var request = await Request(vault.Uid, 0.001M);
            var pin = BtcAttestStore.Withdrawal(BtcWithdrawals.RefOf(request.Hash, vault.Uid))!.Pins[0];
            var pinnedAt = pin.PinnedAtVfxHeight;

            var addresses = Signers.Select(s => s.Address).ToList();
            Assert.Equal(addresses.OrderBy(a => a, StringComparer.Ordinal), BtcWithdrawalSigner.SignersOf(vault.Uid)); // in ordinal order, as the key records them
            var order = addresses.OrderBy(a => BtcWithdrawalSigner.Turn(pin.Txid, a, addresses)).ToList();
            Assert.Equal(new[] { 0, 1, 2, 3 }, order.Select(a => BtcWithdrawalSigner.Turn(pin.Txid, a, addresses)));

            var onBitcoin = false;
            var led = new List<string>();
            var succeed = false;
            BtcWithdrawalSigner.KnownOnBitcoin = _ => Task.FromResult<bool?>(onBitcoin ? true : null);
            BtcWithdrawalSigner.SignAndBroadcast = (row, p) =>
            {
                Assert.Equal(pin.Txid, p.Txid);
                led.Add(row.RequestHash);
                return Task.FromResult((succeed, succeed ? p.Txid : "validators unreachable"));
            };

            // At the pin's block only the first in line acts. Somebody who holds no share of this vault never does.
            Assert.Empty(await BtcWithdrawalSigner.TickAsync(order[1], pinnedAt));
            Assert.Empty(await BtcWithdrawalSigner.TickAsync(Unfunded.Address, pinnedAt + 1_000));
            Assert.Equal(new[] { pin.Txid }, await BtcWithdrawalSigner.TickAsync(order[0], pinnedAt));

            // Its ceremony failed. One turn later the second in line starts as well; the third still waits.
            BtcWithdrawalSigner.ForgetAttempts();
            Assert.Empty(await BtcWithdrawalSigner.TickAsync(order[1], pinnedAt + BtcWithdrawalSigner.TurnBlocks - 1));
            Assert.Empty(await BtcWithdrawalSigner.TickAsync(order[2], pinnedAt + BtcWithdrawalSigner.TurnBlocks));
            succeed = true;
            Assert.Single(await BtcWithdrawalSigner.TickAsync(order[1], pinnedAt + BtcWithdrawalSigner.TurnBlocks));
            Assert.Equal(2, led.Count);

            // It is out. Nobody starts again: not the one that sent it, and not the others once Bitcoin shows it.
            Assert.Empty(await BtcWithdrawalSigner.TickAsync(order[1], pinnedAt + 50));
            onBitcoin = true;
            foreach (var signer in order)
                Assert.Empty(await BtcWithdrawalSigner.TickAsync(signer, pinnedAt + 500));
            Assert.Equal(2, led.Count);

            // Once Bitcoin's block is reported the withdrawal is settled and there is nothing to act on at all.
            await Report(_btc.Mine(BtcTx.Parse(pin.TxHex, Network.Main)));
            onBitcoin = false;
            BtcWithdrawalSigner.ForgetAttempts();
            foreach (var signer in order)
                Assert.Empty(await BtcWithdrawalSigner.TickAsync(signer, Tip + 500));
        }

        /// <summary>
        /// "Nothing is pinned, so nothing is signed" holds only while the block that made the pin stays in the chain.
        /// The signers therefore leave a pin's block to settle before anyone leads or joins a ceremony for it: a
        /// signature given at once would outlive the block if it were replaced.
        /// </summary>
        [Fact]
        public async Task NoSignerSignsAPin_UntilItsBlockHasSettled()
        {
            BtcWithdrawalSigner.SettleBlocksOverride = null; // the real wait
            Assert.Equal(10, BtcWithdrawalSigner.SettleBlocks);
            await Start();
            var vault = await CreateVault(H1 + 1, keyHolders: Signers);
            await Report(_btc.Mine(Pay(vault.Script, 150_000)));
            var request = await Request(vault.Uid, 0.001M);
            var pin = Assert.Single(BtcAttestStore.Withdrawal(BtcWithdrawals.RefOf(request.Hash, vault.Uid))!.Pins);
            var pinned = BtcTx.Parse(pin.TxHex, Network.Main);
            var pinnedAt = pin.PinnedAtVfxHeight;
            Assert.Equal(Tip, pinnedAt);

            var led = new List<string>();
            BtcWithdrawalSigner.KnownOnBitcoin = _ => Task.FromResult<bool?>(null);
            BtcWithdrawalSigner.SignAndBroadcast = (_, p) => { led.Add(p.Txid); return Task.FromResult((true, p.Txid)); };
            var addresses = Signers.Select(s => s.Address).ToList();
            var first = addresses.OrderBy(a => BtcWithdrawalSigner.Turn(pin.Txid, a, addresses)).First();

            // In the pin's own block, and for the nine after it: nobody leads, and nobody gives a share to one who does.
            Assert.Empty(await BtcWithdrawalSigner.TickAsync(first, pinnedAt));
            Assert.Empty(await BtcWithdrawalSigner.TickAsync(first, pinnedAt + BtcWithdrawalSigner.SettleBlocks - 1));
            var (ok, why) = FrostSigningAuthorization.Authorize(Announce(vault.Uid, vault.Script, request.Hash, pinned, Signers[2].Address));
            Assert.False(ok);
            Assert.Contains("once the block that fixed it has settled", why);
            Assert.True(BtcWithdrawals.IsPinnedTransaction(request.Hash, vault.Uid, pin.Txid).Ok); // it is fixed; it is only not signed yet
            await Apply(pinnedAt + BtcWithdrawalSigner.SettleBlocks - 1);
            Assert.False(FrostSigningAuthorization.Authorize(Announce(vault.Uid, vault.Script, request.Hash, pinned, Signers[2].Address)).Item1);

            // From the tenth block on it is signed as before.
            await Apply(pinnedAt + BtcWithdrawalSigner.SettleBlocks);
            (ok, why) = FrostSigningAuthorization.Authorize(Announce(vault.Uid, vault.Script, request.Hash, pinned, Signers[2].Address));
            Assert.True(ok, why);
            Assert.Equal(new[] { pin.Txid }, await BtcWithdrawalSigner.TickAsync(first, pinnedAt + BtcWithdrawalSigner.SettleBlocks));
            Assert.Single(led);
        }
    }
}
