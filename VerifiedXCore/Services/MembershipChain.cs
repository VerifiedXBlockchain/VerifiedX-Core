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
        public static List<CasterMembershipRecord>? ChainAt(long height)
        {
            if (!Configured || height <= Globals.MembershipChainHeight) return null;
            var key = $"{height}|{Globals.LastBlock?.Height}|{Globals.LastBlock?.Hash}";
            if (_cache.TryGetValue(key, out var known)) return known;
            var result = Compute(height);
            if (_cache.Count >= CacheSize) _cache.Clear();
            _cache[key] = result;
            return result;
        }

        private static List<CasterMembershipRecord>? Compute(long height)
        {
            var col = Collection();
            if (col == null) return null;
            var rows = col.Query().Where(x => x.Id >= Globals.MembershipChainHeight && x.Id <= height).OrderBy(x => x.Id).ToList();
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
        }

        /// <summary>Tests only: forgets cached answers.</summary>
        internal static void ResetForTests() => _cache.Clear();
    }
}
