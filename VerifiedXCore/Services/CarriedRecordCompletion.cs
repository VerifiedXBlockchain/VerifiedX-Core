using VerifiedXCore.Models;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Services
{
    /// <summary>
    /// The election, rotation or seed reset a block carries (Block.MembershipRecord) is outside the block's hash, like its
    /// certificate, so a copy of the block passed on without either keeps its hash. On the download path a block behind the
    /// tip is not asked for its certificate, so such a copy was taken as it was and the node kept the committee the record
    /// replaced: it stopped at the tip, and from the membership chain's start for good, since the chain reads the committee
    /// from what its stored blocks carry.
    ///
    /// This keeps the carried records that peers hold for heights above this node's tip, as membership sync returns them.
    /// They are claims, and are used two ways only:
    ///  - <see cref="TryComplete"/>: a copy whose certificate does not hold under the committee in force without a record,
    ///    and does hold under exactly one such record that judges valid on the block, is that record's block. The record
    ///    is put back on it. A certificate that holds without a record leaves the block as it is.
    ///  - <see cref="RefuseIncomplete"/>: on the download path, a copy without a record whose certificate does not hold, at
    ///    a height where peers hold a record that judges valid on it, is not taken from that source, and the downloader asks
    ///    another (<see cref="PreferSource"/>). After <see cref="MaxIncompleteCopies"/> such copies at one height the block
    ///    is taken as it was before, so a node whose peers all serve it that way still moves on.
    /// </summary>
    public static class CarriedRecordCompletion
    {
        /// <summary>The most records kept for one height (rungs of one election, copies with other signatures).</summary>
        public const int MaxPerHeight = 8;

        /// <summary>The most records kept in all.</summary>
        public const int MaxNoted = 64;

        /// <summary>The copies without their record refused at one height before the block is taken as it is.</summary>
        public const int MaxIncompleteCopies = 5;

        private static readonly object _lock = new();
        private static readonly SortedDictionary<long, List<CasterMembershipRecord>> _noted = new();
        private static readonly Dictionary<long, (int Copies, HashSet<string> Sources)> _incomplete = new();

        /// <summary>The source a block staged by the downloader is validated under (BlockValidatorService.ValidateBlocks).</summary>
        public static string DownloadSource(string? ip) => $"BlockDict:{ip}";

        /// <summary>
        /// Keeps the carried records among <paramref name="records"/> (a membership sync's answers) that take effect above
        /// this node's tip and whose fields hash to their record hash. Nothing is judged here: a record is judged on the
        /// block it would be carried by, when that block arrives.
        /// </summary>
        public static void NotePeerRecords(IEnumerable<CasterMembershipRecord?>? records)
        {
            if (records == null) return;
            var tip = Globals.LastBlock?.Height ?? -1;
            lock (_lock)
            {
                PruneLocked(tip);
                foreach (var r in records)
                {
                    if (r == null || !MembershipChain.IsCarriedType(r.ChangeType) || r.EffectiveFromHeight <= tip || !VreeElection.Active(r.EffectiveFromHeight)) continue;
                    if (r.Casters == null || CasterMembershipStore.FieldHoldingTheSeparator(r) != null || CasterMembershipStore.ComputeRecordHash(r) != r.RecordHash) continue;
                    var key = Key(r);
                    if (_noted.TryGetValue(r.EffectiveFromHeight, out var at) && (at.Count >= MaxPerHeight || at.Any(x => Key(x) == key))) continue;
                    if (_noted.Values.Sum(l => l.Count) >= MaxNoted)
                    {
                        // The nearest heights are the ones a download reaches first: the farthest one gives way.
                        var farthest = _noted.Keys.Last();
                        if (farthest <= r.EffectiveFromHeight) continue;
                        var list = _noted[farthest];
                        list.RemoveAt(list.Count - 1);
                        if (list.Count == 0) _noted.Remove(farthest);
                    }
                    if (!_noted.TryGetValue(r.EffectiveFromHeight, out at)) _noted[r.EffectiveFromHeight] = at = new List<CasterMembershipRecord>();
                    at.Add(r);
                }
            }
        }

        /// <summary>
        /// Puts back on <paramref name="block"/> the record its own certificate was signed under, when the block carries
        /// none: its certificate does not hold under the committee in force without a record, and holds under exactly one
        /// record peers hold that judges valid on the block. Any other block is left as it is, so no record a node merely
        /// holds changes a verdict: the block taken is the one its certifying committee signed. Returns whether a record
        /// was put back.
        /// </summary>
        public static bool TryComplete(Block block)
        {
            if (block == null || block.MembershipRecord != null || block.MembershipRun is { Count: > 0 } || block.ConsensusCertificate == null) return false;
            var noted = NotedAt(block.Height);
            if (noted.Count == 0) return false;
            if (ConsensusCertificateVerifier.VerifyOrNotRequired(block)) return false; // its certificate holds without a record

            CasterMembershipRecord? proven = null;
            foreach (var r in noted)
            {
                var withRecord = WithRecord(block, r);
                if (VreeElection.JudgeCarried(withRecord) != VreeElection.CarriedVerdict.Valid) continue;
                if (!ConsensusCertificateVerifier.HasValidCertificate(withRecord)) continue;
                if (proven != null && !string.Equals(proven.RecordHash, r.RecordHash, StringComparison.OrdinalIgnoreCase))
                    return false; // two records would explain the certificate: neither is the block's for certain
                proven ??= r;
            }
            if (proven == null) return false;
            block.MembershipRecord = proven;
            CasterLogUtility.Log($"VREE: block {block.Height} arrived without the {proven.ChangeType} record seq {proven.RecordSeq} its certificate was signed under; put back.", "VREE");
            return true;
        }

        /// <summary>
        /// Whether a downloaded copy of <paramref name="block"/> from <paramref name="source"/> is incomplete and is not to be
        /// taken: it carries no record, its certificate does not hold, and peers hold a record that judges valid on it (the
        /// block that starts that record's committee). Counted; from the <see cref="MaxIncompleteCopies"/>th copy at its
        /// height on, false. A certificate that holds without a record is no sign of a missing one.
        /// </summary>
        public static bool RefuseIncomplete(Block block, string? source)
        {
            if (block == null || block.MembershipRecord != null || block.MembershipRun is { Count: > 0 }) return false;
            var noted = NotedAt(block.Height);
            if (noted.Count == 0) return false;
            if (ConsensusCertificateVerifier.VerifyOrNotRequired(block)) return false;
            if (!noted.Any(r => VreeElection.JudgeCarried(WithRecord(block, r)) == VreeElection.CarriedVerdict.Valid)) return false;
            lock (_lock)
            {
                PruneLocked(Globals.LastBlock?.Height ?? -1);
                var entry = _incomplete.TryGetValue(block.Height, out var e) ? e : (Copies: 0, Sources: new HashSet<string>(StringComparer.Ordinal));
                if (entry.Copies >= MaxIncompleteCopies) return false;
                entry.Copies++;
                entry.Sources.Add(source ?? "unknown");
                _incomplete[block.Height] = entry;
                CasterLogUtility.Log($"VREE: a copy of block {block.Height} from {source ?? "unknown"} has neither the record peers hold for it nor a certificate that holds without one; another is fetched ({entry.Copies}/{MaxIncompleteCopies}).", "VREE");
                if (entry.Copies == MaxIncompleteCopies)
                    CasterLogUtility.Log($"VREE: {MaxIncompleteCopies} copies of block {block.Height} came that way; the next is taken as it is.", "VREE");
                return true;
            }
        }

        /// <summary>Whether <paramref name="source"/> served a copy of the block at <paramref name="height"/> that was refused as incomplete.</summary>
        public static bool ServedIncomplete(long height, string source)
        {
            lock (_lock) return _incomplete.TryGetValue(height, out var e) && e.Sources.Contains(source);
        }

        /// <summary>
        /// The peer to fetch the block at <paramref name="height"/> from: <paramref name="usual"/>, unless it served an
        /// incomplete copy there and one of <paramref name="candidates"/> has not, highest first.
        /// </summary>
        public static NodeInfo PreferSource(long height, NodeInfo usual, IEnumerable<NodeInfo> candidates)
        {
            if (usual == null || !ServedIncomplete(height, DownloadSource(usual.NodeIP))) return usual!;
            return candidates.Where(n => n != null && !ServedIncomplete(height, DownloadSource(n.NodeIP)))
                .OrderByDescending(n => n.NodeHeight).FirstOrDefault() ?? usual;
        }

        private static List<CasterMembershipRecord> NotedAt(long height)
        {
            lock (_lock) return _noted.TryGetValue(height, out var list) ? list.ToList() : new List<CasterMembershipRecord>();
        }

        private static Block WithRecord(Block block, CasterMembershipRecord r) => new()
        {
            Height = block.Height, Hash = block.Hash, PrevHash = block.PrevHash, Validator = block.Validator, Version = block.Version,
            Timestamp = block.Timestamp, ConsensusCertificate = block.ConsensusCertificate, MembershipRecord = r,
        };

        private static string Key(CasterMembershipRecord r) =>
            r.RecordHash + "|" + string.Join(",", (r.Signatures ?? new List<RecordSignature>()).Select(s => $"{s?.SignerAddress}:{s?.Signature}"));

        private static void PruneLocked(long tip)
        {
            foreach (var h in _noted.Keys.Where(h => h <= tip).ToList()) _noted.Remove(h);
            foreach (var h in _incomplete.Keys.Where(h => h <= tip).ToList()) _incomplete.Remove(h);
        }

        /// <summary>Tests only.</summary>
        internal static void ResetForTests()
        {
            lock (_lock) { _noted.Clear(); _incomplete.Clear(); }
        }
    }
}
