using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A fault of this node (its storage, or its own state found missing what the chain wrote) is reported the same way
    /// on every apply path, and only such a fault is: the bridge lock and exit writers report a storage fault as a failed
    /// apply step, and the block-start and block-end steps flag the block only for a node-local fault, so a throw caused
    /// by a block's data (the same on every node) does not mark every node failed.
    /// </summary>
    [Collection("DbContextSequential")]
    public class NodeLocalFaultReportingTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly long _priorFailedAt = StateData.BlockStartFailedAtHeight;
        private readonly bool _priorFlag = Globals.TreisUpdating;

        public NodeLocalFaultReportingTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"nlf_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            StateData.BlockStartFailedAtHeight = -1;
        }

        public void Dispose()
        {
            StateData.BlockStartStepForTests = null;
            StateData.BlockStartFailedAtHeight = _priorFailedAt;
            Globals.TreisUpdating = _priorFlag;
            StateWriteContext.Clear();
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static Block Empty(long height) => new() { Height = height, StateRoot = "sr", Transactions = new List<Transaction>() };

        [Fact]
        public async Task ABlockStartStep_FlagsTheBlockOnlyForAFaultOfThisNode()
        {
            // What a block's data causes is the same on every node: logged, not flagged.
            StateData.BlockStartStepForTests = _ => throw new InvalidOperationException("what the block says");
            Assert.True(await StateData.UpdateTreis(Empty(11)));
            Assert.Equal(-1, StateData.BlockStartFailedAtHeight);

            // This node's storage, or its own state found missing: flagged.
            StateData.BlockStartStepForTests = _ => throw new IOException("disk read failed");
            Assert.False(await StateData.UpdateTreis(Empty(12)));
            Assert.Equal(12, StateData.BlockStartFailedAtHeight);

            StateData.BlockStartFailedAtHeight = -1;
            StateData.BlockStartStepForTests = _ => throw new NodeStateFaultException("this node's state has none at block 13");
            Assert.False(await StateData.UpdateTreis(Empty(13)));
            Assert.Equal(13, StateData.BlockStartFailedAtHeight);

            StateData.BlockStartStepForTests = null;
            StateData.BlockStartFailedAtHeight = -1;
            Assert.True(await StateData.UpdateTreis(Empty(14)));
            Assert.Equal(-1, StateData.BlockStartFailedAtHeight);
        }

        [Fact]
        public void ANodeStateFault_IsNodeLocal_WhateverItWraps()
        {
            Assert.True(StateData.IsNodeLocalFault(new NodeStateFaultException("missing")));
            Assert.True(StateData.IsNodeLocalFault(new NodeStateFaultException("missing", new FormatException())));
            Assert.False(StateData.IsNodeLocalFault(new InvalidOperationException("data", new FormatException())));
        }

        [Fact]
        public void TheBridgeExitAndLockWriters_ReportAStorageFault()
        {
            var store = DbContext.DB_VBTCWithdrawalRequests;
            var broken = new LiteDB.LiteDatabase(new MemoryStream());
            broken.Dispose();
            StateWriteContext.SetHeight(21);
            try
            {
                DbContext.DB_VBTCWithdrawalRequests = broken;
                Assert.False(VBTCBridgeBtcExitState.TryInsert(new VBTCBridgeBtcExitState { BaseBurnTxHash = "0xburn" }));
                Assert.Equal(21, StateData.BlockStartFailedAtHeight);

                StateData.BlockStartFailedAtHeight = -1;
                Assert.False(VBTCBridgeBtcExitState.Update(new VBTCBridgeBtcExitState { BaseBurnTxHash = "0xburn" }));
                Assert.Equal(21, StateData.BlockStartFailedAtHeight);

                StateData.BlockStartFailedAtHeight = -1;
                VBTCBridgeBtcExitState.MarkComplete("0xburn", "btc");
                Assert.Equal(21, StateData.BlockStartFailedAtHeight);

                StateData.BlockStartFailedAtHeight = -1;
                Assert.False(VBTCBridgeLockState.ApplyPartialUnlock("lock", 0.1M, 10_000_000));
                Assert.Equal(21, StateData.BlockStartFailedAtHeight);

                StateData.BlockStartFailedAtHeight = -1;
                Assert.False(VBTCBridgeLockState.TryFinalizeUnlock(new Transaction { Hash = "u", Height = 22 }, new VBTCBridgeLockState { LockId = "lock", Amount = 1M }, "0xburn"));
                Assert.Equal(22, StateData.BlockStartFailedAtHeight);
            }
            finally { DbContext.DB_VBTCWithdrawalRequests = store; }

            // With the store healthy, what is simply not there is no fault.
            StateData.BlockStartFailedAtHeight = -1;
            Assert.False(VBTCBridgeLockState.ApplyPartialUnlock("no-such-lock", 0.1M, 10_000_000));
            VBTCBridgeBtcExitState.MarkComplete("0xnone", "btc");
            Assert.Equal(-1, StateData.BlockStartFailedAtHeight);
        }
    }
}
