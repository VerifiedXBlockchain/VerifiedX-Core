using System.Linq;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json;
using ReserveBlockCore.Data;
using ReserveBlockCore.Models;

namespace ReserveBlockCore.Services
{
    public static class ConsensusCertificateHelper
    {
        /// <summary>Collect M-of-N attestations (eager polling) and set <see cref="Block.ConsensusCertificate"/> before P2P broadcast.</summary>
        public static async Task TryAttachCertificateAsync(Block block, string? winnerAddress = null)
        {
            if (block == null || Globals.IsBootstrapMode || !ConsensusCertificateRules.SupportsConsensusCertificate(block.Version))
                return;

            var winner = string.IsNullOrEmpty(winnerAddress) ? block.Validator : winnerAddress;
            if (winner != block.Validator)
                return;

            if (block.ConsensusCertificate != null && block.ConsensusCertificate.Attestations?.Count > 0)
            {
                var need = ConsensusCertificateVerifier.RequiredAttestations(ConsensusCertificateVerifier.OperationalBlockCasterCount());
                if (block.ConsensusCertificate.Attestations.Count >= need)
                    return;
            }

            if (!Globals.IsBlockCaster)
                return;

            var acc = AccountData.GetLocalValidator();
            if (acc == null || acc.GetPrivKey == null)
                return;

            if (!Globals.BlockCasters.Any(x => x.ValidatorAddress == acc.Address))
                return;

            var msg = ConsensusMessageFormatter.FormatAttestationV1(block.Height, block.Hash, block.Validator, block.PrevHash);
            var sig = SignatureService.CreateSignature(msg, acc.GetPrivKey, acc.PublicKey);
            if (sig == "ERROR")
                return;

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

            var needCount = ConsensusCertificateVerifier.RequiredAttestations(ConsensusCertificateVerifier.OperationalBlockCasterCount());
            var merged = new Dictionary<string, CasterAttestation>(StringComparer.Ordinal);

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
            }
        }
    }
}
