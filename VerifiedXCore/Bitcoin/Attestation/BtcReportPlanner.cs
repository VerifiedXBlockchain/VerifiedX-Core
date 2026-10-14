using System.Numerics;
using LiteDB;
using VerifiedXCore.Bitcoin.Attestation.Net;

namespace VerifiedXCore.Bitcoin.Attestation
{
    /// <summary>One report a validator should sign, unsigned.</summary>
    public sealed class BtcPlannedReport
    {
        public const string Replacement = "header replacement";
        public const string Rewind = "rewind";
        public const string Part = "next part";
        public const string Backfill = "backfill";
        public const string Forward = "forward";
        public const string Quiet = "quiet";

        public string Kind { get; init; } = "";
        public BtcReport Report { get; init; } = null!;
    }

    /// <summary>What a validator should sign now, and what it is waiting for.</summary>
    public sealed class BtcPlan
    {
        /// <summary>
        /// The reports to sign: none, one, or two (a backfill and the next forward or quiet report are both true at
        /// once; whichever gathers its signatures first is mined and the other is rebuilt from the new state).
        /// </summary>
        public List<BtcPlannedReport> Reports { get; } = new();
        /// <summary>Why something cannot be built yet (a block not fetched, a view that is behind); null when nothing is waited for.</summary>
        public string? Waiting { get; set; }

        internal BtcPlan With(string kind, BtcReport report)
        {
            Reports.Add(new BtcPlannedReport { Kind = kind, Report = report });
            return this;
        }
    }

    /// <summary>
    /// Decides what a validator offers its signature for (plan 3.4). There is no leader and no proposal: a report is
    /// a function of chain state and of Bitcoin, so every validator that holds the same Bitcoin blocks builds the same
    /// bytes and their signatures meet on one hash.
    ///
    /// <list type="number">
    /// <item>The attested headers are no longer Bitcoin's best chain: the header replacement, and nothing else; or,
    /// when the reorganisation reaches blocks whose events were applied, the rewind. (When a block's report is under
    /// way and that block is still on the chain, its remaining parts come first.)</item>
    /// <item>A block's report is under way: its next part, built from the state the mined parts left, and nothing else.</item>
    /// <item>Otherwise both of these, when they exist:
    ///   <list type="bullet">
    ///   <item>a backfill: an already-reported block holds something unrecorded for a vault (a deposit mined before
    ///   the vault was created), lowest block first;</item>
    ///   <item>the forward report: the first buried block above the events-through height that holds a vault event,
    ///   with the headers through that block plus the depth and no further; or, when a whole quiet range holds none
    ///   and is buried, the quiet report.</item>
    ///   </list></item>
    /// </list>
    /// A block above the events-through height that has not been fetched is never skipped: the answer is to wait.
    ///
    /// The fields of this class are a memo of work already done (which blocks were found empty against which state).
    /// They never change what a report contains; <see cref="Reset"/> forgets them.
    /// </summary>
    public sealed class BtcReportPlanner
    {
        /// <summary>How far below the events-through height a newly started validator looks for unrecorded events.</summary>
        public const int SweepBack = BtcAttestRules.QuietBlocks + 96;

        private readonly Dictionary<long, (string Hash, long Scripts, long Coins)> _empty = new();
        private long _sweepFrom = -1;
        private long _sweepScripts = -1;
        private long _sweepCoins = -1;

        public void Reset()
        {
            _empty.Clear();
            _sweepFrom = -1;
            _sweepScripts = -1;
            _sweepCoins = -1;
        }

