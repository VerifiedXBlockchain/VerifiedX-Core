using Newtonsoft.Json;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Models;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Security regression: transaction validation sees only persisted state, so two bridge
    /// transactions in one block that redeem the same Base burn, or draw on the same lock, each
    /// passed on their own. The per-block guard rejects the second claim deterministically.
    /// </summary>
    public class BridgeIntraBlockGuardTests
    {
        private static Transaction PoolUnlock(string burn, params (string LockId, decimal Amount)[] allocs) => new Transaction
        {
            TransactionType = TransactionType.VBTC_V2_BRIDGE_POOL_UNLOCK,
            Data = JsonConvert.SerializeObject(new
            {
                Function = "VBTCBridgePoolUnlock()",
                ExitBurnTxHash = burn,
                Allocations = allocs.Select(a => new { a.LockId, SmartContractUID = "sc", UnlockAmount = a.Amount }).ToArray(),
            }),
        };

        private static Transaction ExitToBtc(string burn, params string[] lockIds) => new Transaction
        {
            TransactionType = TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC,
            Data = JsonConvert.SerializeObject(new
            {
                Function = "VBTCBridgeExitToBTC()",
                BaseBurnTxHash = burn,
                Allocations = lockIds.Select(l => new { LockId = l, SmartContractUID = "sc", UnlockAmount = 0.1M }).ToArray(),
            }),
        };

        private static Transaction LegacyUnlock(string burn, string lockId) => new Transaction
        {
            TransactionType = TransactionType.VBTC_V2_BRIDGE_UNLOCK,
            Data = JsonConvert.SerializeObject(new { Function = "VBTCBridgeUnlock()", ExitBurnTxHash = burn, LockId = lockId }),
        };

        [Fact]
        public void SameBurnTwiceInBlock_SecondRejected()
        {
            var st = new BridgeIntraBlockGuard.State();
            Assert.True(BridgeIntraBlockGuard.TryRegister(PoolUnlock("0xAAAA", ("L1", 0.3M)), st).Ok);
            var (ok, reason) = BridgeIntraBlockGuard.TryRegister(PoolUnlock("0xaaaa", ("L2", 0.1M)), st);
            Assert.False(ok);
            Assert.Contains("Duplicate Base burn", reason);
        }

        [Fact]
        public void BurnNormalization_IgnoresCaseAnd0x()
        {
            var st = new BridgeIntraBlockGuard.State();
            Assert.True(BridgeIntraBlockGuard.TryRegister(PoolUnlock("0xABCD", ("L1", 0.3M)), st).Ok);
            Assert.False(BridgeIntraBlockGuard.TryRegister(ExitToBtc("abcd", "L9"), st).Ok);
        }

        [Fact]
        public void OverlappingLock_AcrossDifferentBurns_SecondRejected()
        {
            var st = new BridgeIntraBlockGuard.State();
            Assert.True(BridgeIntraBlockGuard.TryRegister(PoolUnlock("0x01", ("L1", 0.3M), ("L2", 0.1M)), st).Ok);
            var (ok, reason) = BridgeIntraBlockGuard.TryRegister(PoolUnlock("0x02", ("L2", 0.05M)), st);
            Assert.False(ok);
            Assert.Contains("L2", reason);
        }

        [Fact]
        public void OverlappingLock_BetweenPoolUnlockAndExitAndLegacyUnlock_Rejected()
        {
            var st = new BridgeIntraBlockGuard.State();
            Assert.True(BridgeIntraBlockGuard.TryRegister(PoolUnlock("0x01", ("L1", 0.3M)), st).Ok);
            Assert.False(BridgeIntraBlockGuard.TryRegister(ExitToBtc("0x02", "L1"), st).Ok);
            Assert.False(BridgeIntraBlockGuard.TryRegister(LegacyUnlock("0x03", "L1"), st).Ok);
        }

        [Fact]
        public void DistinctBurnsAndLocks_AllAccepted()
        {
            var st = new BridgeIntraBlockGuard.State();
            Assert.True(BridgeIntraBlockGuard.TryRegister(PoolUnlock("0x01", ("L1", 0.3M)), st).Ok);
            Assert.True(BridgeIntraBlockGuard.TryRegister(ExitToBtc("0x02", "L2", "L3"), st).Ok);
            Assert.True(BridgeIntraBlockGuard.TryRegister(LegacyUnlock("0x03", "L4"), st).Ok);
            Assert.Equal(3, st.BurnHashes.Count);
            Assert.Equal(4, st.LockIds.Count);
        }

        [Fact]
        public void RejectedTransaction_LeavesStateUntouched()
        {
            var st = new BridgeIntraBlockGuard.State();
            Assert.True(BridgeIntraBlockGuard.TryRegister(PoolUnlock("0x01", ("L1", 0.3M)), st).Ok);
            // Same burn AND a new lock: rejected for the burn, and L7 must NOT be registered.
            Assert.False(BridgeIntraBlockGuard.TryRegister(PoolUnlock("0x01", ("L7", 0.3M)), st).Ok);
            Assert.DoesNotContain("L7", st.LockIds);
        }

        [Fact]
        public void NonBridgeAndUnparseable_PassThrough()
        {
            var st = new BridgeIntraBlockGuard.State();
            Assert.True(BridgeIntraBlockGuard.TryRegister(new Transaction { TransactionType = TransactionType.TX, Data = "{\"ExitBurnTxHash\":\"0x01\"}" }, st).Ok);
            Assert.True(BridgeIntraBlockGuard.TryRegister(new Transaction { TransactionType = TransactionType.VBTC_V2_BRIDGE_POOL_UNLOCK, Data = "not json" }, st).Ok);
            Assert.Empty(st.BurnHashes);
        }
    }
}
