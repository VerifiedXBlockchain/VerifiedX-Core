using System;
using System.IO;
using System.Reflection;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Data;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Follow-up hardening: the consumed-burn registry is chain-derived state. A full state rebuild
    /// (ResetTreis / replay) wiped locks and exits but not consumed burns, so under the strict apply
    /// order every replayed pool unlock found its burn "already consumed" and credited nothing.
    /// Also: the intra-block bridge guard's reasons are producer faults, not state corruption, so they
    /// must not count toward the automatic ResetTreis trigger.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BridgeStateWipeTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;

        public BridgeStateWipeTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"wipe_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
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

        [Fact]
        public void WipeChainDerivedState_ClearsConsumedBurns()
        {
            Assert.True(VBTCBridgeConsumedBurn.TryMarkConsumed("0xabc", "POOL_UNLOCK", "vfx-tx", 10));
            Assert.True(VBTCBridgeConsumedBurn.IsConsumed("0xabc"));

            var wipe = typeof(BlockRollbackUtility).GetMethod("WipeChainDerivedState", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            Assert.NotNull(wipe);
            var result = wipe!.Invoke(null, wipe.GetParameters().Length == 0 ? null : new object[wipe.GetParameters().Length]);
            if (result is System.Threading.Tasks.Task t) t.GetAwaiter().GetResult();

            Assert.False(VBTCBridgeConsumedBurn.IsConsumed("0xabc"));
        }

        [Fact]
        public void IntraBlockGuardReasons_AreNotStateCorruptionSignals()
        {
            var m = typeof(BlockValidatorService).GetMethod("IsStateCorruptionSignal", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(m);
            Assert.False((bool)m!.Invoke(null, new object[] { "Duplicate Base burn abc within block (already redeemed by an earlier bridge transaction)." })!);
            Assert.False((bool)m.Invoke(null, new object[] { "Bridge lock L1 is already drawn on by an earlier bridge transaction within this block." })!);
            Assert.True((bool)m.Invoke(null, new object[] { "Some other failure" })!);
        }
    }
}
