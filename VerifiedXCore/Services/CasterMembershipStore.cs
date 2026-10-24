using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;
using System.Security.Cryptography;
using System.Text;

namespace VerifiedXCore.Services
{
    /// <summary>
    /// Wave 3: persistence + validation for the caster membership record chain.
    /// Records live in DB_Config (collection rsrv_caster_membership); full history is kept
    /// (one small doc per rotation). A cached head serves the hot quorum paths.
    ///
    /// RECORD ERA (Wave 6): everything here is inert until a genesis record exists — minted by
    /// the seeds at the coordinated restart's bootstrap agreement (boundary = agreed height + 1)
    /// or adopted from peers, validated by ≥2 hardcoded-seed signatures. Before that,
    /// GetCommitteeForHeight returns null and all quorum sites use the legacy BlockCasters view.
    /// </summary>
    public static class CasterMembershipStore
    {
        private const string COLLECTION_NAME = "rsrv_caster_membership";
        private const string SIGNED_MARKER_COLLECTION = "rsrv_membership_signed";
        private const string RESET_MARKER_COLLECTION = "rsrv_membership_reset";

        /// <summary>Where this node's operator last reset the membership store: the tip at the reset.</summary>
        public class ResetMarker
        {
            public int Id { get; set; } = 1;
            public long ResetAtHeight { get; set; }
        }

        private static LiteDB.ILiteCollection<ResetMarker>? ResetCollection() => DbContext.DB_Config?.GetCollection<ResetMarker>(RESET_MARKER_COLLECTION);

        /// <summary>The tip at this node's last operator reset, or 0 when it never had one.</summary>
        public static long LastOperatorResetHeight() => ResetCollection()?.FindById(1)?.ResetAtHeight ?? 0;
        public const string GenesisPrevHash = "GENESIS";

        private static readonly object Mut = new object();
        private static CasterMembershipRecord? _cachedHead;

        /// <summary>Marker preventing a caster from signing two different records at the same seq (equivocation).</summary>
        public class SignedSeqMarker
        {
            public long Id { get; set; }            // = RecordSeq
            public string RecordHash { get; set; } = "";
            /// <summary>ROTATION-LIVENESS (Sep 2026): identity of the caster SET signed at this seq
            /// (see <see cref="ComputeCasterSetHash(CasterMembershipRecord)"/>). Empty on markers
            /// written before the fix.</summary>
            public string SetHash { get; set; } = "";
            /// <summary>Unix seconds the mark was written (diagnostic).</summary>
            public long SignedAt { get; set; }
            /// <summary>The latest EffectiveFromHeight among the candidates signed at this seq. The mark is released
            /// only once that height is behind the tip (<see cref="IsStaleAbortedMark(SignedSeqMarker, long, long, long, long, long)"/>).
            /// 0 on marks written before it was recorded: those keep the old wall-clock expiry (<see cref="LegacyMarkExpirySeconds"/>).</summary>
            public long EffectiveFromHeight { get; set; }
        }

        /// <summary>
        /// Blocks past a signed candidate's EffectiveFromHeight before its mark is released. A live append refuses a
        /// candidate whose effective height has passed (<see cref="EffectiveHeightHasPassed"/>), so once every node near
        /// the tip is past it the signed candidate can no longer land, and a different set may be signed at the seq.
        /// The margin covers nodes whose tip trails ours.
        /// </summary>
        public const int SignedMarkReleaseMarginBlocks = MaxProposerTipLead;

        /// <summary>The expiry of a mark written before marks recorded an effective height (a one-time migration).</summary>
        public const long LegacyMarkExpirySeconds = 600;

        /// <summary>BOOTSTRAP-RESET (Sep 2026): ChangeType of a record that supersedes a stale committee
        /// with the hardcoded seed set. Signed by a seed majority instead of the previous set, and
        /// accepted by a node ONLY while its own tip is at least <see cref="BootstrapResetMinStallSeconds"/>
        /// old — so a node on a moving chain can never adopt one. See BootstrapResetService.</summary>
        public const string BootstrapResetChangeType = "BootstrapReset";
        /// <summary>SEAT-HEAL (Oct 2026): 30 → 10 minutes. Absent members are now removed after a 5-minute grace period
        /// while a quorum is live; this backstop only covers the case where too few are live to sign a removal.</summary>
        public const long BootstrapResetMinStallSeconds = 600;

        /// <summary>
        /// ROTATION-LIVENESS (Sep 2026): rotation records are built per proposer, and the record
        /// hash covers EffectiveFromHeight (proposer's tip + margin) plus each member's PeerIP /
        /// PublicKey as that proposer knows them. Two casters proposing the SAME change seconds
        /// apart therefore produced different hashes, each locked itself to its own hash at that
        /// seq, nobody reached 3-of-5, and — because every retry is a new height — the committee
        /// deadlocked at seq 3 on mainnet for eleven hours (3922 "already signed a DIFFERENT
        /// record" aborts on one caster). Quantizing the effective height to a block window makes
        /// proposers that see the same tip window produce the same height.
        /// </summary>
        public const int RotationHeightWindow = 10;

        /// <summary>Margin so a new record propagates before it starts governing rounds.</summary>
        public const int RotationEffectiveMargin = 2;

