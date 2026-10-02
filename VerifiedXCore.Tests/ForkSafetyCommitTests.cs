using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VerifiedXCore;
using VerifiedXCore.Controllers;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Nodes;
using VerifiedXCore.Services;
using Xunit;
using Gate = VerifiedXCore.Services.BlockValidatorService.CasterGate;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Mainnet fork at 7,414,815 (Oct 1 2026). RFoK's first round at the height agreed on RMct and was rejected; its second
    /// round agreed on R9Kr and fetched R9Kr's block (8d0b75df), but the download loop had already queued a peer's copy
    /// of RMct's block (71aa3a89) and validated it with the caster check skipped — no agreed-hash check and, while the
    /// download held its semaphore, no certificate check. The commit step then skipped the agreed block (something was
    /// already queued), and the queue's "lowest hash" choice would have preferred 71aa… anyway. RFoK forked. Its
    /// resolution then fetched from a peer whose GetBlock served a stale round draft (71aa…) while GetBlockHash reported
    /// its committed 8d0b…, and never recovered.
    /// </summary>
    [Collection("DbContextSequential")]
    public class ForkSafetyCommitTests : IDisposable
    {
        private const string Agreed = "8d0b75df14f65da2053975067f97ca418ca0e5451ce3430e38bffd4bb6f3e180";
        private const string Stale = "71aa3a89563a7dae0000000000000000000000000000000000000000000000ff";
        private const long H = 7_414_815;

        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly bool _priorIsCaster = Globals.IsBlockCaster;

        public ForkSafetyCommitTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"forksafety_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            Globals.LastBlock = new Block { Height = H - 1, Hash = "tip" };
            BlockDownloadService.BlockDict.Clear();
            Globals.CasterApprovedBlockHashDict.Clear();
            Globals.CasterRoundDict.Clear();
        }

        public void Dispose()
        {
            BlockDownloadService.BlockDict.Clear();
            Globals.CasterApprovedBlockHashDict.Clear();
            Globals.CasterRoundDict.Clear();
            Globals.IsBlockCaster = _priorIsCaster;
            Globals.LastBlock = _priorLastBlock;
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static Block B(string hash, string producer = "RProducer", long height = H) => new() { Height = height, Hash = hash, Validator = producer, Transactions = new List<Transaction>() };

        // ── The commit gate ─────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void LiveTip_ADownloadedBlockThatIsNotTheAgreedOne_IsRejected()
        {
            // The fork: download loop (skipCasterCheck + blockDownloads) at the live tip, agreed hash 8d0b…, block 71aa….
            Assert.Equal(Gate.HashMismatch, BlockValidatorService.LiveTipCasterGate(Agreed, Stale, liveTip: true, skipCasterCheck: true, blockDownloads: true, () => true));
            Assert.Equal(Gate.Allow, BlockValidatorService.LiveTipCasterGate(Agreed, Agreed, liveTip: true, skipCasterCheck: true, blockDownloads: true, () => false));
        }

        [Fact]
        public void LiveTip_WithoutAnAgreedHash_ADownloadNeedsAValidCertificate()
        {
            Assert.Equal(Gate.Uncertified, BlockValidatorService.LiveTipCasterGate(null, Stale, liveTip: true, skipCasterCheck: true, blockDownloads: true, () => false));
            Assert.Equal(Gate.Allow, BlockValidatorService.LiveTipCasterGate(null, Stale, liveTip: true, skipCasterCheck: true, blockDownloads: true, () => true));
            Assert.Equal(Gate.Uncertified, BlockValidatorService.LiveTipCasterGate("", Stale, liveTip: true, skipCasterCheck: false, blockDownloads: true, () => false));
        }

        [Fact]
        public void LiveTip_NormalPathWithoutAnAgreedHash_StaysPending()
        {
            Assert.Equal(Gate.Pending, BlockValidatorService.LiveTipCasterGate(null, Agreed, liveTip: true, skipCasterCheck: false, blockDownloads: false, () => true));
        }

        [Fact]
        public void AwayFromTheLiveTip_CatchUpKeepsItsExemptions()
        {
            Assert.Equal(Gate.Allow, BlockValidatorService.LiveTipCasterGate(Agreed, Stale, liveTip: false, skipCasterCheck: true, blockDownloads: true, () => false));
            Assert.Equal(Gate.Allow, BlockValidatorService.LiveTipCasterGate(null, Stale, liveTip: false, skipCasterCheck: true, blockDownloads: true, () => false));
            // The non-skipping path still enforces an agreed hash everywhere, as before.
            Assert.Equal(Gate.HashMismatch, BlockValidatorService.LiveTipCasterGate(Agreed, Stale, liveTip: false, skipCasterCheck: false, blockDownloads: false, () => true));
        }

        // ── The queue's choice between competing blocks ───────────────────────────────────────────────

        [Fact]
        public void ACaster_PicksTheAgreedBlock_OverALowerHash()
        {
            Globals.IsBlockCaster = true;
            Globals.CasterApprovedBlockHashDict[H] = Agreed;
            var picked = BlockDownloadService.SelectCanonicalBlock(new List<(Block, string)> { (B(Stale), "1.1.1.1"), (B(Agreed), "2.2.2.2") });
            Assert.Equal(Agreed, picked!.Hash);
        }

        [Fact]
        public void WithoutAnAgreedHashOrCertificates_TheLowestHashRuleStillApplies()
        {
            Globals.IsBlockCaster = false;
            var picked = BlockDownloadService.SelectCanonicalBlock(new List<(Block, string)> { (B(Agreed), "2.2.2.2"), (B(Stale), "1.1.1.1") });
            Assert.Equal(Stale, picked!.Hash);
        }

        // ── The commit step stages the agreed block ───────────────────────────────────────────────────

        [Fact]
        public void KeepOnly_DropsEveryOtherCandidateAtTheHeight()
        {
            BlockStaging.Stage(B(Stale), "1.1.1.1");
            BlockStaging.Stage(B(Agreed), "2.2.2.2");
            BlockStaging.Stage(B("other-height", height: H + 1), "3.3.3.3");

            BlockcasterNode.DropOtherCandidates(H, Agreed);

            Assert.Equal(new[] { Agreed }, BlockDownloadService.BlockDict[H].Select(b => b.block.Hash));
            Assert.True(BlockDownloadService.BlockDict.ContainsKey(H + 1));
        }

        [Fact]
        public void KeepOnly_RemovesTheHeightWhenOnlyOtherBlocksWereQueued()
        {
            BlockStaging.Stage(B(Stale), "1.1.1.1");
            BlockcasterNode.DropOtherCandidates(H, Agreed);
            Assert.False(BlockDownloadService.BlockDict.ContainsKey(H));
            // The agreed block can then be staged.
            Assert.True(BlockStaging.Stage(B(Agreed), "2.2.2.2"));
        }

        // ── Agreement votes and round drafts ──────────────────────────────────────────────────────────

        [Fact]
        public void AgreementVotes_CountOnlyBlocksFromTheRoundsWinner()
        {
            Assert.True(BlockcasterNode.IsVoteForWinner("R9Kr57DRTTWHer1m6vXQ333mYD3Sey2SAj", "R9Kr57DRTTWHer1m6vXQ333mYD3Sey2SAj"));
            Assert.False(BlockcasterNode.IsVoteForWinner("RMct5BHNfARPDHS2chfiZEGBAwtp6ay2dB", "R9Kr57DRTTWHer1m6vXQ333mYD3Sey2SAj"));
            Assert.False(BlockcasterNode.IsVoteForWinner(null, "R9Kr57DRTTWHer1m6vXQ333mYD3Sey2SAj"));
        }

        [Fact]
        public void ARetry_DropsThePreviousAttemptsDraft_ButNeverACommittedHeight()
        {
            Globals.CasterRoundDict[H] = new CasterRound { BlockHeight = H, Block = B(Stale, "RMct5BHNfARPDHS2chfiZEGBAwtp6ay2dB") };
            Globals.CasterRoundDict[H - 1] = new CasterRound { BlockHeight = H - 1, Block = B("committed", height: H - 1) };

            BlockcasterNode.ClearStaleRoundDraft(H);
            BlockcasterNode.ClearStaleRoundDraft(H - 1);

            Assert.Null(Globals.CasterRoundDict[H].Block);
            Assert.NotNull(Globals.CasterRoundDict[H - 1].Block);
        }

        [Fact]
        public void GetBlock_ForACommittedHeight_NeverServesARoundDraft()
        {
            // The peer at the fork: its round draft for the height was another producer's block.
            Globals.LastBlock = new Block { Height = H, Hash = Agreed };
            Globals.CasterRoundDict[H] = new CasterRound { BlockHeight = H, Block = B(Stale, "RMct5BHNfARPDHS2chfiZEGBAwtp6ay2dB") };

            var action = new ValidatorController().GetBlock(H);
            var body = (string?)Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(action.Result).Value;
            Assert.DoesNotContain(Stale, body ?? "");

            // A height not committed yet still serves the current round's draft (block fetch between casters).
            Globals.CasterRoundDict[H + 1] = new CasterRound { BlockHeight = H + 1, Block = B("next-draft", height: H + 1) };
            var next = (string?)Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(new ValidatorController().GetBlock(H + 1).Result).Value;
            Assert.Contains("next-draft", next ?? "");
        }
    }
}
