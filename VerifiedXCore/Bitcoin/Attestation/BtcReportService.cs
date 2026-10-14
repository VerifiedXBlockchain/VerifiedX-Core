using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using LiteDB;
using Newtonsoft.Json;
using VerifiedXCore.Bitcoin.Attestation.Net;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.P2P;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Bitcoin.Attestation
{
    /// <summary>
    /// The signatures validators have given for reports, as this node holds them (plan 3.4, steps 2 and 3). Memory
    /// only: a signature that is lost is asked for again. A signature is kept only when it is valid and its signer is
    /// in the stored signer set of the epoch it names, so nothing here can be filled by a stranger.
    /// </summary>
    public static class BtcReportSignatures
    {
        /// <summary>Reports a single signer may have signatures held for at once (it signs one or two at a time).</summary>
        public const int MaxReportsPerSigner = 32;

        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> ByReport = new(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, Queue<string>> BySigner = new(StringComparer.Ordinal);
        /// <summary>
        /// The reports this node has itself planned in the last two hours. A signature for one of them is never pushed
        /// out by signatures for other reports: a signer's place in the store used to be a queue of 32 with the oldest
        /// dropped, and anyone could fill it with that signer's own old signatures (they are public once mined, valid
        /// for ever for their hash, and the epoch a signature is offered under is not part of what is signed), pushing
        /// out the signature for the report in hand. Done for a third of the signers, nothing reached its quorum
        /// (second path audit, 14 October 2026).
        /// </summary>
        private static readonly ConcurrentDictionary<string, DateTime> Planned = new(StringComparer.Ordinal);

        /// <summary>This node has planned this report: its signatures are kept until the report is two hours old.</summary>
        public static void NotePlanned(string reportHash)
        {
            if (string.IsNullOrEmpty(reportHash)) return;
            Planned[reportHash] = DateTime.UtcNow;
            if (Planned.Count > 256)
                foreach (var old in Planned.Where(p => DateTime.UtcNow - p.Value > TimeSpan.FromHours(2)).ToList())
                {
                    Planned.TryRemove(old.Key, out _);
                    // Its signatures go with it (they were kept outside the per-signer queue while it was planned).
                    foreach (var key in ByReport.Keys.Where(k => k.StartsWith(old.Key + "|", StringComparison.Ordinal)).ToList())
                        ByReport.TryRemove(key, out _);
                }
        }

        private static bool IsPlanned(string key) => Planned.ContainsKey(key.Substring(0, 64));

        private static string Key(string reportHash, long epoch) => $"{reportHash}|{epoch}";

        /// <summary>Checks a signature and keeps it. <paramref name="tipHeight"/> is this node's VFX tip.</summary>
        public static (bool Ok, string Why) Accept(string? reportHash, long epoch, string? address, string? signature, long tipHeight, LiteDatabase? db = null)
        {
            if (reportHash == null || reportHash.Length != 64 || !reportHash.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                return (false, "not a report hash");
            if (string.IsNullOrEmpty(address) || string.IsNullOrEmpty(signature) || signature.Length > 512)
                return (false, "no signature");
            var current = BtcAttestRules.EpochOf(tipHeight);
            if (epoch < 0 || (epoch != current && epoch != current - 1))
                return (false, $"epoch {epoch} is not the current epoch ({current}) or the one before it");
            var set = BtcAttestStore.SignerSet(epoch, db);
            if (set == null || !set.Addresses.Contains(address, StringComparer.Ordinal))
                return (false, $"{address} is not a signer in epoch {epoch}");
            // A signer's signature already held for this report is not checked again: copies of it (hundreds in one
            // answer from another validator) cost nothing (fourth review, round 2).
            if (ByReport.TryGetValue(Key(reportHash, epoch), out var already) && already.ContainsKey(address))
                return (true, "held");
            if (!VerifiedXCore.Services.SignatureService.VerifySignature(address, BtcReport.SigningMessage(reportHash), signature))
                return (false, "the signature is not valid");

            var key = Key(reportHash, epoch);
            var held = ByReport.GetOrAdd(key, _ => new ConcurrentDictionary<string, string>(StringComparer.Ordinal));
            if (held.TryAdd(address, signature) && !IsPlanned(key))
            {
                // Only signatures for reports this node has not planned (yet) take a place in the signer's queue, and
                // only those are dropped from it.
                var mine = BySigner.GetOrAdd(address, _ => new Queue<string>());
                lock (mine)
                {
                    mine.Enqueue(key);
                    while (mine.Count > MaxReportsPerSigner)
                    {
                        var oldest = mine.Dequeue();
                        if (IsPlanned(oldest)) continue; // planned since it was queued: kept
                        if (ByReport.TryGetValue(oldest, out var old))
                        {
                            old.TryRemove(address, out _);
                            if (old.IsEmpty) ByReport.TryRemove(oldest, out _);
                        }
                    }
                }
            }
            return (true, "");
        }

        /// <summary>The signatures held for a report, in address order.</summary>
        public static List<BtcReportSignature> For(string reportHash, long epoch) =>
            ByReport.TryGetValue(Key(reportHash, epoch), out var held)
                ? held.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => new BtcReportSignature { ValidatorAddress = s.Key, Signature = s.Value }).ToList()
                : new List<BtcReportSignature>();

        public static void Clear()
        {
            ByReport.Clear();
            BySigner.Clear();
            Planned.Clear();
        }
    }

    /// <summary>What a signature request carries between validators.</summary>
    public sealed class BtcReportSignatureMessage
    {
        public string ReportHash { get; set; } = "";
        public long SignerEpoch { get; set; }
        public string ValidatorAddress { get; set; } = "";
        public string Signature { get; set; } = "";
    }

    /// <summary>What this validator's report work looks like right now (GET /frost/btcreport/status).</summary>
    public sealed class BtcReportStatus
    {
        public bool Running { get; set; }
        public string? Idle { get; set; }
        public long VfxTip { get; set; }
        public long BitcoinTipHeight { get; set; }
        public string? BitcoinTipHash { get; set; }
        public int BitcoinPeers { get; set; }
        public string? BitcoinProblem { get; set; }
        public long AttestedTipHeight { get; set; }
        public string? AttestedTipHash { get; set; }
        public long EventsThroughHeight { get; set; }
        public long PendingPartBlockHeight { get; set; }
        public int PendingNextPart { get; set; }
        public string? Waiting { get; set; }
        public List<object> Offered { get; set; } = new();
        public string? LastSubmitted { get; set; }
        public long UpdatedAt { get; set; }
    }

    /// <summary>
    /// A validator's part in reporting (plan 3.4): follow Bitcoin, work out the next report, sign it, exchange
    /// signatures with the other validators, and submit the report once two thirds have signed the same hash.
    /// No validator is chosen to do this; each one does all of it, and the mempool keeps one copy.
    /// </summary>
    public static class BtcReportService
    {
        private const int LoopSeconds = 6;
        private static readonly BtcReportPlanner Planner = new();
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };
        private static readonly ConcurrentDictionary<string, DateTime> FirstReady = new(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, DateTime> Submitted = new(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, DateTime> LastAsked = new(StringComparer.Ordinal);

        public static BtcReportStatus Status { get; private set; } = new();

        /// <summary>Tests only: replaces the wait before submitting.</summary>
        internal static TimeSpan? SubmitWaitOverride;

        /// <summary>Started once at node start. Does nothing while the node is not a validator.</summary>
        public static async Task RunLoop(CancellationToken ct)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(2), ct); } catch { return; } // let startup complete

            while (!ct.IsCancellationRequested)
            {
                // Asked every pass, not once at start: a node may start validating later, and a wallet that is unlocked
                // after start has no validator address until then. (The loop used to end here for good, so such a
                // validator never signed a report until it was restarted.)
                try { if (!string.IsNullOrEmpty(Globals.ValidatorAddress)) await TickAsync(ct); }
                catch (Exception ex) { ErrorLogUtility.LogError($"Bitcoin report loop: {ex}", "BtcReportService.RunLoop()"); }
                try { await Task.Delay(TimeSpan.FromSeconds(LoopSeconds), ct); } catch { return; }
            }
        }

        private static BtcReportStatus Idle(string why) => new() { Running = false, Idle = why, VfxTip = Globals.LastBlock?.Height ?? 0, UpdatedAt = TimeUtil.GetTime() };

        private static async Task TickAsync(CancellationToken ct)
        {
            var tip = Globals.LastBlock?.Height ?? 0;
            if (tip + 1 < Globals.BitcoinAttestationObserveHeight)
            {
                Status = Idle("before the observation height");
                return;
            }
            var (ready, notReady) = VBTCCancellationVoteService.NodeIsAtNetworkHeight();
            if (!ready)
            {
                Status = Idle($"this node is not at the network's height ({notReady})");
                return;
            }
            string? noClient = null;
            var client = BtcLightClient.Current ?? BtcLightClient.Create(out noClient);
            if (client == null)
            {
                Status = Idle($"the Bitcoin checkpoint is not usable: {noClient}");
                return;
            }

            await client.RunOnceAsync(ct);
            await StepAsync(client, GossipOverHttpAsync, ct);
            Status.BitcoinPeers = client.PeerCount;
            Status.BitcoinProblem = client.LastProblem;
        }

        /// <summary>
        /// One round of report work against a view of Bitcoin: plan, sign, exchange signatures, submit what is ready.
        /// <paramref name="gossip"/> carries this node's signature to the other validators and brings theirs back.
        /// </summary>
        internal static async Task StepAsync(IBtcChainView view, Func<BtcReport, string, BtcReportSignature?, CancellationToken, Task> gossip, CancellationToken ct)
        {
            var status = new BtcReportStatus
            {
                Running = true, VfxTip = Globals.LastBlock?.Height ?? 0, UpdatedAt = TimeUtil.GetTime(),
                BitcoinTipHeight = view.TipHeight, BitcoinTipHash = view.HeaderAt(view.TipHeight)?.Hash,
            };
            Status = status;
            var tip = status.VfxTip;

            // From the observation height the node follows Bitcoin; there is nothing to sign until the recording
            // height, when the chain's own view of Bitcoin starts.
            var meta = BtcAttestStore.GetMeta();
            if (meta == null)
            {
                status.Idle = "following Bitcoin; the chain's view starts at the recording height";
                return;
            }
            status.AttestedTipHeight = meta.TipHeight;
            status.AttestedTipHash = meta.TipHash;
            status.EventsThroughHeight = meta.EventsThroughHeight;
            status.PendingPartBlockHeight = meta.PendingPartBlockHeight;
            status.PendingNextPart = meta.PendingNextPart;

            // The plan reads state; a block being applied meanwhile would show it half a block.
            if (Globals.TreisUpdating) return;
            var plan = Planner.Plan(view, tip);
            if (Globals.TreisUpdating || (Globals.LastBlock?.Height ?? 0) != tip)
            {
                // Only this plan is dropped. The planner's memo (blocks found empty, and how far the backfill sweep has
                // gone) is kept: each entry is keyed on the state stamps read when the plan began, so a block applied
                // meanwhile makes the entries it affects stale and they are done again. Forgetting it on every new VFX
                // block meant a plan longer than a block's time (a validator catching up on Bitcoin) never finished,
                // on any validator (fourth review).
                return;
            }
            status.Waiting = plan.Waiting;

            foreach (var planned in plan.Reports)
            {
                var report = planned.Report;
                var hash = report.Hash();
                BtcReportSignatures.NotePlanned(hash);
                var mine = SignAsThisValidator(report, hash, tip);
                await gossip(report, hash, mine, ct);

                var held = BtcReportSignatures.For(hash, report.SignerEpoch);
                var set = BtcAttestStore.SignerSet(report.SignerEpoch);
                var required = set == null ? 0 : BtcAttestRules.RequiredSignatures(set.Addresses.Count);
                status.Offered.Add(new { planned.Kind, ReportHash = hash, report.BlockHeight, report.Part, report.Scope, Headers = report.Headers.Count, Events = report.Events.Count, Signatures = held.Count, Required = required, SignedByThisNode = mine != null });

                if (set == null || held.Count < required) continue;
                var tx = Assemble(report, hash);
                if (tx == null) continue;
                if (!ReadyToSubmit(hash)) continue;
                var (ok, why) = await SubmitAsync(tx);
                if (ok)
                {
                    Submitted[hash] = DateTime.UtcNow;
                    status.LastSubmitted = $"{planned.Kind} {hash} in {tx.Hash}";
                    LogUtility.Log($"Bitcoin report submitted: {planned.Kind}, block {report.BlockHeight}, part {report.Part}, {report.Events.Count} event(s), {report.Headers.Count} header(s), report {hash}, tx {tx.Hash}.", "BtcReportService.StepAsync()");
                }
                else if (!why.Contains("already in the mempool", StringComparison.Ordinal))
                {
                    status.Waiting = $"{planned.Kind} report {hash} was not admitted: {why}";
                }
            }
            Forget();
        }

        /// <summary>
        /// This validator's signature over a report hash; null when it is not a signer of the report's epoch or its
        /// wallet cannot sign now. The signature is also put with the others this node holds.
        /// </summary>
        internal static BtcReportSignature? SignAsThisValidator(BtcReport report, string reportHash, long tipHeight)
        {
            try
            {
                var address = Globals.ValidatorAddress;
                if (string.IsNullOrEmpty(address)) return null;
                var set = BtcAttestStore.SignerSet(report.SignerEpoch);
                if (set == null || !set.Addresses.Contains(address, StringComparer.Ordinal)) return null;

                var already = BtcReportSignatures.For(reportHash, report.SignerEpoch).FirstOrDefault(s => s.ValidatorAddress == address);
                if (already != null) return already;

                var account = AccountData.GetSingleAccount(address);
                if (account == null) return null;
                var signature = VerifiedXCore.Services.SignatureService.CreateSignature(BtcReport.SigningMessage(reportHash), AccountData.GetPrivateKey(account), account.PublicKey);
                if (string.IsNullOrEmpty(signature) || signature == "ERROR") return null;
                var (ok, _) = BtcReportSignatures.Accept(reportHash, report.SignerEpoch, address, signature, tipHeight);
                return ok ? new BtcReportSignature { ValidatorAddress = address, Signature = signature } : null;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Could not sign Bitcoin report {reportHash}: {ex.Message}", "BtcReportService.SignAsThisValidator()");
                return null;
            }
        }

        /// <summary>
        /// The report transaction, carrying exactly as many signatures as are required (the lowest addresses among
        /// those held), or null when too few are held. Exactly as many, so that validators holding the same
        /// signatures build the same transaction and nobody can make a second valid one by leaving signatures out.
        /// </summary>
        internal static Transaction? Assemble(BtcReport report, string reportHash)
        {
            var set = BtcAttestStore.SignerSet(report.SignerEpoch);
            if (set == null) return null;
            var required = BtcAttestRules.RequiredSignatures(set.Addresses.Count);
            var members = new HashSet<string>(set.Addresses, StringComparer.Ordinal);
            var held = BtcReportSignatures.For(reportHash, report.SignerEpoch).Where(s => members.Contains(s.ValidatorAddress)).ToList();
            if (held.Count < required) return null;
            report.Signatures = held.Take(required).ToList();
            return BtcAttestTxValidator.Create(report.Rewind == true ? TransactionType.VBTC_V2_BTC_REORG : TransactionType.VBTC_V2_BTC_REPORT, report.ToCanonicalJson());
        }

        /// <summary>
        /// Every validator reaches the threshold at about the same moment. Each waits a few seconds that depend on
        /// its address and the report, so that most find the report already in the mempool and submit nothing.
        /// Not a turn order: whoever is still without it after its wait submits.
        /// </summary>
        /// <summary>How long after submitting a report this node looks whether it is still on its way.</summary>
        internal static int ResubmitAfterSeconds = 60;

        private static bool ReadyToSubmit(string reportHash)
        {
            // Submitted, and still on its way: nothing to do. Submitted a while ago and no copy of it is in the mempool
            // any more although it is still planned (so it was not mined): it was lost on the way and is sent again.
            // (It used to be marked as submitted for two hours whatever became of it.)
            if (Submitted.TryGetValue(reportHash, out var sent)
                && (DateTime.UtcNow - sent < TimeSpan.FromSeconds(ResubmitAfterSeconds) || BtcAttestTxValidator.ReportInMempool(reportHash)))
                return false;
            var since = FirstReady.GetOrAdd(reportHash, _ => DateTime.UtcNow);
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(reportHash + "|" + Globals.ValidatorAddress));
            var wait = SubmitWaitOverride ?? TimeSpan.FromSeconds(digest[0] % 4 * LoopSeconds);
            return DateTime.UtcNow - since >= wait;
        }

        /// <summary>Judges the transaction as any node will, and puts it in the mempool and on the network.</summary>
        internal static async Task<(bool Ok, string Why)> SubmitAsync(Transaction tx)
        {
            var (ok, why) = await TransactionValidatorService.VerifyTX(tx);
            if (!ok) return (false, why);
            tx.TransactionRating = await TransactionRatingService.GetTransactionRating(tx);
            await TransactionData.AddToPool(tx);
            await P2PClient.SendTXMempool(tx);
            return (true, "");
        }

        private static void Forget()
        {
            var old = DateTime.UtcNow - TimeSpan.FromHours(2);
            foreach (var entry in FirstReady.Where(e => e.Value < old).ToList()) FirstReady.TryRemove(entry.Key, out _);
            foreach (var entry in Submitted.Where(e => e.Value < old).ToList()) Submitted.TryRemove(entry.Key, out _);
            foreach (var entry in LastAsked.Where(e => e.Value < old).ToList()) LastAsked.TryRemove(entry.Key, out _);
        }

        // ── Between validators ──────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// POST /frost/btcreport/signature: keeps the sender's signature when it is valid and answers with the
        /// signatures this node holds for the same report, so one round trip moves signatures both ways.
        /// </summary>
        public static object HandleSignature(BtcReportSignatureMessage? message)
        {
            if (message == null) return new { Success = false, Message = "no signature" };
            var tip = Globals.LastBlock?.Height ?? 0;
            var (ok, why) = BtcReportSignatures.Accept(message.ReportHash, message.SignerEpoch, message.ValidatorAddress, message.Signature, tip);
            return new
            {
                Success = ok,
                Message = why,
                Signatures = ok ? BtcReportSignatures.For(message.ReportHash, message.SignerEpoch) : new List<BtcReportSignature>(),
            };
        }

        /// <summary>
        /// POST /frost/btcreport/headers: the Bitcoin headers this validator follows, after the newest hash of the
        /// request's locator that it holds (see <see cref="BtcValidatorSource"/>). Public data, and nothing the asker
        /// has to believe: it checks every header itself.
        /// </summary>
        public static object HandleHeaders(BtcHeadersRequest? request)
        {
            var chain = BtcLightClient.Current?.Chain;
            if (chain == null) return new { Success = false, Message = "this node does not follow Bitcoin", Headers = new List<string>() };
            if (request?.Locator == null || request.Locator.Count == 0 || request.Locator.Count > BtcValidatorSource.MaxLocatorHashes)
                return new { Success = false, Message = "a header request carries a locator", Headers = new List<string>() };
            return new { Success = true, Message = "", Headers = chain.HexesAfter(request.Locator, BtcValidatorSource.MaxHeadersPerAnswer) };
        }

        private sealed class SignatureAnswer
        {
            public bool Success { get; set; }
            public List<BtcReportSignature>? Signatures { get; set; }
        }

        /// <summary>The most bytes of a signature answer read: every member's address and signature, with room for the JSON.</summary>
        internal static int MaxAnswerBytes(int members) => 4096 + members * 1024;

        /// <summary>
        /// Sends this node's signature to the signers whose signature it lacks, and keeps what they answer with.
        /// A signer is asked again no more often than every few loops.
        /// </summary>
        private static async Task GossipOverHttpAsync(BtcReport report, string reportHash, BtcReportSignature? mine, CancellationToken ct)
        {
            if (mine == null) return; // a node that is not a signer has nothing to offer
            var tip = Globals.LastBlock?.Height ?? 0;
            var set = BtcAttestStore.SignerSet(report.SignerEpoch);
            if (set == null) return;
            var have = new HashSet<string>(BtcReportSignatures.For(reportHash, report.SignerEpoch).Select(s => s.ValidatorAddress), StringComparer.Ordinal);
            var members = new HashSet<string>(set.Addresses, StringComparer.Ordinal);
            var targets = VBTCValidatorRegistry.GetActiveValidatorsAt(tip)
                .Where(v => members.Contains(v.ValidatorAddress) && !have.Contains(v.ValidatorAddress) && !string.IsNullOrEmpty(v.IPAddress))
                .Where(v => !LastAsked.TryGetValue($"{reportHash}|{v.ValidatorAddress}", out var at) || DateTime.UtcNow - at > TimeSpan.FromSeconds(3 * LoopSeconds))
                .ToList();
            if (targets.Count == 0) return;

            var body = JsonConvert.SerializeObject(new BtcReportSignatureMessage
            {
                ReportHash = reportHash, SignerEpoch = report.SignerEpoch, ValidatorAddress = mine.ValidatorAddress, Signature = mine.Signature,
            });
            await Task.WhenAll(targets.Select(async validator =>
            {
                LastAsked[$"{reportHash}|{validator.ValidatorAddress}"] = DateTime.UtcNow;
                try
                {
                    var ip = validator.IPAddress.Replace("::ffff:", "");
                    using var content = new StringContent(body, Encoding.UTF8, "application/json");
                    using var request = new HttpRequestMessage(HttpMethod.Post, $"http://{ip}:{Globals.FrostValidatorPort}/frost/btcreport/signature") { Content = content };
                    // Read only as far as an answer holding every member's signature goes, and look at no more entries
                    // than the set has members, each member once: a signer could answer with gigabytes, or with
                    // thousands of entries that each cost a signature check (fourth review, round 2).
                    using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (!response.IsSuccessStatusCode) return;
                    var text = await Net.BtcValidatorSource.ReadBoundedAsync(response.Content, MaxAnswerBytes(members.Count), ct);
                    if (text == null) return;
                    var answer = JsonConvert.DeserializeObject<SignatureAnswer>(text);
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var s in (answer?.Signatures ?? new List<BtcReportSignature>()).Take(members.Count))
                    {
                        if (s?.ValidatorAddress == null || !members.Contains(s.ValidatorAddress) || !seen.Add(s.ValidatorAddress)) continue;
                        BtcReportSignatures.Accept(reportHash, report.SignerEpoch, s.ValidatorAddress, s.Signature, tip);
                    }
                }
                catch { }
            }));
        }
    }
}
