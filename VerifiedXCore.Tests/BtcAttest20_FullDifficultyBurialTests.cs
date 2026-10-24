using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VerifiedXCore.Bitcoin.Attestation;
using VerifiedXCore.Bitcoin.Attestation.Net;
using VerifiedXCore.Services;
using Xunit;
using BtcBlock = NBitcoin.Block;
using VfxTx = VerifiedXCore.Models.Transaction;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Only headers of full difficulty bury a Bitcoin block (third pass, 14 October 2026).
    ///
    /// A report proves proof-of-work, not validity: the transactions of a reported block are in the block, and whether
    /// the block is one Bitcoin accepts is known by the work others put on top of it. On testnet4 a block timed more
    /// than twenty minutes after its parent carries the easiest target there is, so the three headers that buried a
    /// block there cost nothing: anyone could make a block paying a vault from nowhere, bury it under three more of
    /// their own, and show it to the signers as the best chain until a real block came. Such headers are still
    /// followed (they are Bitcoin), but they bury nothing. On mainnet there are none, and nothing changes.
    ///
    /// The chain here mixes blocks of full difficulty with easy ones under the testnet rule, at regtest sizes.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BtcAttest20_FullDifficultyBurialTests : BtcAttestTestBase
    {
        private const long Checkpoint = 4032;
        private const long First = Checkpoint + 1;
        private const uint FullBits = 0x201fffffu; // four times the work of the easiest target
        private readonly Btc _btc = new(Checkpoint - 11) { Bits = FullBits };
        private readonly BtcReportPlanner _planner = new();

        public BtcAttest20_FullDifficultyBurialTests() : base(realMainnetRules: false)
        {
            BtcChainRules.OverrideForTests = BtcChainRules.RegtestWithEasyBlocks;
            _btc.MineEmpty(12);
            Globals.BitcoinCheckpointHeightMainnet = Checkpoint;
            Globals.BitcoinCheckpointHeadersMainnet = string.Concat(_btc.HeaderHexes(Checkpoint - 11, Checkpoint));
        }

        private sealed class View : IBtcChainView
        {
            private readonly Btc _chain;
            public long? SeenThrough;
            public View(Btc chain) { _chain = chain; }
            public long TipHeight => Math.Min(_chain.Tip, SeenThrough ?? long.MaxValue);
            public BtcHeader? HeaderAt(long height) => height >= Checkpoint - 11 && height <= TipHeight ? _chain.Header(height) : null;
            public BtcBlock? BlockAt(long height) => height >= Checkpoint - 11 && height <= TipHeight ? _chain.Block(height) : null;
        }

        private static AttestationMetaRow Meta => BtcAttestStore.GetMeta()!;
        private static long Tip => Globals.LastBlock.Height;

        private BtcReport Forward(long block, long throughTip) =>
            Assert.Single(BtcBlockScanner.BuildForward(_btc.Block(block), block, _btc.HeaderHexes(Meta.TipHeight + 1, throughTip), 0));

        [Fact]
        public void TheRulesFollowEasyBlocks_AndCountOnlyFullOnes()
        {
            var rules = BtcChainRules.Current;
            _btc.Mine();        // First: full
            _btc.MineEasy();    // First + 1
            _btc.Mine();        // First + 2: full again, at the target of the last full block
            Assert.Equal(FullBits, _btc.Header(First).Bits);
            Assert.Equal(rules.LimitBits, _btc.Header(First + 1).Bits);
            Assert.Equal(FullBits, _btc.Header(First + 2).Bits);
            for (var h = First; h <= First + 2; h++)
                Assert.Null(rules.Validate(_btc.Header(h), h, x => _btc.Header(x)));
            Assert.True(rules.CountsTowardBurial(_btc.Header(First)));
            Assert.False(rules.CountsTowardBurial(_btc.Header(First + 1)));
            // On mainnet, and under the plain test rules, every header counts and burial is block + depth.
            Assert.True(BtcChainRules.Mainnet.CountsTowardBurial(_btc.Header(First + 1)));
            Assert.Equal(First + 3, BtcChainRules.Mainnet.BuriedAt(First, First + 10, 3, _ => _btc.Header(First + 1)));
        }

        [Fact]
        public async Task ABlockUnderEasyHeadersOnly_IsNotBuried_AndItsDepositIsRefused()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            // A block paying the vault, itself easy, and any number of easy blocks on top: what an attacker makes
            // in a moment. (Whether the payment spends a real coin no header can say.)
            var at = _btc.MineEasy(Pay(vault.Script, 50_000_000));
            for (var i = 0; i < 6; i++) _btc.MineEasy();

            var (ok, why) = await Verify(Signed(Forward(at, _btc.Tip)), H1 + 2);
            Assert.False(ok);
            Assert.Contains("is not under 3 attested headers of full difficulty", why);
            // The signers do not offer it either.
            Assert.Empty(_planner.Plan(new View(_btc), Tip).Reports);

            // Two full headers on top, with easy ones between: still not buried.
            _btc.Mine();
            _btc.MineEasy();
            _btc.Mine();
            (ok, why) = await Verify(Signed(Forward(at, _btc.Tip)), H1 + 2);
            Assert.False(ok);
            Assert.Contains("is not under 3 attested headers of full difficulty", why);
            Assert.Empty(_planner.Plan(new View(_btc), Tip).Reports);

            // The third: buried, at that header and no earlier.
            _btc.MineEasy();
            var buriedAt = _btc.Mine();
            _btc.MineEasy();
            var planned = Assert.Single(_planner.Plan(new View(_btc), Tip).Reports);
            Assert.Equal(BtcPlannedReport.Forward, planned.Kind);
            Assert.Equal(at, planned.Report.BlockHeight);
            Assert.Equal(_btc.HeaderHexes(First, buriedAt), planned.Report.Headers); // through the burying header, and no further
            // A validator that has seen less, but as far as the burying header, builds the same bytes.
            var behind = Assert.Single(new BtcReportPlanner().Plan(new View(_btc) { SeenThrough = buriedAt }, Tip).Reports);
            Assert.Equal(planned.Report.Hash(), behind.Report.Hash());
            Assert.Empty(new BtcReportPlanner().Plan(new View(_btc) { SeenThrough = buriedAt - 1 }, Tip).Reports);

            var tx = Signed(planned.Report);
            (ok, why) = await TransactionValidatorService.VerifyTX(tx);
            Assert.True(ok, why);
            await Apply(Tip + 1, tx);
            Assert.Equal(50_000_000, BtcAttestStore.Total(vault.Uid)!.HeldSats);
            Assert.Equal(buriedAt, Meta.TipHeight);
        }

        [Fact]
        public async Task AQuietRange_IsClosedByTheHeaderThatBuriesIt()
        {
            await Start();
            _btc.MineEmpty(BtcAttestRules.QuietBlocks);
            var quietThrough = Checkpoint + BtcAttestRules.QuietBlocks;
            Assert.Equal(quietThrough, _btc.Tip);
            _btc.Mine();
            _btc.MineEasy();
            _btc.MineEasy();
            _btc.Mine();
            Assert.Empty(_planner.Plan(new View(_btc), Tip).Reports); // two full headers above the range
            var buriedAt = _btc.Mine();
            _btc.Mine();

            // Stopping at range + depth, as on mainnet, is short here; one past the burying header is one too many.
            foreach (var through in new[] { quietThrough + BtcAttestRules.Depth, buriedAt - 1, buriedAt + 1 })
            {
                var (ok, why) = await Verify(Signed(BtcBlockScanner.BuildQuiet(_btc.HeaderHexes(First, through), 0, _btc.Header(quietThrough).Hash)), H1 + 1);
                Assert.False(ok);
                Assert.Contains("no more and no fewer", why);
            }
            var planned = Assert.Single(_planner.Plan(new View(_btc), Tip).Reports);
            Assert.Equal(BtcPlannedReport.Quiet, planned.Kind);
            Assert.Equal(_btc.HeaderHexes(First, buriedAt), planned.Report.Headers);
            var tx = Signed(planned.Report);
            var (accepted, reason) = await TransactionValidatorService.VerifyTX(tx);
            Assert.True(accepted, reason);
            await Apply(Tip + 1, tx);
            Assert.Equal(quietThrough, Meta.EventsThroughHeight);
            Assert.Equal(buriedAt, Meta.TipHeight);
        }
    }
}
