using VerifiedXCore.Models;
using VerifiedXCore.Services;

namespace VerifiedXCore.Utilities
{
    /// <summary>
    /// Wave 5: the deterministic fork-choice rule (pure — no I/O, fully unit-testable).
    /// At the divergence height, in priority order:
    ///   1. Certificate weight — count of VALID attestations from committee members
    ///      (applicable only when BOTH candidates are cert-era; pre-Wave-4 the rule
    ///      collapses cleanly to 2+3, and a cert-bearing block never auto-beats a
    ///      cert-absent one across the boundary).
    ///   2. Height — the taller branch wins (reorg is capped at MAX_REORG_DEPTH, and
    ///      rule 1 dominates once certs are live, so height-grinding isn't meaningful).
    ///   3. Lowest ordinal hash — identical semantics to BlockDownloadService's
    ///      pre-commit SelectCanonicalBlock, so pre- and post-commit choices agree.
    /// Two distinct hashes always produce a strict winner — a tie means the same block,
    /// i.e. no fork existed (treated as LocalWins / no-op, defensively).
    /// </summary>
    public static class ForkChoiceUtility
    {
        /// <summary>Single reorg bound (blocks). Matches the HEIGHT-GAP cap and the snapshot-slot
        /// guarantee (H-10 slot always covers a ≤10 rollback). Deeper forks are FORK-TOO-DEEP →
        /// operator playbook (snapshot-anchor restore), never an automatic deep reorg.</summary>
        public const int MAX_REORG_DEPTH = 10;

        public enum Outcome { LocalWins, RemoteWins }

        public class BranchSummary
        {
            public long TipHeight { get; set; }
            public string HashAtDivergence { get; set; } = "";
            public int CertWeight { get; set; }
            public bool CertRuleApplicable { get; set; }
        }

        public static Outcome CompareBranches(BranchSummary local, BranchSummary remote)
        {
            if (local == null || remote == null)
                return Outcome.LocalWins;

            // Defensive: identical hash at divergence = same block = no fork.
            if (string.Equals(local.HashAtDivergence, remote.HashAtDivergence, StringComparison.Ordinal))
                return Outcome.LocalWins;

            // Rule 1: certificate weight (both sides must be cert-era).
            if (local.CertRuleApplicable && remote.CertRuleApplicable && local.CertWeight != remote.CertWeight)
                return local.CertWeight > remote.CertWeight ? Outcome.LocalWins : Outcome.RemoteWins;

            // Rule 2: height.
            if (local.TipHeight != remote.TipHeight)
                return local.TipHeight > remote.TipHeight ? Outcome.LocalWins : Outcome.RemoteWins;

            // Rule 3: lowest ordinal hash — a strict total order, never a tie for distinct hashes.
            return string.CompareOrdinal(local.HashAtDivergence, remote.HashAtDivergence) < 0
                ? Outcome.LocalWins
                : Outcome.RemoteWins;
        }

        /// <summary>
        /// Counts VALID attestations on a block's certificate: signer in the committee for the
        /// height, signature verifies against the canonical payload. 0 for null/cert-less blocks.
        /// </summary>
        public static int CountValidCertWeight(Block? block)
        {
            if (block?.ConsensusCertificate?.Attestations == null)
                return 0;
            var attestorSet = ConsensusCertificateVerifier.AttestorSetForHeight(block.Height);
            var msg = ConsensusMessageFormatter.FormatAttestationV1(block.Height, block.Hash, block.Validator, block.PrevHash);
            var signers = new HashSet<string>(StringComparer.Ordinal);
            foreach (var a in block.ConsensusCertificate.Attestations)
            {
                if (string.IsNullOrEmpty(a.CasterAddress) || signers.Contains(a.CasterAddress)) continue;
                if (!attestorSet.Contains(a.CasterAddress)) continue;
                if (!SignatureService.VerifySignature(a.CasterAddress, msg, a.Signature)) continue;
                signers.Add(a.CasterAddress);
            }
            return signers.Count;
        }

        /// <summary>True when the cert rule applies to a block at this height (cert-era + supported version).</summary>
        public static bool IsCertRuleApplicable(long height, int blockVersion)
            => height >= Globals.CertEnforceHeight && ConsensusCertificateRules.SupportsConsensusCertificate(blockVersion);
    }
}
