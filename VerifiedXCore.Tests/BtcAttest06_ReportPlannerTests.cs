using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NBitcoin;
using VerifiedXCore.Bitcoin.Attestation;
using VerifiedXCore.Bitcoin.Attestation.Net;
using VerifiedXCore.Services;
using Xunit;
using BtcBlock = NBitcoin.Block;
using BtcTx = NBitcoin.Transaction;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Bitcoin facts in consensus, part 6: what a validator offers to sign (plan 3.4). No leader and no proposal: the
    /// report is worked out from chain state and from the Bitcoin blocks the validator holds. Here the planner is run
    /// the way a validator runs it, and every report it produces is signed, judged by VerifyTX and applied by
    /// StateData.UpdateTreis before the planner is asked again.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BtcAttest06_ReportPlannerTests : BtcAttestTestBase
    {
        private const long Checkpoint = 4032;
        private const long First = Checkpoint + 1;
        private readonly Btc _btc = new(Checkpoint - 11);
        private readonly BtcReportPlanner _planner = new();

        public BtcAttest06_ReportPlannerTests() : base(realMainnetRules: false)
        {
            _btc.MineEmpty(12);
            Globals.BitcoinCheckpointHeightMainnet = Checkpoint;
            Globals.BitcoinCheckpointHeadersMainnet = string.Concat(_btc.HeaderHexes(Checkpoint - 11, Checkpoint));
        }

        /// <summary>A validator's view of Bitcoin: a chain, how much of it has arrived, and which blocks are not fetched.</summary>
        private sealed class View : IBtcChainView
        {
            public Btc Chain;
            public long? SeenThrough;
            public readonly HashSet<long> NotFetched = new();

            public View(Btc chain) { Chain = chain; }
            public long TipHeight => Math.Min(Chain.Tip, SeenThrough ?? long.MaxValue);
            public BtcHeader? HeaderAt(long height) => height >= Checkpoint - 11 && height <= TipHeight ? Chain.Header(height) : null;
            public BtcBlock? BlockAt(long height) => height >= Checkpoint - 11 && height <= TipHeight && !NotFetched.Contains(height) ? Chain.Block(height) : null;
        }

        private static AttestationMetaRow Meta => BtcAttestStore.GetMeta()!;
        private static long Tip => Globals.LastBlock.Height;

        /// <summary>
        /// What the validators do between two VFX blocks, until there is nothing left to sign: plan, sign (three of the
        /// four), submit, and the report is mined in the next block. Returns the kinds of report mined, in order.
        /// </summary>
        private async Task<List<string>> Drive(View view, BtcReportPlanner? planner = null, int most = 400)
        {
            var kinds = new List<string>();
            for (var i = 0; i < most; i++)
            {
                var plan = (planner ?? _planner).Plan(view, Tip);
                if (plan.Reports.Count == 0) break;
                var chosen = plan.Reports[0];
                var tx = Signed(chosen.Report);
                var (ok, why) = await TransactionValidatorService.VerifyTX(tx);
                Assert.True(ok, $"the planner built a {chosen.Kind} report the chain refuses: {why}");
                await Apply(Tip + 1, tx);
                kinds.Add(chosen.Kind);
            }
            return kinds;
        }

        // ── Forward ─────────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task ADeposit_IsOfferedOnceItsBlockIsBuried_WithTheHeadersThatBuryItAndNoMore()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            _btc.MineEmpty(2);
            var deposit = Pay(vault.Script, 150_000);
            var at = _btc.Mine(deposit);
            _btc.MineEmpty(6);
            var view = new View(_btc) { SeenThrough = at + BtcAttestRules.Depth - 1 };

            var plan = _planner.Plan(view, Tip);
            Assert.Empty(plan.Reports); // one block short of the depth: nothing to sign
            Assert.Null(plan.Waiting);

            view.SeenThrough = at + BtcAttestRules.Depth;
            var planned = Assert.Single(_planner.Plan(view, Tip).Reports);
            Assert.Equal(BtcPlannedReport.Forward, planned.Kind);
            Assert.Equal(at, planned.Report.BlockHeight);
            Assert.Equal(_btc.HeaderHexes(First, at + BtcAttestRules.Depth), planned.Report.Headers);

            // A validator that has seen further builds the same bytes: nothing in a report depends on how far its
            // builder has seen, so their signatures meet on one hash.
            var ahead = new BtcReportPlanner().Plan(new View(_btc), Tip).Reports.Single();
            Assert.Equal(planned.Report.ToCanonicalJson(), ahead.Report.ToCanonicalJson());
            Assert.Equal(planned.Report.Hash(), ahead.Report.Hash());

            Assert.Equal(new[] { BtcPlannedReport.Forward }, await Drive(view));
            Assert.Equal(150_000, BtcAttestStore.Total(vault.Uid).HeldSats);
            Assert.Equal(at, Meta.EventsThroughHeight);
            Assert.Equal(at + BtcAttestRules.Depth, Meta.TipHeight);
            Assert.Empty(_planner.Plan(view, Tip).Reports);

            // The spend, later: one header is all it still needs.
            view.SeenThrough = null;
            var spend = Pay(new[] { new OutPoint(deposit, 0) }, (Elsewhere(), 100_000), (vault.Script, 49_000));
            var spentAt = _btc.Mine(spend);
            Assert.Empty(_planner.Plan(view, Tip).Reports);
            _btc.MineEmpty(BtcAttestRules.Depth);
            planned = Assert.Single(_planner.Plan(view, Tip).Reports);
            Assert.Equal(_btc.HeaderHexes(Meta.TipHeight + 1, spentAt + BtcAttestRules.Depth), planned.Report.Headers);
            Assert.Equal(new[] { BtcPlannedReport.Forward }, await Drive(view));
            Assert.Equal(49_000, BtcAttestStore.Total(vault.Uid).HeldSats);
        }

        [Fact]
        public async Task ABlockThatHasNotBeenFetched_IsWaitedFor_NeverSkipped()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            var missing = _btc.Mine(Pay(vault.Script, 10_000));
            var later = _btc.Mine(Pay(vault.Script, 20_000));
            _btc.MineEmpty(BtcAttestRules.Depth);
            var view = new View(_btc);
            view.NotFetched.Add(missing);

            var plan = _planner.Plan(view, Tip);
            Assert.Empty(plan.Reports); // not the later block, though it is there and buried
            Assert.Contains($"Bitcoin block {missing} is not fetched yet", plan.Waiting);

            view.NotFetched.Clear();
            Assert.Equal(new[] { BtcPlannedReport.Forward, BtcPlannedReport.Forward }, await Drive(view));
            Assert.Equal(later, Meta.EventsThroughHeight);
            Assert.Equal(30_000, BtcAttestStore.Total(vault.Uid).HeldSats);
        }

        [Fact]
        public async Task AValidatorWhoseHeadersAreBehindTheAttestedTip_Waits()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            var at = _btc.Mine(Pay(vault.Script, 10_000));
            _btc.MineEmpty(BtcAttestRules.Depth);
            Assert.Single(await Drive(new View(_btc)));

            var behind = new View(_btc) { SeenThrough = at + 1 };
            var plan = new BtcReportPlanner().Plan(behind, Tip);
            Assert.Empty(plan.Reports);
            Assert.Contains("below the attested tip", plan.Waiting);
        }

        // ── Quiet days ──────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task DaysWithNoVaultEvent_AreQuietReports_AndADepositAfterThemIsStillFound()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            var view = new View(_btc);

            _btc.MineEmpty(BtcAttestRules.QuietBlocks + BtcAttestRules.Depth - 1);
            Assert.Empty(_planner.Plan(view, Tip).Reports); // the day is not buried yet
            _btc.MineEmpty(1);
            Assert.Equal(new[] { BtcPlannedReport.Quiet }, await Drive(view));
            Assert.Equal(Checkpoint + BtcAttestRules.QuietBlocks, Meta.EventsThroughHeight);

            // A validator that was away for days catches up one quiet day per VFX block.
            _btc.MineEmpty(2 * BtcAttestRules.QuietBlocks + 10);
            var deposit = Pay(vault.Script, 77_000);
            var at = _btc.Mine(deposit);
            _btc.MineEmpty(BtcAttestRules.Depth);
            var kinds = await Drive(view);
            Assert.Equal(new[] { BtcPlannedReport.Quiet, BtcPlannedReport.Quiet, BtcPlannedReport.Forward }, kinds);
            Assert.Equal(at, Meta.EventsThroughHeight);
            Assert.Equal(77_000, BtcAttestStore.Total(vault.Uid).HeldSats);

            // A day that holds an event is never called quiet: the event's block is reported instead.
            _btc.MineEmpty(100);
            var inside = _btc.Mine(Pay(vault.Script, 1_000));
            _btc.MineEmpty(60);
            kinds = await Drive(view);
            Assert.Equal(new[] { BtcPlannedReport.Forward }, kinds);
            Assert.Equal(inside, Meta.EventsThroughHeight);
        }

        // ── Parts ───────────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task ABlockWithManyEvents_IsCarriedThroughPartByPart()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            var at = _btc.Mine(Enumerable.Range(0, 450).Select(i => Pay(vault.Script, 1_000 + i)).ToArray());
            var next = _btc.Mine(Pay(vault.Script, 5));
            _btc.MineEmpty(BtcAttestRules.Depth);
            var view = new View(_btc);

            var kinds = await Drive(view);
            Assert.Equal(BtcPlannedReport.Forward, kinds[0]);
            Assert.True(kinds.Count >= 3);
            Assert.All(kinds.Skip(1).Take(kinds.Count - 2), k => Assert.Equal(BtcPlannedReport.Part, k));
            Assert.Equal(BtcPlannedReport.Forward, kinds[^1]); // the next block, once the first is closed
            Assert.Equal(0, Meta.PendingNextPart);
            Assert.Equal(next, Meta.EventsThroughHeight);
            Assert.Equal(451, BtcAttestStore.CoinsOf(vault.Uid).Count);

            // While a block is under way, a validator that does not hold that block can sign nothing.
            Assert.True(at < next);
        }

        // ── Backfill ────────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task ADepositMinedBeforeItsVaultExisted_IsFoundAndBackfilled_AlongsideTheForwardReport()
        {
            await Start();
            var a = await CreateVault(H1 + 1);
            var bAddress = NewTaprootAddress();
            var early = Pay(ScriptOf(bAddress), 33_000);
            var at = _btc.Mine(Pay(a.Script, 20_000), early);
            _btc.MineEmpty(BtcAttestRules.Depth);
            var view = new View(_btc);
            Assert.Equal(new[] { BtcPlannedReport.Forward }, await Drive(view));
            Assert.Empty(_planner.Plan(view, Tip).Reports);

            var b = await CreateVault(Tip + 1, bAddress);
            // A later, unreported block holds a deposit too: both reports are true at once, and both are offered.
            var later = _btc.Mine(Pay(b.Script, 1_000));
            _btc.MineEmpty(BtcAttestRules.Depth);
            var plan = _planner.Plan(view, Tip);
            Assert.Equal(new[] { BtcPlannedReport.Backfill, BtcPlannedReport.Forward }, plan.Reports.Select(r => r.Kind));
            Assert.Equal(b.Uid, plan.Reports[0].Report.Scope);
            Assert.Equal(at, plan.Reports[0].Report.BlockHeight);
            Assert.Equal(later, plan.Reports[1].Report.BlockHeight);

            // The other order works as well: the forward report first, the backfill after it.
            var forward = Signed(plan.Reports[1].Report);
            Assert.True((await TransactionValidatorService.VerifyTX(forward)).Item1);
            await Apply(Tip + 1, forward);
            Assert.Equal(new[] { BtcPlannedReport.Backfill }, await Drive(view));
            Assert.Equal(34_000, BtcAttestStore.Total(b.Uid).HeldSats);
            Assert.Equal(20_000, BtcAttestStore.Total(a.Uid).HeldSats);
            Assert.Empty(_planner.Plan(view, Tip).Reports);

            // A validator that starts now looks back over the reported blocks and finds nothing owed.
            Assert.Empty(new BtcReportPlanner().Plan(view, Tip).Reports);
        }

        [Fact]
        public async Task ASpendOfABackfilledCoin_InALaterReportedBlock_IsBackfilledToo()
        {
            await Start();
            var a = await CreateVault(H1 + 1);
            var bAddress = NewTaprootAddress();
            var early = Pay(ScriptOf(bAddress), 50_000);
            _btc.Mine(Pay(a.Script, 1_000), early);
            // Vault B's coin is spent in a block that is reported (for vault A) before vault B exists.
            var spend = Pay(new[] { new OutPoint(early, 0) }, (Elsewhere(), 30_000), (ScriptOf(bAddress), 19_000));
            _btc.Mine(Pay(a.Script, 2_000), spend);
            _btc.MineEmpty(BtcAttestRules.Depth);
            var view = new View(_btc);
            Assert.Equal(2, (await Drive(view)).Count);

            var b = await CreateVault(Tip + 1, bAddress);
            // Lowest block first: the deposit, then the block where it is spent (and its change created).
            Assert.Equal(new[] { BtcPlannedReport.Backfill, BtcPlannedReport.Backfill }, await Drive(view));
            Assert.Equal(19_000, BtcAttestStore.Total(b.Uid).HeldSats);
            Assert.Equal(VaultCoinStatus.Spent, BtcAttestStore.Coin(Outpoint(early, 0))!.Status);
            Assert.Equal(VaultCoinStatus.Unspent, BtcAttestStore.Coin(Outpoint(spend, 1))!.Status);
        }

        // ── Reorganisations ─────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task WhenTheAttestedHeadersAreOrphaned_TheShortestHeavierBranchReplacesThem()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            var at = _btc.Mine(Pay(vault.Script, 150_000));
            _btc.MineEmpty(BtcAttestRules.Depth);
            var view = new View(_btc);
            Assert.Single(await Drive(view));
            var attestedTip = Meta.TipHeight;

            // Bitcoin drops the last two attested blocks for a branch that is, so far, no heavier.
            var fork = _btc.ForkAfter(attestedTip - 2);
            fork.MineEmpty(2);
            view.Chain = fork;
            var plan = _planner.Plan(view, Tip);
            Assert.Empty(plan.Reports);
            Assert.Contains("does not yet carry more work", plan.Waiting);

            // It grows by five; the replacement carries three headers, the fewest that outweigh the two replaced.
            fork.MineEmpty(5);
            var planned = Assert.Single(_planner.Plan(view, Tip).Reports);
            Assert.Equal(BtcPlannedReport.Replacement, planned.Kind);
            Assert.Equal(attestedTip - 2, planned.Report.ReplaceAbove);
            Assert.Equal(fork.HeaderHexes(attestedTip - 1, attestedTip + 1), planned.Report.Headers);

            var deposit = Pay(vault.Script, 10_000);
            var onFork = fork.Mine(deposit);
            fork.MineEmpty(BtcAttestRules.Depth);
            Assert.Equal(new[] { BtcPlannedReport.Replacement, BtcPlannedReport.Forward }, await Drive(view));
            Assert.Equal(fork.Header(attestedTip).Hash, BtcAttestStore.HeaderAt(attestedTip)!.Hash);
            Assert.Equal(onFork, Meta.EventsThroughHeight);
            Assert.Equal(160_000, BtcAttestStore.Total(vault.Uid).HeldSats);
            Assert.Equal(at, BtcAttestStore.Coin(Outpoint(_btc.Block(at).Transactions[1], 0))!.CreatedInHeight);
        }

        [Fact]
        public async Task AReorganisationBelowTheReportedEvents_StopsReports_AndSaysWhy()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            var at = _btc.Mine(Pay(vault.Script, 150_000));
            _btc.MineEmpty(BtcAttestRules.Depth);
            var view = new View(_btc);
            Assert.Single(await Drive(view));

            var fork = _btc.ForkAfter(at - 1); // the deposit's own block is gone
            fork.MineEmpty(10);
            view.Chain = fork;
            var plan = _planner.Plan(view, Tip);
            Assert.Empty(plan.Reports);
            Assert.Contains("deep-reorganisation", plan.Waiting);
        }

        // ── Everything together ─────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task FollowingBitcoinBlockByBlock_TheChainEndsUpHoldingExactlyWhatBitcoinHolds()
        {
            await Start();
            var random = new Random(20261012);
            var vaults = new List<(string Uid, string Address, NBitcoin.Script Script)>
            {
                await CreateVault(Tip + 1),
                await CreateVault(Tip + 1),
            };
            var lateAddress = NewTaprootAddress();
            var lateScript = ScriptOf(lateAddress);
            var view = new View(_btc);
            // Ground truth, kept by the test: every output to a vault script, and whether it has been spent.
            var unspent = new Dictionary<string, (NBitcoin.Script Script, long Sats, BtcTx Tx, int Vout)>();
            var kinds = new List<string>();

            for (var round = 0; round < 60; round++)
            {
                var txs = new List<BtcTx>();
                var scripts = vaults.Select(v => v.Script).Append(lateScript).ToList();
                for (var n = random.Next(0, 3); n > 0; n--)
                {
                    var to = scripts[random.Next(scripts.Count)];
                    var tx = Pay(to, random.Next(10_000, 900_000));
                    txs.Add(tx);
                    unspent[Outpoint(tx, 0)] = (to, tx.Outputs[0].Value.Satoshi, tx, 0);
                }
                if (unspent.Count > 0 && random.Next(3) == 0)
                {
                    // A withdrawal: one coin spent, part paid out, the rest back to the same vault. Sometimes it spends
                    // a coin created earlier in this same block.
                    var pick = unspent.ElementAt(random.Next(unspent.Count));
                    var coin = pick.Value;
                    var change = coin.Sats / 3;
                    var spend = Pay(new[] { new OutPoint(coin.Tx, coin.Vout) }, (Elsewhere(), coin.Sats - change - 500), (coin.Script, change));
                    txs.Add(spend);
                    unspent.Remove(pick.Key);
                    unspent[Outpoint(spend, 1)] = (coin.Script, change, spend, 1);
                }
                _btc.Mine(txs.ToArray());

                if (round == 25)
                    vaults.Add(await CreateVault(Tip + 1, lateAddress)); // its address has been receiving deposits all along
                kinds.AddRange(await Drive(view));
            }
            _btc.MineEmpty(BtcAttestRules.Depth);
            kinds.AddRange(await Drive(view));

            Assert.Contains(BtcPlannedReport.Forward, kinds);
            Assert.Contains(BtcPlannedReport.Backfill, kinds);
            Assert.Empty(_planner.Plan(view, Tip).Reports);
            foreach (var vault in vaults)
            {
                var expected = unspent.Where(u => u.Value.Script == vault.Script).ToDictionary(u => u.Key, u => u.Value.Sats);
                var held = BtcAttestStore.CoinsOf(vault.Uid).Where(c => c.Status == VaultCoinStatus.Unspent).ToDictionary(c => c.Outpoint, c => c.ValueSats);
                Assert.Equal(expected.OrderBy(e => e.Key), held.OrderBy(e => e.Key));
                Assert.Equal(expected.Values.Sum(), BtcAttestStore.Total(vault.Uid).HeldSats);
            }
        }
    }
}