        /// <param name="tipHeight">
        /// The height of the VFX tip. Reports are signed under the epoch of the tip, whose signer set is in state; a
        /// report signed just before an epoch ends is still good in the next one.
        /// </param>
        public BtcPlan Plan(IBtcChainView view, long tipHeight, LiteDatabase? db = null)
        {
            db ??= BtcAttestStore.Db;
            var plan = new BtcPlan();
            var meta = BtcAttestStore.GetMeta(db);
            if (meta == null)
            {
                plan.Waiting = "the chain's view of Bitcoin has not started (no checkpoint in state)";
                return plan;
            }
            var epoch = BtcAttestRules.EpochOf(tipHeight);
            var depth = BtcAttestRules.Depth;

            // ── 1. Is the attested tip still on the chain this node follows? ────────────────────────────────
            var mine = view.HeaderAt(meta.TipHeight);
            if (mine == null)
            {
                plan.Waiting = $"this node's Bitcoin headers end at {view.TipHeight}, below the attested tip {meta.TipHeight}";
                return plan;
            }
            if (!string.Equals(mine.Hash, meta.TipHash, StringComparison.Ordinal) && PlanReplacement(plan, view, meta, epoch, db))
                return plan;

            // ── 2. A block's report under way ───────────────────────────────────────────────────────────────
            if (meta.PendingNextPart != 0)
            {
                var block = view.BlockAt(meta.PendingPartBlockHeight);
                if (block == null)
                {
                    plan.Waiting = $"Bitcoin block {meta.PendingPartBlockHeight} is not fetched yet";
                    return plan;
                }
                var rest = BtcBlockScanner.BuildForwardRest(block, meta.PendingPartBlockHeight, meta.PendingLastPosition, meta.PendingNextPart, epoch, db);
                return plan.With(BtcPlannedReport.Part, rest[0]);
            }

            var scripts = MaxStamp(db, BtcAttestStore.Scripts);
            var coins = MaxStamp(db, BtcAttestStore.Coins);

            // ── 3a. Something unrecorded in a block already reported ────────────────────────────────────────
            PlanBackfill(plan, view, meta, epoch, scripts, coins, db);

            // ── 3b. The first buried block above the events-through height with a vault event ───────────────
            // A block is reported once it is buried: `depth` headers that count are above it (on testnet4 a
            // minimum-difficulty header does not count, so the burying header can be further up than h + depth).
            var rules = BtcChainRules.Current;
            var quietThrough = meta.EventsThroughHeight + BtcAttestRules.QuietBlocks;
            var last = meta.EventsThroughHeight;
            for (var h = meta.EventsThroughHeight + 1; h <= quietThrough; h++)
            {
                var buriedAt = rules.BuriedAt(h, view.TipHeight, depth, view.HeaderAt);
                if (buriedAt == null) break; // not buried yet, and so nothing above it is
                last = h;
                var header = view.HeaderAt(h);
                if (header == null)
                {
                    plan.Waiting = $"the header of Bitcoin block {h} is not held";
                    return plan;
                }
                if (_empty.TryGetValue(h, out var memo) && memo.Hash == header.Hash && memo.Scripts == scripts && memo.Coins == coins)
                    continue;

                var block = view.BlockAt(h);
                if (block == null)
                {
                    plan.Waiting = $"Bitcoin block {h} is not fetched yet";
                    return plan;
                }
                var headers = Hexes(view, meta.TipHeight + 1, buriedAt.Value);
                if (headers == null)
                {
                    plan.Waiting = $"the headers through {buriedAt.Value} are not held";
                    return plan;
                }
                var parts = BtcBlockScanner.BuildForward(block, h, headers, epoch, db);
                if (parts.Count > 0) return plan.With(BtcPlannedReport.Forward, parts[0]);
                _empty[h] = (header.Hash, scripts, coins);
            }

            // ── 3c. A whole quiet range, buried, with no event ──────────────────────────────────────────────
            if (last == quietThrough)
            {
                var quietBuriedAt = rules.BuriedAt(last, view.TipHeight, depth, view.HeaderAt)!.Value;
                var headers = Hexes(view, meta.TipHeight + 1, quietBuriedAt);
                if (headers == null)
                {
                    plan.Waiting = $"the headers through {quietBuriedAt} are not held";
                    return plan;
                }
                plan.With(BtcPlannedReport.Quiet, BtcBlockScanner.BuildQuiet(headers, epoch, view.HeaderAt(last)!.Hash));
            }

            foreach (var h in _empty.Keys.Where(k => k <= meta.EventsThroughHeight).ToList()) _empty.Remove(h);
            return plan;
        }

