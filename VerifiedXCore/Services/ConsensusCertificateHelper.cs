using System.Linq;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Services
{
    public static class ConsensusCertificateHelper
    {
        /// <summary>
        /// A4: shared attestation-quorum check. Collects attestations for the exact
        /// (height, hash, winner, prevHash) tuple from the local store first, then from peer
        /// casters' GetAttestations endpoints. Every signature is verified against the canonical
        /// payload and signers must be current casters. Returns true only when a majority of the
        /// operational caster committee has attested this exact block.
        /// </summary>
        public static async Task<bool> TryGetMajorityAttestationsAsync(long height, string blockHash, string winner, string prevHash, int maxPollRounds = 4)
        {
            // Wave 3/4: quorum basis + eligible signers come from the shared committee helpers.
            var need = ConsensusCertificateVerifier.RequiredAttestationsForHeight(height);
            if (need == int.MaxValue)
                return false;

            var attestorSet = ConsensusCertificateVerifier.AttestorSetForHeight(height);
            var msg = ConsensusMessageFormatter.FormatAttestationV1(height, blockHash, winner, prevHash);
            var merged = new Dictionary<string, CasterAttestation>(StringComparer.Ordinal);

            void MergeVerified(IEnumerable<CasterAttestation>? list)
            {
                if (list == null) return;
                foreach (var a in list)
                {
                    if (string.IsNullOrEmpty(a.CasterAddress) || string.IsNullOrEmpty(a.Signature))
                        continue;
                    if (merged.ContainsKey(a.CasterAddress))
                        continue;
                    if (!attestorSet.Contains(a.CasterAddress))
                        continue;
                    if (!SignatureService.VerifySignature(a.CasterAddress, msg, a.Signature))
                        continue;
                    merged[a.CasterAddress] = a;
                }
            }

            MergeVerified(ConsensusAttestationStore.GetForHeight(height));
            if (merged.Count >= need)
                return true;

            for (var i = 0; i < maxPollRounds && merged.Count < need; i++)
            {
                foreach (var peer in Globals.BlockCasters.ToList())
                {
                    if (string.IsNullOrEmpty(peer.PeerIP))
                        continue;
                    try
                    {
                        using var client = Globals.HttpClientFactory.CreateClient();
                        var ip = peer.PeerIP.Replace("::ffff:", "");
                        var uri = $"http://{ip}:{Globals.ValAPIPort}/valapi/validator/GetAttestations/{height}";
                        using var resp = await client.GetAsync(uri).WaitAsync(TimeSpan.FromSeconds(3));
                        if (!resp.IsSuccessStatusCode)
                            continue;
                        var body = await resp.Content.ReadAsStringAsync();
                        MergeVerified(JsonConvert.DeserializeObject<List<CasterAttestation>>(body));
                    }
                    catch { }
                }

                if (merged.Count >= need)
                    break;
                await Task.Delay(250);
            }

            return merged.Count >= need;
        }

        /// <summary>
        /// Collect M-of-N attestations (eager polling) and set <see cref="Block.ConsensusCertificate"/> before P2P broadcast.
        /// Wave 4: returns true only when the attached certificate reached quorum (callers at
        /// enforced heights must HOLD the broadcast otherwise); runs during bootstrap too
        /// (reduced seed quorum via RequiredAttestationsForHeight).
        /// </summary>
        public static async Task<bool> TryAttachCertificateAsync(Block block, string? winnerAddress = null)
        {
            if (block == null || !ConsensusCertificateRules.SupportsConsensusCertificate(block.Version))
                return false;

            var winner = string.IsNullOrEmpty(winnerAddress) ? block.Validator : winnerAddress;
            if (winner != block.Validator)
                return false;

            if (block.ConsensusCertificate != null && block.ConsensusCertificate.Attestations?.Count > 0)
            {
                var have = ConsensusCertificateVerifier.RequiredAttestationsForHeight(block.Height);
                if (block.ConsensusCertificate.Attestations.Count >= have)
                    return true;
            }

            if (!Globals.IsBlockCaster)
                return false;

            var acc = AccountData.GetLocalValidator();
            if (acc == null || acc.GetPrivKey == null)
                return false;

            if (!Globals.BlockCasters.Any(x => x.ValidatorAddress == acc.Address))
                return false;

            var msg = ConsensusMessageFormatter.FormatAttestationV1(block.Height, block.Hash, block.Validator, block.PrevHash);
            var sig = SignatureService.CreateSignature(msg, acc.GetPrivKey, acc.PublicKey);
            if (sig == "ERROR")
                return false;

            // Wave 4: store our OWN attestation locally too — previously the winner only pushed
            // to peers and polled them back, so its own signature wasn't in its local store.
            ConsensusAttestationStore.TryAdd(block.Height, acc.Address, new CasterAttestation
            {
                CasterAddress = acc.Address,
                Signature = sig,
                Timestamp = TimeUtil.GetTime()
            }, out _);

            var submit = new SubmitAttestationRequest
            {
                BlockHeight = block.Height,
                BlockHash = block.Hash,
                WinnerAddress = block.Validator,
                PrevHash = block.PrevHash,
                CasterAddress = acc.Address,
                Signature = sig
            };
            var json = JsonConvert.SerializeObject(submit);

            foreach (var peer in Globals.BlockCasters.ToList())
            {
                if (string.IsNullOrEmpty(peer.PeerIP))
                    continue;
                try
                {
                    using var client = Globals.HttpClientFactory.CreateClient();
                    var ip = peer.PeerIP.Replace("::ffff:", "");
                    var uri = $"http://{ip}:{Globals.ValAPIPort}/valapi/validator/SubmitAttestation";
                    using var content = new StringContent(json, Encoding.UTF8, "application/json");
                    using var _ = await client.PostAsync(uri, content).WaitAsync(TimeSpan.FromSeconds(3));
                }
                catch { }
            }

            var needCount = ConsensusCertificateVerifier.RequiredAttestationsForHeight(block.Height);
            var merged = new Dictionary<string, CasterAttestation>(StringComparer.Ordinal)
            {
                [acc.Address] = new CasterAttestation { CasterAddress = acc.Address, Signature = sig, Timestamp = TimeUtil.GetTime() }
            };

            for (var i = 0; i < 15 && merged.Count < needCount; i++)
            {
                foreach (var peer in Globals.BlockCasters.ToList())
                {
                    if (string.IsNullOrEmpty(peer.PeerIP))
                        continue;
                    try
                    {
                        using var client = Globals.HttpClientFactory.CreateClient();
                        var ip = peer.PeerIP.Replace("::ffff:", "");
                        var uri = $"http://{ip}:{Globals.ValAPIPort}/valapi/validator/GetAttestations/{block.Height}";
                        using var resp = await client.GetAsync(uri).WaitAsync(TimeSpan.FromSeconds(3));
                        if (!resp.IsSuccessStatusCode)
                            continue;
                        var body = await resp.Content.ReadAsStringAsync();
                        var list = JsonConvert.DeserializeObject<List<CasterAttestation>>(body);
                        if (list == null)
                            continue;
                        foreach (var a in list)
                        {
                            if (string.IsNullOrEmpty(a.CasterAddress))
                                continue;
                            var m = ConsensusMessageFormatter.FormatAttestationV1(block.Height, block.Hash, block.Validator, block.PrevHash);
                            if (!SignatureService.VerifySignature(a.CasterAddress, m, a.Signature))
                                continue;
                            merged[a.CasterAddress] = a;
                        }
                    }
                    catch { }
                }

                if (merged.Count >= needCount)
                    break;
                await Task.Delay(250);
            }

            if (merged.Count >= needCount)
            {
                block.ConsensusCertificate = new ConsensusCertificate
                {
                    BlockHeight = block.Height,
                    BlockHash = block.Hash,
                    WinnerAddress = block.Validator,
                    PrevHash = block.PrevHash,
                    Attestations = merged.Values.ToList()
                };
                return true;
            }
            return false;
        }

        /// <summary>
        /// Wave 4 receiver-side top-up: when a block arrives at an enforced height with a missing
        /// or sub-quorum certificate, try to complete it from the local attestation store + peer
        /// GetAttestations polls BEFORE validation. Best-effort — the verifier still enforces the
        /// quorum; this just converts transient attach failures into bounded retries, not stalls.
        /// </summary>
        public static async Task TryCompleteCertificateAsync(Block block)
        {
            try
            {
                if (block == null || block.Height < Globals.CertEnforceHeight
                    || !ConsensusCertificateRules.SupportsConsensusCertificate(block.Version))
                    return;

                var need = ConsensusCertificateVerifier.RequiredAttestationsForHeight(block.Height);
                if (block.ConsensusCertificate?.Attestations?.Count >= need)
                    return;

                var attestorSet = ConsensusCertificateVerifier.AttestorSetForHeight(block.Height);
                var msg = ConsensusMessageFormatter.FormatAttestationV1(block.Height, block.Hash, block.Validator, block.PrevHash);
                var merged = new Dictionary<string, CasterAttestation>(StringComparer.Ordinal);

                foreach (var a in block.ConsensusCertificate?.Attestations ?? new List<CasterAttestation>())
                {
                    if (string.IsNullOrEmpty(a.CasterAddress) || merged.ContainsKey(a.CasterAddress)) continue;
                    if (!attestorSet.Contains(a.CasterAddress)) continue;
                    if (!SignatureService.VerifySignature(a.CasterAddress, msg, a.Signature)) continue;
                    merged[a.CasterAddress] = a;
                }

                foreach (var a in ConsensusAttestationStore.GetForHeight(block.Height))
                {
                    if (merged.Count >= need) break;
                    if (string.IsNullOrEmpty(a.CasterAddress) || merged.ContainsKey(a.CasterAddress)) continue;
                    if (!attestorSet.Contains(a.CasterAddress)) continue;
                    if (!SignatureService.VerifySignature(a.CasterAddress, msg, a.Signature)) continue;
                    merged[a.CasterAddress] = a;
                }

                if (merged.Count < need)
                {
                    foreach (var peer in Globals.BlockCasters.ToList())
                    {
                        if (merged.Count >= need) break;
                        if (string.IsNullOrEmpty(peer.PeerIP)) continue;
                        try
                        {
                            using var client = Globals.HttpClientFactory.CreateClient();
                            var ip = peer.PeerIP.Replace("::ffff:", "");
                            var uri = $"http://{ip}:{Globals.ValAPIPort}/valapi/validator/GetAttestations/{block.Height}";
                            using var resp = await client.GetAsync(uri).WaitAsync(TimeSpan.FromSeconds(3));
                            if (!resp.IsSuccessStatusCode) continue;
                            var body = await resp.Content.ReadAsStringAsync();
                            var list = JsonConvert.DeserializeObject<List<CasterAttestation>>(body);
                            if (list == null) continue;
                            foreach (var a in list)
                            {
                                if (merged.Count >= need) break;
                                if (string.IsNullOrEmpty(a.CasterAddress) || merged.ContainsKey(a.CasterAddress)) continue;
                                if (!attestorSet.Contains(a.CasterAddress)) continue;
                                if (!SignatureService.VerifySignature(a.CasterAddress, msg, a.Signature)) continue;
                                merged[a.CasterAddress] = a;
                            }
                        }
                        catch { }
                    }
                }

                if (merged.Count >= need)
                {
                    block.ConsensusCertificate = new ConsensusCertificate
                    {
                        BlockHeight = block.Height,
                        BlockHash = block.Hash,
                        WinnerAddress = block.Validator,
                        PrevHash = block.PrevHash,
                        Attestations = merged.Values.ToList()
                    };
                }
            }
            catch { /* verifier remains the enforcement point */ }
        }
    }
}
