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
    /// A rescan of Base asked for while a poll is under way is kept: the poll saves a cursor no later than the one the
    /// rescan asked for, whichever polls start in between.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BridgeExitRescanTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly string _run = Guid.NewGuid().ToString("N");
        private readonly List<string> _burns = new();

        public BridgeExitRescanTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"bridgeexitrescantests_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorLastBlock = Globals.LastBlock;
            DbContext.Initialize();
            Globals.LastBlock = new Block { Height = 1_000 };
            BaseBridgeExitWatchService.ForgetRescansForTests();
        }

        public void Dispose()
        {
            foreach (var burn in _burns) Records.TryRemove(burn, out _);
            BaseBridgeExitWatchService.ForgetRescansForTests();
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

        // ── A rescan asked for while a poll is under way ────────────────────────────────────────────────────

        [Fact]
        public void ARescanAskedForDuringAPoll_IsNotOverwrittenWhenThePollSavesWhereItGotTo()
        {
            BridgeExitSyncState.Save(new BridgeExitSyncState { Id = 1, LastScannedBlock = 5_000 });

            // A poll reads the cursor and is querying Base for blocks 5,001 to 5,500 when the operator asks for a rescan.
            var polling = BaseBridgeExitWatchService.ReadCursorForPoll();
            Assert.True(BaseBridgeExitWatchService.RescanFromBlock(1_200).Success);
            polling.LastScannedBlock = 5_500;
            BaseBridgeExitWatchService.SaveCursorFromPoll(polling);

            Assert.Equal(1_199, BridgeExitSyncState.GetOrCreate().LastScannedBlock);

            // The next poll reads from there and moves on as usual.
            var next = BaseBridgeExitWatchService.ReadCursorForPoll();
            Assert.Equal(1_199, next.LastScannedBlock);
            next.LastScannedBlock = 1_699;
            BaseBridgeExitWatchService.SaveCursorFromPoll(next);
            Assert.Equal(1_699, BridgeExitSyncState.GetOrCreate().LastScannedBlock);
        }

        [Fact]
        public void ASecondPollStartingMeanwhile_DoesNotMakeTheFirstForgetTheRescan()
        {
            BridgeExitSyncState.Save(new BridgeExitSyncState { Id = 1, LastScannedBlock = 5_000 });

            var first = BaseBridgeExitWatchService.ReadCursorForPoll();   // the scheduled poll, reading 5,001 on
            BaseBridgeExitWatchService.RescanFromBlock(1_200);
            var second = BaseBridgeExitWatchService.ReadCursorForPoll();  // a poll asked for by hand starts from the rescan
            Assert.Equal(1_199, second.LastScannedBlock);
            second.LastScannedBlock = 1_699;
            BaseBridgeExitWatchService.SaveCursorFromPoll(second);
            first.LastScannedBlock = 5_500;
            BaseBridgeExitWatchService.SaveCursorFromPoll(first);

            // The first poll never read 1,700 to 5,000 again: the cursor is not left past them.
            Assert.True(BridgeExitSyncState.GetOrCreate().LastScannedBlock <= 1_699, $"cursor at {BridgeExitSyncState.GetOrCreate().LastScannedBlock}");
        }

        [Fact]
        public void TwoRescansDuringOnePoll_KeepTheEarlier_AndARescanBetweenPollsIsSimplyWhereTheNextStarts()
        {
            BridgeExitSyncState.Save(new BridgeExitSyncState { Id = 1, LastScannedBlock = 5_000 });
            var polling = BaseBridgeExitWatchService.ReadCursorForPoll();
            BaseBridgeExitWatchService.RescanFromBlock(3_000);
            BaseBridgeExitWatchService.RescanFromBlock(4_000);
            polling.LastScannedBlock = 5_500;
            BaseBridgeExitWatchService.SaveCursorFromPoll(polling);
            Assert.Equal(2_999, BridgeExitSyncState.GetOrCreate().LastScannedBlock);

            // No poll under way: the rescan sets the cursor, and the poll that then starts is not held at it.
            BaseBridgeExitWatchService.RescanFromBlock(100);
            var next = BaseBridgeExitWatchService.ReadCursorForPoll();
            Assert.Equal(99, next.LastScannedBlock);
            next.LastScannedBlock = 599;
            BaseBridgeExitWatchService.SaveCursorFromPoll(next);
            Assert.Equal(599, BridgeExitSyncState.GetOrCreate().LastScannedBlock);
        }

        [Fact]
        public void ManyRescansDuringOnePoll_StillKeepTheEarliest()
        {
            BridgeExitSyncState.Save(new BridgeExitSyncState { Id = 1, LastScannedBlock = 5_000 });
            var polling = BaseBridgeExitWatchService.ReadCursorForPoll();
            BaseBridgeExitWatchService.RescanFromBlock(1_200);
            for (var i = 0; i < 200; i++)
                BaseBridgeExitWatchService.RescanFromBlock(4_000);
            polling.LastScannedBlock = 5_500;
            BaseBridgeExitWatchService.SaveCursorFromPoll(polling);

            Assert.Equal(1_199, BridgeExitSyncState.GetOrCreate().LastScannedBlock);
        }
    }
}
