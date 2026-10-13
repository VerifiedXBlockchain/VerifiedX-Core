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
    /// Bitcoin facts in consensus, part 12: a bridge exit that pays Bitcoin out of vaults the chain watches
    /// (docs/PLAN-vBTC-Bitcoin-Attestation.md, 5.6). The casters send the exit transaction and nothing after it. The
    /// Bitcoin transaction of every vault the exit draws on is fixed in that block (all of them or none), the vaults'
    /// signers sign only those, Bitcoin showing them completes the exit, and an exit that could not be pinned is failed
    /// by rule with its locks restored. Transactions are judged as block validation judges them and applied with
    /// UpdateTreis, under the bridge rules that are live on the network (submitter must be a caster).
    /// </summary>
    [Collection("DbContextSequential")]
    public class BtcAttest12_BridgeExitTests : BtcAttestTestBase
    {
        private const long Checkpoint = 4032;
        private const string BtcDest = "bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4";
        private readonly Btc _btc = new(Checkpoint - 11);
        private readonly V _holder = NewKey();
        private readonly Network? _priorNetwork = Globals.BTCNetwork;
        private readonly long _priorGuard = Globals.BridgeIntraBlockGuardHeight;
        private readonly Func<VaultWithdrawalRow, VaultPin, Task<(bool, string)>> _realSign = BtcWithdrawalSigner.SignAndBroadcast;
        private readonly Func<string, Task<bool?>> _realKnown = BtcWithdrawalSigner.KnownOnBitcoin;

        /// <summary>The caster that sends the exits.</summary>
        private V Caster => Signers[0];

        public BtcAttest12_BridgeExitTests() : base(realMainnetRules: false)
        {
            _btc.MineEmpty(12);
            Globals.BitcoinCheckpointHeightMainnet = Checkpoint;
            Globals.BitcoinCheckpointHeadersMainnet = string.Concat(_btc.HeaderHexes(Checkpoint - 11, Checkpoint));
            Globals.BTCNetwork = Network.Main;
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

        /// <summary>Buries a Bitcoin block and has the validators report it.</summary>
        private async Task Report(long bitcoinHeight)
        {
            _btc.MineEmpty(BtcAttestRules.Depth);
            var meta = BtcAttestStore.GetMeta()!;
            var headers = _btc.HeaderHexes(meta.TipHeight + 1, bitcoinHeight + BtcAttestRules.Depth);
            var report = Assert.Single(BtcBlockScanner.BuildForward(_btc.Block(bitcoinHeight), bitcoinHeight, headers, BtcAttestRules.EpochOf(Tip + 1)));
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

        private VfxTx Transfer(string vault, decimal amount, V? from = null, V? to = null) =>
            SignedBy(from ?? Minter, (to ?? _holder).Address, TransactionType.VBTC_V2_TRANSFER,
                new { Function = "TransferVBTCV2()", ContractUID = vault, FromAddress = (from ?? Minter).Address, ToAddress = (to ?? _holder).Address, Amount = amount });

        private VfxTx Request(string vault, decimal amount, V? from = null) =>
            SignedBy(from ?? Minter, (from ?? Minter).Address, TransactionType.VBTC_V2_WITHDRAWAL_REQUEST,
                new { Function = "VBTCWithdrawalRequest()", ContractUID = vault, BTCAddress = BtcDest, Amount = amount, FeeRate = 10, UniqueId = Guid.NewGuid().ToString("N") });

        /// <summary>A holder locks vBTC of a vault for the bridge; returns the lock's id.</summary>
        private async Task<string> Lock(string vault, decimal amount, V? from = null)
        {
            var lockId = Guid.NewGuid().ToString("N");
            await Mine(SignedBy(from ?? Minter, (from ?? Minter).Address, TransactionType.VBTC_V2_BRIDGE_LOCK,
                new { Function = "VBTCBridgeLock()", ContractUID = vault, LockId = lockId, Amount = amount, AmountSats = (long)(amount * 100_000_000M), EvmDestination = "0x" + new string('a', 40) }));
            Assert.NotNull(VBTCBridgeLockState.GetByLockId(lockId));
            return lockId;
        }

        private static string NewBurn() => "0x" + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

        /// <summary>The exit transaction a caster sends for a burn on Base: which locks pay it, and where the Bitcoin goes.</summary>
        private VfxTx Exit(string burn, string destination, params (string LockId, string Vault, decimal Amount)[] allocations)
        {
            var total = allocations.Sum(a => a.Amount);
            return SignedBy(Caster, Caster.Address, TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC, new
            {
                Function = "VBTCBridgeExitToBTC()", ContractUID = allocations[0].Vault, LockId = allocations[0].LockId,
                Amount = total, AmountSats = (long)(total * 100_000_000M), TotalAmount = total, TotalAmountSats = (long)(total * 100_000_000M),
                BtcDestination = destination, BaseBurnTxHash = burn,
                Allocations = allocations.Select(a => new PoolUnlockAllocation { LockId = a.LockId, SmartContractUID = a.Vault, UnlockAmount = a.Amount }).ToList(),
            }, free: true);
        }

        private VfxTx ExitComplete(string burn, string btcTxid) =>
            SignedBy(Caster, Caster.Address, TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC_COMPLETE,
                new { Function = "VBTCBridgeExitToBTCComplete()", BaseBurnTxHash = burn, BtcTxHash = btcTxid }, free: true);

        private VfxTx ExitFail(string burn, VfxTx exit, params (string LockId, string Vault, decimal Amount)[] allocations) =>
            SignedBy(Caster, Caster.Address, TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC_FAIL, new
            {
                Function = "VBTCBridgeExitToBTCFail()", BaseBurnTxHash = burn, ExitTxHash = exit.Hash, Reason = "test",
                FailedAllocations = allocations.Select(a => new PoolUnlockAllocation { LockId = a.LockId, SmartContractUID = a.Vault, UnlockAmount = a.Amount }).ToList(),
            }, free: true);

        /// <summary>Judged as a block's transaction, then mined in the next block.</summary>
        private async Task<VfxTx> Mine(VfxTx tx)
        {
            var (ok, why) = await Verify(tx, Tip + 1);
            Assert.True(ok, why);
            await Apply(Tip + 1, tx);
            return tx;
        }

        private async Task RefusedInBlock(VfxTx tx, string because, long? height = null)
        {
            var (ok, why) = await Verify(tx, height ?? Tip + 1);
            Assert.False(ok, "accepted in a block");
            Assert.Contains(because, why);
        }

        /// <summary>One vault's part of the exit the chain holds for a burn (its latest attempt).</summary>
        private static VaultWithdrawalRow Part(string burn, string vault) => BtcAttestStore.Withdrawal(BtcWithdrawals.ExitRefOf(Record(burn), vault))!;
        private static VBTCBridgeBtcExitState Record(string burn) => VBTCBridgeBtcExitState.GetByBurnHash(burn)!;

        private async Task<decimal> Spendable(string vault, V who) =>
            (await VBTCService.TryGetAvailableTransparentVbtcBalance(vault, who.Address)).availableBalance;

        /// <summary>What a ceremony leader announces to the signers for a transaction: the bytes, the coins it spends, and every input's sighash.</summary>
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
        public async Task AnExit_IsPinnedInItsOwnBlock_BitcoinCompletesIt_AndTheOwnerIsChargedOnce()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            var deposit = await Deposit(vault.Script, 1_000_000);
            Assert.Equal(0.01M, await Spendable(vault.Uid, Minter));

            // The owner locks 0.004 for the bridge: debited at once, the Bitcoin stays in the vault.
            var lockId = await Lock(vault.Uid, 0.004M);
            Assert.Equal(0.006M, await Spendable(vault.Uid, Minter));

            // That vBTC.b is burned on Base and a caster sends the exit.
            var burn = NewBurn();
            var exit = await Mine(Exit(burn, BtcDest, (lockId, vault.Uid, 0.004M)));
            var part = Part(burn, vault.Uid);
            Assert.True(part.IsBridgeExit);
            Assert.Equal(VaultWithdrawalState.Pinned, part.State);
            Assert.Equal(400_000, part.AmountSats);
            Assert.Equal(BtcWithdrawals.ExitFeeRate, part.FeeRate);
            Assert.Equal(Tip, part.LastModifiedHeight);
            var pin = Assert.Single(part.Pins);
            Assert.Equal(new[] { Outpoint(deposit, 0) }, pin.Inputs);
            Assert.Equal(154 * 10, pin.FeeSats);
            Assert.Equal(400_000 - 1_540, pin.DestinationSats);
            Assert.Equal(600_000, pin.ChangeSats);
            var btc = BtcTx.Parse(pin.TxHex, Network.Main);
            Assert.Equal(BitcoinAddress.Create(BtcDest, Network.Main).ScriptPubKey, btc.Outputs[0].ScriptPubKey);
            Assert.Equal(vault.Script, btc.Outputs[1].ScriptPubKey);

            // The lock is reserved, the exit is open, nothing changed for the owner.
            var reserved = VBTCBridgeLockState.GetByLockId(lockId)!;
            Assert.True(reserved.IsUnlocked);
            Assert.Equal(0.004M, reserved.UnlockedAmount);
            Assert.False(Record(burn).IsComplete);
            Assert.Equal(exit.Hash, Record(burn).ExitTxHash);
            Assert.False(VBTCBridgeConsumedBurn.IsConsumed(burn));
            Assert.Equal(0.006M, await Spendable(vault.Uid, Minter));

            // No caster completes or fails it by hand, and the same burn cannot be exited twice.
            await RefusedInBlock(ExitComplete(burn, pin.Txid), "completed by the chain when Bitcoin shows its transactions");
            await RefusedInBlock(ExitFail(burn, exit, (lockId, vault.Uid, 0.004M)), "failed by the chain");
            await RefusedInBlock(Exit(burn, BtcDest, (lockId, vault.Uid, 0.004M)), "Duplicate Base burn");

            // The signers sign the pinned transaction; Bitcoin confirms it; the validators report it.
            await SignAndConfirm(pin);
            part = Part(burn, vault.Uid);
            Assert.Equal(VaultWithdrawalState.Settled, part.State);
            Assert.Equal(pin.Txid, part.SettledTxid);
            var done = Record(burn);
            Assert.True(done.IsComplete);
            Assert.False(done.IsFailedRecord);
            Assert.Equal(pin.Txid, done.BtcTxHash);
            Assert.True(VBTCBridgeConsumedBurn.IsConsumed(burn));
            await RefusedInBlock(Exit(burn, BtcDest, (lockId, vault.Uid, 0.004M)), "Duplicate Base burn");

            // The vault holds 0.006 and all of it is the owner's: the 0.004 was debited once, at the lock.
            Assert.Equal(600_000, BtcAttestStore.Total(vault.Uid).UnspentSats);
            Assert.Equal(0, BtcAttestStore.Total(vault.Uid).PinnedSats);
            Assert.Equal(400_000, BtcAttestStore.Total(vault.Uid).SettledExitSats);
            Assert.Equal(0.006M, await Spendable(vault.Uid, Minter));
            await RefusedInBlock(Transfer(vault.Uid, 0.00600001M), "Insufficient vBTC balance");
            await Mine(Transfer(vault.Uid, 0.006M));
            Assert.Equal(0M, await Spendable(vault.Uid, Minter));
        }

        [Fact]
        public async Task AHoldersLock_PaidOutByAnExit_LeavesTheOwnerWhatTheVaultStillHolds()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            await Deposit(vault.Script, 1_000_000);
            await Mine(Transfer(vault.Uid, 0.004M));                    // the owner pays a holder
            var lockId = await Lock(vault.Uid, 0.004M, _holder);         // who takes it to Base
            Assert.Equal(0.006M, await Spendable(vault.Uid, Minter));
            Assert.Equal(0M, await Spendable(vault.Uid, _holder));

            var burn = NewBurn();
            var exit = await Mine(Exit(burn, BtcDest, (lockId, vault.Uid, 0.004M)));
            await SignAndConfirm(Part(burn, vault.Uid).Pins[0]);
            Assert.True(Record(burn).IsComplete);
            Assert.Equal(0.006M, BtcAttestBalance.DepositBtc(vault.Uid));
            Assert.Equal(0.006M, await Spendable(vault.Uid, Minter));
            Assert.Equal(0M, await Spendable(vault.Uid, _holder));
        }

        // ── Several vaults ──────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task AnExitOnTwoVaults_IsPinnedOnBothInOneBlock_OrOnNeither()
        {
            await Start();
            var a = await CreateVault(H1 + 1);
            var b = await CreateVault(H1 + 2);
            await Deposit(a.Script, 1_000_000);
            await Deposit(b.Script, 1_000_000);
            var lockA = await Lock(a.Uid, 0.003M);
            var lockB = await Lock(b.Uid, 0.002M);

            // Vault B's only coin is held by a withdrawal that Bitcoin has not confirmed yet.
            var withdrawal = await Mine(Request(b.Uid, 0.001M));
            var withdrawalRow = BtcAttestStore.Withdrawal(BtcWithdrawals.RefOf(withdrawal.Hash, b.Uid))!;
            Assert.Equal(VaultWithdrawalState.Pinned, withdrawalRow.State);

            var burn = NewBurn();
            var exit = await Mine(Exit(burn, BtcDest, (lockA, a.Uid, 0.003M), (lockB, b.Uid, 0.002M)));
            // Vault A could pay its part now. It is not pinned: an exit is paid in full or failed in full.
            Assert.Equal(VaultWithdrawalState.Waiting, Part(burn, a.Uid).State);
            Assert.Equal(VaultWithdrawalState.Waiting, Part(burn, b.Uid).State);
            Assert.Contains("other vaults cannot pay", Part(burn, a.Uid).WaitingReason);
            Assert.Contains("free coins do not cover", Part(burn, b.Uid).WaitingReason);
            Assert.Equal(VaultCoinStatus.Unspent, Assert.Single(BtcAttestStore.CoinsOf(a.Uid)).Status);

            // The withdrawal confirms; its change is a free coin on vault B; the exit is pinned on both in that block.
            await SignAndConfirm(withdrawalRow.Pins[0]);
            var partA = Part(burn, a.Uid);
            var partB = Part(burn, b.Uid);
            Assert.Equal(VaultWithdrawalState.Pinned, partA.State);
            Assert.Equal(VaultWithdrawalState.Pinned, partB.State);
            Assert.Equal(Tip, partA.Pins[0].PinnedAtVfxHeight);
            Assert.Equal(Tip, partB.Pins[0].PinnedAtVfxHeight);
            Assert.Equal(300_000 - 1_540, partA.Pins[0].DestinationSats);
            Assert.Equal(200_000 - 1_540, partB.Pins[0].DestinationSats);

            // One vault's transaction confirms: the burn is used up, the exit is not complete yet.
            await SignAndConfirm(partA.Pins[0]);
            Assert.Equal(VaultWithdrawalState.Settled, Part(burn, a.Uid).State);
            Assert.False(Record(burn).IsComplete);
            Assert.True(VBTCBridgeConsumedBurn.IsConsumed(burn));

            await SignAndConfirm(partB.Pins[0]);
            var done = Record(burn);
            Assert.True(done.IsComplete);
            Assert.False(done.IsFailedRecord);
            var inVaultOrder = new[] { (a.Uid, partA.Pins[0].Txid), (b.Uid, partB.Pins[0].Txid) }.OrderBy(p => p.Item1, StringComparer.Ordinal).Select(p => p.Item2);
            Assert.Equal(string.Join(",", inVaultOrder), done.BtcTxHash);

            // Each owner figure is what its vault still holds.
            Assert.Equal(0.007M, await Spendable(a.Uid, Minter));
            Assert.Equal(0.007M, await Spendable(b.Uid, Minter)); // 0.01 - 0.002 locked - 0.001 withdrawn
            Assert.Equal(0.007M, BtcAttestBalance.DepositBtc(a.Uid));
            Assert.Equal(0.007M, BtcAttestBalance.DepositBtc(b.Uid));
        }

        // ── Failure by rule ─────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task AnExitThatCannotBePinned_IsFailedByRule_ItsLocksComeBack_AndItCanBeMadeAgain()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            await Deposit(vault.Script, 1_000_000);
            var lockId = await Lock(vault.Uid, 0.004M);
            var withdrawal = await Mine(Request(vault.Uid, 0.001M));   // holds the vault's only coin
            var withdrawalRow = BtcAttestStore.Withdrawal(BtcWithdrawals.RefOf(withdrawal.Hash, vault.Uid))!;

            var burn = NewBurn();
            var exit = await Mine(Exit(burn, BtcDest, (lockId, vault.Uid, 0.004M)));
            var mined = Tip;
            Assert.Equal(VaultWithdrawalState.Waiting, Part(burn, vault.Uid).State);
            Assert.True(VBTCBridgeLockState.GetByLockId(lockId)!.IsUnlocked);

            // No caster can fail it, and no signer will sign anything for it.
            await RefusedInBlock(ExitFail(burn, exit, (lockId, vault.Uid, 0.004M)), "failed by the chain");
            var anything = Pay(new[] { new OutPoint(uint256.Parse(withdrawalRow.Pins[0].Inputs[0].Split(':')[0]), 0) },
                (BitcoinAddress.Create(BtcDest, Network.Main).ScriptPubKey, 398_460), (vault.Script, 600_000));
            var (signs, why) = FrostSigningAuthorization.Authorize(
                Announce(vault.Uid, vault.Script, BtcWithdrawals.ExitSigningReference(burn, vault.Uid), anything, Signers[1].Address));
            Assert.False(signs);
            Assert.Contains("No Bitcoin transaction is fixed in state", why);

            await Apply(mined + VBTCWithdrawalRequest.EXPIRY_BLOCKS);
            Assert.Equal(VaultWithdrawalState.Waiting, Part(burn, vault.Uid).State); // the last block of its window
            Assert.False(Record(burn).IsComplete);
            await Apply(mined + VBTCWithdrawalRequest.EXPIRY_BLOCKS + 1);

            var firstAttempt = BtcWithdrawals.ExitRefOf(Record(burn), vault.Uid);
            var expired = Part(burn, vault.Uid);
            Assert.Equal(VaultWithdrawalState.Expired, expired.State);
            Assert.Equal(Tip, expired.ClosedAtVfxHeight);
            var failed = Record(burn);
            Assert.True(failed.IsFailedRecord);
            Assert.StartsWith("FAILED:expired@", failed.BtcTxHash);
            var restored = VBTCBridgeLockState.GetByLockId(lockId)!;
            Assert.False(restored.IsUnlocked);
            Assert.False(restored.IsBlacklisted);
            Assert.Equal(0M, restored.UnlockedAmount);
            Assert.Equal(0.004M, restored.RemainingAmount);
            Assert.False(VBTCBridgeConsumedBurn.IsBurnUsedAnywhere(burn));
            Assert.Equal(0, BtcAttestStore.Total(vault.Uid).SettledExitSats);

            // The withdrawal confirms; the casters make the exit again from the restored lock; this time it is paid.
            await SignAndConfirm(withdrawalRow.Pins[0]);
            Assert.Equal(VaultWithdrawalState.Expired, Part(burn, vault.Uid).State);
            // Even the very same transaction, mined again, is a new attempt with parts of its own.
            await Mine(exit);
            Assert.Equal(Tip, Record(burn).ExitVfxHeight);
            Assert.NotEqual(firstAttempt, BtcWithdrawals.ExitRefOf(Record(burn), vault.Uid));
            Assert.Equal(VaultWithdrawalState.Expired, BtcAttestStore.Withdrawal(firstAttempt)!.State); // the failed attempt stays closed
            var part = Part(burn, vault.Uid);
            Assert.Equal(VaultWithdrawalState.Pinned, part.State);
            await SignAndConfirm(part.Pins[0]);
            Assert.True(Record(burn).IsComplete);
            Assert.False(Record(burn).IsFailedRecord);
            Assert.Equal(0.005M, await Spendable(vault.Uid, Minter)); // 0.01 - 0.004 locked - 0.001 withdrawn
            Assert.Equal(0.005M, BtcAttestBalance.DepositBtc(vault.Uid));
        }

        [Fact]
        public async Task APinnedExitCoinSpentByAnotherTransaction_VoidsThePart_AndTheExitCanBeMadeFromOtherLocks()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            var other = await CreateVault(H1 + 2);
            var deposit = await Deposit(vault.Script, 1_000_000);
            await Deposit(other.Script, 1_000_000);
            var lockId = await Lock(vault.Uid, 0.004M);
            var otherLock = await Lock(other.Uid, 0.004M);
            var burn = NewBurn();
            await Mine(Exit(burn, BtcDest, (lockId, vault.Uid, 0.004M)));
            var attempt = BtcWithdrawals.ExitRefOf(Record(burn), vault.Uid);
            Assert.Equal(VaultWithdrawalState.Pinned, Part(burn, vault.Uid).State);

            // The coin leaves in a transaction that is not the pinned one (signers that signed something else).
            await Report(_btc.Mine(Pay(new[] { new OutPoint(deposit, 0) }, (Elsewhere(), 20_000), (vault.Script, 979_000))));

            var part = BtcAttestStore.Withdrawal(attempt)!;
            Assert.Equal(VaultWithdrawalState.Void, part.State);
            Assert.True(part.Pins[0].Dead);
            Assert.StartsWith("FAILED:void@", Record(burn).BtcTxHash);
            var restored = VBTCBridgeLockState.GetByLockId(lockId)!;
            Assert.Equal(0M, restored.UnlockedAmount);
            Assert.True(restored.IsBlacklisted);          // this vault's signers did what they must never do
            Assert.False(VBTCBridgeConsumedBurn.IsBurnUsedAnywhere(burn));
            Assert.Equal(0, BtcAttestStore.Total(vault.Uid).SettledExitSats);

            // The burn is still owed; another vault's lock pays it.
            Assert.Equal(otherLock, Assert.Single(BridgePoolUnlockService.ComputeExitAllocationPlan(0.004M, null, Tip + 1)!).LockId);
            await Mine(Exit(burn, BtcDest, (otherLock, other.Uid, 0.004M)));
            await SignAndConfirm(Part(burn, other.Uid).Pins[0]);
            Assert.True(Record(burn).IsComplete);
            Assert.False(Record(burn).IsFailedRecord);
            Assert.True(VBTCBridgeConsumedBurn.IsConsumed(burn));
        }

        // ── What an exit may draw on ────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task AnExit_DrawsOnWatchedVaultsOrOlderOnes_NeverBoth_AndNeverOnAPartTooSmallToPay()
        {
            var older = await CreateVault(H1 - 50);
            await Start();
            var watched = await CreateVault(H1 + 1);
            await Deposit(watched.Script, 1_000_000);
            var olderLock = await Lock(older.Uid, 0.002M);
            var watchedLock = await Lock(watched.Uid, 0.003M);

            await RefusedInBlock(Exit(NewBurn(), BtcDest, (olderLock, older.Uid, 0.001M), (watchedLock, watched.Uid, 0.001M)), "not on both");
            // The lock's own record says which vault it belongs to, whatever the transaction claims.
            await RefusedInBlock(Exit(NewBurn(), BtcDest, (olderLock, watched.Uid, 0.001M), (watchedLock, watched.Uid, 0.001M)), "not on both");
            await RefusedInBlock(Exit(NewBurn(), BtcDest, (watchedLock, watched.Uid, 0.00002085M)), "at least 2086 sats");
            await RefusedInBlock(Exit(NewBurn(), "tb1qw508d6qejxtdg4y5r3zarvary0c5xw7kxpjzsx", (watchedLock, watched.Uid, 0.001M)), "not an address on this network");
            // To the address of the vault that would pay it: the payment could not be told from the change, and no signer would sign it.
            await RefusedInBlock(Exit(NewBurn(), watched.Address, (watchedLock, watched.Uid, 0.001M)), "is the deposit address of vault");

            // The smallest part a vault can pay is accepted and pinned: its fee, and an output of exactly the dust limit.
            var smallBurn = NewBurn();
            await Mine(Exit(smallBurn, BtcDest, (watchedLock, watched.Uid, 0.00002086M)));
            var pin = Assert.Single(Part(smallBurn, watched.Uid).Pins);
            Assert.Equal(546, pin.DestinationSats);

            // An exit on older vaults only is not touched by these rules: it is paid the earlier way, and nothing is pinned for it.
            var burn = NewBurn();
            var old = await Mine(Exit(burn, BtcDest, (olderLock, older.Uid, 0.001M)));
            Assert.Null(BtcAttestStore.Withdrawal(BtcWithdrawals.ExitRefOf(Record(burn), older.Uid)));
            Assert.Empty(BtcAttestStore.ExitParts(old.Hash, Tip));
            Assert.Null(BtcWithdrawals.ExitRefusal(ExitComplete(burn, new string('b', 64))));
            Assert.True((await Verify(ExitComplete(burn, new string('b', 64)), Tip + 1)).Ok);
        }

        [Fact]
        public async Task TheCastersPlan_UsesOlderVaultsFirst_ThenWatchedOnes_AndOnlyWatchedOnesOnceOldVaultsAreFrozen()
        {
            const long h2 = H1 + 300;
            var older = await CreateVault(H1 - 50);
            await Start();
            var watched = await CreateVault(H1 + 1);
            await Deposit(watched.Script, 1_000_000);
            var watchedLock = await Lock(watched.Uid, 0.003M);
            var olderLock = await Lock(older.Uid, 0.002M);
            Globals.BitcoinAttestationHeight = h2;                // restored by the base class

            // Before the enforcement height: older vaults pay while they can, so that they empty.
            var plan = BridgePoolUnlockService.ComputeExitAllocationPlan(0.001M, null, h2 - 1)!;
            Assert.Equal(olderLock, Assert.Single(plan).LockId);
            // More than the older vaults hold: watched vaults pay all of it. The two are never combined.
            plan = BridgePoolUnlockService.ComputeExitAllocationPlan(0.0025M, null, h2 - 1)!;
            Assert.Equal(watchedLock, Assert.Single(plan).LockId);
            Assert.Equal(0.0025M, plan[0].UnlockAmount);
            Assert.Null(BridgePoolUnlockService.ComputeExitAllocationPlan(0.004M, null, h2 - 1));
            // A part no Bitcoin transaction could pay is not planned.
            Assert.Null(BridgePoolUnlockService.ComputeExitAllocationPlan(0.00002M, new HashSet<string> { older.Uid }, h2 - 1));

            // From the enforcement height: watched vaults only, and consensus refuses an exit on a frozen one.
            plan = BridgePoolUnlockService.ComputeExitAllocationPlan(0.001M, null, h2)!;
            Assert.Equal(watchedLock, Assert.Single(plan).LockId);
            var onFrozen = Exit(NewBurn(), BtcDest, (olderLock, older.Uid, 0.001M));
            Assert.True((await Verify(onFrozen, h2 - 1)).Ok);
            await RefusedInBlock(onFrozen, "has been frozen since block " + h2, h2);

            // An unlock back to VFX is planned and judged the same way: it would hand out vBTC on a frozen vault.
            // (Both locks, in whichever order the pool holds them: they were mined in the same second.)
            Assert.Equal(new[] { watchedLock, olderLock }.OrderBy(l => l, StringComparer.Ordinal),
                BridgePoolUnlockService.ComputeUnlockAllocationPlan(0.005M, h2 - 1)!.Select(a => a.LockId).OrderBy(l => l, StringComparer.Ordinal));
            Assert.Null(BridgePoolUnlockService.ComputeUnlockAllocationPlan(0.004M, h2));
            Assert.Equal(watchedLock, Assert.Single(BridgePoolUnlockService.ComputeUnlockAllocationPlan(0.003M, h2)!).LockId);
            VfxTx Unlock(params (string LockId, string Vault, decimal Amount)[] allocations) => SignedBy(Caster, Caster.Address, TransactionType.VBTC_V2_BRIDGE_POOL_UNLOCK, new
            {
                Function = "VBTCBridgePoolUnlock()", ExitBurnTxHash = NewBurn(), VfxDestinationAddress = _holder.Address,
                TotalAmount = allocations.Sum(a => a.Amount), TotalAmountSats = (long)(allocations.Sum(a => a.Amount) * 100_000_000M),
                Allocations = allocations.Select(a => new PoolUnlockAllocation { LockId = a.LockId, SmartContractUID = a.Vault, UnlockAmount = a.Amount }).ToList(),
            }, free: true);
            Assert.Null(BtcLegacyFreeze.Refusal(Unlock((olderLock, older.Uid, 0.001M)), h2 - 1));
            Assert.Contains("has been frozen since block " + h2, BtcLegacyFreeze.Refusal(Unlock((olderLock, older.Uid, 0.001M)), h2));
            Assert.Contains("has been frozen since block " + h2, BtcLegacyFreeze.Refusal(Unlock((watchedLock, watched.Uid, 0.001M), (olderLock, watched.Uid, 0.001M)), h2));
            Assert.Null(BtcLegacyFreeze.Refusal(Unlock((watchedLock, watched.Uid, 0.001M)), h2));
        }

        // ── Signing ─────────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task TheVaultsSigners_SignThePinnedExitTransaction_AndNoOther_WithoutACaster()
        {
            await Start();
            var vault = await CreateVault(H1 + 1, keyHolders: Signers);
            await Deposit(vault.Script, 1_000_000);
            var lockId = await Lock(vault.Uid, 0.004M);
            var burn = NewBurn();
            var exit = await Mine(Exit(burn, BtcDest, (lockId, vault.Uid, 0.004M)));
            var part = Part(burn, vault.Uid);
            var pin = Assert.Single(part.Pins);
            var pinned = BtcTx.Parse(pin.TxHex, Network.Main);
            var reference = BtcWithdrawals.ExitSigningReference(burn, vault.Uid);

            // Any signer of the vault may lead: the transaction is the same whoever does. Somebody who is neither a
            // signer of the vault nor a caster may not.
            var (ok, why) = FrostSigningAuthorization.Authorize(Announce(vault.Uid, vault.Script, reference, pinned, Signers[2].Address));
            Assert.True(ok, why);
            (ok, why) = FrostSigningAuthorization.Authorize(Announce(vault.Uid, vault.Script, reference, pinned, Unfunded.Address));
            Assert.False(ok);
            Assert.Contains("led by a committee caster", why);

            // A caster leading a transaction of its own making: right destination, within the vault's share, change to
            // the vault. Every earlier rule passes it. It is not the pinned one.
            var another = BtcTx.Parse(pin.TxHex, Network.Main);
            another.Outputs[0].Value -= Money.Satoshis(20_000);
            (ok, why) = FrostSigningAuthorization.Authorize(Announce(vault.Uid, vault.Script, reference, another, Caster.Address));
            Assert.False(ok, "a signer would have signed an exit transaction that is not the pinned one");
            Assert.Contains("Only the Bitcoin transaction fixed in state", why);

            // The signer loop takes the exit's part as it takes a withdrawal.
            var led = new List<(VaultWithdrawalRow Row, VaultPin Pin)>();
            BtcWithdrawalSigner.SignAndBroadcast = (row, p) => { led.Add((row, p)); return Task.FromResult((true, p.Txid)); };
            BtcWithdrawalSigner.KnownOnBitcoin = _ => Task.FromResult<bool?>(false);
            var txids = await BtcWithdrawalSigner.TickAsync(Signers[1].Address, pin.PinnedAtVfxHeight + 4 * BtcWithdrawalSigner.TurnBlocks);
            Assert.Equal(new[] { pin.Txid }, txids);
            var (ledRow, ledPin) = Assert.Single(led);
            Assert.True(ledRow.IsBridgeExit);
            Assert.Equal(burn, ledRow.ExitBurnHash);
            Assert.Equal(pin.Txid, ledPin.Txid);

            // Once Bitcoin shows it, nothing more is signed for this exit.
            await SignAndConfirm(pin);
            (ok, why) = FrostSigningAuthorization.Authorize(Announce(vault.Uid, vault.Script, reference, pinned, Signers[2].Address));
            Assert.False(ok);
            Assert.Contains("already completed", why);
        }

        /// <summary>
        /// An exit has no requester on this chain who could raise its Bitcoin fee, and a pinned part cannot be given
        /// up. A part fixed at 10 sat/vB while Bitcoin asks for more would hold its vault's coins and its locks for as
        /// long as that lasted. So the chain raises the fee itself, by rule: after 2,160 blocks unconfirmed, a
        /// replacement at twice the rate on the same coins, the fee out of the amount paid.
        /// </summary>
        [Fact]
        public async Task AnExitPartThatDoesNotConfirm_HasItsFeeRaisedByRule_NobodySendsAnything()
        {
            await Start();
            var vault = await CreateVault(H1 + 1, keyHolders: Signers);
            await Deposit(vault.Script, 1_000_000);
            var lockId = await Lock(vault.Uid, 0.004M);
            var burn = NewBurn();
            await Mine(Exit(burn, BtcDest, (lockId, vault.Uid, 0.004M)));
            var first = Assert.Single(Part(burn, vault.Uid).Pins);
            Assert.Equal(10, first.FeeRate);
            Assert.Equal(400_000 - 1_540, first.DestinationSats);

            // Not before 2,160 blocks, and then at the next block the rule looks (every thirtieth).
            long Looks(long from) => from % BtcAttestRules.TakeRetryBlocks == 0 ? from : from + BtcAttestRules.TakeRetryBlocks - from % BtcAttestRules.TakeRetryBlocks;
            var due = Looks(first.PinnedAtVfxHeight + BtcAttestRules.StuckPinBlocks);
            await AdvanceTo(due - BtcAttestRules.TakeRetryBlocks);
            Assert.Single(Part(burn, vault.Uid).Pins);
            await AdvanceTo(due);
            var part = Part(burn, vault.Uid);
            Assert.Equal(VaultWithdrawalState.Pinned, part.State);
            Assert.Equal(2, part.Pins.Count);
            Assert.True(part.Pins[0].Superseded);
            var second = part.Pins[1];
            Assert.Equal(20, second.FeeRate);
            Assert.Equal(154 * 20, second.FeeSats);
            Assert.Equal(400_000 - 154 * 20, second.DestinationSats);   // the larger fee comes out of what the exit pays
            Assert.Equal(first.ChangeSats, second.ChangeSats);           // the vault gets back exactly what it would have
            Assert.Equal(first.Inputs, second.Inputs);
            Assert.Equal(due, second.PinnedAtVfxHeight);

            // The signers sign the replacement (and the first, should a miner take that one), and nothing else.
            var replacement = BtcTx.Parse(second.TxHex, Network.Main);
            var reference = BtcWithdrawals.ExitSigningReference(burn, vault.Uid);
            var (signs, why) = FrostSigningAuthorization.Authorize(Announce(vault.Uid, vault.Script, reference, replacement, Signers[2].Address));
            Assert.True(signs, why);
            Assert.True(FrostSigningAuthorization.Authorize(Announce(vault.Uid, vault.Script, reference, BtcTx.Parse(first.TxHex, Network.Main), Signers[2].Address)).Item1);

            // Still nothing: twice again, 2,160 blocks later.
            var again = Looks(second.PinnedAtVfxHeight + BtcAttestRules.StuckPinBlocks);
            await AdvanceTo(again);
            Assert.Equal(40, Part(burn, vault.Uid).Pins[2].FeeRate);

            // Bitcoin takes the second one. The exit is complete, and the owner is added back the amount, whatever the fee was.
            await SignAndConfirm(second);
            Assert.Equal(VaultWithdrawalState.Settled, Part(burn, vault.Uid).State);
            Assert.Equal(second.Txid, Part(burn, vault.Uid).SettledTxid);
            Assert.True(Record(burn).IsComplete);
            Assert.False(Record(burn).IsFailedRecord);
            Assert.Equal(400_000, BtcAttestStore.Total(vault.Uid).SettledExitSats);
            Assert.Equal(600_000, BtcAttestStore.Total(vault.Uid).HeldSats);

            // A part too small to bear the next fee keeps the transaction it has.
            var smallLock = await Lock(vault.Uid, 0.00003M);
            var smallBurn = NewBurn();
            await Mine(Exit(smallBurn, BtcDest, (smallLock, vault.Uid, 0.00003M)));        // 3,000 sats: 1,540 of fee, 1,460 paid
            var small = Assert.Single(Part(smallBurn, vault.Uid).Pins);
            await AdvanceTo(Looks(small.PinnedAtVfxHeight + BtcAttestRules.StuckPinBlocks));
            Assert.Single(Part(smallBurn, vault.Uid).Pins);                                  // 3,080 of fee would leave nothing to pay
        }
    }
}
