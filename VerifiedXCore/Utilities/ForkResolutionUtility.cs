namespace VerifiedXCore.Utilities
{
    /// <summary>
    /// STALL-RESOLVE: pure, unit-tested helpers behind <see cref="Services.ForkDetectionService"/>.
    ///
    /// Motivation (testnet, Sep 2026): four validators committed a different block 912,064 than
    /// the rest of the network and sat on that dead branch for almost two days. Both automatic
    /// paths were structurally unable to cross the split once the majority had moved on:
    /// the fork-choice fetch used a tip-100 windowed endpoint, the rollback path re-downloaded
    /// the bad block from fellow stranded peers, and the escalation ladder ended in a permanent
    /// "restart with rebuildstate" state. These helpers hold the decision logic for the rewrite:
    /// caster-weighted majority vote, unbounded divergence search, stall override and backoff.
    /// </summary>
    public static class ForkResolutionUtility
    {
        /// <summary>Tip unchanged for at least this long while peers are ahead = stalled.</summary>
        public const int STALL_SECONDS = 90;

        /// <summary>Peers must be at least this many blocks ahead before a stall is declared.</summary>
        public const int STALL_MIN_PEER_LEAD = 3;

        /// <summary>Heights probed linearly below the tip before switching to binary search.</summary>
        public const int LINEAR_SEARCH_WINDOW = ForkChoiceUtility.MAX_REORG_DEPTH;

        /// <summary>Hard cap on the probe fan-out per resolution attempt.</summary>
        public const int MAX_PROBE_PEERS = 64;

        /// <summary>Minimum caster responders for the caster-weighted rule to be decisive.</summary>
        public const int MIN_CASTER_RESPONDERS = 2;

        public sealed class ProbeResult
        {
            public string Ip { get; init; } = "";
            public string Hash { get; init; } = "";
            public bool IsCaster { get; init; }
        }

        public sealed class MajorityVerdict
        {
            /// <summary>True when the responders say our hash at our tip height is the minority.</summary>
            public bool IsMinority { get; init; }
            /// <summary>The hash the majority holds at our tip height (empty when not minority).</summary>
            public string MajorityHash { get; init; } = "";
            /// <summary>Peers that hold the majority hash — the only allowed download sources.</summary>
            public List<string> MajorityPeers { get; init; } = new();
            /// <summary>Peers that hold OUR hash — quarantined as download sources during recovery.</summary>
            public List<string> AgreeingPeers { get; init; } = new();
            /// <summary>Which rule decided: "casters", "all", or "none".</summary>
            public string DecidedBy { get; init; } = "none";
            public int Responders { get; init; }
            public int Differing { get; init; }
        }

        /// <summary>Pure: strict majority — differing responders must exceed half.</summary>
        public static bool IsStrictMajority(int differingCount, int totalResponders)
            => totalResponders > 0 && differingCount * 2 > totalResponders;

        /// <summary>
        /// Caster-weighted majority vote at our committed tip height.
        /// 1) If at least <see cref="MIN_CASTER_RESPONDERS"/> casters answered, a strict majority of
        ///    CASTERS disagreeing with us is decisive on its own (they produce the chain).
        /// 2) Otherwise a strict majority of ALL responders disagreeing decides.
        /// The majority hash is the most common differing hash within the deciding set.
        /// Peers holding our own hash are reported so recovery can quarantine them as sources.
        /// </summary>
        public static MajorityVerdict ComputeMajority(IReadOnlyList<ProbeResult> results, string myHash)
        {
            var valid = results.Where(r => !string.IsNullOrEmpty(r.Hash) && r.Hash != "0").ToList();
            var agreeing = valid.Where(r => r.Hash == myHash).Select(r => r.Ip).Distinct().ToList();

            var casters = valid.Where(r => r.IsCaster).ToList();
            if (casters.Count >= MIN_CASTER_RESPONDERS)
            {
                var casterDiffering = casters.Where(r => r.Hash != myHash).ToList();
                if (IsStrictMajority(casterDiffering.Count, casters.Count))
                    return Build(valid, casterDiffering, agreeing, "casters", valid.Count);
                // Casters answered and do NOT form a majority against us — casters are authoritative.
                return new MajorityVerdict { IsMinority = false, AgreeingPeers = agreeing, DecidedBy = "casters", Responders = valid.Count, Differing = casterDiffering.Count };
            }

            var differing = valid.Where(r => r.Hash != myHash).ToList();
            if (IsStrictMajority(differing.Count, valid.Count))
                return Build(valid, differing, agreeing, "all", valid.Count);

            return new MajorityVerdict { IsMinority = false, AgreeingPeers = agreeing, DecidedBy = valid.Count == 0 ? "none" : "all", Responders = valid.Count, Differing = differing.Count };
        }

        private static MajorityVerdict Build(List<ProbeResult> valid, List<ProbeResult> decidingDiffering, List<string> agreeing, string decidedBy, int responders)
        {
            var majorityHash = decidingDiffering
                .GroupBy(r => r.Hash)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .First().Key;
            var majorityPeers = valid.Where(r => r.Hash == majorityHash).Select(r => r.Ip).Distinct().ToList();
            return new MajorityVerdict
            {
                IsMinority = true,
                MajorityHash = majorityHash,
                MajorityPeers = majorityPeers,
                AgreeingPeers = agreeing,
                DecidedBy = decidedBy,
                Responders = responders,
                Differing = decidingDiffering.Count
            };
        }

        /// <summary>
        /// Unbounded divergence search. Precondition: the peer's hash at <paramref name="tipHeight"/>
        /// differs from ours. Walks linearly for <see cref="LINEAR_SEARCH_WINDOW"/> heights (the
        /// common shallow case), then binary-searches between the last known-different height and
        /// genesis. Returns the first divergent height (common ancestor + 1), or -1 when the peer
        /// stops answering or no common ancestor exists at all (different chain).
        /// Cost is O(log N) requests regardless of fork age — about 20 probes for a million blocks.
        /// </summary>
        public static async Task<long> FindDivergenceHeightAsync(
            Func<long, Task<string?>> peerHashAt,
            Func<long, string?> localHashAt,
            long tipHeight)
        {
            if (tipHeight <= 0)
                return -1;

            long knownDifferent = tipHeight;

            // Linear phase.
            for (var h = tipHeight - 1; h >= Math.Max(0, tipHeight - LINEAR_SEARCH_WINDOW); h--)
            {
                var same = await SameHashAsync(peerHashAt, localHashAt, h);
                if (same == null) return -1;
                if (same.Value) return knownDifferent;
                knownDifferent = h;
            }

            if (knownDifferent == 0)
                return -1; // genesis differs — not the same chain

            // Binary phase: invariant hash[lo] equal (or lo == -1 for "unknown below"), hash[hi] different.
            long lo = -1, hi = knownDifferent;
            // Establish a lower bound quickly: genesis must match, otherwise different chain.
            var genesisSame = await SameHashAsync(peerHashAt, localHashAt, 0);
            if (genesisSame == null || !genesisSame.Value) return -1;
            lo = 0;

            while (hi - lo > 1)
            {
                var mid = lo + (hi - lo) / 2;
                var same = await SameHashAsync(peerHashAt, localHashAt, mid);
                if (same == null) return -1;
                if (same.Value) lo = mid; else hi = mid;
            }
            return hi;
        }

        private static async Task<bool?> SameHashAsync(Func<long, Task<string?>> peerHashAt, Func<long, string?> localHashAt, long h)
        {
            var peer = await peerHashAt(h);
            if (string.IsNullOrEmpty(peer) || peer == "0") return null;
            var local = localHashAt(h);
            if (string.IsNullOrEmpty(local)) return null;
            return string.Equals(peer, local, StringComparison.Ordinal);
        }

        /// <summary>
        /// The fork-choice rule can say LocalWins on certificate weight. A node whose tip has not
        /// moved while a confirmed majority is more than a full reorg window ahead cannot be
        /// "winning" — its branch is dead. In that case we follow the majority regardless.
        /// </summary>
        public static bool ShouldOverrideLocalWin(bool stalled, long localTip, long remoteTip)
            => stalled && remoteTip - localTip > ForkChoiceUtility.MAX_REORG_DEPTH;

        /// <summary>Backoff between resolution attempts while stalled: 10s, 30s, 60s, then 120s forever.</summary>
        public static int NextBackoffSeconds(int attemptsSoFar)
            => attemptsSoFar switch
            {
                <= 0 => 0,
                1 => 10,
                2 => 30,
                3 => 60,
                _ => 120
            };
    }
}
