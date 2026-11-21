using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
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
    /// A bridge exit to Bitcoin that carries allocations also carries TotalAmount and TotalAmountSats, the two fields its
    /// record and its locks' draws are written from: without either it is refused at admission, and in a block from
    /// Globals.BridgeExitCarriesTotalsHeight, so an exit that is accepted is one that is recorded. Below the height a
    /// block's exit is judged and applied as before. An exit submitted to this node is not pooled beside another for the
    /// same burn under any spelling of the burn's id, as a block takes one. Transactions are judged by VerifyTX (at
    /// admission and as a block's), chosen by ProcessTxPool, offered to the block's guard and applied with UpdateTreis,
    /// under the bridge rules that are live on the network: the sender is a committee caster and the exit carries the
    /// committee's votes for its burn, amount and destination.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BridgeExitCarriesTotalsTests : BtcAttestTestBase
    {
        private const string BtcDest = "bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4";
        /// <summary>A vault from before the chain watched vaults' Bitcoin: its exits are judged by the bridge's own rules.</summary>
        private const string Vault = "0123456789abcdef0123456789abcdef:1790600000";
        private const string Refusal = "carries TotalAmount and TotalAmountSats";
        /// <summary>With this network's own seed casters they form the committee; these are a majority of it.</summary>
        private const int Casters = 5;
        private readonly long _priorBinding = Globals.BridgeBurnBindingHeight;
        private readonly long _priorGuard = Globals.BridgeIntraBlockGuardHeight;
        private readonly long _priorTotals = Globals.BridgeExitCarriesTotalsHeight;
        private readonly Network? _priorNetwork = Globals.BTCNetwork;
        private readonly List<V> _casters = Enumerable.Range(0, Casters).Select(_ => NewKey()).ToList();

        public BridgeExitCarriesTotalsTests() : base(realMainnetRules: false)
        {
            Globals.BridgeBurnBindingHeight = 1;
            Globals.BridgeIntraBlockGuardHeight = 1;
            Globals.BridgeExitCarriesTotalsHeight = long.MaxValue; // as released: unset until a test sets it
            Globals.BTCNetwork = Network.Main;
            foreach (var caster in _casters)
            {
                Globals.BootstrapCasterAddresses.Add(caster.Address);
                StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = caster.Address, Balance = 10M, Nonce = 0 });
            }
        }

        public override void Dispose()
        {
            foreach (var caster in _casters) Globals.BootstrapCasterAddresses.Remove(caster.Address);
            Globals.BTCNetwork = _priorNetwork;
            Globals.BridgeExitCarriesTotalsHeight = _priorTotals;
            Globals.BridgeIntraBlockGuardHeight = _priorGuard;
            Globals.BridgeBurnBindingHeight = _priorBinding;
            base.Dispose();
        }

        private static long Tip => Globals.LastBlock.Height;

        private static string NewBurn() => "0x" + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

        /// <summary>A lock on a vault (the older one unless given), as the block that carried it left it; returns its id.</summary>
        private string Lock(decimal amount, string vault = Vault)
        {
            var id = "lock-" + Guid.NewGuid().ToString("N");
            VBTCBridgeLockState.GetCollection().Insert(new VBTCBridgeLockState
            {
                LockId = id, SmartContractUID = vault, OwnerAddress = Minter.Address, Amount = amount, AmountSats = (long)(amount * 100_000_000M),
                EvmDestination = "0x" + new string('a', 40), LockTimestamp = TimeUtil.GetTime(),
            });
            return id;
        }

        /// <summary>Every caster's vote for the burn, its amount and its destination.</summary>
        private List<CasterConsensusVote> Votes(string burn, long sats) => _casters.Select(caster =>
        {
            var timestamp = TimeUtil.GetTime();
            var message = BridgeCasterConsensus.BuildBoundVoteMessage(burn, "BTC_EXIT", sats, BtcDest, timestamp);
            return new CasterConsensusVote { CasterAddress = caster.Address, BaseBurnTxHash = burn, BurnType = "BTC_EXIT", Timestamp = timestamp,
                Signature = VerifiedXCore.Services.SignatureService.CreateSignature(message, caster.Key, caster.Pub) };
        }).ToList();

        /// <summary>
        /// The exit a caster sends for a burn, drawing <paramref name="amount"/> from one lock, with the committee's votes.
        /// <paramref name="total"/> and <paramref name="totalSats"/> leave a total out, <paramref name="shape"/> changes
        /// the payload further, and <paramref name="spelled"/> writes the burn's id another way than the votes do.
        /// </summary>
        private VfxTx Exit(string burn, string lockId, decimal amount, bool total = true, bool totalSats = true, string? spelled = null, int sender = 0,
            string vault = Vault, Action<JObject>? shape = null, long? timestamp = null)
        {
            var sats = (long)(amount * 100_000_000M);
            var data = new JObject
            {
                ["Function"] = "VBTCBridgeExitToBTC()", ["ContractUID"] = vault, ["LockId"] = lockId, ["Amount"] = amount, ["AmountSats"] = sats,
            };
            if (total) data["TotalAmount"] = amount;
            if (totalSats) data["TotalAmountSats"] = sats;
            data["BtcDestination"] = BtcDest;
            data["BaseBurnTxHash"] = spelled ?? burn;
            data["Allocations"] = JArray.FromObject(new[] { new PoolUnlockAllocation { LockId = lockId, SmartContractUID = vault, UnlockAmount = amount } });
            data["BtcWithdrawals"] = JValue.CreateNull(); // as the casters' builder writes it
            data["CasterConsensusVotes"] = JArray.FromObject(Votes(burn, sats));
            shape?.Invoke(data);
            var caster = _casters[sender];
            var tx = new VfxTx
            {
                Timestamp = timestamp ?? TimeUtil.GetTime(), FromAddress = caster.Address, ToAddress = caster.Address, Amount = 0.0M, Fee = 0.0M, Nonce = 0,
                TransactionType = TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC, Data = data.ToString(Formatting.None),
            };
            tx.Build();
            tx.Signature = VerifiedXCore.Services.SignatureService.CreateSignature(tx.Hash, caster.Key, caster.Pub);
            return tx;
        }

        private static VBTCBridgeBtcExitState? Record(string burn) => VBTCBridgeBtcExitState.GetByBurnHash(burn);
        private static VBTCBridgeLockState Held(string lockId) => VBTCBridgeLockState.GetByLockId(lockId)!;
        private static bool Pooled(VfxTx tx) => TransactionData.GetPool().Exists(x => x.Hash == tx.Hash);

        /// <summary>Judged at admission, as a block's transaction and by the block's guard, then mined in the next block.</summary>
        private async Task Mine(VfxTx tx)
        {
            var (admitted, admissionWhy) = await TransactionValidatorService.VerifyTX(tx);
            Assert.True(admitted, admissionWhy);
            var (ok, why) = await Verify(tx, Tip + 1);
            Assert.True(ok, why);
            var (guardOk, guardWhy) = BridgeIntraBlockGuard.TryRegister(tx, new BridgeIntraBlockGuard.State(), Tip + 1);
            Assert.True(guardOk, guardWhy);
            await Apply(Tip + 1, tx);
        }

        [Fact]
        public async Task TheCastersMadeHere_AreAMajorityOfTheCommittee()
        {
            await Start();
            var committee = BridgeCasterConsensus.GetCommitteeForHeight(Tip + 1);
            Assert.All(_casters, c => Assert.Contains(c.Address, committee));
            Assert.True(BridgeCasterConsensus.RequiredVotesFor(committee) <= Casters);
        }

        [Fact]
        public async Task AnExitWithItsTotals_IsAcceptedOnBothSidesOfTheHeight_RecordedAndItsLockDrawn()
        {
            await Start();
            var gate = Tip + 2;
            Globals.BridgeExitCarriesTotalsHeight = gate;

            foreach (var height in new[] { gate - 1, gate })
            {
                Assert.Equal(height, Tip + 1);
                var burn = NewBurn();
                var lockId = Lock(0.004M);
                var exit = Exit(burn, lockId, 0.004M);

                await Mine(exit);

                var record = Record(burn);
                Assert.NotNull(record);
                Assert.Equal(exit.Hash, record!.ExitTxHash);
                Assert.Equal(400_000, record.AmountSats);
                Assert.Equal(0.004M, record.Amount);
                Assert.False(record.IsComplete);
                Assert.Equal(0.004M, Held(lockId).UnlockedAmount);
                Assert.True(Held(lockId).IsUnlocked);
            }
        }

        [Fact]
        public async Task AtAdmission_AnExitWithAllocations_IsRefusedWithoutEitherTotal_BeforeTheHeightIsSet()
        {
            await Start();
            Assert.Equal(long.MaxValue, Globals.BridgeExitCarriesTotalsHeight);
            var lockId = Lock(0.004M);

            foreach (var (total, totalSats) in new[] { (false, false), (true, false), (false, true) })
            {
                var exit = Exit(NewBurn(), lockId, 0.004M, total, totalSats);
                var (ok, why) = await TransactionValidatorService.VerifyTX(exit);
                Assert.False(ok, $"admitted with TotalAmount {total}, TotalAmountSats {totalSats}");
                Assert.Contains(Refusal, why);
            }
            // A total written as null is a total left out.
            foreach (var field in new[] { "TotalAmount", "TotalAmountSats" })
            {
                var (ok, why) = await TransactionValidatorService.VerifyTX(Exit(NewBurn(), lockId, 0.004M, shape: data => data[field] = JValue.CreateNull()));
                Assert.False(ok, $"admitted with {field} null");
                Assert.Contains(Refusal, why);
            }

            // The same exit with both is admitted.
            var (admitted, admittedWhy) = await TransactionValidatorService.VerifyTX(Exit(NewBurn(), lockId, 0.004M));
            Assert.True(admitted, admittedWhy);
        }

        [Fact]
        public async Task InABlock_FromTheHeight_AnExitWithoutItsTotalsIsRefused_AndTheBurnsExitWithThemIsRecorded()
        {
            await Start();
            var gate = Tip + 1;
            Globals.BridgeExitCarriesTotalsHeight = gate;
            var burn = NewBurn();
            var capitals = "0x" + burn[2..].ToUpperInvariant();
            var lockId = Lock(0.004M);
            var other = Lock(0.004M);

            foreach (var height in new[] { gate, gate + 500 })
            {
                foreach (var (total, totalSats, spelled, drawn) in new[]
                {
                    (false, false, burn, lockId), (true, false, burn, lockId), (false, true, burn, lockId),
                    (false, false, capitals, other), // a second spelling of the burn's id
                })
                {
                    var (ok, why) = await Verify(Exit(burn, drawn, 0.004M, total, totalSats, spelled, sender: 1), height);
                    Assert.False(ok, $"accepted at {height} with TotalAmount {total}, TotalAmountSats {totalSats}");
                    Assert.Contains(Refusal, why);
                }
            }

            // The burn's exit with its totals is accepted in that block, recorded, and its lock drawn.
            var exit = Exit(burn, lockId, 0.004M);
            await Mine(exit);

            Assert.Equal(gate, Tip);
            var record = Record(burn);
            Assert.NotNull(record);
            Assert.Equal(exit.Hash, record!.ExitTxHash);
            Assert.Equal(400_000, record.AmountSats);
            Assert.True(Held(lockId).IsUnlocked);
            Assert.Equal(0M, Held(other).UnlockedAmount);
        }

        [Fact]
        public async Task InABlock_BelowTheHeight_AnExitWithoutItsTotals_IsJudgedAndAppliedAsBefore()
        {
            await Start();
            var gate = Tip + 10;
            Globals.BridgeExitCarriesTotalsHeight = gate;
            var burn = NewBurn();
            var lockId = Lock(0.004M);
            var exit = Exit(burn, lockId, 0.004M, total: false, totalSats: false);

            foreach (var height in new[] { Tip + 1, gate - 1 })
            {
                var (ok, why) = await Verify(exit, height);
                Assert.True(ok, why);
            }
            await Apply(Tip + 1, exit);

            // Applied as before the height.
            Assert.Null(Record(burn));
            Assert.Equal(0M, Held(lockId).UnlockedAmount);
            Assert.False(Held(lockId).IsUnlocked);

            var (atGate, atGateWhy) = await Verify(exit, gate);
            Assert.False(atGate);
            Assert.Contains(Refusal, atGateWhy);
        }

        /// <summary>
        /// Block building judges a pooled exit as admission does, before the height is set too: one without its totals is
        /// left out and removed, and the burn's exit with them is proposed, recorded and its lock drawn.
        /// </summary>
        [Fact]
        public async Task BlockBuilding_LeavesOutAnExitWithoutItsTotals_AndProposesTheBurnsExitWithThem()
        {
            await Start();
            Assert.Equal(long.MaxValue, Globals.BridgeExitCarriesTotalsHeight);
            var burn = NewBurn();
            var lockId = Lock(0.004M);
            var other = Lock(0.004M);
            var now = TimeUtil.GetTime();
            var bare = Exit(burn, other, 0.004M, total: false, totalSats: false, spelled: "0x" + burn[2..].ToUpperInvariant(), sender: 1, timestamp: now - 5);
            var exit = Exit(burn, lockId, 0.004M, timestamp: now);
            // Both as a peer's delivery leaves them in the mempool.
            foreach (var tx in new[] { bare, exit })
            {
                tx.TransactionRating = TransactionRating.A; // set on receipt; block building skips unrated transactions
                TransactionData.GetPool().InsertSafe(tx);
            }

            var proposed = await TransactionData.ProcessTxPool();

            Assert.Equal(exit.Hash, Assert.Single(proposed).Hash);
            Assert.False(Pooled(bare));
            await Apply(Tip + 1, proposed.ToArray());
            var record = Record(burn);
            Assert.NotNull(record);
            Assert.Equal(exit.Hash, record!.ExitTxHash);
            Assert.True(Held(lockId).IsUnlocked);
            Assert.Equal(0M, Held(other).UnlockedAmount);
        }

        /// <summary>An exit on a vault the chain watches is held to the same rule, on top of that vault's own.</summary>
        [Fact]
        public async Task AnExitOnAWatchedVault_CarriesBothTotalsToo()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            var gate = Tip + 10;
            Globals.BridgeExitCarriesTotalsHeight = gate;
            var lockId = Lock(0.004M, vault.Uid);
            var withoutSats = Exit(NewBurn(), lockId, 0.004M, totalSats: false, vault: vault.Uid);

            var (admitted, admissionWhy) = await TransactionValidatorService.VerifyTX(withoutSats);
            Assert.False(admitted);
            Assert.Contains(Refusal, admissionWhy);
            var (below, belowWhy) = await Verify(withoutSats, gate - 1);
            Assert.True(below, belowWhy);
            var (atGate, atGateWhy) = await Verify(withoutSats, gate);
            Assert.False(atGate);
            Assert.Contains(Refusal, atGateWhy);

            var whole = Exit(NewBurn(), lockId, 0.004M, vault: vault.Uid);
            Assert.True((await TransactionValidatorService.VerifyTX(whole)).Item1);
            foreach (var height in new[] { gate - 1, gate })
            {
                var (ok, why) = await Verify(whole, height);
                Assert.True(ok, why);
            }
        }

        [Fact]
        public async Task ASubmittedExit_IsNotPooledBesideAnotherForItsBurn_UnderAnySpellingOfTheBurnsId()
        {
            await Start();
            var burn = NewBurn();
            var capitals = "0x" + burn[2..].ToUpperInvariant();
            var first = Exit(burn, Lock(0.004M), 0.004M);
            // The same burn under a second spelling of its id.
            var again = Exit(burn, Lock(0.004M), 0.004M, spelled: capitals, sender: 1);
            var (alone, aloneWhy) = await TransactionValidatorService.VerifyTX(again);
            Assert.True(alone, aloneWhy);

            await TransactionData.AddToPool(first);
            Assert.True(Pooled(first));
            await TransactionData.AddToPool(again);
            Assert.False(Pooled(again));

            // Another burn's exit is held beside it.
            var unrelated = Exit(NewBurn(), Lock(0.004M), 0.004M, sender: 2);
            await TransactionData.AddToPool(unrelated);
            Assert.True(Pooled(unrelated));

            // A block reads the two as one burn too: its guard takes the first, and once that is applied the second is a repeat.
            var guard = new BridgeIntraBlockGuard.State();
            Assert.True(BridgeIntraBlockGuard.TryRegister(first, guard, Tip + 1).Ok);
            Assert.False(BridgeIntraBlockGuard.TryRegister(again, guard, Tip + 1).Ok);
            await Mine(first);
            var (repeat, repeatWhy) = await Verify(again, Tip + 1);
            Assert.False(repeat);
            Assert.Contains("Duplicate Base burn", repeatWhy);
        }
    }
}
