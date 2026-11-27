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
    /// A bridge exit to Bitcoin carries its allocations as a JSON array, and one lock is drawn once per block whatever an
    /// exit's outer LockId says: an exit whose Allocations was an object holding the list under "$values" skipped the
    /// allocation check and named a made-up lock to the block's guard, so two exits in one block drew on one lock, and the
    /// second was recorded (and fixed for payment) with a part it never drew. From Globals.BridgeAllocationsArrayHeight a
    /// block refuses it, the guard claims every allocated lock, and apply draws every part of an exit or none and records
    /// nothing it did not draw; admission refuses it at every height. Below the height blocks are judged and applied as
    /// before. Transactions are judged by VerifyTX (at admission and as a block's), offered to the block's guard and
    /// applied with UpdateTreis, under the bridge rules live on the network.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BridgeAllocationsArrayTests : BtcAttestTestBase
    {
        private const string BtcDest = "bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4";
        /// <summary>A vault from before the chain watched vaults' Bitcoin: its exits are judged by the bridge's own rules.</summary>
        private const string Vault = "0123456789abcdef0123456789abcdef:1790600000";
        private const string Refusal = "carries its Allocations as a JSON array";
        /// <summary>With this network's own seed casters they form the committee; these are a majority of it.</summary>
        private const int Casters = 5;
        private readonly long _priorBinding = Globals.BridgeBurnBindingHeight;
        private readonly long _priorGuard = Globals.BridgeIntraBlockGuardHeight;
        private readonly long _priorArray = Globals.BridgeAllocationsArrayHeight;
        private readonly Network? _priorNetwork = Globals.BTCNetwork;
        private readonly List<V> _casters = Enumerable.Range(0, Casters).Select(_ => NewKey()).ToList();

        public BridgeAllocationsArrayTests() : base(realMainnetRules: false)
        {
            Globals.BridgeBurnBindingHeight = 1;
            Globals.BridgeIntraBlockGuardHeight = 1;
            Globals.BridgeAllocationsArrayHeight = long.MaxValue; // as released: unset until a test sets it
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
            Globals.BridgeAllocationsArrayHeight = _priorArray;
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

        /// <summary>The second exit of the report: a made-up outer LockId, and the lock it draws inside an object.</summary>
        private static void Wrapped(JObject data)
        {
            data["LockId"] = "made-up-id";
            data["Allocations"] = new JObject { ["$values"] = data["Allocations"] };
        }

        /// <summary>Both exits offered to one block's guard: whether the second is taken.</summary>
        private static bool BothTaken(VfxTx first, VfxTx second, long height)
        {
            var block = new BridgeIntraBlockGuard.State();
            Assert.True(BridgeIntraBlockGuard.TryRegister(first, block, height).Ok);
            return BridgeIntraBlockGuard.TryRegister(second, block, height).Ok;
        }

        [Fact]
        public async Task FromTheHeight_AnExitWhoseAllocationsAreAnObject_IsRefused_AndTheGuardDrawsTheLockOnce()
        {
            await Start();
            var gate = Tip + 1;
            Globals.BridgeAllocationsArrayHeight = gate;
            var lockId = Lock(0.008M);
            var first = Exit(NewBurn(), lockId, 0.004M);
            var second = Exit(NewBurn(), lockId, 0.004M, sender: 1, shape: Wrapped);

            foreach (var height in new[] { gate, gate + 500 })
            {
                var (ok, why) = await Verify(second, height);
                Assert.False(ok, $"accepted at {height}");
                Assert.Contains(Refusal, why);
                // The guard reads the lock inside the object, under any spelling of its id.
                Assert.False(BothTaken(first, second, height));
                Assert.False(BothTaken(first, Exit(NewBurn(), " " + lockId.ToUpperInvariant() + " ", 0.004M, sender: 2), height));
            }
            // A bridge unlock of the lock is claimed by its own LockId, whatever an Allocations field beside it holds.
            foreach (var junk in new JToken[] { new JObject(), JValue.CreateNull(), "x", new JArray() })
            {
                var unlock = new VfxTx { TransactionType = TransactionType.VBTC_V2_BRIDGE_UNLOCK,
                    Data = new JObject { ["Function"] = "VBTCBridgeUnlock()", ["ExitBurnTxHash"] = NewBurn(), ["LockId"] = lockId, ["Allocations"] = junk }.ToString(Formatting.None) };
                Assert.False(BothTaken(first, unlock, gate));
            }
            // Two exits on two locks share a block.
            Assert.True(BothTaken(first, Exit(NewBurn(), Lock(0.004M), 0.004M, sender: 1), gate));

            var (firstOk, firstWhy) = await Verify(first, gate);
            Assert.True(firstOk, firstWhy);
        }

        [Fact]
        public async Task AtAdmission_AnExitWhoseAllocationsAreNotANonEmptyArray_IsRefused_BeforeTheHeightIsSet()
        {
            await Start();
            Assert.Equal(long.MaxValue, Globals.BridgeAllocationsArrayHeight);
            var lockId = Lock(0.004M);
            var (wrappedOk, wrappedWhy) = await TransactionValidatorService.VerifyTX(Exit(NewBurn(), lockId, 0.004M, shape: Wrapped));
            Assert.False(wrappedOk);
            Assert.Contains(Refusal, wrappedWhy);
            // No allocations at all is refused too (by the earlier rule that an exit carries its allocations).
            foreach (var shape in new Action<JObject>[] { d => d["Allocations"] = new JArray(), d => d["Allocations"] = JValue.CreateNull(), d => d.Remove("Allocations") })
                Assert.False((await TransactionValidatorService.VerifyTX(Exit(NewBurn(), lockId, 0.004M, shape: shape))).Item1);
            var (admitted, admittedWhy) = await TransactionValidatorService.VerifyTX(Exit(NewBurn(), lockId, 0.004M));
            Assert.True(admitted, admittedWhy);
        }

        [Fact]
        public async Task BelowTheHeight_TheObjectShapedExit_IsJudgedAsBefore()
        {
            await Start();
            var gate = Tip + 10;
            Globals.BridgeAllocationsArrayHeight = gate;
            var lockId = Lock(0.008M);
            var first = Exit(NewBurn(), lockId, 0.004M);
            var second = Exit(NewBurn(), lockId, 0.004M, sender: 1, shape: Wrapped);
            var (ok, why) = await Verify(second, gate - 1);
            Assert.True(ok, why);
            Assert.True(BothTaken(first, second, gate - 1)); // the guard read the outer LockId
        }

        [Fact]
        public async Task FromTheHeight_AnExitWhosePartsCannotAllBeDrawn_DrawsAndRecordsNothing()
        {
            await Start();
            var gate = Tip + 1;
            Globals.BridgeAllocationsArrayHeight = gate;
            var lockId = Lock(0.004M);
            var firstBurn = NewBurn();
            var secondBurn = NewBurn();
            // Two exits on one lock reach apply in one block (as the guard now refuses): the first draws it, the second
            // finds it drawn and leaves no record, no part to pay and nothing to give back on expiry.
            await Apply(gate, Exit(firstBurn, lockId, 0.004M), Exit(secondBurn, lockId, 0.004M, sender: 1));
            Assert.NotNull(Record(firstBurn));
            Assert.Null(Record(secondBurn));
            Assert.Equal(0.004M, Held(lockId).UnlockedAmount);
            // An exit asking one lock twice for more than it holds in all (each part alone fits) draws neither part.
            var other = Lock(0.004M);
            var thirdBurn = NewBurn();
            await Apply(Tip + 1, Exit(thirdBurn, other, 0.005M, shape: d => d["Allocations"] = JArray.FromObject(new[]
            {
                new PoolUnlockAllocation { LockId = other, SmartContractUID = Vault, UnlockAmount = 0.003M },
                new PoolUnlockAllocation { LockId = other.ToUpperInvariant(), SmartContractUID = Vault, UnlockAmount = 0.002M },
            })));
            Assert.Null(Record(thirdBurn));
            Assert.Equal(0M, Held(other).UnlockedAmount);
            // An object-shaped one is not applied at all.
            var fourthBurn = NewBurn();
            await Apply(Tip + 1, Exit(fourthBurn, other, 0.004M, shape: Wrapped));
            Assert.Null(Record(fourthBurn));
            Assert.Equal(0M, Held(other).UnlockedAmount);
        }

        [Fact]
        public async Task BelowTheHeight_AnExitWhosePartCannotBeDrawn_IsRecordedAsBefore()
        {
            await Start();
            var gate = Tip + 10;
            Globals.BridgeAllocationsArrayHeight = gate;
            var lockId = Lock(0.004M);
            var firstBurn = NewBurn();
            var secondBurn = NewBurn();
            await Apply(Tip + 1, Exit(firstBurn, lockId, 0.004M), Exit(secondBurn, lockId, 0.004M, sender: 1));
            Assert.NotNull(Record(firstBurn));
            var second = Record(secondBurn);
            Assert.NotNull(second);
            Assert.Equal("[]", second!.DrawnAllocationsJson);
        }
    }
}
