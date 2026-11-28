using System.Collections.Concurrent;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Services
{
    /// <summary>
    /// The caster committee as the chain carries it. Membership records reach a node apart from the blocks, so two nodes at
    /// one tip could hold different committees and give one block two verdicts. From Globals.MembershipChainHeight every
    /// record rides on a block: promotions, demotions, departures and holds in the block's hashed run
    /// (<see cref="Block.MembershipRun"/>, in force from the next block), elections and seed resets in its carried record
    /// (<see cref="Block.MembershipRecord"/>, in force from that block, tied to it by the new committee's certificate). The
    /// committee for a height is then read from the blocks below it, the same on every node that holds them.
    ///
    /// The chain starts at the first block at or after the height whose run begins with the pinned anchor record
    /// (Globals.MembershipAnchorRecordHash); until then, and for every height up to that block, the records this node
    /// holds decide as before.
    ///
    /// What the chain carried is kept in a small collection next to the blocks, one row per carrying block. A row is
    /// believed only while the block store holds the block it names, so a rollback, a reorg or a restore needs no step of
    /// its own here: a row for a block that is gone is dropped when it is next read, and a block written at that height
    /// replaces it.
    /// </summary>
    public static class MembershipChain
    {
        private const string COLLECTION_NAME = "rsrv_membership_chain";

        /// <summary>How many blocks past its effective height a record may still be carried. Signers keep their mark on it for
        /// as long (CasterMembershipStore.SignedMarkReleaseMarginBlocks), so a record whose signers have let go of it can
        /// no longer land, and two differently signed records at one sequence are never both carryable.</summary>
        public const int CarryWindowBlocks = 30;

        /// <summary>The most records an ordinary block carries.</summary>
        public const int MaxRunRecords = 8;

        /// <summary>The most records the block that starts the chain carries (the anchor and the records signed after it).</summary>
        public const int MaxActivationRunRecords = 64;

        /// <summary>The records a run may carry. Elections and seed resets go in the carried record.</summary>
        public static bool IsRunType(string? changeType) =>
            changeType is "Promotion" or "Demotion" or "Departure" or VreeElection.MaintenanceHoldChangeType;

        /// <summary>
        /// Whether a block at <paramref name="height"/> may still carry <paramref name="record"/> by its effective height:
        /// at most <see cref="CarryWindowBlocks"/> after it. Only while the chain's rules judge that height.
        /// </summary>
        public static bool MayStillCarry(CasterMembershipRecord record, long height) =>
            record != null && ActiveAt(height) && height <= record.EffectiveFromHeight + CarryWindowBlocks;

        /// <summary>The records a block carries in its carried record (Block.MembershipRecord), in force from that block.</summary>
        public static bool IsCarriedType(string? changeType) =>
            changeType is VreeElection.EmergencyElectionChangeType or VreeElection.ScheduledRotationChangeType or CasterMembershipStore.BootstrapResetChangeType;

        /// <summary>One block that carried records.</summary>
        public class ChainRow
        {
            /// <summary>The block's height.</summary>
            public long Id { get; set; }
            public string BlockHash { get; set; } = "";
            /// <summary>True for a run (in force from the next block); false for a carried election or reset (in force from this block).</summary>
            public bool IsRun { get; set; }
            public List<CasterMembershipRecord> Records { get; set; } = new();
        }

        private static readonly ConcurrentDictionary<string, List<CasterMembershipRecord>?> _cache = new(StringComparer.Ordinal);

        /// <summary>The block whose validation is running in this flow (BlockValidatorService), or null.</summary>
        private static readonly AsyncLocal<Block?> _judged = new();

        /// <summary>Whether a block is being judged in this flow: committee reads then come from the chain and that block only.</summary>
        public static bool IsJudging => _judged.Value != null;

        /// <summary>
        /// Marks this flow as judging <paramref name="block"/> until the result is disposed. Every committee read inside it
        /// (the certificate, the transactions' votes and senders) then reads the chain's record, or the election or reset
        /// the block itself carries, and never a record this node holds that the block does not carry.
        /// </summary>
        public static IDisposable Judging(Block? block)
        {
            var prior = _judged.Value;
            _judged.Value = block;
            return new Restore(prior);
        }

        private sealed class Restore : IDisposable
        {
            private readonly Block? _prior;
            public Restore(Block? prior) => _prior = prior;
            public void Dispose() => _judged.Value = _prior;
        }

        /// <summary>
        /// The election or reset the block being judged carries, when it is valid, takes effect at <paramref name="height"/>
        /// (the block's own) and extends <paramref name="onChain"/>; else null.
        /// </summary>
        public static CasterMembershipRecord? CarriedByJudgedBlock(long height, CasterMembershipRecord onChain)
        {
            var block = _judged.Value;
            if (block == null || block.Height != height || block.MembershipRecord is not { } carried || !IsCarriedType(carried.ChangeType)) return null;
            if (carried.RecordSeq != onChain.RecordSeq + 1 || !string.Equals(carried.PrevRecordHash, onChain.RecordHash, StringComparison.OrdinalIgnoreCase)) return null;
            return VreeElection.JudgeCarried(block) == VreeElection.CarriedVerdict.Valid ? carried : null;
        }
        private const int CacheSize = 512;

        private static LiteDB.ILiteCollection<ChainRow>? Collection() => DbContext.DB?.GetCollection<ChainRow>(COLLECTION_NAME);

        /// <summary>Whether the chain's rules can start at all on this network.</summary>
        public static bool Configured => Globals.MembershipChainHeight != long.MaxValue && !string.IsNullOrEmpty(Globals.MembershipAnchorRecordHash);

        // ── Reads ────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The chain's records in force for a block at <paramref name="height"/>, oldest first: the anchor, then every record
        /// the blocks from the chain's first block carried, a run up to the block before <paramref name="height"/> and a
        /// carried record up to <paramref name="height"/> itself. Null when the chain's rules do not judge that height (the
        /// chain has not started below it): the records this node holds decide there.
        /// </summary>
        public static List<CasterMembershipRecord>? ChainAt(long height) => Cached(height, carriedAtHeight: true);

        /// <summary>As <see cref="ChainAt"/>, without what the block at <paramref name="height"/> itself carries: the records a
        /// carried election or reset at that height must extend.</summary>
        public static List<CasterMembershipRecord>? ChainBefore(long height) => Cached(height, carriedAtHeight: false);

        private static List<CasterMembershipRecord>? Cached(long height, bool carriedAtHeight)
        {
            if (!Configured || height <= Globals.MembershipChainHeight) return null;
            var key = $"{height}|{carriedAtHeight}|{Globals.LastBlock?.Height}|{Globals.LastBlock?.Hash}";
            if (_cache.TryGetValue(key, out var known)) return known;
            var result = Compute(carriedAtHeight ? height : height - 1, height);
            if (_cache.Count >= CacheSize) _cache.Clear();
            _cache[key] = result;
            return result;
        }

        /// <summary>The records of the rows up to <paramref name="upTo"/>, a run only below <paramref name="height"/>.</summary>
        private static List<CasterMembershipRecord>? Compute(long upTo, long height)
        {
            if (upTo < Globals.MembershipChainHeight) return null;
            var col = Collection();
            if (col == null) return null;
            var rows = col.Query().Where(x => x.Id >= Globals.MembershipChainHeight && x.Id <= upTo).OrderBy(x => x.Id).ToList();
            var records = new List<CasterMembershipRecord>();
            var started = false;
            foreach (var row in rows)
            {
                if (!Holds(row)) continue;
                if (row.IsRun && row.Id >= height) continue; // a run is in force from the block after it
                if (!started)
                {
                    if (!row.IsRun || row.Records.Count == 0 || !string.Equals(row.Records[0].RecordHash, Globals.MembershipAnchorRecordHash, StringComparison.OrdinalIgnoreCase))
                        continue; // only a run that begins with the anchor starts the chain
                    started = true;
                }
                records.AddRange(row.Records);
            }
            return started ? records : null;
        }

        /// <summary>Whether the block store still holds the block a row names. A row for a block that is gone is dropped.</summary>
        private static bool Holds(ChainRow row)
        {
            var block = BlockchainData.GetBlockByHeight(row.Id);
            if (block != null && string.Equals(block.Hash, row.BlockHash, StringComparison.Ordinal)) return true;
            try { Collection()?.Delete(row.Id); } catch { }
            return false;
        }

        /// <summary>
        /// The heights in (<paramref name="from"/>, <paramref name="to"/>] at which the chain's record in force changes: the
        /// block after one that carried a run, and a block that carried an election or reset. A record's effective height
        /// is only the earliest it may be carried, so reading the record in force at these heights (and at
        /// <paramref name="from"/>) finds every committee that governed a block of the span.
        /// </summary>
        public static List<long> ChangeHeights(long from, long to)
        {
            var col = Collection();
            if (!Configured || col == null || to <= from) return new List<long>();
            return col.Query().Where(x => x.Id >= from && x.Id <= to).OrderBy(x => x.Id).ToList()
                .Where(Holds)
                .Select(row => row.IsRun ? row.Id + 1 : row.Id)
                .Where(h => h > from && h <= to)
                .ToList();
        }

        /// <summary>The record in force for a block at <paramref name="height"/>, or null when the chain does not judge it.</summary>
        public static CasterMembershipRecord? RecordFor(long height) => ChainAt(height)?.LastOrDefault();

        /// <summary>Whether the chain's rules judge a block at <paramref name="height"/>.</summary>
        public static bool ActiveAt(long height) => ChainAt(height) != null;

        /// <summary>The chain's record at a sequence number as of a block at <paramref name="height"/>, or null (below the anchor too).</summary>
        public static Func<long, CasterMembershipRecord?> BySeqAt(long height)
        {
            var chain = ChainAt(height) ?? new List<CasterMembershipRecord>();
            return seq => chain.LastOrDefault(r => r.RecordSeq == seq);
        }

        // ── Judging a run ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Why the run <paramref name="block"/> carries is not one this chain can take at its height, or null. Reads the
        /// block, the blocks below it and nothing else: no clock, no tip, no record this node holds.
        /// </summary>
        public static string? RunRefusal(Block block)
        {
            var run = block.MembershipRun;
            if (run == null || run.Count == 0) return null;
            if (block.Height < Globals.MembershipChainHeight) return null; // not the block's below the height (dropped)
            if (!Configured) return "this network has no membership chain";
            if (block.MembershipRecord != null) return "a block carries either a run or a carried record, not both";
            if (run.Any(r => r == null)) return "an empty entry in the run";

            var governing = RecordFor(block.Height);
            var activation = governing == null;
            if (run.Count > (activation ? MaxActivationRunRecords : MaxRunRecords))
                return $"{run.Count} records, more than the {(activation ? MaxActivationRunRecords : MaxRunRecords)} a block carries";

            CasterMembershipRecord prev;
            var from = 0;
            if (activation)
            {
                // The chain starts here: the run begins with the pinned anchor, trusted by its pin, not by its signatures.
                var anchor = run[0];
                if (!string.Equals(anchor.RecordHash, Globals.MembershipAnchorRecordHash, StringComparison.OrdinalIgnoreCase))
                    return "the chain has not started, and the run does not begin with the anchor record";
                if (CasterMembershipStore.FieldHoldingTheSeparator(anchor) is { } f) return $"the anchor's {f} holds the payload separator";
                if (CasterMembershipStore.ComputeRecordHash(anchor) != anchor.RecordHash) return "the anchor's fields do not hash to its record hash";
                if (anchor.Casters == null || anchor.Casters.Count == 0) return "the anchor has no casters";
                prev = anchor;
                from = 1;
            }
            else prev = governing!;

            var chained = new List<CasterMembershipRecord>(ChainAt(block.Height) ?? new List<CasterMembershipRecord>());
            if (activation) chained.Add(run[0]);
            CasterMembershipRecord? BySeq(long seq) => chained.LastOrDefault(r => r.RecordSeq == seq);
            for (var i = from; i < run.Count; i++)
            {
                var r = run[i];
                if (activation && IsCarriedType(r.ChangeType))
                {
                    // An election or a seed reset that took effect before the chain started is kept, not reverted.
                    if (r.RecordSeq != prev.RecordSeq + 1 || !string.Equals(r.PrevRecordHash, prev.RecordHash, StringComparison.OrdinalIgnoreCase))
                        return $"seq {r.RecordSeq} does not extend the chain's record seq {prev.RecordSeq}";
                    if (PreChainReplacementRefusal(r, prev, block.Height) is { } notKept) return $"seq {r.RecordSeq}: {notKept}";
                    chained.Add(r);
                    prev = r;
                    continue;
                }
                if (!IsRunType(r.ChangeType)) return $"seq {r.RecordSeq}: a {r.ChangeType} record is not carried in a run";
                if (r.RecordSeq != prev.RecordSeq + 1 || !string.Equals(r.PrevRecordHash, prev.RecordHash, StringComparison.OrdinalIgnoreCase))
                    return $"seq {r.RecordSeq} does not extend the chain's record seq {prev.RecordSeq}";
                if (r.EffectiveFromHeight > block.Height) return $"seq {r.RecordSeq} takes effect at {r.EffectiveFromHeight}, after this block";
                // Records signed before the chain started were in force already: the anchor's block carries them at any age.
                if (!activation && block.Height > r.EffectiveFromHeight + CarryWindowBlocks)
                    return $"seq {r.RecordSeq} took effect at {r.EffectiveFromHeight}; a block carries it at most {CarryWindowBlocks} blocks later";
                if (!CasterMembershipStore.ValidateSuccessor(prev, r, out var why, bySeq: BySeq, atHeight: block.Height - 1))
                    return $"seq {r.RecordSeq}: {why}";
                chained.Add(r);
                prev = r;
            }
            return null;
        }

        /// <summary>
        /// Why an election or seed reset in the run that starts the chain is not one that took effect before it, or null. An
        /// election rode on the block at its effective height, which this chain holds: that block must carry exactly this
        /// record. A seed reset rode on no block before the chain: it must be the hard-coded seed set with two seed
        /// signatures (each seed checked the stall on its own clock before signing). Either took effect before this block.
        /// </summary>
        private static string? PreChainReplacementRefusal(CasterMembershipRecord r, CasterMembershipRecord prev, long height)
        {
            if (CasterMembershipStore.FieldHoldingTheSeparator(r) is { } f) return $"its {f} holds the payload separator";
            if (CasterMembershipStore.ComputeRecordHash(r) != r.RecordHash) return "its fields do not hash to its record hash";
            if (r.EffectiveFromHeight >= height) return $"it takes effect at {r.EffectiveFromHeight}, not before this block";
            if (r.EffectiveFromHeight <= prev.EffectiveFromHeight) return "effective height not advancing";
            if (r.ChangeType == CasterMembershipStore.BootstrapResetChangeType)
                return CasterMembershipStore.ValidateBootstrapReset(r, long.MaxValue, out var why) ? null : why;
            if (!VreeElection.Active(r.EffectiveFromHeight)) return "elections are not active at its height";
            var carrier = BlockchainData.GetBlockByHeight(r.EffectiveFromHeight);
            return carrier?.MembershipRecord != null && string.Equals(carrier.MembershipRecord.RecordHash, r.RecordHash, StringComparison.OrdinalIgnoreCase)
                ? null : $"this chain's block at {r.EffectiveFromHeight} does not carry it";
        }

        /// <summary>
        /// A caster's refusal to certify the block that starts the chain when its run would put in force a committee older
        /// than an election or seed reset this node holds: that record cannot be carried later (it rides only on the block at
        /// its effective height), so the chain would start with a committee that may no longer have live signers. The
        /// certifying committee holds what replaced it, so a run that leaves it out is not certified. Null otherwise.
        /// Caster side only (ConsensusCertificateHelper, ConsensusAttestationPublisher): no block verdict reads it.
        /// </summary>
        public static string? ActivationSigningRefusal(Block block)
        {
            if (block?.MembershipRun is not { Count: > 0 } run || !Configured || block.Height < Globals.MembershipChainHeight) return null;
            if (ChainBefore(block.Height) != null) return null; // not the block that starts the chain
            var last = run[^1];
            var held = CasterMembershipStore.HeldForHeight(block.Height);
            if (held == null || held.RecordSeq <= last.RecordSeq) return null;
            for (var seq = last.RecordSeq + 1; seq <= held.RecordSeq; seq++)
                if (CasterMembershipStore.GetBySeq(seq) is { } skipped && IsCarriedType(skipped.ChangeType))
                    return $"the run that starts the membership chain ends at seq {last.RecordSeq} and leaves out the {skipped.ChangeType} seq {skipped.RecordSeq} in force here";
            return null;
        }

        // ── Producing ────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Puts on a block being crafted the records this node holds that extend the chain's record and that the block may
        /// carry (before the chain starts: the anchor and the records after it). Each is judged as a verifier judges it
        /// (<see cref="RunRefusal"/>), and the run stops at the first that would not pass, so the producer never builds a
        /// block its own validation refuses. Nothing held, or nothing that may go yet: no run.
        /// </summary>
        public static void AttachPending(Block block)
        {
            if (block == null) return;
            block.MembershipRun = null;
            if (!Configured || block.Height < Globals.MembershipChainHeight || block.MembershipRecord != null) return;
            try
            {
                var head = RecordFor(block.Height);
                CasterMembershipRecord? prev;
                var run = new List<CasterMembershipRecord>();
                if (head == null)
                {
                    prev = CasterMembershipStore.GetSince(-1).FirstOrDefault(r => string.Equals(r.RecordHash, Globals.MembershipAnchorRecordHash, StringComparison.OrdinalIgnoreCase));
                    if (prev == null) return; // this node does not hold the anchor: another producer starts the chain
                    run.Add(prev);
                    block.MembershipRun = run;
                    if (RunRefusal(block) != null) { block.MembershipRun = null; return; }
                }
                else prev = head;

                var cap = head == null ? MaxActivationRunRecords : MaxRunRecords;
                while (run.Count < cap && CasterMembershipStore.GetBySeq(prev.RecordSeq + 1) is { } next)
                {
                    run.Add(next);
                    block.MembershipRun = run;
                    if (RunRefusal(block) != null) { run.RemoveAt(run.Count - 1); break; }
                    prev = next;
                }
                block.MembershipRun = run.Count > 0 ? run : null;
            }
            catch (Exception ex)
            {
                block.MembershipRun = null;
                ErrorLogUtility.LogError($"Could not put the pending membership records on block {block.Height}: {ex.Message}", "MembershipChain.AttachPending()");
            }
        }

        // ── Writes ───────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Called when a block is written to the store: the records it carries are noted for its height, or a row left at
        /// that height by a block no longer there is dropped. The block was judged first (<see cref="RunRefusal"/>,
        /// VreeElection.PrepareBlockRecord).
        /// </summary>
        public static void NoteCommitted(Block block)
        {
            if (block == null || block.Height < Globals.MembershipChainHeight || !Configured) return;
            var col = Collection();
            if (col == null) return;
            try
            {
                if (block.MembershipRun is { Count: > 0 } run)
                    col.Upsert(new ChainRow { Id = block.Height, BlockHash = block.Hash, IsRun = true, Records = run.ToList() });
                else if (block.MembershipRecord != null && ActiveAt(block.Height) && VreeElection.JudgeCarried(block) == VreeElection.CarriedVerdict.Valid)
                    col.Upsert(new ChainRow { Id = block.Height, BlockHash = block.Hash, IsRun = false, Records = new List<CasterMembershipRecord> { block.MembershipRecord } });
                else
                    col.Delete(block.Height);
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Could not note the membership records of block {block.Height}: {ex.Message}", "MembershipChain.NoteCommitted()");
            }
            _cache.Clear();

            // This node's records follow what the chain now holds, and the casters it works with follow the committee the
            // block's run puts in force from the next block.
            try
            {
                if (ChainAt(block.Height + 1) is { } chain)
                {
                    CasterMembershipStore.FollowChain(chain, block.Height + 1);
                    if (block.MembershipRun is { Count: > 0 })
                        CasterMembershipService.ReconcileBlockCastersToRecord(chain[^1]);
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Could not bring this node's membership records to the chain at block {block.Height}: {ex.Message}", "MembershipChain.NoteCommitted()");
            }
        }

        /// <summary>Tests only: forgets cached answers.</summary>
        internal static void ResetForTests() => _cache.Clear();
    }
}
