using System.Net.Http;
using System.Text;
using Newtonsoft.Json;
using ReserveBlockCore.Data;
using ReserveBlockCore.Models;
using ReserveBlockCore.Utilities;

namespace ReserveBlockCore.Services
{
    public static class CasterBlockFetch
    {
        /// <summary>
        /// Fetches a block from a peer caster. Retries up to 5 times with 2-second delays
        /// to allow the winning caster's consensus loop time to craft and store the block.
        /// The RequestBlock endpoint only returns cached blocks (never crafts independently)
        /// to prevent fork-causing hash divergence.
        /// </summary>
        public static async Task<Block?> TryFetchBlockAsync(Peers caster, long blockHeight, string winnerAddress)
        {
            if (caster == null || string.IsNullOrEmpty(caster.PeerIP))
                return null;

            var ip = caster.PeerIP.Replace("::ffff:", "");
            var baseUri = $"http://{ip}:{Globals.ValAPIPort}/valapi/validator/";

            if (Globals.IsBootstrapMode)
                return await GetBlockLegacyAsync(baseUri, blockHeight).ConfigureAwait(false);

            var acc = AccountData.GetLocalValidator();
            if (acc == null || acc.GetPrivKey == null)
                return null;

            // Retry loop: the winning caster may not have stored the block yet.
            // RequestBlock returns "0" until the block is ready (it no longer crafts independently).
            const int maxRetries = 5;
            const int retryDelayMs = 2000;

            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                if (attempt > 0)
                    await Task.Delay(retryDelayMs).ConfigureAwait(false);

                // Re-sign each attempt with fresh timestamp to pass the 90-second window check
                var ts = TimeUtil.GetTime();
                var msg = ConsensusMessageFormatter.FormatRequestBlockV1(blockHeight, acc.Address, winnerAddress, ts);
                var sig = SignatureService.CreateSignature(msg, acc.GetPrivKey, acc.PublicKey);
                if (sig == "ERROR")
                    return null;

                var req = new RequestBlockRequest
                {
                    BlockHeight = blockHeight,
                    CasterAddress = acc.Address,
                    WinnerAddress = winnerAddress,
                    Timestamp = ts,
                    Signature = sig
                };

                try
                {
                    using var client = Globals.HttpClientFactory.CreateClient();
                    using var content = new StringContent(JsonConvert.SerializeObject(req), Encoding.UTF8, "application/json");
                    using var resp = await client.PostAsync(baseUri + "RequestBlock", content).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                        continue; // retry — peer may not be ready yet

                    var responseBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (string.IsNullOrEmpty(responseBody) || responseBody == "0")
                        continue; // block not ready yet — retry

                    var block = JsonConvert.DeserializeObject<Block>(responseBody);
                    if (block != null)
                        return block;
                }
                catch
                {
                    // Network error — retry
                }
            }

            return null;
        }

        private static async Task<Block?> GetBlockLegacyAsync(string baseUri, long blockHeight)
        {
            try
            {
                using var client = Globals.HttpClientFactory.CreateClient();
                using var resp = await client.GetAsync(baseUri + $"GetBlock/{blockHeight}").ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    return null;
                var responseBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (string.IsNullOrEmpty(responseBody) || responseBody == "0")
                    return null;
                return JsonConvert.DeserializeObject<Block>(responseBody);
            }
            catch
            {
                return null;
            }
        }
    }
}
