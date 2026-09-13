using System;
using System.Collections.Generic;
using VerifiedXCore.Bitcoin.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Follow-up hardening (gated): bridge unlock/exit transactions must be submitted by a committee
    /// caster, pay a canonical valid VFX address (apply credits the raw string), and carry an exact
    /// sats/decimal pair.
    /// </summary>
    public class BridgeTxRulesTests
    {
        private const string ValidVfx = "RAjtW2uDSEDW9mPVkKp2K2AAu4uJD9Zrn7"; // canonical sample from AddressValidationTests
        private static HashSet<string> Committee(params string[] a) => new HashSet<string>(a, StringComparer.Ordinal);

        [Fact]
        public void WellFormedPoolUnlock_Accepted()
        {
            Assert.True(BridgeTxRules.CheckPoolUnlockShape("xCaster", ValidVfx, 0.12345678M, 12_345_678, Committee("xCaster")).Ok);
        }

        [Fact]
        public void NonCommitteeSubmitter_Refused()
        {
            var (ok, reason) = BridgeTxRules.CheckPoolUnlockShape("xAnyone", ValidVfx, 0.1M, 10_000_000, Committee("xCaster"));
            Assert.False(ok);
            Assert.Contains("committee caster", reason);
            Assert.False(BridgeTxRules.CheckSubmitter("xCaster", new HashSet<string>()).Ok); // no committee -> fail closed
        }

        [Fact]
        public void PaddedOrInvalidDestination_Refused()
        {
            Assert.False(BridgeTxRules.CheckPoolUnlockShape("xCaster", " " + ValidVfx, 0.1M, 10_000_000, Committee("xCaster")).Ok);
            Assert.False(BridgeTxRules.CheckPoolUnlockShape("xCaster", ValidVfx + "\n", 0.1M, 10_000_000, Committee("xCaster")).Ok);
            Assert.False(BridgeTxRules.CheckPoolUnlockShape("xCaster", "RNotAnAddress", 0.1M, 10_000_000, Committee("xCaster")).Ok);
            Assert.False(BridgeTxRules.CheckPoolUnlockShape("xCaster", "", 0.1M, 10_000_000, Committee("xCaster")).Ok);
        }

        [Fact]
        public void FailAllocations_MustBeSubsetOfRecordedPlan()
        {
            var plan = Newtonsoft.Json.JsonConvert.SerializeObject(new[]
            {
                new VerifiedXCore.Bitcoin.Models.PoolUnlockAllocation { LockId = "L1", SmartContractUID = "sc", UnlockAmount = 0.3M },
                new VerifiedXCore.Bitcoin.Models.PoolUnlockAllocation { LockId = "L2", SmartContractUID = "sc", UnlockAmount = 0.2M },
            });
            var ok = new[] { new VerifiedXCore.Bitcoin.Models.PoolUnlockAllocation { LockId = "L2", UnlockAmount = 0.2M } };
            Assert.True(BridgeTxRules.CheckFailAllocationsSubset(ok, plan).Ok);

            var foreignLock = new[] { new VerifiedXCore.Bitcoin.Models.PoolUnlockAllocation { LockId = "L9", UnlockAmount = 0.2M } };
            Assert.False(BridgeTxRules.CheckFailAllocationsSubset(foreignLock, plan).Ok);

            var wrongAmount = new[] { new VerifiedXCore.Bitcoin.Models.PoolUnlockAllocation { LockId = "L1", UnlockAmount = 0.31M } };
            Assert.False(BridgeTxRules.CheckFailAllocationsSubset(wrongAmount, plan).Ok);

            Assert.False(BridgeTxRules.CheckFailAllocationsSubset(ok, null).Ok);          // no plan -> cannot verify
            Assert.False(BridgeTxRules.CheckFailAllocationsSubset(ok, "not-json").Ok);
            Assert.False(BridgeTxRules.CheckFailAllocationsSubset(new VerifiedXCore.Bitcoin.Models.PoolUnlockAllocation[0], plan).Ok);
        }

        [Fact]
        public void BtcTxIdShape()
        {
            Assert.True(BridgeTxRules.IsBtcTxIdShape(new string('a', 64)));
            Assert.True(BridgeTxRules.IsBtcTxIdShape("0x" + new string('B', 64)));
            Assert.False(BridgeTxRules.IsBtcTxIdShape("bogus"));
            Assert.False(BridgeTxRules.IsBtcTxIdShape(new string('a', 63)));
            Assert.False(BridgeTxRules.IsBtcTxIdShape(""));
        }

        [Fact]
        public void SubSatoshiDust_Refused()
        {
            // 1.000000009 truncates to 100_000_000 sats but is not an exact pair.
            Assert.False(BridgeTxRules.CheckPoolUnlockShape("xCaster", ValidVfx, 1.000000009M, 100_000_000, Committee("xCaster")).Ok);
            Assert.True(BridgeTxRules.CheckPoolUnlockShape("xCaster", ValidVfx, 1.0M, 100_000_000, Committee("xCaster")).Ok);
            Assert.False(BridgeTxRules.CheckPoolUnlockShape("xCaster", ValidVfx, 0M, 0, Committee("xCaster")).Ok);
        }
    }
}
