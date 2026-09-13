using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Security regression: pool-unlock apply credited the destination first and only then marked
    /// the burn consumed (ignoring the result) and debited the lock (ignoring failure). Two unlocks
    /// for the same burn in one block both credited; two unlocks for different burns drawing on the
    /// same lock both credited even though only one debit could succeed. At/after the gate, the burn
    /// is consumed before any credit and each lock is debited before its credit.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BridgePoolUnlockApplyOrderTests : IDisposable
    {
        private const string Sc = "sc-pool-apply";
        private const string Caster = "xCasterApply";
        private const string Dest = "xDestinationApply";
        private const string BurnX = "0x1111111111111111111111111111111111111111111111111111111111111111";
        private const string BurnY = "0x2222222222222222222222222222222222222222222222222222222222222222";

        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly long _priorGate;

        public BridgePoolUnlockApplyOrderTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"poolapply_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorGate = Globals.BridgeIntraBlockGuardHeight;
            DbContext.Initialize();

            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
            {
                SmartContractUID = Sc,
                OwnerAddress = "xOwner",
                SCStateTreiTokenizationTXes = new() { new SmartContractStateTreiTokenizationTX { FromAddress = "+", ToAddress = "xOwner", Amount = 1.0M } },
            });
            VBTCBridgeLockState.GetCollection().Insert(new VBTCBridgeLockState
            {
                LockId = "L1", SmartContractUID = Sc, OwnerAddress = "xLocker",
                Amount = 0.5M, AmountSats = 50_000_000, EvmDestination = "0xabc", LockTxHash = "lock-tx", LockTimestamp = 1,
            });
        }

        public void Dispose()
        {
            Globals.BridgeIntraBlockGuardHeight = _priorGate;
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static Transaction PoolUnlockTx(string burn, decimal amount, long height, string lockId = "L1")
        {
            var tx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(),
                FromAddress = Caster,
                ToAddress = Caster,
                Amount = 0M,
                Fee = 0M,
                Nonce = 0,
                Height = height,
                TransactionType = TransactionType.VBTC_V2_BRIDGE_POOL_UNLOCK,
                Data = JsonConvert.SerializeObject(new
                {
                    Function = "VBTCBridgePoolUnlock()",
                    TotalAmount = amount,
                    TotalAmountSats = (long)(amount * 100_000_000M),
                    VfxDestinationAddress = Dest,
                    ExitBurnTxHash = burn,
                    Allocations = new[] { new { LockId = lockId, SmartContractUID = Sc, UnlockAmount = amount } },
                }),
            };
            tx.Build();
            return tx;
        }

        private static void Apply(Transaction tx)
        {
            var m = typeof(StateData).GetMethod("ApplyVBTCBridgePoolUnlock", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(m);
            m!.Invoke(null, new object[] { tx });
        }

        private static decimal DestinationCredit() =>
            SmartContractStateTrei.GetSmartContractState(Sc)!.SCStateTreiTokenizationTXes!
                .Where(r => r.ToAddress == Dest).Sum(r => r.Amount);

        private static decimal LockRemaining() => VBTCBridgeLockState.GetByLockId("L1")!.RemainingAmount;

        [Fact]
        public void StrictOrder_SameBurnTwice_SecondCreditsNothing()
        {
            Globals.BridgeIntraBlockGuardHeight = 1;

            Apply(PoolUnlockTx(BurnX, 0.3M, height: 10));
            Assert.Equal(0.3M, DestinationCredit());
            Assert.Equal(0.2M, LockRemaining());
            Assert.True(VBTCBridgeConsumedBurn.IsConsumed(BurnX));

            Apply(PoolUnlockTx(BurnX, 0.1M, height: 10));
            Assert.Equal(0.3M, DestinationCredit()); // unchanged
            Assert.Equal(0.2M, LockRemaining());     // unchanged
        }

        [Fact]
        public void StrictOrder_DifferentBurnsOverdrawingSameLock_SecondCreditsNothing()
        {
            Globals.BridgeIntraBlockGuardHeight = 1;

            Apply(PoolUnlockTx(BurnX, 0.3M, height: 10));
            Apply(PoolUnlockTx(BurnY, 0.3M, height: 10)); // lock has only 0.2 left: debit fails -> no credit

            Assert.Equal(0.3M, DestinationCredit());
            Assert.Equal(0.2M, LockRemaining());
            Assert.True(VBTCBridgeConsumedBurn.IsConsumed(BurnY)); // burn is still consumed (fail closed)
        }

        [Fact]
        public void StrictOrder_TwoValidBurns_BothCredit()
        {
            Globals.BridgeIntraBlockGuardHeight = 1;

            Apply(PoolUnlockTx(BurnX, 0.3M, height: 10));
            Apply(PoolUnlockTx(BurnY, 0.2M, height: 10));

            Assert.Equal(0.5M, DestinationCredit());
            Assert.Equal(0M, LockRemaining());
        }

        [Fact]
        public void PreGate_LegacyOrderIsPreserved()
        {
            // Gate far in the future: legacy behaviour (credit, then debit, then mark) — the
            // documented pre-fix semantics that historical blocks were applied with.
            Globals.BridgeIntraBlockGuardHeight = 999_999_999_999L;

            Apply(PoolUnlockTx(BurnX, 0.3M, height: 10));
            Assert.Equal(0.3M, DestinationCredit());
            Assert.Equal(0.2M, LockRemaining());
            Assert.True(VBTCBridgeConsumedBurn.IsConsumed(BurnX));
        }
    }
}
