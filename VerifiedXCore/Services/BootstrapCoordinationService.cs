using Newtonsoft.Json;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;
using System.Text;

namespace VerifiedXCore.Services
{
    /// <summary>
    /// Phase E: cooperative seed bootstrap. A seed may only enter bootstrap mode (and thereby
    /// jumpstart a stopped chain) after reaching a signed agreement with at least one other seed
    /// that the chain is stalled at the SAME tip hash — never unilaterally. This replaces the old
    /// behavior where any seed whose local tip looked >120s stale would independently begin
    /// bootstrap casting, which is how the mainnet fork incident's disjoint groups formed.
    ///
    /// State machine: Normal → Polling → Agreed → (healthy production) → Normal (with cooldown).
    /// <see cref="AgreementActive"/> is ANDed into <see cref="Globals.IsBootstrapMode"/>, so all
    /// 13 bootstrap read-sites inherit the gate with no per-site changes. Non-seed nodes are
    /// unaffected (IsLocalBootstrapCaster is false → state machine never leaves Normal).
    /// </summary>
    public static class BootstrapCoordinationService
    {
        public enum BootstrapState { Normal, Polling, Agreed }

        private const int TickIntervalMs = 15000;
        private const int ProductionSampleDelayMs = 30000;
        private const int SoloWarningIntervalSeconds = 60;
        private const int HealthyChecksToExit = 3;      // ~3 ticks of fresh tip → exit Agreed
        private const int HealthyTipAgeSeconds = 90;    // tip younger than this counts as healthy
        private const int ReentryCooldownSeconds = 300; // 5 min cooldown after exiting Agreed
        private const int AgreementTimestampWindowSeconds = 300; // ±5 min on signed notices

        private static readonly object StateLock = new object();
        private static BootstrapState _state = BootstrapState.Normal;
        private static long _cooldownUntil = 0;
        private static int _healthyChecks = 0;
        private static long _lastSoloWarning = 0;
        private static bool _running = false;

        public static BootstrapState State => _state;
        public static bool AgreementActive => _state == BootstrapState.Agreed;

        /// <summary>Number of seeds (incl. self) in the active agreement. Used by Phase C's bootstrap cert quorum.</summary>
        public static int AgreedSeedCount { get; private set; } = 0;

        /// <summary>Addresses of the seeds (incl. self) in the active agreement.</summary>
        public static List<string> AgreedSeedAddresses { get; private set; } = new();

        /// <summary>Wave 6: the tip height the seeds agreed on. Genesis boundary = this + 1.</summary>
        public static long AgreedHeight { get; private set; } = -1;

        /// <summary>Wave 6: IPs of the other agreeing seeds (for genesis signature collection).</summary>
        private static List<(string Address, string Ip)> _agreedSeedPeers = new();

        public static void Start()
        {
            if (_running) return;
            _running = true;
            _ = Task.Run(RunLoopAsync);
        }

        private static async Task RunLoopAsync()
        {
            while (true)
            {
                try
                {
                    await TickAsync();
                }
                catch (Exception ex)
                {
                    CasterLogUtility.Log($"BootstrapCoordination tick error: {ex.Message}", "BOOTSTRAP");
                }
                await Task.Delay(TickIntervalMs);
            }
        }

