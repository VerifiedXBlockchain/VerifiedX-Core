using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.FROST;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Data;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Security regression: a bridge-exit FROST signing must be bound to the contract whose vault
    /// is being spent. The reference "btcexit_{burn16}_{sc8}" used to be resolved by burn prefix
    /// alone; the contract suffix was compared to the leader's own input and could be omitted, so a
    /// leader could point contract B's validators at contract A's pending exit and drain B's vault
    /// to the exit destination for the full exit amount.
    /// </summary>
    [Collection("DbContextSequential")]
    public class FrostBridgeExitContractBindingTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;

        private const string Burn = "0x1111111111111111aaaaaaaaaaaaaaaabbbbbbbbbbbbbbbbccccccccccccccdd";
        private const string BurnPrefix = "1111111111111111"; // first 16 chars after 0x is what the caster uses
        private const string ContractA = "AAAAAAAA-contract-a";
        private const string ContractB = "BBBBBBBB-contract-b";
        private const string ContractC = "CCCCCCCC-contract-c";

        public FrostBridgeExitContractBindingTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"exitbind_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static string Ref(string burnPrefix, string? scPrefix) =>
            scPrefix == null ? $"btcexit_{burnPrefix}" : $"btcexit_{burnPrefix}_{scPrefix}";

        private static VBTCBridgeBtcExitState InsertExit(string burn, decimal total, List<PoolUnlockAllocation>? allocations, string lockIds = "L1,L2")
        {
            var rec = new VBTCBridgeBtcExitState
            {
                BaseBurnTxHash = burn,
                LockId = lockIds,
                SmartContractUID = ContractA,
                OwnerAddress = "xCaster",
                Amount = total,
                AmountSats = (long)(total * 100_000_000M),
                BtcDestination = "tb1q066af78la3rqmnchc396keujllva6turs52749",
                ExitTxHash = "exit-tx-1",
                CreatedTimestamp = 1,
                IsComplete = false,
                AllocationsJson = allocations == null ? null : JsonConvert.SerializeObject(allocations),
            };
            Assert.True(VBTCBridgeBtcExitState.TryInsert(rec));
            return rec;
        }

        private static List<PoolUnlockAllocation> TwoContractPlan() => new()
        {
            new PoolUnlockAllocation { LockId = "L1", SmartContractUID = ContractA, UnlockAmount = 0.3M },
            new PoolUnlockAllocation { LockId = "L2", SmartContractUID = ContractB, UnlockAmount = 0.2M },
        };

        [Fact]
        public void ContractInPlan_IsCappedAtItsOwnAllocation_NotTheWholeExit()
        {
            InsertExit(Burn, 0.5M, TwoContractPlan());

            var (exit, cap, reason) = FrostSigningAuthorization.ResolveBtcExitForContract(Ref(BurnPrefix, "BBBBBBBB"), ContractB);
            Assert.NotNull(exit);
            Assert.Equal(20_000_000L, cap);
            Assert.Equal("", reason);

            var (exitA, capA, _) = FrostSigningAuthorization.ResolveBtcExitForContract(Ref(BurnPrefix, "AAAAAAAA"), ContractA);
            Assert.NotNull(exitA);
            Assert.Equal(30_000_000L, capA);
        }

        [Fact]
        public void ContractNotInPlan_IsRefused_EvenWithMatchingSuffix()
        {
            InsertExit(Burn, 0.5M, TwoContractPlan());

            // The attacker declares contract C and crafts a suffix that matches C — the suffix alone must not authorize.
            var (exit, cap, reason) = FrostSigningAuthorization.ResolveBtcExitForContract(Ref(BurnPrefix, "CCCCCCCC"), ContractC);
            Assert.Null(exit);
            Assert.Equal(0, cap);
            Assert.Contains("does not allocate", reason);
        }

        [Fact]
        public void SuffixlessReference_IsRefused()
        {
            InsertExit(Burn, 0.5M, TwoContractPlan());
            var (exit, _, reason) = FrostSigningAuthorization.ResolveBtcExitForContract(Ref(BurnPrefix, null), ContractB);
            Assert.Null(exit);
            Assert.Contains("btcexit_{burnPrefix}_{contractPrefix}", reason);
        }

        [Fact]
        public void SuffixMismatchingDeclaredContract_IsRefused()
        {
            InsertExit(Burn, 0.5M, TwoContractPlan());
            var (exit, _, reason) = FrostSigningAuthorization.ResolveBtcExitForContract(Ref(BurnPrefix, "AAAAAAAA"), ContractB);
            Assert.Null(exit);
            Assert.Contains("prefix does not match", reason);
        }

        [Fact]
        public void UnknownBurn_IsRefused()
        {
            var (exit, _, reason) = FrostSigningAuthorization.ResolveBtcExitForContract(Ref("ffffffffffffffff", "BBBBBBBB"), ContractB);
            Assert.Null(exit);
            Assert.Contains("No on-chain EXIT_TO_BTC", reason);
        }

        [Fact]
        public void AmbiguousBurnPrefix_IsRefused()
        {
            InsertExit(Burn, 0.5M, TwoContractPlan());
            InsertExit("0x1111111111111111ffffffffffffffffffffffffffffffffffffffffffffffff", 0.1M, TwoContractPlan(), "L9");
            var (exit, _, reason) = FrostSigningAuthorization.ResolveBtcExitForContract(Ref(BurnPrefix, "BBBBBBBB"), ContractB);
            Assert.Null(exit);
            Assert.Contains("Ambiguous", reason);
        }

        [Fact]
        public void LegacyRecordWithoutPlan_IsRefused_ForEveryContract()
        {
            // Pre-field record: the lock list says which locks, not how much of each this exit drew,
            // so no per-contract cap can be derived safely. Fail closed.
            VBTCBridgeLockState.GetCollection().Insert(new VBTCBridgeLockState { LockId = "L1", SmartContractUID = ContractA, OwnerAddress = "xO", Amount = 0.9M, AmountSats = 90_000_000, EvmDestination = "0xabc", LockTxHash = "lt1", LockTimestamp = 1 });
            VBTCBridgeLockState.GetCollection().Insert(new VBTCBridgeLockState { LockId = "L2", SmartContractUID = ContractB, OwnerAddress = "xO", Amount = 0.1M, AmountSats = 10_000_000, EvmDestination = "0xabc", LockTxHash = "lt2", LockTimestamp = 1 });
            InsertExit(Burn, 0.5M, allocations: null, lockIds: "L1,L2");

            Assert.Null(FrostSigningAuthorization.ResolveBtcExitForContract(Ref(BurnPrefix, "AAAAAAAA"), ContractA).Exit);
            Assert.Null(FrostSigningAuthorization.ResolveBtcExitForContract(Ref(BurnPrefix, "BBBBBBBB"), ContractB).Exit);
            var (exitC, _, reasonC) = FrostSigningAuthorization.ResolveBtcExitForContract(Ref(BurnPrefix, "CCCCCCCC"), ContractC);
            Assert.Null(exitC);
            Assert.Contains("does not allocate", reasonC);
        }

        [Fact]
        public void ContractAllocationSats_UnparseablePlan_IsZero()
        {
            var rec = new VBTCBridgeBtcExitState { AmountSats = 50_000_000, AllocationsJson = "not-json", LockId = "L1" };
            Assert.Equal(0, FrostSigningAuthorization.ContractAllocationSats(rec, ContractA));
        }
    }
}
