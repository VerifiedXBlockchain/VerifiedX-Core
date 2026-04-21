using System.Linq;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json;
using ReserveBlockCore.Data;
using ReserveBlockCore.Models;

namespace ReserveBlockCore.Services
{
    /// <summary>Submits this node's caster attestation to all known block casters (after local block acceptance).</summary>
    public static class ConsensusAttestationPublisher
    {
        public static async Task PublishLocalAsync(Block block)
        {
            try
            {
                if (Globals.IsBootstrapMode || !ConsensusCertificateRules.SupportsConsensusCertificate(block.Version))
                    return;
                if (!Globals.IsBlockCaster)
                    return;

                var acc = AccountData.GetLocalValidator();
                if (acc == null || acc.GetPrivKey == null)
                    return;

                var myAddr = acc.Address;
                if (!Globals.BlockCasters.Any(x => x.ValidatorAddress == myAddr))
                    return;

                var msg = ConsensusMessageFormatter.FormatAttestationV1(block.Height, block.Hash, block.Validator, block.PrevHash);
                var sig = SignatureService.CreateSignature(msg, acc.GetPrivKey, acc.PublicKey);
                if (sig == "ERROR")
                    return;

                var body = new SubmitAttestationRequest
                {
                    BlockHeight = block.Height,
                    BlockHash = block.Hash,
                    WinnerAddress = block.Validator,
                    PrevHash = block.PrevHash,
                    CasterAddress = myAddr,
                    Signature = sig
                };

                var json = JsonConvert.SerializeObject(body);
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
                        using var _ = await client.PostAsync(uri, content).WaitAsync(TimeSpan.FromSeconds(4));
                    }
                    catch { }
                }
            }
            catch { }
        }
    }
}