        private static async Task TickAsync()
        {
            if (!Globals.IsLocalBootstrapCaster)
            {
                ResetToNormal();
                return;
            }

            switch (_state)
            {
                case BootstrapState.Normal:
                    if (TimeUtil.GetTime() < _cooldownUntil)
                        return;
                    if (Globals.IsChainStalledForBootstrap)
                    {
                        CasterLogUtility.Log(
                            $"BOOTSTRAP: local stall detected (tip height {Globals.LastBlock?.Height}). Entering Polling — will NOT bootstrap without seed agreement.",
                            "BOOTSTRAP");
                        ConsoleWriterService.OutputValCaster("[BOOTSTRAP] Local stall detected — polling peer seeds for agreement.");
                        lock (StateLock) { _state = BootstrapState.Polling; }
                    }
                    break;

                case BootstrapState.Polling:
                    if (!Globals.IsChainStalledForBootstrap)
                    {
                        CasterLogUtility.Log("BOOTSTRAP: production resumed while polling — back to Normal.", "BOOTSTRAP");
                        ResetToNormal();
                        return;
                    }

                    if (Globals.ForceSoloBootstrap)
                    {
                        ConsoleWriterService.OutputValCaster("[BOOTSTRAP] *** ForceSoloBootstrap=true — ENTERING SOLO BOOTSTRAP. ***");
                        ConsoleWriterService.OutputValCaster("[BOOTSTRAP] *** This bypasses seed agreement and risks a fork if other seeds are alive. ***");
                        CasterLogUtility.Log("BOOTSTRAP: FORCED SOLO BOOTSTRAP via config override. NOTE: a solo seed cannot mint a genesis membership record (needs 2 seed signatures) — the record era stays unarmed.", "BOOTSTRAP");
                        EnterAgreed(new List<string> { Globals.ValidatorAddress ?? "" }, Globals.LastBlock?.Height ?? -1, new List<(string, string)>());
                        return;
                    }

                    var agreed = await TryReachAgreementAsync();
                    if (!agreed)
                    {
                        var now = TimeUtil.GetTime();
                        if (now - _lastSoloWarning >= SoloWarningIntervalSeconds)
                        {
                            _lastSoloWarning = now;
                            ConsoleWriterService.OutputValCaster("[BOOTSTRAP] BLOCKED — refusing solo bootstrap (consistency over availability). Need >=2 seeds agreeing on the same tip hash. Set ForceSoloBootstrap=true in config ONLY for disaster recovery.");
                            CasterLogUtility.Log("BOOTSTRAP BLOCKED — refusing solo bootstrap; no 2-of-3 seed agreement yet.", "BOOTSTRAP");
                        }
                    }
                    break;

                case BootstrapState.Agreed:
                    // Wave 6: the restart event itself arms the record era — while Agreed with an
                    // empty membership store, keep trying to mint + co-sign the genesis record
                    // (idempotent: deterministic record, same-hash re-sign allowed).
                    if (AgreedSeedCount >= 2 && CasterMembershipStore.GetCurrent() == null && AgreedHeight >= 0)
                        await TryCreateGenesisAsync();

                    var tipAge = TimeUtil.GetTime() - (Globals.LastBlock?.Timestamp ?? 0);
                    var tipAdvancing = tipAge < HealthyTipAgeSeconds;
                    _healthyChecks = tipAdvancing ? _healthyChecks + 1 : 0;

                    if (_healthyChecks >= HealthyChecksToExit)
                    {
                        CasterLogUtility.Log(
                            $"BOOTSTRAP EXIT: production healthy for {_healthyChecks} consecutive checks. Returning to Normal with {ReentryCooldownSeconds}s cooldown.",
                            "BOOTSTRAP");
                        ConsoleWriterService.OutputValCaster("[BOOTSTRAP] Production healthy — exiting bootstrap mode.");
                        _cooldownUntil = TimeUtil.GetTime() + ReentryCooldownSeconds;
                        ResetToNormal();
                    }
                    break;
            }
        }

        private static void ResetToNormal()
        {
            lock (StateLock)
            {
                _state = BootstrapState.Normal;
                _healthyChecks = 0;
                AgreedSeedCount = 0;
                AgreedSeedAddresses = new List<string>();
                AgreedHeight = -1;
                _agreedSeedPeers = new List<(string, string)>();
            }
        }

        private static void EnterAgreed(List<string> seedAddresses, long agreedHeight, List<(string Address, string Ip)> agreedSeedPeers)
        {
            lock (StateLock)
            {
                _state = BootstrapState.Agreed;
                _healthyChecks = 0;
                AgreedSeedAddresses = seedAddresses.Where(a => !string.IsNullOrEmpty(a)).Distinct().ToList();
                AgreedSeedCount = AgreedSeedAddresses.Count;
                AgreedHeight = agreedHeight;
                _agreedSeedPeers = agreedSeedPeers;
            }
        }

