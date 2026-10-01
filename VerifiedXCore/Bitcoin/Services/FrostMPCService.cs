using NBitcoin;
using VerifiedXCore.Bitcoin.FROST;
using VerifiedXCore.Bitcoin.FROST.Models;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using System.Net.Http.Json;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VerifiedXCore.Models;
using VerifiedXCore.Models.SmartContracts;

namespace VerifiedXCore.Bitcoin.Services
{
    /// <summary>
    /// FROST MPC Service - Coordinates DKG and signing ceremonies across validators
    /// This is the C# orchestration layer that wraps the FROST protocol
    /// </summary>
    public class FrostMPCService
    {
        private static readonly HttpClient _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        /// <summary>
        /// Dedicated long-timeout HttpClient for DKG Round 2 share generation.
        /// FROST crypto with 85+ participants involves generating 84 secret shares per validator,
        /// which can take significant time. 90-second timeout accommodates this.
        /// </summary>
        private static readonly HttpClient _ceremonyHttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(90)
        };

        #region Validator Reachability

        /// <summary>
        /// Dedicated short-timeout HttpClient for health check probes.
        /// Uses a 5-second timeout so we don't wait 30 seconds per offline validator.
        /// </summary>
        private static readonly HttpClient _probeHttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        /// <summary>
        /// Probe all validators to find which ones are actually reachable on the FROST port.
        /// Uses the /health endpoint with a short timeout to quickly identify online validators.
        /// This prevents the DKG ceremony from being passed 34 validators when only 4 are online.
        /// </summary>
        /// <param name="validators">All registered/active validators to probe</param>
        /// <returns>List of validators that responded successfully to the health check</returns>
        public static async Task<List<VBTCValidator>> ProbeValidatorReachability(List<VBTCValidator> validators)
        {
            var reachable = new System.Collections.Concurrent.ConcurrentBag<VBTCValidator>();
            var unreachableCount = 0;

            LogUtility.Log($"[FROST MPC] Probing {validators.Count} validators for FROST port reachability...", 
                "FrostMPCService.ProbeValidatorReachability");

            var tasks = validators.Select(async validator =>
            {
                try
                {
                    // Validate IP before making HTTP call
                    if (!InputValidationHelper.ValidateValidatorIPAddress(validator.IPAddress, out string ipError))
                    {
                        Interlocked.Increment(ref unreachableCount);
                        return;
                    }

                    var url = $"http://{validator.IPAddress}:{Globals.FrostValidatorPort}/health";
                    var response = await _probeHttpClient.GetAsync(url);

                    if (response.IsSuccessStatusCode)
                    {
                        var body = await response.Content.ReadAsStringAsync();
                        if (body.Contains("\"Success\":true") || body.Contains("\"Success\": true"))
                        {
                            reachable.Add(validator);
                            return;
                        }
                    }

                    Interlocked.Increment(ref unreachableCount);
                }
                catch
                {
                    // Timeout or connection refused — validator is offline
                    Interlocked.Increment(ref unreachableCount);
                }
            });

            await Task.WhenAll(tasks);

            var reachableList = reachable.ToList();

            LogUtility.Log($"[FROST MPC] Reachability probe complete: {reachableList.Count}/{validators.Count} validators online on FROST port {Globals.FrostValidatorPort}. " +
                $"({unreachableCount} unreachable)", "FrostMPCService.ProbeValidatorReachability");

            if (reachableList.Count > 0)
            {
                var addressList = string.Join(", ", reachableList.Select(v => v.ValidatorAddress));
                LogUtility.Log($"[FROST MPC] Reachable validators: {addressList}", "FrostMPCService.ProbeValidatorReachability");
            }

            return reachableList;
        }

        /// <summary>One outstanding signed withdrawal tx on a contract, merged across the validators that report it.</summary>
        public sealed class ContractPinView
        {
            public string WithdrawalRequestHash { get; set; } = "";
            public string BtcTxId { get; set; } = "";
            public HashSet<string> Outpoints { get; set; } = new();
            /// <summary>True only when every validator that reported the pin marks it reclaimable (a withheld tx).</summary>
            public bool Reclaimable { get; set; }
        }

        /// <summary>
        /// Reads /frost/pins/{scUID} from the contract's validators so a withdrawal is built from coins no other
        /// withdrawal's signed transaction holds. Pins are merged by withdrawal; a pin is reclaimable only when
        /// every validator reporting it agrees (the stricter view wins). AnyAnswered is false when no validator
        /// served the endpoint (older build or unreachable) — the build then proceeds without exclusions and a
        /// validator refusal names the held coins instead.
        /// </summary>
        public static async Task<(bool AnyAnswered, List<ContractPinView> Pins)> FetchContractPins(string scUID, List<VBTCValidator> validators)
        {
            var merged = new System.Collections.Concurrent.ConcurrentDictionary<string, ContractPinView>(StringComparer.OrdinalIgnoreCase);
            var answered = 0;

            var tasks = validators.Select(async validator =>
            {
                try
                {
                    if (!InputValidationHelper.ValidateValidatorIPAddress(validator.IPAddress, out _))
                        return;
                    var response = await _probeHttpClient.GetAsync($"http://{validator.IPAddress}:{Globals.FrostValidatorPort}/frost/pins/{Uri.EscapeDataString(scUID)}");
                    if (!response.IsSuccessStatusCode)
                        return;
                    var body = JObject.Parse(await response.Content.ReadAsStringAsync());
                    if (body["Success"]?.ToObject<bool>() != true)
                        return;
                    Interlocked.Increment(ref answered);

                    foreach (var p in body["Pins"] as JArray ?? new JArray())
                    {
                        var wrh = p["WithdrawalRequestHash"]?.ToObject<string>() ?? "";
                        if (string.IsNullOrEmpty(wrh))
                            continue;
                        var reclaimable = p["Reclaimable"]?.ToObject<bool>() == true;
                        var outpoints = p["Outpoints"]?.ToObject<List<string>>() ?? new List<string>();
                        merged.AddOrUpdate(wrh,
                            _ => new ContractPinView
                            {
                                WithdrawalRequestHash = wrh,
                                BtcTxId = p["BtcTxId"]?.ToObject<string>() ?? "",
                                Outpoints = outpoints.Select(o => o.ToLowerInvariant()).ToHashSet(),
                                Reclaimable = reclaimable
                            },
                            (_, existing) =>
                            {
                                lock (existing)
                                {
                                    foreach (var o in outpoints) existing.Outpoints.Add(o.ToLowerInvariant());
                                    existing.Reclaimable &= reclaimable;
                                }
                                return existing;
                            });
                    }
                }
                catch
                {
                    // Unreachable or older validator: it contributes nothing; its refusal (if any) surfaces at signing.
                }
            });

            await Task.WhenAll(tasks);
            return (answered > 0, merged.Values.ToList());
        }

        #endregion

        #region DKG Ceremony - Taproot Address Generation

        /// <summary>The outcome of a key ceremony run: the result, the contract UID it was attested for, and who was dropped.</summary>
        public sealed class FrostDkgRun
        {
            public FrostDKGResult? Result { get; set; }
            /// <summary>The contract UID the validators attested and stored their key packages under (see CoordinateDKGCeremony).</summary>
            public string? ContractUID { get; set; }
            public int Attempts { get; set; }
            /// <summary>Participants dropped from the ceremony, with the reason (address -> reason).</summary>
            public Dictionary<string, string> Excluded { get; } = new(StringComparer.Ordinal);
            public string? Error { get; set; }
        }

        internal sealed class DkgAttempt
        {
            public FrostDKGResult? Result;
            public readonly Dictionary<string, string> Failed = new(StringComparer.Ordinal);
            public string? Error;
        }

        /// <summary>Test seam: runs one attempt (contract UID, session, participants, auth) in place of the validator network.</summary>
        internal static Func<string, string, List<VBTCValidator>, PreSignedLeaderAuth?, Task<DkgAttempt>>? AttemptRunnerForTests;

        /// <summary>
        /// Attempts per ceremony: each failed attempt drops the participants that caused it, so the participation floor
        /// bounds the loop; this caps the time. (Mainnet: three attempts were spent on two slow Round 2 answers, one missed
        /// start and the three validators that never finish.)
        /// </summary>
        public const int MaxDkgAttempts = 6;

