using Newtonsoft.Json;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Services
{
    /// <summary>
    /// BOOTSTRAP-RESET SURVEY (Sep 2026, testnet 975,533): before the seeds may supersede a stale
    /// membership record, they must PROVE the network is stalled — not merely fail to see it moving.
    /// The fear this guards against is concrete: 120 validators, 110 stalled, 10 still producing
    /// behind a connection leak the seeds cannot see through. A reset in that state forks the chain,
    /// and a fork is worse than a stall.
    ///
    /// The survey therefore:
    ///  • talks to the WIDEST set it can assemble (seeds, committee members, NetworkValidators, the
    ///    on-chain validator registry, every P2P/validator/caster connection) — not "who we happen
    ///    to be connected to";
    ///  • uses fresh HttpClient calls to the val-API <c>Health</c> endpoint — the path that stayed
    ///    reliable throughout the outage — never the hub connection pool;
    ///  • collects height AND tip hash from every responder.
    ///
    /// <see cref="Evaluate"/> is pure and unit-tested. Its verdict is fail-safe in every direction:
    ///  • reach below <see cref="MinReachFraction"/> → NOT stalled (blind ≠ stalled; a leak that
    ///    blinds the seeds also drops them under the threshold);
    ///  • any responder ahead of us, or holding a different hash at our height → NOT stalled
    ///    (that is a lagging node or a fork — sync/resolve, never reset);
    ///  • committee majority not reachable → <see cref="Verdict.CommitteeUnreachable"/>: dead and
    ///    partitioned look identical from here, so the machine refuses to decide — this is the
    ///    operator's call (see BootstrapResetService.ForceNextReset).
    /// </summary>
    public static class NetworkStallSurvey
    {
        /// <summary>Fraction of the known set that must answer before a stall can be declared.</summary>
        public const double MinReachFraction = 0.5;
        /// <summary>Never declare a stall from fewer responders than this, whatever the fraction.</summary>
        public const int MinResponders = 3;

        public sealed class Node
        {
            public string Ip { get; init; } = "";
            public string Address { get; init; } = "";
            public bool IsCommittee { get; init; }
        }

        public sealed class Reply
        {
            public string Ip { get; init; } = "";
            public long Height { get; init; } = -1;
            public string TipHash { get; init; } = "";
        }

        public enum Verdict
        {
            /// <summary>Every gate passed: the reachable network is provably stalled at our tip.</summary>
            Stalled,
            /// <summary>Too few responders — blind, not stalled.</summary>
            InsufficientReach,
            /// <summary>Someone is ahead — we are lagging, sync instead.</summary>
            PeerAhead,
            /// <summary>Same height, different hash somewhere — a fork, resolve instead.</summary>
            HashDisagreement,
            /// <summary>Committee majority did not answer — dead or partitioned, undecidable here.</summary>
            CommitteeUnreachable,
        }

        public sealed class Result
        {
            public Verdict Verdict { get; init; }
            public int Known { get; init; }
            public int Responded { get; init; }
            public int CommitteeKnown { get; init; }
            public int CommitteeResponded { get; init; }
            public long MaxPeerHeight { get; init; } = -1;
            public string Detail { get; init; } = "";
            public List<string> Silent { get; init; } = new();
            public List<string> Ahead { get; init; } = new();
            public List<string> Disagreeing { get; init; } = new();
        }

        /// <summary>
        /// Pure verdict over a survey. <paramref name="committeeQuorum"/> is how many committee
        /// members must have answered (majority of the committee) for the "committee stalled"
        /// gate to be decidable.
        /// </summary>
        public static Result Evaluate(
            IReadOnlyList<Node> known,
            IReadOnlyList<Reply> replies,
            long ourHeight,
            string ourHash,
            int committeeQuorum)
        {
            var knownIps = known.Select(k => k.Ip).Where(ip => ip.Length > 0).Distinct(StringComparer.Ordinal).ToList();
            var replyByIp = replies.Where(r => r.Height >= 0).GroupBy(r => r.Ip).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var responded = knownIps.Count(ip => replyByIp.ContainsKey(ip));
            var silent = knownIps.Where(ip => !replyByIp.ContainsKey(ip)).ToList();

            var committeeIps = known.Where(k => k.IsCommittee).Select(k => k.Ip).Distinct(StringComparer.Ordinal).ToList();
            var committeeResponded = committeeIps.Count(ip => replyByIp.ContainsKey(ip));

            var ahead = replyByIp.Values.Where(r => r.Height > ourHeight).Select(r => $"{r.Ip}@{r.Height}").ToList();
            var disagreeing = replyByIp.Values
                .Where(r => r.Height == ourHeight && !string.IsNullOrEmpty(r.TipHash) && !string.Equals(r.TipHash, ourHash, StringComparison.OrdinalIgnoreCase))
                .Select(r => $"{r.Ip}:{r.TipHash[..Math.Min(16, r.TipHash.Length)]}")
                .ToList();
            var maxPeer = replyByIp.Values.Select(r => r.Height).DefaultIfEmpty(-1).Max();

            Result Make(Verdict v, string detail) => new()
            {
                Verdict = v,
                Known = knownIps.Count,
                Responded = responded,
                CommitteeKnown = committeeIps.Count,
                CommitteeResponded = committeeResponded,
                MaxPeerHeight = maxPeer,
                Detail = detail,
                Silent = silent,
                Ahead = ahead,
                Disagreeing = disagreeing,
            };

            // Order matters: anyone ahead or disagreeing is decisive regardless of reach — it is
            // positive evidence that a reset would be wrong.
            if (ahead.Count > 0)
                return Make(Verdict.PeerAhead, $"{ahead.Count} responder(s) ahead of our h={ourHeight}: {string.Join(",", ahead.Take(5))}");
            if (disagreeing.Count > 0)
                return Make(Verdict.HashDisagreement, $"{disagreeing.Count} responder(s) hold a different hash at h={ourHeight}: {string.Join(",", disagreeing.Take(5))}");

            var required = Math.Max(MinResponders, (int)Math.Ceiling(knownIps.Count * MinReachFraction));
            if (responded < required)
                return Make(Verdict.InsufficientReach, $"reached {responded}/{knownIps.Count} (need {required}) — blind, not stalled");

            if (committeeIps.Count > 0 && committeeResponded < committeeQuorum)
                return Make(Verdict.CommitteeUnreachable, $"committee {committeeResponded}/{committeeIps.Count} answered (need {committeeQuorum}) — dead or partitioned: operator decision");

            return Make(Verdict.Stalled, $"reached {responded}/{knownIps.Count}, committee {committeeResponded}/{committeeIps.Count}, none ahead, all agree on {ourHash[..Math.Min(16, Math.Max(0, ourHash.Length))]} at h={ourHeight}");
        }

        /// <summary>
        /// Assembles the widest known set. Committee IPs come from the record so the committee gate
        /// is judged against the members that actually count.
        /// </summary>
        public static List<Node> AssembleKnownSet(CasterMembershipRecord? committee)
        {
            var committeeAddrs = committee?.Casters.Select(c => c.Address).ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
            var nodes = new Dictionary<string, Node>(StringComparer.Ordinal);

            void Add(string? ip, string? addr)
            {
                var n = RecoverySourcePolicy.Normalize(ip);
                if (n.Length == 0) return;
                var a = addr ?? "";
                var isCommittee = a.Length > 0 && committeeAddrs.Contains(a);
                if (nodes.TryGetValue(n, out var existing))
                {
                    if (isCommittee && !existing.IsCommittee)
                        nodes[n] = new Node { Ip = n, Address = a, IsCommittee = true };
                    return;
                }
                nodes[n] = new Node { Ip = n, Address = a, IsCommittee = isCommittee };
            }

            if (committee != null)
                foreach (var c in committee.Casters) Add(c.PeerIP, c.Address);
            foreach (var s in SeedNodeService.GetBootstrapSeedPeers()) Add(s.PeerIP, s.ValidatorAddress);
            foreach (var c in Globals.BlockCasters.ToList()) Add(c.PeerIP, c.ValidatorAddress);
            foreach (var v in Globals.NetworkValidators.Values.ToList()) Add(v.IPAddress, v.Address);
            try { foreach (var v in Bitcoin.Services.VBTCValidatorRegistry.GetActiveValidators()) Add(v.IPAddress, v.ValidatorAddress); } catch { }
            foreach (var n in Globals.ValidatorNodes.Values.ToList()) Add(n.NodeIP, null);
            foreach (var n in Globals.BlockCasterNodes.Values.ToList()) Add(n.NodeIP, null);
            foreach (var n in Globals.Nodes.Values.ToList()) Add(n.NodeIP, null);

            // Never count ourselves.
            var self = RecoverySourcePolicy.Normalize(Globals.ReportedIP);
            if (self.Length > 0) nodes.Remove(self);
            return nodes.Values.ToList();
        }

        /// <summary>Fresh-connection Health probe of every node; fan-out bounded by the caller's set.</summary>
        public static async Task<List<Reply>> CollectAsync(IReadOnlyList<Node> known, int timeoutSeconds = 4)
        {
            var tasks = known.Select(async n =>
            {
                try
                {
                    using var client = Globals.HttpClientFactory.CreateClient();
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                    var uri = $"http://{n.Ip}:{Globals.ValAPIPort}/valapi/validator/Health";
                    var resp = await client.GetAsync(uri, cts.Token);
                    if (!resp.IsSuccessStatusCode) return new Reply { Ip = n.Ip };
                    var body = await resp.Content.ReadAsStringAsync();
                    var parsed = JsonConvert.DeserializeAnonymousType(body, new { Height = -1L, TipHash = "" });
                    if (parsed == null) return new Reply { Ip = n.Ip };
                    return new Reply { Ip = n.Ip, Height = parsed.Height, TipHash = parsed.TipHash ?? "" };
                }
                catch
                {
                    return new Reply { Ip = n.Ip };
                }
            }).ToList();
            return (await Task.WhenAll(tasks)).ToList();
        }

        /// <summary>One full survey against the current tip.</summary>
        public static async Task<Result> RunAsync(CasterMembershipRecord? committee)
        {
            var known = AssembleKnownSet(committee);
            var replies = await CollectAsync(known);
            var tip = Globals.LastBlock;
            var quorum = committee != null ? ConsensusQuorum.Required(committee.Casters.Count) : 0;
            var result = Evaluate(known, replies, tip?.Height ?? -1, tip?.Hash ?? "", quorum);
            LogUtility.Log($"STALL-SURVEY: {result.Verdict} — {result.Detail}; silent={result.Silent.Count} [{string.Join(",", result.Silent.Take(8))}]", "NetworkStallSurvey");
            return result;
        }
    }
}
