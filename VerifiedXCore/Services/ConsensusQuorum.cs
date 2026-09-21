using VerifiedXCore.Nodes;

namespace VerifiedXCore.Services
{
    /// <summary>
    /// ONE-QUORUM (Sep 2026, testnet 975,533): the single source of truth for "how many casters must
    /// agree at height H". Every consensus gate — attestation count, block-hash agreement, winner
    /// agreement, proof-set agreement, proof exchange, readiness — derives from here.
    ///
    /// Why this exists: before this class there were two rules over the same height. Block-hash
    /// agreement used committee majority (4 of 6), while the attestation gate that publishes the
    /// "caster-approved" hash took a bootstrap branch (majority of the agreed seeds → 2). Two rules
    /// means two blocks at one height can each be legitimately "approved" — which is exactly how
    /// 975,533 forked (ab9d89a1 attested by 2 seeds, 02179c3c hash-agreed by 3 casters, neither at
    /// the committee's 4). A quorum that only some nodes relax is a fork generator, so the bootstrap
    /// relaxation is gone: bootstrap seeds meet the same majority as everyone else. Liveness when
    /// the committee itself cannot be met is the job of heal (record members re-added), the
    /// committee cap (quorum ≤ 3 of a 5-slot pool), and the operator/bootstrap reset — never of a
    /// weaker per-node rule.
    ///
    /// Legacy era (no membership record): the denominator is <see cref="BlockcasterNode.LegacyQuorumDenominator(int)"/>
    /// over DISTINCT caster addresses in the live bag (SPLIT-GUARD floor applies).
    /// </summary>
    public static class ConsensusQuorum
    {
        /// <summary>Pure: majority of <paramref name="denominator"/>, floored at 1. A non-positive
        /// denominator is unsatisfiable (int.MaxValue) — callers special-case ≤1 committees before this.</summary>
        public static int Required(int denominator) =>
            denominator <= 0 ? int.MaxValue : Math.Max(1, denominator / 2 + 1);

        /// <summary>N for height: committee size in the record era, legacy bag denominator otherwise.</summary>
        public static int DenominatorForHeight(long height)
        {
            var committee = CasterMembershipStore.GetCommitteeForHeight(height);
            if (committee != null)
                return committee.Count;
            // Legacy era: distinct live casters; with an empty bag fall back to the broader
            // BlockCasters ∪ KnownCasters view (what the attestation path used before), so a node
            // that has not populated its bag yet is not handed an unsatisfiable quorum.
            var live = ConsensusCertificateVerifier.OperationalBlockCasterCount();
            if (live == 0)
                live = ConsensusCertificateVerifier.AttestorSetForHeight(height).Count;
            return BlockcasterNode.LegacyQuorumDenominator(live);
        }

        /// <summary>M for height — the number every gate at this height must reach.</summary>
        public static int RequiredForHeight(long height) => Required(DenominatorForHeight(height));
    }
}