        /// <summary>
        /// Participants that failed a key ceremony coordinated by this node, until when, and why. A later ceremony leaves
        /// them out from the start (while the participation floor still holds), instead of spending attempts finding them
        /// again: the mainnet validators that never finish failed every ceremony at its last step.
        /// </summary>
        internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Until, string Reason)> RecentDkgFailures = new(StringComparer.Ordinal);
        public const long DkgFailureMemorySeconds = 6 * 3600;

        /// <summary>
        /// Coordinate a FROST key ceremony (DKG) for a vBTC V2 vault. Any node (validator or wallet) can coordinate; the
        /// coordinator only relays and never holds key material.
        ///
        /// A DKG needs every participant to finish, and consensus (NEW-26) needs an attestation from every participant, so
        /// one participant that fails - by accident or on purpose - used to fail the whole ceremony (mainnet: 3 of 162
        /// validators did not finish and no vault could be created). Now each attempt that fails names the participants
        /// that caused it (did not join, no or invalid Round 1 commitment, no shares, a bad or unopenable share reported by
        /// its recipients and resolved by FrostDkgBlame, did not finish, a different group key, no valid attestation); they
        /// are dropped and the ceremony runs again, up to MaxDkgAttempts times, while the remaining participants still meet
        /// <paramref name="participantShortfall"/> (the NEW-26 participation rule).
        ///
        /// Every attempt is a separate ceremony with its own session and its own contract UID: validators that finished an
        /// earlier attempt stored a key package under its UID and refuse a second, different key for it. The first attempt
        /// uses <paramref name="ceremonyId"/>, so an untroubled ceremony's contract UID is its ceremony id;
        /// <see cref="FrostDkgRun.ContractUID"/> is the one to create the contract with.
        ///
        /// <paramref name="preSignedAuths"/> (web wallet flow) holds one pre-signed leader authorization per attempt; the
        /// number given bounds the attempts.
        /// </summary>
        public static async Task<FrostDkgRun> CoordinateDKGCeremony(
            string ceremonyId,
            string ownerAddress,
            List<VBTCValidator> validators,
            int threshold,
            Action<int, int>? progressCallback = null,
            IReadOnlyList<PreSignedLeaderAuth>? preSignedAuths = null,
            Func<int, string?>? participantShortfall = null)
        {
            var run = new FrostDkgRun();
            var leaderAddress = ownerAddress;
            var attempts = preSignedAuths is { Count: > 0 } ? Math.Min(MaxDkgAttempts, preSignedAuths.Count) : MaxDkgAttempts;
            var participants = validators.GroupBy(v => v.ValidatorAddress, StringComparer.Ordinal).Select(g => g.First()).ToList();
            progressCallback?.Invoke(0, 5);

            // Leave out participants that failed a recent ceremony, unless that would breach the participation floor.
            var now = TimeUtil.GetTime();
            var remembered = participants
                .Select(v => (v.ValidatorAddress, Known: RecentDkgFailures.TryGetValue(v.ValidatorAddress, out var f) && f.Until > now ? f.Reason : null))
                .Where(r => r.Known != null).ToList();
            if (remembered.Count > 0)
            {
                var remaining = participants.Count - remembered.Count;
                if (remaining >= FrostDkgAttestation.MinParticipants && participantShortfall?.Invoke(remaining) == null)
                {
                    foreach (var (address, reason) in remembered)
                        run.Excluded[address] = $"failed a recent key ceremony ({reason})";
                    LogUtility.Log($"[FROST MPC] Leaving out {remembered.Count} validator(s) that failed a recent key ceremony: " +
                        string.Join(", ", remembered.Select(r => r.ValidatorAddress)), "FrostMPCService.CoordinateDKGCeremony");
                }
                else
                {
                    LogUtility.Log($"[FROST MPC] Keeping {remembered.Count} validator(s) that failed a recent key ceremony: leaving them out would breach the participation floor.",
                        "FrostMPCService.CoordinateDKGCeremony");
                }
            }

            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                participants = participants.Where(v => !run.Excluded.ContainsKey(v.ValidatorAddress)).ToList();
                var shortfall = participants.Count < FrostDkgAttestation.MinParticipants
                    ? $"Only {participants.Count} validator(s) remain; a key ceremony needs at least {FrostDkgAttestation.MinParticipants}."
                    : participantShortfall?.Invoke(participants.Count);
                if (shortfall != null)
                {
                    run.Error = run.Excluded.Count == 0 ? shortfall
                        : $"{shortfall} {run.Excluded.Count} validator(s) were dropped after failing the ceremony.";
                    break;
                }

                run.Attempts = attempt;
                var auth = preSignedAuths is { Count: > 0 } ? preSignedAuths[attempt - 1] : null;
                var contractUid = attempt == 1 ? ceremonyId : FrostDkgAttestation.NewContractUid();
                var sessionId = !string.IsNullOrEmpty(auth?.SessionId) ? auth!.SessionId : Guid.NewGuid().ToString();
                LogUtility.Log($"[FROST MPC] DKG attempt {attempt}/{attempts}. Ceremony: {ceremonyId}, Contract: {contractUid}, Session: {sessionId}, " +
                    $"Validators: {participants.Count}, Threshold: {threshold}%, PreSigned: {auth != null}", "FrostMPCService.CoordinateDKGCeremony");

                var outcome = AttemptRunnerForTests != null
                    ? await AttemptRunnerForTests(contractUid, sessionId, participants, auth)
                    : await RunDkgAttempt(contractUid, sessionId, leaderAddress, participants, threshold, progressCallback, auth);
                if (outcome.Result != null)
                {
                    run.Result = outcome.Result;
                    run.ContractUID = contractUid;
                    run.Error = null;
                    foreach (var p in outcome.Result.ParticipantAddresses ?? new List<string>())
                        RecentDkgFailures.TryRemove(p, out _);
                    LogUtility.Log($"[FROST MPC] DKG ceremony completed on attempt {attempt}. Contract: {contractUid}, Address: {outcome.Result.TaprootAddress}, " +
                        $"Participants: {outcome.Result.ParticipantAddresses.Count}, Dropped: {run.Excluded.Count}", "FrostMPCService.CoordinateDKGCeremony");
                    return run;
                }

                run.Error = outcome.Error ?? "The key ceremony failed.";
                foreach (var (address, reason) in outcome.Failed)
                {
                    run.Excluded.TryAdd(address, reason);
                    RecentDkgFailures[address] = (TimeUtil.GetTime() + DkgFailureMemorySeconds, reason);
                    LogUtility.Log($"[FROST MPC] DKG attempt {attempt}: dropping {address} — {reason}", "FrostMPCService.CoordinateDKGCeremony");
                }
                if (outcome.Failed.Count == 0)
                    break; // nobody to drop: another attempt would fail the same way
            }

            LogUtility.Log($"[FROST MPC] DKG ceremony {ceremonyId} failed after {run.Attempts} attempt(s): {run.Error}", "FrostMPCService.CoordinateDKGCeremony");
            return run;
        }

        /// <summary>One ceremony over exactly these participants; on failure, the participants that caused it.</summary>
        private static async Task<DkgAttempt> RunDkgAttempt(
            string contractUid,
            string sessionId,
            string leaderAddress,
            List<VBTCValidator> validators,
            int threshold,
            Action<int, int>? progressCallback,
            PreSignedLeaderAuth? preSignedAuth)
        {
            var attempt = new DkgAttempt();
            try
            {
                // Start: FROST DKG needs every participant from here on.
                await BroadcastDKGStart(sessionId, contractUid, leaderAddress, validators, threshold, preSignedAuth, attempt.Failed);
                if (attempt.Failed.Count > 0)
                {
                    attempt.Error = $"{attempt.Failed.Count} of {validators.Count} validators did not join the key ceremony.";
                    return attempt;
                }
                progressCallback?.Invoke(0, 10);

                // Round 1: each validator's own commitment, and its proof of knowledge (a bad one fails every honest
                // participant's Round 2, which would otherwise look like everyone failing).
                progressCallback?.Invoke(1, 30);
                var commitments = await CollectDKGRound1Commitments(sessionId, validators);
                foreach (var v in validators.Where(v => !commitments.ContainsKey(v.ValidatorAddress)))
                    attempt.Failed[v.ValidatorAddress] = "did not provide a Round 1 commitment";
                if (attempt.Failed.Count == 0)
                {
                    var minSigners = FrostDkgAttestation.ThresholdFor(validators.Count, threshold);
                    var invalid = FrostDkgBlame.InvalidRound1Packages(commitments, validators.Select(v => v.ValidatorAddress).ToList(), minSigners);
                    if (invalid == null)
                        LogUtility.Log("[FROST MPC] Round 1 commitment check unavailable (native library); continuing without it.", "FrostMPCService.RunDkgAttempt");
                    else
                        foreach (var address in invalid)
                            attempt.Failed[address] = "invalid Round 1 commitment (proof of knowledge)";
                }
                if (attempt.Failed.Count > 0)
                {
                    attempt.Error = $"Round 1 failed for {attempt.Failed.Count} of {validators.Count} validators.";
                    return attempt;
                }
                progressCallback?.Invoke(1, 40);

                // Round 2: shares generated by each validator and delivered to each recipient.
                progressCallback?.Invoke(2, 50);
                await CoordinateShareDistribution(sessionId, validators, commitments, leaderAddress, preSignedAuth, attempt.Failed);
                if (attempt.Failed.Count > 0)
                {
                    attempt.Error = $"Round 2 failed for {attempt.Failed.Count} of {validators.Count} validators.";
                    return attempt;
                }
                progressCallback?.Invoke(2, 65);

                // Results: every participant must have finished with the same key and attested it.
                progressCallback?.Invoke(3, 85);
                attempt.Result = await AggregateDKGResult(sessionId, contractUid, validators, threshold, leaderAddress, attempt.Failed);
                if (attempt.Result == null)
                    attempt.Error = attempt.Failed.Count > 0
                        ? $"{attempt.Failed.Count} of {validators.Count} validators did not finish the key ceremony with a valid attestation."
                        : "No validator returned a usable key ceremony result.";
                else
                    progressCallback?.Invoke(3, 100);
                return attempt;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"DKG attempt error: {ex.Message}", "FrostMPCService.RunDkgAttempt");
                attempt.Error = "The key ceremony failed unexpectedly.";
                return attempt;
            }
        }

        /// <summary>
        /// Broadcast the DKG start to every participant; each one that does not accept it is added to
        /// <paramref name="failed"/> (FROST DKG needs all of them).
        /// </summary>
        private static async Task BroadcastDKGStart(
            string sessionId,
            string contractUid,
            string leaderAddress,
            List<VBTCValidator> validators,
            int threshold,
            PreSignedLeaderAuth? preSignedAuth,
            Dictionary<string, string> failed)
        {
            // Sign with leader's key using deterministic message format
            // Any VFX wallet owner can be the leader — not just validators
            // When preSignedAuth is provided (web wallet flow), use the pre-signed signature
            // instead of calling AddressSignature() which requires a local private key.
            long timestamp;
            string leaderSignature;

            if (preSignedAuth != null && !string.IsNullOrEmpty(preSignedAuth.StartSignature))
            {
                timestamp = preSignedAuth.StartTimestamp;
                leaderSignature = preSignedAuth.StartSignature;
                LogUtility.Log($"[FROST MPC] Using pre-signed leader auth for DKG start (web wallet flow)",
                    "FrostMPCService.BroadcastDKGStart");
            }
            else
            {
                timestamp = TimeUtil.GetTime();
                var leaderMessage = $"{sessionId}.{leaderAddress}.{timestamp}";
                leaderSignature = VerifiedXCore.Services.SignatureService.AddressSignature(leaderAddress, leaderMessage);
            }

            var startRequest = new FrostDKGStartRequest
            {
                SessionId = sessionId,
                SmartContractUID = contractUid, // NEW-26: the contract UID the validators attest and store their key under
                LeaderAddress = leaderAddress,
                Timestamp = timestamp,
                LeaderSignature = leaderSignature,
                ParticipantAddresses = validators.Select(v => v.ValidatorAddress).ToList(),
                RequiredThreshold = threshold
            };

            var failures = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
            var tasks = validators.Select(async validator =>
            {
                try
                {
                    // FIND-007 Fix: Defensive IP validation before HTTP call (last resort)
                    if (!InputValidationHelper.ValidateValidatorIPAddress(validator.IPAddress, out string ipError))
                    {
                        ErrorLogUtility.LogError($"FIND-007 Security (HTTP Client): Blocked HTTP call to invalid validator IP. Address: {validator.ValidatorAddress}, IP: {validator.IPAddress}, Error: {ipError}",
                            "FrostMPCService.BroadcastDKGStart");
                        failures[validator.ValidatorAddress] = "invalid IP address";
                        return;
                    }

                    var url = $"http://{validator.IPAddress}:{Globals.FrostValidatorPort}/frost/dkg/start";
                    var response = await _httpClient.PostAsJsonAsync(url, startRequest);
                    if (!response.IsSuccessStatusCode)
                    {
                        var errorBody = await response.Content.ReadAsStringAsync();
                        LogUtility.Log($"[FROST MPC] DKG Start REJECTED by {validator.ValidatorAddress}: HTTP {(int)response.StatusCode} — {errorBody}",
                            "FrostMPCService.BroadcastDKGStart");
                        failures[validator.ValidatorAddress] = $"refused the DKG start (HTTP {(int)response.StatusCode})";
                    }
                }
                catch (Exception ex)
                {
                    LogUtility.Log($"[FROST MPC] Failed to contact validator {validator.ValidatorAddress}: {ex.Message}",
                        "FrostMPCService.BroadcastDKGStart");
                    failures[validator.ValidatorAddress] = "did not answer the DKG start";
                }
            });
            await Task.WhenAll(tasks);

            foreach (var (address, reason) in failures) failed[address] = reason;
            LogUtility.Log($"[FROST MPC] DKG Start broadcast: {validators.Count - failures.Count}/{validators.Count} joined",
                "FrostMPCService.BroadcastDKGStart");
        }

        /// <summary>
        /// Collect Round 1 commitments from all validators in parallel. Each validator's own commitment is taken only from
        /// that validator: a validator answering with a commitment under another participant's address is ignored (it
        /// could otherwise substitute an honest participant's commitment and make that participant appear to fail).
        /// </summary>
        private static async Task<Dictionary<string, string>> CollectDKGRound1Commitments(
            string sessionId,
            List<VBTCValidator> validators)
        {
            var commitments = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.Ordinal);
            try
            {
                LogUtility.Log($"[FROST MPC] Collecting Round 1 commitments from {validators.Count} validators (parallel)...",
                    "FrostMPCService.CollectDKGRound1Commitments");

                // Allow time for validators to complete Round 1 generation
                await Task.Delay(2000);

                var tasks = validators.Select(async validator =>
                {
                    try
                    {
                        var url = $"http://{validator.IPAddress}:{Globals.FrostValidatorPort}/frost/dkg/round1/{sessionId}";
                        var response = await _httpClient.GetAsync(url);
                        if (!response.IsSuccessStatusCode)
                            return;

                        var json = JObject.Parse(await response.Content.ReadAsStringAsync());
                        if (json["Success"]?.Value<bool>() == true
                            && json["SessionId"]?.Value<string>() == sessionId
                            && json["Commitments"] is JObject commitmentsObj
                            && commitmentsObj[validator.ValidatorAddress]?.Type == JTokenType.String)
                        {
                            var own = commitmentsObj[validator.ValidatorAddress]!.Value<string>();
                            if (!string.IsNullOrEmpty(own))
                                commitments[validator.ValidatorAddress] = own;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogUtility.Log($"[FROST MPC] Failed to collect commitment from {validator.ValidatorAddress}: {ex.Message}",
                            "FrostMPCService.CollectDKGRound1Commitments");
                    }
                });
                await Task.WhenAll(tasks);
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Round 1 collection error: {ex.Message}", "FrostMPCService.CollectDKGRound1Commitments");
            }

            LogUtility.Log($"[FROST MPC] Collected {commitments.Count}/{validators.Count} commitments",
                "FrostMPCService.CollectDKGRound1Commitments");
            return commitments.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal);
        }

        /// <summary>
        /// Round 2: each validator generates its shares from all Round 1 commitments, and each recipient is sent the shares
        /// addressed to it. Validators that produce no shares or do not accept theirs are added to <paramref name="failed"/>;
        /// recipients' reports of a share that would not open or did not match its sender's commitment are resolved by
        /// FrostDkgBlame.ResolveAccusations (validators from this build report them; older ones just do not finish).
        /// </summary>
        private static async Task CoordinateShareDistribution(
            string sessionId,
            List<VBTCValidator> validators,
            Dictionary<string, string> commitments,
            string leaderAddress,
            PreSignedLeaderAuth? preSignedAuth,
            Dictionary<string, string> failed)
        {
            LogUtility.Log($"[FROST MPC] Coordinating share distribution for {validators.Count} validators (parallel)...",
                "FrostMPCService.CoordinateShareDistribution");

            // Step 1: Send all Round 1 commitments to each validator in parallel and collect their generated Round 2 shares.
            // Uses _ceremonyHttpClient (90s timeout) because FROST crypto with many participants is computationally heavy.
            var commitmentPayload = JsonConvert.SerializeObject(commitments);
            var allGeneratedShares = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.Ordinal);
            var generationFailures = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.Ordinal);

            var round2Tasks = validators.Select(async validator =>
            {
                try
                {
                    var url = $"http://{validator.IPAddress}:{Globals.FrostValidatorPort}/frost/dkg/round2/{sessionId}";
                    var content = new StringContent(commitmentPayload, Encoding.UTF8, "application/json");
                    var response = await _ceremonyHttpClient.PostAsync(url, content);
                    var responseBody = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                    {
                        LogUtility.Log($"[FROST MPC] DKG Round 2 REJECTED by {validator.ValidatorAddress}: HTTP {(int)response.StatusCode} — {responseBody}",
                            "FrostMPCService.CoordinateShareDistribution");
                        generationFailures[validator.ValidatorAddress] = $"did not generate Round 2 shares (HTTP {(int)response.StatusCode})";
                        return;
                    }
                    var json = JObject.Parse(responseBody);
                    var sharesData = json["Success"]?.Value<bool>() == true ? json["GeneratedShares"]?.ToString() : null;
                    if (string.IsNullOrEmpty(sharesData))
                    {
                        generationFailures[validator.ValidatorAddress] = "did not generate Round 2 shares";
                        return;
                    }
                    allGeneratedShares[validator.ValidatorAddress] = sharesData;
                    LogUtility.Log($"[FROST MPC] Collected Round 2 shares from {validator.ValidatorAddress}",
                        "FrostMPCService.CoordinateShareDistribution");
                }
                catch (Exception ex)
                {
                    LogUtility.Log($"[FROST MPC] Failed to collect Round 2 shares from {validator.ValidatorAddress}: {ex.Message}",
                        "FrostMPCService.CoordinateShareDistribution");
                    generationFailures[validator.ValidatorAddress] = "did not answer Round 2";
                }
            });
            await Task.WhenAll(round2Tasks);

            if (!generationFailures.IsEmpty)
            {
                foreach (var (address, reason) in generationFailures) failed[address] = reason;
                return;
            }
            LogUtility.Log($"[FROST MPC] Collected Round 2 shares from {allGeneratedShares.Count}/{validators.Count} validators. Redistributing...",
                "FrostMPCService.CoordinateShareDistribution");

            // Step 2: deliver to each validator the shares addressed to it.
            // Use the same leaderAddress that was used in the DKG start so validators accept the request
            // (they check leaderAddr == session.LeaderAddress). When preSignedAuth is provided (web wallet flow),
            // use the pre-signed signature.
            long timestamp;
            string leaderSignature;
            if (preSignedAuth != null && !string.IsNullOrEmpty(preSignedAuth.ShareDistributionSignature) && preSignedAuth.ShareDistributionTimestamp.HasValue)
            {
                timestamp = preSignedAuth.ShareDistributionTimestamp.Value;
                leaderSignature = preSignedAuth.ShareDistributionSignature;
                LogUtility.Log($"[FROST MPC] Using pre-signed leader auth for share distribution (web wallet flow)",
                    "FrostMPCService.CoordinateShareDistribution");
            }
            else
            {
                timestamp = TimeUtil.GetTime();
                var leaderMessage = $"{sessionId}.{leaderAddress}.{timestamp}";
                leaderSignature = VerifiedXCore.Services.SignatureService.AddressSignature(leaderAddress, leaderMessage);
            }

            // Each validator gets only the shares addressed to it. Sending every validator all n×(n-1) shares made the
            // upload grow with n³: ~10 MB per validator and ~1.6 GB in total at 162 validators, so every request hit
            // the 90 s timeout and mainnet DKG never got past Round 2.
            var sharesByRecipient = SharesForEachRecipient(validators.Select(v => v.ValidatorAddress).ToList(), allGeneratedShares);

            var participantSet = validators.Select(v => v.ValidatorAddress).ToHashSet(StringComparer.Ordinal);
            var deliveryFailures = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.Ordinal);
            var accusations = new System.Collections.Concurrent.ConcurrentBag<(string Accuser, string Accused)>();
            var distributeTasks = validators.Select(async validator =>
            {
                try
                {
                    var url = $"http://{validator.IPAddress}:{Globals.FrostValidatorPort}/frost/dkg/shares/{sessionId}";
                    var redistributePayload = JsonConvert.SerializeObject(new
                    {
                        LeaderAddress = leaderAddress,
                        Timestamp = timestamp,
                        LeaderSignature = leaderSignature,
                        AllGeneratedShares = sharesByRecipient[validator.ValidatorAddress]
                    });
                    var content = new StringContent(redistributePayload, Encoding.UTF8, "application/json");
                    var response = await _ceremonyHttpClient.PostAsync(url, content);
                    if (!response.IsSuccessStatusCode)
                    {
                        deliveryFailures[validator.ValidatorAddress] = $"did not accept its Round 2 shares (HTTP {(int)response.StatusCode})";
                        return;
                    }
                    try
                    {
                        var json = JObject.Parse(await response.Content.ReadAsStringAsync());
                        if (json["Accusations"] is JObject reported)
                            foreach (var p in reported.Properties())
                                if (participantSet.Contains(p.Name))
                                {
                                    accusations.Add((validator.ValidatorAddress, p.Name));
                                    LogUtility.Log($"[FROST MPC] {validator.ValidatorAddress} reports the share from {p.Name}: {p.Value}",
                                        "FrostMPCService.CoordinateShareDistribution");
                                }
                    }
                    catch { /* an older validator's answer: no reports */ }
                }
                catch (Exception ex)
                {
                    LogUtility.Log($"[FROST MPC] Failed to distribute shares to {validator.ValidatorAddress}: {ex.Message}",
                        "FrostMPCService.CoordinateShareDistribution");
                    deliveryFailures[validator.ValidatorAddress] = "did not answer the share delivery";
                }
            });
            await Task.WhenAll(distributeTasks);

            LogUtility.Log($"[FROST MPC] Share redistribution complete: {validators.Count - deliveryFailures.Count}/{validators.Count} validators received shares" +
                (accusations.IsEmpty ? "" : $", {accusations.Count} share report(s)"), "FrostMPCService.CoordinateShareDistribution");

            foreach (var (address, reason) in deliveryFailures) failed[address] = reason;
            foreach (var address in FrostDkgBlame.ResolveAccusations(accusations))
                failed.TryAdd(address, "Round 2 share dispute (a share that would not open or did not match its commitment)");
        }

        /// <summary>
        /// The Round 2 share batch for each participant: per sender, only the one package addressed to that participant
        /// ({ sender: "{ identifier: sealedPackage }" }), in the shape the validators' /frost/dkg/shares handler already
        /// reads (it parses each sender's map and looks up its own identifier), so validators need no upgrade.
        /// Identifiers come from the sorted participant list, as the validators derive them. A sender whose map cannot be
        /// read, or lacks the participant's identifier, is passed through whole (the previous behaviour).
        /// </summary>
        internal static Dictionary<string, Dictionary<string, string>> SharesForEachRecipient(
            List<string> participantAddresses, IReadOnlyDictionary<string, string> generatedSharesBySender)
        {
            var idByAddress = FrostStartup.BuildAddressToIdentifierMap(participantAddresses);
            var parsed = new Dictionary<string, JObject?>();
            foreach (var (sender, shares) in generatedSharesBySender)
            {
                try { parsed[sender] = JObject.Parse(shares); }
                catch { parsed[sender] = null; }
            }

            var result = new Dictionary<string, Dictionary<string, string>>();
            foreach (var recipient in participantAddresses)
            {
                var batch = new Dictionary<string, string>();
                idByAddress.TryGetValue(recipient, out var recipientId);
                foreach (var (sender, shares) in generatedSharesBySender)
                {
                    if (sender == recipient)
                        continue;
                    var token = recipientId == null ? null : parsed[sender]?[recipientId];
                    batch[sender] = token == null
                        ? shares
                        : new JObject { [recipientId!] = token }.ToString(Formatting.None);
                }
                result[recipient] = batch;
            }
            return result;
        }

        /// <summary>
        /// Collect each participant's DKG result. The result needs every participant to have finished with the same group
        /// key and to have returned a valid attestation (NEW-26 consensus); participants that did not finish, report a
        /// different key than the majority, or attest invalidly are added to <paramref name="failed"/> and no result is
        /// returned. The Taproot address must be the group key's (FrostDkgAttestation.DeriveTaprootAddress).
        /// </summary>
        private static async Task<FrostDKGResult?> AggregateDKGResult(
            string sessionId,
            string contractUid,
            List<VBTCValidator> validators,
            int threshold,
            string leaderAddress,
            Dictionary<string, string> failed)
        {
            LogUtility.Log($"[FROST MPC] Collecting DKG results from {validators.Count} validators...", "FrostMPCService.AggregateDKGResult");

            var results = new System.Collections.Concurrent.ConcurrentDictionary<string, (string Gpk, string Address, FrostDkgAttestation.Attestation? Attestation)>(StringComparer.Ordinal);
            var resultTasks = validators.Select(async validator =>
            {
                try
                {
                    var url = $"http://{validator.IPAddress}:{Globals.FrostValidatorPort}/frost/dkg/result/{sessionId}";
                    var response = await _httpClient.GetAsync(url);
                    if (!response.IsSuccessStatusCode)
                        return;
                    var json = JObject.Parse(await response.Content.ReadAsStringAsync());
                    if (json["Success"]?.Value<bool>() != true || json["IsCompleted"]?.Value<bool>() != true)
                        return;
                    var gpk = json["GroupPublicKey"]?.Value<string>();
                    var addr = json["TaprootAddress"]?.Value<string>();
                    FrostDkgAttestation.Attestation? attestation = null;
                    try { attestation = json["Attestation"]?.Type == JTokenType.Object ? json["Attestation"]!.ToObject<FrostDkgAttestation.Attestation>() : null; } catch { }
                    if (!string.IsNullOrEmpty(gpk) && !string.IsNullOrEmpty(addr))
                        results[validator.ValidatorAddress] = (gpk, addr, attestation);
                }
                catch (Exception ex)
                {
                    LogUtility.Log($"[FROST MPC] Failed to collect DKG result from {validator.ValidatorAddress}: {ex.Message}",
                        "FrostMPCService.AggregateDKGResult");
                }
            });
            await Task.WhenAll(resultTasks);

            foreach (var v in validators.Where(v => !results.ContainsKey(v.ValidatorAddress)))
                failed[v.ValidatorAddress] = "did not finish the key ceremony";
            if (results.IsEmpty)
                return null;

            // The key the majority finished with; a participant reporting another key is dropped.
            var majority = results.GroupBy(r => (r.Value.Gpk, r.Value.Address)).OrderByDescending(g => g.Count()).First();
            var groupPublicKey = majority.Key.Gpk;
            var taprootAddress = majority.Key.Address;
            foreach (var r in results.Where(r => r.Value.Gpk != groupPublicKey || r.Value.Address != taprootAddress))
                failed[r.Key] = "reported a different group key";

            // NEW-26: the address must be the group key's Taproot address (consensus derives it the same way).
            try
            {
                if (BitcoinAddress.Create(taprootAddress, Globals.BTCNetwork) is not TaprootAddress
                    || FrostDkgAttestation.DeriveTaprootAddress(groupPublicKey, FrostDkgAttestation.ConsensusNetwork) != taprootAddress)
                {
                    ErrorLogUtility.LogError($"FROST DKG: address {taprootAddress} is not the Taproot address of the group key", "FrostMPCService.AggregateDKGResult");
                    return null;
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"FROST DKG: Invalid Taproot address '{taprootAddress}': {ex.Message}", "FrostMPCService.AggregateDKGResult");
                return null;
            }

            // NEW-26 (follow-up): participants, threshold and owner are the ceremony's own (the validators sign them from
            // their sessions); consensus requires one valid attestation from every participant, by that participant.
            var participants = FrostDkgAttestation.Canonical(validators.Select(v => v.ValidatorAddress));
            var signingThreshold = FrostDkgAttestation.ThresholdFor(participants.Count, threshold);
            var attestations = new List<FrostDkgAttestation.Attestation>();
            foreach (var r in results.Where(r => !failed.ContainsKey(r.Key)))
            {
                var a = r.Value.Attestation;
                if (a != null && a.ValidatorAddress == r.Key
                    && FrostDkgAttestation.Verify(a, contractUid, groupPublicKey, taprootAddress, leaderAddress, signingThreshold, participants))
                    attestations.Add(a);
                else
                    failed[r.Key] = "did not return a valid attestation";
            }
            LogUtility.Log($"[FROST MPC] DKG attested by {attestations.Count}/{validators.Count} validators for contract {contractUid}",
                "FrostMPCService.AggregateDKGResult");
            if (failed.Count > 0)
                return null;

            var dkgProof = FrostDkgAttestation.BuildProof(contractUid, groupPublicKey, taprootAddress, leaderAddress, signingThreshold, participants, attestations);
            LogUtility.Log($"[FROST MPC] DKG aggregation complete. GroupPubKey: {groupPublicKey.Substring(0, 16)}..., Address: {taprootAddress}",
                "FrostMPCService.AggregateDKGResult");

            return new FrostDKGResult
            {
                SessionId = sessionId,
                SmartContractUID = contractUid,
                GroupPublicKey = groupPublicKey,
                TaprootAddress = taprootAddress,
                DKGProof = dkgProof ?? string.Empty,
                CompletionTimestamp = TimeUtil.GetTime(),
                ParticipantAddresses = validators.Select(v => v.ValidatorAddress).ToList(),
                Threshold = threshold
            };
        }

        #endregion

        #region Signing Ceremony - Withdrawal Transaction Signing

        /// <summary>
        /// Coordinate a FROST 2-round signing ceremony for a Bitcoin withdrawal transaction
        /// Returns the aggregated Schnorr signature
        /// </summary>
        /// <param name="messageHash">Bitcoin transaction sighash (BIP 341)</param>
        /// <param name="scUID">Smart contract UID</param>
        /// <param name="validators">List of active validators to participate</param>
        /// <param name="threshold">Required threshold percentage</param>
        /// <param name="inputIndex">Which transaction input this ceremony signs (multi-input withdrawals run one ceremony per input)</param>
        /// <returns>A FrostCeremonyOutcome — Result is set on success; on failure FailureCode/Detail/counts identify the exact cause</returns>
        public static async Task<FrostCeremonyOutcome> CoordinateSigningCeremony(
            string messageHash,
            string scUID,
            List<VBTCValidator> validators,
            int threshold,
            string? ceremonyId = null,
            string? coordinatorAddress = null,
            string? withdrawalRequestHash = null,
            PreSignedLeaderAuth? preSignedAuth = null,
            int inputIndex = 0,
            FrostSigningTxContext? txContext = null)
        {
            // Reuse the pre-signed session ID if provided (web wallet flow), otherwise generate a new one.
            // The pre-signed signatures embed this session ID in the message, so it MUST match.
            var sessionId = !string.IsNullOrEmpty(preSignedAuth?.SessionId) ? preSignedAuth.SessionId : Guid.NewGuid().ToString();
            long abortTimestamp = 0;
            string abortSignature = string.Empty;
            string abortLeader = string.Empty;
            bool startBroadcast = false;

            // Fire-and-forget abort so validators drop the dead session and start their retry
            // cooldown instead of leaving state dangling until TTLs expire.
            void AbortCeremony()
            {
                if (startBroadcast && !string.IsNullOrEmpty(abortSignature))
                    _ = BroadcastSigningAbort(sessionId, abortLeader, abortTimestamp, abortSignature, validators);
            }

            try
            {
                // Use provided coordinator address, fall back to validator address, then first validator
                // Note: Globals.ValidatorAddress is "" (not null) for non-validators, so ?? won't fall through
                var leaderAddress = !string.IsNullOrEmpty(coordinatorAddress) ? coordinatorAddress
                    : !string.IsNullOrEmpty(Globals.ValidatorAddress) ? Globals.ValidatorAddress
                    : validators.First().ValidatorAddress;

                var requiredCount = GetRequiredValidatorCount(validators.Count, threshold);

                LogUtility.Log($"[FROST MPC] sid={sessionId} input={inputIndex} wrh={Shorten(withdrawalRequestHash)} phase=start — starting ceremony. Validators: {validators.Count}, required: {requiredCount}, PreSignedSession: {preSignedAuth?.SessionId != null}", "FrostMPCService.CoordinateSigningCeremony");

                // Phase 1: Broadcast signing start
                var (startCount, startFailures, startTimestamp, startSignature) = await BroadcastSigningStart(sessionId, messageHash, scUID, leaderAddress, validators, threshold, ceremonyId, withdrawalRequestHash, preSignedAuth, inputIndex, txContext);
                abortTimestamp = startTimestamp;
                abortSignature = startSignature;
                abortLeader = leaderAddress;
                startBroadcast = startCount > 0;

                if (startCount < requiredCount)
                {
                    // Distinguish "validators actively rejected us" (409 dedup / 403 bad signature — the
                    // rejection bodies say why) from "not enough validators reachable".
                    var code = startFailures.Any(f => f.HttpStatus > 0)
                        ? FrostCeremonyFailureCode.StartRejectedByValidators
                        : FrostCeremonyFailureCode.StartInsufficientResponses;
                    var outcome = FrostCeremonyOutcome.Fail(code, sessionId,
                        "signing start did not reach threshold", startCount, requiredCount, validators.Count, startFailures, inputIndex);
                    LogUtility.Log($"[FROST MPC] sid={sessionId} input={inputIndex} wrh={Shorten(withdrawalRequestHash)} phase=start ok={startCount}/{requiredCount}/{validators.Count} code={code} — {outcome.Describe()}", "FrostMPCService.CoordinateSigningCeremony");
                    AbortCeremony();
                    return outcome;
                }

                // Phase 2: Signing Round 1 - Nonce commitments
                var round1Nonces = await CollectSigningRound1Nonces(sessionId, validators);
                if (round1Nonces == null || round1Nonces.Count < requiredCount)
                {
                    var got = round1Nonces?.Count ?? 0;
                    LogUtility.Log($"[FROST MPC] sid={sessionId} input={inputIndex} wrh={Shorten(withdrawalRequestHash)} phase=r1 ok={got}/{requiredCount}/{validators.Count} code={FrostCeremonyFailureCode.Round1InsufficientNonces}", "FrostMPCService.CoordinateSigningCeremony");
                    AbortCeremony();
                    return FrostCeremonyOutcome.Fail(FrostCeremonyFailureCode.Round1InsufficientNonces, sessionId,
                        "insufficient nonce commitments", got, requiredCount, validators.Count, null, inputIndex);
                }

                // Phase 3: Signing Round 2 - Signature shares
                var round2Shares = await CollectSigningRound2Shares(sessionId, validators, round1Nonces, leaderAddress, startTimestamp, startSignature);
                if (round2Shares == null || round2Shares.Count < requiredCount)
                {
                    var got = round2Shares?.Count ?? 0;
                    LogUtility.Log($"[FROST MPC] sid={sessionId} input={inputIndex} wrh={Shorten(withdrawalRequestHash)} phase=r2 ok={got}/{requiredCount}/{validators.Count} code={FrostCeremonyFailureCode.Round2InsufficientShares}", "FrostMPCService.CoordinateSigningCeremony");
                    AbortCeremony();
                    return FrostCeremonyOutcome.Fail(FrostCeremonyFailureCode.Round2InsufficientShares, sessionId,
                        "insufficient signature shares", got, requiredCount, validators.Count, null, inputIndex);
                }

                // Phase 4: Aggregate signature
                // FIND-026 Fix: Pass scUID and ordered signer addresses for correct pubkey package lookup
                // and FROST Identifier remapping
                var signerAddresses = validators.Select(v => v.ValidatorAddress).ToList();
                var (signingResult, aggCode, aggDetail) = await AggregateSignature(sessionId, messageHash, scUID, signerAddresses, validators, threshold, round2Shares, ceremonyId);
                if (signingResult != null)
                {
                    LogUtility.Log($"[FROST MPC] sid={sessionId} input={inputIndex} wrh={Shorten(withdrawalRequestHash)} phase=agg — ceremony completed successfully", "FrostMPCService.CoordinateSigningCeremony");
                    return FrostCeremonyOutcome.Ok(signingResult, sessionId, inputIndex);
                }

                // Previously this path returned null with NO log at this level — an aggregation
                // failure was indistinguishable from every other cause. Note: validators that
                // generated shares stay Signed after the abort; the idempotent same-sighash rule
                // makes the retry work regardless.
                LogUtility.Log($"[FROST MPC] sid={sessionId} input={inputIndex} wrh={Shorten(withdrawalRequestHash)} phase=agg code={aggCode} — {aggDetail}", "FrostMPCService.CoordinateSigningCeremony");
                AbortCeremony();
                return FrostCeremonyOutcome.Fail(aggCode, sessionId, aggDetail,
                    round2Shares.Count, requiredCount, validators.Count, null, inputIndex);
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Signing ceremony error. sid={sessionId} input={inputIndex}: {ex}", "FrostMPCService.CoordinateSigningCeremony");
                AbortCeremony();
                return FrostCeremonyOutcome.Fail(FrostCeremonyFailureCode.CoordinatorException, sessionId, ApiErrorText.For(ex), inputIndex: inputIndex);
            }
        }

        /// <summary>
        /// Fire-and-forget: tell all validators to drop a dead signing session. Authenticated by
        /// replaying the original start signature (see /frost/sign/abort). Failures are ignored —
        /// validator-side TTLs and staleness rules remain the backstop.
        /// </summary>
        private static async Task BroadcastSigningAbort(string sessionId, string leaderAddress, long timestamp, string leaderSignature, List<VBTCValidator> validators)
        {
            try
            {
                var abortRequest = new FrostSigningAbortRequest
                {
                    SessionId = sessionId,
                    LeaderAddress = leaderAddress,
                    Timestamp = timestamp,
                    LeaderSignature = leaderSignature
                };

                var tasks = validators.Select(async validator =>
                {
                    try
                    {
                        var url = $"http://{validator.IPAddress}:{Globals.FrostValidatorPort}/frost/sign/abort";
                        await _httpClient.PostAsJsonAsync(url, abortRequest);
                    }
                    catch { /* best effort */ }
                });

                await Task.WhenAll(tasks);
                LogUtility.Log($"[FROST MPC] sid={sessionId} abort broadcast to {validators.Count} validator(s)", "FrostMPCService.BroadcastSigningAbort");
            }
            catch (Exception ex)
            {
                LogUtility.Log($"[FROST MPC] sid={sessionId} abort broadcast error (ignored): {ex.Message}", "FrostMPCService.BroadcastSigningAbort");
            }
        }

        /// <summary>
        /// First 16 chars of a hash for compact log lines.
        /// </summary>
        /// <summary>
        /// Round messages prove leadership by replaying the session's start signature
        /// (validators check it against the session they created at sign/start).
        /// </summary>
        private static void AddLeaderAuthHeaders(HttpRequestMessage req, string leaderAddress, long startTimestamp, string startSignature)
        {
            req.Headers.TryAddWithoutValidation("X-Frost-Leader", leaderAddress);
            req.Headers.TryAddWithoutValidation("X-Frost-Timestamp", startTimestamp.ToString());
            req.Headers.TryAddWithoutValidation("X-Frost-Signature", startSignature);
        }

        private static string Shorten(string? hash)
        {
            if (string.IsNullOrEmpty(hash)) return "-";
            return hash.Length <= 16 ? hash : hash.Substring(0, 16);
        }

        /// <summary>
        /// Broadcast signing ceremony start to all validators.
        /// Returns the success count plus each rejecting validator's HTTP status and response body —
        /// the body carries the validator's actual refusal reason (tracker dedup, bad leader
        /// signature, session collision), which is the single most useful diagnostic in a failed
        /// ceremony.
        /// </summary>
        private static async Task<(int SuccessCount, List<FrostValidatorFailure> Failures, long Timestamp, string LeaderSignature)> BroadcastSigningStart(
            string sessionId,
            string messageHash,
            string scUID,
            string leaderAddress,
            List<VBTCValidator> validators,
            int threshold,
            string? ceremonyId = null,
            string? withdrawalRequestHash = null,
            PreSignedLeaderAuth? preSignedAuth = null,
            int inputIndex = 0,
            FrostSigningTxContext? txContext = null)
        {
            // Sign with leader's key using deterministic message format
            // Any VFX wallet owner can be the leader — not just validators
            // When preSignedAuth is provided (web wallet flow), use the pre-signed signature.
            long timestamp;
            string signingLeaderSignature;

            if (preSignedAuth != null && !string.IsNullOrEmpty(preSignedAuth.StartSignature))
            {
                timestamp = preSignedAuth.StartTimestamp;
                signingLeaderSignature = preSignedAuth.StartSignature;
                LogUtility.Log($"[FROST MPC] Using pre-signed leader auth for signing start (web wallet flow)",
                    "FrostMPCService.BroadcastSigningStart");
            }
            else
            {
                timestamp = TimeUtil.GetTime();
                var signingLeaderMessage = $"{sessionId}.{leaderAddress}.{timestamp}";
                signingLeaderSignature = VerifiedXCore.Services.SignatureService.AddressSignature(leaderAddress, signingLeaderMessage);
            }

            try
            {
                var startRequest = new FrostSigningStartRequest
                {
                    SessionId = sessionId,
                    MessageHash = messageHash,
                    SmartContractUID = scUID,
                    CeremonyId = ceremonyId,
                    LeaderAddress = leaderAddress,
                    Timestamp = timestamp,
                    LeaderSignature = signingLeaderSignature,
                    SignerAddresses = validators.Select(v => v.ValidatorAddress).ToList(),
                    RequiredThreshold = threshold,
                    WithdrawalRequestHash = withdrawalRequestHash,  // FIND-028: For validator-side dedup
                    InputIndex = inputIndex,
                    InputCount = txContext?.InputCount ?? 1,
                    AllInputSighashes = txContext?.AllInputSighashes,
                    TxInputOutpoints = txContext?.TxInputOutpoints,
                    BtcTxId = txContext?.BtcTxId,
                    UnsignedTxHex = txContext?.UnsignedTxHex,
                    Prevouts = txContext?.Prevouts
                };

                var tasks = validators.Select(async validator =>
                {
                    try
                    {
                        var url = $"http://{validator.IPAddress}:{Globals.FrostValidatorPort}/frost/sign/start";
                        LogUtility.Log($"[FROST MPC] Signing start → contacting {validator.ValidatorAddress} at {url}", "FrostMPCService.BroadcastSigningStart");
                        var response = await _httpClient.PostAsJsonAsync(url, startRequest);
                        if (!response.IsSuccessStatusCode)
                        {
                            var errorBody = await response.Content.ReadAsStringAsync();
                            LogUtility.Log($"[FROST MPC] Signing start REJECTED by {validator.ValidatorAddress} ({validator.IPAddress}): HTTP {(int)response.StatusCode} — {errorBody}",
                                "FrostMPCService.BroadcastSigningStart");
                            return (Success: false, Failure: new FrostValidatorFailure
                            {
                                ValidatorAddress = validator.ValidatorAddress,
                                HttpStatus = (int)response.StatusCode,
                                Message = errorBody.Length > 300 ? errorBody.Substring(0, 300) : errorBody
                            });
                        }
                        return (Success: true, Failure: (FrostValidatorFailure?)null);
                    }
                    catch (Exception ex)
                    {
                        LogUtility.Log($"[FROST MPC] Signing start EXCEPTION contacting {validator.ValidatorAddress} ({validator.IPAddress}): {ex.Message}",
                            "FrostMPCService.BroadcastSigningStart");
                        return (Success: false, Failure: new FrostValidatorFailure
                        {
                            ValidatorAddress = validator.ValidatorAddress,
                            HttpStatus = 0,
                            Message = ex.Message
                        });
                    }
                });

                var results = await Task.WhenAll(tasks);
                var successCount = results.Count(r => r.Success);
                var failures = results.Where(r => r.Failure != null).Select(r => r.Failure!).ToList();
                var requiredCount = GetRequiredValidatorCount(validators.Count, threshold);

                LogUtility.Log($"[FROST MPC] Signing start: {successCount}/{validators.Count} responded (required: {requiredCount})", "FrostMPCService.BroadcastSigningStart");
                return (successCount, failures, timestamp, signingLeaderSignature);
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Signing start error: {ex.Message}", "FrostMPCService.BroadcastSigningStart");
                return (0, new List<FrostValidatorFailure> { new FrostValidatorFailure { ValidatorAddress = "coordinator", HttpStatus = 0, Message = ApiErrorText.For(ex) } }, timestamp, signingLeaderSignature);
            }
        }

        /// <summary>
        /// Collect Round 1 nonce commitments from validators
        /// </summary>
        private static async Task<Dictionary<string, string>?> CollectSigningRound1Nonces(
            string sessionId,
            List<VBTCValidator> validators)
        {
            try
            {
                var nonces = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
                
                LogUtility.Log($"[FROST MPC] Collecting Round 1 nonces from {validators.Count} validators (parallel)...", "FrostMPCService.CollectSigningRound1Nonces");
                await Task.Delay(1500);

                // FIND-015 Fix: Use actual server response format
                // Server GET /frost/sign/round1/{sessionId} returns {Success, SessionId, Nonces: {addr:data}, ...}
                var nonceTasks = validators.Select(async validator =>
                {
                    try
                    {
                        var url = $"http://{validator.IPAddress}:{Globals.FrostValidatorPort}/frost/sign/round1/{sessionId}";
                        var response = await _httpClient.GetAsync(url);
                        
                        if (response.IsSuccessStatusCode)
                        {
                            var responseBody = await response.Content.ReadAsStringAsync();
                            var json = JObject.Parse(responseBody);
                            
                            if (json["Success"]?.Value<bool>() == true 
                                && json["SessionId"]?.Value<string>() == sessionId
                                && json["Nonces"] is JObject noncesObj)
                            {
                                foreach (var kvp in noncesObj)
                                {
                                    var addr = kvp.Key;
                                    var data = kvp.Value?.Value<string>();
                                    if (!string.IsNullOrEmpty(data))
                                    {
                                        nonces.TryAdd(addr, data);
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogUtility.Log($"[FROST MPC] Failed to collect nonce from {validator.ValidatorAddress}: {ex.Message}", "FrostMPCService.CollectSigningRound1Nonces");
                    }
                });

                await Task.WhenAll(nonceTasks);

                var nonceResult = nonces.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                LogUtility.Log($"[FROST MPC] Collected {nonceResult.Count}/{validators.Count} nonces", "FrostMPCService.CollectSigningRound1Nonces");
                return nonceResult.Count > 0 ? nonceResult : null;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Nonce collection error: {ex.Message}", "FrostMPCService.CollectSigningRound1Nonces");
                return null;
            }
        }

        /// <summary>
        /// Collect Round 2 signature shares from validators
        /// </summary>
        private static async Task<Dictionary<string, string>?> CollectSigningRound2Shares(
            string sessionId,
            List<VBTCValidator> validators,
            Dictionary<string, string> nonces,
            string leaderAddress,
            long startTimestamp,
            string startSignature)
        {
            try
            {
                var shares = new Dictionary<string, string>();
                
                LogUtility.Log($"[FROST MPC] Broadcasting nonces and collecting signature shares...", "FrostMPCService.CollectSigningRound2Shares");
                
                // Broadcast aggregated nonces to all validators, extract signature shares
                // directly from POST responses (the endpoint returns the share inline)
                var noncePayload = JsonConvert.SerializeObject(nonces);
                var postSuccessCount = 0;
                var postTasks = validators.Select(async validator =>
                {
                    try
                    {
                        var url = $"http://{validator.IPAddress}:{Globals.FrostValidatorPort}/frost/sign/round2/{sessionId}";
                        var content = new StringContent(noncePayload, Encoding.UTF8, "application/json");
                        using var round2Req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
                        AddLeaderAuthHeaders(round2Req, leaderAddress, startTimestamp, startSignature);
                        var response = await _httpClient.SendAsync(round2Req);
                        
                        if (response.IsSuccessStatusCode)
                        {
                            Interlocked.Increment(ref postSuccessCount);
                            
                            // Extract the signature share directly from the POST response
                            // instead of relying solely on a later GET poll.
                            // POST /frost/sign/round2/{sessionId} returns:
                            //   { Success: true, SignatureShare: "...", SessionId: "..." }
                            try
                            {
                                var responseBody = await response.Content.ReadAsStringAsync();
                                var json = JObject.Parse(responseBody);
                                if (json["Success"]?.Value<bool>() == true 
                                    && json["ShareGenerated"]?.Value<bool>() == true)
                                {
                                    var share = json["SignatureShare"]?.Value<string>();
                                    if (!string.IsNullOrEmpty(share))
                                    {
                                        lock (shares)
                                        {
                                            if (!shares.ContainsKey(validator.ValidatorAddress))
                                            {
                                                shares[validator.ValidatorAddress] = share;
                                                LogUtility.Log($"[FROST MPC] Extracted signature share from POST response for {validator.ValidatorAddress}",
                                                    "FrostMPCService.CollectSigningRound2Shares");
                                            }
                                        }
                                    }
                                }
                            }
                            catch (Exception parseEx)
                            {
                                LogUtility.Log($"[FROST MPC] Failed to parse POST response from {validator.ValidatorAddress}: {parseEx.Message}",
                                    "FrostMPCService.CollectSigningRound2Shares");
                            }
                            
                            return true;
                        }
                        else
                        {
                            var errorBody = await response.Content.ReadAsStringAsync();
                            LogUtility.Log($"[FROST MPC] Sign Round 2 POST REJECTED by {validator.ValidatorAddress} ({validator.IPAddress}): HTTP {(int)response.StatusCode} — {errorBody}",
                                "FrostMPCService.CollectSigningRound2Shares");
                            return false;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogUtility.Log($"[FROST MPC] Sign Round 2 POST EXCEPTION contacting {validator.ValidatorAddress} ({validator.IPAddress}): {ex.Message}",
                            "FrostMPCService.CollectSigningRound2Shares");
                        return false;
                    }
                });
                var postResults = await Task.WhenAll(postTasks);

                LogUtility.Log($"[FROST MPC] Sign Round 2 broadcast: {postSuccessCount}/{validators.Count} accepted, {shares.Count} shares extracted from POST responses",
                    "FrostMPCService.CollectSigningRound2Shares");

                // If we already have all shares from POST responses, skip polling entirely
                if (shares.Count >= validators.Count)
                {
                    LogUtility.Log($"[FROST MPC] All {shares.Count} signature shares collected from POST responses — skipping GET polling",
                        "FrostMPCService.CollectSigningRound2Shares");
                    return shares;
                }

                // Retry polling with backoff: poll up to 5 times with increasing delays
                // instead of a single fixed 2-second wait. This accommodates network latency
                // and FROST native crypto processing time on remote validators.
                var requiredCount = validators.Count; // ideally collect all shares
                var maxAttempts = 5;
                var pollDelaysMs = new[] { 2000, 2000, 3000, 3000, 5000 }; // total up to 15 seconds

                for (int attempt = 0; attempt < maxAttempts; attempt++)
                {
                    await Task.Delay(pollDelaysMs[attempt]);

                    // FIND-015 Fix: Collect signature shares using actual server response format
                    // Server GET /frost/sign/share/{sessionId} returns {Success, SessionId, Shares: {addr:data}, ...}
                    var pollTasks = validators.Select(async validator =>
                    {
                        try
                        {
                            var url = $"http://{validator.IPAddress}:{Globals.FrostValidatorPort}/frost/sign/share/{sessionId}";
                            using var shareReq = new HttpRequestMessage(HttpMethod.Get, url);
                            AddLeaderAuthHeaders(shareReq, leaderAddress, startTimestamp, startSignature);
                            var response = await _httpClient.SendAsync(shareReq);
                            
                            if (response.IsSuccessStatusCode)
                            {
                                var responseBody = await response.Content.ReadAsStringAsync();
                                var json = JObject.Parse(responseBody);
                                
                                if (json["Success"]?.Value<bool>() == true 
                                    && json["SessionId"]?.Value<string>() == sessionId
                                    && json["Shares"] is JObject sharesObj)
                                {
                                    lock (shares)
                                    {
                                        foreach (var kvp in sharesObj)
                                        {
                                            var addr = kvp.Key;
                                            var data = kvp.Value?.Value<string>();
                                            if (!string.IsNullOrEmpty(data) && !shares.ContainsKey(addr))
                                            {
                                                shares[addr] = data;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            LogUtility.Log($"[FROST MPC] Failed to collect share from {validator.ValidatorAddress} (attempt {attempt + 1}): {ex.Message}", "FrostMPCService.CollectSigningRound2Shares");
                        }
                    });

                    await Task.WhenAll(pollTasks);

                    LogUtility.Log($"[FROST MPC] Collected {shares.Count}/{validators.Count} signature shares (attempt {attempt + 1}/{maxAttempts})", "FrostMPCService.CollectSigningRound2Shares");

                    // If we have all shares, no need to keep polling
                    if (shares.Count >= requiredCount)
                        break;
                }

                LogUtility.Log($"[FROST MPC] Final signature share collection: {shares.Count}/{validators.Count}", "FrostMPCService.CollectSigningRound2Shares");
                return shares.Count > 0 ? shares : null;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Share collection error: {ex.Message}", "FrostMPCService.CollectSigningRound2Shares");
                return null;
            }
        }

        /// <summary>
        /// FIND-026 Fix: Aggregate signature shares into final Schnorr signature using FROST native library.
        /// The coordinator collects nonce commitments and signature shares from validators (which were
        /// generated via FrostNative on each validator), then calls FrostNative.SignAggregate to produce
        /// the final 64-byte Schnorr signature. The signature is verified internally by the FROST library
        /// against the group public key before returning. If aggregation or verification fails, returns null
        /// (fail closed - no invalid signatures will be injected into Bitcoin transactions).
        /// 
        /// FIND-026 Fix: Now accepts scUID and signerAddresses to:
        /// 1. Look up the correct pubkey package for this specific contract (not just the first found)
        /// 2. Remap signature shares and nonce commitments from VFX address keys to FROST Identifier keys
        ///    (64-char hex scalars), matching the format the FROST native library expects.
        /// </summary>
        private static async Task<(FrostSigningResult? Result, FrostCeremonyFailureCode Code, string Detail)> AggregateSignature(
            string sessionId,
            string messageHash,
            string scUID,
            List<string> signerAddresses,
            List<VBTCValidator> validators,
            int threshold,
            Dictionary<string, string> shares,
            string? ceremonyId = null)
        {
            try
            {
                LogUtility.Log($"[FROST MPC] Aggregating {shares.Count} signature shares via FROST native library for contract {scUID}...", "FrostMPCService.AggregateSignature");

                // Collect nonce commitments from validators in parallel (needed for aggregation)
                var nonces = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
                var nonceCollectTasks = validators.Select(async validator =>
                {
                    try
                    {
                        var url = $"http://{validator.IPAddress}:{Globals.FrostValidatorPort}/frost/sign/round1/{sessionId}";
                        var response = await _httpClient.GetAsync(url);
                        if (response.IsSuccessStatusCode)
                        {
                            var responseBody = await response.Content.ReadAsStringAsync();
                            var json = JObject.Parse(responseBody);
                            if (json["Success"]?.Value<bool>() == true && json["Nonces"] is JObject noncesObj)
                            {
                                foreach (var kvp in noncesObj)
                                {
                                    nonces.TryAdd(kvp.Key, kvp.Value?.Value<string>() ?? "");
                                }
                            }
                        }
                    }
                    catch { /* Already logged during collection phase */ }
                });

                await Task.WhenAll(nonceCollectTasks);

                // 3-tier key lookup for pubkey package:
                // 1. Try SCUID first (works after SignStart auto-updated the record)
                // 2. Try ceremonyId (original DKG session ID)
                // 3. State Trei fallback → decompile contract → get GroupPublicKey → lookup by GPK
                LogUtility.Log($"[FROST MPC] Key lookup for aggregation. scUID: {scUID}, ceremonyId: {ceremonyId ?? "null"}", "FrostMPCService.AggregateSignature");

                string? pubkeyPackage = null;
                var myAddr = Globals.ValidatorAddress;
                FrostValidatorKeyStore? resolvedKeyStore = null;

                // Tier 1: Try SCUID (SignStart may have already updated the record to use the real SCUID)
                if (!string.IsNullOrEmpty(myAddr))
                {
                    resolvedKeyStore = FrostValidatorKeyStore.GetKeyPackage(scUID, myAddr);
                    if (resolvedKeyStore != null && !string.IsNullOrEmpty(resolvedKeyStore.PubkeyPackage))
                    {
                        pubkeyPackage = resolvedKeyStore.PubkeyPackage;
                        LogUtility.Log($"[FROST MPC] Found pubkey package via SCUID lookup: {scUID}", "FrostMPCService.AggregateSignature");
                    }
                }

                // Tier 2: Try ceremonyId (original DKG session GUID)
                if (string.IsNullOrEmpty(pubkeyPackage) && !string.IsNullOrEmpty(ceremonyId) && !string.IsNullOrEmpty(myAddr))
                {
                    resolvedKeyStore = FrostValidatorKeyStore.GetKeyPackage(ceremonyId, myAddr);
                    if (resolvedKeyStore != null && !string.IsNullOrEmpty(resolvedKeyStore.PubkeyPackage))
                    {
                        pubkeyPackage = resolvedKeyStore.PubkeyPackage;
                        LogUtility.Log($"[FROST MPC] Found pubkey package via ceremonyId fallback: {ceremonyId}", "FrostMPCService.AggregateSignature");
                    }
                }

                // Tier 3: State Trei fallback — decompile contract to get FrostGroupPublicKey, then lookup by GPK
                if (string.IsNullOrEmpty(pubkeyPackage))
                {
                    string? resolvedGroupPubKey = null;

                    // Try local DB first
                    var localContract = VBTCContractV2.GetContract(scUID);
                    if (localContract != null && !string.IsNullOrEmpty(localContract.FrostGroupPublicKey))
                    {
                        resolvedGroupPubKey = localContract.FrostGroupPublicKey;
                    }

                    // Fall back to State Trei (works on all nodes including non-owners)
                    if (string.IsNullOrEmpty(resolvedGroupPubKey))
                    {
                        var scStateTreiRec = SmartContractStateTrei.GetSmartContractState(scUID);
                        if (scStateTreiRec != null && !string.IsNullOrEmpty(scStateTreiRec.ContractData))
                        {
                            var scMainDecompile = SmartContractMain.GenerateSmartContractInMemory(scStateTreiRec.ContractData);
                            if (scMainDecompile?.Features != null)
                            {
                                var tknzFeature = scMainDecompile.Features
                                    .Where(x => x.FeatureName == FeatureName.TokenizationV2)
                                    .Select(x => x.FeatureFeatures)
                                    .FirstOrDefault();
                                if (tknzFeature is TokenizationV2Feature tknz)
                                {
                                    resolvedGroupPubKey = tknz.FrostGroupPublicKey;
                                    LogUtility.Log($"[FROST MPC] Resolved GroupPublicKey from State Trei: {resolvedGroupPubKey}", "FrostMPCService.AggregateSignature");
                                }
                            }
                        }
                    }

                    if (!string.IsNullOrEmpty(resolvedGroupPubKey) && !string.IsNullOrEmpty(myAddr))
                    {
                        resolvedKeyStore = FrostValidatorKeyStore.GetKeyPackageByGroupPublicKey(resolvedGroupPubKey, myAddr);
                        if (resolvedKeyStore != null && !string.IsNullOrEmpty(resolvedKeyStore.PubkeyPackage))
                        {
                            pubkeyPackage = resolvedKeyStore.PubkeyPackage;
                            LogUtility.Log($"[FROST MPC] Found pubkey package via GroupPublicKey fallback: {resolvedGroupPubKey}", "FrostMPCService.AggregateSignature");
                        }
                    }
                }

                // Tier 4: Non-validator fallback — use pubkey package from local VBTCContractV2.
                // Non-validator wallet nodes don't have FrostValidatorKeyStore records (those are validator-only),
                // but the contract owner's local DB stores the FrostPubkeyPackage from DKG.
                if (string.IsNullOrEmpty(pubkeyPackage))
                {
                    var localContract = VBTCContractV2.GetContract(scUID);
                    if (localContract != null && !string.IsNullOrEmpty(localContract.FrostPubkeyPackage))
                    {
                        pubkeyPackage = localContract.FrostPubkeyPackage;
                        LogUtility.Log($"[FROST MPC] Found pubkey package via local VBTCContractV2 fallback (non-validator node)", "FrostMPCService.AggregateSignature");
                    }
                }

                // Tier 5: Fetch pubkey package from a validator via the /frost/key/pubkey endpoint.
                // This is the last resort for non-validator nodes where the local contract doesn't have it.
                if (string.IsNullOrEmpty(pubkeyPackage))
                {
                    LogUtility.Log($"[FROST MPC] Tier 5: Fetching pubkey package from validators...", "FrostMPCService.AggregateSignature");
                    foreach (var validator in validators)
                    {
                        try
                        {
                            // Try scUID first, then ceremonyId
                            foreach (var lookupId in new[] { scUID, ceremonyId }.Where(id => !string.IsNullOrEmpty(id)))
                            {
                                var url = $"http://{validator.IPAddress}:{Globals.FrostValidatorPort}/frost/key/pubkey/{lookupId}";
                                var response = await _httpClient.GetAsync(url);
                                if (response.IsSuccessStatusCode)
                                {
                                    var responseBody = await response.Content.ReadAsStringAsync();
                                    var json = JObject.Parse(responseBody);
                                    var pp = json["PubkeyPackage"]?.Value<string>();
                                    if (!string.IsNullOrEmpty(pp))
                                    {
                                        pubkeyPackage = pp;
                                        LogUtility.Log($"[FROST MPC] Found pubkey package from validator {validator.ValidatorAddress} (lookup: {lookupId})", "FrostMPCService.AggregateSignature");

                                        // Cache it locally so we don't need to fetch again
                                        try
                                        {
                                            var localContract = VBTCContractV2.GetContract(scUID);
                                            if (localContract != null)
                                            {
                                                localContract.FrostPubkeyPackage = pp;
                                                VBTCContractV2.UpdateContract(localContract);
                                                LogUtility.Log($"[FROST MPC] Cached pubkey package to local VBTCContractV2", "FrostMPCService.AggregateSignature");
                                            }
                                        }
                                        catch (Exception cacheEx)
                                        {
                                            LogUtility.Log($"[FROST MPC] Warning: Failed to cache pubkey package locally: {cacheEx.Message}", "FrostMPCService.AggregateSignature");
                                        }

                                        break;
                                    }
                                }
                            }
                            if (!string.IsNullOrEmpty(pubkeyPackage)) break;
                        }
                        catch (Exception fetchEx)
                        {
                            LogUtility.Log($"[FROST MPC] Failed to fetch pubkey package from {validator.ValidatorAddress}: {fetchEx.Message}", "FrostMPCService.AggregateSignature");
                        }
                    }
                }

                if (string.IsNullOrEmpty(pubkeyPackage))
                {
                    // A non-validator coordinator (e.g. a web-wallet user's node that never ran DKG)
                    // skips the keystore tiers entirely and depends on the HTTP fetch from validators —
                    // say so, because that is exactly the owner-vs-non-owner divergence seen in production.
                    var coordinatorContext = string.IsNullOrEmpty(myAddr)
                        ? "coordinator is a non-validator (keystore tiers skipped; depended on validator HTTP fetch)"
                        : "coordinator is a validator (all lookup tiers exhausted)";
                    var detail = $"pubkey package not found (ceremonyId: {ceremonyId ?? "null"}, scUID: {scUID}); {coordinatorContext}";
                    ErrorLogUtility.LogError($"FROST Signing: {detail}", "FrostMPCService.AggregateSignature");
                    return (null, FrostCeremonyFailureCode.PubkeyPackageNotFound, detail);
                }

                // FIND-026 Fix: Remap signature shares and nonce commitments from VFX address keys
                // to FROST Identifier keys (64-char hex scalars). The FROST native library expects
                // BTreeMap<Identifier, SignatureShare> and BTreeMap<Identifier, NonceCommitment>.
                // Use stored participant order from DKG if available, otherwise fall back to sorted order.
                List<string>? storedOrderForAggregate = null;
                if (resolvedKeyStore != null && !string.IsNullOrEmpty(resolvedKeyStore.ParticipantOrderJson))
                {
                    try
                    {
                        storedOrderForAggregate = JsonConvert.DeserializeObject<List<string>>(resolvedKeyStore.ParticipantOrderJson);
                        LogUtility.Log($"[FROST MPC] Using stored participant order for aggregation ({storedOrderForAggregate?.Count ?? 0} entries)", "FrostMPCService.AggregateSignature");
                    }
                    catch (Exception orderEx)
                    {
                        LogUtility.Log($"[FROST MPC] WARNING: Failed to parse stored participant order: {orderEx.Message}. Falling back to sorted order.", "FrostMPCService.AggregateSignature");
                    }
                }
                // A coordinator outside the DKG (Spyglass's node for every web wallet withdrawal) has no stored
                // order. The identifiers were assigned over the DKG participants, so numbering the reachable
                // signers instead shifts every identifier after a missing participant and aggregation fails
                // (native error -4). The contract's validator snapshot is that participant list.
                List<string>? contractParticipants = null;
                if (storedOrderForAggregate == null || storedOrderForAggregate.Count == 0)
                    contractParticipants = GetContractDkgParticipants(scUID);
                var addressListForAggregate = ResolveAggregationOrder(storedOrderForAggregate, contractParticipants, pubkeyPackage, signerAddresses, out var orderSource);
                LogUtility.Log($"[FROST MPC] Aggregation identifier order: {orderSource} ({addressListForAggregate.Count} entries, {signerAddresses.Count} signers)", "FrostMPCService.AggregateSignature");
                var addrToFrostId = BuildAddressToFrostIdentifierMap(addressListForAggregate);

                // Remap shares: VFX address → FROST Identifier
                var remappedShares = new JObject();
                foreach (var kvp in shares)
                {
                    try
                    {
                        var shareToken = JToken.Parse(kvp.Value);
                        if (addrToFrostId.TryGetValue(kvp.Key, out var frostId))
                        {
                            remappedShares[frostId] = shareToken;
                        }
                        else
                        {
                            LogUtility.Log($"[FROST MPC] WARNING: Signer address '{kvp.Key}' not found in signer list for share remapping", "FrostMPCService.AggregateSignature");
                        }
                    }
                    catch (Exception parseEx)
                    {
                        LogUtility.Log($"[FROST MPC] Failed to parse share for '{kvp.Key}': {parseEx.Message}", "FrostMPCService.AggregateSignature");
                    }
                }

                // Remap nonces: VFX address → FROST Identifier
                var remappedNonces = new JObject();
                foreach (var kvp in nonces)
                {
                    try
                    {
                        var nonceToken = JToken.Parse(kvp.Value);
                        if (addrToFrostId.TryGetValue(kvp.Key, out var frostId))
                        {
                            remappedNonces[frostId] = nonceToken;
                        }
                        else
                        {
                            LogUtility.Log($"[FROST MPC] WARNING: Signer address '{kvp.Key}' not found in signer list for nonce remapping", "FrostMPCService.AggregateSignature");
                        }
                    }
                    catch (Exception parseEx)
                    {
                        LogUtility.Log($"[FROST MPC] Failed to parse nonce for '{kvp.Key}': {parseEx.Message}", "FrostMPCService.AggregateSignature");
                    }
                }

                var sharesJson = remappedShares.ToString(Formatting.None);
                var noncesJson = remappedNonces.ToString(Formatting.None);

                LogUtility.Log($"[FROST MPC] Remapped {remappedShares.Count} shares and {remappedNonces.Count} nonces from VFX addresses to FROST Identifiers", 
                    "FrostMPCService.AggregateSignature");

                // Call FROST native library to aggregate signature shares
                var (schnorrSignature, errorCode) = FrostNative.SignAggregate(
                    sharesJson, noncesJson, messageHash, pubkeyPackage);

                if (errorCode != FrostNative.SUCCESS || string.IsNullOrEmpty(schnorrSignature))
                {
                    ErrorLogUtility.LogError($"FROST signature aggregation failed. Error code: {errorCode}. " +
                        "This is a fail-closed result - no invalid signature will be used.", "FrostMPCService.AggregateSignature");
                    return (null, FrostCeremonyFailureCode.NativeAggregationFailed, $"native SignAggregate returned error code {errorCode}");
                }

                // Validate signature is 64 bytes (128 hex chars) - standard Schnorr signature size
                if (schnorrSignature.Length != 128)
                {
                    ErrorLogUtility.LogError($"FROST: Aggregated signature unexpected length: {schnorrSignature.Length} hex chars (expected 128)",
                        "FrostMPCService.AggregateSignature");
                    return (null, FrostCeremonyFailureCode.InvalidSignatureLength, $"aggregated signature was {schnorrSignature.Length} hex chars (expected 128)");
                }

                LogUtility.Log($"[FROST MPC] Signature aggregation complete. Schnorr sig: {schnorrSignature.Substring(0, 16)}...", 
                    "FrostMPCService.AggregateSignature");

                return (new FrostSigningResult
                {
                    SessionId = sessionId,
                    MessageHash = messageHash,
                    SchnorrSignature = schnorrSignature,
                    SignatureValid = true, // FROST native library verifies internally before returning
                    CompletionTimestamp = TimeUtil.GetTime(),
                    SignerAddresses = signerAddresses,
                    Threshold = threshold
                }, FrostCeremonyFailureCode.None, string.Empty);
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Signature aggregation error: {ex}", "FrostMPCService.AggregateSignature");
                return (null, FrostCeremonyFailureCode.CoordinatorException, $"aggregation exception: {ApiErrorText.For(ex)}");
            }
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Calculate required number of validators based on threshold
        /// </summary>
        private static int GetRequiredValidatorCount(int totalValidators, int thresholdPercentage)
        {
            return (int)Math.Ceiling(totalValidators * (thresholdPercentage / 100.0));
        }


        /// <summary>
        /// FIND-026 Fix: Convert a 1-based participant index to a FROST Identifier hex string.
        /// FROST Identifier is a secp256k1 Scalar serialized as 32 bytes big-endian (64 hex chars).
        /// Participant 1 → "0000000000000000000000000000000000000000000000000000000000000001"
        /// Participant 2 → "0000000000000000000000000000000000000000000000000000000000000002"
        /// This must match the identical logic in FrostStartup.BuildAddressToIdentifierMap.
        /// </summary>
        /// <summary>
        /// The address list whose sorted order gives each signer its DKG identifier. Prefers the coordinator's stored
        /// DKG order, then the contract's DKG participants, then the reachable signers (only correct when every DKG
        /// participant is among them). Contract participants are used only when their count matches the pubkey
        /// package's verifying shares, so a snapshot that differs from the DKG set never replaces the old behaviour.
        /// </summary>
        internal static List<string> ResolveAggregationOrder(
            List<string>? storedOrder,
            List<string>? contractParticipants,
            string pubkeyPackage,
            List<string> signerAddresses,
            out string source)
        {
            if (storedOrder != null && storedOrder.Count > 0)
            {
                source = "stored DKG order";
                return storedOrder;
            }

            if (contractParticipants != null && contractParticipants.Count > 0)
            {
                var shareCount = CountVerifyingShares(pubkeyPackage);
                if (shareCount == contractParticipants.Count)
                {
                    source = "contract DKG participants";
                    return contractParticipants;
                }
                source = $"signer addresses (contract lists {contractParticipants.Count} participants, pubkey package has {shareCount?.ToString() ?? "unreadable"} verifying shares)";
                return signerAddresses;
            }

            source = "signer addresses (no DKG participant list found)";
            return signerAddresses;
        }

        internal static int? CountVerifyingShares(string pubkeyPackage)
        {
            try
            {
                return (JObject.Parse(pubkeyPackage)["verifying_shares"] as JObject)?.Count;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>The contract's DKG participants (ValidatorAddressesSnapshot), from the local DB or State Trei.</summary>
        internal static List<string>? GetContractDkgParticipants(string scUID)
        {
            try
            {
                var localContract = VBTCContractV2.GetContract(scUID);
                if (localContract?.ValidatorAddressesSnapshot != null && localContract.ValidatorAddressesSnapshot.Count > 0)
                    return localContract.ValidatorAddressesSnapshot;

                var scStateTreiRec = SmartContractStateTrei.GetSmartContractState(scUID);
                if (scStateTreiRec == null || string.IsNullOrEmpty(scStateTreiRec.ContractData))
                    return null;

                var scMain = SmartContractMain.GenerateSmartContractInMemory(scStateTreiRec.ContractData);
                var tknz = scMain?.Features?
                    .Where(x => x.FeatureName == FeatureName.TokenizationV2)
                    .Select(x => x.FeatureFeatures)
                    .OfType<TokenizationV2Feature>()
                    .FirstOrDefault();
                return tknz?.ValidatorAddressesSnapshot;
            }
            catch (Exception ex)
            {
                LogUtility.Log($"[FROST MPC] Could not read DKG participants for {scUID}: {ex.Message}", "FrostMPCService.GetContractDkgParticipants");
                return null;
            }
        }

        private static string ParticipantIndexToFrostIdentifier(int participantIndex)
        {
            return participantIndex.ToString("x").PadLeft(64, '0');
        }

        /// <summary>
        /// FIND-026 Fix: Build a lookup from VFX address to FROST Identifier hex string
        /// using participant list ordering. The ordering must be identical to how the signing
        /// session was created (i.e., the SignerAddresses list order from BroadcastSigningStart).
        /// </summary>
        /// <summary>
        /// Build a deterministic mapping from signer addresses to FROST Identifiers.
        /// CRITICAL: Addresses are sorted alphabetically (Ordinal) before assigning identifiers
        /// so that the same set of addresses ALWAYS produces the same mapping, regardless of
        /// input order. This must match FrostStartup.BuildAddressToIdentifierMap exactly.
        /// </summary>
        internal static Dictionary<string, string> BuildAddressToFrostIdentifierMap(List<string> signerAddresses)
        {
            var sorted = signerAddresses.OrderBy(a => a, StringComparer.Ordinal).ToList();
            var map = new Dictionary<string, string>();
            for (int i = 0; i < sorted.Count; i++)
            {
                map[sorted[i]] = ParticipantIndexToFrostIdentifier(i + 1); // 1-based
            }
            return map;
        }

        #endregion
    }
}
