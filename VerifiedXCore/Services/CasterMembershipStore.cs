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
        public const string GenesisPrevHash = "GENESIS";

        private static readonly object Mut = new object();
        private static CasterMembershipRecord? _cachedHead;

        /// <summary>Marker preventing a caster from signing two different records at the same seq (equivocation).</summary>
        public class SignedSeqMarker
        {
            public long Id { get; set; }            // = RecordSeq
            public string RecordHash { get; set; } = "";
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
            if (candidate.Casters == null || candidate.Casters.Count == 0)
            { reason = "empty caster set"; return false; }
            if (ComputeRecordHash(candidate) != candidate.RecordHash)
            { reason = "recordHash mismatch"; return false; }

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

        /// <summary>Appends a validated successor of the local head. Returns false (with reason) otherwise.</summary>
        public static bool TryAppend(CasterMembershipRecord candidate, out string reason)
        {
            lock (Mut)
            {
                var head = GetCurrent();
                if (head != null && candidate.RecordSeq <= head.RecordSeq)
                {
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
                if (TryAppend(r, out _))
                    applied++;
            }
            return applied;
        }

        // ── Double-sign protection ───────────────────────────────────────

        /// <summary>
        /// Atomically records that this node signed <paramref name="recordHash"/> at <paramref name="seq"/>.
        /// Returns false when a DIFFERENT hash was already signed at that seq (equivocation refused).
        /// Signing the same record twice is allowed (idempotent retries).
        /// </summary>
        public static bool TryMarkSigned(long seq, string recordHash)
        {
            lock (Mut)
            {
                var col = MarkerCollection();
                if (col == null) return false;
                var existing = col.FindById(seq);
                if (existing != null)
                    return string.Equals(existing.RecordHash, recordHash, StringComparison.OrdinalIgnoreCase);
                col.Insert(seq, new SignedSeqMarker { Id = seq, RecordHash = recordHash });
                return true;
            }
        }

        /// <summary>Test/diagnostic support: clears the in-memory head cache (persisted data untouched).</summary>
        public static void InvalidateCache()
        {
            lock (Mut) { _cachedHead = null; }
        }
    }
}
