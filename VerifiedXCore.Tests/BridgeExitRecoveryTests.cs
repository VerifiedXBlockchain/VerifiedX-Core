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
    /// A caster's record of a pool unlock follows the chain: one a reorganisation removed is voted on again, and a
    /// burn another caster's transaction paid is complete here too, not put to new rounds.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BridgeExitRecoveryTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly string _run = Guid.NewGuid().ToString("N");
        private readonly List<string> _burns = new();

        public BridgeExitRecoveryTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"bridgeexitrecoverytests_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
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

        // ── A pool unlock a reorganisation removed ──────────────────────────────────────────────────────────

        [Fact]
        public async Task APoolUnlockReadFromARecentBlock_ThatAReorganisationThenRemoves_IsVotedOnAgain()
        {
            // A caster starts and reads its recent blocks: one holds the pool unlock that paid a burn.
            var burn = Burn(10);
            var unlock = new Transaction
            {
                Hash = "unlock-tx", TransactionType = TransactionType.VBTC_V2_BRIDGE_POOL_UNLOCK, FromAddress = "xCaster", ToAddress = "xHolder",
                Data = JsonConvert.SerializeObject(new { Function = "BridgePoolUnlock()", ExitBurnTxHash = burn }),
            };
            BlockData.GetBlocks().Insert(new Block { Height = 990, Hash = "b990", Transactions = new List<Transaction> { unlock } });
            Assert.True(VBTCBridgeConsumedBurn.TryMarkConsumed(burn, "POOL_UNLOCK", "unlock-tx", 990)); // as applying that block does
            await (Task)typeof(BurnExitConsensusService).GetMethod("CatchUpSync", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;

            var record = ProcessedBurnForTests(burn);
            Assert.NotNull(record);
            Assert.Equal(BurnExitStatus.Complete, record!.Status);
            Assert.Equal(BurnExitType.VfxPoolUnlock, record.ExitType);

            // While the chain holds the redemption nothing changes.
            Assert.Equal(0, ReopenUnlocksTheChainLost(1_000, stateIsSettled: () => true));
            Assert.Equal(BurnExitStatus.Complete, record.Status);

            // The block is replaced by one without the unlock: the state rolled back no longer has the burn redeemed.
            // Found missing once it is only noted; found missing again a few blocks on, the burn is voted on again.
            VBTCBridgeConsumedBurn.GetCollection().DeleteAll();
            Assert.Equal(0, ReopenUnlocksTheChainLost(1_001, stateIsSettled: () => true));
            Assert.Equal(BurnExitStatus.Complete, record.Status);
            Assert.Equal(1, ReopenUnlocksTheChainLost(1_001 + RedemptionMissingBlocks, stateIsSettled: () => true));

            Assert.Equal(BurnExitStatus.Pending, record.Status);
            Assert.Equal("", record.CompletionVfxTxHash);
            Assert.False(record.FieldsFromBase); // its amount and destination are read from Base again by the round
            Assert.Contains(record, DueForARound(Records.Values, TimeUtil.GetTime()));
        }

        [Fact]
        public void OnlyAPoolUnlockTheChainHasSurelyLost_IsReopened()
        {
            void Seen(ProcessedBurnRecord r) { r.RedemptionSeenAtBlock = 990; r.CompletionVfxTxHash = "tx-" + r.BaseBurnTxHash[^2..]; }
            var lost = Track(20, BurnExitType.VfxPoolUnlock, BurnExitStatus.Complete, Seen);
            var held = Track(21, BurnExitType.VfxPoolUnlock, BurnExitStatus.Complete, Seen);
            var backInMempool = Track(22, BurnExitType.VfxPoolUnlock, BurnExitStatus.Complete, Seen);
            var unread = Track(23, BurnExitType.VfxPoolUnlock, BurnExitStatus.Complete, Seen);
            var bitcoinExit = Track(24, BurnExitType.BtcExit, BurnExitStatus.Complete, Seen);
            var ownSendWaiting = Track(25, BurnExitType.VfxPoolUnlock, BurnExitStatus.Complete, r => { Seen(r); r.CompletionBroadcastAtBlock = 998; });
            var settled = Track(26, BurnExitType.VfxPoolUnlock, BurnExitStatus.Complete, r => { Seen(r); r.RedemptionSeenAtBlock = 1_000 - RedemptionWatchBlocks - 1; });
            var missingOnce = Track(27, BurnExitType.VfxPoolUnlock, BurnExitStatus.Complete, Seen);

            var missingOnceIsBack = false;
            int Check(long block) => ReopenUnlocksTheChainLost(block,
                redeemed: burn => burn == held.BaseBurnTxHash ? true : burn == unread.BaseBurnTxHash ? null : burn == missingOnce.BaseBurnTxHash ? missingOnceIsBack : false,
                inMempool: tx => tx == backInMempool.CompletionVfxTxHash,
                stateIsSettled: () => true);

            Assert.Equal(0, Check(1_000)); // first found missing: noted only
            missingOnceIsBack = true;      // a read that came back empty once, the redemption is there again
            Assert.Equal(1, Check(1_000 + RedemptionMissingBlocks));

            Assert.Equal(BurnExitStatus.Pending, lost.Status);
            Assert.All(new[] { held, backInMempool, unread, bitcoinExit, ownSendWaiting, settled, missingOnce }, r => Assert.Equal(BurnExitStatus.Complete, r.Status));
            Assert.Equal(0, Check(1_010 + RedemptionMissingBlocks));
            Assert.Equal(BurnExitStatus.Complete, missingOnce.Status); // its count started over
        }

        [Fact]
        public void WhileTheNodeRestoresOrReplaysItsState_NoUnlockIsReopened()
        {
            // A restore empties the record of redeemed burns and refills it: every unlock looks lost meanwhile.
            var record = Track(28, BurnExitType.VfxPoolUnlock, BurnExitStatus.Complete, r => { r.RedemptionSeenAtBlock = 990; r.CompletionVfxTxHash = "tx"; });

            for (var block = 1_000; block < 1_020; block++)
                Assert.Equal(0, ReopenUnlocksTheChainLost(block, redeemed: _ => false, inMempool: _ => false, stateIsSettled: () => false));

            Assert.Equal(BurnExitStatus.Complete, record.Status);
            Assert.Equal(0, record.RedemptionMissingSinceBlock);

            // And it is kept past the usual time a completed record is held, while it is watched.
            record.DetectedAt = DateTime.UtcNow.AddHours(-30);
            PruneExpiredRecords();
            Assert.NotNull(ProcessedBurnForTests(record.BaseBurnTxHash));
        }

        // ── A burn another caster's transaction paid ────────────────────────────────────────────────────────

        [Fact]
        public async Task ABurnPaidThroughAnotherCastersUnlock_IsCompleteHere_AndNotPutToNewRounds()
        {
            // This caster agreed to the exit; another was the handler. Fifty blocks on, its record is where its round ended.
            var paid = Track(30, BurnExitType.VfxPoolUnlock, BurnExitStatus.ConsensusReached, r => r.DetectedAtBlock = 900);
            Assert.True(VBTCBridgeConsumedBurn.TryMarkConsumed(paid.BaseBurnTxHash, "POOL_UNLOCK", "handlers-tx", 905));

            await (Task)typeof(BurnExitConsensusService).GetMethod("CheckForStuckExits", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;

            Assert.Equal(BurnExitStatus.Complete, paid.Status);
            Assert.Empty(paid.HandlerCasterAddress);
            Assert.DoesNotContain(paid, DueForARound(Records.Values, long.MaxValue)); // no sweep takes it up
            Assert.Equal(1_000, paid.RedemptionSeenAtBlock); // and it is watched like any pool unlock seen on chain
        }

        [Fact]
        public void ABurnTheChainHasNotPaid_IsStillTakenForStuck()
        {
            var unpaid = Track(31, BurnExitType.VfxPoolUnlock, BurnExitStatus.ConsensusReached);

            Assert.False(FollowRedemptionOnChain(unpaid, 1_000));
            Assert.Equal(BurnExitStatus.ConsensusReached, unpaid.Status);
        }

    }
}