        /// <summary>
        /// Wave 6: mints the genesis membership record ahead of the agreement boundary
        /// (EffectiveFromHeight = AgreedHeight + GenesisBoundaryMargin — production resumes during
        /// the minting retry loop, so the boundary must stay in front of it; see
        /// CasterMembershipStore.GenesisBoundaryMargin). Every agreeing seed constructs the
        /// IDENTICAL record (deterministic given the agreed height), so signatures from concurrent
        /// attempts merge and the equivocation guard never trips on honest seeds. Requires
        /// ≥2 hardcoded-seed signatures — collected via the SignMembershipRecord endpoint.
        /// Installing the record arms cert enforcement network-wide as it propagates.
        /// </summary>
        private static async Task TryCreateGenesisAsync()
        {
            try
            {
                var account = AccountData.GetLocalValidator();
                if (account?.GetPrivKey == null || string.IsNullOrEmpty(Globals.ValidatorAddress))
                    return;

                var genesis = CasterMembershipStore.BuildGenesisRecord(AgreedHeight + CasterMembershipStore.GenesisBoundaryMargin);

                if (!CasterMembershipStore.TryMarkSigned(0, genesis.RecordHash))
                {
                    CasterLogUtility.Log("MEMBERSHIP: refusing genesis — already signed a DIFFERENT record at seq 0.", "MEMBERSHIP");
                    return;
                }

                var payload = CasterMembershipStore.CanonicalPayload(genesis);
                var selfSig = SignatureService.CreateSignature(payload, account.GetPrivKey, account.PublicKey);
                if (selfSig == "ERROR")
                    return;
                genesis.Signatures.Add(new RecordSignature { SignerAddress = Globals.ValidatorAddress, Signature = selfSig });

                var signRequest = JsonConvert.SerializeObject(new MembershipSignRequest { Candidate = genesis, ProposerAddress = Globals.ValidatorAddress });
                foreach (var seed in _agreedSeedPeers)
                {
                    if (genesis.Signatures.Count >= CasterMembershipStore.GenesisMinSeedSignatures) break;
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
                        if (!genesis.Signatures.Any(s => s.SignerAddress == sig.SignerAddress))
                            genesis.Signatures.Add(sig);
                    }
                    catch { /* seed unreachable — retried next tick */ }
                }

                if (genesis.Signatures.Count < CasterMembershipStore.GenesisMinSeedSignatures)
                {
                    CasterLogUtility.Log($"MEMBERSHIP: genesis has {genesis.Signatures.Count}/{CasterMembershipStore.GenesisMinSeedSignatures} seed signatures — retrying next tick.", "MEMBERSHIP");
                    return;
                }

                if (CasterMembershipStore.TryAppend(genesis, out var reason))
                {
                    ConsoleWriterService.OutputValCaster($"[BOOTSTRAP] GENESIS MEMBERSHIP RECORD installed — record era + certificates active from height {genesis.EffectiveFromHeight}.");
                    await CasterMembershipService.BroadcastRecordAsync(genesis);
                }
                else
                {
                    CasterLogUtility.Log($"MEMBERSHIP: genesis append failed — {reason}.", "MEMBERSHIP");
                }
            }
            catch (Exception ex)
            {
                CasterLogUtility.Log($"MEMBERSHIP: genesis creation error — {ex.Message}", "MEMBERSHIP");
            }
        }

