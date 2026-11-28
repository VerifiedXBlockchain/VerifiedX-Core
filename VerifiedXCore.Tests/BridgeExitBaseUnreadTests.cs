using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;
using Xunit;
using static VerifiedXCore.Bitcoin.Services.BurnExitConsensusService;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// An exit transaction this node did not send because Base could not be read when its burn was looked up again is
    /// planned again after a wait, as the look-up before the round already is. A rule refusing the transaction still
    /// fails the burn.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BridgeExitBaseUnreadTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly string _run = Guid.NewGuid().ToString("N");
        private readonly List<string> _burns = new();

        public BridgeExitBaseUnreadTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"bridgeexitbaseunreadtests_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorLastBlock = Globals.LastBlock;
            DbContext.Initialize();
            Globals.LastBlock = new Block { Height = 1_000 };
        }

        public void Dispose()
        {
            foreach (var burn in _burns) Records.TryRemove(burn, out _);
            try { DbContext.CloseDB(); } catch { }
            Globals.LastBlock = _priorLastBlock;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static ConcurrentDictionary<string, ProcessedBurnRecord> Records =>
            (ConcurrentDictionary<string, ProcessedBurnRecord>)typeof(BurnExitConsensusService)
                .GetField("_processedBurns", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        private string Burn(int n)
        {
            var hash = $"0x{_run}{new string('0', 24)}{n:x8}";
            _burns.Add(hash);
            return hash;
        }

        private ProcessedBurnRecord Track(int n, BurnExitType type, BurnExitStatus status, Action<ProcessedBurnRecord>? set = null)
        {
            var record = new ProcessedBurnRecord
            {
                BaseBurnTxHash = Burn(n), ExitType = type, Status = status, Amount = 0.001M, DetectedAtBlock = 900,
                VfxDestinationAddress = "xDestinationAddressForTheUnlock000", FieldsFromBase = true,
            };
            set?.Invoke(record);
            Records[record.BaseBurnTxHash] = record;
            return record;
        }

        // ── Base not answering when the exit transaction is checked ─────────────────────────────────────────

        [Theory]
        [InlineData("Exit burn transaction could not be verified on Base: Could not read Base receipt: no RPC reachable")]
        [InlineData("Base burn transaction could not be verified: Could not read Base receipt: The operation timed out")]
        [InlineData("Exit burn transaction could not be verified on Base: Could not read Base receipt: receipt not found")]
        public void AnExitNotSentBecauseBaseCouldNotBeRead_IsPlannedAgain_NotGivenUp(string reason)
        {
            var record = Track(1, BurnExitType.VfxPoolUnlock, BurnExitStatus.ConsensusReached);

            Assert.True(NotSentIsRetried(record, reason, "VFX pool unlock"));

            Assert.Equal(BurnExitStatus.Pending, record.Status);
            Assert.True(record.NextAttemptAt > TimeUtil.GetTime(), "it waits before its next round");
            Assert.Contains(record, DueForARound(Records.Values, record.NextAttemptAt)); // and the sweep takes it up again
        }

        [Theory]
        [InlineData("Exit burn amount does not match TotalAmountSats.")]
        [InlineData("Exit burn transaction could not be verified on Base: No matching burn event emitted by the bridge contract")]
        [InlineData("Exit burn transaction could not be verified on Base: Base transaction did not succeed")]
        [InlineData("Caster consensus votes invalid or insufficient for pool unlock.")]
        public void AnExitARuleRefused_IsNotPlannedAgainAsIfBaseHadNotAnswered(string reason)
        {
            var record = Track(2, BurnExitType.VfxPoolUnlock, BurnExitStatus.ConsensusReached);

            Assert.False(NotSentIsRetried(record, reason, "VFX pool unlock"));
            Assert.Equal(BurnExitStatus.ConsensusReached, record.Status);
        }

    }
}
