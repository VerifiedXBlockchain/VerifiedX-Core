using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A bridge lock's apply writes the lock row before it debits the holder's vBTC. A row that cannot be written (a
    /// fault of this node's storage) leaves the vBTC where it was and marks the apply failed, so this node stops instead
    /// of holding a debit nothing can mint or unlock against. A lock applied again is not debited twice.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BridgeLockApplyOrderTests : IDisposable
    {
        private const string Vault = "5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d:1790500030";
        private const string Holder = "xHolderOfTheBridgeLockTestXXXXXXX";
        private const long Height = 500;
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly long _priorFailedAt = StateData.BlockStartFailedAtHeight;

        public BridgeLockApplyOrderTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"bridgelock_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            StateData.BlockStartFailedAtHeight = -1;
            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
            {
                SmartContractUID = Vault, ContractData = VbtcTestContracts.VbtcV2ContractData, MinterAddress = Holder, OwnerAddress = "xOwner",
                SCStateTreiTokenizationTXes = new List<SmartContractStateTreiTokenizationTX>
                {
                    new SmartContractStateTreiTokenizationTX { FromAddress = "+", ToAddress = Holder, Amount = 1.0M },
                },
            });
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            StateData.BlockStartFailedAtHeight = _priorFailedAt;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static Transaction Lock(string lockId) => new()
        {
            Hash = "lock-" + lockId, FromAddress = Holder, ToAddress = Holder, Height = Height, Timestamp = 1_790_500_100,
            TransactionType = TransactionType.VBTC_V2_BRIDGE_LOCK,
            Data = JsonConvert.SerializeObject(new { Function = "VBTCBridgeLock()", ContractUID = Vault, LockId = lockId, Amount = 0.5M, AmountSats = 50_000_000L, EvmDestination = "0x" + new string('a', 40) }),
        };

        private static void Apply(Transaction tx) =>
            typeof(StateData).GetMethod("ApplyVBTCBridgeLock", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { tx, tx.Height });

        private static decimal Balance() =>
            SmartContractStateTrei.GetSmartContractState(Vault)!.SCStateTreiTokenizationTXes!
                .Where(t => t.FromAddress == Holder || t.ToAddress == Holder).Sum(t => t.Amount);

        [Fact]
        public void ALock_WritesItsRowAndDebitsOnce()
        {
            Apply(Lock("lock-a"));
            Assert.NotNull(VBTCBridgeLockState.GetByLockId("lock-a"));
            Assert.Equal(0.5M, Balance());
            Assert.Equal(-1, StateData.BlockStartFailedAtHeight);

            // Applied again (a replay): the row is there, nothing more is debited.
            Assert.Equal(VBTCBridgeLockState.LockInsert.AlreadyPresent,
                VBTCBridgeLockState.InsertFromLockTx(Lock("lock-a"), Vault, "lock-a", 0.5M, 50_000_000, "0x" + new string('a', 40)));
            Apply(Lock("lock-a"));
            Assert.Equal(0.5M, Balance());
            Assert.Equal(-1, StateData.BlockStartFailedAtHeight);
        }

        [Fact]
        public void ALockRowThatCannotBeWritten_LeavesTheVbtcWhereItWas_AndMarksTheApplyFailed()
        {
            try
            {
                VBTCBridgeLockState.InsertStepForTests = _ => throw new IOException("No space left on device");
                Apply(Lock("lock-b"));
            }
            finally { VBTCBridgeLockState.InsertStepForTests = null; }

            Assert.Equal(1.0M, Balance());
            Assert.Null(VBTCBridgeLockState.GetByLockId("lock-b"));
            Assert.Equal(Height, StateData.BlockStartFailedAtHeight);
        }
    }
}