        /// <summary>
        /// Attempts the full cooperative agreement:
        /// 1. Poll peer seeds' BootstrapStatus — need another seed that also sees a stall.
        /// 2. Compare tip hash at a common height — must match ours.
        /// 3. Confirm no production anywhere (sample regular peers' height twice, 30s apart).
        /// 4. Exchange signed BootstrapAgreementNotices — need >=2 distinct seed signatures
        ///    (incl. our own) over the same {height, tipHash}.
        /// </summary>
        private static async Task<bool> TryReachAgreementAsync()
        {
            var account = AccountData.GetLocalValidator();
            if (account == null || account.GetPrivKey == null || string.IsNullOrEmpty(Globals.ValidatorAddress))
                return false;

            var seedPeers = SeedNodeService.GetBootstrapSeedPeers()
                .Where(p => !string.IsNullOrEmpty(p.PeerIP) && p.ValidatorAddress != Globals.ValidatorAddress)
                .ToList();

            // 1. Poll peer seeds' status
            var candidates = new List<(string Address, string Ip, long Height, string TipHash)>();
            foreach (var seed in seedPeers)
            {
                try
                {
                    using var client = Globals.HttpClientFactory.CreateClient();
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                    var uri = $"http://{seed.PeerIP.Replace("::ffff:", "")}:{Globals.ValAPIPort}/valapi/validator/BootstrapStatus";
                    var resp = await client.GetAsync(uri, cts.Token);
                    if (!resp.IsSuccessStatusCode)
                        continue;
                    var body = await resp.Content.ReadAsStringAsync();
                    var status = JsonConvert.DeserializeAnonymousType(body, new { Height = 0L, TipHash = "", IsBootstrapCandidate = false, ValidatorAddress = "" });
                    if (status == null || !status.IsBootstrapCandidate)
                        continue;
                    if (status.ValidatorAddress != seed.ValidatorAddress)
                        continue;
                    candidates.Add((status.ValidatorAddress, seed.PeerIP.Replace("::ffff:", ""), status.Height, status.TipHash));
                }
                catch { /* unreachable seed */ }
            }

            if (candidates.Count == 0)
                return false;

            // 2. Tip-hash comparison at a common height (min of all tips incl. ours)
            var myHeight = Globals.LastBlock?.Height ?? -1;
            var commonHeight = Math.Min(myHeight, candidates.Min(c => c.Height));
            if (commonHeight < 0)
                return false;

            var myBlockAtCommon = commonHeight == myHeight ? Globals.LastBlock : BlockchainData.GetBlockByHeight(commonHeight);
            var myHashAtCommon = myBlockAtCommon?.Hash;
            if (string.IsNullOrEmpty(myHashAtCommon))
                return false;

            var agreeingSeeds = new List<(string Address, string Ip)>();
            foreach (var c in candidates)
            {
                try
                {
                    using var client = Globals.HttpClientFactory.CreateClient();
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                    var uri = $"http://{c.Ip}:{Globals.ValAPIPort}/valapi/validator/GetBlockHash/{commonHeight}";
                    var resp = await client.GetAsync(uri, cts.Token);
                    if (!resp.IsSuccessStatusCode)
                        continue;
                    var body = await resp.Content.ReadAsStringAsync();
                    var hashInfo = JsonConvert.DeserializeAnonymousType(body, new { Hash = "", Validator = "", Height = 0L });
                    if (hashInfo != null && hashInfo.Hash == myHashAtCommon)
                        agreeingSeeds.Add((c.Address, c.Ip));
                    else
                        CasterLogUtility.Log(
                            $"BOOTSTRAP: seed {c.Address} hash MISMATCH at h={commonHeight} (theirs={hashInfo?.Hash?[..Math.Min(12, hashInfo?.Hash?.Length ?? 0)]} ours={myHashAtCommon[..Math.Min(12, myHashAtCommon.Length)]}). Seeds shut down forked — operator must reconcile per runbook.",
                            "BOOTSTRAP");
                }
                catch { }
            }

            if (agreeingSeeds.Count == 0)
                return false;

            // 3. No-production check: sample regular peers' height twice, 30s apart
            var sample1 = await SamplePeerHeightsAsync();
            await Task.Delay(ProductionSampleDelayMs);
            var sample2 = await SamplePeerHeightsAsync();
            foreach (var kv in sample2)
            {
                if (sample1.TryGetValue(kv.Key, out var h1) && kv.Value > h1)
                {
                    CasterLogUtility.Log(
                        $"BOOTSTRAP: peer {kv.Key} height advanced {h1}→{kv.Value} during stall check — the chain is alive somewhere. Aborting bootstrap.",
                        "BOOTSTRAP");
                    return false;
                }
            }

            // 4. Signed agreement exchange
            var ts = TimeUtil.GetTime();
            var myMsg = $"BOOTSTRAP|{Globals.ValidatorAddress}|{commonHeight}|{myHashAtCommon}|{ts}";
            var mySig = SignatureService.CreateSignature(myMsg, account.GetPrivKey, account.PublicKey);
            if (mySig == "ERROR")
                return false;

            var myNotice = new BootstrapAgreementNotice
            {
                SeedAddress = Globals.ValidatorAddress,
                Height = commonHeight,
                TipHash = myHashAtCommon,
                Timestamp = ts,
                Signature = mySig
            };
            var noticeJson = JsonConvert.SerializeObject(myNotice);

            var signers = new HashSet<string>(StringComparer.Ordinal) { Globals.ValidatorAddress };
            foreach (var seed in agreeingSeeds)
            {
                try
                {
                    using var client = Globals.HttpClientFactory.CreateClient();
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    var uri = $"http://{seed.Ip}:{Globals.ValAPIPort}/valapi/validator/BootstrapAgree";
                    using var content = new StringContent(noticeJson, Encoding.UTF8, "application/json");
                    var resp = await client.PostAsync(uri, content, cts.Token);
                    if (!resp.IsSuccessStatusCode)
                        continue;
                    var body = await resp.Content.ReadAsStringAsync();
                    var theirNotice = JsonConvert.DeserializeObject<BootstrapAgreementNotice>(body);
                    if (theirNotice == null)
                        continue;
                    if (VerifyNotice(theirNotice, commonHeight, myHashAtCommon))
                        signers.Add(theirNotice.SeedAddress);
                }
                catch { }
            }

            if (signers.Count >= 2)
            {
                ConsoleWriterService.OutputValCaster($"[BOOTSTRAP] BOOTSTRAP-AGREEMENT REACHED: {signers.Count}/{seedPeers.Count + 1} seeds @ h={commonHeight}, hash={myHashAtCommon[..Math.Min(16, myHashAtCommon.Length)]}…");
                CasterLogUtility.Log(
                    $"BOOTSTRAP-AGREEMENT REACHED: {signers.Count} seeds [{string.Join(",", signers)}] @ h={commonHeight} hash={myHashAtCommon}",
                    "BOOTSTRAP");
                var agreedPeers = agreeingSeeds.Where(s => signers.Contains(s.Address)).ToList();
                EnterAgreed(signers.ToList(), commonHeight, agreedPeers);

                // Wave 6: the restart event arms the record era — mint + co-sign the genesis
                // membership record at the agreed boundary (no manual height, no config).
                if (CasterMembershipStore.GetCurrent() == null)
                    await TryCreateGenesisAsync();

                return true;
            }

            return false;
        }