        /// <summary>Pure (tested): the effective height for a rotation proposed at <paramref name="tipHeight"/>
        /// on top of a head effective at <paramref name="headEffectiveFromHeight"/>.</summary>
        public static long ComputeRotationEffectiveHeight(long headEffectiveFromHeight, long tipHeight)
        {
            var minimum = tipHeight + RotationEffectiveMargin;
            var quantized = ((minimum + RotationHeightWindow - 1) / RotationHeightWindow) * RotationHeightWindow;
            return Math.Max(headEffectiveFromHeight + 1, quantized);
        }

        /// <summary>How many blocks a proposer's tip may lead ours and still have its record's effective height signed here.</summary>
        public const int MaxProposerTipLead = RotationHeightWindow;

        /// <summary>
        /// Whether a successor's EffectiveFromHeight is one an honest proposer near our tip builds (rotations and bootstrap
        /// resets both take it from the tip). Only a lower bound was checked, so one signed record far in the future
        /// blocked every later rotation: each successor must be later still. Signer-side only: what a node appends does not
        /// depend on its tip.
        /// </summary>
        public static bool EffectiveHeightWithinReach(long candidateEffective, long headEffective, long tip)
            => candidateEffective <= ComputeRotationEffectiveHeight(headEffective, tip + MaxProposerTipLead);

        /// <summary>Identity of a record's caster SET: sorted addresses only — independent of
        /// EffectiveFromHeight, PeerIP, PublicKey and signatures.</summary>
        public static string ComputeCasterSetHash(CasterMembershipRecord r)
            => ComputeCasterSetHash(r.Casters?.Select(c => c.Address));

        public static string ComputeCasterSetHash(IEnumerable<string?>? addresses)
        {
            var joined = string.Join("|",
                (addresses ?? Enumerable.Empty<string?>())
                    .Where(a => !string.IsNullOrEmpty(a))
                    .Select(a => a!)
                    .OrderBy(a => a, StringComparer.Ordinal));
            using var sha256 = SHA256.Create();
            return Convert.ToHexString(sha256.ComputeHash(Encoding.UTF8.GetBytes("CASTER-SET-V1|" + joined))).ToLowerInvariant();
        }

        internal enum SignedMarkDecision { Insert, Idempotent, SameSetRefresh, LegacyUpgrade, Refuse }

        /// <summary>
        /// Pure (tested) equivocation decision. The guard protects against signing two DIFFERENT
        /// caster sets at one seq (a real committee fork). Re-signing the same set with a different
        /// hash (another proposer's height/IP view of the same change) is allowed. A marker written
        /// before this fix has no SetHash and cannot be compared; it is upgraded to the first new
        /// request's identity — those markers are exactly the deadlock artifact, and any record
        /// they belong to that DID complete elsewhere would have advanced our head already.
        /// </summary>
        internal static SignedMarkDecision EvaluateSignedMarker(SignedSeqMarker? existing, string recordHash, string setHash)
        {
            if (existing == null)
                return SignedMarkDecision.Insert;
            if (string.Equals(existing.RecordHash, recordHash, StringComparison.OrdinalIgnoreCase))
                return SignedMarkDecision.Idempotent;
            if (string.IsNullOrEmpty(existing.SetHash))
                return SignedMarkDecision.LegacyUpgrade;
            if (string.Equals(existing.SetHash, setHash, StringComparison.OrdinalIgnoreCase))
                return SignedMarkDecision.SameSetRefresh;
            return SignedMarkDecision.Refuse;
        }

