using System.Collections.Concurrent;
using System.Text;
using Newtonsoft.Json;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Services
{
    /// <summary>A caster's tip, sent to a validator every <see cref="VreeElection.ProbeSeconds"/>: header and certificate, never the body.</summary>
    public class VreeProbe
    {
        public long Height { get; set; }
        public string Hash { get; set; } = "";
        public string PrevHash { get; set; } = "";
        public string Validator { get; set; } = "";
        public int Version { get; set; }
        public ConsensusCertificate? Certificate { get; set; }
        public string Prober { get; set; } = "";
        /// <summary>The membership records the validator did not hold when it last answered.</summary>
        public List<CasterMembershipRecord> Records { get; set; } = new();
    }

    public class VreeProbeAnswer
    {
        public bool Ack { get; set; }
        public string Address { get; set; } = "";
        public string Signature { get; set; } = "";
        public long RecordSeq { get; set; } = -1;
        public string Reason { get; set; } = "";
    }

    public class VreeSignatureAnswer
    {
        public string SignerAddress { get; set; } = "";
        public string Signature { get; set; } = "";
        public string RecordHash { get; set; } = "";
        public string Refusal { get; set; } = "";
    }

    /// <summary>
    /// The validator-ratified emergency election: the network side of <see cref="VreeElection"/>. Each validator decides for
    /// itself whether to sign the rung its own chain gives (<see cref="AnswerSignature"/>); a rung's members collect those
    /// signatures, install the record and announce it. Each caster probes the validators with its certified tip and stands
    /// down when too few acknowledge it (<see cref="StandDownReason"/>). Nothing here decides who is elected or what a
    /// block means.
    /// </summary>
    public static class VreeService
    {
        private const int TickSeconds = 5;
        private const int HttpTimeoutSeconds = 4;
        /// <summary>A survey is refreshed this often while the election is open, and counts as fresh for twice as long.</summary>
        private const long SurveySeconds = 15;
        private const int MaxProbeRecords = 8;
        private const int MaxProbeAttestations = 16;
        /// <summary>Claims of a block above or beside the tip that one survey checks.</summary>
        private const int MaxClaimsPerSurvey = 5;
        /// <summary>How many electors a signer asks for the record, besides the casters and seeds it knows.</summary>
        private const int RecordPullSample = 8;
        /// <summary>A probe's first answer is re-read for this long before the reach set is recomputed.</summary>
        private const long ReachSetCacheSeconds = 60;

        // ── Test seams ──────────────────────────────────────────────────────────────────────────

        /// <summary>This node's validator key. Test seam.</summary>
        internal static Func<(string Address, PrivateKey? Key, string PublicKey)> LocalKey = () =>
        {
            var account = AccountData.GetLocalValidator();
            return (account?.Address ?? "", account?.GetPrivKey, account?.PublicKey ?? "");
        };

        /// <summary>GET a path of the validator API at an address; null when it did not answer. Test seam.</summary>
        internal static Func<string, string, Task<string?>> HttpGet = async (ip, path) =>
        {
            try
            {
                using var client = Globals.HttpClientFactory.CreateClient();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(HttpTimeoutSeconds));
                var resp = await client.GetAsync($"http://{ip}:{Globals.ValAPIPort}/valapi/validator/{path}", cts.Token);
                return resp.IsSuccessStatusCode ? await resp.Content.ReadAsStringAsync() : null;
            }
            catch { return null; }
        };

        /// <summary>POST JSON to a path of the validator API at an address; null when it did not answer. Test seam.</summary>
        internal static Func<string, string, string, Task<string?>> HttpPost = async (ip, path, json) =>
        {
            try
            {
                using var client = Globals.HttpClientFactory.CreateClient();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(HttpTimeoutSeconds));
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                var resp = await client.PostAsync($"http://{ip}:{Globals.ValAPIPort}/valapi/validator/{path}", content, cts.Token);
                return resp.IsSuccessStatusCode ? await resp.Content.ReadAsStringAsync() : null;
            }
            catch { return null; }
        };

        internal static void ResetForTests()
        {
            _survey = null; _evidence = null; _lastSignedUnix = 0; _surveyRunning = 0;
            _collected.Clear(); _mySignatures.Clear();
            _acks.Clear(); _peerSeq.Clear(); _lastProbeUnix = 0; _firstProbeUnix = 0; _reachCount = 0; _probedCommittee = "";
            _reachCache = null; _verifiedTips.Clear(); _myAck = null; _lastPullForProbeUnix = 0;
            _reconciledRecord = "";
        }

        internal static void NoteSurveyForTests(string tipHash, bool committeeUnreachable) => _survey = new Survey(tipHash, TimeUtil.GetTime(), committeeUnreachable);
        internal static void ResetEvidenceForTests() => _evidence = null;
        internal static long LastSignedForTests => _lastSignedUnix;

        // ── The loop ────────────────────────────────────────────────────────────────────────────

        /// <summary>Runs beside the caster monitor on every validator. Its own loop: the monitor blocks on unreachable peers, which is when this matters.</summary>
        public static async Task RunAsync()
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(TickSeconds));
                try { await TickAsync(); }
                catch (Exception ex) { ErrorLogUtility.LogError($"VREE tick failed: {ex.Message}", "VreeService.RunAsync()"); }
            }
        }

        internal static async Task TickAsync()
        {
            if (string.IsNullOrEmpty(Globals.ValidatorAddress) || Globals.StopAllTimers || !Globals.IsChainSynced || Globals.IsResyncing)
                return;
            ReconcileWhenRotationTakesOver();
            await Task.WhenAll(ProbeIfDueAsync(), ElectionTickAsync());
        }

        private static string _reconciledRecord = "";

        /// <summary>A rotation's reconcile was put off while it was pending (CasterMembershipService); it takes over at the block before its effective height.</summary>
        private static void ReconcileWhenRotationTakesOver()
        {
            var head = CasterMembershipStore.GetCurrent();
            if (head?.ChangeType != VreeElection.ScheduledRotationChangeType || head.RecordHash == _reconciledRecord) return;
            if (VreeElection.RotationPending(head, Globals.LastBlock?.Height ?? -1)) return;
            _reconciledRecord = head.RecordHash;
            CasterMembershipService.ReconcileBlockCastersToRecord(head);
        }

        private static VreeElection.ElectionView? CurrentView()
        {
            var tip = Globals.LastBlock;
            return tip == null ? null : VreeElection.ViewAt(CasterMembershipStore.GetCurrent(), tip.Height, tip.Hash, VreeElection.StallSeconds(), CasterMembershipStore.GetBySeq, VreeElection.TipTimestampAgeSeconds());
        }

        // ── Survey: rules 2, 3 and 8 ────────────────────────────────────────────────────────────

        private sealed record Survey(string TipHash, long At, bool CommitteeUnreachable);
        private static volatile Survey? _survey;
        private static int _surveyRunning;

        /// <summary>A block above or beside the tip, seen with a certificate that verifies. A bare claim from a peer never gets here.</summary>
        private sealed record Evidence(long Height, string Hash, long At);
        private static volatile Evidence? _evidence;

        /// <summary>
        /// How long evidence lasts without being seen again. A live committee shows its tip every probe, and a peer that holds
        /// the block shows it every survey; a block seen once whose committee then died, and that nobody can serve, must not
        /// veto every election for good. If it does exist it loses to the ratified record in fork choice.
        /// </summary>
        private const long EvidenceSeconds = VreeElection.ElectSeconds;

        internal static bool HasEvidenceAgainst(long tipHeight, string? tipHash)
        {
            var e = _evidence;
            if (e == null || TimeUtil.GetTime() - e.At >= EvidenceSeconds) return false;
            return e.Height > tipHeight || (e.Height == tipHeight && !string.Equals(e.Hash, tipHash, StringComparison.OrdinalIgnoreCase));
        }

        internal static void NoteEvidence(long height, string hash, long? at = null) => _evidence = new Evidence(height, hash, at ?? TimeUtil.GetTime());

        /// <summary>A survey counts for its tip. A rotation is signed on a moving chain, where no survey outlives its tip: rule 3 does not apply there, and age alone is what matters.</summary>
        private static bool SurveyCounts(Survey? s, VreeElection.ElectionView view, long maxAge) =>
            s != null && (view.IsRotation || s.TipHash == view.TipHash) && TimeUtil.GetTime() - s.At < maxAge;

        private static void StartSurveyIfDue(VreeElection.ElectionView view)
        {
            if (SurveyCounts(_survey, view, SurveySeconds)) return;
            if (Interlocked.CompareExchange(ref _surveyRunning, 1, 0) != 0) return;
            _ = Task.Run(async () =>
            {
                try { await RunSurveyAsync(view); }
                catch (Exception ex) { ErrorLogUtility.LogError($"VREE survey failed: {ex.Message}", "VreeService.RunSurveyAsync()"); }
                finally { Interlocked.Exchange(ref _surveyRunning, 0); }
            });
        }

        /// <summary>Pulls the record so the signature is on the network's head (rule 8), then sweeps Health: who is casting at the tip (rule 3), and who claims a block we do not have (rule 2).</summary>
        internal static async Task RunSurveyAsync(VreeElection.ElectionView view)
        {
            var self = Globals.ValidatorAddress;
            var sample = view.Epoch.Ranked.Where(a => a != self).Take(RecordPullSample).Select(a => view.Epoch.Electors[a].IPAddress);
            var peers = ValidatorMembershipPull.PeerIps().Concat(sample).Where(ip => !string.IsNullOrEmpty(ip)).Select(ip => ip.Replace("::ffff:", "")).Distinct().ToList();
            if (peers.Count > 0)
            {
                try { await CasterDiscoveryService.FetchAndAdoptMembershipAsync(peers, reconcileWhenUnchanged: false); } catch { }
            }

            var committee = CasterMembershipStore.GetForHeight(view.TipHeight);
            var known = NetworkStallSurvey.AssembleKnownSet(committee);
            var replies = await NetworkStallSurvey.CollectAsync(known, HttpTimeoutSeconds);
            var unreachable = committee == null
                || VreeElection.CommitteeUnreachable(known, replies, view.TipHeight, ConsensusQuorum.Required(committee.Casters.Count));
            await VerifyClaimsAsync(view, replies);
            _survey = new Survey(view.TipHash, TimeUtil.GetTime(), unreachable);
        }

        /// <summary>
        /// Rule 2: a peer's claim counts only if it serves the block with a certificate that verifies. A height claim costs
        /// nothing, so a bare one would be a free veto on every election. Electors' claims are read first, so a flood of
        /// claims from unbonded peers cannot crowd one out.
        /// </summary>
        internal static async Task VerifyClaimsAsync(VreeElection.ElectionView view, IEnumerable<NetworkStallSurvey.Reply> replies)
        {
            var electorIps = view.Epoch.Electors.Values.Select(v => (v.IPAddress ?? "").Replace("::ffff:", "")).ToHashSet(StringComparer.Ordinal);
            var claims = replies
                .Where(r => r.Height > view.TipHeight || (r.Height == view.TipHeight && !string.IsNullOrEmpty(r.TipHash) && !string.Equals(r.TipHash, view.TipHash, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(r => electorIps.Contains(r.Ip))
                .ThenBy(r => r.Height)
                .GroupBy(r => (r.Height, r.TipHash)).Select(g => g.First())
                .Take(MaxClaimsPerSurvey).ToList();
            foreach (var verified in await Task.WhenAll(claims.Select(ServedAndCertifiedAsync)))
            {
                if (verified != null)
                {
                    NoteEvidence(verified.Height, verified.Hash);
                    CasterLogUtility.Log($"VREE: verified a certified block at {verified.Height} ({verified.Hash[..Math.Min(16, verified.Hash.Length)]}); refusing to sign for the tip at {view.TipHeight}.", "VREE");
                    return;
                }
            }
        }

        private static async Task<Block?> ServedAndCertifiedAsync(NetworkStallSurvey.Reply claim)
        {
            try
            {
                var body = await HttpGet(claim.Ip, $"GetBlock/{claim.Height}");
                if (string.IsNullOrEmpty(body) || body == "0") return null;
                var block = JsonConvert.DeserializeObject<Block>(body);
                if (block == null || block.Height != claim.Height || block.GetBlockHash() != block.Hash) return null;
                return ConsensusCertificateVerifier.HasValidCertificate(block) ? block : null;
            }
            catch { return null; }
        }

        // ── Signing ─────────────────────────────────────────────────────────────────────────────

        private static long _lastSignedUnix;
        private static readonly ConcurrentDictionary<string, string> _mySignatures = new(StringComparer.Ordinal);

        internal static VreeElection.SignFacts FactsFor(VreeElection.ElectionView view, string self)
        {
            var now = TimeUtil.GetTime();
            var head = CasterMembershipStore.GetCurrent();
            var survey = _survey;
            var fresh = SurveyCounts(survey, view, 2 * SurveySeconds);
            var recent = head == null ? new List<CasterMembershipRecord>() : CasterMembershipStore.GetSince(head.RecordSeq - 5);
            return new VreeElection.SignFacts
            {
                IsElector = view.Epoch.Electors.ContainsKey(self),
                VerifiedEvidenceAgainstTip = HasEvidenceAgainst(view.TipHeight, view.TipHash),
                CommitteeUnreachable = fresh && survey!.CommitteeUnreachable,
                SurveyFresh = fresh,
                HoldActive = VreeElection.HoldActive(recent, view.TipHeight, Globals.LastBlock?.Timestamp ?? 0, now, VreeElection.BlockAt),
                RatifiedHold = VreeElection.WithinRatifiedHold(head, view.Epoch.EpochHash, now),
            };
        }

        /// <summary>This validator's signature on rung <paramref name="rung"/> of the election in view, or why not.</summary>
        internal static VreeSignatureAnswer SignRung(VreeElection.ElectionView view, int rung)
        {
            var (self, key, publicKey) = LocalKey();
            if (string.IsNullOrEmpty(self) || key == null)
                return new VreeSignatureAnswer { Refusal = "this node has no validator key" };

            var refusal = VreeElection.SignRefusal(view, rung, FactsFor(view, self));
            if (refusal != null)
                return new VreeSignatureAnswer { SignerAddress = self, Refusal = refusal };

            var record = view.Build(rung);
            if (record.Casters.Count < CasterMembershipStore.MinCommitteeSize)
                return new VreeSignatureAnswer { SignerAddress = self, Refusal = "the rung has too few members" };
            if (_mySignatures.TryGetValue($"{self}|{record.RecordHash}", out var known))
                return new VreeSignatureAnswer { SignerAddress = self, Signature = known, RecordHash = record.RecordHash };

            if (!CasterMembershipStore.TryMarkElectionSigned(view.Epoch.EpochHash, rung, record.RecordHash))
                return new VreeSignatureAnswer { SignerAddress = self, Refusal = "rule 7: another record was signed for this rung" };
            var signature = SignatureService.CreateSignature(CasterMembershipStore.CanonicalPayload(record), key, publicKey);
            if (signature == "ERROR")
                return new VreeSignatureAnswer { SignerAddress = self, Refusal = "signing failed" };

            // Rung 0 of a rotation is signed on a moving chain: the outgoing committee is not the other side of anything, and it produces until the handover.
            if (!view.IsRotation || rung > 0) _lastSignedUnix = TimeUtil.GetTime();
            if (_mySignatures.Count > 64) _mySignatures.Clear();
            _mySignatures[$"{self}|{record.RecordHash}"] = signature;
            CasterLogUtility.Log($"VREE: signed rung {rung} of the {view.Epoch.ChangeType} at tip {view.TipHeight} (record {record.RecordHash[..16]}).", "VREE");
            return new VreeSignatureAnswer { SignerAddress = self, Signature = signature, RecordHash = record.RecordHash };
        }

        /// <summary>GetElectionSignature: a read like GetBlockHash. The signature is over a record anyone can compute, so giving it away costs nothing.</summary>
        public static VreeSignatureAnswer AnswerSignature(long height, int rung)
        {
            var view = CurrentView();
            if (view == null || view.TipHeight != height)
                return new VreeSignatureAnswer { Refusal = $"no election is open at height {height} here (tip {Globals.LastBlock?.Height})" };
            StartSurveyIfDue(view);
            return SignRung(view, rung);
        }

        // ── Collecting and installing ───────────────────────────────────────────────────────────

        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> _collected = new(StringComparer.Ordinal);

        private static async Task ElectionTickAsync()
        {
            var view = CurrentView();
            if (view == null || view.Epoch.Dark || !VreeElection.Active(view.EffectiveFor(view.ClockRung)))
                return;
            var self = Globals.ValidatorAddress;
            if (string.IsNullOrEmpty(self) || !view.Epoch.Electors.ContainsKey(self))
                return;
            StartSurveyIfDue(view);
            // Nobody signs another rung during the hold, and what was collected before it does not ratify one either.
            if (VreeElection.WithinRatifiedHold(CasterMembershipStore.GetCurrent(), view.Epoch.EpochHash, TimeUtil.GetTime()))
                return;

            // Rung r starts when r opens by this member's own clock, and r-1 stays open behind it. The signer's tolerance
            // is the signer's: a collector working ahead of the clock would ratify two rungs every time.
            foreach (var rung in new[] { view.ClockRung, view.ClockRung - 1 })
            {
                if (rung < 0 || view.RungClosed(rung) != null) continue;
                if (!VreeElection.RungMembers(view.Epoch.Ranked, rung).Contains(self)) continue;
                await CollectRungAsync(view, rung, self);
            }
        }

        internal static async Task CollectRungAsync(VreeElection.ElectionView view, int rung, string self)
        {
            var record = view.Build(rung);
            var head = CasterMembershipStore.GetCurrent();
            if (head != null && string.Equals(head.RecordHash, record.RecordHash, StringComparison.OrdinalIgnoreCase))
                return; // installed

            if (_collected.Count > 16) _collected.Clear();
            var signatures = _collected.GetOrAdd(record.RecordHash, _ => new ConcurrentDictionary<string, string>(StringComparer.Ordinal));
            var mine = SignRung(view, rung);
            if (!string.IsNullOrEmpty(mine.Signature) && !string.IsNullOrEmpty(mine.SignerAddress))
                signatures[mine.SignerAddress] = mine.Signature;

            var payload = CasterMembershipStore.CanonicalPayload(record);
            var missing = view.Epoch.Electors.Where(e => e.Key != self && !signatures.ContainsKey(e.Key) && !string.IsNullOrEmpty(e.Value.IPAddress)).ToList();
            await Task.WhenAll(missing.Select(async elector =>
            {
                var body = await HttpGet(elector.Value.IPAddress.Replace("::ffff:", ""), $"GetElectionSignature/{view.TipHeight}/{rung}");
                if (string.IsNullOrEmpty(body)) return;
                VreeSignatureAnswer? answer;
                try { answer = JsonConvert.DeserializeObject<VreeSignatureAnswer>(body); } catch { return; }
                if (answer == null || string.IsNullOrEmpty(answer.Signature) || answer.SignerAddress != elector.Key) return;
                if (!string.Equals(answer.RecordHash, record.RecordHash, StringComparison.OrdinalIgnoreCase)) return;
                if (SignatureService.VerifySignature(elector.Key, payload, answer.Signature))
                    signatures[elector.Key] = answer.Signature;
            }));

            var need = view.Epoch.RatifyNeeded;
            if (signatures.Count < need)
                return;
            record.Signatures = signatures.OrderBy(s => s.Key, StringComparer.Ordinal).Take(need)
                .Select(s => new RecordSignature { SignerAddress = s.Key, Signature = s.Value }).ToList();
            await InstallAsync(record, view);
        }

        private static async Task InstallAsync(CasterMembershipRecord record, VreeElection.ElectionView view)
        {
            var head = CasterMembershipStore.GetCurrent();
            string reason;
            var installed = head != null && record.RecordSeq == head.RecordSeq
                ? CasterMembershipStore.ReplaceHeadWithOwnRung(record, out reason)
                : CasterMembershipStore.TryAppend(record, out reason);
            if (!installed)
            {
                CasterLogUtility.Log($"VREE: rung {record.Rung} ratified but not installed — {reason}.", "VREE");
                return;
            }
            var line = $"VREE: rung {record.Rung} RATIFIED by {record.Signatures.Count} of {view.Epoch.Electors.Count} electors ({record.ChangeType} seq {record.RecordSeq}, effective {record.EffectiveFromHeight}): casters [{string.Join(",", record.Casters.Select(c => c.Address))}].";
            CasterLogUtility.Log(line, "VREE");
            ConsoleWriterService.OutputValCaster($"[VREE] {line}");
            CasterMembershipService.ReconcileBlockCastersToRecord(record);

            // The old committee is what the validators pull records from, and it is dead: this goes to all of them.
            var json = JsonConvert.SerializeObject(record);
            var targets = view.Epoch.Electors.Values.Select(v => v.IPAddress)
                .Concat(record.Casters.Select(c => c.PeerIP))
                .Where(ip => !string.IsNullOrEmpty(ip)).Select(ip => ip!.Replace("::ffff:", ""))
                .Where(ip => ip != Globals.ReportedIP).Distinct().ToList();
            await Task.WhenAll(targets.Select(ip => HttpPost(ip, "AnnounceMembershipRecord", json)));
        }

        /// <summary>The record a block carries goes into the store once the block's certificate verifies under it. False: the block must wait.</summary>
        public static bool AdoptFromBlock(Block block, out string refusal) => AdoptFromBlock(block, out refusal, out _);

        /// <summary>
        /// As above. <paramref name="undo"/> is set when the record replaced siblings here: it puts the store and the caster set
        /// back as they were, for a block that is refused after its record went in (the store is outside the block's rollback).
        /// A record that only extends the head stays, as it would from gossip: it is ratified whatever its block turns out to be.
        /// </summary>
        public static bool AdoptFromBlock(Block block, out string refusal, out Action? undo)
        {
            refusal = "";
            undo = null;
            var record = block.MembershipRecord;
            if (record == null) return true;
            var head = CasterMembershipStore.GetCurrent();
            if (head == null && string.IsNullOrEmpty(Globals.ValidatorAddress)) return true; // a wallet or explorer node holds no records
            if (head != null && head.RecordSeq == record.RecordSeq && string.Equals(head.RecordHash, record.RecordHash, StringComparison.OrdinalIgnoreCase))
                return true;
            if (VreeElection.JudgeCarried(block) != VreeElection.CarriedVerdict.Valid)
            {
                refusal = "the record it carries is not valid here";
                return false;
            }
            if (!ConsensusCertificateVerifier.VerifyOrNotRequired(block))
            {
                refusal = "its certificate is not that of the record it carries";
                return false;
            }
            var storedBefore = CasterMembershipStore.RecordsFrom(record.RecordSeq);
            var castersBefore = Globals.BlockCasters;
            var wasCaster = Globals.IsBlockCaster;
            if (!CasterMembershipStore.AdoptCarried(record, out refusal))
                return false;
            CasterMembershipService.ReconcileBlockCastersToRecord(record);
            if (storedBefore.Count == 0) return true;
            undo = () =>
            {
                CasterMembershipStore.RestoreRecordsFrom(record.RecordSeq, storedBefore);
                Globals.BlockCasters = castersBefore;
                Globals.SyncKnownCastersFromBlockCasters();
                Globals.IsBlockCaster = wasCaster;
                CasterLogUtility.Log($"MEMBERSHIP: block {block.Height} was refused after its record seq {record.RecordSeq} replaced siblings; they are put back.", "MEMBERSHIP");
            };
            return true;
        }

        // ── Layer 2: the validator's side ───────────────────────────────────────────────────────

        private sealed record MyAck(long Height, string Hash, string Address, string Signature);
        private static volatile MyAck? _myAck;
        private static readonly ConcurrentDictionary<string, byte> _verifiedTips = new(StringComparer.Ordinal);
        private static long _lastPullForProbeUnix;

        public static string AckMessage(long height, string hash) => $"VFX-VREE-ACK-V1|{(Globals.IsTestNet ? "testnet" : "mainnet")}|{height}|{hash}";

        private static bool ProbeVerifies(VreeProbe probe)
        {
            // All of what the certificate signs: a hash alone let a probe with a verified hash claim any height, a free veto of every election.
            var key = $"{probe.Height}|{probe.Hash}|{probe.PrevHash}|{probe.Validator}|{probe.Version}";
            if (_verifiedTips.ContainsKey(key)) return true;
            var header = new Block { Height = probe.Height, Hash = probe.Hash, PrevHash = probe.PrevHash, Validator = probe.Validator, Version = probe.Version, ConsensusCertificate = probe.Certificate };
            if (!ConsensusCertificateVerifier.HasValidCertificate(header)) return false;
            if (_verifiedTips.Count > 32) _verifiedTips.Clear();
            _verifiedTips[key] = 1;
            return true;
        }

        /// <summary>
        /// ProbeTip. An acknowledgement means "I verified this tip, and I will not sign an election against it": a validator
        /// holding the tip's block is at it, and one behind has a verified block above its tip, which refuses every election
        /// for that tip (rule 2). So the validators a caster counts are ones that do not sign, even when they can hear the
        /// caster and not reach it. A probe of the tip a validator already holds is no such evidence, since anyone can replay
        /// one: it is acknowledged, and refuses nothing.
        /// </summary>
        public static async Task<VreeProbeAnswer> AnswerProbeAsync(VreeProbe? probe, string? senderIp)
        {
            var answer = new VreeProbeAnswer { RecordSeq = CasterMembershipStore.GetCurrent()?.RecordSeq ?? -1 };
            VreeProbeAnswer No(string why) { answer.Reason = why; answer.RecordSeq = CasterMembershipStore.GetCurrent()?.RecordSeq ?? -1; return answer; }

            var (self, key, publicKey) = LocalKey();
            var tip = Globals.LastBlock;
            if (string.IsNullOrEmpty(self) || key == null) return No("no validator key");
            if (!Globals.IsChainSynced || tip == null || tip.Height < 0) return No("not synced");
            if (probe == null || string.IsNullOrEmpty(probe.Hash) || probe.Height <= 0) return No("no tip");
            if ((probe.Certificate?.Attestations?.Count ?? 0) > MaxProbeAttestations) return No("oversized certificate");

            // Records first: a validator behind on them cannot verify a new committee's tip (the ordinary promotion's window).
            AdoptProbeRecords(probe);
            var verified = ProbeVerifies(probe);
            if (!verified && !string.IsNullOrEmpty(senderIp) && TimeUtil.GetTime() - _lastPullForProbeUnix >= ValidatorMembershipPull.RetrySeconds)
            {
                _lastPullForProbeUnix = TimeUtil.GetTime();
                ValidatorMembershipPull.RequestSoon();
                try { await CasterDiscoveryService.FetchAndAdoptMembershipAsync(new List<string> { senderIp!.Replace("::ffff:", "") }, reconcileWhenUnchanged: false); } catch { }
                verified = ProbeVerifies(probe);
            }
            if (!verified) return No("the tip's certificate does not verify here");

            if (probe.Height > tip.Height)
                NoteEvidence(probe.Height, probe.Hash);
            else
            {
                var ours = probe.Height == tip.Height ? tip.Hash : VreeElection.BlockAt(probe.Height)?.Hash;
                if (!string.Equals(ours, probe.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    if (probe.Height == tip.Height) NoteEvidence(probe.Height, probe.Hash);
                    return No("this chain holds another block at that height");
                }
            }

            var committee = CasterMembershipStore.GetCommitteeForHeight(Math.Max(probe.Height, tip.Height) + 1);
            if (committee == null || !committee.Contains(probe.Prober)) return No("the prober is not on the committee here");
            // Having signed for this stall, this validator is not to be counted on the other side of it for the hold. The
            // committee it elected is not the other side: two thirds of the electorate signed, and it needs a third of them.
            var elected = CasterMembershipStore.GetCurrent() is { } current && VreeElection.IsElectionRecord(current) && current.Casters.Any(c => c.Address == probe.Prober);
            if (!elected && TimeUtil.GetTime() - _lastSignedUnix < VreeElection.RatifiedHoldSeconds) return No("signed an election within the hold");

            var mine = _myAck;
            if (mine == null || mine.Height != probe.Height || mine.Hash != probe.Hash || mine.Address != self)
            {
                var signature = SignatureService.CreateSignature(AckMessage(probe.Height, probe.Hash), key, publicKey);
                if (signature == "ERROR") return No("signing failed");
                mine = new MyAck(probe.Height, probe.Hash, self, signature);
                _myAck = mine;
            }
            answer.Ack = true;
            answer.Address = self;
            answer.Signature = mine.Signature;
            answer.RecordSeq = CasterMembershipStore.GetCurrent()?.RecordSeq ?? -1;
            return answer;
        }

        private static void AdoptProbeRecords(VreeProbe probe)
        {
            var records = probe.Records?.Where(r => r != null).Take(MaxProbeRecords).ToList();
            if (records == null || records.Count == 0) return;
            var before = CasterMembershipStore.GetCurrent();
            if (before != null && records.Max(r => r.RecordSeq) <= before.RecordSeq) return;
            if (CasterMembershipStore.TryAppendChain(records) > 0 && CasterMembershipStore.GetCurrent() is { } head)
                CasterMembershipService.ReconcileBlockCastersToRecord(head);
        }

        // ── Layer 2: the caster's side ──────────────────────────────────────────────────────────

        private static readonly ConcurrentDictionary<string, long> _acks = new(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, long> _peerSeq = new(StringComparer.Ordinal);
        private static long _lastProbeUnix;
        private static long _firstProbeUnix;
        private static volatile int _reachCount;
        private static string _probedCommittee = "";

        private sealed record ReachCache(string Key, long At, Dictionary<string, string> Ips);
        private static volatile ReachCache? _reachCache;

        /// <summary>The reach set of this caster, address to IP: the eligible set minus its own committee.</summary>
        private static Dictionary<string, string> ReachSet(long tipHeight, string tipHash, HashSet<string> committee)
        {
            var key = $"{VrfSeed.AnchorHeight(tipHeight)}|{CasterMembershipStore.ComputeCasterSetHash(committee)}";
            var now = TimeUtil.GetTime();
            var cached = _reachCache;
            if (cached != null && cached.Key == key && now - cached.At < ReachSetCacheSeconds) return cached.Ips;
            var epoch = VreeElection.ReachSetFor(tipHeight, tipHash, committee);
            var ips = epoch?.Electors.ToDictionary(e => e.Key, e => (e.Value.IPAddress ?? "").Replace("::ffff:", ""), StringComparer.Ordinal)
                      ?? new Dictionary<string, string>(StringComparer.Ordinal);
            _reachCache = new ReachCache(key, now, ips);
            return ips;
        }

        /// <summary>The tip to probe with: the newest of the last few blocks that has its certificate here.</summary>
        private static Block? CertifiedTip(Block tip)
        {
            for (var h = tip.Height; h >= Math.Max(1, tip.Height - 3); h--)
            {
                var block = h == tip.Height && tip.ConsensusCertificate != null ? tip : VreeElection.BlockAt(h);
                if (block?.ConsensusCertificate?.Attestations?.Count > 0) return block;
            }
            return null;
        }

        private static async Task ProbeIfDueAsync()
        {
            var tip = Globals.LastBlock;
            var self = Globals.ValidatorAddress;
            if (!Globals.IsBlockCaster || Globals.IsBootstrapMode || tip == null || tip.Height <= 0 || string.IsNullOrEmpty(self)) return;
            var committee = CasterMembershipStore.GetCommitteeForHeight(tip.Height + 1);
            if (committee == null || !committee.Contains(self)) return;
            var now = TimeUtil.GetTime();
            if (now - _lastProbeUnix < VreeElection.ProbeSeconds) return;
            _lastProbeUnix = now;
            await ProbeOnceAsync(tip, committee, self, now);
        }

        internal static async Task ProbeOnceAsync(Block tip, HashSet<string> committee, string self, long now)
        {
            // Acknowledgements are of this committee's tip: a new committee starts counting from nothing.
            var committeeKey = CasterMembershipStore.ComputeCasterSetHash(committee);
            if (committeeKey != _probedCommittee)
            {
                _probedCommittee = committeeKey;
                _acks.Clear();
                _firstProbeUnix = 0;
            }

            var reach = ReachSet(tip.Height, tip.Hash, committee);
            _reachCount = reach.Count;
            if (reach.Count < VreeElection.MinElectors) return;
            var certified = CertifiedTip(tip);
            if (certified == null) return;
            if (_firstProbeUnix == 0) _firstProbeUnix = now;

            var head = CasterMembershipStore.GetCurrent();
            var message = AckMessage(certified.Height, certified.Hash);
            await Task.WhenAll(reach.Where(r => r.Value.Length > 0).Select(async validator =>
            {
                var probe = new VreeProbe
                {
                    Height = certified.Height, Hash = certified.Hash, PrevHash = certified.PrevHash, Validator = certified.Validator, Version = certified.Version,
                    Certificate = certified.ConsensusCertificate, Prober = self,
                    Records = RecordsFor(validator.Key, head),
                };
                var body = await HttpPost(validator.Value, "ProbeTip", JsonConvert.SerializeObject(probe));
                if (string.IsNullOrEmpty(body)) return;
                VreeProbeAnswer? answer;
                try { answer = JsonConvert.DeserializeObject<VreeProbeAnswer>(body); } catch { return; }
                if (answer == null) return;
                _peerSeq[validator.Key] = answer.RecordSeq;
                if (answer.Ack && answer.Address == validator.Key && SignatureService.VerifySignature(validator.Key, message, answer.Signature))
                    _acks[validator.Key] = now;
            }));

            foreach (var gone in _acks.Keys.Where(a => !reach.ContainsKey(a)).ToList())
                _acks.TryRemove(gone, out _);
            var count = AckCount(TimeUtil.GetTime());
            var line = $"VREE-REACH: {count} of {reach.Count} validators acknowledged within {VreeElection.ReachWindowSeconds}s (need {VreeElection.ReachNeed(reach.Count)}) at {certified.Height}.";
            CasterLogUtility.Log(line, "VREE");
        }

        /// <summary>The records a validator lacks, from what it last reported. One it never answered gets the last two: enough for the ordinary case, and a few KB.</summary>
        private static List<CasterMembershipRecord> RecordsFor(string validator, CasterMembershipRecord? head)
        {
            if (head == null) return new List<CasterMembershipRecord>();
            var since = _peerSeq.TryGetValue(validator, out var seq) ? seq : head.RecordSeq - 2;
            if (since >= head.RecordSeq) return new List<CasterMembershipRecord>();
            since = Math.Max(since, head.RecordSeq - MaxProbeRecords);
            return CasterMembershipStore.GetSince(since);
        }

        private static int AckCount(long now) => _acks.Values.Count(at => now - at <= VreeElection.ReachWindowSeconds);

        /// <summary>
        /// Pure: why a caster must stand down, or null. Before the height nothing stands down: casters only count. A caster
        /// that has not been probing for a whole window has not measured anything yet, and a seed bootstrap has its own rules.
        /// </summary>
        internal static string? StandDownDecision(bool active, bool bootstrap, bool caster, long firstProbeUnix, long now, int acks, int reachSet)
        {
            if (!active || bootstrap || !caster || firstProbeUnix == 0) return null;
            if (now - firstProbeUnix < VreeElection.ReachWindowSeconds) return null;
            if (VreeElection.ReachHeld(acks, reachSet)) return null;
            return $"only {acks} of {reachSet} validators acknowledged this caster's tip within {VreeElection.ReachWindowSeconds}s (need {VreeElection.ReachNeed(reachSet)}); it resumes when they return";
        }

        /// <summary>Why this caster must not cast or attest at <paramref name="height"/>, or null: its committee's term is over, or it lost the validators.</summary>
        public static string? StandDownReason(long height)
        {
            if (!VreeElection.Active(height)) return null;
            if (VreeElection.Lapsed(CasterMembershipStore.GetForHeight(height), height, CasterMembershipStore.GetBySeq))
                return "this committee's term is over; a scheduled rotation replaces it";
            // A rung before its first block: validators holding a sibling do not acknowledge it until that block, which it
            // could then never produce. Standing down here would strand it; one block is what this can cost.
            if (VreeElection.FreshlyElected(CasterMembershipStore.GetCurrent(), Globals.LastBlock?.Height ?? -1, Globals.ValidatorAddress))
                return null;
            var now = TimeUtil.GetTime();
            return StandDownDecision(true, Globals.IsBootstrapMode, Globals.IsBlockCaster, _firstProbeUnix, now, AckCount(now), _reachCount);
        }

        // ── Read-only ───────────────────────────────────────────────────────────────────────────

        /// <summary>What Health reports: how many of the reach set acknowledge this caster, the number the activation height is set from.</summary>
        public static (long AckCount, int ReachSet, bool Active, string? StandDown) Status()
        {
            var next = (Globals.LastBlock?.Height ?? -1) + 1;
            return (AckCount(TimeUtil.GetTime()), _reachCount, VreeElection.Active(next), StandDownReason(next));
        }

        /// <summary>
        /// GetElectorate: V(E) as this node computes it near its tip. Anyone may ask, so it takes no height (an old one would scan
        /// old blocks for whoever asks), and it moves only every hundred blocks so that asking costs one computation.
        /// </summary>
        public static object? Electorate()
        {
            var tip = Globals.LastBlock;
            if (tip == null || tip.Height <= 0) return null;
            var h = tip.Height - tip.Height % 100;
            var hash = h == tip.Height ? tip.Hash : VrfSeed.BlockHashAt(h);
            var epoch = string.IsNullOrEmpty(hash) ? null : VreeElection.EpochForStall(h, hash!);
            if (epoch == null) return new { Height = h, Available = false };
            return new
            {
                Height = h,
                Available = true,
                Active = VreeElection.Active(h + 1),
                Anchor = epoch.Anchor,
                Electors = epoch.Electors.Count,
                Candidates = epoch.Ranked.Count,
                RatifyNeed = epoch.RatifyNeeded,
                ReachNeed = VreeElection.ReachNeed(epoch.Electors.Count),
                Dark = epoch.Dark,
                Rung0 = VreeElection.RungMembers(epoch.Ranked, 0),
                Addresses = epoch.Electors.Keys.OrderBy(a => a, StringComparer.Ordinal).ToList(),
            };
        }
    }
}
