using System.Collections.Concurrent;
using Newtonsoft.Json;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Services
{
    /// <summary>
    /// SPLIT-GUARD: majority-wins commit gate for NON-caster validators in the legacy (pre-record)
    /// era. Before this, a validator committed the FIRST valid block at tip+1 that any authenticated
    /// peer pushed to it (message 7 from a caster, or ReceiveBlockVal gossip from another validator).
    /// When two casters commit different blocks for the same height, first-write-wins strands every
    /// validator that saw the minority block first (testnet 912,064, four validators, ~2 days).
    ///
    /// Rule (only for a live tip+1 commit on a non-caster while certificate enforcement is not active —
    /// the certificate verifier already enforces the quorum once the record era is armed):
    ///   confirmed when the same (height, hash) has been delivered by ≥2 DISTINCT casters, or when a
    ///   GetBlockHash probe of the caster set shows ≥2 casters holding this hash as COMMITTED and more
    ///   casters agree than disagree. Deliveries and probes are retried briefly because casters commit
    ///   and broadcast within a few hundred ms of each other.
    /// Liveness fallback: if fewer than 2 casters answer at all (validator behind NAT, casters
    /// unreachable on the validator API) the gate cannot judge and allows the block, loudly.
    /// A rejected block is simply not committed; the node stays one behind, receives the majority
    /// block from the next caster delivery, and the stall resolver covers anything worse.
    /// </summary>
    public static class ValidatorCommitGate
    {
        public const int MIN_CONFIRMING_CASTERS = 2;
        public const int MAX_ATTEMPTS = 4;
        public const int ATTEMPT_DELAY_MS = 750;

        private static readonly ConcurrentDictionary<(long Height, string Hash), ConcurrentDictionary<string, byte>> _deliveries = new();
        private static long _lastPrunedHeight = -1;

        public sealed class ProbeAnswer
        {
            public string Ip { get; init; } = "";
            public string Hash { get; init; } = "";
            public bool Committed { get; init; }
        }

        /// <summary>Pure (unit-tested): the confirmation decision.</summary>
        /// <param name="deliveryIps">casters that pushed exactly this block to us</param>
        /// <param name="answers">GetBlockHash answers from casters for this height</param>
        /// <param name="hash">the candidate block hash</param>
        /// <returns>Confirmed / Rejected / Pending (wait and retry) / Undecidable (too few casters answered)</returns>
        public static Verdict Decide(IReadOnlyCollection<string> deliveryIps, IReadOnlyList<ProbeAnswer> answers, string hash)
        {
            var valid = answers.Where(a => !string.IsNullOrEmpty(a.Hash) && a.Hash != "0").ToList();

            // A caster's committed answer is fresher than its earlier delivery: if it now holds a
            // different committed hash it is a dissenter, not a supporter.
            var dissenters = valid.Where(a => a.Committed && a.Hash != hash).Select(a => a.Ip).ToHashSet();
            var supporters = valid.Where(a => a.Committed && a.Hash == hash).Select(a => a.Ip).ToHashSet();
            foreach (var ip in deliveryIps)
                if (!dissenters.Contains(ip))
                    supporters.Add(ip);

            if (supporters.Count >= MIN_CONFIRMING_CASTERS && supporters.Count > dissenters.Count)
                return Verdict.Confirmed;

            if (dissenters.Count >= MIN_CONFIRMING_CASTERS && dissenters.Count > supporters.Count)
                return Verdict.Rejected;

            if (valid.Count < MIN_CONFIRMING_CASTERS && deliveryIps.Count == 0)
                return Verdict.Undecidable;

            return Verdict.Pending;
        }

        public enum Verdict { Confirmed, Rejected, Pending, Undecidable }

        /// <summary>
        /// Records that <paramref name="senderIp"/> delivered this block; returns true when the block
        /// may be committed now. Cheap early-outs for every case the gate does not cover.
        /// </summary>
        public static async Task<bool> ConfirmAsync(Block block, string? senderIp, string caller)
        {
            try
            {
                if (block == null || string.IsNullOrEmpty(block.Hash)) return false;
                if (Globals.IsBlockCaster) return true;                       // casters have their own agreement gate
                if (!Globals.IsChainSynced) return true;                       // startup sync — download path
                if (block.Height != Globals.LastBlock.Height + 1) return true; // not a live tip commit
                if (block.Height >= Globals.CertEnforceHeight) return true;    // certificate quorum governs
                if (ForkRecoveryUtility.IsInDownloadPhase) return true;        // recovery redownload

                var casters = CasterIps();
                if (casters.Count < MIN_CONFIRMING_CASTERS) return true;       // cannot judge

                Prune(block.Height);
                var key = (block.Height, block.Hash);
                var set = _deliveries.GetOrAdd(key, _ => new ConcurrentDictionary<string, byte>());
                var sender = RecoverySourcePolicy.Normalize(senderIp);
                if (sender.Length > 0 && casters.Contains(sender))
                    set[sender] = 1;

                for (var attempt = 1; attempt <= MAX_ATTEMPTS; attempt++)
                {
                    var deliveries = set.Keys.ToList();
                    var answers = deliveries.Count >= MIN_CONFIRMING_CASTERS
                        ? new List<ProbeAnswer>()
                        : await ProbeCastersAsync(casters, block.Height);

                    var verdict = Decide(deliveries, answers, block.Hash);
                    switch (verdict)
                    {
                        case Verdict.Confirmed:
                            if (attempt > 1 || deliveries.Count < MIN_CONFIRMING_CASTERS)
                                LogUtility.Log($"[{caller}] COMMIT-GATE: block {block.Height} {Short(block.Hash)} confirmed (deliveries={deliveries.Count}, committed-agree={answers.Count(a => a.Committed && a.Hash == block.Hash)}, attempt {attempt}).", "ValidatorCommitGate");
                            return true;
                        case Verdict.Rejected:
                            LogUtility.Log($"[{caller}] COMMIT-GATE: REJECTED block {block.Height} {Short(block.Hash)} from {sender} — casters hold a different committed hash ({string.Join(",", answers.Where(a => a.Committed && a.Hash != block.Hash).Select(a => $"{a.Ip}:{Short(a.Hash)}"))}). Not committing a minority block.", "ValidatorCommitGate");
                            ConsoleWriterService.Output($"[Consensus] Rejected block {block.Height} from {sender}: caster majority holds a different block.");
                            return false;
                        case Verdict.Undecidable:
                            LogUtility.Log($"[{caller}] COMMIT-GATE: fewer than {MIN_CONFIRMING_CASTERS} casters answered for block {block.Height}; allowing (legacy behaviour).", "ValidatorCommitGate");
                            return true;
                        case Verdict.Pending:
                            if (attempt < MAX_ATTEMPTS)
                                await Task.Delay(ATTEMPT_DELAY_MS);
                            break;
                    }
                }

                LogUtility.Log($"[{caller}] COMMIT-GATE: block {block.Height} {Short(block.Hash)} from {sender} not confirmed by {MIN_CONFIRMING_CASTERS} casters after {MAX_ATTEMPTS} attempts — deferring (the majority block will arrive from the next caster delivery).", "ValidatorCommitGate");
                return false;
            }
            catch (Exception ex)
            {
                LogUtility.Log($"[{caller}] COMMIT-GATE error: {ex.Message} — allowing.", "ValidatorCommitGate");
                return true;
            }
        }

        private static HashSet<string> CasterIps()
            => Globals.BlockCasters.ToList()
                .Select(c => RecoverySourcePolicy.Normalize(c.PeerIP))
                .Where(ip => ip.Length > 0)
                .ToHashSet();

        private static void Prune(long height)
        {
            if (height == _lastPrunedHeight) return;
            _lastPrunedHeight = height;
            foreach (var k in _deliveries.Keys.Where(k => k.Height < height - 5).ToList())
                _deliveries.TryRemove(k, out _);
        }

        private static async Task<List<ProbeAnswer>> ProbeCastersAsync(HashSet<string> casters, long height)
        {
            var tasks = casters.Select(async ip =>
            {
                try
                {
                    using var client = Globals.HttpClientFactory.CreateClient();
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    var resp = await client.GetAsync($"http://{ip}:{Globals.ValAPIPort}/valapi/validator/GetBlockHash/{height}", cts.Token);
                    if (!resp.IsSuccessStatusCode) return null;
                    var body = await resp.Content.ReadAsStringAsync();
                    if (string.IsNullOrEmpty(body) || body == "0") return null;
                    var parsed = JsonConvert.DeserializeAnonymousType(body, new { Hash = "", Validator = "", Height = 0L, Committed = (bool?)null });
                    if (parsed == null || string.IsNullOrEmpty(parsed.Hash)) return null;
                    // Committed is absent on not-yet-upgraded casters; for a tip+1 query such a
                    // caster only answers from its committed LastBlock or a CasterRoundDict draft.
                    // Treat absent as committed (legacy behaviour) — the delivery rule still needs 2.
                    return new ProbeAnswer { Ip = ip, Hash = parsed.Hash, Committed = parsed.Committed ?? true };
                }
                catch { return null; }
            });
            var all = await Task.WhenAll(tasks);
            return all.Where(a => a != null).Select(a => a!).ToList();
        }

        private static string Short(string? h) => string.IsNullOrEmpty(h) ? "" : h[..Math.Min(12, h.Length)];
    }
}
