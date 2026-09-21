using System.Text;
using Newtonsoft.Json;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Services
{
    /// <summary>
    /// BOOTSTRAP-RESET (Sep 2026, testnet 975,533): automates what the operator did by hand to end
    /// the outage — replace a membership record whose quorum can no longer be met with the
    /// hardcoded seed set — but only when a stall is PROVEN, and never when the machine cannot
    /// tell "dead" from "partitioned".
    ///
    /// Runs on seeds, inside an active seed agreement, when a record exists. Ladder:
    ///  1. tip age ≥ <see cref="CasterMembershipStore.BootstrapResetMinStallSeconds"/> (30 min);
    ///  2. the live seeds cannot meet the current committee's quorum on their own (otherwise heal +
    ///     normal rounds will recover and a reset is unnecessary);
    ///  3. <see cref="NetworkStallSurvey"/> returns Stalled on <see cref="RequiredConsecutiveSurveys"/>
    ///     consecutive surveys ≥ <see cref="SurveyIntervalSeconds"/> apart — any other verdict resets
    ///     the streak. PeerAhead / HashDisagreement / InsufficientReach are never overridable.
    ///  4. CommitteeUnreachable is the undecidable case: NOT overridden automatically. It raises a
    ///     throttled operator alarm carrying the survey evidence and the exact command; an operator
    ///     on any one seed runs the localhost <c>BootstrapResetForce</c> endpoint, which lets the
    ///     next survey window treat CommitteeUnreachable as Stalled — the other gates still apply.
    ///  5. Proposal: seq head+1, effective tip+1 (a stalled chain never reaches a margin), casters =
    ///     seeds, co-signed by the agreed seeds via SignMembershipRecord, appended, broadcast.
    /// Every other node accepts it only under its own stale-tip rule (ValidateBootstrapReset).
    /// <see cref="Decide"/> is pure and unit-tested.
    /// </summary>
    public static class BootstrapResetService
    {
        public const int RequiredConsecutiveSurveys = 3;
        public const int SurveyIntervalSeconds = 60;
        public const int ForceWindowSeconds = 900;
        public const int AlarmIntervalSeconds = 300;

        public enum Decision { NotStalledYet, QuorumReachableBySeeds, WaitingForSurvey, StreakReset, StreakAdvanced, OperatorRequired, Propose }

        public sealed class State
        {
            public int Consecutive;
            public long LastSurveyUnix;
            public long ForceUntilUnix;
            public long LastAlarmUnix;
            public string LastVerdict = "";
            public string LastDetail = "";
        }

        private static readonly State _live = new();
        private static int _running;

        public static bool ForceArmed => TimeUtil.GetTime() < _live.ForceUntilUnix;
        public static int Consecutive => _live.Consecutive;
        public static string LastVerdict => _live.LastVerdict;
        public static string LastDetail => _live.LastDetail;

        /// <summary>Operator: treat CommitteeUnreachable as Stalled for the next window. Localhost-only caller.</summary>
        public static void ForceNextReset()
        {
            _live.ForceUntilUnix = TimeUtil.GetTime() + ForceWindowSeconds;
            _live.Consecutive = 0;
            var msg = $"BOOTSTRAP-RESET: operator FORCE armed for {ForceWindowSeconds}s — CommitteeUnreachable will count as Stalled; PeerAhead / HashDisagreement / InsufficientReach still veto.";
            CasterLogUtility.Log(msg, "BOOTSTRAP-RESET");
            ConsoleWriterService.OutputValCaster($"[BOOTSTRAP-RESET] {msg}");
        }

        /// <summary>
        /// Pure ladder step. <paramref name="verdict"/> is null when no survey ran this tick.
        /// </summary>
        public static Decision Decide(State s, long nowUnix, long tipAgeSeconds, bool quorumReachableBySeeds, NetworkStallSurvey.Verdict? verdict)
        {
            if (tipAgeSeconds < CasterMembershipStore.BootstrapResetMinStallSeconds)
            {
                s.Consecutive = 0;
                return Decision.NotStalledYet;
            }
            if (quorumReachableBySeeds)
            {
                s.Consecutive = 0;
                return Decision.QuorumReachableBySeeds;
            }
            if (verdict == null)
                return Decision.WaitingForSurvey;

            var forced = nowUnix < s.ForceUntilUnix;
            var counts = verdict == NetworkStallSurvey.Verdict.Stalled
                      || (forced && verdict == NetworkStallSurvey.Verdict.CommitteeUnreachable);

            if (!counts)
            {
                s.Consecutive = 0;
                return verdict == NetworkStallSurvey.Verdict.CommitteeUnreachable ? Decision.OperatorRequired : Decision.StreakReset;
            }

            s.Consecutive++;
            return s.Consecutive >= RequiredConsecutiveSurveys ? Decision.Propose : Decision.StreakAdvanced;
        }

        /// <summary>Called from BootstrapCoordinationService's Agreed branch (≈15 s cadence).</summary>
        public static async Task TickAsync()
        {
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
                return;
            try
            {
                if (!Globals.IsLocalBootstrapCaster || !BootstrapCoordinationService.AgreementActive)
                    return;
                var head = CasterMembershipStore.GetCurrent();
                if (head == null)
                    return; // no record → genesis minting handles it
                if (BootstrapCoordinationService.AgreedSeedCount < CasterMembershipStore.GenesisMinSeedSignatures)
                    return;

                var now = TimeUtil.GetTime();
                var tipAge = CasterMembershipStore.TipAgeSeconds();

                var committee = head.Casters.Select(c => c.Address).ToHashSet(StringComparer.Ordinal);
                var seedsInCommittee = BootstrapCoordinationService.AgreedSeedAddresses.Count(a => committee.Contains(a));
                var quorumReachable = seedsInCommittee >= ConsensusQuorum.Required(head.Casters.Count);

                NetworkStallSurvey.Verdict? verdict = null;
                NetworkStallSurvey.Result? result = null;
                var surveyDue = tipAge >= CasterMembershipStore.BootstrapResetMinStallSeconds
                                && !quorumReachable
                                && now - _live.LastSurveyUnix >= SurveyIntervalSeconds;
                if (surveyDue)
                {
                    _live.LastSurveyUnix = now;
                    result = await NetworkStallSurvey.RunAsync(head);
                    verdict = result.Verdict;
                    _live.LastVerdict = result.Verdict.ToString();
                    _live.LastDetail = result.Detail;
                }

                var decision = Decide(_live, now, tipAge, quorumReachable, verdict);
                switch (decision)
                {
                    case Decision.StreakAdvanced:
                        CasterLogUtility.Log($"BOOTSTRAP-RESET: survey {_live.Consecutive}/{RequiredConsecutiveSurveys} consistent — {result?.Detail}", "BOOTSTRAP-RESET");
                        break;
                    case Decision.StreakReset:
                        CasterLogUtility.Log($"BOOTSTRAP-RESET: veto ({verdict}) — {result?.Detail}. Streak reset; no reset while this holds.", "BOOTSTRAP-RESET");
                        break;
                    case Decision.OperatorRequired:
                        RaiseOperatorAlarm(head, result!, now);
                        break;
                    case Decision.Propose:
                        _live.Consecutive = 0;
                        await ProposeAsync(head, result!);
                        break;
                }
            }
            catch (Exception ex)
            {
                CasterLogUtility.Log($"BOOTSTRAP-RESET: tick error — {ex.Message}", "BOOTSTRAP-RESET");
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        }

        private static void RaiseOperatorAlarm(CasterMembershipRecord head, NetworkStallSurvey.Result r, long now)
        {
            if (now - _live.LastAlarmUnix < AlarmIntervalSeconds)
                return;
            _live.LastAlarmUnix = now;
            var msg =
                $"BOOTSTRAP-RESET NEEDS OPERATOR: chain stalled {CasterMembershipStore.TipAgeSeconds()}s at h={Globals.LastBlock?.Height}; committee seq {head.RecordSeq} " +
                $"({head.Casters.Count} members, quorum {ConsensusQuorum.Required(head.Casters.Count)}) cannot be met by the live seeds, and the committee majority did NOT answer the survey " +
                $"({r.CommitteeResponded}/{r.CommitteeKnown} answered; reached {r.Responded}/{r.Known} nodes; none ahead; all agree on our hash). " +
                $"Dead or partitioned cannot be told apart from here. Silent: [{string.Join(",", r.Silent.Take(10))}]. " +
                $"If you have confirmed those nodes are down (not partitioned), run on any ONE seed: curl http://localhost:{Globals.ValAPIPort}/valapi/validator/BootstrapResetForce";
            ErrorLogUtility.LogError(msg, "BootstrapResetService");
            ConsoleWriterService.OutputValCaster($"[BOOTSTRAP-RESET] {msg}");
        }

        private static async Task ProposeAsync(CasterMembershipRecord head, NetworkStallSurvey.Result evidence)
        {
            var account = AccountData.GetLocalValidator();
            if (account?.GetPrivKey == null || string.IsNullOrEmpty(Globals.ValidatorAddress))
                return;

            var seeds = SeedNodeService.GetBootstrapSeedPeers()
                .Where(p => !string.IsNullOrEmpty(p.ValidatorAddress))
                .Select(p => new CasterInfo { Address = p.ValidatorAddress!, PeerIP = (p.PeerIP ?? "").Replace("::ffff:", ""), PublicKey = p.ValidatorPublicKey ?? "" })
                .OrderBy(c => c.Address, StringComparer.Ordinal)
                .ToList();

            var candidate = new CasterMembershipRecord
            {
                RecordSeq = head.RecordSeq + 1,
                EffectiveFromHeight = Math.Max(head.EffectiveFromHeight + 1, (Globals.LastBlock?.Height ?? 0) + 1),
                PrevRecordHash = head.RecordHash,
                ChangeType = CasterMembershipStore.BootstrapResetChangeType,
                ChangedAddress = "",
                Casters = seeds,
                Signatures = new List<RecordSignature>()
            };
            candidate.RecordHash = CasterMembershipStore.ComputeRecordHash(candidate);

            if (!CasterMembershipStore.TryMarkSigned(candidate.RecordSeq, candidate.RecordHash, CasterMembershipStore.ComputeCasterSetHash(candidate)))
            {
                CasterLogUtility.Log($"BOOTSTRAP-RESET: refusing to propose — already signed a different set at seq {candidate.RecordSeq}.", "BOOTSTRAP-RESET");
                return;
            }
            var payload = CasterMembershipStore.CanonicalPayload(candidate);
            var selfSig = SignatureService.CreateSignature(payload, account.GetPrivKey, account.PublicKey);
            if (selfSig == "ERROR") return;
            candidate.Signatures.Add(new RecordSignature { SignerAddress = Globals.ValidatorAddress, Signature = selfSig });

            CasterLogUtility.Log(
                $"BOOTSTRAP-RESET: PROPOSING seq={candidate.RecordSeq} effective={candidate.EffectiveFromHeight} casters=[{string.Join(",", seeds.Select(s => s.Address))}] — evidence: {evidence.Detail}",
                "BOOTSTRAP-RESET");
            ConsoleWriterService.OutputValCaster($"[BOOTSTRAP-RESET] Proposing committee reset to the seed set (seq {candidate.RecordSeq}) — survey proved the network stalled.");

            var signRequest = JsonConvert.SerializeObject(new MembershipSignRequest { Candidate = candidate, ProposerAddress = Globals.ValidatorAddress });
            foreach (var seed in BootstrapCoordinationService.AgreedSeedPeers)
            {
                if (candidate.Signatures.Count >= CasterMembershipStore.GenesisMinSeedSignatures) break;
                if (seed.Address == Globals.ValidatorAddress || string.IsNullOrEmpty(seed.Ip)) continue;
                try
                {
                    using var client = Globals.HttpClientFactory.CreateClient();
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    var uri = $"http://{seed.Ip}:{Globals.ValAPIPort}/valapi/validator/SignMembershipRecord";
                    using var content = new StringContent(signRequest, Encoding.UTF8, "application/json");
                    var resp = await client.PostAsync(uri, content, cts.Token);
                    if (!resp.IsSuccessStatusCode) continue;
                    var body = await resp.Content.ReadAsStringAsync();
                    var sig = JsonConvert.DeserializeObject<RecordSignature>(body);
                    if (sig == null || sig.SignerAddress != seed.Address) continue;
                    if (!SignatureService.VerifySignature(sig.SignerAddress, payload, sig.Signature)) continue;
                    if (!candidate.Signatures.Any(s => s.SignerAddress == sig.SignerAddress))
                        candidate.Signatures.Add(sig);
                }
                catch { /* seed unreachable — next window retries */ }
            }

            if (candidate.Signatures.Count < CasterMembershipStore.GenesisMinSeedSignatures)
            {
                CasterLogUtility.Log($"BOOTSTRAP-RESET: {candidate.Signatures.Count}/{CasterMembershipStore.GenesisMinSeedSignatures} seed signatures — not appended; releasing mark, next window retries.", "BOOTSTRAP-RESET");
                CasterMembershipStore.ReleaseSignedMark(candidate.RecordSeq, CasterMembershipStore.ComputeCasterSetHash(candidate));
                return;
            }

            if (!CasterMembershipStore.TryAppend(candidate, out var reason))
            {
                CasterLogUtility.Log($"BOOTSTRAP-RESET: append refused — {reason}.", "BOOTSTRAP-RESET");
                return;
            }

            _live.ForceUntilUnix = 0;
            ConsoleWriterService.OutputValCaster($"[BOOTSTRAP-RESET] Committee reset INSTALLED (seq {candidate.RecordSeq}): casters are now the seed set. Production resumes on the next round.");
            await CasterMembershipService.BroadcastRecordAsync(candidate);
            CasterMembershipService.ReconcileBlockCastersToRecord(candidate);
        }
    }
}