        /// <summary>
        /// Pure (tested): a "sibling" is a majority-signed record at the SAME seq as our head with the
        /// same predecessor and the same caster set but a different hash (another proposer's view of
        /// the same change). Nodes converge on the lexicographically LOWER hash so the chain stays
        /// unique for the next rotation's PrevRecordHash check.
        /// </summary>
        internal static bool IsCanonicalSibling(CasterMembershipRecord head, CasterMembershipRecord candidate)
        {
            if (head == null || candidate == null) return false;
            if (candidate.RecordSeq != head.RecordSeq || candidate.RecordSeq <= 0) return false;
            if (!string.Equals(candidate.PrevRecordHash, head.PrevRecordHash, StringComparison.OrdinalIgnoreCase)) return false;
            if (string.IsNullOrEmpty(candidate.RecordHash) || string.Equals(candidate.RecordHash, head.RecordHash, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.Equals(ComputeCasterSetHash(candidate), ComputeCasterSetHash(head), StringComparison.OrdinalIgnoreCase)) return false;
            // The same change, and every entry kept: a lower-hash sibling with the same addresses replaced the head whole,
            // so it could put in another PeerIP or PublicKey for a member. Only the promoted member's PeerIP may differ:
            // each proposer reads it from its own view of the candidate, and honest siblings must still converge. A
            // Demotion and a Departure of one member are one change (an outdated member stopped as it was demoted).
            static string Kind(string? change) => change is "Demotion" or "Departure" ? "Removal" : change ?? "";
            if (!string.Equals(Kind(candidate.ChangeType), Kind(head.ChangeType), StringComparison.Ordinal)
                || !string.Equals(candidate.ChangedAddress, head.ChangedAddress, StringComparison.Ordinal)) return false;
            foreach (var c in candidate.Casters ?? new List<CasterInfo>())
            {
                var h = head.Casters?.FirstOrDefault(x => string.Equals(x.Address, c.Address, StringComparison.Ordinal));
                if (h == null || !string.Equals(c.PublicKey, h.PublicKey, StringComparison.Ordinal)) return false;
                var promoted = string.Equals(candidate.ChangeType, "Promotion", StringComparison.Ordinal)
                    && string.Equals(c.Address, candidate.ChangedAddress, StringComparison.Ordinal);
                if (!promoted && !string.Equals(c.PeerIP, h.PeerIP, StringComparison.Ordinal)) return false;
            }
            return string.CompareOrdinal(candidate.RecordHash.ToLowerInvariant(), head.RecordHash.ToLowerInvariant()) < 0;
        }

        /// <summary>Wave 6: minimum hardcoded-seed signatures a genesis record must carry.</summary>
        public const int GenesisMinSeedSignatures = 2;

        /// <summary>
        /// Wave 6: forward margin (in blocks) between the bootstrap AgreedHeight and the genesis
        /// EffectiveFromHeight. Genesis minting is a retry loop that runs while block production
        /// has already resumed; a boundary of AgreedHeight+1 would arm cert enforcement
        /// RETROACTIVELY over cert-less blocks committed during minting, which any node adopting
        /// the record later can never validate (attestations are in-memory only). The margin
        /// keeps the boundary ahead of production for the whole minting window (~45s at ~12s
        /// blocks) so enforcement only ever begins on blocks produced under the installed record.
        /// </summary>
        public const int GenesisBoundaryMargin = 32;

        /// <summary>
        /// Minimum caster-set size any non-genesis record may carry. A committee below 2 can
        /// never legally rotate again (rotations need a majority of the previous set) and
        /// deadlocks the network on restart — a coordinated shutdown's departure cascade must
        /// bottom out here, never at a committee-of-one. Matches GenesisMinSeedSignatures.
        /// </summary>
        public const int MinCommitteeSize = 2;

        private static bool _armed = false;

        /// <summary>
        /// Wave 6: the record era is active once a (seed-signed) genesis record exists in the
        /// store — created automatically by the seeds at the coordinated restart's bootstrap
        /// agreement, or adopted from peers. No manual height, no config.
        /// </summary>
        public static bool RecordEraActive => GetCurrent() != null;

        /// <summary>
        /// Wave 6: arms cert enforcement from the stored genesis record (idempotent). The genesis
        /// EffectiveFromHeight is the boundary between historical cert-free blocks and new-rules
        /// blocks. Called on genesis install/adopt and on first store read after boot.
        /// </summary>
        private static void TryArmFromGenesis()
        {
            if (_armed) return;
            var col = Collection();
            var genesis = col?.FindById(0L);
            if (genesis == null) return;
            Globals.CertEnforceHeight = genesis.EffectiveFromHeight;
            _armed = true;
            CasterLogUtility.Log(
                $"MEMBERSHIP: ARMED from genesis record — record era + cert enforcement active from height {genesis.EffectiveFromHeight}.",
                "MEMBERSHIP");
        }

        private static LiteDB.ILiteCollection<CasterMembershipRecord>? Collection()
        {
            var db = DbContext.DB_Config;
            return db?.GetCollection<CasterMembershipRecord>(COLLECTION_NAME);
        }

        private static LiteDB.ILiteCollection<SignedSeqMarker>? MarkerCollection()
        {
            var db = DbContext.DB_Config;
            return db?.GetCollection<SignedSeqMarker>(SIGNED_MARKER_COLLECTION);
        }

        // ── Canonical payload / hashing ──────────────────────────────────

        public static string CanonicalPayload(CasterMembershipRecord r)
        {
            var sorted = r.Casters.OrderBy(c => c.Address, StringComparer.Ordinal)
                .Select(c => (c.Address, c.PeerIP, c.PublicKey));
            return ConsensusMessageFormatter.FormatCasterMembershipV1(
                r.RecordSeq, r.EffectiveFromHeight, r.PrevRecordHash, r.ChangeType, r.ChangedAddress, sorted);
        }

        public static string ComputeRecordHash(CasterMembershipRecord r)
        {
            using var sha256 = SHA256.Create();
            var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(CanonicalPayload(r)));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        // ── Genesis ──────────────────────────────────────────────────────

        /// <summary>
        /// Wave 6: builds the UNSIGNED genesis record (seq 0) for the given boundary height from
        /// the hardcoded seed list. Deterministic given the height, so every agreeing seed
        /// constructs the identical record (same RecordHash) and their signatures merge.
        /// Validity requires ≥GenesisMinSeedSignatures hardcoded-seed signatures — collected by
        /// the seeds at bootstrap agreement, never self-asserted.
        /// </summary>
        public static CasterMembershipRecord BuildGenesisRecord(long effectiveFromHeight)
        {
            var seeds = SeedNodeService.GetBootstrapSeedPeers()
                .Where(p => !string.IsNullOrEmpty(p.ValidatorAddress))
                .Select(p => new CasterInfo
                {
                    Address = p.ValidatorAddress!,
                    PeerIP = (p.PeerIP ?? "").Replace("::ffff:", ""),
                    PublicKey = p.ValidatorPublicKey ?? ""
                })
                .OrderBy(c => c.Address, StringComparer.Ordinal)
                .ToList();

            var genesis = new CasterMembershipRecord
            {
                RecordSeq = 0,
                EffectiveFromHeight = effectiveFromHeight,
                Casters = seeds,
                PrevRecordHash = GenesisPrevHash,
                ChangeType = "Genesis",
                ChangedAddress = "",
                Signatures = new List<RecordSignature>()
            };
            genesis.RecordHash = ComputeRecordHash(genesis);
            return genesis;
        }

        /// <summary>
        /// Operator disaster-recovery reset (localhost-only ClearMembershipRecord endpoint):
        /// wipes the record chain + signed-seq markers and returns the node to the LEGACY era —
        /// cached head cleared, cert enforcement disarmed (CertEnforceHeight back to MaxValue).
        /// Use when the stored chain no longer reflects the live network (e.g. seeds restarted
        /// holding a genesis record while casters rotated via the legacy path) and the record
        /// era will be re-minted at a future coordinated restart. Returns records removed.
        /// </summary>
        public static int ClearAllForOperatorReset()
        {
            lock (Mut)
            {
                int removed = 0;
                var col = Collection();
                if (col != null)
                    removed = col.DeleteAll();
                MarkerCollection()?.DeleteAll();
                // A genesis from before the reset stays validly signed forever: remembered here, so it cannot re-arm the
                // era this reset was meant to end (TryAppend refuses a genesis that takes effect below it).
                ResetCollection()?.Upsert(new ResetMarker { Id = 1, ResetAtHeight = Globals.LastBlock?.Height ?? 0 });
                _cachedHead = null;
                _armed = false;
                Globals.CertEnforceHeight = long.MaxValue;
                CasterLogUtility.Log(
                    $"MEMBERSHIP: OPERATOR RESET — cleared {removed} record(s) + signed-seq markers; record era inactive, cert enforcement disarmed.",
                    "MEMBERSHIP");
                return removed;
            }
        }

        // ── Reads ────────────────────────────────────────────────────────

        public static CasterMembershipRecord? GetCurrent()
        {
            lock (Mut)
            {
                if (_cachedHead != null) return _cachedHead;
                var col = Collection();
                if (col == null) return null;
                _cachedHead = col.Query().OrderByDescending(x => x.RecordSeq).FirstOrDefault();
                if (_cachedHead != null)
                    TryArmFromGenesis(); // Wave 6: first load after boot re-arms cert enforcement
                return _cachedHead;
            }
        }

        /// <summary>Records with seq &gt; sinceSeq, ordered ascending (for peer catch-up).</summary>
        public static List<CasterMembershipRecord> GetSince(long sinceSeq)
        {
            var col = Collection();
            if (col == null) return new List<CasterMembershipRecord>();
            return col.Query().Where(x => x.RecordSeq > sinceSeq).OrderBy(x => x.RecordSeq).ToList();
        }

        /// <summary>
        /// The record governing <paramref name="height"/>: max EffectiveFromHeight &lt;= height.
        /// Null when the record era is inactive or the height predates the genesis boundary
        /// (legacy fallback — historical blocks stay valid without committees/certs).
        /// </summary>
        public static CasterMembershipRecord? GetForHeight(long height)
        {
            var head = GetCurrent();
            if (head == null) return null;
            if (head.EffectiveFromHeight <= height) return head;
            var col = Collection();
            if (col == null) return null;
            return col.Query().Where(x => x.EffectiveFromHeight <= height)
                .OrderByDescending(x => x.RecordSeq).FirstOrDefault();
        }

        /// <summary>Committee addresses for a height, or null in the legacy era.</summary>
        public static HashSet<string>? GetCommitteeForHeight(long height)
        {
            var rec = GetForHeight(height);
            if (rec == null) return null;
            return rec.Casters.Where(c => !string.IsNullOrEmpty(c.Address))
                .Select(c => c.Address).ToHashSet(StringComparer.Ordinal);
        }

        // ── Validation + append ──────────────────────────────────────────

        /// <summary>
        /// Validates that <paramref name="candidate"/> correctly extends <paramref name="prev"/>:
        /// seq/prevHash chain, advancing effective height, correct RecordHash, and majority
        /// signatures from prev's caster set (skipped for genesis, which is deterministic).
        /// Pure — used by TryAppend, the signing endpoint, and chain verification.
        /// </summary>
        public static bool ValidateSuccessor(CasterMembershipRecord? prev, CasterMembershipRecord candidate, out string reason)
        {
            reason = "";
            if (candidate == null) { reason = "null candidate"; return false; }
            if (FieldHoldingTheSeparator(candidate) is { } field) { reason = $"{field} holds the payload separator '|'"; return false; }

            if (candidate.RecordSeq == 0)
            {
                // Wave 6: genesis is created by the seeds at bootstrap agreement (its boundary
                // height comes from the agreement, so it is not locally recomputable). Validity =
                // structure + caster set == hardcoded seeds + ≥GenesisMinSeedSignatures valid
                // signatures from the hardcoded seed allowlist (the same trust root as bootstrap).
                if (candidate.PrevRecordHash != GenesisPrevHash)
                { reason = "genesis prevHash"; return false; }
                if (candidate.EffectiveFromHeight <= 0)
                { reason = "genesis effective height"; return false; }
                if (ComputeRecordHash(candidate) != candidate.RecordHash)
                { reason = "genesis recordHash mismatch"; return false; }

                var expectedSeeds = SeedNodeService.GetBootstrapSeedPeers()
                    .Select(p => p.ValidatorAddress)
                    .Where(a => !string.IsNullOrEmpty(a))
                    .ToHashSet(StringComparer.Ordinal);
                var candidateSet = (candidate.Casters ?? new List<CasterInfo>())
                    .Select(c => c.Address).ToHashSet(StringComparer.Ordinal);
                if (!expectedSeeds.SetEquals(candidateSet))
                { reason = "genesis caster set is not the hardcoded seed list"; return false; }

                var gPayload = CanonicalPayload(candidate);
                var seedSigners = new HashSet<string>(StringComparer.Ordinal);
                foreach (var sig in candidate.Signatures ?? new List<RecordSignature>())
                {
                    if (string.IsNullOrEmpty(sig.SignerAddress) || string.IsNullOrEmpty(sig.Signature)) continue;
                    if (!Globals.BootstrapCasterAddresses.Contains(sig.SignerAddress)) continue;
                    if (!SignatureService.VerifySignature(sig.SignerAddress, gPayload, sig.Signature)) continue;
                    seedSigners.Add(sig.SignerAddress);
                }
                if (seedSigners.Count < GenesisMinSeedSignatures)
                { reason = $"genesis signatures {seedSigners.Count}/{GenesisMinSeedSignatures} from seed allowlist"; return false; }

                return true;
            }

            if (prev == null) { reason = "no previous record"; return false; }
            if (candidate.RecordSeq != prev.RecordSeq + 1) { reason = $"seq {candidate.RecordSeq} != {prev.RecordSeq + 1}"; return false; }
            if (!string.Equals(candidate.PrevRecordHash, prev.RecordHash, StringComparison.OrdinalIgnoreCase))
            { reason = "prevRecordHash mismatch"; return false; }
            if (candidate.EffectiveFromHeight <= prev.EffectiveFromHeight)
            { reason = "effective height not advancing"; return false; }
            if (candidate.Casters == null || candidate.Casters.Count < MinCommitteeSize)
            { reason = $"caster set below minimum {MinCommitteeSize}"; return false; }
            // COMMITTEE-CAP (Sep 2026, testnet 975,533): the live pool holds at most MaxCasters, so a
            // committee larger than that has a quorum that can NEVER be met from a full pool — seq 15
            // wrote 6 members against a 5-slot pool, quorum 4, and one restart later only 3 were live.
            // A record like that is an unrecoverable state by construction; refuse it at write time.
            if (candidate.Casters.Count > CasterDiscoveryService.MaxCasters)
            { reason = $"caster set {candidate.Casters.Count} exceeds pool cap {CasterDiscoveryService.MaxCasters}"; return false; }
            if (ComputeRecordHash(candidate) != candidate.RecordHash)
            { reason = "recordHash mismatch"; return false; }

            // BOOTSTRAP-RESET: a seed-majority-signed supersession — different signer rule, and
            // only acceptable while OUR tip is provably stale (see ValidateBootstrapReset).
            if (string.Equals(candidate.ChangeType, BootstrapResetChangeType, StringComparison.Ordinal))
                return ValidateBootstrapReset(candidate, TipAgeSeconds(), out reason);

            var prevSet = prev.Casters.Select(c => c.Address).ToHashSet(StringComparer.Ordinal);
            var need = prevSet.Count / 2 + 1;
            var payload = CanonicalPayload(candidate);
            var validSigners = new HashSet<string>(StringComparer.Ordinal);
            foreach (var sig in candidate.Signatures ?? new List<RecordSignature>())
            {
                if (string.IsNullOrEmpty(sig.SignerAddress) || string.IsNullOrEmpty(sig.Signature)) continue;
                if (!prevSet.Contains(sig.SignerAddress)) continue;
                if (!SignatureService.VerifySignature(sig.SignerAddress, payload, sig.Signature)) continue;
                validSigners.Add(sig.SignerAddress);
            }
            if (validSigners.Count < need)
            { reason = $"signatures {validSigners.Count}/{need} from previous set"; return false; }

            return true;
        }

        /// <summary>
        /// The signed payload joins a record's fields with '|' (FormatCasterMembershipV1). A field that holds '|' made two
        /// records with different fields - a longer ChangedAddress and one member fewer, say - the same bytes, so the
        /// signatures on one were valid on the other. No honest field holds it (addresses, IPs, hex keys and hashes, a
        /// change name), so a record with one is refused: with none, the payload reads back as exactly one record.
        /// </summary>
        internal static string? FieldHoldingTheSeparator(CasterMembershipRecord r)
        {
            static bool Has(string? s) => s != null && s.Contains('|');
            if (Has(r.PrevRecordHash)) return "PrevRecordHash";
            if (Has(r.ChangeType)) return "ChangeType";
            if (Has(r.ChangedAddress)) return "ChangedAddress";
            foreach (var c in r.Casters ?? new List<CasterInfo>())
                if (c == null || Has(c.Address) || Has(c.PeerIP) || Has(c.PublicKey)) return "a caster entry";
            return null;
        }

        /// <summary>Seconds since our tip's timestamp; long.MaxValue with no tip (cold start counts as stale).</summary>
        public static long TipAgeSeconds()
        {
            var tip = Globals.LastBlock;
            if (tip == null || tip.Height < 0) return long.MaxValue;
            return Math.Max(0, TimeUtil.GetTime() - tip.Timestamp);
        }

        /// <summary>
        /// BOOTSTRAP-RESET (Sep 2026, testnet 975,533): validity of a record that replaces a committee
        /// whose quorum can no longer be met with the hardcoded seed set. Pure given the tip age.
        ///  • caster set must be exactly the hardcoded seeds (same trust root as genesis);
        ///  • ≥ <see cref="GenesisMinSeedSignatures"/> valid signatures from the seed allowlist — the
        ///    previous set's majority is, by definition, the thing that cannot be reached;
        ///  • <paramref name="tipAgeSeconds"/> ≥ <see cref="BootstrapResetMinStallSeconds"/>: every node
        ///    judges this from its OWN tip, so a node whose chain is moving rejects the reset. That is
        ///    the property that keeps a reset from ever being adopted by a producing partition.
        /// The proposer-side survey (NetworkStallSurvey) decides whether to ATTEMPT one; this decides
        /// whether to ACCEPT one.
        /// </summary>
        public static bool ValidateBootstrapReset(CasterMembershipRecord candidate, long tipAgeSeconds, out string reason)
        {
            reason = "";
            if (tipAgeSeconds < BootstrapResetMinStallSeconds)
            { reason = $"bootstrap reset refused — our tip is only {tipAgeSeconds}s old (need ≥{BootstrapResetMinStallSeconds}s); chain is not stalled here"; return false; }

            var expectedSeeds = SeedNodeService.GetBootstrapSeedPeers()
                .Select(p => p.ValidatorAddress)
                .Where(a => !string.IsNullOrEmpty(a))
                .ToHashSet(StringComparer.Ordinal);
            var candidateSet = (candidate.Casters ?? new List<CasterInfo>())
                .Select(c => c.Address).ToHashSet(StringComparer.Ordinal);
            if (expectedSeeds.Count == 0 || !expectedSeeds.SetEquals(candidateSet))
            { reason = "bootstrap reset caster set is not the hardcoded seed list"; return false; }
            if (candidate.Casters!.Count != candidateSet.Count)
            { reason = "bootstrap reset lists a seed twice"; return false; }
            // Each seed's entry as hardcoded (BootstrapResetService.ProposeAsync builds it so): the set alone let a reset
            // rewrite a seed's PeerIP or PublicKey, and its co-signers check no entry either.
            foreach (var seed in SeedNodeService.GetBootstrapSeedPeers().Where(p => !string.IsNullOrEmpty(p.ValidatorAddress)))
            {
                var entry = candidate.Casters!.First(c => string.Equals(c.Address, seed.ValidatorAddress, StringComparison.Ordinal));
                if (!string.Equals(entry.PeerIP, (seed.PeerIP ?? "").Replace("::ffff:", ""), StringComparison.Ordinal)
                    || !string.Equals(entry.PublicKey, seed.ValidatorPublicKey ?? "", StringComparison.Ordinal))
                { reason = $"bootstrap reset changes seed {seed.ValidatorAddress}'s entry from the hardcoded one"; return false; }
            }

            var payload = CanonicalPayload(candidate);
            var seedSigners = new HashSet<string>(StringComparer.Ordinal);
            foreach (var sig in candidate.Signatures ?? new List<RecordSignature>())
            {
                if (string.IsNullOrEmpty(sig.SignerAddress) || string.IsNullOrEmpty(sig.Signature)) continue;
                if (!Globals.BootstrapCasterAddresses.Contains(sig.SignerAddress)) continue;
                if (!SignatureService.VerifySignature(sig.SignerAddress, payload, sig.Signature)) continue;
                seedSigners.Add(sig.SignerAddress);
            }
            if (seedSigners.Count < GenesisMinSeedSignatures)
            { reason = $"bootstrap reset signatures {seedSigners.Count}/{GenesisMinSeedSignatures} from seeds"; return false; }

            return true;
        }

        /// <summary>
        /// Whether a record's effective height is at or below <paramref name="tip"/>. A record announced live after that
        /// is refused: it would govern blocks already produced, and it may be a signed candidate withheld until its
        /// signers had released their marks and signed another set at the seq.
        /// </summary>
        public static bool EffectiveHeightHasPassed(long effectiveFromHeight, long tip) => tip >= 0 && effectiveFromHeight <= tip;

        /// <summary>
        /// Appends a validated successor of the local head. Returns false (with reason) otherwise. A live append
        /// (announce, the proposer's own) refuses a rotation whose effective height has passed; a record fetched by
        /// membership sync (<paramref name="fromSync"/>) is applied as the peers that hold it applied it, so a lagging
        /// node catches up.
        /// </summary>
        public static bool TryAppend(CasterMembershipRecord candidate, out string reason, bool fromSync = false)
        {
            lock (Mut)
            {
                var head = GetCurrent();
                if (head != null && candidate.RecordSeq <= head.RecordSeq)
                {
                    // ROTATION-LIVENESS: adopt a canonical sibling of our head (same seq, same
                    // predecessor, same caster set, lower hash) when it carries a valid majority.
                    // Membership is unchanged; only the hash chain converges.
                    if (IsCanonicalSibling(head, candidate))
                    {
                        var prevOfHead = Collection()?.FindById(head.RecordSeq - 1);
                        var siblingReason = "no predecessor record";
                        if (prevOfHead != null && ValidateSuccessor(prevOfHead, candidate, out siblingReason))
                        {
                            var scol = Collection();
                            if (scol == null) { reason = "db unavailable"; return false; }
                            scol.Update(candidate.RecordSeq, candidate);
                            _cachedHead = candidate;
                            CasterLogUtility.Log(
                                $"MEMBERSHIP: adopted canonical sibling at seq={candidate.RecordSeq} ({candidate.RecordHash[..Math.Min(16, candidate.RecordHash.Length)]}… replaces {head.RecordHash[..Math.Min(16, head.RecordHash.Length)]}…, same caster set).",
                                "MEMBERSHIP");
                            reason = "sibling adopted";
                            return true;
                        }
                        CasterLogUtility.Log($"MEMBERSHIP: sibling at seq={candidate.RecordSeq} NOT adopted — {siblingReason}.", "MEMBERSHIP");
                    }

                    // Wave 6 FIRST-GENESIS-WINS: a DIFFERENT genesis than ours is loud evidence of
                    // a competing restart era — never silently swallowed.
                    if (candidate.RecordSeq == 0)
                    {
                        var ourGenesis = Collection()?.FindById(0L);
                        if (ourGenesis != null && !string.Equals(ourGenesis.RecordHash, candidate.RecordHash, StringComparison.OrdinalIgnoreCase))
                            CasterLogUtility.Log(
                                $"MEMBERSHIP: REJECTED competing GENESIS (theirs={candidate.RecordHash[..Math.Min(16, candidate.RecordHash.Length)]}… ours={ourGenesis.RecordHash[..Math.Min(16, ourGenesis.RecordHash.Length)]}…). First genesis wins — operator attention required if this repeats.",
                                "MEMBERSHIP");
                    }
                    reason = $"stale seq {candidate.RecordSeq} (head {head.RecordSeq})"; return false;
                }
                if (candidate.RecordSeq == 0 && candidate.EffectiveFromHeight < LastOperatorResetHeight())
                {
                    reason = $"genesis effective at {candidate.EffectiveFromHeight} is from before this node's operator reset at {LastOperatorResetHeight()}";
                    CasterLogUtility.Log($"MEMBERSHIP: REJECTED pre-reset GENESIS — {reason}.", "MEMBERSHIP");
                    return false;
                }
                var tip = Globals.LastBlock?.Height ?? -1;
                if (!fromSync && candidate.RecordSeq > 0 && EffectiveHeightHasPassed(candidate.EffectiveFromHeight, tip))
                {
                    reason = $"effective height {candidate.EffectiveFromHeight} has passed (tip {tip})";
                    return false;
                }
                if (!ValidateSuccessor(head, candidate, out reason))
                    return false;
                var col = Collection();
                if (col == null) { reason = "db unavailable"; return false; }
                col.Insert(candidate.RecordSeq, candidate);
                _cachedHead = candidate;
                CasterLogUtility.Log(
                    $"MEMBERSHIP: appended record seq={candidate.RecordSeq} ({candidate.ChangeType} {candidate.ChangedAddress}) effective={candidate.EffectiveFromHeight} casters=[{string.Join(",", candidate.Casters.Select(c => c.Address))}]",
                    "MEMBERSHIP");
                if (candidate.RecordSeq == 0)
                    TryArmFromGenesis(); // Wave 6: installing genesis arms cert enforcement
                return true;
            }
        }

        /// <summary>
        /// Verifies and appends an ordered chain of records (catch-up). Returns how many were applied.
        /// </summary>
        public static int TryAppendChain(List<CasterMembershipRecord> records)
        {
            var applied = 0;
            foreach (var r in records.OrderBy(x => x.RecordSeq))
            {
                // Wave 6: genesis is appended like any record — validated by seed signatures.
                if (TryAppend(r, out _, fromSync: true))
                    applied++;
            }
            return applied;
        }

        // ── Double-sign protection ───────────────────────────────────────

        /// <summary>
        /// Atomically records that this node signed <paramref name="recordHash"/> (caster set
        /// <paramref name="setHash"/>) at <paramref name="seq"/>. Returns false only when a DIFFERENT
        /// caster set was already signed at that seq (equivocation refused). Re-signing the same
        /// record, or the same set under another proposer's hash, is allowed — see
        /// <see cref="EvaluateSignedMarker"/> for the rationale and the legacy-marker migration.
        /// </summary>
        public static bool TryMarkSigned(long seq, string recordHash, string setHash, long effectiveFromHeight)
        {
            lock (Mut)
            {
                var col = MarkerCollection();
                if (col == null) return false;
                var existing = col.FindById(seq);
                switch (EvaluateSignedMarker(existing, recordHash, setHash))
                {
                    case SignedMarkDecision.Insert:
                        col.Insert(seq, new SignedSeqMarker { Id = seq, RecordHash = recordHash, SetHash = setHash, SignedAt = NowUnix(), EffectiveFromHeight = effectiveFromHeight });
                        return true;
                    case SignedMarkDecision.Idempotent:
                        return true;
                    case SignedMarkDecision.SameSetRefresh:
                        existing!.RecordHash = recordHash;
                        existing.EffectiveFromHeight = Math.Max(existing.EffectiveFromHeight, effectiveFromHeight);
                        col.Update(seq, existing);
                        return true;
                    case SignedMarkDecision.LegacyUpgrade:
                        CasterLogUtility.Log(
                            $"MEMBERSHIP: legacy signed-seq marker at seq {seq} ({existing!.RecordHash[..Math.Min(16, existing.RecordHash.Length)]}…) carries no set identity — upgraded to the current request's caster set (one-time migration).",
                            "MEMBERSHIP");
                        existing.RecordHash = recordHash;
                        existing.SetHash = setHash;
                        existing.EffectiveFromHeight = effectiveFromHeight;
                        col.Update(seq, existing);
                        return true;
                    default:
                        // A mark for a DIFFERENT set at this seq refuses us, until nothing can append the
                        // candidate it marks (its effective height is behind the tip): then it is an aborted
                        // proposal, not an equivocation risk, and is released for the new set.
                        if (existing != null && IsStaleAbortedMark(existing, seq))
                        {
                            CasterLogUtility.Log(
                                $"MEMBERSHIP: signed-seq marker at seq {seq} released — its rotation (effective {existing.EffectiveFromHeight}) never appended and is behind the tip; releasing for a new caster set.",
                                "MEMBERSHIP");
                            existing.RecordHash = recordHash;
                            existing.SetHash = setHash;
                            existing.SignedAt = NowUnix();
                            existing.EffectiveFromHeight = effectiveFromHeight;
                            col.Update(seq, existing);
                            return true;
                        }
                        return false;
                }
            }
        }

        /// <summary>
        /// ROTATION-LIVENESS (Sep 2026): the proposer of an ABORTED rotation releases its own mark.
        /// Safe because only the proposer collects signatures for its candidate — once it gives up,
        /// that record can never be appended by anyone. Only removes a mark for the same set, and
        /// only while the head is still below <paramref name="seq"/> (nothing landed there).
        /// </summary>
        public static void ReleaseSignedMark(long seq, string setHash)
        {
            lock (Mut)
            {
                var col = MarkerCollection();
                if (col == null) return;
                var existing = col.FindById(seq);
                if (existing == null) return;
                if (!string.Equals(existing.SetHash, setHash, StringComparison.Ordinal)) return;
                var head = GetCurrent();
                if (head != null && head.RecordSeq >= seq) return;
                col.Delete(seq);
            }
        }

        private static long NowUnix() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        /// <summary>Whether a mark can be released for a different set: see the pure overload.</summary>
        internal static bool IsStaleAbortedMark(SignedSeqMarker existing, long seq)
        {
            var head = GetCurrent();
            if (head != null && head.RecordSeq >= seq) return false;
            return IsStaleAbortedMark(existing, seq, headSeq: head?.RecordSeq ?? -1, tipHeight: Globals.LastBlock?.Height ?? -1, tipAgeSeconds: TipAgeSeconds(), nowUnix: NowUnix());
        }

        /// <summary>
        /// Pure decision: the mark's rotation never landed (head below seq) and no node near the tip can append it any
        /// more, because its effective height is <see cref="SignedMarkReleaseMarginBlocks"/> behind our tip. While the
        /// chain is stalled (tip at least <see cref="BootstrapResetMinStallSeconds"/> old) the tip cannot pass it, so the
        /// mark is released then too, or an aborted rotation would lock the seq against the removal or bootstrap reset
        /// that ends the stall. A mark written before marks recorded an effective height keeps the wall-clock expiry it
        /// was written under (one with no timestamp either never expires).
        /// </summary>
        internal static bool IsStaleAbortedMark(SignedSeqMarker existing, long seq, long headSeq, long tipHeight, long tipAgeSeconds, long nowUnix)
        {
            if (headSeq >= seq) return false;
            if (tipAgeSeconds >= BootstrapResetMinStallSeconds) return true;
            if (existing.EffectiveFromHeight <= 0)
                return existing.SignedAt > 0 && nowUnix - existing.SignedAt >= LegacyMarkExpirySeconds;
            return tipHeight >= existing.EffectiveFromHeight + SignedMarkReleaseMarginBlocks;
        }

        /// <summary>
        /// Pure: this node has no membership record while a majority (at least two) of the peers it last asked report
        /// one. Without a record certificate enforcement is not armed: the node takes the next block uncertified and
        /// fork choice falls through to lowest-hash-wins. Mainnet 7,562,232 (23 October 2026): 66 of one operator's 70
        /// validators had no record and switched to the uncertified block that way. When peers report none either (the
        /// legacy era, or seeds restarting a chain before they mint the genesis record), this is false.
        /// </summary>
        internal static bool MissingWhilePeersHaveOne(long? localSeq, IReadOnlyList<long> peerHeads)
            => localSeq == null && peerHeads.Count >= 2 && peerHeads.Count(h => h >= 0) * 2 > peerHeads.Count;

        /// <summary>This node, against the record heads its last membership pull observed.</summary>
        internal static bool MissingWhilePeersHaveOne()
            => MissingWhilePeersHaveOne(GetCurrent()?.RecordSeq, CasterDiscoveryService.LastObservedPeerHeads);

        /// <summary>Test/diagnostic support: clears the in-memory head cache (persisted data untouched).</summary>
        public static void InvalidateCache()
        {
            lock (Mut) { _cachedHead = null; }
        }
    }
}
