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
using VerifiedXCore.Utilities;
using Xunit;
using Xunit.Abstractions;
using BtcTx = NBitcoin.Transaction;
using VfxTx = VerifiedXCore.Models.Transaction;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Bitcoin facts in consensus, part 16: the accounting identity (docs/PLAN-vBTC-Bitcoin-Attestation.md, 5.4 and
    /// test 6 of section 11). A random sequence of everything that can happen to a watched vault is driven through
    /// VerifyTX and UpdateTreis: deposits, transfers between several holders, withdrawal requests, fee bumps,
    /// confirmations of either transaction of a request, cancellations, expiries, bridge locks, bridge exits and their
    /// confirmations, a pinned coin spent by a transaction nobody pinned, and Bitcoin reorganisations that drop blocks
    /// whose events were already applied (a rewind, then the new branch reported again). After every step:
    ///
    ///   the vault's Bitcoin as the chain records it
    ///     = the coins really unspent on the Bitcoin chain
    ///     = what the owner can spend + what every holder can spend + what is escrowed in open withdrawals
    ///       + what is locked for the bridge and not yet paid out.
    ///
    /// Nothing is created, nothing is counted twice, and nobody but the owner absorbs a loss.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BtcAttest16_AccountingIdentityTests : BtcAttestTestBase
    {
        private const long Checkpoint = 4032;
        private const string BtcDest = "bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4";
        private const decimal Sat = 0.00000001M;
        private readonly ITestOutputHelper _out;
        private Btc _btc = new(Checkpoint - 11);
        private readonly List<V> _holders = Enumerable.Range(0, 3).Select(_ => NewKey()).ToList();
        private readonly long _priorGuard = Globals.BridgeIntraBlockGuardHeight;

        // The vault under test and what the Bitcoin chain really holds for it.
        private (string Uid, string Address, NBitcoin.Script Script) _vault;
        private readonly Dictionary<string, long> _onBitcoin = new(StringComparer.Ordinal);
        private readonly List<string> _locks = new();
        private readonly List<string> _log = new();
        private readonly Dictionary<string, int> _done = new();
        private long _lostToForeignSpends;
        /// <summary>Deposits a reorganisation dropped that have not confirmed again yet (they do, in the end).</summary>
        private readonly List<BtcTx> _notYetBack = new();
        private Random _rng = new(1);

        private V Caster => Signers[0];
        private static long Tip => Globals.LastBlock.Height;
        private IEnumerable<V> Everyone => _holders.Prepend(Minter);

        public BtcAttest16_AccountingIdentityTests(ITestOutputHelper output) : base(realMainnetRules: false)
        {
            _out = output;
            _btc.MineEmpty(12);
            Globals.BitcoinCheckpointHeightMainnet = Checkpoint;
            Globals.BitcoinCheckpointHeadersMainnet = string.Concat(_btc.HeaderHexes(Checkpoint - 11, Checkpoint));
            Globals.BridgeIntraBlockGuardHeight = 1;
            Globals.BootstrapCasterAddresses.Add(Caster.Address);
            BtcAttestRules.StuckPinBlocksOverride = 40; // a run stays inside one epoch of 600 blocks
        }

        protected override void SeedAccounts()
        {
            base.SeedAccounts();
            foreach (var h in _holders)
                StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = h.Address, Balance = 100M, Nonce = 0 });
        }

        public override void Dispose()
        {
            Globals.BootstrapCasterAddresses.Remove(Caster.Address);
            Globals.BridgeIntraBlockGuardHeight = _priorGuard;
            base.Dispose();
        }

        // ── Building blocks ─────────────────────────────────────────────────────────────────────────────────

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

        /// <summary>Mines a transaction if block validation accepts it; says whether it did.</summary>
        private async Task<bool> TryMine(VfxTx tx)
        {
            var (ok, _) = await Verify(tx, Tip + 1);
            if (ok) await Apply(Tip + 1, tx);
            return ok;
        }

        /// <summary>Mines Bitcoin transactions in one block, buries it, and has the validators report it.</summary>
        private async Task MineAndReport(params BtcTx[] txs)
        {
            var height = _btc.Mine(txs);
            _btc.MineEmpty(BtcAttestRules.Depth);
            var headers = _btc.HeaderHexes(BtcAttestStore.GetMeta()!.TipHeight + 1, height + BtcAttestRules.Depth);
            var report = Signed(Assert.Single(BtcBlockScanner.BuildForward(_btc.Block(height), height, headers, BtcAttestRules.EpochOf(Tip + 1))));
            var (ok, why) = await Verify(report, Tip + 1);
            Assert.True(ok, why);
            await Apply(Tip + 1, report);

            Recount();
        }

        /// <summary>What Bitcoin itself holds for the vault: read off the chain the validators follow, block by block.</summary>
        private void Recount()
        {
            _onBitcoin.Clear();
            for (var h = Checkpoint - 11; h <= _btc.Tip; h++)
                foreach (var tx in _btc.Block(h).Transactions)
                {
                    if (!tx.IsCoinBase)
                        foreach (var input in tx.Inputs)
                            _onBitcoin.Remove(BtcAttestStore.OutpointKey(input.PrevOut.Hash.ToString(), input.PrevOut.N));
                    for (var i = 0; i < tx.Outputs.Count; i++)
                        if (tx.Outputs[i].ScriptPubKey == _vault.Script)
                            _onBitcoin[BtcAttestStore.OutpointKey(tx.GetHash().ToString(), i)] = tx.Outputs[i].Value.Satoshi;
                }
        }

        /// <summary>Whether Bitcoin could confirm this transaction now: every vault coin it spends is there.</summary>
        private bool Spendable(BtcTx tx) =>
            tx.Inputs.All(i => !(BtcAttestStore.Coin(BtcAttestStore.OutpointKey(i.PrevOut.Hash.ToString(), i.PrevOut.N)) is { } coin)
                               || coin.Status == VaultCoinStatus.Unspent || coin.Status == VaultCoinStatus.Pinned);

        private async Task<decimal> Spendable(V who) =>
            (await VBTCService.TryGetAvailableTransparentVbtcBalance(_vault.Uid, who.Address)).availableBalance;

        private List<VaultWithdrawalRow> Rows(VaultWithdrawalState state) =>
            BtcAttestStore.WithdrawalsOf(_vault.Uid, state).OrderBy(r => r.RequestVfxHeight).ThenBy(r => r.Ref, StringComparer.Ordinal).ToList();

        private T Pick<T>(IReadOnlyList<T> from) => from[_rng.Next(from.Count)];
        private V KeyOf(string address) => Everyone.First(v => v.Address == address);
        private static decimal Btc8(long sats) => sats * Sat;
        private void Did(string what, string detail) { _done[what] = _done.GetValueOrDefault(what) + 1; _log.Add($"{Tip}: {what} {detail}"); }

        // ── What can happen to a vault ──────────────────────────────────────────────────────────────────────

        private async Task Deposit()
        {
            var sats = _rng.Next(30_000, 400_000);
            await MineAndReport(Pay(_vault.Script, sats));
            Did("deposit", $"{sats} sats");
        }

        private async Task Transfer()
        {
            var from = Pick(Everyone.ToList());
            var have = (long)(await Spendable(from) / Sat);
            if (have < 2_000) return;
            var to = Pick(Everyone.Where(v => v != from).ToList());
            var amount = Btc8(_rng.Next(1_000, (int)Math.Min(have, 250_000) + 1));
            if (await TryMine(SignedBy(from, to.Address, TransactionType.VBTC_V2_TRANSFER,
                    new { Function = "TransferVBTCV2()", ContractUID = _vault.Uid, FromAddress = from.Address, ToAddress = to.Address, Amount = amount })))
                Did("transfer", $"{amount} {(from == Minter ? "owner" : "holder")} -> {(to == Minter ? "owner" : "holder")}");
        }

        private async Task Request()
        {
            var from = Pick(Everyone.ToList());
            var have = (long)(await Spendable(from) / Sat);
            if (have < 12_000) return;
            var amount = Btc8(_rng.Next(10_000, (int)Math.Min(have, 200_000) + 1));
            var feeRate = _rng.Next(2, 30);
            var shared = SharedCoins();
            if (await TryMine(SignedBy(from, from.Address, TransactionType.VBTC_V2_WITHDRAWAL_REQUEST,
                    new { Function = "VBTCWithdrawalRequest()", ContractUID = _vault.Uid, BTCAddress = BtcDest, Amount = amount, FeeRate = feeRate, UniqueId = Guid.NewGuid().ToString("N") })))
                Did(SharedCoins() > shared ? "takeover" : "request", $"{amount} at {feeRate} sat/vB");
        }

        private async Task FeeBump()
        {
            var pinned = Rows(VaultWithdrawalState.Pinned).Where(r => !r.IsBridgeExit && r.Pins.Count < BtcWithdrawals.MaxPins).ToList();
            if (pinned.Count == 0) return;
            var row = Pick(pinned);
            var rate = row.Pins.Last(p => !p.Dead).FeeRate + _rng.Next(1, 25);
            if (await TryMine(SignedBy(KeyOf(row.Requester), row.Requester, TransactionType.VBTC_V2_COIN_PIN,
                    new { Function = BtcWithdrawals.FeeBumpFunction, ContractUID = _vault.Uid, WithdrawalRequestHash = row.RequestHash, FeeRate = rate })))
                Did("fee bump", $"to {rate} sat/vB ({row.Pins.Count + 1} transactions)");
        }

        /// <summary>A requester gives a pinned withdrawal up: its coins are to go back to the vault.</summary>
        private async Task Release()
        {
            var pinned = Rows(VaultWithdrawalState.Pinned).Where(r => !r.IsBridgeExit && r.Pins.Count < BtcWithdrawals.MaxPins).ToList();
            if (pinned.Count == 0) return;
            var row = Pick(pinned);
            var rate = row.Pins.Last(p => !p.Dead).FeeRate + _rng.Next(1, 12);
            if (await TryMine(SignedBy(KeyOf(row.Requester), row.Requester, TransactionType.VBTC_V2_COIN_PIN,
                    new { Function = BtcWithdrawals.ReleaseFunction, ContractUID = _vault.Uid, WithdrawalRequestHash = row.RequestHash, FeeRate = rate })))
                Did("release", $"at {rate} sat/vB ({row.Pins.Count + 1} transactions)");
        }

        /// <summary>
        /// Blocks pass with nothing in them. Pinned withdrawals grow old, and one that has stopped moving no longer
        /// keeps its coins from a waiting withdrawal that pays more.
        /// </summary>
        private async Task TimePasses()
        {
            // (The waiting withdrawals are tried against stopped ones on every thirtieth block; this run does not apply every block.)
            var to = (Tip + _rng.Next(10, 70)) / BtcAttestRules.TakeRetryBlocks * BtcAttestRules.TakeRetryBlocks + BtcAttestRules.TakeRetryBlocks;
            if (to >= H1 + BtcAttestRules.EpochBlocks - 20) return;
            var shared = SharedCoins();
            await Apply(to);
            if (SharedCoins() > shared) Did("takeover", "a waiting withdrawal was fixed on coins of one that stopped moving");
            else Did("time passes", $"to block {to}");
        }

        private int SharedCoins() => BtcAttestStore.CoinsOf(_vault.Uid).Count(c => c.Status == VaultCoinStatus.Pinned && c.AlsoPinnedTo is { Count: > 0 });

        /// <summary>Bitcoin confirms one of the transactions fixed for a withdrawal or an exit part: not always the newest.</summary>
        private async Task Confirm()
        {
            // (After a reorganisation a pinned transaction can be waiting for a coin that is not back yet.)
            var pinned = Rows(VaultWithdrawalState.Pinned).Where(r => r.Pins.Any(p => !p.Dead && Spendable(BtcTx.Parse(p.TxHex, Network.Main)))).ToList();
            if (pinned.Count == 0) return;
            var row = Pick(pinned);
            var pin = Pick(row.Pins.Where(p => !p.Dead).ToList());
            var others = BtcWithdrawals.HoldersOf(BtcAttestStore.Coin(pin.Inputs[0])!, null).Where(h => h.Ref != row.Ref).ToList();
            await MineAndReport(BtcTx.Parse(pin.TxHex, Network.Main));
            // A withdrawal that held one of the same coins (a takeover) lost to it, and is refunded.
            foreach (var other in others)
            {
                Assert.Equal(VaultWithdrawalState.Void, BtcAttestStore.Withdrawal(other.Ref)!.State);
                Did("takeover decided", $"{other.Ref[..12]} refunded");
            }
            var after = BtcAttestStore.Withdrawal(row.Ref)!;
            Assert.Equal(pin.IsRelease ? VaultWithdrawalState.Released : VaultWithdrawalState.Settled, after.State);
            Assert.Equal(pin.Txid, after.SettledTxid);
            Did(pin.IsRelease ? "withdrawal released" : row.IsBridgeExit ? "exit paid" : row.Pins.Count > 1 ? "withdrawal paid (after a fee bump)" : "withdrawal paid", $"{row.AmountSats} sats by {pin.Txid[..12]}");
        }

        private async Task Cancel()
        {
            var waiting = Rows(VaultWithdrawalState.Waiting).Where(r => !r.IsBridgeExit).ToList();
            if (waiting.Count == 0) return;
            var row = Pick(waiting);
            if (await TryMine(SignedBy(KeyOf(row.Requester), row.Requester, TransactionType.VBTC_V2_WITHDRAWAL_CANCEL,
                    new { Function = "VBTCWithdrawalCancel()", ContractUID = _vault.Uid, WithdrawalRequestHash = row.RequestHash, FailureProof = "" })))
                Did("cancel", $"{row.AmountSats} sats");
        }

        /// <summary>Enough blocks pass for everything still unpinned to run out of its window.</summary>
        private async Task LetWindowsEnd()
        {
            var waiting = Rows(VaultWithdrawalState.Waiting);
            if (waiting.Count == 0 || Tip + VBTCWithdrawalRequest.EXPIRY_BLOCKS + 5 >= H1 + BtcAttestRules.EpochBlocks) return;
            // (A waiting withdrawal that could still take a slower one's coins is kept past its window until it can.)
            var pinned = Rows(VaultWithdrawalState.Pinned);
            var last = waiting.Max(r => Math.Max(r.RequestVfxHeight + VBTCWithdrawalRequest.EXPIRY_BLOCKS, BtcWithdrawals.KeptForTakeoverUntil(r, pinned))) + 1;
            if (last >= H1 + BtcAttestRules.EpochBlocks - 20) return;
            await Apply(last);
            Assert.Empty(Rows(VaultWithdrawalState.Waiting));
            Did("expiry", $"{waiting.Count} unpinned request(s) or exit part(s)");
        }

        private async Task Lock()
        {
            var from = Pick(Everyone.ToList());
            var have = (long)(await Spendable(from) / Sat);
            if (have < 20_000) return;
            var amount = Btc8(_rng.Next(10_000, (int)Math.Min(have, 150_000) + 1));
            var lockId = Guid.NewGuid().ToString("N");
            if (await TryMine(SignedBy(from, from.Address, TransactionType.VBTC_V2_BRIDGE_LOCK,
                    new { Function = "VBTCBridgeLock()", ContractUID = _vault.Uid, LockId = lockId, Amount = amount, AmountSats = (long)(amount / Sat), EvmDestination = "0x" + new string('a', 40) })))
            {
                _locks.Add(lockId);
                Did("bridge lock", $"{amount}");
            }
        }

        private async Task Exit()
        {
            var usable = _locks.Select(VBTCBridgeLockState.GetByLockId).Where(l => l != null && !l.IsUnlocked && !l.IsBlacklisted && l.RemainingAmountSats >= BtcWithdrawals.MinExitPartSats).ToList();
            if (usable.Count == 0) return;
            var theLock = Pick(usable)!;
            var sats = _rng.Next((int)BtcWithdrawals.MinExitPartSats, (int)theLock.RemainingAmountSats + 1);
            var amount = Btc8(sats);
            var burn = "0x" + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            if (await TryMine(SignedBy(Caster, Caster.Address, TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC, new
                {
                    Function = "VBTCBridgeExitToBTC()", ContractUID = _vault.Uid, LockId = theLock.LockId, Amount = amount, AmountSats = (long)sats,
                    TotalAmount = amount, TotalAmountSats = (long)sats, BtcDestination = BtcDest, BaseBurnTxHash = burn,
                    Allocations = new List<PoolUnlockAllocation> { new() { LockId = theLock.LockId, SmartContractUID = _vault.Uid, UnlockAmount = amount } },
                }, free: true)))
                Did("bridge exit", $"{amount} from lock {theLock.LockId[..8]}");
        }

        /// <summary>A pinned coin leaves in a transaction nobody pinned (signers that signed something else): most of it comes back to the vault, some is gone.</summary>
        private async Task ForeignSpend()
        {
            var pinned = Rows(VaultWithdrawalState.Pinned).Where(r => BtcAttestStore.Coin(r.Pins.Last(p => !p.Dead).Inputs[0])!.Status == VaultCoinStatus.Pinned).ToList();
            if (pinned.Count == 0) return;
            var row = Pick(pinned);
            var outpoint = row.Pins.Last(p => !p.Dead).Inputs[0];
            var coin = BtcAttestStore.Coin(outpoint)!;
            var gone = Math.Min(coin.ValueSats, _rng.Next(200, 3_000));
            var split = outpoint.IndexOf(':');
            var spend = new[] { new OutPoint(uint256.Parse(outpoint[..split]), uint.Parse(outpoint[(split + 1)..])) };
            await MineAndReport(coin.ValueSats - gone >= 600
                ? Pay(spend, (_vault.Script, coin.ValueSats - gone), (Elsewhere(), 1))
                : Pay(spend, (Elsewhere(), 1)));
            _lostToForeignSpends += coin.ValueSats - gone >= 600 ? gone : coin.ValueSats;
            Assert.Equal(VaultWithdrawalState.Void, BtcAttestStore.Withdrawal(row.Ref)!.State);
            Did(row.IsBridgeExit ? "exit part void" : "withdrawal void", $"{outpoint[..12]} spent by another transaction");
        }

        /// <summary>
        /// Bitcoin drops the last one to three blocks that held vault transactions, and everything mined on top of
        /// them, for a longer branch. The validators sign a rewind. On the new branch the dropped transactions confirm
        /// again in their old order, each when Bitcoin could confirm it; now and then a deposit does not come back
        /// for a while.
        /// </summary>
        private async Task DeepReorg()
        {
            var meta = BtcAttestStore.GetMeta()!;
            var withVaultTxs = Enumerable.Range(0, (int)(meta.EventsThroughHeight - Checkpoint))
                .Select(i => meta.EventsThroughHeight - i)
                .Where(h => _btc.Block(h).Transactions.Count > 1)
                .Take(_rng.Next(1, 4)).ToList();
            if (withVaultTxs.Count == 0) return;
            var shared = withVaultTxs.Min() - 1;
            if (shared <= Checkpoint) return;
            var dropped = Enumerable.Range((int)shared + 1, (int)(_btc.Tip - shared)).SelectMany(h => _btc.Block(h).Transactions.Skip(1)).ToList();

            var branch = _btc.ForkAfter(shared);
            branch.MineEmpty((int)(meta.TipHeight - shared) + 1);
            var rewind = Signed(BtcBlockScanner.BuildRewind(shared, branch.HeaderHexes(shared + 1, branch.Tip), BtcAttestRules.EpochOf(Tip + 1)));
            var (ok, why) = await Verify(rewind, Tip + 1);
            Assert.True(ok, why);
            await Apply(Tip + 1, rewind);
            _btc = branch;
            Recount();
            _notYetBack.AddRange(dropped.Where(IsDepositFromOutside));
            Did("deep reorg", $"back to {shared}: {dropped.Count} transaction(s) dropped");
            await Check("deep reorg (rewound)");

            foreach (var tx in dropped)
            {
                if (IsDepositFromOutside(tx) && _rng.Next(4) == 0) continue;   // this one does not confirm again for now
                if (!Spendable(tx)) continue;                                   // it needs a coin that is not back
                await MineAndReport(tx);
                _notYetBack.Remove(tx);
                await Check("deep reorg (a dropped transaction confirmed again)");
            }
        }

        private bool IsDepositFromOutside(BtcTx tx) =>
            tx.Outputs.Any(o => o.ScriptPubKey == _vault.Script)
            && tx.Inputs.All(i => BtcAttestStore.Coin(BtcAttestStore.OutpointKey(i.PrevOut.Hash.ToString(), i.PrevOut.N)) == null);

        // ── The identity ────────────────────────────────────────────────────────────────────────────────────

        private async Task Check(string after)
        {
            var total = BtcAttestStore.Total(_vault.Uid);
            var coins = BtcAttestStore.CoinsOf(_vault.Uid);

            // The chain's record of the vault's coins is what Bitcoin holds, coin by coin.
            var recorded = coins.Where(c => c.Status != VaultCoinStatus.Spent && c.Status != VaultCoinStatus.Orphaned).ToDictionary(c => c.Outpoint, c => c.ValueSats, StringComparer.Ordinal);
            Assert.True(recorded.Count == _onBitcoin.Count && recorded.All(c => _onBitcoin.TryGetValue(c.Key, out var v) && v == c.Value),
                Fail(after, $"coins recorded {recorded.Values.Sum()} sats in {recorded.Count}, on Bitcoin {_onBitcoin.Values.Sum()} sats in {_onBitcoin.Count}"));
            Assert.Equal(recorded.Values.Sum(), total.HeldSats);
            Assert.Equal(coins.Where(c => c.Status == VaultCoinStatus.Pinned).Sum(c => c.ValueSats), total.PinnedSats);

            // Every held coin is held for a withdrawal that is still to be paid, and every such withdrawal holds its coins.
            // (After a takeover a coin is held for more than one; every one of them is still to be paid.)
            foreach (var coin in coins.Where(c => c.Status == VaultCoinStatus.Pinned))
                foreach (var holder in BtcWithdrawals.RefsOn(coin))
                    Assert.True(BtcAttestStore.Withdrawal(holder)?.State == VaultWithdrawalState.Pinned, Fail(after, $"coin {coin.Outpoint} is held for {holder}, which is not pinned"));
            foreach (var row in Rows(VaultWithdrawalState.Pinned))
                foreach (var outpoint in row.Pins.Where(p => !p.Dead).SelectMany(p => p.Inputs).Distinct())
                    Assert.True(BtcAttestStore.Coin(outpoint) is { Status: VaultCoinStatus.Pinned or VaultCoinStatus.Orphaned } c && BtcWithdrawals.RefsOn(c).Contains(row.Ref), Fail(after, $"{row.Ref} does not hold its coin {outpoint}"));

            // Claims.
            var owner = await Spendable(Minter);
            decimal holders = 0M;
            foreach (var h in _holders)
            {
                var balance = await Spendable(h);
                Assert.True(balance >= 0M, Fail(after, $"a holder's balance is {balance}"));
                holders += balance;
            }
            var open = Rows(VaultWithdrawalState.Waiting).Concat(Rows(VaultWithdrawalState.Pinned)).ToList();
            var escrowed = Btc8(open.Where(r => !r.IsBridgeExit).Sum(r => r.AmountSats));
            var locked = _locks.Select(VBTCBridgeLockState.GetByLockId).Sum(l => l!.RemainingAmount) + Btc8(open.Where(r => r.IsBridgeExit).Sum(r => r.AmountSats));
            var held = Btc8(total.HeldSats);

            Assert.True(held == owner + holders + escrowed + locked,
                Fail(after, $"vault holds {held}; owner {owner} + holders {holders} + escrowed {escrowed} + locked for the bridge {locked} = {owner + holders + escrowed + locked}"));
            // Only what left in transactions nobody pinned, and deposits Bitcoin dropped after they were spent
            // against, can take the owner below zero, and by no more than that.
            var gone = _lostToForeignSpends + _notYetBack.Sum(tx => tx.Outputs.Where(o => o.ScriptPubKey == _vault.Script).Sum(o => o.Value.Satoshi));
            Assert.True(owner >= -Btc8(gone), Fail(after, $"owner is at {owner} with {gone} sats lost to foreign spends and dropped deposits"));

            // And block validation holds the owner to that figure, to the satoshi.
            if (owner >= Sat)
            {
                var to = _holders[0];
                VfxTx Send(decimal amount) => SignedBy(Minter, to.Address, TransactionType.VBTC_V2_TRANSFER,
                    new { Function = "TransferVBTCV2()", ContractUID = _vault.Uid, FromAddress = Minter.Address, ToAddress = to.Address, Amount = amount });
                Assert.True((await Verify(Send(owner), Tip + 1)).Ok, Fail(after, $"the owner could not send its whole {owner}"));
                Assert.False((await Verify(Send(owner + Sat), Tip + 1)).Ok, Fail(after, $"the owner could send {owner + Sat}, one satoshi more than it has"));
            }
        }

        private string Fail(string after, string what) =>
            $"After '{after}' at block {Tip}: {what}.\nSteps:\n  " + string.Join("\n  ", _log.TakeLast(40));

        // ── The run ─────────────────────────────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        public Task TheVaultsBitcoin_AlwaysEqualsEveryonesClaims(int seed) => Run(seed);

        private async Task Run(int seed)
        {
            _rng = new Random(seed);
            await Start();
            _vault = await CreateVault(H1 + 1);
            await Deposit();
            await Deposit();
            await Check("the first deposits");

            var steps = new (int Weight, string Name, Func<Task> Run)[]
            {
                (5, "deposit", Deposit), (14, "transfer", Transfer), (22, "request", Request), (8, "fee bump", FeeBump), (10, "confirm", Confirm),
                (6, "cancel", Cancel), (4, "expiry", LetWindowsEnd), (9, "lock", Lock), (9, "exit", Exit), (3, "foreign spend", ForeignSpend),
                (4, "deep reorg", DeepReorg), (5, "release", Release), (7, "time passes", TimePasses),
            };
            var weights = steps.Sum(s => s.Weight);
            for (var i = 0; i < 90 && Tip < H1 + BtcAttestRules.EpochBlocks - 20; i++)
            {
                var roll = _rng.Next(weights);
                var step = steps.First(s => (roll -= s.Weight) < 0);
                await step.Run();
                await Check(step.Name);
            }

            // Wind down: the deposits that were dropped confirm after all, everything still to be paid is paid, and
            // the identity holds with nothing open.
            foreach (var deposit in _notYetBack.ToList())
            {
                await MineAndReport(deposit);
                _notYetBack.Remove(deposit);
                await Check("a dropped deposit confirmed after all");
            }
            for (var i = 0; i < 60 && Rows(VaultWithdrawalState.Pinned).Count > 0; i++)
            {
                await Confirm();
                await Check("confirm (winding down)");
            }
            Assert.Empty(Rows(VaultWithdrawalState.Pinned));

            _out.WriteLine($"seed {seed}: " + string.Join(", ", _done.OrderBy(d => d.Key).Select(d => $"{d.Key} x{d.Value}")));
            Assert.True(_done.GetValueOrDefault("deposit") >= 2 && _done.ContainsKey("transfer") && _done.ContainsKey("request"), "the run did too little to mean anything");
        }

        // ── Replay ──────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Every row of the chain-derived tables a vault's accounting lives in, as text, table by table.</summary>
        private static Dictionary<string, List<string>> EveryTable()
        {
            var tables = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            // Left out of the comparison: a row number the database hands out (it keeps counting after a wipe), and
            // the wall-clock time a node noted when it recorded a consumed burn. Neither is read by any rule.
            void Add(string label, LiteDB.LiteDatabase db, IEnumerable<string> collections, bool numbered = false, params string[] notCompared)
            {
                foreach (var name in collections.OrderBy(n => n, StringComparer.Ordinal))
                    tables[$"{label}/{name}"] = db.GetCollection(name).FindAll().Select(d =>
                    {
                        if (numbered && d["_id"].IsNumber) d.Remove("_id");
                        foreach (var field in notCompared) d.Remove(field);
                        return LiteDB.JsonSerializer.Serialize(d);
                    }).OrderBy(t => t, StringComparer.Ordinal).ToList();
            }
            Add("attestation", DbContext.DB_BtcAttest, BtcAttestStore.AllCollections);
            Add("withdrawals and bridge", DbContext.DB_VBTCWithdrawalRequests, new[]
            {
                DbContext.RSRV_VBTC_WITHDRAWAL_REQUESTS, VBTCBridgeLockState.CollectionName, VBTCBridgeBtcExitState.CollectionName,
            }, numbered: true);
            Add("withdrawals and bridge", DbContext.DB_VBTCWithdrawalRequests, new[] { VBTCBridgeConsumedBurn.CollectionName }, numbered: true, "Timestamp");
            Add("contract state", DbContext.DB_SmartContractStateTrei, DbContext.DB_SmartContractStateTrei.GetCollectionNames(), numbered: true);
            tables["accounts"] = StateData.GetAccountStateTrei().FindAll().Select(a => $"{a.Key} {a.Balance} {a.Nonce}").OrderBy(t => t, StringComparer.Ordinal).ToList();
            return tables;
        }

        /// <summary>
        /// A node that rebuilds its state from the blocks (a resync, a replay after a fault) must arrive at the same
        /// tables as a node that followed the chain live: the same coins, pins, settlements, voids, refunds, locks and
        /// balances, row for row. The state is wiped the way the node wipes it before a replay, and the same blocks are
        /// applied again.
        /// </summary>
        [Theory]
        [InlineData(3)]
        [InlineData(5)]
        public async Task ReplayingTheSameBlocks_RebuildsTheSameState_RowForRow(int seed)
        {
            await Run(seed);
            var live = EveryTable();
            var blocks = AppliedBlocks.ToList();
            Assert.True(blocks.Count > 50);
            Assert.NotEmpty(live["attestation/" + BtcAttestStore.Withdrawals]);

            BlockRollbackUtility.WipeChainDerivedState();
            Assert.All(BtcAttestStore.AllCollections, name => Assert.Equal(0, DbContext.DB_BtcAttest.GetCollection(name).Count()));
            SeedAccounts();
            StateWriteContext.Clear();
            Globals.LastBlock = new VerifiedXCore.Models.Block { Height = H1 - 2 };
            foreach (var block in blocks)
            {
                Assert.True(await StateData.UpdateTreis(block), $"block {block.Height} did not apply on replay");
                Globals.LastBlock = block;
            }

            var replayed = EveryTable();
            Assert.Equal(live.Keys.OrderBy(k => k), replayed.Keys.OrderBy(k => k));
            foreach (var (table, rows) in live)
            {
                var again = replayed[table];
                var onlyLive = rows.Except(again).Take(3).ToList();
                var onlyReplay = again.Except(rows).Take(3).ToList();
                Assert.True(onlyLive.Count == 0 && onlyReplay.Count == 0 && rows.Count == again.Count,
                    $"{table}: {rows.Count} rows live, {again.Count} after replay.\nOnly live:\n  {string.Join("\n  ", onlyLive)}\nOnly after replay:\n  {string.Join("\n  ", onlyReplay)}");
            }
            await Check("replay");
        }

        /// <summary>
        /// Every kind of step once, in a fixed order, with the identity checked after each. The random runs above add
        /// breadth; this one guarantees that no kind of step goes unexercised whatever the seeds happen to draw.
        /// </summary>
        [Fact]
        public async Task EveryKindOfStep_OnceInAFixedOrder_KeepsTheIdentity()
        {
            async Task<VfxTx> Mined(VfxTx tx)
            {
                var (ok, why) = await Verify(tx, Tip + 1);
                Assert.True(ok, why);
                await Apply(Tip + 1, tx);
                return tx;
            }
            Task<VfxTx> Send(V from, V to, decimal amount) => Mined(SignedBy(from, to.Address, TransactionType.VBTC_V2_TRANSFER,
                new { Function = "TransferVBTCV2()", ContractUID = _vault.Uid, FromAddress = from.Address, ToAddress = to.Address, Amount = amount }));
            Task<VfxTx> Ask(V from, decimal amount, int feeRate = 10) => Mined(SignedBy(from, from.Address, TransactionType.VBTC_V2_WITHDRAWAL_REQUEST,
                new { Function = "VBTCWithdrawalRequest()", ContractUID = _vault.Uid, BTCAddress = BtcDest, Amount = amount, FeeRate = feeRate, UniqueId = Guid.NewGuid().ToString("N") }));
            VaultWithdrawalRow RowOf(VfxTx request) => BtcAttestStore.Withdrawal(BtcWithdrawals.RefOf(request.Hash, _vault.Uid))!;
            var holder = _holders[0];

            await Start();
            _vault = await CreateVault(H1 + 1);
            await MineAndReport(Pay(_vault.Script, 300_000));
            await Check("deposit");

            await Send(Minter, holder, 0.001M);
            await Check("transfer");

            // The holder takes 0.0004 to the bridge; 0.0003 of it is burned on Base and exits.
            var lockId = Guid.NewGuid().ToString("N");
            await Mined(SignedBy(holder, holder.Address, TransactionType.VBTC_V2_BRIDGE_LOCK,
                new { Function = "VBTCBridgeLock()", ContractUID = _vault.Uid, LockId = lockId, Amount = 0.0004M, AmountSats = 40_000L, EvmDestination = "0x" + new string('a', 40) }));
            _locks.Add(lockId);
            await Check("bridge lock");
            var burn = "0x" + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            await Mined(SignedBy(Caster, Caster.Address, TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC, new
            {
                Function = "VBTCBridgeExitToBTC()", ContractUID = _vault.Uid, LockId = lockId, Amount = 0.0003M, AmountSats = 30_000L,
                TotalAmount = 0.0003M, TotalAmountSats = 30_000L, BtcDestination = BtcDest, BaseBurnTxHash = burn,
                Allocations = new List<PoolUnlockAllocation> { new() { LockId = lockId, SmartContractUID = _vault.Uid, UnlockAmount = 0.0003M } },
            }, free: true));
            var exitPart = Assert.Single(Rows(VaultWithdrawalState.Pinned));     // holds the vault's only coin
            Assert.True(exitPart.IsBridgeExit);
            await Check("bridge exit");

            // A request that has to wait for coins is cancelled; another one is left to run out of its window.
            var cancelled = await Ask(Minter, 0.0005M);
            Assert.Equal(VaultWithdrawalState.Waiting, RowOf(cancelled).State);
            await Check("request (waiting)");
            await Mined(SignedBy(Minter, Minter.Address, TransactionType.VBTC_V2_WITHDRAWAL_CANCEL,
                new { Function = "VBTCWithdrawalCancel()", ContractUID = _vault.Uid, WithdrawalRequestHash = cancelled.Hash, FailureProof = "" }));
            Assert.Equal(VaultWithdrawalState.Cancelled, RowOf(cancelled).State);
            await Check("cancel");
            var expires = await Ask(holder, 0.0002M);
            await Check("request (waiting, by a holder)");
            await Apply(expires.Height + VBTCWithdrawalRequest.EXPIRY_BLOCKS + 1);
            Assert.Equal(VaultWithdrawalState.Expired, RowOf(expires).State);
            Assert.Equal(VaultWithdrawalState.Pinned, BtcAttestStore.Withdrawal(exitPart.Ref)!.State);   // a pinned exit does not expire
            await Check("expiry");

            // Bitcoin pays the exit; its change is the vault's free coin again.
            await MineAndReport(BtcTx.Parse(exitPart.Pins[0].TxHex, Network.Main));
            Assert.True(VBTCBridgeBtcExitState.GetByBurnHash(burn)!.IsComplete);
            await Check("exit paid");

            // A withdrawal, its fee raised, and Bitcoin confirming the first transaction all the same.
            var paid = await Ask(Minter, 0.0005M);
            await Check("request (pinned)");
            await Mined(SignedBy(Minter, Minter.Address, TransactionType.VBTC_V2_COIN_PIN,
                new { Function = BtcWithdrawals.FeeBumpFunction, ContractUID = _vault.Uid, WithdrawalRequestHash = paid.Hash, FeeRate = 40 }));
            Assert.Equal(2, RowOf(paid).Pins.Count);
            await Check("fee bump");
            await MineAndReport(BtcTx.Parse(RowOf(paid).Pins[0].TxHex, Network.Main));
            Assert.Equal(VaultWithdrawalState.Settled, RowOf(paid).State);
            await Check("withdrawal paid by its first transaction");

            // A holder's withdrawal whose coin leaves in a transaction nobody pinned: void, refunded, and the loss is the owner's.
            var stolen = await Ask(holder, 0.0002M);
            var coin = BtcAttestStore.Coin(RowOf(stolen).Pins[0].Inputs[0])!;
            var at = coin.Outpoint.IndexOf(':');
            var holderBefore = await Spendable(holder);
            await MineAndReport(Pay(new[] { new OutPoint(uint256.Parse(coin.Outpoint[..at]), uint.Parse(coin.Outpoint[(at + 1)..])) }, (_vault.Script, coin.ValueSats - 5_000), (Elsewhere(), 5_000)));
            _lostToForeignSpends += 5_000;
            Assert.Equal(VaultWithdrawalState.Void, RowOf(stolen).State);
            Assert.Equal(holderBefore + 0.0002M, await Spendable(holder));
            await Check("withdrawal void");

            // What is left: 300,000 came in; 30,000 and 50,000 were paid out; 5,000 left in the foreign transaction.
            Assert.Equal(215_000, BtcAttestStore.Total(_vault.Uid).HeldSats);
            Assert.Equal(0.0006M, await Spendable(holder));                      // 0.001 received - 0.0004 locked; both of its requests came back
            Assert.Equal(0.0001M, VBTCBridgeLockState.GetByLockId(lockId)!.RemainingAmount);
            Assert.Equal(0.00215M - 0.0006M - 0.0001M, await Spendable(Minter)); // everything else: the 5,000 sats that are gone are the owner's loss
        }
    }
}