        /// <summary>
        /// The attested headers above some height are not on the chain this node follows. The replacement carries this
        /// node's headers from the last shared height up to the first one at which they outweigh what they replace:
        /// the shortest proof that the other branch is heavier, and the same for every validator on that branch.
        ///
        /// When the last shared height is below something this chain already applied (reported events, or the mined
        /// parts of a block's report that is still under way) it is a rewind instead: the same headers, and the
        /// applied events are taken back.
        ///
        /// Returns false in one case, in which the caller goes on: a block's report is under way and that block is
        /// still on this node's chain. Its remaining parts are built first (they need no new headers); the headers
        /// are replaced once the block is closed.
        /// </summary>
        private static bool PlanReplacement(BtcPlan plan, IBtcChainView view, AttestationMetaRow meta, long epoch, LiteDatabase db)
        {
            long shared = -1;
            for (var h = meta.TipHeight - 1; h >= meta.CheckpointHeight; h--)
            {
                var attested = BtcAttestStore.HeaderAt(h, db);
                if (attested != null && view.HeaderAt(h)?.Hash == attested.Hash) { shared = h; break; }
            }
            if (shared < 0)
            {
                plan.Waiting = $"this node's Bitcoin chain shares no header with the attested chain at or above the checkpoint {meta.CheckpointHeight}";
                return true;
            }
            var pending = meta.PendingNextPart != 0;
            if (pending && shared >= meta.PendingPartBlockHeight)
                return false;
            var rewind = shared < meta.EventsThroughHeight || pending;

            var replaced = BigInteger.Zero;
            for (var h = shared + 1; h <= meta.TipHeight; h++)
                replaced += BtcAttestStore.HeaderAt(h, db)?.Work() ?? BigInteger.Zero;

            var headers = new List<string>();
            var work = BigInteger.Zero;
            for (var h = shared + 1; h <= view.TipHeight && headers.Count < BtcAttestRules.MaxHeaders; h++)
            {
                var header = view.HeaderAt(h)!;
                headers.Add(header.Hex);
                work += header.Work();
                if (work > replaced)
                {
                    if (rewind) plan.With(BtcPlannedReport.Rewind, BtcBlockScanner.BuildRewind(shared, headers, epoch));
                    else plan.With(BtcPlannedReport.Replacement, BtcBlockScanner.BuildHeaderReplacement(shared, headers, epoch));
                    return true;
                }
            }
            plan.Waiting = $"this node's Bitcoin branch above {shared} does not yet carry more work than the attested one";
            return true;
        }

        /// <summary>
        /// Looks through reported blocks for events no report recorded. Only where something can have appeared: from a
        /// newly registered vault's first watched block, or from the block of a coin a backfill has just added (its
        /// spend may sit in a later reported block). A block that is no longer held is queued and the sweep goes on
        /// from it next time; it never holds up the forward report.
        /// </summary>
        private void PlanBackfill(BtcPlan plan, IBtcChainView view, AttestationMetaRow meta, long epoch, long scripts, long coins, LiteDatabase db)
        {
            var floor = Math.Max(meta.CheckpointHeight + 1, meta.EventsThroughHeight - SweepBack);
            if (_sweepFrom < 0)
            {
                _sweepFrom = floor; // first run of this process: what was swept before is not known
            }
            else
            {
                if (scripts > _sweepScripts)
                    foreach (var row in db.GetCollection<VaultDepositScriptRow>(BtcAttestStore.Scripts).Find(x => x.LastModifiedHeight > _sweepScripts))
                        _sweepFrom = Math.Min(_sweepFrom, row.WatchFromBitcoinHeight);
                if (coins > _sweepCoins)
                    foreach (var row in db.GetCollection<VaultCoinRow>(BtcAttestStore.Coins).Find(x => x.LastModifiedHeight > _sweepCoins))
                        if (row.Status == VaultCoinStatus.Unspent && row.CreatedInHeight < meta.EventsThroughHeight)
                            _sweepFrom = Math.Min(_sweepFrom, row.CreatedInHeight);
            }
            _sweepScripts = scripts;
            _sweepCoins = coins;
            _sweepFrom = Math.Max(_sweepFrom, floor);

            while (_sweepFrom <= meta.EventsThroughHeight)
            {
                var block = view.BlockAt(_sweepFrom);
                if (block == null) return;
                foreach (var vault in BtcBlockScanner.VaultsWithUnrecordedEvents(block, _sweepFrom, db))
                {
                    var report = BtcBlockScanner.BuildBackfill(block, _sweepFrom, vault, epoch, db).FirstOrDefault();
                    if (report == null) continue;
                    plan.With(BtcPlannedReport.Backfill, report);
                    return;
                }
                _sweepFrom++;
            }
        }

        private static List<string>? Hexes(IBtcChainView view, long from, long to)
        {
            var result = new List<string>();
            for (var h = from; h <= to; h++)
            {
                var header = view.HeaderAt(h);
                if (header == null) return null;
                result.Add(header.Hex);
            }
            return result;
        }

        /// <summary>The newest write stamp of a table: one index lookup, whatever the table's size.</summary>
        private static long MaxStamp(LiteDatabase db, string collection)
        {
            var newest = db.GetCollection(collection).Query().OrderByDescending("LastModifiedHeight").Limit(1).FirstOrDefault();
            return newest == null ? 0 : newest["LastModifiedHeight"].AsInt64;
        }
    }
}