        /// <summary>
        /// Verifies an inbound/returned BootstrapAgreementNotice: known seed address, matching
        /// height+hash, timestamp within window, valid signature over the canonical payload.
        /// Used both by the coordination loop (responses) and the BootstrapAgree endpoint (requests).
        /// </summary>
        public static bool VerifyNotice(BootstrapAgreementNotice notice, long expectedHeight, string expectedHash)
        {
            if (notice == null || string.IsNullOrEmpty(notice.SeedAddress) || string.IsNullOrEmpty(notice.Signature))
                return false;
            if (!Globals.BootstrapCasterAddresses.Contains(notice.SeedAddress))
                return false;
            if (notice.SeedAddress == Globals.ValidatorAddress)
                return false;
            if (notice.Height != expectedHeight || notice.TipHash != expectedHash)
                return false;
            if (Math.Abs(TimeUtil.GetTime() - notice.Timestamp) > AgreementTimestampWindowSeconds)
                return false;

            var msg = $"BOOTSTRAP|{notice.SeedAddress}|{notice.Height}|{notice.TipHash}|{notice.Timestamp}";
            return SignatureService.VerifySignature(notice.SeedAddress, msg, notice.Signature);
        }

        /// <summary>
        /// Endpoint-side handler: called by ValidatorController.BootstrapAgree when a peer seed
        /// sends its signed notice. If this node is itself a stalled bootstrap candidate and its
        /// hash at the notice height matches, it counter-signs and returns its own notice.
        /// Returns null when it does not concur.
        /// </summary>
        public static BootstrapAgreementNotice? HandleAgreeRequest(BootstrapAgreementNotice notice)
        {
            if (notice == null)
                return null;
            if (!Globals.IsLocalBootstrapCaster || !Globals.IsChainStalledForBootstrap)
                return null;

            var myHeight = Globals.LastBlock?.Height ?? -1;
            if (notice.Height > myHeight)
                return null;

            var myBlock = notice.Height == myHeight ? Globals.LastBlock : BlockchainData.GetBlockByHeight(notice.Height);
            var myHash = myBlock?.Hash;
            if (string.IsNullOrEmpty(myHash))
                return null;

            if (!VerifyNotice(notice, notice.Height, myHash))
                return null;

            var account = AccountData.GetLocalValidator();
            if (account == null || account.GetPrivKey == null || string.IsNullOrEmpty(Globals.ValidatorAddress))
                return null;

            var ts = TimeUtil.GetTime();
            var msg = $"BOOTSTRAP|{Globals.ValidatorAddress}|{notice.Height}|{myHash}|{ts}";
            var sig = SignatureService.CreateSignature(msg, account.GetPrivKey, account.PublicKey);
            if (sig == "ERROR")
                return null;

            CasterLogUtility.Log(
                $"BOOTSTRAP: counter-signed agreement from {notice.SeedAddress} @ h={notice.Height} hash={myHash}",
                "BOOTSTRAP");

            return new BootstrapAgreementNotice
            {
                SeedAddress = Globals.ValidatorAddress,
                Height = notice.Height,
                TipHash = myHash,
                Timestamp = ts,
                Signature = sig
            };
        }

        private static async Task<Dictionary<string, long>> SamplePeerHeightsAsync()
        {
            var result = new Dictionary<string, long>(StringComparer.Ordinal);
            var peers = Globals.Nodes.Values.Select(n => n.NodeIP)
                .Concat(Globals.ValidatorNodes.Values.Select(n => n.NodeIP))
                .Where(ip => !string.IsNullOrEmpty(ip))
                .Distinct()
                .Take(5)
                .ToList();

            foreach (var ip in peers)
            {
                try
                {
                    using var client = Globals.HttpClientFactory.CreateClient();
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    var uri = $"http://{ip.Replace("::ffff:", "")}:{Globals.ValAPIPort}/valapi/validator/GetBlockHeight";
                    var resp = await client.GetAsync(uri, cts.Token);
                    if (!resp.IsSuccessStatusCode)
                        continue;
                    var body = (await resp.Content.ReadAsStringAsync())?.Trim().Trim('"');
                    if (long.TryParse(body, out var h))
                        result[ip] = h;
                }
                catch { }
            }
            return result;
        }
    }
}
