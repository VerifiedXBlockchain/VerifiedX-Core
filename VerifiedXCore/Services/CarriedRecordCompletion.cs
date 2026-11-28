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

        /// <summary>The most records one responder's answers take at one height, so no one peer fills a height.</summary>
        public const int MaxPerSourcePerHeight = 2;

        /// <summary>The most new records of one responder's answer whose signatures are checked, per pull.</summary>
        public const int MaxCheckedPerSource = 4;

        /// <summary>The copies without their record refused at one height before the block is taken as it is.</summary>
        public const int MaxIncompleteCopies = 5;

        private static readonly object _lock = new();
        private static readonly SortedDictionary<long, List<(CasterMembershipRecord Record, string Source)>> _noted = new();
        private static readonly Dictionary<long, (int Copies, HashSet<string> Sources)> _incomplete = new();

        /// <summary>The source a block staged by the downloader is validated under (BlockValidatorService.ValidateBlocks).</summary>
        public static string DownloadSource(string? ip) => $"BlockDict:{ip}";

        /// <summary>
        /// Keeps the carried records among <paramref name="records"/>, one responder's answer to a membership sync, that take
        /// effect above this node's tip, whose fields hash to their record hash and whose every signature is its signer's
        /// over the record (a seed reset: the seeds' set and signatures, which need no block). Whether the signers ratify it
        /// needs the chain up to the block that carries it, and is judged there. A responder's copies take at most
        /// <see cref="MaxPerSourcePerHeight"/> places at a height, and a copy judged not valid on its block gives its place up.
        /// </summary>
        public static void NotePeerRecords(string? source, IEnumerable<CasterMembershipRecord?>? records)
        {
            if (records == null) return;
            source ??= "unknown";
            var tip = Globals.LastBlock?.Height ?? -1;
            var candidates = new List<CasterMembershipRecord>();
            lock (_lock)
            {
                PruneLocked(tip);
                foreach (var r in records)
                {
                    if (candidates.Count >= MaxCheckedPerSource) break;
                    if (r == null || !MembershipChain.IsCarriedType(r.ChangeType) || r.EffectiveFromHeight <= tip || !VreeElection.Active(r.EffectiveFromHeight)) continue;
                    if (r.Casters == null || CasterMembershipStore.FieldHoldingTheSeparator(r) != null || CasterMembershipStore.ComputeRecordHash(r) != r.RecordHash) continue;
                    if (!HasRoomLocked(r, source)) continue;
                    candidates.Add(r);
                }
            }

            // Signatures are checked outside the lock: they are the costly part, and only a few per responder are checked.
            var signed = candidates.Where(r => SignatureRefusal(r) == null).ToList();

            lock (_lock)
            {
                foreach (var r in signed)
                {
                    if (!HasRoomLocked(r, source)) continue;
                    if (_noted.Values.Sum(l => l.Count) >= MaxNoted)
                    {
                        // The nearest heights are the ones a download reaches first: the farthest one gives way.
                        var farthest = _noted.Keys.Last();
                        if (farthest <= r.EffectiveFromHeight) continue;
                        var list = _noted[farthest];
                        list.RemoveAt(list.Count - 1);
                        if (list.Count == 0) _noted.Remove(farthest);
                    }
                    if (!_noted.TryGetValue(r.EffectiveFromHeight, out var at)) _noted[r.EffectiveFromHeight] = at = new();
                    at.Add((r, source));
                }
            }
        }

        /// <summary>Whether the copy is new at its height and both the height and the responder have a place left for it.</summary>
        private static bool HasRoomLocked(CasterMembershipRecord r, string source)
        {
            if (!_noted.TryGetValue(r.EffectiveFromHeight, out var at)) return true;
            var key = Key(r);
            return at.Count < MaxPerHeight && !at.Any(x => Key(x.Record) == key) && at.Count(x => x.Source == source) < MaxPerSourcePerHeight;
        }

        /// <summary>
        /// Why a copy's signatures are not all its signers' own over the record, or null. A seed reset is judged whole: the
        /// hard-coded seed set and two seed signatures. Stops at the first bad signature.
        /// </summary>
        internal static string? SignatureRefusal(CasterMembershipRecord r)
        {
            if (r.ChangeType == CasterMembershipStore.BootstrapResetChangeType)
                return CasterMembershipStore.ValidateBootstrapReset(r, long.MaxValue, out var why) ? null : why;
            var signatures = r.Signatures ?? new List<RecordSignature>();
            if (signatures.Count == 0) return "no signatures";
            var payload = CasterMembershipStore.CanonicalPayload(r);
            var signers = new HashSet<string>(StringComparer.Ordinal);
            foreach (var sig in signatures)
            {
                if (string.IsNullOrEmpty(sig?.SignerAddress) || string.IsNullOrEmpty(sig.Signature)) return "an empty signature";
                if (!signers.Add(sig.SignerAddress)) return $"two signatures from {sig.SignerAddress}";
                if (!SignatureService.VerifySignature(sig.SignerAddress, payload, sig.Signature)) return $"an invalid signature from {sig.SignerAddress}";
            }
            return null;
        }

        /// <summary>A copy judged not valid on the block at its height gives its place up, for a copy a later pull brings.</summary>
        private static void Forget(long height, CasterMembershipRecord r)
        {
            lock (_lock)
            {
                if (!_noted.TryGetValue(height, out var at)) return;
                at.RemoveAll(x => ReferenceEquals(x.Record, r));
                if (at.Count == 0) _noted.Remove(height);
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
                if (!JudgedValid(withRecord)) continue;
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
            if (!noted.Select(r => JudgedValid(WithRecord(block, r))).ToList().Contains(true)) return false; // every copy judged
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
            lock (_lock) return _noted.TryGetValue(height, out var list) ? list.Select(x => x.Record).ToList() : new List<CasterMembershipRecord>();
        }

        /// <summary>Whether the record on <paramref name="withRecord"/> judges valid there; one judged not valid is forgotten.</summary>
        private static bool JudgedValid(Block withRecord)
        {
            var verdict = VreeElection.JudgeCarried(withRecord);
            if (verdict == VreeElection.CarriedVerdict.Invalid) Forget(withRecord.Height, withRecord.MembershipRecord!);
            return verdict == VreeElection.CarriedVerdict.Valid;
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
